using Agentic.AgentCore;

namespace Agentic.Patterns.QuoteGenerator;

public sealed record CritiqueIssue(string Category, string Message);

public sealed record CritiqueResult(IReadOnlyList<CritiqueIssue> Issues)
{
    public bool Passed => Issues.Count == 0;
    public IEnumerable<string> Categories => Issues.Select(i => i.Category).Distinct();
}

/// <summary>
/// The deterministic half of the reflection pattern (guide §3): a rule-based
/// critic that checks a drafted quote email against ground truth — no LLM
/// involved. Categories: math (totals reconcile with the QuoteEngine),
/// turnaround, clause (required business clauses present), template
/// (placeholders / engine artefacts leaked into customer text), banned
/// (phrases our quotes must never contain). The reflection loop feeds these
/// violations back to the model until it passes or rounds run out.
/// </summary>
public static class RuleCritic
{
    public static CritiqueResult Check(string draft, QuoteBreakdown quote, string customer)
    {
        var issues = new List<CritiqueIssue>();
        var lower = draft.ToLowerInvariant();

        // math — the exact total (to the cent) must appear, $ and thousands
        // separators optional (normalised away before matching).
        var total = quote.Total.ToString("F2");
        var flat = lower.Replace("$", "").Replace(",", "").Replace(" ", "");
        if (!flat.Contains(total))
            issues.Add(new CritiqueIssue("math",
                $"the total must be ${total} including GST — the draft does not state it (or states it wrong)"));

        // turnaround
        var days = quote.TurnaroundDays;
        if (!lower.Contains($"{days} business day") && !lower.Contains($"{days} day"))
            issues.Add(new CritiqueIssue("turnaround",
                $"must state the turnaround of {days} business days exactly"));

        // required clauses
        if (!(lower.Contains("proof") &&
              (lower.Contains("before production") || lower.Contains("before we start") ||
               lower.Contains("before printing") || lower.Contains("approval") || lower.Contains("approve"))))
            issues.Add(new CritiqueIssue("clause",
                "missing required clause: an artwork proof must be approved before production begins"));

        if (!(lower.Contains("50%") && lower.Contains("deposit")))
            issues.Add(new CritiqueIssue("clause",
                "missing required clause: new accounts pay a 50% deposit"));

        if (!((lower.Contains("14-day") || lower.Contains("14 day") || lower.Contains("14 calendar")) &&
              lower.Contains("term")))
            issues.Add(new CritiqueIssue("clause",
                "missing required clause: established accounts are on 14-day terms"));

        if (!((lower.Contains("5%") || lower.Contains("5 percent") || lower.Contains("±5") || lower.Contains("+/-5")) &&
              lower.Contains("size")))
            issues.Add(new CritiqueIssue("clause",
                "missing required clause: size-run tolerance of +/-5%"));

        // unfilled template placeholders — "Dear [Customer Name]" going out
        // to a customer is exactly the defect this pattern exists to catch.
        // The message names the value to substitute so the revision is mechanical.
        if (draft.Contains('[') || draft.Contains(']'))
            issues.Add(new CritiqueIssue("template",
                $"unfilled template placeholder left in the email — replace it with the customer's name ({customer})"));

        // engine text copied verbatim into a customer-facing email
        if (draft.Contains("location(s)"))
            issues.Add(new CritiqueIssue("template",
                "price-table artefact copied into the email (contains 'colour-location(s)') — rewrite it as plain English"));

        // banned phrases
        foreach (var phrase in new[] { "guaranteed", "cheapest", "lowest price", "best price" })
            if (lower.Contains(phrase))
                issues.Add(new CritiqueIssue("banned", $"banned phrase used: \"{phrase}\""));

        return new CritiqueResult(issues);
    }
}
