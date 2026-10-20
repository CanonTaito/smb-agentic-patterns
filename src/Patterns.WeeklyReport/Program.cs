using System.Diagnostics;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tracing;
using Agentic.AgentCore.Tools;
using Agentic.Patterns.WeeklyReport;

internal static class Program
{
    private static readonly InklineDb Db = new();
    private static readonly ToolRegistry Tools = ReportTools.Build(Db);
    internal static readonly OllamaClient Client = new();

    internal const string DefaultRequest =
        "Produce the weekly operations report for the week ending 2026-10-18 covering jobs at risk, " +
        "cash owed, stock levels for blanks and ink, and recent customer feedback.";

    internal static string TraceDir => Path.Combine(RepoPaths.FindRepoRoot(), "runs", "weekly-report");

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                Weekly Report — agentic pattern 2: planning & task decomposition

                  dotnet run --project src/Patterns.WeeklyReport                 plan → approve gate → run the default report
                  dotnet run --project src/Patterns.WeeklyReport -- --auto       same, gate auto-approved (non-interactive)
                  dotnet run --project src/Patterns.WeeklyReport -- <request>    report request in your own words
                  dotnet run --project src/Patterns.WeeklyReport -- --eval       run the 10-case eval suite → evals/results/
                """);
            return 0;
        }

        if (args.Contains("--eval")) return await EvalRunner.RunAsync();

        if (!await Client.IsHealthyAsync())
        {
            Console.Error.WriteLine($"Ollama is not reachable at {Client.BaseUrl}. Start it with 'ollama serve'.");
            return 1;
        }

        var request = string.Join(" ", args.Where(a => !a.StartsWith("--")));
        if (string.IsNullOrWhiteSpace(request)) request = DefaultRequest;
        var autoApprove = args.Contains("--auto");

        Directory.CreateDirectory(TraceDir);
        var tracer = new Tracer(Path.Combine(TraceDir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"));

        Console.WriteLine($"Request: {request}\n");
        var result = await ReportPipeline.RunAsync(
            Client, Tools, request, autoApprove, tracer, Console.Out);

        switch (result.Outcome)
        {
            case PipelineOutcome.PlanInvalid:
                Console.Error.WriteLine("The model could not produce a valid plan: " + result.Failure);
                return 1;
            case PipelineOutcome.Rejected:
                Console.WriteLine("Plan rejected at the gate — nothing was executed." +
                                  (result.Failure is null ? "" : $" ({result.Failure})"));
                tracer.WriteTree();
                return 0;
        }

        Console.WriteLine("\n================ REPORT ================");
        Console.WriteLine(result.Report);
        Console.WriteLine("========================================");

        var failed = result.Executions.Count(e => !e.Ok);
        var verified = result.VerificationAfterRepair ?? result.Verification;
        Console.WriteLine(
            $"plan {result.Plan!.Steps.Count} steps | {result.Executions.Count} executed" +
            $"{(failed > 0 ? $" ({failed} FAILED)" : "")} | verify {(verified?.Ok == true ? "PASS" : "FAIL")}" +
            $"{(result.ReportRepaired ? " after repair" : "")} | {result.LlmCalls} llm calls " +
            $"| {result.Usage.TotalTokens} tokens | {result.Ms} ms");

        var reportPath = Path.Combine(TraceDir, $"report-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(reportPath, result.Report);
        Console.WriteLine($"saved: {reportPath}");
        tracer.WriteTree();
        return 0;
    }
}
