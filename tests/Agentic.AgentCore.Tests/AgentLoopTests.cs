using System.Text.Json.Nodes;
using Agentic.AgentCore.Tools;
using Agentic.AgentCore.Tracing;
using Xunit;

namespace Agentic.AgentCore.Tests;

public class AgentLoopTests
{
    private static ToolRegistry SimpleTools() =>
        new ToolRegistry().Register(new ToolDefinition
        {
            Name = "get_date",
            Description = "Return today's date.",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ => Task.FromResult(ToolResult.Success(new { date = "2026-10-18" })),
        });

    private static AgentLoop Loop(FakeOllamaClient client, ToolRegistry? tools = null) =>
        new(client, tools ?? SimpleTools(), new Tracer());

    [Fact]
    public async Task Run_NoToolCall_ReturnsAnswerImmediately()
    {
        var client = new FakeOllamaClient().EnqueueText("The answer is 42.");

        var result = await Loop(client).RunAsync(new AgentOptions { UserTask = "what is 6*7?" });

        Assert.Equal(StopReason.Completed, result.StopReason);
        Assert.Equal("The answer is 42.", result.FinalText);
        Assert.Single(result.Steps);
        Assert.Equal(15, result.TotalUsage.TotalTokens); // 10 prompt + 5 completion
    }

    [Fact]
    public async Task Run_ToolCallThenAnswer_CompletesInTwoSteps()
    {
        var client = new FakeOllamaClient()
            .EnqueueToolCall("get_date")
            .EnqueueText("Today is 2026-10-18.");

        var result = await Loop(client).RunAsync(new AgentOptions { UserTask = "what day is it?" });

        Assert.Equal(StopReason.Completed, result.StopReason);
        Assert.Equal(2, result.Steps.Count);
        Assert.Single(result.Steps[0].ToolCalls);
        Assert.True(result.Steps[0].ToolCalls[0].Ok);
    }

    [Fact]
    public async Task Run_IdenticalToolCalls_TriggersLoopDetection()
    {
        var client = new FakeOllamaClient()
            .EnqueueToolCall("get_date")
            .EnqueueToolCall("get_date")
            .EnqueueToolCall("get_date") // 3rd identical call: rejected with a hint, not executed
            .EnqueueToolCall("get_date") // still repeating despite the hint
            .EnqueueToolCall("get_date") // third rejection stops the run
            .EnqueueText("never reached");

        var result = await Loop(client).RunAsync(new AgentOptions { UserTask = "date?" });

        Assert.Equal(StopReason.LoopDetected, result.StopReason);
        Assert.Equal(5, result.Steps.Count);
        // the third call was rejected with a loop-detection message, not executed
        var thirdCall = result.Steps[2].ToolCalls[0];
        Assert.False(thirdCall.Ok);
        Assert.Contains("loop detected", thirdCall.ResultJson);
        // stopped runs still return usable text, never a blank string
        Assert.False(string.IsNullOrWhiteSpace(result.FinalText));
    }

    [Fact]
    public async Task Run_RejectedRepeat_GetsHintAndRecovers()
    {
        var client = new FakeOllamaClient()
            .EnqueueToolCall("get_date")
            .EnqueueToolCall("get_date")
            .EnqueueToolCall("get_date") // rejected with a corrective hint
            .EnqueueText("Today is 2026-10-18.");

        var result = await Loop(client).RunAsync(new AgentOptions { UserTask = "date?" });

        Assert.Equal(StopReason.Completed, result.StopReason);
        Assert.Equal("Today is 2026-10-18.", result.FinalText);
        Assert.False(result.Steps[2].ToolCalls[0].Ok);
        // the hint really reached the model: the final request contains it as a tool message
        Assert.Contains(client.CapturedRequests[^1],
            m => m.Role == "tool" && m.Content != null && m.Content.Contains("loop detected"));
    }

    [Fact]
    public async Task Run_DifferentArguments_AreNotLoopDetected()
    {
        var tools = new ToolRegistry().Register(new ToolDefinition
        {
            Name = "lookup",
            Description = "lookup",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } },
            },
            Handler = args => Task.FromResult(ToolResult.Success(new { id = args["id"] })),
        });
        var client = new FakeOllamaClient()
            .EnqueueToolCall("lookup", new JsonObject { ["id"] = "1" })
            .EnqueueToolCall("lookup", new JsonObject { ["id"] = "2" })
            .EnqueueToolCall("lookup", new JsonObject { ["id"] = "3" })
            .EnqueueText("all done");

        var result = await Loop(client, tools).RunAsync(new AgentOptions { UserTask = "look things up" });

        Assert.Equal(StopReason.Completed, result.StopReason);
    }

    [Fact]
    public async Task Run_MaxSteps_StopsWithPartialResult()
    {
        var client = new FakeOllamaClient()
            .EnqueueToolCall("get_date")
            .EnqueueToolCall("get_date");

        var result = await Loop(client).RunAsync(new AgentOptions { UserTask = "date?", MaxSteps = 2 });

        Assert.Equal(StopReason.MaxSteps, result.StopReason);
        Assert.False(result.Completed);
        Assert.Contains("stopped: MaxSteps", result.FinalText);
    }

    [Fact]
    public async Task Run_TokenBudgetExceeded_Stops()
    {
        var client = new FakeOllamaClient()
            .EnqueueToolCall("get_date") // each fake call bills 15 tokens
            .EnqueueToolCall("get_date")
            .EnqueueText("never");

        var result = await Loop(client).RunAsync(new AgentOptions
        {
            UserTask = "date?",
            MaxTotalTokens = 20,
        });

        Assert.Equal(StopReason.MaxTokens, result.StopReason);
    }

    [Fact]
    public async Task Run_ToolError_IsFedBackToModelAsData()
    {
        var tools = new ToolRegistry().Register(new ToolDefinition
        {
            Name = "flaky",
            Description = "sometimes fails",
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            Handler = _ => Task.FromResult(ToolResult.Fail("warehouse offline", "try again later")),
        });
        var client = new FakeOllamaClient()
            .EnqueueToolCall("flaky")
            .EnqueueText("The warehouse is offline; I'll retry later.");

        var result = await Loop(client, tools).RunAsync(new AgentOptions { UserTask = "check stock" });

        Assert.Equal(StopReason.Completed, result.StopReason);
        var fedBack = result.Steps[0].ToolCalls[0];
        Assert.False(fedBack.Ok);
        Assert.Contains("warehouse offline", fedBack.ResultJson);
        // the tool-result message really was appended to the model's context
        Assert.Contains(client.CapturedRequests[^1], m => m.Role == "tool");
    }

    [Fact]
    public async Task Run_EmptyAssistantReply_NudgesInsteadOfEndingBlank()
    {
        var client = new FakeOllamaClient()
            .Enqueue(() => new ChatMessage { Role = "assistant", Content = "" })
            .EnqueueText("Job 1002 is in production.");

        var result = await Loop(client, new ToolRegistry()).RunAsync(new AgentOptions { UserTask = "status of 1002?" });

        Assert.Equal(StopReason.Completed, result.StopReason);
        Assert.Equal("Job 1002 is in production.", result.FinalText);
        Assert.Equal(2, result.Steps.Count);
        // a user nudge telling the model its reply was empty was sent
        Assert.Contains(client.CapturedRequests[^1],
            m => m.Role == "user" && m.Content != null && m.Content.Contains("empty"));
    }

    [Fact]
    public async Task Run_ReplyEmptyEvenAfterNudge_ReturnsNonEmptyFallback()
    {
        var client = new FakeOllamaClient()
            .Enqueue(() => new ChatMessage { Role = "assistant", Content = "" })
            .Enqueue(() => new ChatMessage { Role = "assistant", Content = "   " });

        var result = await Loop(client, new ToolRegistry()).RunAsync(
            new AgentOptions { UserTask = "status of 1002?", MaxSteps = 2 });

        Assert.Equal(StopReason.Completed, result.StopReason);
        Assert.False(string.IsNullOrWhiteSpace(result.FinalText));
        Assert.Contains("no final text", result.FinalText);
    }
}
