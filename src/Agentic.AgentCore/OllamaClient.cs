using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agentic.AgentCore;

public sealed record OllamaChatResult(
    ChatMessage Message,
    TokenUsage Usage,
    string? DoneReason,
    long DurationMs);

/// <summary>
/// Minimal interface over the LLM so the agent loop is unit-testable
/// with a fake and swappable across models/providers.
/// </summary>
public interface IOllamaClient
{
    Task<OllamaChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<OllamaTool>? tools = null,
        string? jsonFormat = null,
        double temperature = 0.2,
        string? model = null,
        CancellationToken ct = default);

    Task<float[]> EmbedAsync(string input, string? model = null, CancellationToken ct = default);
}

/// <summary>
/// Raw HttpClient client for Ollama's HTTP API — no SDK, no framework.
/// The whole point: every byte of the loop is visible and debuggable.
/// </summary>
public sealed class OllamaClient : IOllamaClient, IDisposable
{
    public const string DefaultChatModel = "qwen2.5:3b";
    public const string DefaultEmbedModel = "nomic-embed-text";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public string BaseUrl { get; }
    public string DefaultModel { get; }

    public OllamaClient(string? baseUrl = null, string? defaultModel = null, HttpClient? http = null)
    {
        BaseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? "http://localhost:11434").TrimEnd('/');
        DefaultModel = defaultModel ?? Environment.GetEnvironmentVariable("AGENT_MODEL") ?? DefaultChatModel;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<OllamaChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<OllamaTool>? tools = null,
        string? jsonFormat = null,
        double temperature = 0.2,
        string? model = null,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = model ?? DefaultModel,
            ["messages"] = JsonSerializer.SerializeToNode(messages, JsonOpts)!.AsArray(),
            ["stream"] = false,
            ["options"] = new JsonObject { ["temperature"] = temperature },
        };
        if (tools is { Count: > 0 })
            body["tools"] = JsonSerializer.SerializeToNode(tools, JsonOpts)!.AsArray();
        if (jsonFormat is not null)
            body["format"] = jsonFormat; // "json" or a JSON schema

        var started = Environment.TickCount64;
        using var response = await _http.PostAsJsonAsync($"{BaseUrl}/api/chat", body, JsonOpts, ct);
        var elapsed = Environment.TickCount64 - started;

        var payload = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonOpts, ct)
                      ?? throw new InvalidOperationException("Ollama returned an empty response body.");

        if (!response.IsSuccessStatusCode)
            throw new OllamaApiException((int)response.StatusCode, payload.DoneReason ?? "unknown error");

        return new OllamaChatResult(
            payload.Message ?? new ChatMessage { Role = "assistant", Content = "" },
            new TokenUsage(payload.PromptEvalCount, payload.EvalCount),
            payload.DoneReason,
            elapsed);
    }

    public async Task<float[]> EmbedAsync(string input, string? model = null, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = model ?? DefaultEmbedModel,
            ["input"] = input,
        };
        using var response = await _http.PostAsJsonAsync($"{BaseUrl}/api/embed", body, JsonOpts, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(JsonOpts, ct);
        var vector = payload?.Embeddings is { Length: > 0 } first ? first[0] : null;
        return vector is { Length: > 0 }
            ? vector
            : throw new InvalidOperationException("Ollama returned no embedding.");
    }

    /// <summary>Health check used by eval runners before a suite starts.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{BaseUrl}/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public sealed class OllamaApiException(int statusCode, string message)
    : Exception($"Ollama API returned {statusCode}: {message}");
