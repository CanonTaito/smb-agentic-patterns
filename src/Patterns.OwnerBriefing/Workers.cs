using Agentic.AgentCore;
using Agentic.AgentCore.Tools;
using Agentic.AgentCore.Tracing;

namespace Agentic.Patterns.OwnerBriefing;

public sealed record WorkerResult(
    string Name,
    string Output,
    int Steps,
    int ToolCalls,
    TokenUsage Usage,
    StopReason StopReason);

/// <summary>
/// The three workers and the single-agent control arm. Each worker gets one
/// job, one output format and one tool belt; the writer deliberately gets no
/// tools at all, so it can only reuse what the other two found.
/// </summary>
public static class Workers
{
    public const string CommonRules = """
        You are a worker agent for Inkline Print Co., a small screen-printing shop in
        Springfield Lakes, Queensland. The owner reads your output on Monday morning.
        Rules:
        - Use ONLY figures returned by tools. Never invent, estimate or round differently.
        - Today is 2026-10-18 (Sunday). The briefing covers the week just ended.
        - Australian English, plain text, no markdown tables.
        - Be concise: short bullets, exact numbers, no filler.
        """;

    public const string AnalystPrompt = CommonRules + """

        You are the SALES ANALYST worker. Call sales_kpis ONCE with window_weeks=4 - that single
        result already contains last week, the prior week and the 4-week averages, so do not call
        it again with another window. Then call at_risk_jobs.
        Produce bullet points covering:
        - Last week: revenue, orders received vs shipped, quotes sent, average turnaround.
        - Change vs the prior week: revenue percentage and direction.
        - The 4-week view: average and total revenue, average turnaround, orders, quotes.
        - At-risk jobs: every job due within 7 days that is not shipped/complete/cancelled,
          with job number, customer, status, due date and days_until_due. Say "overdue by N days"
          ONLY when days_until_due is negative; otherwise say "due in N days". Never call a
          positive days_until_due overdue. Say explicitly whether any are already overdue.
        - Close with the single most important thing the owner should notice in the numbers.
        If a number is not in a tool result, write "not available" instead of guessing.
        """;

    public const string ResearcherPrompt = CommonRules + """

        You are the CUSTOMER RESEARCHER worker. Call list_reviews.
        Produce bullet points covering:
        - Overall sentiment: review count, average rating, how many ratings are 3 or below.
        - The main themes across all reviews, with how many reviews support each theme:
          print quality/colour, turnaround/delays, communication/status updates,
          invoicing, and reprint recovery.
        - For every low rating (3 or below), say what went wrong and whether it was resolved.
          Quote or paraphrase short evidence, e.g. "Brisbane Makerspace: had to chase status twice".
        - Name the single theme the owner should act on first, and why.
        Do not invent reviews that are not in the tool result.
        """;

    public static string WriterPrompt(string supervisorBrief) => CommonRules + $"""

        You are the WRITER worker. You have no tools; the analyst and researcher have
        already done the work. Write the owner's Sunday-night briefing email.

        Supervisor's brief for this edition: {supervisorBrief}

        Shape it like this (plain text, under 300 words):
        - "Hi," on its own line, then a 1-2 sentence headline for the week.
        - "THE NUMBERS" - last week's revenue, orders, turnaround and quotes; the week-on-week
          revenue change; AND the 4-week average revenue and 4-week average turnaround. Quote
          every figure exactly as the analyst wrote it.
        - "PIPELINE RISK" - the at-risk jobs that matter, always naming the job number
          (e.g. "job 1010"), with the customer, due date and days overdue if any.
        - "CUSTOMERS" - rating summary and the themes that need attention.
        - "DO THIS WEEK" - 2 or 3 numbered, specific actions for the owner or the team.
        - Sign off with "- Inkline ops".
        Use only the figures in the analyst and researcher findings. Never invent a number,
        never omit the job number, and never claim something the findings do not support.
        """;

    public static string SingleAgentPrompt(string focus) => CommonRules + $"""

        You are writing the Sunday-night owner briefing email in a single pass.
        Owner focus for this edition: {focus}

        BEFORE you write a single word, call sales_kpis, at_risk_jobs and list_reviews.
        Do not write the email until those three tool results are in front of you; every
        figure must come from them. Same shape as always (under 300 words):
        - "Hi," then a 1-2 sentence headline for the week.
        - "THE NUMBERS" - last week's revenue, orders, turnaround and quotes; the week-on-week
          revenue change; AND the 4-week average revenue and 4-week average turnaround.
        - "PIPELINE RISK" - the at-risk jobs that matter, always naming the job number,
          with the customer, due date and days overdue if any.
        - "CUSTOMERS" - rating summary and the themes that need attention.
        - "DO THIS WEEK" - 2 or 3 numbered, specific actions.
        - Sign off with "- Inkline ops".
        Never invent a number that a tool did not return.
        """;

    public static AgentOptions AnalystOptions(string task) => new()
    {
        SystemPrompt = AnalystPrompt,
        UserTask = task,
        MaxSteps = 5,
        Temperature = 0.2,
    };

    public static AgentOptions ResearcherOptions(string task) => new()
    {
        SystemPrompt = ResearcherPrompt,
        UserTask = task,
        MaxSteps = 5,
        Temperature = 0.2,
    };

    public static AgentOptions SingleAgentOptions(string focus) => new()
    {
        SystemPrompt = SingleAgentPrompt(focus),
        UserTask = "Write this week's owner briefing email.",
        MaxSteps = 8,
        Temperature = 0.2,
    };

    /// <summary>
    /// Runs one worker. Small models sometimes answer from thin air, or call
    /// only the first tool that comes to mind; the supervisor verifies the
    /// worker actually did its assigned job and retries once with the missing
    /// calls spelled out. The retry's tokens and steps are counted, so the eval
    /// sees the real cost of the guard.
    /// </summary>
    public static Task<WorkerResult> RunAsync(
        string name,
        IOllamaClient client,
        ToolRegistry tools,
        Tracer tracer,
        AgentOptions options,
        Func<IReadOnlyList<ToolCallRecord>, string?>? verify = null,
        CancellationToken ct = default) =>
        tracer.InSpanAsync($"worker: {name}", async span =>
        {
            var run = await RunOnceAsync(client, tools, tracer, options, ct);
            var usage = run.TotalUsage;
            var steps = run.Steps.Count;

            var violation = verify?.Invoke(AllCalls(run));
            if (violation is not null)
            {
                span.SetTag("supervisorRetry", true);
                span.SetTag("supervisorViolation", violation);
                var retryOptions = WithTask(options,
                    $"IMPORTANT: the supervisor rejected your previous attempt: {violation}. " +
                    "Start again, make the required tool call(s) first, then answer using only those results.\n\n" +
                    options.UserTask);
                run = await RunOnceAsync(client, tools, tracer, retryOptions, ct);
                usage += run.TotalUsage;
                steps += run.Steps.Count;

                var stillMissing = verify?.Invoke(AllCalls(run));
                if (stillMissing is not null) span.SetTag("supervisorRejected", stillMissing);
            }

            var toolCalls = run.Steps.Sum(s => s.ToolCalls.Count);
            span.SetTag("steps", steps);
            span.SetTag("toolCalls", toolCalls);
            span.SetTag("stop", run.StopReason.ToString());
            span.SetTag("promptTokens", usage.PromptTokens);
            span.SetTag("completionTokens", usage.CompletionTokens);
            if (!run.Completed) span.SetTag("incomplete", true);
            if (toolCalls == 0) span.SetTag("noToolCalls", true);

            return new WorkerResult(name, run.FinalText, steps, toolCalls, usage, run.StopReason);
        }, SpanKind.Agent);

    private static IReadOnlyList<ToolCallRecord> AllCalls(AgentRunResult run) =>
        run.Steps.SelectMany(s => s.ToolCalls).ToList();

    private static Task<AgentRunResult> RunOnceAsync(
        IOllamaClient client,
        ToolRegistry tools,
        Tracer tracer,
        AgentOptions options,
        CancellationToken ct) =>
        new AgentLoop(client, tools, tracer).RunAsync(options, ct);

    private static AgentOptions WithTask(AgentOptions o, string task) => new()
    {
        SystemPrompt = o.SystemPrompt,
        MaxSteps = o.MaxSteps,
        MaxTotalTokens = o.MaxTotalTokens,
        Temperature = o.Temperature,
        Model = o.Model,
        MaxIdenticalCalls = o.MaxIdenticalCalls,
        UserTask = task,
    };
}
