using System.Text.Json.Nodes;
using Agentic.AgentCore.Tools;
using Xunit;

namespace Agentic.AgentCore.Tests;

public class ToolRegistryTests
{
    private static ToolRegistry RegistryWithEcho(bool throwOnRun = false) =>
        new ToolRegistry().Register(new ToolDefinition
        {
            Name = "lookup_job",
            Description = "Look up a print job by number.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["job_number"] = new JsonObject { ["type"] = "string" },
                    ["verbose"] = new JsonObject { ["type"] = "boolean" },
                },
                ["required"] = new JsonArray("job_number"),
            },
            Handler = args => throwOnRun
                ? throw new InvalidOperationException("db is on fire")
                : Task.FromResult(ToolResult.Success(new { job = args["job_number"]!.GetValue<string>() })),
        });

    [Fact]
    public async Task Execute_ValidArgs_CallsHandler()
    {
        var result = await RegistryWithEcho().ExecuteAsync("lookup_job",
            new JsonObject { ["job_number"] = "1002" });

        Assert.True(result.Ok);
        Assert.Contains("1002", result.DataJson);
    }

    [Fact]
    public async Task Execute_MissingRequired_ReturnsStructuredErrorWithHint()
    {
        var result = await RegistryWithEcho().ExecuteAsync("lookup_job", new JsonObject());

        Assert.False(result.Ok);
        Assert.Contains("missing required property 'job_number'", result.Error);
        Assert.Contains("hint", result.DataJson);
    }

    [Fact]
    public async Task Execute_WrongType_ReturnsSchemaError()
    {
        var result = await RegistryWithEcho().ExecuteAsync("lookup_job",
            new JsonObject { ["job_number"] = 1002 });

        Assert.False(result.Ok);
        Assert.Contains("should be of type string", result.Error);
    }

    [Fact]
    public async Task Execute_UnknownTool_ListsAvailableTools()
    {
        var result = await RegistryWithEcho().ExecuteAsync("frobnicate", new JsonObject());

        Assert.False(result.Ok);
        Assert.Contains("Unknown tool", result.Error);
        Assert.Contains("lookup_job", result.DataJson);
    }

    [Fact]
    public async Task Execute_HandlerThrows_ReturnsErrorInsteadOfKillingLoop()
    {
        var result = await RegistryWithEcho(throwOnRun: true).ExecuteAsync("lookup_job",
            new JsonObject { ["job_number"] = "1002" });

        Assert.False(result.Ok);
        Assert.Contains("InvalidOperationException", result.Error);
        Assert.Contains("db is on fire", result.Error);
    }

    [Fact]
    public async Task Execute_EnumViolation_ReturnsAllowedValues()
    {
        var registry = new ToolRegistry().Register(new ToolDefinition
        {
            Name = "set_status",
            Description = "Set job status.",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["status"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("in_production", "shipped"),
                    },
                },
                ["required"] = new JsonArray("status"),
            },
            Handler = _ => Task.FromResult(ToolResult.Success(new { ok = true })),
        });

        var result = await registry.ExecuteAsync("set_status",
            new JsonObject { ["status"] = "teleported" });

        Assert.False(result.Ok);
        Assert.Contains("must be one of", result.Error);
        Assert.Contains("shipped", result.Error);
    }

    [Fact]
    public void Register_DuplicateName_Throws() =>
        Assert.Throws<InvalidOperationException>(() => RegistryWithEcho().Register(new ToolDefinition
        {
            Name = "lookup_job",
            Description = "dup",
            InputSchema = new JsonObject(),
            Handler = _ => Task.FromResult(ToolResult.Success(new { })),
        }));
}
