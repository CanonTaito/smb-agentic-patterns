using System.Text.Json;
using System.Text.Json.Nodes;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tools;
using Agentic.AgentCore.Tracing;

namespace Agentic.Patterns.WeeklyReport;

public enum PipelineOutcome { Completed, Rejected, PlanInvalid }

public sealed class PipelineResult
{
    public required PipelineOutcome Outcome { get; init; }
    public ReportPlan? Plan { get; init; }
    public GateResult? Gate { get; init; }
    public IReadOnlyList<ExecutionRecord> Executions { get; init; } = [];
    public string Report { get; init; } = "";
    public Verification? Verification { get; init; }
    public Verification? VerificationAfterRepair { get; init; }
    public bool ReportRepaired { get; init; }
    public TokenUsage Usage { get; init; } = TokenUsage.None;
    public int LlmCalls { get; init; }
    public long Ms { get; init; }
    public string? Failure { get; init; }
}

/// <summary>
/// Pattern 2 pipeline: plan → human gate → execute steps → assemble report →
/// self-verify (→ repair → re-verify). Every phase is a traced span; the plan
/// is a human checkpoint before any data is fetched, and the verification
/// pass is the agent checking its own work against the plan's criteria.
/// </summary>
public static class ReportPipeline
{
    public static async Task<PipelineResult> RunAsync(
        IOllamaClient innerClient,
        ToolRegistry tools,
        string reportRequest,
        bool autoApprove,
        Tracer tracer,
        TextWriter? log = null,
        CancellationToken ct = default)
    {
        log ??= TextWriter.Null;
        var client = new RecordingOllamaClient(innerClient, tracer);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var root = tracer.StartSpan($"weekly report: {Truncate(reportRequest, 50)}", SpanKind.Agent);

        // --- phase 1: plan ------------------------------------------------
        ReportPlan plan;
        using (var planSpan = tracer.StartSpan("phase: plan", SpanKind.Internal))
        {
            try
            {
                plan = await Planner.CreateAsync(client, tools, reportRequest, DataSeeder.SeedDate, ct);
            }
            catch (StructuredOutputException ex)
            {
                planSpan.SetTag("ok", false);
                root.SetTag("outcome", nameof(PipelineOutcome.PlanInvalid));
                sw.Stop();
                return new PipelineResult
                {
                    Outcome = PipelineOutcome.PlanInvalid,
                    Failure = ex.Message,
                    Usage = client.TotalUsage,
                    LlmCalls = client.Calls.Count,
                    Ms = sw.ElapsedMilliseconds,
                };
            }
            planSpan.SetTag("steps", plan.Steps.Count);
            planSpan.SetTag("ok", true);
            log.WriteLine($"PLAN ({plan.Steps.Count} steps):");
            log.WriteLine(Planner.ToDisplay(plan));
        }

        // --- phase 2: human gate ------------------------------------------
        GateResult gate;
        using (var gateSpan = tracer.StartSpan("phase: gate", SpanKind.Gate))
        {
            gate = await HumanGate.RequestApprovalAsync(
                "fetch data and produce the weekly report with this plan",
                Planner.ToDisplay(plan),
                autoApprove, output: log, ct: ct);
            gateSpan.SetTag("decision", gate.Decision.ToString());
            gateSpan.SetTag("note", gate.Note ?? "");
        }

        if (!gate.ShouldProceed)
        {
            root.SetTag("outcome", nameof(PipelineOutcome.Rejected));
            sw.Stop();
            return new PipelineResult
            {
                Outcome = PipelineOutcome.Rejected,
                Plan = plan,
                Gate = gate,
                Usage = client.TotalUsage,
                LlmCalls = client.Calls.Count,
                Ms = sw.ElapsedMilliseconds,
            };
        }

        // If the operator edited the plan, the edited content must still be a valid plan.
        if (gate.Decision == GateDecision.Edited)
        {
            var edited = Planner.TryParse(gate.Content ?? "");
            if (edited is null || edited.Steps.Count == 0)
            {
                root.SetTag("outcome", nameof(PipelineOutcome.Rejected));
                sw.Stop();
                return new PipelineResult
                {
                    Outcome = PipelineOutcome.Rejected,
                    Plan = plan,
                    Gate = gate,
                    Failure = "edited plan was not a valid plan JSON; aborting",
                    Usage = client.TotalUsage,
                    LlmCalls = client.Calls.Count,
                    Ms = sw.ElapsedMilliseconds,
                };
            }
            plan = edited;
            log.WriteLine("[gate] edited plan accepted");
        }

        // --- phase 3: execute steps ---------------------------------------
        var executions = new List<ExecutionRecord>();
        using (var execSpan = tracer.StartSpan($"phase: execute {plan.Steps.Count} steps", SpanKind.Internal))
        {
            foreach (var step in plan.Steps)
            {
                ct.ThrowIfCancellationRequested();
                var record = await ExecuteStepAsync(client, tools, step, tracer, log, ct);
                executions.Add(record);
            }
            execSpan.SetTag("steps", executions.Count);
            execSpan.SetTag("failed", executions.Count(e => !e.Ok));
            execSpan.SetTag("repaired", executions.Count(e => e.Repaired));
        }

        // --- phase 4: assemble --------------------------------------------
        var report = await AssembleAsync(client, plan, executions, tracer, log, ct);

        // --- phase 5: self-verify, repair once, re-verify ------------------
        var verification = await VerifyAsync(client, plan, report, tracer, ct);
        Verification? verification2 = null;
        var repaired = false;
        if (!verification.Ok)
        {
            repaired = true;
            log.WriteLine($"[verify] issues found: {string.Join("; ", verification.Issues.Concat(verification.MissingSections))}");
            var repairedReport = await RepairAsync(client, plan, report, verification, tracer, log, ct);
            // Small models sometimes "repair" by dropping whole sections; a
            // repair that loses planned sections is worse than the original.
            var plannedSections = plan.Steps.Select(s => s.Section).Distinct().ToList();
            var keptSections = plannedSections.Count(s => SectionAppearsIn(s, repairedReport));
            if (keptSections < plannedSections.Count(s => SectionAppearsIn(s, report)))
            {
                log.WriteLine("[repair] repaired report lost planned sections; keeping the original");
                repaired = false;
            }
            else
            {
                report = repairedReport;
                verification2 = await VerifyAsync(client, plan, report, tracer, ct);
            }
        }

        root.SetTag("outcome", nameof(PipelineOutcome.Completed));
        root.SetTag("planSteps", plan.Steps.Count);
        root.SetTag("failedSteps", executions.Count(e => !e.Ok));
        root.SetTag("llmCalls", client.Calls.Count);
        root.SetTag("tokens", client.TotalUsage.TotalTokens);
        root.SetTag("verifiedOk", (verification2 ?? verification).Ok);
        sw.Stop();

        return new PipelineResult
        {
            Outcome = PipelineOutcome.Completed,
            Plan = plan,
            Gate = gate,
            Executions = executions,
            Report = report,
            Verification = verification,
            VerificationAfterRepair = verification2,
            ReportRepaired = repaired,
            Usage = client.TotalUsage,
            LlmCalls = client.Calls.Count,
            Ms = sw.ElapsedMilliseconds,
        };
    }

    private static async Task<ExecutionRecord> ExecuteStepAsync(
        RecordingOllamaClient client, ToolRegistry tools, PlanStep step, Tracer tracer, TextWriter log, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var span = tracer.StartSpan($"step {step.Step}: {step.Tool}", SpanKind.Tool);
        span.SetTag("section", step.Section);

        var args = step.Args is { Count: > 0 } ? step.Args : new JsonObject();
        var result = await tools.ExecuteAsync(step.Tool, args);
        var repaired = false;

        // One repair round: hand the structured error back, get fixed arguments, retry.
        if (!result.Ok)
        {
            span.SetTag("repairAttempt", true);
            log.WriteLine($"  step {step.Step} [{step.Tool}] failed: {Truncate(result.Error ?? "", 120)} → asking model to fix arguments");
            var tool = tools.Tools.First(t => string.Equals(t.Name, step.Tool, StringComparison.OrdinalIgnoreCase));
            var fixedArgs = await RepairArgsAsync(client, tool, args, result.Error ?? "unknown error", ct);
            if (fixedArgs is not null)
            {
                result = await tools.ExecuteAsync(step.Tool, fixedArgs);
                repaired = true;
                args = fixedArgs;
            }
        }

        sw.Stop();
        span.SetTag("ok", result.Ok);
        span.SetTag("repaired", repaired);
        span.SetTag("durationMs", sw.ElapsedMilliseconds);
        log.WriteLine($"  step {step.Step} [{step.Tool}] {(result.Ok ? "done" : "FAILED")} ({sw.ElapsedMilliseconds} ms){(repaired ? " after args repair" : "")}");

        return new ExecutionRecord
        {
            Step = step,
            Ok = result.Ok,
            DataJson = result.Ok ? result.DataJson : result.Error ?? "",
            Error = result.Ok ? null : result.Error,
            Repaired = repaired,
            DurationMs = sw.ElapsedMilliseconds,
        };
    }

    private static async Task<JsonObject?> RepairArgsAsync(
        IOllamaClient client, ToolDefinition tool, JsonObject args, string error, CancellationToken ct)
    {
        try
        {
            return await StructuredOutput.GetAsync<JsonObject>(
                client,
                systemPrompt: "You fix tool-call arguments for a report generator. Reply with ONLY a corrected JSON object of arguments for the named tool — no prose, no markdown.",
                userPrompt: $"Tool: {tool.Name}\nSchema: {tool.InputSchema.ToJsonString()}\n" +
                            $"Rejected arguments: {args.ToJsonString()}\nError: {error}\n" +
                            "Return only the corrected arguments JSON object.",
                maxAttempts: 2,
                validate: obj => ValidateRequired(obj, tool.InputSchema),
                ct: ct);
        }
        catch (StructuredOutputException)
        {
            return null;
        }
    }

    private static string? ValidateRequired(JsonObject args, JsonObject schema)
    {
        if (schema["required"] is not JsonArray required) return null;
        foreach (var req in required)
        {
            var name = req?.GetValue<string>();
            if (name is null) continue;
            if (!args.ContainsKey(name) || args[name] is null)
                return $"missing required property '{name}'";
        }
        return null;
    }

    private static async Task<string> AssembleAsync(
        RecordingOllamaClient client, ReportPlan plan, IReadOnlyList<ExecutionRecord> executions,
        Tracer tracer, TextWriter log, CancellationToken ct)
    {
        using var span = tracer.StartSpan("phase: assemble", SpanKind.Internal);

        var outputs = executions.Select(e => new
        {
            step = e.Step.Step,
            section = e.Step.Section,
            description = e.Step.Description,
            tool = e.Step.Tool,
            ok = e.Ok,
            data = e.Ok ? Truncate(e.DataJson, 3000) : $"ERROR: {e.Error}",
        });

        var user = $"""
            Write the report described below as markdown.

            Goal: {plan.Goal}
            Verification criteria: {plan.VerificationCriteria}

            Planned sections (each must appear in the report):
            {string.Join(Environment.NewLine, plan.Steps.DistinctBy(s => s.Section).Select(s => "- " + s.Section))}

            Data gathered from the tools (JSON, in plan order):
            {JsonSerializer.Serialize(outputs)}

            Rules: every planned section present; cite concrete numbers from the data; if a step failed, say
            that section is unavailable and why; plain markdown with ## headings; no preamble outside the report.
            """;

        var result = await client.ChatAsync(
            [ChatMessage.System("You are the report writer for Inkline Print Co. You turn tool outputs into a tight operations report in markdown."), ChatMessage.User(user)],
            tools: null, jsonFormat: null, temperature: 0.2, ct: ct);

        var report = TrimCodeFences(result.Message.Content?.Trim() ?? "");
        span.SetTag("chars", report.Length);
        log.WriteLine($"[assemble] {report.Length} chars");
        return report;
    }

    private static async Task<Verification> VerifyAsync(
        RecordingOllamaClient client, ReportPlan plan, string report, Tracer tracer, CancellationToken ct)
    {
        using var span = tracer.StartSpan("phase: verify", SpanKind.Internal);

        // The LLM only judges what needs judgement — concrete factual issues.
        // Section presence is checked in code: small models match section names
        // too literally (ink_levels vs "Ink Levels") and often botch the ok
        // flag, so the structural half of the self-check is deterministic.
        var verification = await StructuredOutput.GetAsync<Verification>(
            client,
            systemPrompt: """
                You check a generated report's FACTUAL content. Look ONLY for concrete problems:
                - a number that contradicts the stated criteria or is internally inconsistent
                - a section heading with no content under it
                Do NOT comment on wording, ordering, style, or the general presence of headings.
                Return ONLY JSON: {"issues":["..."]} — an array of SHORT STRINGS, empty if the
                facts are fine.
                """,
            userPrompt: $"""
                Verification criteria: {plan.VerificationCriteria}

                Report:
                {report}
                """,
            ct: ct);

        var sections = plan.Steps.DistinctBy(s => s.Section).Select(s => s.Section).ToArray();
        var missing = sections.Where(s => !SectionAppearsIn(s, report)).ToList();
        var issues = verification.Issues.Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();
        var hybrid = new Verification
        {
            Ok = missing.Count == 0 && issues.Length == 0,
            MissingSections = [.. missing],
            Issues = issues,
        };

        span.SetTag("ok", hybrid.Ok);
        span.SetTag("issues", hybrid.Issues.Length + hybrid.MissingSections.Length);
        return hybrid;
    }

    /// <summary>
    /// Deterministic section-presence check: a planned section counts as present
    /// when a markdown heading shares most of its words (order-insensitive, so
    /// "jobs at risk" matches "## At-Risk Jobs").
    /// </summary>
    private static bool SectionAppearsIn(string section, string report)
    {
        var sectionWords = Words(section);
        if (sectionWords.Count == 0) return false;
        var needed = (int)Math.Ceiling(sectionWords.Count * 0.6);

        foreach (var line in report.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('#')) continue;
            var headingWords = Words(trimmed.TrimStart('#', ' '));
            if (sectionWords.Count(w => headingWords.Contains(w)) >= needed) return true;
        }
        return false;
    }

    private static HashSet<string> Words(string s) =>
        s.ToLowerInvariant()
            .Split([' ', '_', '-', ':', ',', '.', '(', ')', '/', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .ToHashSet();

    private static async Task<string> RepairAsync(
        RecordingOllamaClient client, ReportPlan plan, string report, Verification verification,
        Tracer tracer, TextWriter log, CancellationToken ct)
    {
        using var span = tracer.StartSpan("phase: repair report", SpanKind.Internal);

        var issues = verification.Issues.Concat(verification.MissingSections).ToArray();
        var result = await client.ChatAsync(
            [
                ChatMessage.System("""
                    You fix a report so it passes review. Return ONLY the corrected full report in markdown.
                    Rules: never mention the review or its issues inside the report; never quote the review
                    notes; never wrap the report in code fences; keep every section heading that was
                    already in the report unless the review says a heading is wrong.
                    """),
                ChatMessage.User($"""
                    Goal: {plan.Goal}

                    Current report:
                    {report}

                    Review notes (fix these IN the report — never quote them in it):
                    {string.Join(Environment.NewLine, issues.Select(i => "- " + i))}

                    Return the corrected report.
                    """),
            ],
            tools: null, jsonFormat: null, temperature: 0.2, ct: ct);

        var repaired = TrimCodeFences(result.Message.Content?.Trim() ?? "");
        span.SetTag("chars", repaired.Length);
        log.WriteLine($"[repair] {issues.Length} issue(s), new report {repaired.Length} chars");
        return string.IsNullOrWhiteSpace(repaired) ? report : repaired;
    }

    // Small models love wrapping markdown in ```markdown fences; strip them.
    private static string TrimCodeFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        if (firstNl < 0) return s;
        s = s[(firstNl + 1)..];
        var end = s.LastIndexOf("```");
        return (end >= 0 ? s[..end] : s).Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
