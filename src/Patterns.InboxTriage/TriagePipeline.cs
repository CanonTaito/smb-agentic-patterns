using System.Text.Json;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tracing;

namespace Agentic.Patterns.InboxTriage;

/// <summary>
/// The triage pipeline: route the email, then either a clarifying reply
/// (low confidence / unclear) or a department specialist with a narrow tool
/// belt. Every reply passes the human gate before "sending"; quotes over the
/// owner-approval threshold are held even in non-interactive runs.
/// </summary>
public sealed class TriageResult
{
    public required InboxMessage Message { get; init; }
    public required RouteDecision Route { get; init; }

    /// <summary>"llm" or "embedding" — which router made the call.</summary>
    public required string RouterKind { get; init; }

    /// <summary>"routed" (specialist answered) or "clarified" (asked them to).</summary>
    public required string Mode { get; init; }

    public required string Reply { get; init; }
    public required GateResult Gate { get; init; }

    /// <summary>Total from price_quote when the specialist priced an order.</summary>
    public decimal? QuoteTotal { get; init; }

    public string? SpecialistStop { get; init; }
    public int SpecialistSteps { get; init; }
    public IReadOnlyList<string> SpecialistTools { get; init; } = [];
    public required TokenUsage Usage { get; init; }
    public required int LlmCalls { get; init; }
    public required long Ms { get; init; }
}

public static class TriagePipeline
{
    /// <summary>Same rule as pattern 3: quotes at or above this need the owner, no exceptions.</summary>
    public const decimal OwnerApprovalThreshold = 2000m;

    private static readonly Lazy<InklineDb> Db = new(() => new InklineDb());

    public static async Task<TriageResult> RunAsync(
        IOllamaClient inner,
        InboxMessage message,
        Tracer tracer,
        bool useEmbeddingRouter = false,
        EmbeddingRouter? embeddingRouter = null,
        bool autoApprove = false,
        TextWriter? log = null,
        TextWriter? gateOut = null,
        CancellationToken ct = default)
    {
        var started = Environment.TickCount64;

        // The recorder wraps route/clarify calls only; the specialist loop gets
        // the raw client because AgentLoop already creates its own llm/tool spans
        // (recording again would double-count them in the trace tree).
        var recorder = new RecordingOllamaClient(inner, tracer);

        using var root = tracer.StartSpan($"triage {message.Id}: {message.ShortSubject}", SpanKind.Agent);

        // --- 1. route -----------------------------------------------------
        RouteDecision route;
        string routerKind;
        if (useEmbeddingRouter)
        {
            if (embeddingRouter is null)
                throw new ArgumentException("useEmbeddingRouter requires a warmed EmbeddingRouter.");
            if (embeddingRouter.PrototypeCount == 0)
                await embeddingRouter.WarmUpAsync(ct);
            route = await embeddingRouter.RouteAsync(message, ct);
            routerKind = "embedding";
        }
        else
        {
            using (var routeSpan = tracer.StartSpan("route: classify", SpanKind.Internal))
            {
                route = await TriageRouter.RouteAsync(recorder, message, ct);
                routeSpan.SetTag("category", route.Category);
                routeSpan.SetTag("confidence", route.Confidence);
            }
            routerKind = "llm";
        }

        root.SetTag("router", routerKind);
        root.SetTag("category", route.Category);
        root.SetTag("confidence", route.Confidence);
        root.SetTag("clarify", route.NeedsClarification);
        log?.WriteLine(
            $"  route: {route.Category} conf {route.Confidence:0.00} ({routerKind})" +
            (route.Reason is null ? "" : $" — {route.Reason}"));

        // --- 2a. low confidence → clarifying reply, no department ---------
        if (route.NeedsClarification)
        {
            log?.WriteLine("  → unclear/low confidence: sending a clarifying reply instead of a department");

            string reply;
            int callsBefore = recorder.Calls.Count;
            using (var span = tracer.StartSpan("compose clarifying reply", SpanKind.Internal))
            {
                reply = await Specialists.ClarifyAsync(recorder, message, ct);
                span.SetTag("tokens", recorder.Calls.Skip(callsBefore).Sum(c => c.TotalTokens));
            }

            var clarifyGate = await GateAsync(reply, message, category: route.Category, clarify: true, autoApprove, gateOut, tracer, ct);

            root.SetTag("mode", "clarified");
            root.SetTag("gate", clarifyGate.Decision.ToString());
            return new TriageResult
            {
                Message = message,
                Route = route,
                RouterKind = routerKind,
                Mode = "clarified",
                Reply = reply,
                Gate = clarifyGate,
                SpecialistSteps = 0,
                Usage = recorder.TotalUsage,
                LlmCalls = recorder.Calls.Count,
                Ms = Environment.TickCount64 - started,
            };
        }

        // --- 2b. routed → specialist with its tool belt -------------------
        var (systemPrompt, belt) = Specialists.For(route.Category);
        var tools = TriageTools.Build(Db.Value, belt);
        var loop = new AgentLoop(inner, tools, tracer);
        var run = await loop.RunAsync(new AgentOptions
        {
            SystemPrompt = systemPrompt,
            UserTask = EmailTask(message, route.Category),
            MaxSteps = 6,
            MaxTotalTokens = 40_000,
            Temperature = 0.2,
        }, ct);

        var specialistReply = run.FinalText;
        var quoteTotal = ExtractQuoteTotal(run);
        var specialistTools = run.Steps
            .SelectMany(s => s.ToolCalls)
            .Select(c => c.ToolName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n)
            .ToList();

        log?.WriteLine(
            $"  → {route.Category} specialist: {run.Steps.Count} steps, tools: " +
            (specialistTools.Count == 0 ? "none" : string.Join(", ", specialistTools)) +
            $" ({run.StopReason})");

        var gate = await GateAsync(specialistReply, message, category: route.Category, clarify: false, autoApprove, gateOut, tracer, ct, quoteTotal);

        root.SetTag("mode", "routed");
        root.SetTag("gate", gate.Decision.ToString());
        root.SetTag("specialistSteps", run.Steps.Count);
        root.SetTag("stopReason", run.StopReason.ToString());
        if (quoteTotal is not null) root.SetTag("quoteTotal", quoteTotal.Value);

        return new TriageResult
        {
            Message = message,
            Route = route,
            RouterKind = routerKind,
            Mode = "routed",
            Reply = specialistReply,
            Gate = gate,
            QuoteTotal = quoteTotal,
            SpecialistStop = run.StopReason.ToString(),
            SpecialistSteps = run.Steps.Count,
            SpecialistTools = specialistTools,
            Usage = recorder.TotalUsage + run.TotalUsage,
            LlmCalls = recorder.Calls.Count + run.Steps.Count,
            Ms = Environment.TickCount64 - started,
        };
    }

    private static async Task<GateResult> GateAsync(
        string reply,
        InboxMessage message,
        string category,
        bool clarify,
        bool autoApprove,
        TextWriter? gateOut,
        Tracer tracer,
        CancellationToken ct,
        decimal? quoteTotal = null)
    {
        using var gateSpan = tracer.StartSpan("gate", SpanKind.Gate);

        var gate = quoteTotal is > OwnerApprovalThreshold
            ? await HumanGate.EscalateAsync(
                $"quote of ${quoteTotal:N0} is at/over the ${OwnerApprovalThreshold:N0} owner-approval threshold — held, not sent",
                reply, gateOut, ct)
            : await HumanGate.RequestApprovalAsync(
                clarify
                    ? $"send clarifying reply to {message.From} ({message.Id})"
                    : $"send reply to {message.From} ({message.Id}, {category})",
                reply, autoApprove, gateOut, input: null, ct);

        gateSpan.SetTag("decision", gate.Decision.ToString());
        gateSpan.SetTag("shouldProceed", gate.ShouldProceed);
        if (quoteTotal is not null) gateSpan.SetTag("quoteTotal", quoteTotal.Value);
        return gate;
    }

    /// <summary>
    /// The hand-off note the router passes with the message: routing context
    /// travelling with the request, so the specialist starts with the right
    /// first move instead of rediscovering the intent.
    /// </summary>
    private static string EmailTask(InboxMessage message, string category)
    {
        var steer = category switch
        {
            "quote_request" =>
                "\nHint from the router: this is a quote request — price it with price_quote " +
                "before you reply. Baseline assumptions (1 colour, Front) are fine when the " +
                "customer left details out; say what you assumed.",
            "order_status" =>
                "\nHint from the router: this is a job-status question — look the job up before replying.",
            "invoice_payment" =>
                "\nHint from the router: this is an accounts question — pull the invoice/account record first.",
            "artwork_change" =>
                "\nHint from the router: this is an artwork/proof request — check the proof state first.",
            _ => "",
        };

        return $"""
            Reply to this customer email.

            From: {message.From}
            Subject: {message.Subject}

            {message.Body}
            {steer}

            Write your reply now.
            """;
    }

    private static decimal? ExtractQuoteTotal(AgentRunResult run)
    {
        decimal? total = null;
        foreach (var step in run.Steps)
        {
            foreach (var call in step.ToolCalls)
            {
                if (!call.Ok || call.ToolName != "price_quote") continue;
                try
                {
                    using var doc = JsonDocument.Parse(call.ResultJson);
                    if (doc.RootElement.TryGetProperty("total_incl_gst", out var node) && node.TryGetDecimal(out var d))
                        total = d;
                }
                catch (JsonException)
                {
                    // unparseable tool result — treat as "no total", gate falls back to normal approval
                }
            }
        }
        return total;
    }
}
