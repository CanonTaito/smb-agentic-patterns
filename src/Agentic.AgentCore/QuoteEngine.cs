namespace Agentic.AgentCore;

// Deterministic quote maths for Inkline Print Co.
// This is the ground truth the reflection critic verifies LLM drafts against,
// and the pricing tool the routing pattern's sales specialist calls.

public enum GarmentType { Tee, Hoodie, Cap, Tote }

public sealed record PrintLocation(string Location, int Colors); // "Front" / "Back"

public sealed record QuoteRequest(
    GarmentType Garment,
    int Quantity,
    IReadOnlyList<PrintLocation> Locations,
    bool Rush = false,
    string? GarmentSku = null);

public sealed record QuoteLine(string Label, decimal Amount);

public sealed record QuoteBreakdown(
    IReadOnlyList<QuoteLine> Lines,
    decimal Subtotal,
    decimal Gst,
    decimal Total,
    int TurnaroundDays,
    int ScreenCount)
{
    public string ToText()
    {
        var lines = string.Join(Environment.NewLine, Lines.Select(l => $"  {l.Label,-44} ${l.Amount,8:F2}"));
        return $"""
            Line items:
            {lines}
            {"",-44} ${Subtotal,8:F2}   (subtotal)
            {"",-44} ${Gst,8:F2}   (GST 10%)
            {"TOTAL",-44} ${Total,8:F2}
            Turnaround: {TurnaroundDays} business days
            """;
    }
}

public static class QuoteEngine
{
    // AUD, ex-GST garment unit prices by quantity tier.
    private static readonly Dictionary<GarmentType, (int Min, decimal Unit)[]> PriceTiers = new()
    {
        [GarmentType.Tee] = [(1, 12.50m), (25, 9.80m), (50, 8.40m), (100, 7.20m), (250, 6.50m)],
        [GarmentType.Hoodie] = [(1, 32.00m), (25, 27.50m), (50, 24.80m), (100, 22.40m), (250, 20.90m)],
        [GarmentType.Cap] = [(1, 14.00m), (25, 11.50m), (50, 10.20m), (100, 9.10m), (250, 8.40m)],
        [GarmentType.Tote] = [(1, 9.50m), (25, 7.60m), (50, 6.60m), (100, 5.80m), (250, 5.20m)],
    };

    public const decimal ScreenSetupFee = 30.00m;   // per screen (colour x location)
    public const decimal PrintFeePerPiecePerColour = 1.60m; // per garment, per colour, per location
    public const decimal RushSurchargeRate = 0.25m;  // +25% of subtotal
    public const decimal GstRate = 0.10m;
    public const int MaxLocations = 2;

    public static decimal UnitPriceFor(GarmentType garment, int quantity)
    {
        if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity), "quantity must be >= 1");
        var tiers = PriceTiers[garment];
        decimal unit = tiers[0].Unit;
        foreach (var (min, u) in tiers)
            if (quantity >= min) unit = u;
        return unit;
    }

    public static QuoteBreakdown Quote(QuoteRequest request)
    {
        if (request.Quantity < 1)
            throw new ArgumentException("Quantity must be at least 1.");
        if (request.Locations is not { Count: > 0 })
            throw new ArgumentException("At least one print location is required.");
        if (request.Locations.Count > MaxLocations)
            throw new ArgumentException($"At most {MaxLocations} print locations are supported.");
        foreach (var loc in request.Locations)
        {
            if (string.IsNullOrWhiteSpace(loc.Location))
                throw new ArgumentException("Each print location needs a name (e.g. Front, Back).");
            if (loc.Colors is < 1 or > 8)
                throw new ArgumentException($"Colours for '{loc.Location}' must be between 1 and 8.");
        }

        var unit = UnitPriceFor(request.Garment, request.Quantity);
        var garmentTotal = Math.Round(unit * request.Quantity, 2);
        var totalColors = request.Locations.Sum(l => l.Colors);
        var screenCount = totalColors; // one screen per colour per location
        var setupTotal = screenCount * ScreenSetupFee;
        var printTotal = Math.Round(PrintFeePerPiecePerColour * totalColors * request.Quantity, 2);

        var lines = new List<QuoteLine>
        {
            new($"{request.Garment} x {request.Quantity} @ ${unit:F2}/unit", garmentTotal),
            new($"Screen setup: {screenCount} screens @ ${ScreenSetupFee:F2}", setupTotal),
            new($"Printing: {totalColors} colour-location(s) x {request.Quantity} pcs @ ${PrintFeePerPiecePerColour:F2}", printTotal),
        };

        var subtotal = garmentTotal + setupTotal + printTotal;
        if (request.Rush)
        {
            var rush = Math.Round(subtotal * RushSurchargeRate, 2);
            lines.Add(new($"Rush surcharge ({RushSurchargeRate:P0} of subtotal)", rush));
            subtotal += rush;
        }

        var gst = Math.Round(subtotal * GstRate, 2);
        var turnaround = TurnaroundDays(request.Quantity, request.Rush);

        return new QuoteBreakdown(lines, Math.Round(subtotal, 2), gst, Math.Round(subtotal + gst, 2), turnaround, screenCount);
    }

    public static int TurnaroundDays(int quantity, bool rush)
    {
        var baseline = Math.Clamp(7 + (quantity / 50), 8, 15);
        return rush ? Math.Max(4, (int)Math.Round(baseline * 0.6)) : baseline;
    }

    public static GarmentType ParseGarment(string s) => s.Trim().ToLowerInvariant() switch
    {
        "tee" or "tees" or "t-shirt" or "tshirts" or "shirt" or "shirts" => GarmentType.Tee,
        "hoodie" or "hoodies" or "sweatshirt" => GarmentType.Hoodie,
        "cap" or "caps" or "hat" or "hats" => GarmentType.Cap,
        "tote" or "totes" or "tote bag" or "totebags" or "bag" or "bags" => GarmentType.Tote,
        _ => throw new ArgumentException($"Unknown garment type '{s}'. Use tee, hoodie, cap, or tote."),
    };
}
