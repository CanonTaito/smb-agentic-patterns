using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tracing;
using Agentic.Patterns.InboxTriage;

internal static class Program
{
    internal static readonly OllamaClient Client = new();

    internal static string TraceDir => Path.Combine(RepoPaths.FindRepoRoot(), "runs", "inbox-triage");

    // Demo default: a rush-quote email, so one run shows routing, the
    // price_quote specialist tool, and the human gate in a single pass.
    private const string DefaultMessageId = "M014";

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                Inbox Triage — agentic pattern 4: multi-agent routing & hand-off

                  dotnet run --project src/Patterns.InboxTriage                          demo: triage M014 (rush quote) → specialist → gate
                  dotnet run --project src/Patterns.InboxTriage -- --msg M003            triage any message from data/inbox.jsonl
                  dotnet run --project src/Patterns.InboxTriage -- --msg M021 --auto     non-interactive (gate auto-approved)
                  dotnet run --project src/Patterns.InboxTriage -- --router embed        route with embeddings instead of the LLM
                  dotnet run --project src/Patterns.InboxTriage -- --eval                30-message eval: routing vs embeddings, handled rate
                """);
            return 0;
        }

        if (args.Contains("--eval")) return await EvalRunner.RunAsync();

        if (!await Client.IsHealthyAsync())
        {
            Console.Error.WriteLine($"Ollama is not reachable at {Client.BaseUrl}. Start it with 'ollama serve'.");
            return 1;
        }

        var messageId = DefaultMessageId;
        var msgIdx = Array.IndexOf(args, "--msg");
        if (msgIdx >= 0 && msgIdx + 1 < args.Length) messageId = args[msgIdx + 1];

        var message = LoadMessages().FirstOrDefault(m =>
            string.Equals(m.Id, messageId, StringComparison.OrdinalIgnoreCase));
        if (message is null)
        {
            var ids = string.Join(", ", LoadMessages().Select(m => m.Id));
            Console.Error.WriteLine($"No message '{messageId}' in data/inbox.jsonl. Known ids: {ids}");
            return 1;
        }

        var useEmbeddingRouter = GetFlag(args, "--router", out var routerIdx) &&
                                 routerIdx + 1 < args.Length &&
                                 string.Equals(args[routerIdx + 1], "embed", StringComparison.OrdinalIgnoreCase);
        var autoApprove = args.Contains("--auto");

        Directory.CreateDirectory(TraceDir);
        var tracer = new Tracer(Path.Combine(TraceDir, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl"));

        Console.WriteLine($"[{message.Id}] {message.From}");
        Console.WriteLine($"  subject: {message.Subject}");

        EmbeddingRouter? embedRouter = null;
        if (useEmbeddingRouter)
        {
            Console.Write("  warming embedding prototypes (15 embeds)... ");
            var warmStart = Environment.TickCount64;
            embedRouter = new EmbeddingRouter(Client);
            await embedRouter.WarmUpAsync();
            Console.WriteLine($"ready in {Environment.TickCount64 - warmStart} ms");
        }

        Console.WriteLine();
        var result = await TriagePipeline.RunAsync(
            Client, message, tracer,
            useEmbeddingRouter: useEmbeddingRouter,
            embeddingRouter: embedRouter,
            autoApprove: autoApprove,
            log: Console.Out,
            gateOut: Console.Out);

        Console.WriteLine($"\n================ REPLY ({result.Mode}) ================");
        Console.WriteLine(result.Reply);
        Console.WriteLine("========================================================");

        if (result.QuoteTotal is not null)
            Console.WriteLine($"priced total: ${result.QuoteTotal:N2} (owner threshold ${TriagePipeline.OwnerApprovalThreshold:N0})");
        Console.WriteLine(
            $"route: {result.Route.Category} ({result.Route.Confidence:0.00}) via {result.RouterKind} | " +
            $"gate: {result.Gate.Decision}" +
            (result.Gate.Note is null ? "" : $" ({result.Gate.Note})") + " | " +
            $"{result.LlmCalls} llm calls | {result.Usage.TotalTokens} tokens | {result.Ms} ms");

        tracer.WriteTree();
        return result.Gate.ShouldProceed ? 0 : 1;
    }

    private static List<InboxMessage> LoadMessages()
    {
        var path = Path.Combine(RepoPaths.FindRepoRoot(), "data", "inbox.jsonl");
        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return File.ReadLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => System.Text.Json.JsonSerializer.Deserialize<InboxMessage>(l, opts)!)
            .ToList();
    }

    private static bool GetFlag(string[] args, string flag, out int index)
    {
        index = Array.IndexOf(args, flag);
        return index >= 0;
    }
}
