using System.Text.Json.Serialization;
using Agentic.AgentCore;

namespace Agentic.Patterns.OwnerBriefing;

public sealed record BriefingPlan(
    [property: JsonPropertyName("analyst_task")] string AnalystTask,
    [property: JsonPropertyName("researcher_task")] string ResearcherTask,
    [property: JsonPropertyName("writer_task")] string WriterTask);

/// <summary>
/// The supervisor step (guide pattern 5): before any worker runs, the owner's
/// focus is decomposed into three explicit tasks. The decomposition is data,
/// not a prompt side-effect - it is validated, traced, and then handed to the
/// workers as their brief.
/// </summary>
public static class Supervisor
{
    public const string SystemPrompt = """
        You are the supervisor of a three-worker team producing Inkline Print Co.'s
        Sunday-night owner briefing. Decompose the owner's focus into three focused tasks.
        Return JSON only:
        {"analyst_task": "...", "researcher_task": "...", "writer_task": "..."}

        - analyst_task: which sales and pipeline questions the analyst must answer this week.
        - researcher_task: which customer-review questions the researcher must answer this week.
        - writer_task: one sentence on how the email should be shaped or emphasised for this focus.
        Keep each task under 60 words. Never ask a worker to invent data or predict the future.

        Guardrails for every edition, regardless of the owner's focus:
        - The analyst must always report last week's revenue, orders, quotes and turnaround,
          the week-on-week revenue change, the 4-week averages, and every job due within 7 days.
        - The researcher must always report the rating summary and the themes from low-rated reviews.
        Focus only changes emphasis and adds questions; it never reduces the core facts collected.
        """;

    public static Task<BriefingPlan> DecomposeAsync(
        IOllamaClient client,
        string focus,
        CancellationToken ct = default) =>
        StructuredOutput.GetAsync<BriefingPlan>(
            client,
            SystemPrompt,
            $"Owner focus for this Sunday's briefing: {focus}\nDecompose it for the team.",
            validate: plan =>
                string.IsNullOrWhiteSpace(plan.AnalystTask) ||
                string.IsNullOrWhiteSpace(plan.ResearcherTask) ||
                string.IsNullOrWhiteSpace(plan.WriterTask)
                    ? "all three tasks (analyst_task, researcher_task, writer_task) must be non-empty strings"
                    : null,
            ct: ct);
}
