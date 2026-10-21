using System.Diagnostics;
using Agentic.AgentCore;
using Agentic.AgentCore.Tracing;

namespace Agentic.Patterns.QuoteGenerator;

public sealed class QuoteResult
{
    public required QuoteJob Job { get; init; }
    public required QuoteBreakdown Breakdown { get; init; }
    public required string FinalText { get; init; }
    public required string FirstDraft { get; init; }
    public required CritiqueResult Critique { get; init; }
    public required IReadOnlyList<CritiqueResult> RoundHistory { get; init; }
    public int Revisions { get; init; }
    public bool RequiresOwnerApproval { get; init; }
    public GateResult? Gate { get; init; }
    public required TokenUsage Usage { get; init; }
    public required IReadOnlyList<TokenUsage> CallUsage { get; init; }
    public int LlmCalls { get; init; }
    public long Ms { get; init; }
    public bool Passed => Critique.Passed;
}

/// <summary>
/// Pattern 3 pipeline: draft → deterministic rule critic → revise with the
/// violations fed back (≤ N rounds) → owner gate for quotes over $2,000.
/// With maxRevisionRounds = 0 this is a single-shot draft (the A/B baseline);
/// the eval scores both modes with the same critic.
/// </summary>
public static class QuotePipeline
{
    public const decimal OwnerApprovalThreshold = 2000m;

    private const string SystemPrompt = """
        You write customer quote emails for Inkline Print Co., a small screen-printing shop in Queensland, Australia.

        You receive a job spec and a price breakdown computed by our quoting system. Reproduce the
        figures EXACTLY — the total to the cent, the turnaround in business days.

        Every quote email must:
        - greet the customer by name and describe their job
        - show the price summary and the TOTAL including GST
        - state the turnaround in business days
        - say an artwork proof must be approved before production begins
        - state that new accounts pay a 50% deposit and established accounts are on 14-day terms
        - mention our size-run tolerance of +/-5%
        - end with a clear next step and sign off as "the Inkline team"

        Banned in our quotes — never write these words: guaranteed, cheapest, lowest price, best price.
        Reply with ONLY the email text.
        """;

    public static async Task<QuoteResult> RunAsync(
        IOllamaClient innerClient,
        QuoteJob job,
        int maxRevisionRounds,
        bool autoApprove,
        Tracer tracer,
        TextWriter? log = null,
        CancellationToken ct = default)
    {
        log ??= TextWriter.Null;
        var client = new RecordingOllamaClient(innerClient, tracer);
        var sw = Stopwatch.StartNew();
        var breakdown = QuoteEngine.Quote(job.Request);

        using var root = tracer.StartSpan($"quote: {job.Customer}, {job.Quantity} {job.Garment}", SpanKind.Agent);
        root.SetTag("total", breakdown.Total);
        root.SetTag("reflection", maxRevisionRounds);

        // --- draft ---------------------------------------------------------
        string draft;
        using (var span = tracer.StartSpan("phase: draft", SpanKind.Internal))
        {
            draft = await ChatAsync(client, job, breakdown, SystemPrompt,
                BuildDraftPrompt(job, breakdown), temperature: 0.2, ct);
            span.SetTag("chars", draft.Length);
            log.WriteLine($"SPEC: {job.SpecText()}");
            log.WriteLine(breakdown.ToText());
            log.WriteLine($"\nDRAFT:\n{draft}");
        }

        // --- reflect: critic → revise → re-check ---------------------------
        var history = new List<CritiqueResult>();
        var critique = RuleCritic.Check(draft, breakdown, job.Customer);
        var revisions = 0;
        var current = draft;
        var stagnated = false;

        for (var round = 1; round <= maxRevisionRounds && !critique.Passed; round++)
        {
            var issuesBefore = critique.Issues.Select(i => i.Message).ToArray();
            history.Add(critique);
            using var span = tracer.StartSpan($"phase: revise round {round}", SpanKind.Internal);
            span.SetTag("violations", critique.Issues.Count);

            log.WriteLine($"\nCRITIQUE round {round - 1}: FAIL ({critique.Issues.Count} issue(s))");
            foreach (var issue in critique.Issues)
                log.WriteLine($"  [{issue.Category}] {issue.Message}");

            current = await ChatAsync(client, job, breakdown, SystemPrompt,
                BuildRevisePrompt(job, breakdown, current, critique, stagnated),
                temperature: stagnated ? 0.7 : 0.2, ct);
            revisions++;
            critique = RuleCritic.Check(current, breakdown, job.Customer);

            // Stagnation: a revision that leaves the same violations unfixed
            // (typically the model re-emitting the same text at low temperature)
            // arms a blunt nudge + higher temperature for the next round.
            stagnated = critique.Issues.Select(i => i.Message).SequenceEqual(issuesBefore);
            span.SetTag("chars", current.Length);
            span.SetTag("stillFailing", !critique.Passed);
            span.SetTag("stagnated", stagnated);
            if (stagnated)
                log.WriteLine($"  round {round} left the same violations unfixed — nudging next round");
        }

        history.Add(critique);
        log.WriteLine(critique.Passed
            ? $"\nCRITIQUE: PASS after {revisions} revision(s)"
            : $"\nCRITIQUE: still FAILING after {revisions} revision(s) — " +
              string.Join("; ", critique.Issues.Select(i => i.Message)));

        // --- owner gate for big quotes -------------------------------------
        var requiresApproval = breakdown.Total > OwnerApprovalThreshold;
        GateResult? gate = null;
        if (requiresApproval)
        {
            using var gateSpan = tracer.StartSpan("phase: gate", SpanKind.Gate);
            gate = await HumanGate.RequestApprovalAsync(
                $"send quote to {job.Customer} — total ${breakdown.Total:N2} exceeds ${OwnerApprovalThreshold:N0}, owner approval required",
                current, autoApprove, output: log, ct: ct);
            gateSpan.SetTag("decision", gate.Decision.ToString());
            gateSpan.SetTag("total", breakdown.Total);
        }

        sw.Stop();
        root.SetTag("passed", critique.Passed);
        root.SetTag("revisions", revisions);
        root.SetTag("llmCalls", client.Calls.Count);
        root.SetTag("tokens", client.TotalUsage.TotalTokens);

        return new QuoteResult
        {
            Job = job,
            Breakdown = breakdown,
            FinalText = current,
            FirstDraft = draft,
            Critique = critique,
            RoundHistory = history,
            Revisions = revisions,
            RequiresOwnerApproval = requiresApproval,
            Gate = gate,
            Usage = client.TotalUsage,
            CallUsage = [.. client.Calls],
            LlmCalls = client.Calls.Count,
            Ms = sw.ElapsedMilliseconds,
        };
    }

    private static string BuildDraftPrompt(QuoteJob job, QuoteBreakdown breakdown) => $"""
        Customer: {job.Customer}
        Job spec: {job.SpecText()}

        {breakdown.ToText()}

        Write the quote email.
        """;

    private static string BuildRevisePrompt(QuoteJob job, QuoteBreakdown breakdown, string draft,
        CritiqueResult critique, bool nudged) => $"""
        Customer: {job.Customer}
        Job spec: {job.SpecText()}

        {breakdown.ToText()}

        This draft failed our quote rules:
        {string.Join(Environment.NewLine, critique.Issues.Select((i, n) => $"{n + 1}. [{i.Category}] {i.Message}"))}

        Current draft:
        {draft}

        Return the FULL corrected email — fix every listed problem, keep everything else.
        {(nudged ? "WARNING: your last revision came back with the same problems unfixed. Actually rewrite the offending lines this time — do not return the same text." : "")}
        """;

    private static async Task<string> ChatAsync(
        RecordingOllamaClient client, QuoteJob job, QuoteBreakdown breakdown,
        string systemPrompt, string userPrompt, double temperature, CancellationToken ct)
    {
        var result = await client.ChatAsync(
            [ChatMessage.System(systemPrompt), ChatMessage.User(userPrompt)],
            tools: null, jsonFormat: null, temperature: temperature, ct: ct);
        var text = TrimCodeFences(result.Message.Content?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(text))
            text = $"(the model returned an empty reply for {job.Customer}'s quote — please retry)";
        return text;
    }

    private static string TrimCodeFences(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        if (firstNl < 0) return s;
        s = s[(firstNl + 1)..];
        var end = s.LastIndexOf("```");
        return (end >= 0 ? s[..end] : s).Trim();
    }
}
