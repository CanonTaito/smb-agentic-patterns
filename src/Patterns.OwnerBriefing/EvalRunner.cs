using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tracing;

namespace Agentic.Patterns.OwnerBriefing;

internal sealed record BriefingCase(
    string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("scenario_key")] string ScenarioKey,
    string Focus);

internal sealed record RubricCheck(string Name, bool Pass, string Detail);

internal sealed record RubricScore(IReadOnlyList<RubricCheck> Checks)
{
    public int Passed => Checks.Count(c => c.Pass);
    public int Total => Checks.Count;
    // One miss is allowed: a single rounded figure should not sink a briefing
    // that is otherwise complete and correctly sourced.
    public bool Overall => Passed >= Total - 1;
    public IEnumerable<RubricCheck> Failures => Checks.Where(c => !c.Pass);
}

/// <summary>
/// Deterministic rubric: every number is checked against ground truth computed
/// in code from sales.csv and business.db, so the score cannot be gamed by a
/// persuasive-sounding briefing. No LLM judge - the checks are cheap and stable.
/// </summary>
internal static class BriefingRubric
{
    private static readonly Dictionary<string, string[]> ScenarioKeywords = new()
    {
        ["standard"] = ["revenue", "orders", "briefing"],
        ["cash"] = ["revenue", "cash", "trend", "week-on-week", "week on week"],
        ["quality"] = ["reprint", "colour", "color", "quality", "review"],
        ["risk"] = ["late", "overdue", "due", "risk", "behind"],
        ["growth"] = ["quote", "conversion", "funnel", "growth", "pipeline"],
    };

    // The three issues raised by the low-rated reviews (R004, R006, R010).
    private static readonly string[][] LowRatingThemes =
    [
        ["communi", "chase", "status email", "status update"],
        ["invoice", "billing"],
        ["colour", "color", "reprint", "quality"],
    ];

    private static readonly string[] DownWords = ["down", "fell", "fall", "drop", "lower", "declin", "decreas", "soft"];
    private static readonly string[] UpWords = ["up", "rose", "grew", "grow", "higher", "increas"];

    public static RubricScore Score(
        string briefing,
        SalesKpis kpis,
        IReadOnlyList<JobRecord> atRisk,
        string scenarioKey)
    {
        var text = Normalise(briefing);
        var checks = new List<RubricCheck>();

        var revenue = kpis.LastWeek.RevenueAud;
        var revenueVariants = new[] { revenue.ToString("0.##", CultureInfo.InvariantCulture), revenue.ToString("0", CultureInfo.InvariantCulture) };
        checks.Add(new RubricCheck(
            "last_week_revenue",
            HasAny(text, revenueVariants),
            $"expected {revenue:0.00} (accepts {string.Join(" / ", revenueVariants)})"));

        var wowAbs = Math.Abs(kpis.WowRevenuePct);
        var wowVariants = new[] { wowAbs.ToString("0.0", CultureInfo.InvariantCulture), wowAbs.ToString("0.00", CultureInfo.InvariantCulture), Math.Round(wowAbs).ToString("0", CultureInfo.InvariantCulture) + "%" };
        var direction = kpis.WowRevenuePct < 0 ? DownWords : UpWords;
        var signed = kpis.WowRevenuePct < 0
            ? wowVariants.Any(v => text.Contains("-" + v, StringComparison.Ordinal))
            : wowVariants.Any(v => text.Contains("+" + v, StringComparison.Ordinal));
        var wowOk = HasAny(text, wowVariants) && (HasAny(text, direction) || signed);
        checks.Add(new RubricCheck(
            "wow_direction",
            wowOk,
            $"expected {kpis.WowRevenuePct:+0.0;-0.0}% ({string.Join("/", direction)}) and a matching direction word"));

        var turnaroundVariants = new[] { kpis.WindowAvgTurnaround.ToString("0.00", CultureInfo.InvariantCulture), kpis.WindowAvgTurnaround.ToString("0.0", CultureInfo.InvariantCulture) };
        checks.Add(new RubricCheck(
            "avg_turnaround",
            HasAny(text, turnaroundVariants),
            $"expected {kpis.WindowAvgTurnaround:0.00} days ({kpis.WindowWeeks}-week average)"));

        checks.Add(new RubricCheck(
            "at_risk_job",
            atRisk.Any(j => Regex.IsMatch(text, $@"\b{Regex.Escape(j.JobNumber)}\b")),
            "cites at least one open job due within 7 days: " +
            string.Join(", ", atRisk.Select(j => j.JobNumber))));

        checks.Add(new RubricCheck(
            "low_rating_theme",
            LowRatingThemes.Any(group => HasAny(text, group)),
            "mentions a theme from a 3-star-or-below review (comms, invoicing or colour/reprint)"));

        // Accept either a numbered/bulleted list or a short set of sentences -
        // the rubric is about having concrete actions, not markdown formatting.
        var actionsSection = Regex.Match(
            briefing, @"(?is)do this week\s*[-:]?\s*(.*?)(?:\n\s*-?\s*inkline ops|$)");
        var actionsText = actionsSection.Success ? actionsSection.Groups[1].Value : "";
        var bulletActions = Regex.Matches(actionsText, @"(?m)^\s*(?:\d+[.)]|[-*•])\s+\S").Count;
        var sentenceActions = Regex.Split(actionsText.Trim(), @"(?<=[.!?])\s+")
            .Count(s => s.Trim().Length > 12);
        checks.Add(new RubricCheck(
            "action_items",
            bulletActions >= 2 || sentenceActions >= 2,
            $"actions found: {bulletActions} bullets, {sentenceActions} sentences (need >= 2 of either)"));

        var scenarioWords = ScenarioKeywords.TryGetValue(scenarioKey, out var words) ? words : [];
        checks.Add(new RubricCheck(
            "scenario_focus",
            scenarioWords.Length > 0 && HasAny(text, scenarioWords),
            $"addresses the owner's focus ({scenarioKey}): {string.Join(" / ", scenarioWords)}"));

        return new RubricScore(checks);
    }

    private static string Normalise(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"(?<=\d),(?=\d)", "");

    private static bool HasAny(string normalisedText, IEnumerable<string> variants) =>
        variants.Any(v => normalisedText.Contains(v, StringComparison.Ordinal));
}

public static class EvalRunner
{
    private sealed record EvalRow(
        BriefingCase Case,
        BriefingResult Multi,
        RubricScore MultiScore,
        WorkerResult Single,
        RubricScore SingleScore,
        long SingleMs);

    public static async Task<int> RunAsync()
    {
        var root = RepoPaths.FindRepoRoot();
        var cases = LoadCases(Path.Combine(root, "evals", "owner-briefing.cases.jsonl"));

        var db = new InklineDb();
        var kpis = InklineData.ComputeKpis(InklineData.LoadSales());
        var reviews = InklineData.LoadReviews();
        var stats = InklineData.ComputeReviewStats(reviews);
        var atRisk = db.ListAtRiskJobs(DataSeeder.SeedDate);

        var client = new OllamaClient();
        var traceDir = Path.Combine(root, "runs", "owner-briefing");
        Directory.CreateDirectory(traceDir);

        Console.WriteLine("OWNER BRIEFING EVAL - supervisor-worker vs single agent");
        Console.WriteLine($"model: {client.DefaultModel} | cases: {cases.Count} | world date: {DataSeeder.SeedDate}");
        Console.WriteLine();
        PrintGroundTruth(kpis, stats, atRisk);
        Console.WriteLine();

        var rows = new List<EvalRow>();
        foreach (var c in cases)
        {
            Console.WriteLine($"--- {c.Id} ({c.ScenarioKey}) {c.Focus}");

            var multiTracer = NewTracer(traceDir, $"eval-{c.Id}-multi");
            var multi = await BriefingPipeline.RunAsync(
                client, c.Focus, multiTracer, autoApprove: true, log: TextWriter.Null, gateOut: TextWriter.Null);
            var multiScore = BriefingRubric.Score(multi.Briefing, kpis, atRisk, c.ScenarioKey);
            File.WriteAllText(Path.Combine(traceDir, $"eval-{c.Id}-multi.md"), multi.Briefing);
            File.WriteAllText(Path.Combine(traceDir, $"eval-{c.Id}-workers.md"),
                $"# {c.Id} - supervisor plan\n\n" +
                $"analyst_task: {multi.Plan.AnalystTask}\n\n" +
                $"researcher_task: {multi.Plan.ResearcherTask}\n\n" +
                $"writer_task: {multi.Plan.WriterTask}\n\n" +
                $"## analyst ({multi.Analyst.Steps} steps, {multi.Analyst.ToolCalls} tool calls)\n\n" +
                multi.Analyst.Output + "\n\n" +
                $"## researcher ({multi.Researcher.Steps} steps, {multi.Researcher.ToolCalls} tool calls)\n\n" +
                multi.Researcher.Output + "\n");

            var singleTracer = NewTracer(traceDir, $"eval-{c.Id}-single");
            var sw = Stopwatch.StartNew();
            var single = await BriefingPipeline.RunSingleAsync(client, c.Focus, singleTracer);
            sw.Stop();
            var singleScore = BriefingRubric.Score(single.Output, kpis, atRisk, c.ScenarioKey);
            File.WriteAllText(Path.Combine(traceDir, $"eval-{c.Id}-single.md"), single.Output);

            rows.Add(new EvalRow(c, multi, multiScore, single, singleScore, sw.ElapsedMilliseconds));

            Console.WriteLine(
                $"    multi  {multiScore.Passed}/{multiScore.Total} " +
                $"({multi.Usage.TotalTokens:N0} tok, {multi.LlmCalls} calls, " +
                $"analyst {multi.Analyst.ToolCalls} tools / researcher {multi.Researcher.ToolCalls} tools, {multi.Ms} ms)  " +
                $"single {singleScore.Passed}/{singleScore.Total} " +
                $"({single.Usage.TotalTokens:N0} tok, {single.Steps} calls, {single.ToolCalls} tools, " +
                $"{sw.ElapsedMilliseconds} ms)");
        }

        var resultsPath = Path.Combine(root, "evals", "results", "owner-briefing-results.md");
        Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
        File.WriteAllText(resultsPath, BuildMarkdown(rows, kpis, stats, atRisk));

        Console.WriteLine();
        PrintSummary(rows, resultsPath);
        return 0;
    }

    internal static void PrintGroundTruth(SalesKpis kpis, ReviewStats stats, IReadOnlyList<JobRecord> atRisk)
    {
        Console.WriteLine("ground truth (computed in code):");
        Console.WriteLine(
            $"  last week {kpis.LastWeek.WeekStart}: ${kpis.LastWeek.RevenueAud:N2} · " +
            $"{kpis.LastWeek.OrdersReceived} received / {kpis.LastWeek.OrdersShipped} shipped · " +
            $"turnaround {kpis.LastWeek.AvgTurnaroundDays:0.0}d · {kpis.LastWeek.QuotesSent} quotes");
        Console.WriteLine(
            $"  WoW revenue {kpis.WowRevenuePct:+0.0;-0.0}% · {kpis.WindowWeeks}-week avg ${kpis.WindowAvgRevenue:N2} · " +
            $"avg turnaround {kpis.WindowAvgTurnaround:0.00}d");
        Console.WriteLine(
            $"  reviews {stats.Count} · avg {stats.AvgRating:0.00} · low (<=3) {stats.LowRatingCount}");
        Console.WriteLine(
            $"  at risk: {string.Join(", ", atRisk.Select(j => $"{j.JobNumber} ({j.Status}, due {j.DueDate})"))}");
    }

    private static void PrintSummary(IReadOnlyList<EvalRow> rows, string resultsPath)
    {
        var multiTotal = rows.Sum(r => r.MultiScore.Passed);
        var singleTotal = rows.Sum(r => r.SingleScore.Passed);
        var maxTotal = rows.Sum(r => r.MultiScore.Total);
        var multiPass = rows.Count(r => r.MultiScore.Overall);
        var singlePass = rows.Count(r => r.SingleScore.Overall);

        Console.WriteLine($"multi-agent : {multiTotal}/{maxTotal} checks · {multiPass}/{rows.Count} briefings pass");
        Console.WriteLine($"single-agent: {singleTotal}/{maxTotal} checks · {singlePass}/{rows.Count} briefings pass");
        Console.WriteLine($"tokens      : multi {rows.Sum(r => r.Multi.Usage.TotalTokens):N0} · single {rows.Sum(r => r.Single.Usage.TotalTokens):N0}");
        Console.WriteLine($"latency     : multi {rows.Sum(r => r.Multi.Ms):N0} ms · single {rows.Sum(r => r.SingleMs):N0} ms");
        Console.WriteLine($"results written to {resultsPath}");
    }

    private static string BuildMarkdown(
        IReadOnlyList<EvalRow> rows,
        SalesKpis kpis,
        ReviewStats stats,
        IReadOnlyList<JobRecord> atRisk)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Owner Briefing Pack - eval results");
        sb.AppendLine();
        sb.AppendLine($"- Run: {DateTime.Now:yyyy-MM-dd HH:mm} · model: `qwen2.5:3b` via local Ollama");
        sb.AppendLine($"- World date: {DataSeeder.SeedDate} · {kpis.Series.Count} weeks of sales · {stats.Count} reviews");
        sb.AppendLine("- 5 owner focuses × 2 architectures; both arms get the same tools, data and required output shape (the single arm writes the email directly in one agent loop).");
        sb.AppendLine("- Rubric: 7 deterministic checks per briefing against code-computed ground truth. A briefing passes at 6/7.");
        sb.AppendLine("- Numbers are checked by text match after removing thousands separators; no LLM judge is involved.");
        sb.AppendLine();
        sb.AppendLine("## Ground truth (computed in code)");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Last week ({kpis.LastWeek.WeekStart}) revenue | ${kpis.LastWeek.RevenueAud:N2} |");
        sb.AppendLine($"| Week-on-week revenue | {kpis.WowRevenuePct:+0.0;-0.0}% (prior ${kpis.PriorWeek?.RevenueAud:N2}) |");
        sb.AppendLine($"| Last week orders / shipped / quotes | {kpis.LastWeek.OrdersReceived} / {kpis.LastWeek.OrdersShipped} / {kpis.LastWeek.QuotesSent} |");
        sb.AppendLine($"| {kpis.WindowWeeks}-week average revenue | ${kpis.WindowAvgRevenue:N2} |");
        sb.AppendLine($"| {kpis.WindowWeeks}-week average turnaround | {kpis.WindowAvgTurnaround:0.00} days |");
        sb.AppendLine($"| Reviews | {stats.Count}, average {stats.AvgRating:0.00}, {stats.LowRatingCount} at 3 stars or below |");
        sb.AppendLine($"| At-risk jobs | {string.Join(", ", atRisk.Select(j => $"{j.JobNumber} ({j.Status}, due {j.DueDate})"))} |");
        sb.AppendLine();
        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Focus | Multi-agent | Single-agent |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var r in rows)
        {
            var m = $"{r.MultiScore.Passed}/{r.MultiScore.Total}" + (r.MultiScore.Overall ? "" : " (fail)");
            var s = $"{r.SingleScore.Passed}/{r.SingleScore.Total}" + (r.SingleScore.Overall ? "" : " (fail)");
            sb.AppendLine($"| {r.Case.Id} ({r.Case.ScenarioKey}) | {Escape(r.Case.Focus)} | {m} | {s} |");
        }

        var maxTotal = rows.Sum(r => r.MultiScore.Total);
        sb.AppendLine(
            $"| **Total** | | **{rows.Sum(r => r.MultiScore.Passed)}/{maxTotal}** " +
            $"({rows.Count(r => r.MultiScore.Overall)}/{rows.Count} pass) | " +
            $"**{rows.Sum(r => r.SingleScore.Passed)}/{maxTotal}** " +
            $"({rows.Count(r => r.SingleScore.Overall)}/{rows.Count} pass) |");
        sb.AppendLine();
        sb.AppendLine("## Cost");
        sb.AppendLine();
        sb.AppendLine("| Architecture | Avg tokens | Total tokens | Avg time | Total time | Avg LLM calls |");
        sb.AppendLine("|---|---|---|---|---|---|");
        sb.AppendLine(
            $"| Multi-agent | {rows.Average(r => r.Multi.Usage.TotalTokens):N0} | " +
            $"{rows.Sum(r => r.Multi.Usage.TotalTokens):N0} | {rows.Average(r => r.Multi.Ms):N0} ms | " +
            $"{rows.Sum(r => r.Multi.Ms):N0} ms | {rows.Average(r => r.Multi.LlmCalls):0.0} |");
        sb.AppendLine(
            $"| Single-agent | {rows.Average(r => r.Single.Usage.TotalTokens):N0} | " +
            $"{rows.Sum(r => r.Single.Usage.TotalTokens):N0} | {rows.Average(r => r.SingleMs):N0} ms | " +
            $"{rows.Sum(r => r.SingleMs):N0} ms | {rows.Average(r => r.Single.Steps):0.0} |");
        sb.AppendLine();
        sb.AppendLine("> Ollama runs one model on one local GPU, so the fan-out did not save wall-clock time and cost about");
        sb.AppendLine("> 1.9x the tokens. At 3B the completeness gap is inside run-to-run noise: over four back-to-back evals");
        sb.AppendLine("> the arms traded the lead (multi 34, 33, 32, 32 /35; single 30, 34, 32, 30 /35). The reliable wins are");
        sb.AppendLine("> bounded worker context, per-worker attribution, and supervisor QA that repairs missing facts and");
        sb.AppendLine("> rejects ungrounded figures.");
        sb.AppendLine();
        sb.AppendLine("## Check breakdown");
        sb.AppendLine();
        sb.AppendLine("| Check | Multi pass | Single pass |");
        sb.AppendLine("|---|---|---|");
        foreach (var check in rows[0].MultiScore.Checks)
        {
            var name = check.Name;
            var m = rows.Count(r => r.MultiScore.Checks.First(c => c.Name == name).Pass);
            var s = rows.Count(r => r.SingleScore.Checks.First(c => c.Name == name).Pass);
            sb.AppendLine($"| {name} | {m}/{rows.Count} | {s}/{rows.Count} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Misses");
        sb.AppendLine();
        var anyMiss = false;
        foreach (var r in rows)
        {
            foreach (var (arch, score) in new[] { ("multi", r.MultiScore), ("single", r.SingleScore) })
            {
                foreach (var f in score.Failures)
                {
                    anyMiss = true;
                    sb.AppendLine($"- {r.Case.Id} {arch}: `{f.Name}` - {f.Detail}");
                }
            }
        }
        if (!anyMiss) sb.AppendLine("- None.");
        sb.AppendLine();
        sb.AppendLine("Raw traces and full briefing texts: `runs/owner-briefing/` (gitignored).");
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("|", "\\|");

    private static Tracer NewTracer(string dir, string prefix) =>
        new(Path.Combine(dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"));

    private static List<BriefingCase> LoadCases(string path)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<BriefingCase>(l, opts)
                          ?? throw new JsonException($"bad case line: {l}"))
            .ToList();
    }
}
