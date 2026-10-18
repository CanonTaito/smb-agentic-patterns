using Microsoft.Data.Sqlite;

namespace Agentic.AgentCore.Data;

public sealed record CustomerRecord(
    string Id, string Name, string ContactName, string Email, string Abn,
    int PaymentTermsDays, bool IsNewAccount, string Notes);

public sealed record JobRecord(
    int Id, string JobNumber, string CustomerId, string CustomerName, string Description,
    string GarmentType, int Quantity, string PrintSpec, string Status,
    decimal QuotedTotal, string DueDate, bool Rush, string CreatedAt);

public sealed record InvoiceRecord(
    string Id, string JobNumber, string CustomerId, string CustomerName,
    decimal Amount, string Status, string DueDate);

public sealed record BlankStockRecord(string GarmentType, string Size, int Qty, int ReorderLevel);

public sealed record InkRecord(string Colour, double MlRemaining);

public sealed record ProofRecord(string Id, string JobNumber, int Version, string Status, string SentAt, string? ApprovedAt);

/// <summary>
/// Thin data access over the seeded SQLite business database. Tools call
/// these methods; results are serialised straight into tool responses.
/// </summary>
public sealed class InklineDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public InklineDb(string? dbPath = null)
    {
        dbPath ??= RepoPaths.BusinessDbPath;
        DataSeeder.EnsureSeeded(dbPath);
        // Pooling=false so disposing the connection really releases the file
        // handle (tests delete their temp databases on teardown).
        _conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        _conn.Open();
    }

    public CustomerRecord? GetCustomer(string id) =>
        QuerySingleOrDefault("""
            SELECT id, name, contact_name, email, abn, payment_terms_days, is_new_account, notes
            FROM customers WHERE id = @id OR name LIKE @name
            """,
        r => new CustomerRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetString(4), r.GetInt32(5), r.GetInt32(6) == 1, r.GetString(7)),
        ("@id", id), ("@name", $"%{id}%"));

    public JobRecord? GetJob(string jobNumber) =>
        QuerySingleOrDefault("""
            SELECT j.id, j.job_number, j.customer_id, c.name, j.description, j.garment_type,
                   j.quantity, j.print_spec, j.status, j.quoted_total, j.due_date, j.rush, j.created_at
            FROM jobs j JOIN customers c ON c.id = j.customer_id
            WHERE j.job_number = @n
            """,
        r => new JobRecord(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetDecimal(9),
            r.GetString(10), r.GetInt32(11) == 1, r.GetString(12)),
        ("@n", jobNumber));

    public IReadOnlyList<JobRecord> ListJobs(string? status = null, string? customerId = null) =>
        QueryList("""
            SELECT j.id, j.job_number, j.customer_id, c.name, j.description, j.garment_type,
                   j.quantity, j.print_spec, j.status, j.quoted_total, j.due_date, j.rush, j.created_at
            FROM jobs j JOIN customers c ON c.id = j.customer_id
            WHERE (@status IS NULL OR j.status = @status)
              AND (@cust IS NULL OR j.customer_id = @cust)
            ORDER BY j.due_date
            """,
        r => new JobRecord(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetDecimal(9),
            r.GetString(10), r.GetInt32(11) == 1, r.GetString(12)),
        ("@status", status), ("@cust", customerId));

    public IReadOnlyList<JobRecord> ListAtRiskJobs(string today) =>
        QueryList("""
            SELECT j.id, j.job_number, j.customer_id, c.name, j.description, j.garment_type,
                   j.quantity, j.print_spec, j.status, j.quoted_total, j.due_date, j.rush, j.created_at
            FROM jobs j JOIN customers c ON c.id = j.customer_id
            WHERE j.due_date <= date(@today, '+7 days')
              AND j.status NOT IN ('shipped', 'complete', 'cancelled')
            ORDER BY j.due_date
            """,
        r => new JobRecord(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.GetString(5), r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetDecimal(9),
            r.GetString(10), r.GetInt32(11) == 1, r.GetString(12)),
        ("@today", today));

    public IReadOnlyList<BlankStockRecord> CheckBlankStock(string? garmentType = null, string? size = null) =>
        QueryList("""
            SELECT garment_type, size, qty, reorder_level
            FROM inventory_blanks
            WHERE (@g IS NULL OR garment_type = @g)
              AND (@s IS NULL OR size = @s)
            ORDER BY garment_type, size
            """,
        r => new BlankStockRecord(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)),
        ("@g", garmentType), ("@s", size));

    public IReadOnlyList<InkRecord> CheckInk(string? colour = null) =>
        QueryList("""
            SELECT colour, ml_remaining FROM inks
            WHERE (@c IS NULL OR colour LIKE @c)
            ORDER BY colour
            """,
        r => new InkRecord(r.GetString(0), r.GetDouble(1)),
        ("@c", colour is null ? null : $"%{colour}%"));

    public IReadOnlyList<ProofRecord> GetProofs(string jobNumber) =>
        QueryList("""
            SELECT id, job_number, version, status, sent_at, approved_at
            FROM artwork_proofs WHERE job_number = @n ORDER BY version
            """,
        r => new ProofRecord(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3),
            r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)),
        ("@n", jobNumber));

    public IReadOnlyList<InvoiceRecord> ListInvoices(string? customerId = null, string? status = null) =>
        QueryList("""
            SELECT i.id, i.job_number, i.customer_id, c.name, i.amount, i.status, i.due_date
            FROM invoices i JOIN customers c ON c.id = i.customer_id
            WHERE (@cust IS NULL OR i.customer_id = @cust)
              AND (@status IS NULL OR i.status = @status)
            ORDER BY i.due_date
            """,
        r => new InvoiceRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetDecimal(4), r.GetString(5), r.GetString(6)),
        ("@cust", customerId), ("@status", status));

    public InvoiceRecord? GetInvoice(string invoiceId) =>
        QuerySingleOrDefault("""
            SELECT i.id, i.job_number, i.customer_id, c.name, i.amount, i.status, i.due_date
            FROM invoices i JOIN customers c ON c.id = i.customer_id
            WHERE i.id = @id
            """,
        r => new InvoiceRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.GetDecimal(4), r.GetString(5), r.GetString(6)),
        ("@id", invoiceId));

    private delegate T MapFunc<T>(SqliteDataReader r);

    private IReadOnlyList<T> QueryList<T>(string sql, MapFunc<T> map, params (string, object?)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var results = new List<T>();
        while (reader.Read()) results.Add(map(reader));
        return results;
    }

    private T? QuerySingleOrDefault<T>(string sql, MapFunc<T> map, params (string, object?)[] args)
        where T : class
    {
        var list = QueryList(sql, map, args);
        return list.Count > 0 ? list[0] : null;
    }

    public void Dispose() => _conn.Dispose();
}
