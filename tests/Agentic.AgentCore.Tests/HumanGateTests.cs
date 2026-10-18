using Agentic.AgentCore;
using Xunit;

namespace Agentic.AgentCore.Tests;

public class HumanGateTests
{
    [Fact]
    public async Task AutoApprove_ApprovesWithoutPrompting()
    {
        var output = new StringWriter();

        var result = await HumanGate.RequestApprovalAsync(
            "send reply", "Hello customer!", autoApprove: true, output: output);

        Assert.Equal(GateDecision.Approved, result.Decision);
        Assert.Equal("Hello customer!", result.Content);
        Assert.Contains("AUTO-APPROVED", output.ToString());
    }

    [Fact]
    public async Task OperatorApprove_ReturnsContent()
    {
        var result = await HumanGate.RequestApprovalAsync(
            "send reply", "Hello customer!",
            output: new StringWriter(), input: new StringReader("a\n"));

        Assert.Equal(GateDecision.Approved, result.Decision);
        Assert.True(result.ShouldProceed);
    }

    [Fact]
    public async Task OperatorReject_BlocksProceed()
    {
        var result = await HumanGate.RequestApprovalAsync(
            "send reply", "Hello customer!",
            output: new StringWriter(), input: new StringReader("r\n"));

        Assert.Equal(GateDecision.Rejected, result.Decision);
        Assert.False(result.ShouldProceed);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task OperatorEdit_ReturnsEditedContent()
    {
        var result = await HumanGate.RequestApprovalAsync(
            "send reply", "draft", output: new StringWriter(),
            input: new StringReader("e\nEdited line one.\nEdited line two.\nEND\n"));

        Assert.Equal(GateDecision.Edited, result.Decision);
        Assert.Equal("Edited line one." + Environment.NewLine + "Edited line two.", result.Content);
    }
}
