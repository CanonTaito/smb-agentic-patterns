using System.Text.Json.Nodes;
using Agentic.AgentCore;

namespace Agentic.AgentCore.Tests;

/// <summary>Scripted fake: pops a pre-programmed sequence of chat responses.</summary>
public sealed class FakeOllamaClient : IOllamaClient
{
    private readonly Queue<Func<ChatMessage>> _script = new();

    public List<IReadOnlyList<ChatMessage>> CapturedRequests { get; } = new();

    public FakeOllamaClient Enqueue(Func<ChatMessage> next) { _script.Enqueue(next); return this; }

    public FakeOllamaClient EnqueueToolCall(string toolName, JsonObject? args = null, string? content = null) =>
        Enqueue(() => new ChatMessage
        {
            Role = "assistant",
            Content = content,
            ToolCalls = [new OllamaToolCall
            {
                Function = new OllamaFunctionCall { Name = toolName, Arguments = args ?? new JsonObject() },
            }],
        });

    public FakeOllamaClient EnqueueText(string content, int promptTokens = 10, int completionTokens = 5) =>
        Enqueue(() => new ChatMessage { Role = "assistant", Content = content });

    public Task<OllamaChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<OllamaTool>? tools = null,
        string? jsonFormat = null, double temperature = 0.2, string? model = null,
        CancellationToken ct = default)
    {
        CapturedRequests.Add(messages.ToList());
        if (_script.Count == 0)
            throw new InvalidOperationException("FakeOllamaClient script exhausted.");
        var message = _script.Dequeue()();
        return Task.FromResult(new OllamaChatResult(
            message, new TokenUsage(10, 5), "stop", 1));
    }

    public Task<float[]> EmbedAsync(string input, string? model = null, CancellationToken ct = default) =>
        Task.FromResult(new float[] { 1f, 0f, 0f });
}
