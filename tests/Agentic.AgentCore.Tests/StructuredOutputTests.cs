using Agentic.AgentCore;
using Xunit;

namespace Agentic.AgentCore.Tests;

public class StructuredOutputTests
{
    private sealed record Route(string Category, float Confidence);

    [Fact]
    public async Task Get_FirstReplyValid_ReturnsParsed()
    {
        var client = new FakeOllamaClient()
            .EnqueueText("{\"category\": \"quote_request\", \"confidence\": 0.9}");

        var route = await StructuredOutput.GetAsync<Route>(client, "sys", "classify");

        Assert.Equal("quote_request", route.Category);
        Assert.Equal(0.9f, route.Confidence);
    }

    [Fact]
    public async Task Get_BadJsonThenGood_RetriesWithValidationError()
    {
        var client = new FakeOllamaClient()
            .EnqueueText("I think the category is quote_request because...")
            .EnqueueText("{\"category\": \"order_status\", \"confidence\": 0.8}");

        var route = await StructuredOutput.GetAsync<Route>(client, "sys", "classify");

        Assert.Equal("order_status", route.Category);
        Assert.Equal(2, client.CapturedRequests.Count);
        // the retry prompt must contain the validation error so the model can fix it
        var retryTurn = client.CapturedRequests[^1];
        Assert.Contains(retryTurn, m => m.Content != null && m.Content.Contains("did not parse"));
    }

    [Fact]
    public async Task Get_AllRepliesInvalid_ThrowsAfterMaxAttempts()
    {
        var client = new FakeOllamaClient()
            .EnqueueText("not json")
            .EnqueueText("still not json")
            .EnqueueText("nope");

        await Assert.ThrowsAsync<StructuredOutputException>(() =>
            StructuredOutput.GetAsync<Route>(client, "sys", "classify", maxAttempts: 3));
    }

    [Fact]
    public async Task Get_ValidatorRejects_Retries()
    {
        var client = new FakeOllamaClient()
            .EnqueueText("{\"category\": \"quote_request\", \"confidence\": 0.2}")
            .EnqueueText("{\"category\": \"quote_request\", \"confidence\": 0.9}");

        var route = await StructuredOutput.GetAsync<Route>(client, "sys", "classify",
            validate: r => r.Confidence < 0.5f ? "confidence below 0.5 — pick unclear or raise confidence" : null);

        Assert.Equal(0.9f, route.Confidence);
    }
}
