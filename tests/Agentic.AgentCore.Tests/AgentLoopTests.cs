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
            .EnqueueToolCall("get_date") // third identical call must be stopped
            .EnqueueText("never reached");

        var result = await Loop(client).RunAsync(new AgentOptions { UserTask = "date?" });

        Assert.Equal(StopReason.LoopDetected, result.StopReason);
        Assert.Equal(3, result.Steps.Count);
        // the third call was rejected with a loop-detection message, not executed
        var thirdCall = result.Steps[^1].ToolCalls[0];
        Assert.False(thirdCall.Ok);
        Assert.Contains("loop detected", thirdCall.ResultJson);
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
}
