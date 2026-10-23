using System.Text.Json.Nodes;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tools;

namespace Agentic.Patterns.OwnerBriefing;

/// <summary>
/// Tool belts for the briefing team. The analyst sees sales and pipeline data,
/// the researcher sees customer reviews, and neither can reach into the other's
/// data - the bounded-context rule that makes the fan-out worth its tokens.
/// </summary>
public static class BriefingTools
{
    public static ToolRegistry Build(InklineDb db, IReadOnlyCollection<string> toolNames)
    {
        var all = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal)
        {
            ["sales_kpis"] = SalesKpis(),
            ["at_risk_jobs"] = AtRiskJobs(db),
            ["list_reviews"] = ListReviews(),
            ["get_today"] = GetToday(),
        };

        var reg = new ToolRegistry();
        foreach (var name in toolNames)
        {
            if (!all.TryGetValue(name, out var tool))
                throw new ArgumentException($"Unknown tool '{name}'. Known: {string.Join(", ", all.Keys)}.");
            reg.Register(tool);
        }
        return reg;
    }

    private static ToolDefinition SalesKpis() => new()
    {
        Name = "sales_kpis",
        Description = "Weekly sales KPIs computed in code from sales.csv: last week (revenue, orders received " +
                      "and shipped, quotes sent, average turnaround), the prior week, week-on-week revenue change " +
                      "percent, window averages, and the full 11-week series. Call this before quoting any " +
                      "revenue, order or turnaround figure.",
        InputSchema = ObjectSchema(
            ("window_weeks", Int("How many recent weeks to average over (default 4)."), false)),
        Handler = args =>
        {
            var weeks = OptInt(args, "window_weeks") ?? 4;
            if (weeks is < 1 or > 52)
                return Task.FromResult(ToolResult.Fail(
                    $"window_weeks must be between 1 and 52, got {weeks}.",
                    "Use 4 for the standard briefing window."));

            var sales = InklineData.LoadSales();
            if (weeks > sales.Count)
                return Task.FromResult(ToolResult.Fail(
                    $"sales.csv only has {sales.Count} weeks of data, so window_weeks={weeks} is impossible.",
                    $"Use a window between 1 and {sales.Count}."));

            return Task.FromResult(ToolResult.Success(InklineData.ComputeKpis(sales, weeks)));
        },
    };

    private static ToolDefinition AtRiskJobs(InklineDb db) => new()
    {
        Name = "at_risk_jobs",
        Description = "List every open print job due within the next 7 days (not shipped, complete or " +
                      "cancelled), ordered by due date. Includes job number, customer, status, due date, " +
                      "days until due (negative means overdue), rush flag and quoted total.",
        InputSchema = ObjectSchema(),
        Handler = _ =>
        {
            var today = DataSeeder.SeedDate;
            var jobs = db.ListAtRiskJobs(today)
                .Select(j => new
                {
                    job_number = j.JobNumber,
                    customer = j.CustomerName,
                    description = j.Description,
                    status = j.Status,
                    due_date = j.DueDate,
                    days_until_due = DaysUntil(today, j.DueDate),
                    rush = j.Rush,
                    quoted_total = j.QuotedTotal,
                })
                .ToList();

            return Task.FromResult(ToolResult.Success(new
            {
                today,
                count = jobs.Count,
                overdue = jobs.Count(j => j.days_until_due < 0),
                jobs,
            }));
        },
    };

    private static ToolDefinition ListReviews() => new()
    {
        Name = "list_reviews",
        Description = "All customer reviews from reviews.csv with a rating summary (count, average rating, " +
                      "how many are 3 stars or below, counts by star) and the low-rated reviews called out " +
                      "for follow-up.",
        InputSchema = ObjectSchema(),
        Handler = _ =>
        {
            var reviews = InklineData.LoadReviews();
            var stats = InklineData.ComputeReviewStats(reviews);
            var low = reviews.Where(r => r.Rating <= 3).ToList();

            return Task.FromResult(ToolResult.Success(new
            {
                stats.Count,
                stats.AvgRating,
                stats.LowRatingCount,
                stats.ByRating,
                reviews,
                low_ratings = low,
            }));
        },
    };

    private static ToolDefinition GetToday() => new()
    {
        Name = "get_today",
        Description = "Return today's date in the seeded business world (used for due-date maths).",
        InputSchema = ObjectSchema(),
        Handler = _ => Task.FromResult(ToolResult.Success(new
        {
            today = DataSeeder.SeedDate,
            weekday = DayOfWeekFor(DataSeeder.SeedDate),
        })),
    };

    private static string DayOfWeekFor(string date) =>
        DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture).DayOfWeek.ToString();

    private static int DaysUntil(string from, string to) =>
        DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture).DayNumber -
        DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture).DayNumber;

    // --- small helpers (same style as the other patterns' tools) ----------

    private static JsonObject Str(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    private static JsonObject Int(string description) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
    };

    private static JsonObject ObjectSchema(params (string Name, JsonObject Schema, bool IsRequired)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema, isRequired) in properties)
        {
            props[name] = schema;
            if (isRequired) required.Add(name);
        }

        var result = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) result["required"] = required;
        return result;
    }

    private static int? OptInt(JsonObject args, string name)
    {
        if (args[name] is not { } node) return null;
        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number when node is JsonValue num && num.TryGetValue<double>(out var d)
                => (int)Math.Round(d),
            System.Text.Json.JsonValueKind.String when int.TryParse(node.GetValue<string>(), out var i) => i,
            _ => null,
        };
    }
}
