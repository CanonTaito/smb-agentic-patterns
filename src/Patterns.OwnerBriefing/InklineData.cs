using System.Globalization;
using System.Text;
using Agentic.AgentCore.Data;

namespace Agentic.Patterns.OwnerBriefing;

// The two flat-file feeds the owner briefing is built from. Both are parsed
// and aggregated in code: the LLM may narrate these numbers but never computes
// them, which is what makes the eval's ground truth meaningful.

public sealed record SalesWeek(
    string WeekStart,
    int OrdersReceived,
    int OrdersShipped,
    decimal RevenueAud,
    double AvgTurnaroundDays,
    int QuotesSent);

public sealed record Review(
    string ReviewId,
    string Customer,
    string ReviewDate,
    int Rating,
    string Comment);

public sealed record WeekKpi(
    string WeekStart,
    int OrdersReceived,
    int OrdersShipped,
    decimal RevenueAud,
    double AvgTurnaroundDays,
    int QuotesSent);

public sealed record SalesKpis(
    int WindowWeeks,
    WeekKpi LastWeek,
    WeekKpi? PriorWeek,
    decimal WowRevenuePct,
    decimal WindowAvgRevenue,
    decimal WindowTotalRevenue,
    double WindowAvgTurnaround,
    int WindowOrdersReceived,
    int WindowOrdersShipped,
    int WindowQuotesSent,
    IReadOnlyList<WeekKpi> Series);

public sealed record ReviewStats(
    int Count,
    double AvgRating,
    int LowRatingCount,
    IReadOnlyDictionary<int, int> ByRating);

public static class InklineData
{
    public static IReadOnlyList<SalesWeek> LoadSales(string? path = null)
    {
        path ??= RepoPaths.SalesCsvPath;
        return ParseCsv(path)
            .Select(f => new SalesWeek(
                WeekStart: f[0],
                OrdersReceived: ParseInt(f[1]),
                OrdersShipped: ParseInt(f[2]),
                RevenueAud: decimal.Parse(f[3], CultureInfo.InvariantCulture),
                AvgTurnaroundDays: double.Parse(f[4], CultureInfo.InvariantCulture),
                QuotesSent: ParseInt(f[5])))
            .OrderBy(w => w.WeekStart, StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<Review> LoadReviews(string? path = null)
    {
        path ??= RepoPaths.ReviewsCsvPath;
        return ParseCsv(path)
            .Select(f => new Review(
                ReviewId: f[0],
                Customer: f[1],
                ReviewDate: f[2],
                Rating: ParseInt(f[3]),
                Comment: f[4]))
            .ToList();
    }

    /// <summary>
    /// Weekly KPIs, computed from sales.csv in code. The last row is the week
    /// just ended; the window (default 4 weeks) drives the averages.
    /// </summary>
    public static SalesKpis ComputeKpis(IReadOnlyList<SalesWeek> sales, int windowWeeks = 4)
    {
        if (sales.Count == 0)
            throw new InvalidOperationException("sales.csv has no data rows.");

        var window = sales.TakeLast(Math.Clamp(windowWeeks, 1, sales.Count)).Select(ToKpi).ToList();
        var last = window[^1];
        var prior = window.Count > 1 ? window[^2] : null;

        var wow = prior is null || prior.RevenueAud == 0
            ? 0m
            : Math.Round((last.RevenueAud - prior.RevenueAud) / prior.RevenueAud * 100m, 1);

        return new SalesKpis(
            WindowWeeks: window.Count,
            LastWeek: last,
            PriorWeek: prior,
            WowRevenuePct: wow,
            WindowAvgRevenue: Math.Round(window.Average(w => w.RevenueAud), 2),
            WindowTotalRevenue: window.Sum(w => w.RevenueAud),
            WindowAvgTurnaround: Math.Round(window.Average(w => w.AvgTurnaroundDays), 2),
            WindowOrdersReceived: window.Sum(w => w.OrdersReceived),
            WindowOrdersShipped: window.Sum(w => w.OrdersShipped),
            WindowQuotesSent: window.Sum(w => w.QuotesSent),
            Series: sales.Select(ToKpi).ToList());
    }

    public static ReviewStats ComputeReviewStats(IReadOnlyList<Review> reviews)
    {
        if (reviews.Count == 0)
            return new ReviewStats(0, 0, 0, new Dictionary<int, int>());

        var byRating = reviews.GroupBy(r => r.Rating)
            .OrderByDescending(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        return new ReviewStats(
            Count: reviews.Count,
            AvgRating: Math.Round(reviews.Average(r => r.Rating), 2),
            LowRatingCount: reviews.Count(r => r.Rating <= 3),
            ByRating: byRating);
    }

    private static WeekKpi ToKpi(SalesWeek w) => new(
        w.WeekStart, w.OrdersReceived, w.OrdersShipped, w.RevenueAud, w.AvgTurnaroundDays, w.QuotesSent);

    private static int ParseInt(string s) => int.Parse(s.Trim(), CultureInfo.InvariantCulture);

    private static IEnumerable<string[]> ParseCsv(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Data file not found: {path}", path);

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return SplitCsvLine(line.TrimStart('\uFEFF'));
        }
    }

    // Minimal RFC-4180-ish splitter: the review comments contain commas and
    // quoted text, so a naive string.Split would corrupt every other row.
    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
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
}
