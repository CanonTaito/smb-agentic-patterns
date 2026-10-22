using Agentic.AgentCore;

namespace Agentic.Patterns.InboxTriage;

public static class Triage
{
    public static readonly string[] Categories =
        ["quote_request", "order_status", "invoice_payment", "artwork_change", "unclear"];
}

/// <summary>A routing decision. Confidence below the threshold (or category
/// "unclear") sends the message down the clarifying-reply path instead of a
/// specialist.</summary>
public sealed record RouteDecision(string Category, double Confidence, string? Reason)
{
    public bool NeedsClarification =>
        Category == "unclear" || Confidence < TriageRouter.MinConfidence;
}

/// <summary>
/// The router (pattern 4: routing / hand-off). Two interchangeable strategies
/// over the same RouteDecision contract:
///   - LLM: structured-output classification with a confidence the pipeline
///     can threshold on (the production path).
///   - Embeddings: cosine similarity against hand-written category prototypes
///     with nomic-embed-text — cheap and fast, but raw cosine is uncalibrated
///     (evaluated for accuracy, see README).
/// </summary>
public static class TriageRouter
{
    public const double MinConfidence = 0.7;

    private const string SystemPrompt = """
        You are the inbox router for Inkline Print Co., a small screen-printing shop in Queensland, Australia.

        Classify the customer email into exactly one category:

        - quote_request    — they want a price, a quote, costing, or to place/reorder goods
        - order_status     — where a specific job/order is up to, delivery status or ETA
        - invoice_payment  — invoices, payments, amounts owed, account statements, payment terms
        - artwork_change   — proofs, artwork revisions, design or size changes before/after printing
        - unclear          — MIXED emails (two departments' questions in one), vague requests, or
                             anything that does not clearly belong to exactly one category above

        Rules:
        - Force-routing a mixed email into ONE department is wrong. If it contains two kinds of
          request (e.g. "reorder, and also an invoice I don't recognise"), choose unclear — we
          send a clarifying reply instead of a half-answer.
        - A reply that answers only one half of their email is a failure; unclear exists to
          prevent that.
        - Confidence is your probability that the classification is correct (0.0 to 1.0).

        Examples:
        - "We want to reorder the winter hoodies, and there's an invoice on our account I don't recognise" → unclear (mixed)
        - "Attached is the updated artwork - confirm you've got it and let us know the delivery date" → unclear (mixed)
        - "What's your process for getting shirts made, and how does pricing work at different quantities?" → unclear (vague, spans depts)
        - "Before we commit to 200 totes, do you provide sizing samples? Also turnaround if we approve next week?" → unclear (mixed)
        - "Could you confirm the amount and due date on invoice INV-3103?" → invoice_payment
        - "Where is job 1005 up to?" → order_status
        - "Can you give us a price on 80 staff polos?" → quote_request
        - "Proof for job 1009 looks good - approved to proceed" → artwork_change

        Reply with ONLY a JSON object: {"category":"...","confidence":0.0,"reason":"one short sentence"}
        """;

    public static Task<RouteDecision> RouteAsync(IOllamaClient client, InboxMessage message, CancellationToken ct = default) =>
        StructuredOutput.GetAsync<RouteDecision>(
            client,
            SystemPrompt,
            "Classify this email:\n\n" + message.Text(),
            maxAttempts: 3,
            temperature: 0.0,
            validate: d =>
            {
                if (!Triage.Categories.Contains(d.Category))
                    return $"category must be one of: {string.Join(", ", Triage.Categories)}";
                if (d.Confidence is < 0 or > 1)
                    return "confidence must be between 0.0 and 1.0";
                return null;
            },
            ct: ct);
}

/// <summary>
/// Embedding-based router: embed the message, compare cosine similarity
/// against a small bank of hand-written prototypes per category, take the
/// winner. No LLM call — this is the cheap baseline the eval compares
/// against the LLM router on the same 30 labels.
/// </summary>
public sealed class EmbeddingRouter
{
    private static readonly (string Category, string Text)[] Prototypes =
    [
        ("quote_request", "Can you give us a price on 100 branded tees with a two-colour logo?"),
        ("quote_request", "We need a quote for 50 caps with a small one-colour print."),
        ("quote_request", "What would 25 hoodies cost with a front print?"),
        ("order_status", "Where is job 1005 up to?"),
        ("order_status", "Has our order shipped yet and when will it arrive?"),
        ("order_status", "What's the current status of job 1011, we need a delivery date."),
        ("invoice_payment", "Could you confirm the amount and due date on invoice INV-3103?"),
        ("invoice_payment", "What invoices are outstanding on our account right now?"),
        ("invoice_payment", "Can you reissue the invoice with our updated ABN and send a statement?"),
        ("artwork_change", "We've sent revised artwork - please update the proof before printing."),
        ("artwork_change", "The proof has our old logo on it, can you fix it?"),
        ("artwork_change", "Can we change the size breakdown before the job prints?"),
        ("unclear", "Can you help with a reorder and also an invoice query?"),
        ("unclear", "What's your process for getting shirts made and how does pricing work at different quantities?"),
        ("unclear", "We need artwork updated and also want to know the delivery date."),
    ];

    private readonly IOllamaClient _client;
    private readonly List<(string Category, float[] Vector)> _bank = [];
    private float[]? _center;

    public EmbeddingRouter(IOllamaClient client) => _client = client;

    public int PrototypeCount => _bank.Count;

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        _bank.Clear();
        var raw = new List<(string Category, float[] Vector)>();
        foreach (var (category, text) in Prototypes)
            raw.Add((category, await _client.EmbedAsync(text, OllamaClient.DefaultEmbedModel, ct)));

        // Mean-centre ("all-but-the-top"): raw cosine on embedding models sits
        // in a narrow anisotropic cone where every sentence scores ~0.8 against
        // everything and the ranking collapses to one category. Subtracting the
        // prototype mean before comparing restores the discriminative signal —
        // measured, not assumed (see README: 23% raw vs centred).
        _center = new float[raw[0].Vector.Length];
        foreach (var (_, vec) in raw)
            for (var i = 0; i < _center.Length; i++)
                _center[i] += vec[i] / raw.Count;

        foreach (var (category, vec) in raw)
            _bank.Add((category, Subtract(vec, _center)));
    }

    public async Task<RouteDecision> RouteAsync(InboxMessage message, CancellationToken ct = default)
    {
        if (_bank.Count == 0) await WarmUpAsync(ct);

        var vector = Subtract(
            await _client.EmbedAsync(message.Text(), OllamaClient.DefaultEmbedModel, ct),
            _center!);

        string best = "";
        string second = "";
        double bestScore = double.MinValue;
        double secondScore = double.MinValue;
        foreach (var (category, vec) in _bank)
        {
            var score = Cosine(vector, vec);
            if (score > bestScore)
            {
                (second, secondScore) = (best, bestScore);
                (best, bestScore) = (category, score);
            }
            else if (score > secondScore)
            {
                (second, secondScore) = (category, score);
            }
        }

        // Raw cosine similarity of the mean-centred vectors — deliberately NOT
        // squashed into a calibrated probability. The eval reports accuracy;
        // the pipeline thresholds at TriageRouter.MinConfidence, and a
        // similarity that misses it is a real finding, not a bug to hide.
        return new RouteDecision(best, Math.Round(bestScore, 4),
            $"centred cosine {bestScore:0.000} vs {second} {secondScore:0.000} (embedding prototypes)");
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("embedding dimension mismatch");
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static float[] Subtract(float[] vector, float[] center)
    {
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
            result[i] = vector[i] - center[i];
        return result;
    }
}
