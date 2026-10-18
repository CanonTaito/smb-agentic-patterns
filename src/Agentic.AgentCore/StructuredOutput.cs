using System.Text.Json;

namespace Agentic.AgentCore;

/// <summary>
/// Structured output helper (guide §4.4): forces JSON via Ollama's format mode,
/// validates by deserialising, and retries with the validation error in
/// context when the model produces something unparseable.
/// </summary>
public static class StructuredOutput
{
    public static async Task<T> GetAsync<T>(
        IOllamaClient client,
        string systemPrompt,
        string userPrompt,
        int maxAttempts = 3,
        double temperature = 0.2,
        string? model = null,
        Func<T, string?>? validate = null,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage> { ChatMessage.System(systemPrompt) };
        string? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var attemptPrompt = lastError is null
                ? userPrompt
                : userPrompt + $"\n\nYour previous reply was rejected: {lastError}\n" +
                  "Return ONLY a valid JSON object that fixes the problem.";

            messages.Add(ChatMessage.User(attemptPrompt));

            var result = await client.ChatAsync(messages, tools: null, jsonFormat: "json",
                temperature: temperature, model: model, ct);
            var text = result.Message.Content?.Trim() ?? "";

            try
            {
                var parsed = JsonSerializer.Deserialize<T>(text, JsonOptions)
                             ?? throw new JsonException("deserialised to null");
                var validationError = validate?.Invoke(parsed);
                if (validationError is null)
                    return parsed;
                lastError = validationError;
            }
            catch (JsonException ex)
            {
                lastError = $"not valid JSON for {typeof(T).Name} ({ex.Message}). Raw reply started: \"{Truncate(text, 120)}\"";
            }

            messages.RemoveAt(messages.Count - 1); // drop the failed user prompt before retrying
            messages.Add(ChatMessageExtensions.AssistantRaw(text));
            messages.Add(ChatMessage.User(
                $"That reply did not parse: {lastError}\nReturn ONLY the corrected JSON object."));
        }

        throw new StructuredOutputException(typeof(T), maxAttempts, lastError);
    }

    private static JsonSerializerOptions JsonOptions => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}

public sealed class StructuredOutputException(Type targetType, int attempts, string? lastError)
    : Exception($"Failed to obtain valid {targetType.Name} JSON after {attempts} attempts. Last error: {lastError}");

// small local helper to append raw assistant text back into the conversation
internal static class ChatMessageExtensions
{
    public static ChatMessage AssistantRaw(string content) => new() { Role = "assistant", Content = content };
}
