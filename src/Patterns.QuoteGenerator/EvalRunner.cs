using System.Text;
using System.Text.Json;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tracing;

namespace Agentic.Patterns.QuoteGenerator;

/// <summary>
/// Same-lineage A/B eval (the plan's headline metric for pattern 3): every
/// job spec runs ONCE through the full pipeline (≤3 critic-driven revision
/// rounds), and we score the SAME draft lineage twice — the first draft as
/// "reflection off" and the final draft as "reflection on". Both scores come
/// from the identical deterministic rule critic, so the lift measures exactly
/// what reflection added, with no draft-to-draft luck in between. Reports
/// pass-rate lift, per-category corrections, and the token cost of reflection.
/// </summary>
internal static class EvalRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record ModeScore(
        bool Passed, bool MathOk, bool TurnOk, bool ClauseOk, bool BannedOk, bool TemplateOk,
        string[] Detail, int Revisions, int LlmCalls, int Tokens, string? Error)
    {
        public int Issues => Detail.Length;
    }

    private sealed record CaseResult(string Id, QuoteJob Job, decimal Total, ModeScore Off, ModeScore On)
    {
        public string? Pressure => Job.Pressure;
    }

    public static async Task<int> RunAsync()
    {
        if (!await Program.Client.IsHealthyAsync())
        {
            Console.Error.WriteLine($"Ollama is not reachable at {Program.Client.BaseUrl}.");
            return 1;
        }

        var casesPath = Path.Combine(RepoPaths.FindRepoRoot(), "evals", "quote-generator.cases.jsonl");
        if (!File.Exists(casesPath))
        {
            Console.Error.WriteLine($"Eval cases not found: {casesPath}");
            return 1;
        }

        var cases = File.ReadLines(casesPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l =>
            {
                using var doc = JsonDocument.Parse(l);
                var id = doc.RootElement.GetProperty("id").GetString()!;
                return (Id: id, Job: JsonSerializer.Deserialize<QuoteJob>(l, JsonOpts)!);
            })
            .ToList();

        Directory.CreateDirectory(Program.TraceDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var rawPath = Path.Combine(Program.TraceDir, $"eval-{stamp}.jsonl");

        Console.WriteLine($"Running {cases.Count} specs (single pipeline run each, ≤3 revision rounds) against {Program.Client.DefaultModel}...\n");

        var results = new List<CaseResult>();

        foreach (var (id, job) in cases)
        {
            var (result, error) = await RunCaseAsync(id, job, rawPath);
            var total = QuoteEngine.Quote(job.Request).Total;
            var (off, on) = DeriveScores(result, error);
            results.Add(new CaseResult(id, job, total, off, on));
            AppendRaw(rawPath, id, job, result, off, on);

            Console.WriteLine(
                $"{id,-4} off:{Verdict(off)} on:{Verdict(on)}  " +
                $"{job.Quantity,4} {job.Garment,-6} ${total,9:N2}  " +
                $"{(job.Pressure is null ? "" : $"[bait: {job.Pressure}] ")}{Truncate(job.SpecText(), 44)}");
        }

        var resultsPath = Path.Combine(RepoPaths.FindRepoRoot(), "evals", "results", "quote-generator-results.md");
        WriteResultsMarkdown(resultsPath, Program.Client.DefaultModel, results);
        PrintAggregate(results);
        Console.WriteLine($"\nResults written to {resultsPath}");
        Console.WriteLine($"Raw run records: {rawPath}");
        return 0;
    }

    private static async Task<(QuoteResult? Result, string? Error)> RunCaseAsync(string id, QuoteJob job, string rawPath)
    {
        var tracer = new Tracer(rawPath);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(240));
            var result = await QuotePipeline.RunAsync(
                Program.Client, job,
                maxRevisionRounds: 3,
                autoApprove: true, tracer, log: null, cts.Token);
            return (result, null);
        }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"     ! {id}: {error}");
            AppendRaw(rawPath, id, job, result: null, off: ErrorScore(error), on: ErrorScore(error));
            return (null, error);
        }
    }

    private static (ModeScore Off, ModeScore On) DeriveScores(QuoteResult? result, string? error)
    {
        if (result is null)
        {
            var failed = ErrorScore(error ?? "run failed");
            return (failed, failed);
        }

        // Reflection off = the first draft's critique, scored with ONE LLM call.
        var firstCritique = result.RoundHistory[0];
        var off = Score(firstCritique, revisions: 0,
            llmCalls: Math.Min(1, result.CallUsage.Count),
            tokens: result.CallUsage.Count > 0 ? result.CallUsage[0].TotalTokens : 0);

        // Reflection on = the final draft, scored with every call it took.
        var on = Score(result.Critique, revisions: result.Revisions,
            llmCalls: result.LlmCalls, tokens: result.Usage.TotalTokens);

        return (off, on);
    }

    private static ModeScore Score(CritiqueResult critique, int revisions, int llmCalls, int tokens)
    {
        var cats = critique.Issues.Select(i => i.Category).ToHashSet();
        return new ModeScore(
            critique.Passed,
            MathOk: !cats.Contains("math"),
            TurnOk: !cats.Contains("turnaround"),
            ClauseOk: !cats.Contains("clause"),
            BannedOk: !cats.Contains("banned"),
            TemplateOk: !cats.Contains("template"),
            Detail: critique.Issues.Select(i => $"[{i.Category}] {i.Message}").ToArray(),
            revisions, llmCalls, tokens, Error: null);
    }

    private static ModeScore ErrorScore(string error) =>
        new(false, false, false, false, false, false, [], 0, 0, 0, error);

    private static void AppendRaw(string rawPath, string id, QuoteJob job,
        QuoteResult? result, ModeScore off, ModeScore on)
    {
        var line = JsonSerializer.Serialize(new
        {
            id,
            customer = job.Customer,
            spec = job.SpecText(),
            total = result?.Breakdown.Total,
            first_draft = result?.FirstDraft,
            final_draft = result?.FinalText,
            pressure = job.Pressure,
            off = ScoreRecord(off),
            on = ScoreRecord(on),
            revisions = result?.Revisions,
            llmCalls = result?.LlmCalls,
            tokens = result?.Usage.TotalTokens,
            requiresOwnerApproval = result?.RequiresOwnerApproval,
            gate = result?.Gate?.Decision.ToString(),
            error = off.Error ?? on.Error,
        });
        File.AppendAllText(rawPath, line + Environment.NewLine);

        static object ScoreRecord(ModeScore s) => new
        {
            passed = s.Passed,
            issues = s.Detail,
            revisions = s.Revisions,
            llmCalls = s.LlmCalls,
            tokens = s.Tokens,
            error = s.Error,
        };
    }

    private static void WriteResultsMarkdown(string path, string model, List<CaseResult> results)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var off = results.Select(r => r.Off).ToList();
        var on = results.Select(r => r.On).ToList();

        var md = new StringBuilder();
        md.AppendLine("# Eval results — P3 Quote Generator (pattern 3: reflection & self-improvement)");
        md.AppendLine();
        md.AppendLine($"- Run date: {DateTime.Now:yyyy-MM-dd HH:mm} (local, live Ollama)");
        md.AppendLine($"- Model: `{model}`, temperature 0.2");
        md.AppendLine("- Method: **same-lineage ablation** — each spec runs ONCE through the full pipeline (≤3 revision rounds);");
        md.AppendLine("  the first draft is scored as \"reflection off\" and the final draft as \"reflection on\" — same draft lineage,");
        md.AppendLine("  same deterministic rule critic, so the lift is exactly what reflection added (no draft-luck confound).");
        md.AppendLine($"- Cases: {results.Count} (evals/quote-generator.cases.jsonl)");
        md.AppendLine($"- **Adversarial input:** {results.Count(r => r.Pressure is not null)} of {results.Count} specs carry customer notes that bait a rule");
        md.AppendLine("  (guarantee requests, price matching, deposit pushback); the rest are clean.");
        md.AppendLine("- Critic rules: **math** = total to the cent matches the QuoteEngine; **turnaround** = exact");
        md.AppendLine("  business days; **clause** = all four required clauses present (proof approval, 50% deposit,");
        md.AppendLine("  14-day terms, ±5% size tolerance); **template** = no unfilled placeholders like `[Customer Name]` and no");
        md.AppendLine("  price-table artefacts like `colour-location(s)`; **banned** = no forbidden phrases.");
        md.AppendLine();
        md.AppendLine("## Aggregate");
        md.AppendLine();
        md.AppendLine("| Metric | No reflection (first draft) | With reflection (final) |");
        md.AppendLine("|---|---|---|");
        md.AppendLine($"| Pass rate (all rules) | **{Count(off, s => s.Passed)}/{results.Count} ({Pct(Count(off, s => s.Passed), results.Count)})** | **{Count(on, s => s.Passed)}/{results.Count} ({Pct(Count(on, s => s.Passed), results.Count)})** |");
        md.AppendLine($"| Math correct | {Count(off, s => s.MathOk)}/{results.Count} | {Count(on, s => s.MathOk)}/{results.Count} |");
        md.AppendLine($"| Turnaround correct | {Count(off, s => s.TurnOk)}/{results.Count} | {Count(on, s => s.TurnOk)}/{results.Count} |");
        md.AppendLine($"| All clauses present | {Count(off, s => s.ClauseOk)}/{results.Count} | {Count(on, s => s.ClauseOk)}/{results.Count} |");
        md.AppendLine($"| No template artefacts | {Count(off, s => s.TemplateOk)}/{results.Count} | {Count(on, s => s.TemplateOk)}/{results.Count} |");
        md.AppendLine($"| No banned phrases | {Count(off, s => s.BannedOk)}/{results.Count} | {Count(on, s => s.BannedOk)}/{results.Count} |");
        md.AppendLine($"| Avg LLM calls | {Avg(off, s => (double)s.LlmCalls):0.0} | {Avg(on, s => (double)s.LlmCalls):0.0} |");
        md.AppendLine($"| Avg tokens | {Avg(off, s => s.Tokens):N0} | {Avg(on, s => s.Tokens):N0} |");
        md.AppendLine($"| Total tokens ({results.Count} specs) | {Sum(off, s => s.Tokens):N0} | {Sum(on, s => s.Tokens):N0} |");
        md.AppendLine();

        var pressured = results.Where(r => r.Pressure is not null).ToList();
        if (pressured.Count > 0)
        {
            md.AppendLine("### Pressure cases only");
            md.AppendLine();
            md.AppendLine("| Metric | First draft | After reflection |");
            md.AppendLine("|---|---|---|");
            md.AppendLine($"| Pass rate | {Count(pressured.Select(r => r.Off).ToList(), s => s.Passed)}/{pressured.Count} | {Count(pressured.Select(r => r.On).ToList(), s => s.Passed)}/{pressured.Count} |");
            md.AppendLine($"| Banned phrases clean | {Count(pressured.Select(r => r.Off).ToList(), s => s.BannedOk)}/{pressured.Count} | {Count(pressured.Select(r => r.On).ToList(), s => s.BannedOk)}/{pressured.Count} |");
            md.AppendLine($"| All clauses present | {Count(pressured.Select(r => r.Off).ToList(), s => s.ClauseOk)}/{pressured.Count} | {Count(pressured.Select(r => r.On).ToList(), s => s.ClauseOk)}/{pressured.Count} |");
            md.AppendLine();
        }
        md.AppendLine("## Cases");
        md.AppendLine();
        md.AppendLine("| ID | Spec | Total | Baited rule | Off: first draft | On: final draft | Revisions |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            md.AppendLine(
                $"| {r.Id} | {Escape(Truncate(r.Job.SpecText(), 46))} | ${r.Total:N2} " +
                $"| {Escape(r.Pressure ?? "—")} | {Cell(r.Off)} | {Cell(r.On)} | {r.On.Revisions} |");
        }

        var failingOn = results.Where(r => r.On.Error is null && !r.On.Passed).ToList();
        md.AppendLine();
        md.AppendLine("## Still failing WITH reflection");
        md.AppendLine();
        if (failingOn.Count == 0)
            md.AppendLine("Nothing — every spec passes the rule critic after at most three revisions.");
        foreach (var r in failingOn)
        {
            md.AppendLine($"### {r.Id} — {Escape(r.Job.SpecText())} (${r.Total:N2})");
            md.AppendLine();
            md.AppendLine($"- Issues after 3 revisions: {Escape(string.Join("; ", r.On.Detail))}");
            md.AppendLine($"- First draft: {(r.Off.Passed ? "pass" : $"fail ({r.Off.Issues} issues)")}");
            md.AppendLine();
        }

        var fixedByReflection = results.Where(r => r.Off.Error is null && !r.Off.Passed && r.On.Passed).ToList();
        md.AppendLine("## Fixed by reflection");
        md.AppendLine();
        if (fixedByReflection.Count == 0)
            md.AppendLine("Nothing — the first drafts already passed everywhere.");
        foreach (var r in fixedByReflection)
        {
            md.AppendLine($"- **{r.Id}** ({Escape(Truncate(r.Job.SpecText(), 60))}): " +
                          $"first draft failed [{Escape(string.Join("; ", r.Off.Detail))}] → fixed in {r.On.Revisions} revision(s)");
        }
        md.AppendLine();

        var errors = results.Where(r => r.Off.Error is not null || r.On.Error is not null).ToList();
        if (errors.Count > 0)
        {
            md.AppendLine("## Errors");
            md.AppendLine();
            foreach (var r in errors)
                md.AppendLine($"- {r.Id}: {r.Off.Error ?? r.On.Error}");
            md.AppendLine();
        }

        File.WriteAllText(path, md.ToString());
    }

    private static void PrintAggregate(List<CaseResult> results)
    {
        var off = results.Select(r => r.Off).ToList();
        var on = results.Select(r => r.On).ToList();
        Console.WriteLine($"\n--- {results.Count} specs (same-lineage first-draft vs final) ---");
        Console.WriteLine($"pass rate:  off {Count(off, s => s.Passed)}/{results.Count} ({Pct(Count(off, s => s.Passed), results.Count)})" +
                          $"  →  on {Count(on, s => s.Passed)}/{results.Count} ({Pct(Count(on, s => s.Passed), results.Count)})");
        Console.WriteLine($"math:       off {Count(off, s => s.MathOk)}/{results.Count} → on {Count(on, s => s.MathOk)}/{results.Count}");
        Console.WriteLine($"clauses:    off {Count(off, s => s.ClauseOk)}/{results.Count} → on {Count(on, s => s.ClauseOk)}/{results.Count}");
        Console.WriteLine($"template:   off {Count(off, s => s.TemplateOk)}/{results.Count} → on {Count(on, s => s.TemplateOk)}/{results.Count}");
        var pressured = results.Where(r => r.Pressure is not null).ToList();
        if (pressured.Count > 0)
            Console.WriteLine($"pressure:   off {Count(pressured.Select(r => r.Off).ToList(), s => s.Passed)}/{pressured.Count}" +
                              $" → on {Count(pressured.Select(r => r.On).ToList(), s => s.Passed)}/{pressured.Count}  (baited rules only)");
        Console.WriteLine($"tokens:     first draft total {Sum(off, s => s.Tokens):N0} → full pipeline {Sum(on, s => s.Tokens):N0}" +
                          $" (reflection costs {(Sum(on, s => s.Tokens) - Sum(off, s => s.Tokens)):N0} more)");
    }

    private static int Count(List<ModeScore> scores, Func<ModeScore, bool> f) => scores.Count(f);
    private static double Avg(List<ModeScore> scores, Func<ModeScore, double> f) =>
        scores.Count == 0 ? 0 : scores.Average(f);
    private static int Sum(List<ModeScore> scores, Func<ModeScore, int> f) => scores.Sum(f);

    private static string Verdict(ModeScore s) =>
        s.Error is not null ? $"ERROR({Truncate(s.Error, 18)})"
            : s.Passed ? $"PASS{(s.Revisions > 0 ? $"(rev{s.Revisions})" : "")}"
                : $"FAIL({s.Issues})";

    private static string Cell(ModeScore s) =>
        s.Error is not null ? "error" : s.Passed ? (s.Revisions > 0 ? $"✓ after {s.Revisions} rev" : "✓") : "✗ " + s.Issues + " issues";

    private static string Pct(int n, int total) => total == 0 ? "-" : $"{100.0 * n / total:0}%";
    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ");
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
