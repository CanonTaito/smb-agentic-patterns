using Agentic.AgentCore;
using Agentic.AgentCore.Tracing;

namespace Agentic.Patterns.QuoteGenerator;

/// <summary>
/// Wraps the real Ollama client so every LLM call the quote pipeline makes —
/// draft, each revision round — lands as a traced span and contributes to a
/// per-run token total.
/// </summary>
public sealed class RecordingOllamaClient : IOllamaClient
{
    private readonly IOllamaClient _inner;
    private readonly Tracer _tracer;

    public RecordingOllamaClient(IOllamaClient inner, Tracer tracer)
    {
        _inner = inner;
        _tracer = tracer;
    }

    public List<TokenUsage> Calls { get; } = [];
    public TokenUsage TotalUsage => Calls.Aggregate(TokenUsage.None, (a, b) => a + b);

    public async Task<OllamaChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<OllamaTool>? tools = null,
        string? jsonFormat = null,
        double temperature = 0.2,
        string? model = null,
        CancellationToken ct = default)
    {
        using var span = _tracer.StartSpan("llm call", SpanKind.Llm);
        span.SetTag("jsonMode", jsonFormat is not null);
        span.SetTag("tools", tools?.Count ?? 0);

        var result = await _inner.ChatAsync(messages, tools, jsonFormat, temperature, model, ct);

        span.SetTag("durationMs", result.DurationMs);
        span.SetTag("promptTokens", result.Usage.PromptTokens);
        span.SetTag("completionTokens", result.Usage.CompletionTokens);
        span.SetTag("toolCalls", result.Message.ToolCalls?.Count ?? 0);
        Calls.Add(result.Usage);
        return result;
    }

    public Task<float[]> EmbedAsync(string input, string? model = null, CancellationToken ct = default) =>
        _inner.EmbedAsync(input, model, ct);
}
