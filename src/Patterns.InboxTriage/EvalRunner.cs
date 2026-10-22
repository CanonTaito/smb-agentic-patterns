using System.Text;
using System.Text.Json;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tracing;

namespace Agentic.Patterns.InboxTriage;

/// <summary>
/// 30-message routing eval: every message runs ONCE through the full pipeline
/// (LLM router → specialist or clarifying reply → gate, auto-approved) and is
/// scored against data/inbox.jsonl's expected_category / ambiguous labels.
/// The same 30 are ALSO classified by the embedding router — same labels, no
/// LLM — for a head-to-head routing comparison. Reports routing accuracy,
/// handled rate, clarify precision/recall, gate decisions and token cost.
/// </summary>
internal static class EvalRunner
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record MsgScore(
        string Id,
        string Subject,
        string Expected,
        bool Ambiguous,
        string LlmRoute,
        double LlmConf,
        bool RouteOk,
        string? EmbedRoute,
        double? EmbedConf,
        bool? EmbedOk,
        string Mode,
        bool Clarified,
        bool Handled,
        string Gate,
        decimal? QuoteTotal,
        int LlmCalls,
        int Tokens,
        long Ms,
        IReadOnlyList<string> Tools,
        string Reply,
        string? Error)
    {
        public bool EmbedOkValue => EmbedOk == true;

        /// <summary>quote_request reply that shows a $ figure without price_quote having run.</summary>
        public bool UngroundedPrice =>
            Expected == "quote_request" && Reply.Contains('$') && !Tools.Contains("price_quote");

        public bool PricedByTool => Expected == "quote_request" && Tools.Contains("price_quote");
    }

    public static async Task<int> RunAsync()
    {
        if (!await Program.Client.IsHealthyAsync())
        {
            Console.Error.WriteLine($"Ollama is not reachable at {Program.Client.BaseUrl}.");
            return 1;
        }

        var inboxPath = Path.Combine(RepoPaths.FindRepoRoot(), "data", "inbox.jsonl");
        if (!File.Exists(inboxPath))
        {
            Console.Error.WriteLine($"Inbox data not found: {inboxPath}");
            return 1;
        }

        var messages = File.ReadLines(inboxPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<InboxMessage>(l, JsonOpts)!)
            .ToList();

        Directory.CreateDirectory(Program.TraceDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var rawPath = Path.Combine(Program.TraceDir, $"eval-{stamp}.jsonl");

        // --- embedding router: warm prototypes, then classify all 30 (no LLM) ---
        var embedRouter = new EmbeddingRouter(Program.Client);
        var embedAvailable = true;
        try
        {
            Console.Write("Warming embedding prototypes (15 embeds)... ");
            var warmStart = Environment.TickCount64;
            await embedRouter.WarmUpAsync();
            Console.WriteLine($"ready in {Environment.TickCount64 - warmStart} ms");
        }
        catch (Exception ex)
        {
            embedAvailable = false;
            Console.WriteLine($"unavailable ({ex.GetType().Name}: {ex.Message}) — embedding comparison skipped.");
        }

        Console.WriteLine($"Running {messages.Count} messages through the pipeline " +
                          $"(router {Program.Client.DefaultModel}, auto-approved gate)...\n");

        var results = new List<MsgScore>();
        foreach (var message in messages)
        {
            var score = await RunMessageAsync(message, embedAvailable ? embedRouter : null, rawPath);
            results.Add(score);
            AppendRaw(rawPath, score);

            Console.WriteLine(
                $"{score.Id} {Verdict(score)} expected:{score.Expected,-15} " +
                $"llm:{score.LlmRoute}{Mark(score.RouteOk)} " +
                (score.EmbedRoute is null
                    ? "embed:n/a            "
                    : $"embed:{score.EmbedRoute}{Mark(score.EmbedOkValue)} ") +
                $"{score.Mode,-9} gate:{score.Gate,-9} " +
                $"{(score.Error is null ? "" : "ERR " + Truncate(score.Error, 30))}" +
                $"{score.Tokens,6} tok {score.Ms,6} ms");
        }

        var resultsPath = Path.Combine(RepoPaths.FindRepoRoot(), "evals", "results", "inbox-triage-results.md");
        WriteResultsMarkdown(resultsPath, results, embedAvailable);
        PrintAggregate(results, embedAvailable);
        Console.WriteLine($"\nResults written to {resultsPath}");
        Console.WriteLine($"Raw run records: {rawPath}");
        return 0;
    }

    private static async Task<MsgScore> RunMessageAsync(InboxMessage message, EmbeddingRouter? embedRouter, string rawPath)
    {
        // Embedding classification first — outside the pipeline, no LLM involved.
        string? embedRoute = null;
        double? embedConf = null;
        bool? embedOk = null;
        if (embedRouter is not null)
        {
            try
            {
                using var embedCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var e = await embedRouter.RouteAsync(message, embedCts.Token);
                embedRoute = e.Category;
                embedConf = e.Confidence;
                embedOk = e.Category == message.ExpectedCategory;
            }
            catch (Exception ex)
            {
                embedOk = null;
                embedRoute = $"error:{ex.GetType().Name}";
            }
        }

        var tracer = new Tracer(rawPath);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            var result = await TriagePipeline.RunAsync(
                Program.Client, message, tracer,
                useEmbeddingRouter: false,
                autoApprove: true,
                log: null,
                gateOut: TextWriter.Null,
                ct: cts.Token);

            var handled = message.Ambiguous
                ? result.Mode == "clarified"
                : result.Route.Category == message.ExpectedCategory
                  && result.Mode == "routed"
                  && !string.IsNullOrWhiteSpace(result.Reply)
                  && result.Gate.Decision != GateDecision.Rejected;

            return new MsgScore(
                message.Id, message.Subject, message.ExpectedCategory, message.Ambiguous,
                result.Route.Category, result.Route.Confidence,
                result.Route.Category == message.ExpectedCategory,
                embedRoute, embedConf, embedOk,
                result.Mode, result.Mode == "clarified", handled,
                result.Gate.Decision.ToString(), result.QuoteTotal,
                result.LlmCalls, result.Usage.TotalTokens, result.Ms,
                result.SpecialistTools, result.Reply,
                Error: null);
        }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"     ! {message.Id}: {error}");
            return new MsgScore(
                message.Id, message.Subject, message.ExpectedCategory, message.Ambiguous,
                "error", 0, false, embedRoute, embedConf, embedOk,
                "error", false, false, "none", null, 0, 0, 0,
                [], "", error);
        }
    }

    private static void AppendRaw(string rawPath, MsgScore s) =>
        File.AppendAllText(rawPath, JsonSerializer.Serialize(new
        {
            id = s.Id,
            expected = s.Expected,
            ambiguous = s.Ambiguous,
            llm_route = new { category = s.LlmRoute, confidence = s.LlmConf, ok = s.RouteOk },
            embed_route = s.EmbedRoute is null ? null : new { category = s.EmbedRoute, confidence = s.EmbedConf, ok = s.EmbedOk },
            mode = s.Mode,
            handled = s.Handled,
            gate = s.Gate,
            quote_total = s.QuoteTotal,
            llm_calls = s.LlmCalls,
            tokens = s.Tokens,
            ms = s.Ms,
            tools = s.Tools,
            reply = s.Reply,
            ungrounded_price = s.UngroundedPrice,
            error = s.Error,
        }) + Environment.NewLine);

    private static void WriteResultsMarkdown(string path, List<MsgScore> results, bool embedAvailable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var n = results.Count;
        var ambiguous = results.Where(r => r.Ambiguous).ToList();
        var clear = results.Where(r => !r.Ambiguous).ToList();
        var embeddable = results.Where(r => r.EmbedRoute is not null && !r.EmbedRoute.StartsWith("error")).ToList();

        var llmOk = results.Count(r => r.RouteOk);
        var embedOk = embeddable.Count(r => r.EmbedOkValue);
        var handled = results.Count(r => r.Handled);
        var clarifiedAmbiguous = ambiguous.Count(r => r.Clarified);
        var falseClarify = clear.Count(r => r.Clarified);

        var md = new StringBuilder();
        md.AppendLine("# Eval results — P4 Inbox Triage (pattern 4: multi-agent routing & hand-off)");
        md.AppendLine();
        md.AppendLine($"- Run date: {DateTime.Now:yyyy-MM-dd HH:mm} (local, live Ollama)");
        md.AppendLine($"- Router model: `{Program.Client.DefaultModel}`; embedding router: `nomic-embed-text` (cosine vs 15 hand-written prototypes)");
        md.AppendLine("- Method: each of the 30 messages runs ONCE through the full pipeline (LLM router → specialist with a");
        md.AppendLine("  category-scoped tool belt or a clarifying reply → human gate, auto-approved) and is scored against the");
        md.AppendLine("  inbox labels. The same 30 are also classified by the embedding router — same labels, no LLM — for a");
        md.AppendLine("  head-to-head routing comparison.");
        md.AppendLine($"- Cases: {n} (data/inbox.jsonl), of which {ambiguous.Count} are labelled ambiguous (mixed or vague on purpose).");
        md.AppendLine("- **Handled** = routed to the right department with a non-empty gated reply (clear messages), or a clarifying");
        md.AppendLine("  reply was sent (ambiguous messages). Wrong department = not handled, even if the reply sounds nice.");
        md.AppendLine("- Gate: every reply passes the human gate; in the eval it is auto-approved, except quotes at/over the");
        md.AppendLine($"  ${TriagePipeline.OwnerApprovalThreshold:N0} owner-approval threshold, which are escalated (held) and still count as handled.");
        md.AppendLine();
        md.AppendLine("## Aggregate");
        md.AppendLine();
        md.AppendLine("| Metric | Result |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| LLM router accuracy | **{llmOk}/{n} ({Pct(llmOk, n)})** |");
        if (embedAvailable)
            md.AppendLine($"| Embedding router accuracy | **{embedOk}/{embeddable.Count} ({Pct(embedOk, embeddable.Count)})** |");
        md.AppendLine($"| Handled (right dept or clarifying) | **{handled}/{n} ({Pct(handled, n)})** |");
        md.AppendLine($"| Ambiguous → clarifying reply | {clarifiedAmbiguous}/{ambiguous.Count} |");
        md.AppendLine($"| Clear → wrongly clarified (over-ask) | {falseClarify}/{clear.Count} |");
        md.AppendLine($"| Replies gated | {n}/{n} (auto-approved {results.Count(r => r.Gate == "Approved")}, escalated {results.Count(r => r.Gate == "Escalated")}, rejected {results.Count(r => r.Gate == "Rejected")}) |");
        md.AppendLine($"| Avg LLM calls per message | {results.Average(r => (double)r.LlmCalls):0.0} |");
        md.AppendLine($"| Avg tokens per message | {results.Average(r => r.Tokens):N0} |");
        md.AppendLine($"| Total tokens ({n} messages) | {results.Sum(r => r.Tokens):N0} |");

        var quoteMsgs = results.Where(r => r.Expected == "quote_request").ToList();
        var dollarReplies = quoteMsgs.Where(r => r.Reply.Contains('$')).ToList();
        md.AppendLine($"| quote messages that called price_quote | {quoteMsgs.Count(r => r.PricedByTool)}/{quoteMsgs.Count} |");
        if (dollarReplies.Count > 0)
            md.AppendLine($"| $ figures in quote replies traced to price_quote | {dollarReplies.Count(r => r.Tools.Contains("price_quote"))}/{dollarReplies.Count} |");
        if (embedAvailable && embeddable.Count > 0)
            md.AppendLine($"| embedding scores clearing the 0.7 clarify threshold | {embeddable.Count(r => r.EmbedConf >= TriageRouter.MinConfidence)}/{embeddable.Count} |");
        md.AppendLine();

        md.AppendLine("## LLM router by expected category");
        md.AppendLine();
        md.AppendLine("| Expected category | n | Correct |");
        md.AppendLine("|---|---|---|");
        foreach (var group in results.GroupBy(r => r.Expected).OrderBy(g => g.Key))
            md.AppendLine($"| {group.Key} | {group.Count()} | {group.Count(g => g.RouteOk)} |");
        md.AppendLine();

        if (embedAvailable && embeddable.Count > 0)
        {
            md.AppendLine("## Embedding vs LLM router (same 30 labels)");
            md.AppendLine();
            md.AppendLine("| Expected | LLM correct | Embedding correct |");
            md.AppendLine("|---|---|---|");
            foreach (var group in results.Where(r => r.EmbedRoute is not null).GroupBy(r => r.Expected).OrderBy(g => g.Key))
                md.AppendLine($"| {group.Key} | {group.Count(g => g.RouteOk)}/{group.Count()} | {group.Count(g => g.EmbedOkValue)}/{group.Count()} |");
            md.AppendLine();

            var disagreements = results.Where(r => r.EmbedRoute is not null && r.RouteOk && !r.EmbedOkValue).ToList();
            md.AppendLine("### Where the embedding router is wrong but the LLM router is right");
            md.AppendLine();
            if (disagreements.Count == 0)
                md.AppendLine("None.");
            foreach (var r in disagreements)
                md.AppendLine($"- **{r.Id}** \"{Escape(r.Subject)}\" (expected {r.Expected}): embed chose `{r.EmbedRoute}` (cosine {r.EmbedConf:0.000})");
            md.AppendLine();
        }

        md.AppendLine("## Per-message");
        md.AppendLine();
        md.AppendLine("| ID | Expected | Amb. | LLM route | Embed route | Mode | Handled | Gate | Tokens |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
        {
            md.AppendLine(
                $"| {r.Id} | {r.Expected} | {(r.Ambiguous ? "✓" : "")} " +
                $"| {r.LlmRoute}{Mark(r.RouteOk)} | {(r.EmbedRoute ?? "—")}{(r.EmbedRoute is null ? "" : Mark(r.EmbedOkValue))} " +
                $"| {r.Mode} | {(r.Handled ? "✓" : "✗")} | {r.Gate} | {r.Tokens} |");
        }

        var failing = results.Where(r => !r.Handled).ToList();
        md.AppendLine();
        md.AppendLine("## Not handled");
        md.AppendLine();
        if (failing.Count == 0)
            md.AppendLine("Nothing — every message reached the right department (or got a clarifying reply when ambiguous).");
        foreach (var r in failing)
        {
            md.AppendLine($"### {r.Id} — expected {r.Expected}{(r.Ambiguous ? " (ambiguous)" : "")}");
            md.AppendLine();
            md.AppendLine($"- LLM router said `{r.LlmRoute}` (conf {r.LlmConf:0.00}) → mode {r.Mode}, gate {r.Gate}");
            if (r.Error is not null) md.AppendLine($"- Error: {Escape(r.Error)}");
            md.AppendLine();
        }

        var errors = results.Where(r => r.Error is not null).ToList();
        if (errors.Count > 0)
        {
            md.AppendLine("## Errors");
            md.AppendLine();
            foreach (var r in errors)
                md.AppendLine($"- {r.Id}: {Escape(r.Error!)}");
            md.AppendLine();
        }

        var ungrounded = results.Where(r => r.UngroundedPrice).ToList();
        md.AppendLine("## Replies with a $ figure that never called price_quote");
        md.AppendLine();
        if (ungrounded.Count == 0)
            md.AppendLine("None — every dollar figure in a quote reply came out of price_quote.");
        foreach (var r in ungrounded)
            md.AppendLine($"- **{r.Id}** \"{Escape(r.Subject)}\": reply contains a price but the specialist's tools were [{string.Join(", ", r.Tools)}]");
        md.AppendLine();

        File.WriteAllText(path, md.ToString());
    }

    private static void PrintAggregate(List<MsgScore> results, bool embedAvailable)
    {
        var n = results.Count;
        var ambiguous = results.Where(r => r.Ambiguous).ToList();
        var clear = results.Where(r => !r.Ambiguous).ToList();
        var embeddable = results.Where(r => r.EmbedRoute is not null && !r.EmbedRoute.StartsWith("error")).ToList();

        Console.WriteLine($"\n--- {n} messages ---");
        Console.WriteLine($"llm router:  {results.Count(r => r.RouteOk)}/{n} ({Pct(results.Count(r => r.RouteOk), n)}) correct");
        if (embedAvailable && embeddable.Count > 0)
            Console.WriteLine($"embedding:   {embeddable.Count(r => r.EmbedOkValue)}/{embeddable.Count} ({Pct(embeddable.Count(r => r.EmbedOkValue), embeddable.Count)}) correct" +
                              $", {embeddable.Count(r => r.EmbedConf >= TriageRouter.MinConfidence)} clear the 0.7 threshold");
        Console.WriteLine($"handled:     {results.Count(r => r.Handled)}/{n} ({Pct(results.Count(r => r.Handled), n)})" +
                          $"  — ambiguous clarified {ambiguous.Count(r => r.Clarified)}/{ambiguous.Count}," +
                          $" clear over-clarified {clear.Count(r => r.Clarified)}/{clear.Count}");
        Console.WriteLine($"gates:       approved {results.Count(r => r.Gate == "Approved")}" +
                          $"  escalated {results.Count(r => r.Gate == "Escalated")}" +
                          $"  rejected {results.Count(r => r.Gate == "Rejected")}");
        Console.WriteLine($"tokens:      total {results.Sum(r => r.Tokens):N0}, avg {results.Average(r => (double)r.Tokens):N0}/message" +
                          $" ({results.Average(r => (double)r.LlmCalls):0.0} llm calls)");

        var quoteMsgs = results.Where(r => r.Expected == "quote_request").ToList();
        var dollarReplies = quoteMsgs.Where(r => r.Reply.Contains('$')).ToList();
        Console.WriteLine($"pricing:     price_quote called on {quoteMsgs.Count(r => r.PricedByTool)}/{quoteMsgs.Count} quote messages" +
                          (dollarReplies.Count > 0
                              ? $"; $ figures grounded {dollarReplies.Count(r => r.Tools.Contains("price_quote"))}/{dollarReplies.Count}"
                              : ""));

        var failing = results.Where(r => !r.Handled).ToList();
        if (failing.Count > 0)
            Console.WriteLine("not handled: " + string.Join(", ", failing.Select(r => $"{r.Id}({r.LlmRoute})")));
        var ungrounded = results.Where(r => r.UngroundedPrice).ToList();
        if (ungrounded.Count > 0)
            Console.WriteLine("ungrounded $: " + string.Join(", ", ungrounded.Select(r => r.Id)));
    }

    private static string Verdict(MsgScore s) =>
        s.Error is not null ? $"ERR  " : s.Handled ? "OK   " : "MISS ";

    private static string Mark(bool ok) => ok ? "✓" : "✗";
    private static string Pct(int count, int total) => total == 0 ? "-" : $"{100.0 * count / total:0}%";
    private static string Escape(string s) => s.Replace("|", "\\|").Replace("\n", " ");
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
