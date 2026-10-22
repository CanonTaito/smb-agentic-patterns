using System.Text.Json.Nodes;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using Agentic.AgentCore.Tools;

namespace Agentic.Patterns.InboxTriage;

/// <summary>
/// The tool belts. Routing isn't just about who answers — each specialist
/// only gets the tools its department needs (the least-privilege rule from
/// the guide): the invoice team can't quote, the quote team can't see
/// invoices. Build(db, belt) registers just those.
/// </summary>
public static class TriageTools
{
    public static ToolRegistry Build(InklineDb db, IReadOnlyCollection<string> toolNames)
    {
        var all = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal)
        {
            ["lookup_job"] = LookupJob(db),
            ["list_jobs"] = ListJobs(db),
            ["lookup_customer"] = LookupCustomer(db),
            ["list_invoices"] = ListInvoices(db),
            ["get_invoice"] = GetInvoice(db),
            ["check_proof_status"] = CheckProofStatus(db),
            ["price_quote"] = PriceQuote(),
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

    private static ToolDefinition LookupJob(InklineDb db) => new()
    {
        Name = "lookup_job",
        Description = "Look up one print job by its job number. Returns the customer, description, garment type, " +
                      "quantity, print spec, status, quoted total, due date and rush flag.",
        InputSchema = ObjectSchema(
            ("job_number", Str("The job number as a string, e.g. \"1002\"."), true)),
        Handler = args =>
        {
            var job = db.GetJob(ArgString(args, "job_number"));
            return Task.FromResult(job is null
                ? ToolResult.Fail(
                    $"No job found with number '{ArgString(args, "job_number")}'.",
                    "Job numbers in this system run 1001-1012. Use list_jobs to see every job.")
                : ToolResult.Success(job));
        },
    };

    private static ToolDefinition ListJobs(InklineDb db) => new()
    {
        Name = "list_jobs",
        Description = "List print jobs ordered by due date (soonest first). Optionally filter by status " +
                      "and/or customer id.",
        InputSchema = ObjectSchema(
            ("status", Enum(["quoted", "artwork_pending", "proof_approved", "in_production", "qc", "shipped", "complete", "cancelled"],
                "Optional job status filter."), false),
            ("customer_id", Str("Optional customer id filter, e.g. \"C02\"."), false)),
        Handler = args =>
        {
            var jobs = db.ListJobs(OptString(args, "status"), OptString(args, "customer_id"));
            return Task.FromResult(jobs.Count == 0
                ? ToolResult.Fail("No jobs match those filters.", "Customer ids look like C01-C06.")
                : ToolResult.Success(new { count = jobs.Count, jobs }));
        },
    };

    private static ToolDefinition LookupCustomer(InklineDb db) => new()
    {
        Name = "lookup_customer",
        Description = "Find a customer by id (e.g. \"C05\") or by a fragment of their name (e.g. \"Westlake\"). " +
                      "Returns contact details, payment terms and account notes.",
        InputSchema = ObjectSchema(
            ("query", Str("Customer id or a fragment of the customer name."), true)),
        Handler = args =>
        {
            var customer = db.GetCustomer(ArgString(args, "query"));
            return Task.FromResult(customer is null
                ? ToolResult.Fail(
                    $"No customer matches '{ArgString(args, "query")}'.",
                    "Try a shorter fragment of the name (e.g. 'Westlake'), or an id like C01-C06.")
                : ToolResult.Success(customer));
        },
    };

    private static ToolDefinition ListInvoices(InklineDb db) => new()
    {
        Name = "list_invoices",
        Description = "List invoices, optionally filtered by customer id and/or status. Use after lookup_customer " +
                      "to answer 'what's outstanding on our account'.",
        InputSchema = ObjectSchema(
            ("customer_id", Str("Optional customer id filter, e.g. \"C06\"."), false),
            ("status", Enum(["draft", "sent", "overdue", "paid", "disputed"],
                "Optional invoice status filter."), false)),
        Handler = args =>
        {
            var invoices = db.ListInvoices(OptString(args, "customer_id"), OptString(args, "status"));
            return Task.FromResult(invoices.Count == 0
                ? ToolResult.Fail(
                    $"No invoices match customer_id='{OptString(args, "customer_id") ?? "any"}' status='{OptString(args, "status") ?? "any"}'.",
                    "Statuses: draft, sent, overdue, paid, disputed. Customer ids look like C01-C06.")
                : ToolResult.Success(new { count = invoices.Count, invoices }));
        },
    };

    private static ToolDefinition GetInvoice(InklineDb db) => new()
    {
        Name = "get_invoice",
        Description = "Fetch one invoice by its id (e.g. \"INV-3103\"). Returns amount, GST, due date, status " +
                      "and the customer it belongs to.",
        InputSchema = ObjectSchema(
            ("invoice_id", Str("The invoice id, e.g. \"INV-3103\"."), true)),
        Handler = args =>
        {
            var invoice = db.GetInvoice(ArgString(args, "invoice_id"));
            return Task.FromResult(invoice is null
                ? ToolResult.Fail(
                    $"No invoice found with id '{ArgString(args, "invoice_id")}'.",
                    "Invoice ids look like INV-3098 to INV-3106. Use list_invoices to see the customer's invoices.")
                : ToolResult.Success(invoice));
        },
    };

    private static ToolDefinition CheckProofStatus(InklineDb db) => new()
    {
        Name = "check_proof_status",
        Description = "List all artwork proof versions for a job with their statuses " +
                      "(draft, sent, approved, revision_requested) and approval dates.",
        InputSchema = ObjectSchema(
            ("job_number", Str("The job number as a string, e.g. \"1007\"."), true)),
        Handler = args =>
        {
            var jobNumber = ArgString(args, "job_number");
            var proofs = db.GetProofs(jobNumber);
            if (proofs.Count == 0)
            {
                return Task.FromResult(ToolResult.Fail(
                    $"No artwork proofs on file for job '{jobNumber}'.",
                    "Either the job number is wrong, or no proof has been sent yet for that job."));
            }

            return Task.FromResult(ToolResult.Success(new
            {
                job_number = jobNumber,
                latest = proofs[^1],
                proofs,
            }));
        },
    };

    /// <summary>
    /// Deterministic pricing via the shared QuoteEngine — the same maths the
    /// reflection critic checks in pattern 3, so the quote the sales specialist
    /// sends is arithmetically guaranteed.
    /// </summary>
    private static ToolDefinition PriceQuote() => new()
    {
        Name = "price_quote",
        Description = "Price a print order with our standard rates: returns the full line-item breakdown, " +
                      "total incl. GST and turnaround in business days. Garments: tee, hoodie, cap, tote.",
        InputSchema = ObjectSchema(
            ("garment", Enum(["tee", "hoodie", "cap", "tote"],
                "Garment type. We print tees, hoodies, caps and totes only."), true),
            ("quantity", new JsonObject { ["type"] = "integer", ["description"] = "Number of garments." }, true),
            ("locations", new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Print locations, e.g. [{\"location\":\"Front\",\"colors\":2}]. Max 2 locations, 1-8 colours each.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["location"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JsonArray("Front", "Back"),
                            ["description"] = "Print position on the garment.",
                        },
                        ["colors"] = new JsonObject { ["type"] = "integer", ["description"] = "Number of print colours (1-8)." },
                    },
                    ["required"] = new JsonArray("location", "colors"),
                },
            }, true),
            ("rush", new JsonObject { ["type"] = "boolean", ["description"] = "True if the customer needs it faster than standard turnaround." }, false)),
        Handler = args =>
        {
            GarmentType garment;
            try
            {
                garment = QuoteEngine.ParseGarment(ArgString(args, "garment"));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ToolResult.Fail(ex.Message,
                    "We print tees, hoodies, caps and totes only. For anything else, say we don't print that product " +
                    "and offer a chat about alternatives — never make up a price."));
            }

            var quantity = ArgInt(args, "quantity");
            var locations = ParseLocations(args);
            if (locations is null)
            {
                return Task.FromResult(ToolResult.Fail(
                    "'locations' must be a non-empty array of objects like {\"location\":\"Front\",\"colors\":2}.",
                    "location: Front or Back; colors: 1-8."));
            }

            try
            {
                var breakdown = QuoteEngine.Quote(new QuoteRequest(garment, quantity, locations, OptBool(args, "rush")));
                return Task.FromResult(ToolResult.Success(new
                {
                    garment = garment.ToString(),
                    quantity,
                    rush = OptBool(args, "rush"),
                    subtotal = breakdown.Subtotal,
                    gst = breakdown.Gst,
                    total_incl_gst = breakdown.Total,
                    turnaround_days = breakdown.TurnaroundDays,
                    screen_count = breakdown.ScreenCount,
                    breakdown = breakdown.ToText(),
                }));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ToolResult.Fail(ex.Message,
                    "Adjust quantity (min 1), locations (max 2) and colours (1-8), then retry."));
            }
        },
    };

    private static ToolDefinition GetToday() => new()
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
    };

    private static List<PrintLocation>? ParseLocations(JsonObject args)
    {
        if (args["locations"] is not JsonArray array || array.Count == 0) return null;
        var result = new List<PrintLocation>();
        foreach (var item in array)
        {
            if (item is not JsonObject obj) return null;
            var location = OptString(obj, "location");
            var colors = ArgInt(obj, "colors");
            if (string.IsNullOrWhiteSpace(location) || colors is < 1 or > 8) return null;
            result.Add(new PrintLocation(location, colors));
        }
        return result;
    }

    // --- small helpers (same style as the pattern 1 tools) ---------------

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

    private static bool OptBool(JsonObject args, string name) =>
        args[name] is { } node && node.GetValueKind() == System.Text.Json.JsonValueKind.True;

    private static int ArgInt(JsonObject args, string name)
    {
        if (args[name] is not { } node) return 0;
        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number when node is System.Text.Json.Nodes.JsonValue num
                && num.TryGetValue<double>(out var d) => (int)Math.Round(d),
            System.Text.Json.JsonValueKind.String when int.TryParse(node.GetValue<string>(), out var i) => i,
            _ => 0,
        };
    }
}
