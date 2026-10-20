using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tracing;
using Agentic.AgentCore.Tools;

namespace Agentic.Patterns.WeeklyReport;

internal sealed record EvalCase(
    string Id,
    string Question,
    string[] ExpectedTools,
    string[] ExpectKeywords,
    string[] ExpectKeywordsAny,
    string[]? ExpectedToolsAny = null);

internal sealed record CaseOutcome(
    EvalCase Case,
    PipelineResult Result,
    bool PlanOk,
    bool ToolOk,
    bool AnswerOk,
    bool VerifiedOk,
    IReadOnlyList<string> CalledTools,
    string? Error);

/// <summary>
/// Runs each report request through the FULL pipeline (plan → auto-approved
/// gate → execute → assemble → verify) live against Ollama and scores:
///   plan ok   = pipeline completed, every planned step executed successfully
///   tool match = every expected tool ran (or any listed alternative)
///   answer    = keyword anchors present in the final (possibly repaired) report
///   verified  = the agent's own self-check passed (reported as a metric)
/// </summary>
internal static class EvalRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task<int> RunAsync()
    {
        if (!await Program.Client.IsHealthyAsync())
        {
            Console.Error.WriteLine($"Ollama is not reachable at {Program.Client.BaseUrl}.");
            return 1;
        }

        var casesPath = Path.Combine(RepoPaths.FindRepoRoot(), "evals", "weekly-report.cases.jsonl");
        if (!File.Exists(casesPath))
        {
            Console.Error.WriteLine($"Eval cases not found: {casesPath}");
            return 1;
        }

        var cases = File.ReadLines(casesPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<EvalCase>(l, JsonOpts)!)
            .ToList();

        Directory.CreateDirectory(Program.TraceDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var rawPath = Path.Combine(Program.TraceDir, $"eval-{stamp}.jsonl");

        Console.WriteLine($"Running {cases.Count} full-pipeline eval cases against {Program.Client.DefaultModel}...\n");

        var outcomes = new List<CaseOutcome>();
        foreach (var evalCase in cases)
        {
            var outcome = await RunCaseAsync(evalCase, rawPath);
            outcomes.Add(outcome);

            var verdict = outcome.Error is not null ? $"ERROR {Truncate(outcome.Error, 30)}"
                : outcome.PlanOk && outcome.ToolOk && outcome.AnswerOk ? "pass"
                    : $"plan:{Mark(outcome.PlanOk)} tool:{Mark(outcome.ToolOk)} report:{Mark(outcome.AnswerOk)}";
            var r = outcome.Result;
            var execNote = r.Plan is null ? "-" : $"{r.Executions.Count(e => !e.Ok)}x/{r.Executions.Count}failed";
            Console.WriteLine(
                $"{evalCase.Id,-4} {verdict,-38} {execNote,-10} {r.LlmCalls,2} llm, {r.Usage.TotalTokens,5} tok, {r.Ms,5} ms  " +
                $"{Truncate(evalCase.Question, 44)}");
        }

        var resultsPath = Path.Combine(RepoPaths.FindRepoRoot(), "evals", "results", "weekly-report-results.md");
        WriteResultsMarkdown(resultsPath, Program.Client.DefaultModel, outcomes);
        PrintAggregate(outcomes);
        Console.WriteLine($"\nResults written to {resultsPath}");
        Console.WriteLine($"Raw run records: {rawPath}");
        return 0;
    }

    private static async Task<CaseOutcome> RunCaseAsync(EvalCase evalCase, string rawPath)
    {
        var tracer = new Tracer(rawPath);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(240));
            var result = await ReportPipeline.RunAsync(
                Program.Client, ReportTools.Build(new Agentic.AgentCore.Data.InklineDb()),
                evalCase.Question, autoApprove: true, tracer, log: null, cts.Token);

            if (result.Outcome != PipelineOutcome.Completed)
            {
                var failedOutcome = new CaseOutcome(
                    evalCase, result, PlanOk: false, ToolOk: false, AnswerOk: false, VerifiedOk: false,
                    CalledTools: [], Error: result.Failure ?? $"pipeline {result.Outcome}");
                AppendRaw(rawPath, failedOutcome, missingKeywords: []);
                return failedOutcome;
            }

            var called = result.Executions
                .Where(e => e.Ok)
                .Select(e => e.Step.Tool)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var planOk = result.Plan is not null
                         && result.Executions.Count == result.Plan.Steps.Count
                         && result.Executions.All(e => e.Ok);

            var toolOk = evalCase.ExpectedTools.All(t => called.Contains(t, StringComparer.OrdinalIgnoreCase))
                         && (evalCase.ExpectedToolsAny is not { Length: > 0 } alternatives
                             || alternatives.Any(t => called.Contains(t, StringComparer.OrdinalIgnoreCase)));

            var reportNorm = Norm(result.Report);
            var missing = evalCase.ExpectKeywords.Where(k => !reportNorm.Contains(Norm(k))).ToList();
            var anyOk = evalCase.ExpectKeywordsAny.Length == 0
                        || evalCase.ExpectKeywordsAny.Any(p => reportNorm.Contains(Norm(p)));
            var answerOk = missing.Count == 0 && anyOk;

            var verifiedOk = (result.VerificationAfterRepair ?? result.Verification)?.Ok == true;

            var outcome = new CaseOutcome(
                evalCase, result, planOk, toolOk, answerOk, verifiedOk, called, Error: null);
            AppendRaw(rawPath, outcome, missing);
            return outcome;
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException is not null
                ? $"{ex.GetType().Name}: {ex.Message} (inner: {ex.InnerException.Message})"
                : $"{ex.GetType().Name}: {ex.Message}";
            var outcome = new CaseOutcome(
                evalCase, new PipelineResult { Outcome = PipelineOutcome.PlanInvalid },
                false, false, false, false, [], Error: detail);
            AppendRaw(rawPath, outcome, missingKeywords: []);
            Console.WriteLine($"     ! {detail}");
            return outcome;
        }
    }

    private static void AppendRaw(string rawPath, CaseOutcome o, IReadOnlyList<string> missingKeywords)
    {
        var r = o.Result;
        var line = JsonSerializer.Serialize(new
        {
            id = o.Case.Id,
            question = o.Case.Question,
            outcome = r.Outcome.ToString(),
            plan = r.Plan is null ? null : r.Plan.Steps.Select(s => new { s.Step, s.Tool, s.Section, args = s.Args?.ToJsonString() }),
            gate = r.Gate?.Decision.ToString(),
            executions = r.Executions.Select(e => new { step = e.Step.Step, tool = e.Step.Tool, ok = e.Ok, repaired = e.Repaired, error = e.Error }),
            calledTools = o.CalledTools,
            planOk = o.PlanOk,
            toolOk = o.ToolOk,
            answerOk = o.AnswerOk,
            verifiedOk = o.VerifiedOk,
            missingKeywords,
            reportRepaired = r.ReportRepaired,
            llmCalls = r.LlmCalls,
            tokens = r.Usage.TotalTokens,
            ms = r.Ms,
            error = o.Error,
            report = r.Report,
        });
        File.AppendAllText(rawPath, line + Environment.NewLine);
    }

    private static void WriteResultsMarkdown(string path, string model, List<CaseOutcome> outcomes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var done = outcomes.Where(o => o.Error is null).ToList();

        var planOk = outcomes.Count(o => o.PlanOk);
        var toolOk = outcomes.Count(o => o.ToolOk);
        var answerOk = outcomes.Count(o => o.AnswerOk);
        var bothOk = outcomes.Count(o => o.PlanOk && o.ToolOk && o.AnswerOk);
        var verifiedOk = outcomes.Count(o => o.VerifiedOk);
        var totalExecs = done.Sum(o => o.Result.Executions.Count);
        var execOk = done.Sum(o => o.Result.Executions.Count(e => e.Ok));
        var repaired = done.Sum(o => o.Result.Executions.Count(e => e.Repaired));

        var md = new StringBuilder();
        md.AppendLine("# Eval results — P2 Weekly Report (pattern 2: planning & task decomposition)");
        md.AppendLine();
        md.AppendLine($"- Run date: {DateTime.Now:yyyy-MM-dd HH:mm} (local, live Ollama)");
        md.AppendLine($"- Model: `{model}`, temperature 0.2; every case runs the FULL pipeline: plan → gate (auto-approved) → execute → assemble → self-verify");
        md.AppendLine($"- Cases: {outcomes.Count} (evals/weekly-report.cases.jsonl)");
        md.AppendLine("- Scoring: **plan ok** = pipeline completed and every planned step executed successfully;");
        md.AppendLine("  **tool match** = every expected tool ran; **report match** = keyword anchors in the final");
        md.AppendLine("  (possibly repaired) report, normalised for case/spaces/underscores/commas.");
        md.AppendLine("  **self-check** is the agent's own verification verdict — reported as a metric, not a gate.");
        md.AppendLine();
        md.AppendLine("## Aggregate");
        md.AppendLine();
        md.AppendLine("| Metric | Value |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Plan ok (all steps executed) | {planOk}/{outcomes.Count} ({Pct(planOk, outcomes.Count)}) |");
        md.AppendLine($"| Step success (executions) | {execOk}/{totalExecs} ({Pct(execOk, totalExecs)}) |");
        md.AppendLine($"| Steps needing args repair | {repaired}/{totalExecs} |");
        md.AppendLine($"| Tool match | {toolOk}/{outcomes.Count} ({Pct(toolOk, outcomes.Count)}) |");
        md.AppendLine($"| Report keyword match | {answerOk}/{outcomes.Count} ({Pct(answerOk, outcomes.Count)}) |");
        md.AppendLine($"| Full pass (plan+tool+report) | {bothOk}/{outcomes.Count} ({Pct(bothOk, outcomes.Count)}) |");
        md.AppendLine($"| Agent self-check passed | {verifiedOk}/{outcomes.Count} ({Pct(verifiedOk, outcomes.Count)}) |");
        var planned = done.Where(o => o.Result.Plan is not null).ToList();
        md.AppendLine($"| Avg plan steps | {(planned.Count == 0 ? "—" : planned.Average(o => o.Result.Plan!.Steps.Count).ToString("0.0"))} |");
        md.AppendLine($"| Avg LLM calls / tokens per case | {(done.Count == 0 ? "—" : $"{done.Average(o => o.Result.LlmCalls):0.0} / {done.Average(o => o.Result.Usage.TotalTokens):0}")} |");
        md.AppendLine($"| Total tokens | {done.Sum(o => o.Result.Usage.TotalTokens):N0} |");
        md.AppendLine();
        md.AppendLine("## Cases");
        md.AppendLine();
        md.AppendLine("| ID | Request | Plan | Called tools | Tool | Report | Self-check | Tokens |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var o in outcomes)
        {
            var plan = o.Result.Plan is null ? "—" : $"{o.Result.Plan.Steps.Count} steps, {o.Result.Executions.Count(e => !e.Ok)} failed";
            var called = o.CalledTools.Count == 0 ? "—" : string.Join(", ", o.CalledTools);
            md.AppendLine(
                $"| {o.Case.Id} | {Escape(Truncate(o.Case.Question, 60))} | {plan} | {Escape(called)} " +
                $"| {(o.ToolOk ? "✓" : "✗")} | {(o.AnswerOk ? "✓" : "✗")} | {(o.VerifiedOk ? "✓" : "✗")} | {o.Result.Usage.TotalTokens} |");
        }

        md.AppendLine();
        md.AppendLine("## Failed cases");
        md.AppendLine();
        var failures = outcomes.Where(o => !(o.PlanOk && o.ToolOk && o.AnswerOk)).ToList();
        if (failures.Count == 0) md.AppendLine("All cases passed.");
        foreach (var o in failures)
        {
            md.AppendLine($"### {o.Case.Id} — {Escape(o.Case.Question)}");
            md.AppendLine();
            if (o.Error is not null)
            {
                md.AppendLine($"- Error: {Escape(o.Error)}");
                md.AppendLine();
                continue;
            }
            md.AppendLine($"- Expected tools: `{string.Join("`, `", o.Case.ExpectedTools)}`" +
                          (o.Case.ExpectedToolsAny is { Length: > 0 } alt ? $" or any of: `{string.Join("`, `", alt)}`" : ""));
            md.AppendLine($"- Called: `{(o.CalledTools.Count == 0 ? "none" : string.Join("`, `", o.CalledTools))}`");
            md.AppendLine($"- Missing keywords: `{string.Join("`, `", o.Case.ExpectKeywords)}` required" +
                          (o.Case.ExpectKeywordsAny.Length > 0 ? $" / any of `{string.Join("`, `", o.Case.ExpectKeywordsAny)}`" : ""));
            if (o.Result.Verification is { } v)
                md.AppendLine($"- Self-check: ok={v.Ok}, missing=[{string.Join(", ", v.MissingSections)}], issues=[{string.Join("; ", v.Issues)}]");
            md.AppendLine($"- Report: {Escape(Truncate(o.Result.Report, 700))}");
            md.AppendLine();
        }

        File.WriteAllText(path, md.ToString());
    }

    private static void PrintAggregate(List<CaseOutcome> outcomes)
    {
        var planOk = outcomes.Count(o => o.PlanOk);
        var toolOk = outcomes.Count(o => o.ToolOk);
        var answerOk = outcomes.Count(o => o.AnswerOk);
        var bothOk = outcomes.Count(o => o.PlanOk && o.ToolOk && o.AnswerOk);
        var verifiedOk = outcomes.Count(o => o.VerifiedOk);
        Console.WriteLine($"\n--- {outcomes.Count} cases ---");
        Console.WriteLine($"plan ok:      {planOk}/{outcomes.Count} ({Pct(planOk, outcomes.Count)})");
        Console.WriteLine($"tool match:   {toolOk}/{outcomes.Count} ({Pct(toolOk, outcomes.Count)})");
        Console.WriteLine($"report match: {answerOk}/{outcomes.Count} ({Pct(answerOk, outcomes.Count)})");
        Console.WriteLine($"full pass:    {bothOk}/{outcomes.Count} ({Pct(bothOk, outcomes.Count)})");
        Console.WriteLine($"self-check:   {verifiedOk}/{outcomes.Count} ({Pct(verifiedOk, outcomes.Count)})");
        var done = outcomes.Where(o => o.Error is null).ToList();
        Console.WriteLine($"avg {(done.Count == 0 ? "—" : $"{done.Average(o => o.Result.LlmCalls):0.0} llm calls, {done.Average(o => o.Result.Usage.TotalTokens):0} tokens per case")}; total {done.Sum(o => o.Result.Usage.TotalTokens):N0}");
    }

    private static string Norm(string s) =>
        s.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace(",", "");

    private static string Mark(bool ok) => ok ? "ok" : "NO";

    private static string Pct(int n, int total) => total == 0 ? "-" : $"{100.0 * n / total:0}%";

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
