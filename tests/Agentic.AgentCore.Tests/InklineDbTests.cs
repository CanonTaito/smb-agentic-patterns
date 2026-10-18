using Agentic.AgentCore.Data;
using Xunit;

namespace Agentic.AgentCore.Tests;

public class InklineDbTests : IDisposable
{
    private readonly string _tempDir;

    public InklineDbTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "inkline-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private InklineDb OpenDb() => new(Path.Combine(_tempDir, "business.db"));

    [Fact]
    public void Seeder_CreatesDatabaseOnFirstOpen()
    {
        using var db = OpenDb();
        var customers = db.CheckBlankStock(); // any query proves the schema exists
        Assert.NotEmpty(customers);
    }

    [Fact]
    public void GetJob_ReturnsJobWithCustomerName()
    {
        using var db = OpenDb();
        var job = db.GetJob("1002");

        Assert.NotNull(job);
        Assert.Equal("Northside Cafe", job.CustomerName);
        Assert.Equal("in_production", job.Status);
    }

    [Fact]
    public void GetJob_Unknown_ReturnsNull()
    {
        using var db = OpenDb();
        Assert.Null(db.GetJob("9999"));
    }

    [Fact]
    public void ListAtRiskJobs_FindsDueSoonUnshippedJobs()
    {
        using var db = OpenDb();
        var atRisk = db.ListAtRiskJobs(DataSeeder.SeedDate);

        // 1002 (due 17 Oct, in_production), 1010 (24 Oct), 1011 (21 Oct) etc. — but not shipped ones
        Assert.Contains(atRisk, j => j.JobNumber == "1002");
        Assert.Contains(atRisk, j => j.JobNumber == "1010");
        Assert.DoesNotContain(atRisk, j => j.JobNumber == "1001"); // shipped
    }

    [Fact]
    public void CheckBlankStock_FiltersByGarmentAndSize()
    {
        using var db = OpenDb();
        var teeM = db.CheckBlankStock("Tee", "M");

        var row = Assert.Single(teeM);
        Assert.Equal(15, row.Qty); // deliberately low for the demo
        Assert.True(row.Qty < row.ReorderLevel);
    }

    [Fact]
    public void CheckInk_FindsLowNavy()
    {
        using var db = OpenDb();
        var inks = db.CheckInk("Navy");

        var navy = Assert.Single(inks);
        Assert.Equal(450, navy.MlRemaining);
    }

    [Fact]
    public void GetProof_ReturnsLatestVersion()
    {
        using var db = OpenDb();
        var proofs = db.GetProofs("1012");

        var proof = Assert.Single(proofs);
        Assert.Equal("revision_requested", proof.Status);
        Assert.Null(proof.ApprovedAt);
    }

    [Fact]
    public void ListInvoices_FiltersByStatus()
    {
        using var db = OpenDb();
        var overdue = db.ListInvoices(status: "overdue");

        var invoice = Assert.Single(overdue);
        Assert.Equal("INV-3101", invoice.Id);
        Assert.Equal("Summit Accounting", invoice.CustomerName);
    }
}
