using System.Text.Json.Nodes;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tools;

namespace Agentic.Patterns.WeeklyReport;

/// <summary>
/// The eight data tools the weekly report pipeline can plan around: the
/// SQLite business tables plus the sales and reviews CSV exports. Same tool
/// design rules as P1 — one job each, schema-validated, structured errors.
/// </summary>
public static class ReportTools
{
    private static readonly string[] GarmentTypes = ["Tee", "Hoodie", "Cap", "Tote", "Apron"];
    private static readonly string[] Sizes = ["S", "M", "L", "XL", "XXL", "OneSize"];
    private static readonly string[] JobStatuses =
        ["quoted", "artwork_pending", "proof_approved", "in_production", "qc", "shipped", "complete", "cancelled"];
    private static readonly string[] InvoiceStatuses = ["unpaid", "overdue", "disputed", "paid"];

    public static ToolRegistry Build(InklineDb db)
    {
        var reg = new ToolRegistry();

        reg.Register(new ToolDefinition
        {
            Name = "at_risk_jobs",
            Description = "Jobs due within 7 days of the business date that have not shipped or been " +
                          "cancelled, soonest first. Includes jobs already past their due date.",
            InputSchema = EmptySchema(),
            Handler = _ =>
            {
                var jobs = db.ListAtRiskJobs(DataSeeder.SeedDate);
                return Task.FromResult(ToolResult.Success(new { count = jobs.Count, jobs }));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "list_jobs",
            Description = "List all print jobs ordered by due date. Optionally filter by status and/or customer id.",
            InputSchema = ObjectSchema(
                ("status", Enum(JobStatuses, "Optional job status filter.")),
                ("customer_id", Str("Optional customer id filter, e.g. \"C02\"."))),
            Handler = args =>
            {
                var jobs = db.ListJobs(OptString(args, "status"), OptString(args, "customer_id"));
                return Task.FromResult(ToolResult.Success(new { count = jobs.Count, jobs }));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "list_invoices",
            Description = "List invoices with customer names and due dates, soonest first. Filter by status: " +
                          "unpaid, overdue, disputed or paid.",
            InputSchema = ObjectSchema(
                ("status", Enum(InvoiceStatuses, "Optional invoice status filter."))),
            Handler = args =>
            {
                var invoices = db.ListInvoices(status: OptString(args, "status"));
                if (invoices.Count == 0)
                    return Task.FromResult(ToolResult.Fail(
                        $"No invoices with status '{OptString(args, "status")}'.",
                        $"Statuses: {string.Join(", ", InvoiceStatuses)}. Omit status to list all."));
                return Task.FromResult(ToolResult.Success(new { count = invoices.Count, invoices }));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "check_inventory",
            Description = "Blank garment stock with quantity, reorder level and a low flag (qty below reorder level). " +
                          "Optionally filter by garment type and/or size.",
            InputSchema = ObjectSchema(
                ("garment_type", Enum(GarmentTypes, "Optional garment type filter.")),
                ("size", Enum(Sizes, "Optional size filter."))),
            Handler = args =>
            {
                var rows = db.CheckBlankStock(OptString(args, "garment_type"), OptString(args, "size"));
                if (rows.Count == 0)
                    return Task.FromResult(ToolResult.Fail(
                        $"No stock rows match garment_type='{OptString(args, "garment_type") ?? "any"}' size='{OptString(args, "size") ?? "any"}'.",
                        $"Garment types: {string.Join(", ", GarmentTypes)}. Sizes: {string.Join(", ", Sizes)}."));
                var items = rows.Select(r => new { r.GarmentType, r.Size, r.Qty, r.ReorderLevel, Low = r.Qty < r.ReorderLevel });
                return Task.FromResult(ToolResult.Success(new { items }));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "check_ink",
            Description = "Ink levels in millilitres with a low flag (below 500 ml). Optionally filter by colour.",
            InputSchema = ObjectSchema(
                ("colour", Str("Optional colour name or fragment, e.g. \"Navy\"."))),
            Handler = args =>
            {
                var rows = db.CheckInk(OptString(args, "colour"));
                if (rows.Count == 0)
                    return Task.FromResult(ToolResult.Fail(
                        $"No ink matches colour='{OptString(args, "colour")}'.",
                        "Inks on hand: White, Black, Navy, Red, Forest Green, Mustard, Charcoal, Teal."));
                var items = rows.Select(r => new { r.Colour, r.MlRemaining, Low = r.MlRemaining < 500 });
                return Task.FromResult(ToolResult.Success(new { items }));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "recent_reviews",
            Description = "Customer reviews newest first (date, customer, rating out of 5, comment). " +
                          "Pass max_rating to see only the poor ones, e.g. max_rating=3.",
            InputSchema = ObjectSchema(
                ("max_rating", new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "Only return reviews rated this value or lower (1-5).",
                })),
            Handler = args =>
            {
                var rows = ReadCsv(RepoPaths.ReviewsCsvPath);
                var maxRating = args["max_rating"] is { } mr && mr.GetValueKind() == System.Text.Json.JsonValueKind.Number
                    ? (int?)mr.GetValue<int>()
                    : null;
                var reviews = rows
                    .Select(r => new
                    {
                        review_id = r["review_id"],
                        customer = r["customer"],
                        review_date = r["review_date"],
                        rating = int.Parse(r["rating"]),
                        comment = r["comment"],
                    })
                    .Where(r => maxRating is null || r.rating <= maxRating)
                    .OrderByDescending(r => r.review_date)
                    .ToList();
                return Task.FromResult(ToolResult.Success(new { count = reviews.Count, reviews }));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "weekly_sales",
            Description = "Weekly sales metrics: orders received/shipped, revenue (AUD), average turnaround days, " +
                          "quotes sent. Oldest to newest. Pass weeks to return only the most recent N weeks.",
            InputSchema = ObjectSchema(
                ("weeks", new JsonObject
                {
                    ["type"] = "integer",
                    ["description"] = "Return only the most recent N weeks (e.g. 4). Omit for all weeks.",
                })),
            Handler = args =>
            {
                var rows = ReadCsv(RepoPaths.SalesCsvPath);
                var parsed = rows.Select(r => new
                {
                    week_start = r["week_start"],
                    orders_received = int.Parse(r["orders_received"]),
                    orders_shipped = int.Parse(r["orders_shipped"]),
                    revenue_aud = decimal.Parse(r["revenue_aud"]),
                    avg_turnaround_days = double.Parse(r["avg_turnaround_days"]),
                    quotes_sent = int.Parse(r["quotes_sent"]),
                }).ToList();
                if (args["weeks"] is { } w && w.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                    parsed = parsed.Skip(Math.Max(0, parsed.Count - w.GetValue<int>())).ToList();
                return Task.FromResult(ToolResult.Success(new { count = parsed.Count, weeks = parsed }));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "get_today",
            Description = "Return the business date. The dataset is a frozen snapshot — always call this " +
                          "instead of assuming what day it is.",
            InputSchema = EmptySchema(),
            Handler = _ => Task.FromResult(ToolResult.Success(new
            {
                today = DataSeeder.SeedDate,
                note = "Frozen dataset snapshot; compute relative dates from this date.",
            })),
        });

        return reg;
    }

    // --- CSV (quotes, embedded commas and escaped quotes handled) ---------

    internal static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return [];
        var header = ParseCsvLine(lines[0]);
        var rows = new List<Dictionary<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsvLine(line);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length && i < fields.Length; i++)
                row[header[i]] = fields[i];
            rows.Add(row);
        }
        return rows;
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }

    // --- schema helpers ---------------------------------------------------

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Enum(string[] values, string description) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
        ["description"] = description,
    };

    private static JsonObject EmptySchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    private static JsonObject ObjectSchema(params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties) props[name] = schema;
        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }

    private static string? OptString(JsonObject args, string name) =>
        args[name] is { } node && node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : null;
}
