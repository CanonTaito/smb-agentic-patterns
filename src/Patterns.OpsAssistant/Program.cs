using System.Diagnostics;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tracing;
using Agentic.AgentCore.Tools;
using Agentic.Patterns.OpsAssistant;

internal static class Program
{
    private static readonly InklineDb Db = new();
    private static readonly ToolRegistry Tools = InklineTools.Build(Db);
    internal static readonly OllamaClient Client = new();

    internal const string SystemPrompt = """
        You are the operations assistant for Inkline Print Co., a small screen-printing shop in Queensland.
        Staff ask you about jobs, customers, stock, ink and artwork proofs. You answer quickly and factually.

        Rules:
        - Every fact must come from a tool call in this conversation. Never guess, never use outside knowledge.
        - The dataset is a frozen snapshot; call get_today for the business date rather than assuming one.
        - Job numbers are strings like "1002". Customer ids look like "C05".
        - If a tool returns an error or no matches, say so plainly and mention what you looked up.
        - Keep answers under 80 words. Plain text only, no markdown.
        """;

    internal static string TraceDir => Path.Combine(RepoPaths.FindRepoRoot(), "runs", "ops-assistant");

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                Inkline Ops Assistant — agentic pattern 1: tool use (ReAct)

                  dotnet run --project src/Patterns.OpsAssistant              interactive chat
                  dotnet run --project src/Patterns.OpsAssistant -- --trace    chat, print a trace tree after each answer
                  dotnet run --project src/Patterns.OpsAssistant -- --demo     three scripted questions with trace trees
                  dotnet run --project src/Patterns.OpsAssistant -- --eval     run the 20-case eval suite, write evals/results/
                """);
            return 0;
        }

        if (args.Contains("--eval")) return await EvalRunner.RunAsync();
        if (args.Contains("--demo")) return await RunDemoAsync();

        return await RunChatAsync(traceTrees: args.Contains("--trace"));
    }

    internal static async Task<AgentRunResult> AskAsync(string question, Tracer tracer, CancellationToken ct = default)
    {
        var loop = new AgentLoop(Client, Tools, tracer);
        return await loop.RunAsync(new AgentOptions
        {
            SystemPrompt = SystemPrompt,
            UserTask = question,
            MaxSteps = 8,
            MaxTotalTokens = 40_000,
            Temperature = 0.2,
        }, ct);
    }

    private static async Task<int> RunChatAsync(bool traceTrees)
    {
        if (!await GuardOllamaAsync()) return 1;

        Directory.CreateDirectory(TraceDir);
        var sessionFile = Path.Combine(TraceDir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");

        Console.WriteLine("Inkline Ops Assistant — pattern 1: tool use (ReAct).");
        Console.WriteLine("Ask about jobs, customers, stock, ink or proofs. Type 'exit' to quit.");

        while (true)
        {
            Console.Write("\nyou> ");
            var question = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(question)) continue;
            if (question is "exit" or "quit") break;

            var tracer = new Tracer(sessionFile);
            Console.Write("... ");
            var sw = Stopwatch.StartNew();
            var result = await AskAsync(question, tracer);
            Console.WriteLine($"assistant> {result.FinalText}");

            var tools = result.Steps.SelectMany(s => s.ToolCalls).Select(c => c.ToolName).Distinct().ToList();
            Console.WriteLine(
                $"  [{result.Steps.Count} steps | tools: {(tools.Count == 0 ? "none" : string.Join(", ", tools))} " +
                $"| {result.TotalUsage.TotalTokens} tokens | {result.StopReason} | {sw.ElapsedMilliseconds} ms]");

            if (traceTrees) tracer.WriteTree();
        }

        return 0;
    }

    private static async Task<int> RunDemoAsync()
    {
        if (!await GuardOllamaAsync()) return 1;

        Directory.CreateDirectory(TraceDir);
        var questions = new[]
        {
            "What's the status of job 1002, and is it overdue?",
            "Could we fulfil a 60-shirt order in blank tee M from current stock?",
            "What's happening with the artwork proof on job 1007?",
        };

        foreach (var question in questions)
        {
            Console.WriteLine($"\nyou> {question}");
            var tracer = new Tracer(Path.Combine(TraceDir, $"demo-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"));
            var sw = Stopwatch.StartNew();
            var result = await AskAsync(question, tracer);
            Console.WriteLine($"assistant> {result.FinalText}");
            Console.WriteLine(
                $"  [{result.Steps.Count} steps | {result.TotalUsage.TotalTokens} tokens " +
                $"| {result.StopReason} | {sw.ElapsedMilliseconds} ms]");
            tracer.WriteTree();
            Console.WriteLine();
        }

        return 0;
    }

    private static async Task<bool> GuardOllamaAsync()
    {
        if (await Client.IsHealthyAsync()) return true;
        Console.Error.WriteLine($"Ollama is not reachable at {Client.BaseUrl}. Start it with 'ollama serve'.");
        return false;
    }
}
