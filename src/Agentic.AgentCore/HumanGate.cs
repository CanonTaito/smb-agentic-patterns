namespace Agentic.AgentCore;

public enum GateDecision { Approved, Edited, Rejected }

public sealed record GateResult(GateDecision Decision, string? Content, string? Note)
{
    public bool ShouldProceed => Decision != GateDecision.Rejected;
}

/// <summary>
/// Human-in-the-loop gate (guide §3.4): checkpoint before an irreversible
/// action — sending a reply, issuing a refund, starting production.
/// Non-interactive runs pass autoApprove (e.g. in eval mode); the decision
/// and note are recorded either way so traces show the gate fired.
/// </summary>
public static class HumanGate
{
    public static async Task<GateResult> RequestApprovalAsync(
        string actionDescription,
        string proposedOutput,
        bool autoApprove = false,
        TextWriter? output = null,
        TextReader? input = null,
        CancellationToken ct = default)
    {
        output ??= Console.Out;

        if (autoApprove)
        {
            output.WriteLine("[gate] AUTO-APPROVED: " + actionDescription);
            return new GateResult(GateDecision.Approved, proposedOutput, "auto-approved (non-interactive run)");
        }

        output.WriteLine();
        output.WriteLine("================ HUMAN APPROVAL REQUIRED ================");
        output.WriteLine("Action: " + actionDescription);
        output.WriteLine("---------------------------------------------------------");
        output.WriteLine(proposedOutput);
        output.WriteLine("---------------------------------------------------------");
        output.Write("[a]pprove / [e]dit / [r]eject > ");

        input ??= Console.In;
        var line = (await input.ReadLineAsync(ct))?.Trim().ToLowerInvariant();

        switch (line)
        {
            case "a" or "approve":
                return new GateResult(GateDecision.Approved, proposedOutput, "approved by operator");
            case "e" or "edit":
                output.WriteLine("Edit the output below. Finish with a single line containing only 'END':");
                var lines = new List<string>();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var l = await input.ReadLineAsync(ct);
                    if (l is null || l == "END") break;
                    lines.Add(l);
                }
                var edited = string.Join(Environment.NewLine, lines);
                return new GateResult(GateDecision.Edited, edited, "edited by operator");
            default:
                return new GateResult(GateDecision.Rejected, null, "rejected by operator");
        }
    }
}
