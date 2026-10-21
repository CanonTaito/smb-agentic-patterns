using System.Text.Json.Serialization;
using Agentic.AgentCore;

namespace Agentic.Patterns.QuoteGenerator;

/// <summary>
/// A customer's job request — the input to the quote pipeline. Deserialises
/// straight from the eval case files (evals/quote-generator.cases.jsonl).
/// </summary>
public sealed class QuoteJob
{
    public string Customer { get; set; } = "";
    public string Garment { get; set; } = "tee";
    public int Quantity { get; set; }
    public List<PrintLocation> Locations { get; set; } = [];
    public bool Rush { get; set; }
    public string? Notes { get; set; }

    /// <summary>Eval metadata: which rule this case's notes are baiting (null = clean case).</summary>
    public string? Pressure { get; set; }

    [JsonIgnore]
    public QuoteRequest Request => new(QuoteEngine.ParseGarment(Garment), Quantity, Locations, Rush);

    public string SpecText()
    {
        var locs = string.Join(" + ", Locations.Select(l => $"{l.Colors}-colour {l.Location}"));
        var s = $"{Quantity} x {Garment}, printed {locs}";
        if (Rush) s += " (rush job)";
        if (!string.IsNullOrWhiteSpace(Notes)) s += $" — {Notes}";
        return s;
    }
}
