using Microsoft.Data.Sqlite;

namespace Agentic.AgentCore.Data;

/// <summary>
/// Deterministic seed for the fictional Inkline Print Co. dataset.
/// Recreates business.db from scratch whenever it is missing, so the repo
/// never commits a binary database — only the code that defines it.
/// "Today" in the seeded world is 2026-10-18.
/// </summary>
public static class DataSeeder
{
    public const string SeedDate = "2026-10-18";

    public static void EnsureSeeded(string dbPath)
    {
        if (File.Exists(dbPath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = Schema;
            cmd.ExecuteNonQuery();
        }
        foreach (var insert in Inserts)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = insert;
            cmd.ExecuteNonQuery();
        }
    }

    private const string Schema = """
        CREATE TABLE customers (
          id TEXT PRIMARY KEY, name TEXT NOT NULL, contact_name TEXT NOT NULL,
          email TEXT NOT NULL, abn TEXT NOT NULL, payment_terms_days INTEGER NOT NULL,
          is_new_account INTEGER NOT NULL DEFAULT 0, notes TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE jobs (
          id INTEGER PRIMARY KEY AUTOINCREMENT, job_number TEXT UNIQUE NOT NULL,
          customer_id TEXT NOT NULL REFERENCES customers(id), description TEXT NOT NULL,
          garment_type TEXT NOT NULL, quantity INTEGER NOT NULL, print_spec TEXT NOT NULL,
          status TEXT NOT NULL, quoted_total REAL NOT NULL, due_date TEXT NOT NULL,
          rush INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL
        );
        CREATE TABLE inventory_blanks (
          id TEXT PRIMARY KEY, garment_type TEXT NOT NULL, size TEXT NOT NULL,
          qty INTEGER NOT NULL, reorder_level INTEGER NOT NULL
        );
        CREATE TABLE inks (id TEXT PRIMARY KEY, colour TEXT NOT NULL, ml_remaining REAL NOT NULL);
        CREATE TABLE artwork_proofs (
          id TEXT PRIMARY KEY, job_number TEXT NOT NULL, version INTEGER NOT NULL,
          status TEXT NOT NULL, sent_at TEXT NOT NULL, approved_at TEXT
        );
        CREATE TABLE invoices (
          id TEXT PRIMARY KEY, job_number TEXT NOT NULL, customer_id TEXT NOT NULL REFERENCES customers(id),
          amount REAL NOT NULL, status TEXT NOT NULL, due_date TEXT NOT NULL
        );
        """;

    private static readonly string[] Inserts =
    [
        // ---- customers -----------------------------------------------------
        "INSERT INTO customers VALUES ('C01','Northside Cafe','Priya Raman','orders@northsidecafe.com.au','51822340917',14,0,'Regular corporate client. Brand colours: charcoal + mustard.')",
        "INSERT INTO customers VALUES ('C02','Westlake Crossfit','Jess Thornton','jess@westlakecrossfit.com.au','63119820441',14,0,'Gym wear orders spike before winter and new-member intakes.')",
        "INSERT INTO customers VALUES ('C03','Harbour Dental','Sam Okafor','accounts@harbourdental.com.au','44176503288',14,0,'Pays on time. Prefers clear proof mockups.')",
        "INSERT INTO customers VALUES ('C04','Brisbane Makerspace','Dan Ellis','dan@makerspace.org.au','22804117735',7,0,'Not-for-profit rate. Slow email responses historically.')",
        "INSERT INTO customers VALUES ('C05','Ferny Grove Bakery','Alice Moreau','owner@fernycovebakery.com.au','77403281650',7,0,'Had a colour-mismatch reprint in Oct 2026. Recovered well.')",
        "INSERT INTO customers VALUES ('C06','Summit Accounting','Mel Bright','mel@summitaccounting.com.au','36902741582',14,0,'Order kits for new staff monthly.')",
        "INSERT INTO customers VALUES ('C07','River City Brewing','Tom Nguyen','info@rivercitybrewing.com.au','19504832601',0,1,'New prospect (opening West End). Deposit required on first order.')",
        "INSERT INTO customers VALUES ('C08','Brisbane Fest','Events Team','hello@brisbanefest.org.au','88205149376',0,1,'December community festival. Large tote + tee order under discussion.')",

        // ---- jobs ------------------------------------------------------------
        // status vocabulary: quoted | artwork_pending | proof_approved | in_production | qc | shipped | complete | cancelled
        "INSERT INTO jobs VALUES (1,'1001','C03','Staff polos - dental clinic','Tee',60,'Front 1-colour (navy)','shipped',702.60,'2026-09-20',0,'2026-09-05')",
        "INSERT INTO jobs VALUES (2,'1002','C01','Cafe aprons - charcoal print','Apron',40,'Front 2-colour (mustard + white)','in_production',684.00,'2026-10-17',0,'2026-10-01')",
        "INSERT INTO jobs VALUES (3,'1003','C06','Corporate polos','Tee',25,'Front 1-colour (black)','shipped',342.50,'2026-09-10',0,'2026-08-28')",
        "INSERT INTO jobs VALUES (4,'1004','C05','Bakery tees - spring run','Tee',50,'Front 2-colour (forest + black)','shipped',590.00,'2026-09-29',0,'2026-09-15')",
        "INSERT INTO jobs VALUES (5,'1005','C02','Training tees - new member intake','Tee',120,'Front 2-colour (black + red)','in_production',1102.80,'2026-10-30',0,'2026-10-02')",
        "INSERT INTO jobs VALUES (6,'1006','C03','Dental awareness tees','Tee',40,'Front 1-colour (teal)','shipped',421.60,'2026-09-26',0,'2026-09-12')",
        "INSERT INTO jobs VALUES (7,'1007','C04','Membership hoodies batch 3','Hoodie',35,'Left chest 1-colour (white)','artwork_pending',1013.30,'2026-11-06',0,'2026-10-06')",
        "INSERT INTO jobs VALUES (8,'1008','C05','Bakery staff tees reorder','Tee',60,'Front 1-colour (black)','shipped',638.40,'2026-10-09',0,'2026-09-25')",
        "INSERT INTO jobs VALUES (9,'1009','C02','Winter program hoodies','Hoodie',80,'Front 2-colour (black + white)','proof_approved',2044.00,'2026-11-13',0,'2026-10-08')",
        "INSERT INTO jobs VALUES (10,'1010','C05','Promo caps','Cap',30,'Front 1-colour (forest)','in_production',376.50,'2026-10-24',0,'2026-10-07')",
        "INSERT INTO jobs VALUES (11,'1011','C04','Market day totes','Tote',20,'Front 1-colour (black)','qc',173.20,'2026-10-21',0,'2026-10-09')",
        "INSERT INTO jobs VALUES (12,'1012','C01','Cafe relaunch tees','Tee',100,'Front 2-colour (charcoal + mustard)','artwork_pending',864.00,'2026-11-03',0,'2026-10-11')",

        // ---- blanks ----------------------------------------------------------
        "INSERT INTO inventory_blanks VALUES ('B01','Tee','S',80,40)",
        "INSERT INTO inventory_blanks VALUES ('B02','Tee','M',15,40)",
        "INSERT INTO inventory_blanks VALUES ('B03','Tee','L',95,40)",
        "INSERT INTO inventory_blanks VALUES ('B04','Tee','XL',60,30)",
        "INSERT INTO inventory_blanks VALUES ('B05','Tee','XXL',25,15)",
        "INSERT INTO inventory_blanks VALUES ('B06','Hoodie','S',30,20)",
        "INSERT INTO inventory_blanks VALUES ('B07','Hoodie','M',45,20)",
        "INSERT INTO inventory_blanks VALUES ('B08','Hoodie','L',50,20)",
        "INSERT INTO inventory_blanks VALUES ('B09','Hoodie','XL',22,15)",
        "INSERT INTO inventory_blanks VALUES ('B10','Cap','OneSize',60,25)",
        "INSERT INTO inventory_blanks VALUES ('B11','Tote','OneSize',140,50)",

        // ---- inks ------------------------------------------------------------
        "INSERT INTO inks VALUES ('I1','White',6000)",
        "INSERT INTO inks VALUES ('I2','Black',4500)",
        "INSERT INTO inks VALUES ('I3','Navy',450)",
        "INSERT INTO inks VALUES ('I4','Red',2200)",
        "INSERT INTO inks VALUES ('I5','Forest Green',1800)",
        "INSERT INTO inks VALUES ('I6','Mustard',1350)",
        "INSERT INTO inks VALUES ('I7','Charcoal',2400)",
        "INSERT INTO inks VALUES ('I8','Teal',900)",

        // ---- artwork proofs ---------------------------------------------------
        "INSERT INTO artwork_proofs VALUES ('P1001','1001',1,'approved','2026-09-07','2026-09-08')",
        "INSERT INTO artwork_proofs VALUES ('P1002','1002',1,'approved','2026-10-02','2026-10-03')",
        "INSERT INTO artwork_proofs VALUES ('P1004','1004',1,'approved','2026-09-16','2026-09-17')",
        "INSERT INTO artwork_proofs VALUES ('P1005','1005',1,'approved','2026-10-03','2026-10-04')",
        "INSERT INTO artwork_proofs VALUES ('P1007','1007',1,'revision_requested','2026-10-07',NULL)",
        "INSERT INTO artwork_proofs VALUES ('P1009','1009',1,'approved','2026-10-14','2026-10-18')",
        "INSERT INTO artwork_proofs VALUES ('P1010','1010',1,'approved','2026-10-08','2026-10-09')",
        "INSERT INTO artwork_proofs VALUES ('P1011','1011',1,'approved','2026-10-10','2026-10-11')",
        "INSERT INTO artwork_proofs VALUES ('P1012','1012',1,'revision_requested','2026-10-12',NULL)",

        // ---- invoices ---------------------------------------------------------
        "INSERT INTO invoices VALUES ('INV-3098','1001','C03',1240.00,'disputed','2026-10-04')",
        "INSERT INTO invoices VALUES ('INV-3101','1003','C06',1890.00,'overdue','2026-09-30')",
        "INSERT INTO invoices VALUES ('INV-3102','1006','C03',421.60,'paid','2026-10-26')",
        "INSERT INTO invoices VALUES ('INV-3103','1004','C05',590.00,'unpaid','2026-10-23')",
        "INSERT INTO invoices VALUES ('INV-3104','1008','C05',638.40,'unpaid','2026-10-30')",
        "INSERT INTO invoices VALUES ('INV-3105','1011','C04',173.20,'unpaid','2026-10-30')",
    ];
}
