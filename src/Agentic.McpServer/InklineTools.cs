using System.ComponentModel;
using System.Text.Json;
using Agentic.AgentCore;
using Agentic.AgentCore.Data;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class InklineTools
{
    private static readonly Lazy<InklineDb> Db = new(() =>
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AgenticPatterns.slnx")))
            dir = dir.Parent;
        var root = dir?.FullName ?? Directory.GetCurrentDirectory();
        return new InklineDb(Path.Combine(root, "data", "business.db"));
    });

    [McpServerTool(Name = "price_quote"), Description("Get an Inkline Print Co. quote for a garment order. Uses the same QuoteEngine as the agents.")]
    public static string PriceQuote(
        [Description("Garment type: tee, hoodie, polo, cap, tote")] string garment,
        [Description("Quantity")] int quantity,
        [Description("Front print colors")] int frontColors = 1,
        [Description("Back print colors")] int backColors = 0,
        [Description("Rush order")] bool rush = false)
    {
        var garmentType = QuoteEngine.ParseGarment(garment);
        var locations = new List<PrintLocation>();
        if (frontColors > 0) locations.Add(new PrintLocation("Front", frontColors));
        if (backColors > 0) locations.Add(new PrintLocation("Back", backColors));
        if (locations.Count == 0) locations.Add(new PrintLocation("Front", 1));

        var req = new QuoteRequest(garmentType, quantity, locations, rush);
        var quote = QuoteEngine.Quote(req);
        return JsonSerializer.Serialize(new
        {
            garment = garmentType.ToString().ToLowerInvariant(),
            quantity,
            locations = locations.Select(l => new { location = l.Location, colors = l.Colors }),
            rush,
            subtotal = quote.Subtotal,
            gst = quote.Gst,
            total = quote.Total,
            turnaroundDays = quote.TurnaroundDays,
            currency = "AUD"
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Name = "lookup_job"), Description("Look up an Inkline Print Co. job by job number (e.g. 1001).")]
    public static string LookupJob(
        [Description("Job number like 1001")] string jobNumber)
    {
        var job = Db.Value.GetJob(jobNumber);
        if (job == null) return JsonSerializer.Serialize(new { found = false, jobNumber });
        return JsonSerializer.Serialize(new
        {
            found = true,
            jobNumber = job.JobNumber,
            customerId = job.CustomerId,
            customerName = job.CustomerName,
            description = job.Description,
            garmentType = job.GarmentType,
            quantity = job.Quantity,
            printSpec = job.PrintSpec,
            status = job.Status,
            quotedTotal = job.QuotedTotal,
            dueDate = job.DueDate,
            rush = job.Rush,
            createdAt = job.CreatedAt
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Name = "list_at_risk_jobs"), Description("List jobs at risk of being late based on today's seeded date (2026-10-18).")]
    public static string ListAtRiskJobs()
    {
        var jobs = Db.Value.ListAtRiskJobs(DataSeeder.SeedDate);
        return JsonSerializer.Serialize(new
        {
            asOf = DataSeeder.SeedDate,
            count = jobs.Count,
            jobs = jobs.Select(j => new
            {
                jobNumber = j.JobNumber,
                customerId = j.CustomerId,
                customerName = j.CustomerName,
                status = j.Status,
                garmentType = j.GarmentType,
                quantity = j.Quantity,
                dueDate = j.DueDate,
                daysUntilDue = (DateOnly.Parse(j.DueDate).DayNumber - DateOnly.Parse(DataSeeder.SeedDate).DayNumber),
                quotedTotal = j.QuotedTotal,
                rush = j.Rush
            })
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool(Name = "get_sales_kpis"), Description("Get Inkline sales KPIs for a window of weeks from sales.csv.")]
    public static string GetSalesKpis(
        [Description("Number of weeks to include in the rolling window (default 4)")] int windowWeeks = 4)
    {
        var sales = Agentic.Patterns.OwnerBriefing.InklineData.LoadSales();
        var kpis = Agentic.Patterns.OwnerBriefing.InklineData.ComputeKpis(sales, windowWeeks);
        return JsonSerializer.Serialize(new
        {
            windowWeeks = kpis.WindowWeeks,
            lastWeek = new { weekStart = kpis.LastWeek.WeekStart, revenueAud = kpis.LastWeek.RevenueAud, ordersReceived = kpis.LastWeek.OrdersReceived, ordersShipped = kpis.LastWeek.OrdersShipped, quotesSent = kpis.LastWeek.QuotesSent, avgTurnaroundDays = kpis.LastWeek.AvgTurnaroundDays },
            priorWeek = kpis.PriorWeek == null ? null : new { weekStart = kpis.PriorWeek.WeekStart, revenueAud = kpis.PriorWeek.RevenueAud },
            wowRevenuePct = kpis.WowRevenuePct,
            windowAvgRevenue = kpis.WindowAvgRevenue,
            windowTotalRevenue = kpis.WindowTotalRevenue,
            windowAvgTurnaround = kpis.WindowAvgTurnaround
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
