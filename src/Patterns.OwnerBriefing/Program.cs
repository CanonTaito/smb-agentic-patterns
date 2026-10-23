using System.Diagnostics;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tracing;
using Agentic.Patterns.OwnerBriefing;

internal static class Program
{
    internal static readonly OllamaClient Client = new();

    internal static string TraceDir => Path.Combine(RepoPaths.FindRepoRoot(), "runs", "owner-briefing");

    private const string DefaultFocus =
        "The usual Sunday briefing: revenue and orders, anything at risk, what customers are saying, " +
        "and what to do this week.";

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                Owner Briefing Pack - agentic pattern 5: supervisor-worker fan-out

                  dotnet run --project src/Patterns.OwnerBriefing                         demo: supervisor plan -> analyst + researcher (parallel) -> writer -> gate
                  dotnet run --project src/Patterns.OwnerBriefing -- --focus "cash"      brief the owner on a specific focus
                  dotnet run --project src/Patterns.OwnerBriefing -- --single            single-agent control arm (same tools, one loop)
                  dotnet run --project src/Patterns.OwnerBriefing -- --auto              non-interactive (gate auto-approved)
                  dotnet run --project src/Patterns.OwnerBriefing -- --eval              5 focuses x multi vs single, rubric + cost comparison
                """);
            return 0;
        }

        if (args.Contains("--eval")) return await EvalRunner.RunAsync();

        if (args.Contains("--facts"))
        {
            var db = new InklineDb();
            EvalRunner.PrintGroundTruth(
                InklineData.ComputeKpis(InklineData.LoadSales()),
                InklineData.ComputeReviewStats(InklineData.LoadReviews()),
                db.ListAtRiskJobs(DataSeeder.SeedDate));
            return 0;
        }

        if (!await Client.IsHealthyAsync())
        {
            Console.Error.WriteLine($"Ollama is not reachable at {Client.BaseUrl}. Start it with 'ollama serve'.");
            return 1;
        }

        var focus = GetFlag(args, "--focus", out var focusIdx) && focusIdx + 1 < args.Length
            ? args[focusIdx + 1]
            : DefaultFocus;
        var single = args.Contains("--single");
        var autoApprove = args.Contains("--auto");

        Directory.CreateDirectory(TraceDir);
        var tracer = new Tracer(Path.Combine(TraceDir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"));

        Console.WriteLine($"owner focus: {focus}");
        Console.WriteLine($"mode: {(single ? "single agent (control)" : "supervisor-worker fan-out")}");
        Console.WriteLine();

        string briefing;
        GateResult gate;
        int llmCalls;
        TokenUsage usage;
        long ms;

        if (single)
        {
            var sw = Stopwatch.StartNew();
            var run = await BriefingPipeline.RunSingleAsync(Client, focus, tracer);
            sw.Stop();
            briefing = run.Output;
            llmCalls = run.Steps;
            usage = run.Usage;
            ms = sw.ElapsedMilliseconds;

            using (var gateSpan = tracer.StartSpan("gate: send briefing", SpanKind.Gate))
            {
                gate = await HumanGate.RequestApprovalAsync(
                    "Send the Sunday-night owner briefing to the owner's inbox",
                    briefing, autoApprove, Console.Out);
                gateSpan.SetTag("decision", gate.Decision.ToString());
            }

            Console.WriteLine($"single-agent: {llmCalls} llm calls · {usage.TotalTokens:N0} tokens · {ms} ms");
        }
        else
        {
            var result = await BriefingPipeline.RunAsync(
                Client, focus, tracer, autoApprove, Console.Out, Console.Out);
            briefing = result.Briefing;
            gate = result.Gate;
            llmCalls = result.LlmCalls;
            usage = result.Usage;
            ms = result.Ms;

            if (args.Contains("--show-workers"))
            {
                Console.WriteLine();
                Console.WriteLine("---------------- ANALYST ----------------");
                Console.WriteLine(result.Analyst.Output);
                Console.WriteLine("---------------- RESEARCHER -------------");
                Console.WriteLine(result.Researcher.Output);
            }
        }

        Console.WriteLine();
        Console.WriteLine("================ OWNER BRIEFING ================");
        Console.WriteLine(briefing);
        Console.WriteLine("================================================");
        Console.WriteLine(
            $"gate: {gate.Decision}" + (gate.Note is null ? "" : $" ({gate.Note})") + " | " +
            $"{llmCalls} llm calls | {usage.TotalTokens:N0} tokens | {ms:N0} ms");

        var briefingPath = Path.Combine(TraceDir, $"briefing-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(briefingPath, briefing);
        Console.WriteLine($"briefing saved to {briefingPath}");

        tracer.WriteTree();

        var topSpans = tracer.Spans
            .OrderByDescending(s => s.DurationMs)
            .Take(3)
            .ToList();
        Console.WriteLine();
        Console.WriteLine("MOST EXPENSIVE SPANS");
        foreach (var span in topSpans)
            Console.WriteLine($"  {span.DurationMs,6} ms  {span.Kind,-7} {span.Name}");

        return gate.ShouldProceed ? 0 : 1;
    }

    private static bool GetFlag(string[] args, string flag, out int index)
    {
        index = Array.IndexOf(args, flag);
        return index >= 0;
    }
}
