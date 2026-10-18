using Agentic.AgentCore;
using Xunit;

namespace Agentic.AgentCore.Tests;

public class QuoteEngineTests
{
    [Fact]
    public void Quote_100Tees_FrontTwoColour_MatchesHandCalculation()
    {
        var req = new QuoteRequest(GarmentType.Tee, 100,
            [new PrintLocation("Front", 2)]);

        var q = QuoteEngine.Quote(req);

        // 100 x $7.20 garment + 2 screens x $30 + 2 colour-locations x $1.60 x 100
        Assert.Equal(720.00m, q.Lines[0].Amount);
        Assert.Equal(60.00m, q.Lines[1].Amount);
        Assert.Equal(320.00m, q.Lines[2].Amount);
        Assert.Equal(1100.00m, q.Subtotal);
        Assert.Equal(110.00m, q.Gst);
        Assert.Equal(1210.00m, q.Total);
        Assert.Equal(2, q.ScreenCount);
    }

    [Fact]
    public void Quote_Rush_Adds25PercentAndCutsTurnaround()
    {
        var baseReq = new QuoteRequest(GarmentType.Tee, 100, [new PrintLocation("Front", 2)]);
        var rushReq = baseReq with { Rush = true };

        var baseQ = QuoteEngine.Quote(baseReq);
        var rushQ = QuoteEngine.Quote(rushReq);

        Assert.Equal(baseQ.Subtotal * 1.25m, rushQ.Subtotal);
        Assert.True(rushQ.TurnaroundDays < baseQ.TurnaroundDays);
        Assert.Equal(5, rushQ.TurnaroundDays); // baseline 8 * 0.6 rounded up
    }

    [Fact]
    public void UnitPrice_TierBoundaries_UseHigherTier()
    {
        Assert.Equal(12.50m, QuoteEngine.UnitPriceFor(GarmentType.Tee, 24));
        Assert.Equal(9.80m, QuoteEngine.UnitPriceFor(GarmentType.Tee, 25));
        Assert.Equal(6.50m, QuoteEngine.UnitPriceFor(GarmentType.Tee, 300));
    }

    [Fact]
    public void Quote_TwoLocations_SumsScreensAcrossLocations()
    {
        var q = QuoteEngine.Quote(new QuoteRequest(GarmentType.Hoodie, 50,
            [new PrintLocation("Front", 2), new PrintLocation("Back", 1)]));

        Assert.Equal(3, q.ScreenCount);
        Assert.Equal(90.00m, q.Lines[1].Amount); // 3 screens x $30
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Quote_InvalidQuantity_Throws(int qty) =>
        Assert.Throws<ArgumentException>(() =>
            QuoteEngine.Quote(new QuoteRequest(GarmentType.Tee, qty, [new PrintLocation("Front", 1)])));

    [Fact]
    public void Quote_NoLocations_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            QuoteEngine.Quote(new QuoteRequest(GarmentType.Tee, 10, [])));

    [Theory]
    [InlineData("tee", GarmentType.Tee)]
    [InlineData("Hoodies", GarmentType.Hoodie)]
    [InlineData("tote bag", GarmentType.Tote)]
    public void ParseGarment_AcceptsCommonNames(string input, GarmentType expected) =>
        Assert.Equal(expected, QuoteEngine.ParseGarment(input));

    [Fact]
    public void ParseGarment_Unknown_Throws() =>
        Assert.Throws<ArgumentException>(() => QuoteEngine.ParseGarment("jacket"));

    [Fact]
    public void Turnaround_Baseline_ClampsBetween8And15()
    {
        Assert.Equal(8, QuoteEngine.TurnaroundDays(10, rush: false));
        Assert.Equal(15, QuoteEngine.TurnaroundDays(500, rush: false));
    }
}
