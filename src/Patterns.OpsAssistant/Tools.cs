using System.Text.Json.Nodes;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tools;

namespace Agentic.Patterns.OpsAssistant;

/// <summary>
/// The seven tools the ops assistant exposes. Each does one job, validates
/// its inputs (the registry enforces the schema), and returns a structured
/// error with a hint when it cannot help — the tool design rules from the
/// guide's pattern 1 section.
/// </summary>
public static class InklineTools
{
    private static readonly string[] GarmentTypes = ["Tee", "Hoodie", "Cap", "Tote", "Apron"];
    private static readonly string[] Sizes = ["S", "M", "L", "XL", "XXL", "OneSize"];
    private static readonly string[] JobStatuses =
        ["quoted", "artwork_pending", "proof_approved", "in_production", "qc", "shipped", "complete", "cancelled"];

    public static ToolRegistry Build(InklineDb db)
    {
        var reg = new ToolRegistry();

        reg.Register(new ToolDefinition
        {
            Name = "lookup_job",
            Description = "Look up one print job by its job number. Returns the customer, description, " +
                          "garment type, quantity, print spec, status, quoted total, due date and rush flag.",
            InputSchema = ObjectSchema(
                ("job_number", Str("The job number as a string, e.g. \"1002\""), true)),
            Handler = args =>
            {
                var job = db.GetJob(ArgString(args, "job_number"));
                return Task.FromResult(job is null
                    ? ToolResult.Fail(
                        $"No job found with number '{ArgString(args, "job_number")}'.",
                        "Job numbers in this system run 1001-1012. Use list_jobs to see every job.")
                    : ToolResult.Success(job));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "list_jobs",
            Description = "List print jobs ordered by due date (soonest first). Optionally filter by status " +
                          "and/or customer id. Use this instead of lookup_job when you need many jobs, " +
                          "e.g. 'everything in production' or 'all jobs for customer C02'.",
            InputSchema = ObjectSchema(
                ("status", Enum(JobStatuses, "Optional job status filter."), false),
                ("customer_id", Str("Optional customer id filter, e.g. \"C02\"."), false)),
            Handler = args =>
            {
                var status = OptString(args, "status");
                var customer = OptString(args, "customer_id");
                var jobs = db.ListJobs(status, customer);
                return Task.FromResult(jobs.Count == 0
                    ? ToolResult.Fail(
                        $"No jobs match status='{status ?? "any"}' customer_id='{customer ?? "any"}'.",
                        "Statuses: " + string.Join(", ", JobStatuses) + ". Customer ids look like C01-C08.")
                    : ToolResult.Success(new { count = jobs.Count, jobs }));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "lookup_customer",
            Description = "Find a customer by id (e.g. \"C05\") or by a fragment of their name " +
                          "(e.g. \"Westlake\"). Returns contact details, payment terms and account notes.",
            InputSchema = ObjectSchema(
                ("query", Str("Customer id or a fragment of the customer name."), true)),
            Handler = args =>
            {
                var customer = db.GetCustomer(ArgString(args, "query"));
                return Task.FromResult(customer is null
                    ? ToolResult.Fail(
                        $"No customer matches '{ArgString(args, "query")}'.",
                        "Try a shorter fragment of the name (e.g. 'Westlake'), or an id like C01-C08.")
                    : ToolResult.Success(customer));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "check_inventory",
            Description = "Check blank garment stock. Each row shows quantity on hand and the reorder level; " +
                          "'low' is true when quantity is below the reorder level. Filter by garment type " +
                          "and/or size, or omit both to see everything.",
            InputSchema = ObjectSchema(
                ("garment_type", Enum(GarmentTypes, "Optional garment type filter."), false),
                ("size", Enum(Sizes, "Optional size filter."), false)),
            Handler = args =>
            {
                var garment = OptString(args, "garment_type");
                var size = OptString(args, "size");
                var rows = db.CheckBlankStock(garment, size);
                if (rows.Count == 0)
                {
                    return Task.FromResult(ToolResult.Fail(
                        $"No stock rows match garment_type='{garment ?? "any"}' size='{size ?? "any"}'.",
                        $"Garment types: {string.Join(", ", GarmentTypes)}. Sizes: {string.Join(", ", Sizes)}."));
                }

                var items = rows.Select(r => new
                {
                    r.GarmentType,
                    r.Size,
                    r.Qty,
                    r.ReorderLevel,
                    Low = r.Qty < r.ReorderLevel,
                });
                return Task.FromResult(ToolResult.Success(new { items }));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "check_ink",
            Description = "Check ink levels in millilitres. Each row shows ml remaining and 'low' = true when " +
                          "below 500 ml (the reorder threshold). Omit colour to see all inks.",
            InputSchema = ObjectSchema(
                ("colour", Str("Optional colour name or fragment, e.g. \"Navy\"."), false)),
            Handler = args =>
            {
                var colour = OptString(args, "colour");
                var rows = db.CheckInk(colour);
                if (rows.Count == 0)
                {
                    return Task.FromResult(ToolResult.Fail(
                        $"No ink matches colour='{colour}'.",
                        "Inks on hand: White, Black, Navy, Red, Forest Green, Mustard, Charcoal, Teal."));
                }

                var items = rows.Select(r => new
                {
                    r.Colour,
                    r.MlRemaining,
                    Low = r.MlRemaining < 500,
                });
                return Task.FromResult(ToolResult.Success(new { items }));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "check_proof_status",
            Description = "List all artwork proof versions for a job with their statuses " +
                          "(draft, sent, approved, revision_requested) and approval dates.",
            InputSchema = ObjectSchema(
                ("job_number", Str("The job number as a string, e.g. \"1007\"."), true)),
            Handler = args =>
            {
                var proofs = db.GetProofs(ArgString(args, "job_number"));
                if (proofs.Count == 0)
                {
                    return Task.FromResult(ToolResult.Fail(
                        $"No artwork proofs on file for job '{ArgString(args, "job_number")}'.",
                        "Either the job number is wrong, or no proof has been sent yet for that job."));
                }

                return Task.FromResult(ToolResult.Success(new
                {
                    job_number = ArgString(args, "job_number"),
                    latest = proofs[^1],
                    proofs,
                }));
            },
        });

        reg.Register(new ToolDefinition
        {
            Name = "get_today",
            Description = "Return the business date. The dataset is a frozen snapshot, so the date does not " +
                          "advance — always call this instead of assuming what day it is.",
            InputSchema = ObjectSchema(),
            Handler = _ => Task.FromResult(ToolResult.Success(new
            {
                today = DataSeeder.SeedDate,
                note = "Frozen dataset snapshot; relative dates (days until due, overdue) should be computed from this date.",
            })),
        });

        return reg;
    }

    // --- small schema helpers -------------------------------------------

    private static JsonObject Str(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    private static JsonObject Enum(string[] values, string description) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
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

    private static string ArgString(JsonObject args, string name) =>
        args[name]?.GetValue<string>() ?? "";

    private static string? OptString(JsonObject args, string name) =>
        args[name] is { } node && node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : null;
}
