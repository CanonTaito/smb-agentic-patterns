using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Agentic.AgentCore;

// Wire models for Ollama's /api/chat and /api/embed.
// Kept deliberately close to the JSON contract so the raw loop stays debuggable.

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; set; } // "system" | "user" | "assistant" | "tool"

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OllamaToolCall>? ToolCalls { get; set; }

    // Ollama tags tool results with tool_name (unlike OpenAI's tool_call_id).
    [JsonPropertyName("tool_name")]
    public string? ToolName { get; set; }

    public static ChatMessage System(string content) => new() { Role = "system", Content = content };
    public static ChatMessage User(string content) => new() { Role = "user", Content = content };

    public static ChatMessage ToolResult(string toolName, string contentJson) =>
        new() { Role = "tool", ToolName = toolName, Content = contentJson };
}

public sealed class OllamaToolCall
{
    [JsonPropertyName("function")]
    public required OllamaFunctionCall Function { get; set; }
}

public sealed class OllamaFunctionCall
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    // Ollama may deliver arguments as a parsed object or as a JSON string
    // depending on model/version — always normalise via GetArguments().
    [JsonPropertyName("arguments")]
    public JsonNode? Arguments { get; set; }

    public JsonObject GetArguments()
    {
        if (Arguments is JsonObject obj) return obj;
        if (Arguments is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
        {
            try
            {
                if (JsonNode.Parse(s) is JsonObject parsed) return parsed;
            }
            catch (JsonException) { /* fall through to empty */ }
        }
        return new JsonObject();
    }
}

public sealed class OllamaTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public required OllamaToolFunction Function { get; set; }
}

public sealed class OllamaToolFunction
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [JsonPropertyName("parameters")]
    public required JsonObject Parameters { get; set; }
}

public sealed class OllamaChatResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("done_reason")]
    public string? DoneReason { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int PromptEvalCount { get; set; }

    [JsonPropertyName("eval_count")]
    public int EvalCount { get; set; }

    [JsonPropertyName("total_duration_ns")]
    public long TotalDurationNs { get; set; }
}

// /api/embed (plural): the legacy /api/embeddings endpoint returns
// {"embedding":[]} on Ollama 0.40+, so we post to /api/embed and read
// the first vector of "embeddings".
public sealed class OllamaEmbedResponse
{
    [JsonPropertyName("embeddings")]
    public float[][]? Embeddings { get; set; }
}

public sealed record TokenUsage(int PromptTokens, int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.PromptTokens + b.PromptTokens, a.CompletionTokens + b.CompletionTokens);
    public static readonly TokenUsage None = new(0, 0);
}
