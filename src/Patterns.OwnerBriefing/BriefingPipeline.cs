using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tracing;

namespace Agentic.Patterns.OwnerBriefing;

public sealed record BriefingResult(
    string Focus,
    BriefingPlan Plan,
    WorkerResult Analyst,
    WorkerResult Researcher,
    string Briefing,
    GateResult Gate,
    int LlmCalls,
    TokenUsage Usage,
    long Ms);

/// <summary>
/// Supervisor-worker fan-out (guide pattern 5). The supervisor decomposes the
/// owner's focus into briefs, the analyst and researcher run concurrently via
/// Task.WhenAll on disjoint tool belts, and the writer (no tools) synthesises
/// their findings into one email. Every phase is a nested span, so the trace
/// shows which worker actually cost the time and tokens.
/// </summary>
public static class BriefingPipeline
{
    public static readonly string[] AnalystTools = ["sales_kpis", "at_risk_jobs", "get_today"];
    public static readonly string[] ResearcherTools = ["list_reviews", "get_today"];
    public static readonly string[] SingleAgentTools = ["sales_kpis", "at_risk_jobs", "list_reviews", "get_today"];

    private static readonly Lazy<InklineDb> LazyDb = new(() => new InklineDb());

    public static async Task<BriefingResult> RunAsync(
        IOllamaClient client,
        string focus,
        Tracer tracer,
        bool autoApprove = false,
        TextWriter? log = null,
        TextWriter? gateOut = null,
        CancellationToken ct = default)
    {
        log ??= Console.Out;
        var sw = Stopwatch.StartNew();
        var db = LazyDb.Value;

        using var runSpan = tracer.StartSpan("briefing run (multi-agent)", SpanKind.Agent);
        runSpan.SetTag("focus", Truncate(focus, 80));

        var recorded = new RecordingOllamaClient(client, tracer);

        // 1. Supervisor: decompose the owner's focus into worker briefs.
        var plan = await tracer.InSpanAsync("supervisor: decompose", async span =>
        {
            var p = await Supervisor.DecomposeAsync(recorded, focus, ct);
            span.SetTag("analystTask", Truncate(p.AnalystTask, 90));
            span.SetTag("researcherTask", Truncate(p.ResearcherTask, 90));
            return p;
        });

        log.WriteLine("SUPERVISOR PLAN");
        log.WriteLine("  analyst   : " + plan.AnalystTask);
        log.WriteLine("  researcher: " + plan.ResearcherTask);
        log.WriteLine("  writer    : " + plan.WriterTask);
        log.WriteLine();

        // 2. Analyst and researcher run concurrently. On a single local Ollama
        // instance requests queue anyway, so this buys isolation and bounded
        // context rather than wall-clock speed - the eval measures both.
        var analystTask = Workers.RunAsync(
            "analyst", client, BriefingTools.Build(db, AnalystTools), tracer,
            Workers.AnalystOptions(plan.AnalystTask), verify: VerifyAnalyst, ct: ct);
        var researcherTask = Workers.RunAsync(
            "researcher", client, BriefingTools.Build(db, ResearcherTools), tracer,
            Workers.ResearcherOptions(plan.ResearcherTask), verify: VerifyResearcher, ct: ct);

        await Task.WhenAll(analystTask, researcherTask);
        var analyst = analystTask.Result;
        var researcher = researcherTask.Result;

        // 3. Writer: synthesis only, deliberately no tools. The supervisor
        // checks the draft for currency/percentage figures the workers never
        // reported and retries once with the violations listed - a 3B writer
        // will otherwise fill a gap in the findings with a confident guess.
        // Supervisors verify hand-offs, not just delegate them. If the analyst
        // missed a core KPI (or never got the data), the orchestrator fetches
        // it itself and hands the writer authoritative facts.
        var repairedFacts = await RepairMissingFactsAsync(db, analyst.Output, tracer);

        var writerPrompt = Workers.WriterPrompt(plan.WriterTask);
        var findings = repairedFacts +
                       $"ANALYST FINDINGS:\n{analyst.Output}\n\nRESEARCHER FINDINGS:\n{researcher.Output}";
        var briefing = "";
        var ungrounded = new List<string>();

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var userMessage = $"Owner focus: {focus}\n\n{findings}\n\n";
            if (attempt > 1)
                userMessage +=
                    "Your previous draft used figures that do not appear in the findings: " +
                    string.Join(", ", ungrounded) +
                    ". Rewrite the email using only figures that appear in the findings. " +
                    "If a required figure is missing, write \"not available\" instead of inventing it.\n\n";
            userMessage += "Write the owner briefing email now.";

            briefing = await tracer.InSpanAsync(
                attempt == 1 ? "worker: writer" : "worker: writer (grounding retry)",
                async span =>
                {
                    var result = await recorded.ChatAsync(
                        [ChatMessage.System(writerPrompt), ChatMessage.User(userMessage)],
                        temperature: 0.3, ct: ct);

                    var text = result.Message.Content?.Trim() ?? "";
                    span.SetTag("chars", text.Length);
                    span.SetTag("ungroundedFigures", attempt == 1 ? 0 : ungrounded.Count);
                    span.SetTag("promptTokens", result.Usage.PromptTokens);
                    span.SetTag("completionTokens", result.Usage.CompletionTokens);
                    return text;
                });

            ungrounded = UngroundedFigures(briefing, findings);
            if (ungrounded.Count == 0) break;
        }

        if (ungrounded.Count > 0)
            runSpan.SetTag("ungroundedFigures", string.Join(",", ungrounded));

        // 4. Nothing reaches the owner's inbox without an explicit yes.
        var gate = await tracer.InSpanAsync("gate: send briefing", async span =>
        {
            var g = await HumanGate.RequestApprovalAsync(
                "Send the Sunday-night owner briefing to the owner's inbox",
                briefing, autoApprove, gateOut, ct: ct);
            span.SetTag("decision", g.Decision.ToString());
            return g;
        }, SpanKind.Gate);

        sw.Stop();
        var usage = analyst.Usage + researcher.Usage + recorded.TotalUsage;
        var llmCalls = analyst.Steps + researcher.Steps + recorded.Calls.Count;

        runSpan.SetTag("tokens", usage.TotalTokens);
        runSpan.SetTag("llmCalls", llmCalls);
        runSpan.SetTag("gate", gate.Decision.ToString());

        log.WriteLine(
            $"multi-agent: {llmCalls} llm calls · {usage.TotalTokens:N0} tokens · {sw.ElapsedMilliseconds} ms");

        return new BriefingResult(
            focus, plan, analyst, researcher,
            gate.Content ?? briefing, gate, llmCalls, usage, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Control arm for the eval: the same tools, data and output shape in one
    /// agent loop, so the comparison isolates the decomposition, not the task.
    /// </summary>
    public static async Task<WorkerResult> RunSingleAsync(
        IOllamaClient client,
        string focus,
        Tracer tracer,
        CancellationToken ct = default)
    {
        using var span = tracer.StartSpan("briefing run (single-agent)", SpanKind.Agent);
        span.SetTag("focus", Truncate(focus, 80));

        var result = await Workers.RunAsync(
            "single agent", client, BriefingTools.Build(LazyDb.Value, SingleAgentTools), tracer,
            Workers.SingleAgentOptions(focus), verify: VerifySingleAgent, ct: ct);

        span.SetTag("tokens", result.Usage.TotalTokens);
        return result;
    }

    // Worker QA: the supervisor checks that the data-gathering actually happened
    // before it trusts the findings. A 3B model that "knows" the answer will
    // happily skip a tool call; these guards make that observable and retryable.
    internal static string? VerifyAnalyst(IReadOnlyList<ToolCallRecord> calls)
    {
        var missing = new List<string>();
        if (!CalledFourWeekKpis(calls)) missing.Add("sales_kpis with window_weeks=4");
        if (calls.All(c => c.ToolName != "at_risk_jobs")) missing.Add("at_risk_jobs");
        return missing.Count == 0
            ? null
            : "you must call " + string.Join(" and ", missing) + " before writing your findings";
    }

    internal static string? VerifyResearcher(IReadOnlyList<ToolCallRecord> calls) =>
        calls.Any(c => c.ToolName == "list_reviews")
            ? null
            : "you must call list_reviews before writing your findings";

    internal static string? VerifySingleAgent(IReadOnlyList<ToolCallRecord> calls)
    {
        var missing = new List<string>();
        if (!CalledFourWeekKpis(calls)) missing.Add("sales_kpis with window_weeks=4");
        if (calls.All(c => c.ToolName != "at_risk_jobs")) missing.Add("at_risk_jobs");
        if (calls.All(c => c.ToolName != "list_reviews")) missing.Add("list_reviews");
        return missing.Count == 0
            ? null
            : "you must call " + string.Join(", ", missing) + " before writing the email";
    }

    private static bool CalledFourWeekKpis(IReadOnlyList<ToolCallRecord> calls)
    {
        foreach (var call in calls.Where(c => c.ToolName == "sales_kpis"))
        {
            if (string.IsNullOrWhiteSpace(call.ArgumentsJson)) return true;
            try
            {
                using var doc = JsonDocument.Parse(call.ArgumentsJson);
                if (!doc.RootElement.TryGetProperty("window_weeks", out var weeks)) return true;
                if (weeks.ValueKind == JsonValueKind.Number && weeks.GetDouble() >= 4) return true;
            }
            catch (JsonException)
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<string> RepairMissingFactsAsync(InklineDb db, string analystText, Tracer tracer)
    {
        var kpis = InklineData.ComputeKpis(InklineData.LoadSales());
        var atRisk = db.ListAtRiskJobs(DataSeeder.SeedDate);
        var text = analystText.ToLowerInvariant().Replace(",", "");

        var missing = new List<string>();
        if (!text.Contains(kpis.LastWeek.RevenueAud.ToString("0.##", CultureInfo.InvariantCulture)))
            missing.Add("last-week revenue");
        if (!text.Contains(Math.Abs(kpis.WowRevenuePct).ToString("0.0", CultureInfo.InvariantCulture)))
            missing.Add("week-on-week change");
        if (!text.Contains(kpis.WindowAvgRevenue.ToString("0.##", CultureInfo.InvariantCulture)))
            missing.Add("4-week averages");
        if (!atRisk.Any(j => Regex.IsMatch(text, $@"\b{j.JobNumber}\b")))
            missing.Add("at-risk jobs");
        if (missing.Count == 0) return "";

        using var span = tracer.StartSpan("supervisor: repair missing facts", SpanKind.Internal);
        span.SetTag("missing", string.Join(",", missing));

        var tools = BriefingTools.Build(db, ["sales_kpis", "at_risk_jobs"]);
        var kpiResult = await tools.ExecuteAsync("sales_kpis", new JsonObject { ["window_weeks"] = 4 });
        var riskResult = await tools.ExecuteAsync("at_risk_jobs", new JsonObject());

        return "SUPERVISOR-VERIFIED FACTS (authoritative - if the analyst disagrees with these, use these):\n" +
               kpiResult.DataJson + "\n" + riskResult.DataJson + "\n\n";
    }

    private static readonly Regex MoneyOrPercent = new(
        @"(\$\s?[\d,]+(?:\.\d+)?)|([\d,]+(?:\.\d+)?\s?%)", RegexOptions.Compiled);

    private static readonly Regex AnyNumber = new(@"\d[\d,]*(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>Currency/percentage figures in the draft that no worker reported.</summary>
    private static List<string> UngroundedFigures(string briefing, string findings)
    {
        var allowed = AnyNumber.Matches(findings)
            .Select(m => m.Value.Replace(",", ""))
            .ToHashSet();

        var result = new List<string>();
        foreach (Match match in MoneyOrPercent.Matches(briefing))
        {
            var core = AnyNumber.Match(match.Value).Value.Replace(",", "");
            if (core.Length > 0 && !allowed.Contains(core) && !result.Contains(core))
                result.Add(core);
        }
        return result;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
