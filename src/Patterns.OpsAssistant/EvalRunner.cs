using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Agentic.AgentCore;
using Agentic.AgentCore.Tracing;

namespace Agentic.Patterns.OpsAssistant;

/// <summary>One labelled eval case: a staff question, the tools a competent agent must call, and answer anchors.</summary>
/// <remarks>
/// ExpectedTools must ALL be called (tool match). When a question has more
/// than one legitimate tool path, list the alternatives in ExpectedToolsAny —
/// calling ANY of those also satisfies tool match.
/// </remarks>
internal sealed record EvalCase(
    string Id,
    string Question,
    string[] ExpectedTools,
    string[] ExpectKeywords,
    string[] ExpectKeywordsAny,
    string[]? ExpectedToolsAny = null);

internal sealed record CaseOutcome(
    EvalCase Case,
    IReadOnlyList<string> CalledTools,
    string FinalText,
    bool ToolOk,
    bool AnswerOk,
    StopReason StopReason,
    int Steps,
    int Tokens,
    long Ms,
    string? Error);

/// <summary>
/// Runs the 20-case eval suite live against Ollama and writes the curated
/// results to evals/results/ (committed) plus raw per-run records to runs/
/// (gitignored).
///
/// Scoring is deliberately simple and deterministic:
///  - tool match  = every expected tool was called (or, when a case lists
///    alternatives, any one of the alternative tools)
///  - answer match = all required keywords appear in the final answer, and at
///    least one of the optional phrases does too (when the case lists any)
///  Keyword matching is normalised: case, spaces, underscores and commas are
///  ignored, so "in_production", "In Production" and "in production" all match.
/// </summary>
internal static class EvalRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<int> RunAsync()
    {
        if (!await Program.Client.IsHealthyAsync())
        {
            Console.Error.WriteLine($"Ollama is not reachable at {Program.Client.BaseUrl}.");
            return 1;
        }

        var casesPath = Path.Combine(RepoPathsHelper.RepoRoot(), "evals", "ops-assistant.cases.jsonl");
        if (!File.Exists(casesPath))
        {
            Console.Error.WriteLine($"Eval cases not found: {casesPath}");
            return 1;
        }

        var cases = File.ReadLines(casesPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<EvalCase>(line, JsonOpts)!)
            .ToList();

        Directory.CreateDirectory(Program.TraceDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var rawPath = Path.Combine(Program.TraceDir, $"eval-{stamp}.jsonl");

        Console.WriteLine($"Running {cases.Count} eval cases against {Program.Client.DefaultModel}...\n");

        var outcomes = new List<CaseOutcome>();
        foreach (var evalCase in cases)
        {
            var outcome = await RunCaseAsync(evalCase, rawPath);
            outcomes.Add(outcome);

            var verdict = outcome.Error is not null
                ? "ERROR"
                : outcome.ToolOk && outcome.AnswerOk ? "pass"
                    : $"tool:{(outcome.ToolOk ? "ok" : "NO")} answer:{(outcome.AnswerOk ? "ok" : "NO")}";
            Console.WriteLine(
                $"{evalCase.Id,-4} {verdict,-22} {outcome.Steps} steps, {outcome.Tokens} tok, {outcome.Ms,5} ms  " +
                $"{Truncate(evalCase.Question, 48)}");
        }

        var resultsPath = Path.Combine(RepoPathsHelper.RepoRoot(), "evals", "results", "ops-assistant-results.md");
        WriteResultsMarkdown(resultsPath, Program.Client.DefaultModel, outcomes);

        PrintAggregate(outcomes);
        Console.WriteLine($"\nResults written to {resultsPath}");
        Console.WriteLine($"Raw run records: {rawPath}");
        return 0;
    }

    private static async Task<CaseOutcome> RunCaseAsync(EvalCase evalCase, string rawPath)
    {
        var tracer = new Tracer(rawPath);
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            var result = await Program.AskAsync(evalCase.Question, tracer, cts.Token);
            sw.Stop();

            var called = result.Steps
                .SelectMany(s => s.ToolCalls)
                .Select(c => c.ToolName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var toolOk = evalCase.ExpectedTools.All(t =>
                           called.Contains(t, StringComparer.OrdinalIgnoreCase))
                       && (evalCase.ExpectedToolsAny is not { Length: > 0 } alternatives
                           || alternatives.Any(t => called.Contains(t, StringComparer.OrdinalIgnoreCase)));

            var finalNorm = Norm(result.FinalText);
            var missing = evalCase.ExpectKeywords.Where(k => !finalNorm.Contains(Norm(k))).ToList();
            var anyOk = evalCase.ExpectKeywordsAny.Length == 0 ||
                        evalCase.ExpectKeywordsAny.Any(p => finalNorm.Contains(Norm(p)));
            var answerOk = missing.Count == 0 && anyOk;

            var outcome = new CaseOutcome(
                evalCase, called, result.FinalText, toolOk, answerOk,
                result.StopReason, result.Steps.Count, result.TotalUsage.TotalTokens,
                sw.ElapsedMilliseconds, Error: null);

            AppendRaw(rawPath, outcome, missing);
            return outcome;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var outcome = new CaseOutcome(
                evalCase, [], $"<run failed: {ex.Message}>", ToolOk: false, AnswerOk: false,
                StopReason.MaxSteps, Steps: 0, Tokens: 0, sw.ElapsedMilliseconds,
                Error: ex.Message);
            AppendRaw(rawPath, outcome, missingKeywords: []);
            Console.WriteLine($"     ! {ex.GetType().Name}: {ex.Message}");
            return outcome;
        }
    }

    private static void AppendRaw(string rawPath, CaseOutcome o, IReadOnlyList<string> missingKeywords)
    {
        var line = JsonSerializer.Serialize(new
        {
            id = o.Case.Id,
            question = o.Case.Question,
            expectedTools = o.Case.ExpectedTools,
            calledTools = o.CalledTools,
            toolOk = o.ToolOk,
            answerOk = o.AnswerOk,
            missingKeywords,
            stopReason = o.StopReason.ToString(),
            steps = o.Steps,
            tokens = o.Tokens,
            ms = o.Ms,
            error = o.Error,
            finalText = o.FinalText,
        });
        File.AppendAllText(rawPath, line + Environment.NewLine);
    }

    private static void WriteResultsMarkdown(string path, string model, List<CaseOutcome> outcomes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var toolOk = outcomes.Count(o => o.ToolOk);
        var answerOk = outcomes.Count(o => o.AnswerOk);
        var bothOk = outcomes.Count(o => o.ToolOk && o.AnswerOk);
        var avgSteps = outcomes.Average(o => o.Steps);
        var avgTokens = outcomes.Average(o => o.Tokens);
        var totalTokens = outcomes.Sum(o => o.Tokens);

        var md = new StringBuilder();
        md.AppendLine("# Eval results — P1 Ops Assistant (pattern 1: tool use / ReAct)");
        md.AppendLine();
        md.AppendLine($"- Run date: {DateTime.Now:yyyy-MM-dd HH:mm} (local, live Ollama)");
        md.AppendLine($"- Model: `{model}`, temperature 0.2, max 8 steps / 40k tokens per question");
        md.AppendLine($"- Cases: {outcomes.Count} (evals/ops-assistant.cases.jsonl)");
        md.AppendLine("- Scoring: **tool match** = every expected tool was called (or any listed alternative); **answer match** =");
        md.AppendLine("  all required keywords present in the final answer (normalised: case, spaces, underscores and commas");
        md.AppendLine("  ignored) and at least one optional phrase when the case provides alternatives.");
        md.AppendLine();
        md.AppendLine("## Aggregate");
        md.AppendLine();
        md.AppendLine("| Metric | Value |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Tool match (all expected called) | {toolOk}/{outcomes.Count} ({Pct(toolOk, outcomes.Count)}) |");
        md.AppendLine($"| Answer match (keywords found) | {answerOk}/{outcomes.Count} ({Pct(answerOk, outcomes.Count)}) |");
        md.AppendLine($"| Full pass (both) | {bothOk}/{outcomes.Count} ({Pct(bothOk, outcomes.Count)}) |");
        md.AppendLine($"| Avg steps per question | {avgSteps:0.0} |");
        md.AppendLine($"| Avg tokens per question | {avgTokens:0} |");
        md.AppendLine($"| Total tokens (all {outcomes.Count} runs) | {totalTokens:N0} |");
        md.AppendLine();
        md.AppendLine("## Cases");
        md.AppendLine();
        md.AppendLine("| ID | Question | Called tools | Tool | Answer | Stop | Steps | Tokens |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var o in outcomes)
        {
            var called = o.CalledTools.Count == 0 ? "—" : string.Join(", ", o.CalledTools);
            var stop = o.Error is not null ? "error" : o.StopReason.ToString();
            md.AppendLine(
                $"| {o.Case.Id} | {Escape(o.Case.Question)} | {Escape(called)} " +
                $"| {(o.ToolOk ? "✓" : "✗")} | {(o.AnswerOk ? "✓" : "✗")} | {stop} | {o.Steps} | {o.Tokens} |");
        }

        md.AppendLine();
        md.AppendLine("## Failed cases (raw final answers)");
        md.AppendLine();
        var failures = outcomes.Where(o => !(o.ToolOk && o.AnswerOk)).ToList();
        if (failures.Count == 0)
        {
            md.AppendLine("All cases passed.");
        }
        else
        {
            foreach (var o in failures)
            {
                md.AppendLine($"### {o.Case.Id} — {Escape(o.Case.Question)}");
                md.AppendLine();
                md.AppendLine($"- Expected tools: `{string.Join("`, `", o.Case.ExpectedTools)}`");
                md.AppendLine($"- Called: `{(o.CalledTools.Count == 0 ? "none" : string.Join("`, `", o.CalledTools))}`");
                md.AppendLine($"- Answer: {Escape(Truncate(o.FinalText, 600))}");
                md.AppendLine();
            }
        }

        File.WriteAllText(path, md.ToString());
    }

    private static void PrintAggregate(List<CaseOutcome> outcomes)
    {
        var toolOk = outcomes.Count(o => o.ToolOk);
        var answerOk = outcomes.Count(o => o.AnswerOk);
        var bothOk = outcomes.Count(o => o.ToolOk && o.AnswerOk);
        Console.WriteLine($"\n--- {outcomes.Count} cases ---");
        Console.WriteLine($"tool match:   {toolOk}/{outcomes.Count} ({Pct(toolOk, outcomes.Count)})");
        Console.WriteLine($"answer match: {answerOk}/{outcomes.Count} ({Pct(answerOk, outcomes.Count)})");
        Console.WriteLine($"full pass:    {bothOk}/{outcomes.Count} ({Pct(bothOk, outcomes.Count)})");
        Console.WriteLine($"avg steps {outcomes.Average(o => o.Steps):0.0}, avg tokens {outcomes.Average(o => o.Tokens):0}, total tokens {outcomes.Sum(o => o.Tokens):N0}");
    }

    private static string Norm(string s) =>
        s.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace(",", "");

    private static string Pct(int n, int total) => total == 0 ? "-" : $"{100.0 * n / total:0}%";

    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}

/// <summary>Local helper so the eval runner does not depend on Core's RepoPaths being public API we reshape later.</summary>
internal static class RepoPathsHelper
{
    public static string RepoRoot() => Agentic.AgentCore.Data.RepoPaths.FindRepoRoot();
}
