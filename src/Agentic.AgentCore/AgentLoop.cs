using System.Text.Json;
using System.Text.Json.Nodes;
using Agentic.AgentCore.Tools;
using Agentic.AgentCore.Tracing;

namespace Agentic.AgentCore;

public sealed class AgentOptions
{
    public string? SystemPrompt { get; init; }
    public int MaxSteps { get; init; } = 10;
    public int MaxTotalTokens { get; init; } = 100_000;
    public double Temperature { get; init; } = 0.2;
    public string? Model { get; init; }

    /// <summary>Stop if the same tool+arguments appears this many times (loop detection).</summary>
    public int MaxIdenticalCalls { get; init; } = 2;

    public string UserTask { get; init; } = "";
}

public enum StopReason { Completed, MaxSteps, MaxTokens, LoopDetected }

public sealed record ToolCallRecord(string ToolName, string ArgumentsJson, bool Ok, string ResultJson);

public sealed record StepRecord(
    int Index,
    string? AssistantText,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    TokenUsage Usage);

public sealed class AgentRunResult
{
    public required string FinalText { get; init; }
    public required StopReason StopReason { get; init; }
    public IReadOnlyList<StepRecord> Steps { get; init; } = Array.Empty<StepRecord>();
    public TokenUsage TotalUsage => Steps.Aggregate(TokenUsage.None, (acc, s) => acc + s.Usage);
    public bool Completed => StopReason == StopReason.Completed;
}

/// <summary>
/// The agent loop from the guide's §1.3, hardened with the control patterns
/// production demands: max steps, token budget, loop detection, and every
/// tool failure returned to the model as structured data instead of thrown.
/// </summary>
public sealed class AgentLoop
{
    private readonly IOllamaClient _client;
    private readonly ToolRegistry _tools;
    private readonly Tracer _tracer;

    public AgentLoop(IOllamaClient client, ToolRegistry tools, Tracer tracer)
    {
        _client = client;
        _tools = tools;
        _tracer = tracer;
    }

    public async Task<AgentRunResult> RunAsync(AgentOptions options, CancellationToken ct = default)
    {
        using var runSpan = _tracer.StartSpan($"agent: {DescribeTask(options.UserTask)}", SpanKind.Agent);
        runSpan.SetTag("maxSteps", options.MaxSteps);
        runSpan.SetTag("maxTokens", options.MaxTotalTokens);

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(options.SystemPrompt))
            messages.Add(ChatMessage.System(options.SystemPrompt));
        messages.Add(ChatMessage.User(options.UserTask));

        var steps = new List<StepRecord>();
        var callCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var tokenTotal = TokenUsage.None;
        var stop = StopReason.MaxSteps;
        var loopRejections = 0;

        for (var step = 1; step <= options.MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            OllamaChatResult llmResult;
            using (var llmSpan = _tracer.StartSpan($"llm call (step {step})", SpanKind.Llm))
            {
                llmSpan.SetTag("model", options.Model ?? "default");
                llmResult = await _client.ChatAsync(
                    messages, _tools.ToOllamaTools(), jsonFormat: null,
                    temperature: options.Temperature, model: options.Model, ct);
                llmSpan.SetTag("durationMs", llmResult.DurationMs);
                llmSpan.SetTag("promptTokens", llmResult.Usage.PromptTokens);
                llmSpan.SetTag("completionTokens", llmResult.Usage.CompletionTokens);
                llmSpan.SetTag("toolCalls", llmResult.Message.ToolCalls?.Count ?? 0);
            }

            messages.Add(llmResult.Message);
            tokenTotal += llmResult.Usage;

            var assistantText = llmResult.Message.Content;
            var toolCalls = llmResult.Message.ToolCalls ?? [];

            if (toolCalls.Count == 0)
            {
                // Small models sometimes emit an empty assistant message with no
                // tool calls. Ending there would return a blank answer, so nudge
                // (bounded by MaxSteps) instead.
                if (string.IsNullOrWhiteSpace(assistantText) && step < options.MaxSteps)
                {
                    steps.Add(new StepRecord(step, assistantText, [], llmResult.Usage));
                    messages.Add(ChatMessage.User(
                        "Your reply was empty. Answer the user's question directly in plain text now, " +
                        "using what the tools told you."));
                    continue;
                }

                steps.Add(new StepRecord(step, assistantText, [], llmResult.Usage));
                stop = StopReason.Completed;
                break;
            }

            var records = new List<ToolCallRecord>();
            var stopForLoop = false;
            foreach (var call in toolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var name = call.Function.Name ?? "";
                var args = call.Function.GetArguments();
                var argsJson = args.ToJsonString();

                // Loop detection: identical tool+args past the limit are rejected
                // with a structured hint instead of executed. The hint goes back
                // to the model so a stuck run gets one chance to recover; if it
                // keeps repeating (3 rejections), the run is stopped.
                var key = $"{name}({argsJson})";
                callCounts.TryGetValue(key, out var seen);
                callCounts[key] = seen + 1;
                if (seen + 1 > options.MaxIdenticalCalls)
                {
                    loopRejections++;
                    var msg = $"{{\"ok\":false,\"error\":\"loop detected: identical call '{name}' with the same arguments repeated {seen + 1} times\",\"hint\":\"You have already done this. Change the arguments, pick a different tool, or answer with what you have.\"}}";
                    messages.Add(ChatMessage.ToolResult(name, msg));
                    records.Add(new ToolCallRecord(name, argsJson, false, msg));
                    if (loopRejections >= 3) stopForLoop = true;
                    continue;
                }

                ToolResult result;
                using (var toolSpan = _tracer.StartSpan($"tool: {name}", SpanKind.Tool))
                {
                    toolSpan.SetTag("args", Truncate(argsJson, 200));
                    result = await _tools.ExecuteAsync(name, args);
                    toolSpan.SetTag("ok", result.Ok);
                    if (!result.Ok) toolSpan.SetTag("error", Truncate(result.Error ?? "", 200));
                }

                messages.Add(ChatMessage.ToolResult(name, result.DataJson));
                records.Add(new ToolCallRecord(name, argsJson, result.Ok, result.DataJson));
            }

            steps.Add(new StepRecord(step, assistantText, records, llmResult.Usage));

            if (stopForLoop)
            {
                stop = StopReason.LoopDetected;
                break;
            }

            if (tokenTotal.TotalTokens > options.MaxTotalTokens)
            {
                stop = StopReason.MaxTokens;
                break;
            }
        }

        runSpan.SetTag("stopReason", stop.ToString());
        runSpan.SetTag("steps", steps.Count);
        runSpan.SetTag("tokens", tokenTotal.TotalTokens);
        runSpan.SetTag("promptTokens", tokenTotal.PromptTokens);
        runSpan.SetTag("completionTokens", tokenTotal.CompletionTokens);

        var final = steps.LastOrDefault()?.AssistantText ?? "";
        if (string.IsNullOrWhiteSpace(final))
            final = $"The agent finished with stop reason {stop} after {steps.Count} step(s) but produced no " +
                    "final text. The tool results are recorded in the trace.";

        if (stop != StopReason.Completed)
        {
            final = $"[stopped: {stop}] {final}\n\n" +
                    "The agent did not finish within its limits. Partial progress is above; " +
                    "steps taken: " + steps.Count + ", tokens used: " + tokenTotal.TotalTokens + ".";
        }

        return new AgentRunResult { FinalText = final, StopReason = stop, Steps = steps };
    }

    private static string DescribeTask(string task) =>
        task.Length <= 60 ? task : task[..57] + "...";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
