using System.Text.Json;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tracing;
using Agentic.Patterns.QuoteGenerator;

internal static class Program
{
    internal static readonly OllamaClient Client = new();

    internal static string TraceDir => Path.Combine(RepoPaths.FindRepoRoot(), "runs", "quote-generator");

    // Deliberately over the $2,000 owner-approval threshold so the demo shows the gate.
    private static readonly QuoteJob DemoJob = new()
    {
        Customer = "Westlake Crossfit",
        Garment = "hoodie",
        Quantity = 200,
        Locations = [new PrintLocation("Front", 3), new PrintLocation("Back", 1)],
        Rush = true,
        Notes = "new member intake, needed before the winter campaign",
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                Quote Generator — agentic pattern 3: reflection & self-improvement

                  dotnet run --project src/Patterns.QuoteGenerator                   draft → rule critic → revise → owner gate (demo job)
                  dotnet run --project src/Patterns.QuoteGenerator -- --no-reflect   single-shot draft (no reflection loop) for comparison
                  dotnet run --project src/Patterns.QuoteGenerator -- --spec <json>  your own job, e.g. '{"customer":"...","garment":"tee","quantity":50,"locations":[{"location":"Front","colors":2}],"rush":true}'
                  dotnet run --project src/Patterns.QuoteGenerator -- --auto         gate auto-approved (non-interactive)
                  dotnet run --project src/Patterns.QuoteGenerator -- --eval        same-lineage A/B: 20 specs, first draft vs reflected final
                """);
            return 0;
        }

        if (args.Contains("--eval")) return await EvalRunner.RunAsync();

        if (!await Client.IsHealthyAsync())
        {
            Console.Error.WriteLine($"Ollama is not reachable at {Client.BaseUrl}. Start it with 'ollama serve'.");
            return 1;
        }

        var job = DemoJob;
        var specIdx = Array.IndexOf(args, "--spec");
        if (specIdx >= 0 && specIdx + 1 < args.Length)
        {
            try
            {
                job = JsonSerializer.Deserialize<QuoteJob>(args[specIdx + 1],
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new JsonException("deserialised to null");
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Could not read --spec JSON: {ex.Message}");
                return 1;
            }
        }

        var rounds = args.Contains("--no-reflect") ? 0 : 3;
        var autoApprove = args.Contains("--auto");

        Directory.CreateDirectory(TraceDir);
        var tracer = new Tracer(Path.Combine(TraceDir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"));

        Console.WriteLine($"Spec: {job.SpecText()}");
        if (rounds == 0) Console.WriteLine("Reflection: OFF (single draft, critic scores only)");

        var result = await QuotePipeline.RunAsync(Client, job, rounds, autoApprove, tracer, Console.Out);

        Console.WriteLine("\n================ QUOTE EMAIL ================");
        Console.WriteLine(result.FinalText);
        Console.WriteLine("=============================================");

        Console.WriteLine(result.Passed
            ? $"critique: PASS" + (result.Revisions > 0 ? $" after {result.Revisions} revision(s)" : "") +
              $" | ${result.Breakdown.Total:N2} | {result.LlmCalls} llm calls | {result.Usage.TotalTokens} tokens | {result.Ms} ms"
            : $"critique: FAIL — {result.Critique.Issues.Count} issue(s) after {result.Revisions} revision(s)" +
              $" | ${result.Breakdown.Total:N2} | {result.LlmCalls} llm calls | {result.Usage.TotalTokens} tokens | {result.Ms} ms");

        foreach (var issue in result.Critique.Issues)
            Console.WriteLine($"  [{issue.Category}] {issue.Message}");

        if (result.RequiresOwnerApproval)
            Console.WriteLine(result.Gate?.ShouldProceed == true
                ? $"gate: owner approval required (${result.Breakdown.Total:N2} > ${QuotePipeline.OwnerApprovalThreshold:N0}) — {result.Gate.Decision.ToString().ToLowerInvariant()}"
                : "gate: owner approval required — REJECTED, do not send");

        tracer.WriteTree();
        return result.Passed && result.Gate?.ShouldProceed != false ? 0 : 1;
    }
}
