using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

/// <summary>
/// The economy ledger read on databases older than the tables it reads: missing tables and columns are reported as
/// issues, found by checking the schema rather than by SQLite's error text.
/// </summary>
public sealed class EconomySchemaTests : IDisposable
{
    private static readonly EconomyCatalog Catalog =
        new(new Dictionary<long, EconomyAssetKind> { [1] = EconomyAssetKind.Currency });
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ro3emu-economy-schema-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    private void Create(string sql)
    {
        using var db = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void ADatabaseWithoutTheEconomyTablesHasNoLedgerCoverage()
    {
        Create("CREATE TABLE role(id INTEGER PRIMARY KEY)");
        var window = EconomyLedgerReader.Read(_path, Catalog, "auto", 0, 0);
        Assert.Contains(window.Issues, i => i.Kind == EconomyIssueKind.Coverage && i.Key == "ledger/schema");
    }

    [Fact]
    public void AReceiptTableWithoutAColumnTheLedgerReadsIsAnUnknownSchema()
    {
        Create("""
            CREATE TABLE economy_meta(singleton INTEGER PRIMARY KEY, realm_id TEXT, next_sequence INTEGER,
                retained_floor INTEGER);
            INSERT INTO economy_meta VALUES(1, 'realm', 0, 0);
            CREATE TABLE operation_receipt(operation_id TEXT PRIMARY KEY, commit_seq INTEGER);
            CREATE TABLE economy_entry(entry_id INTEGER PRIMARY KEY, commit_seq INTEGER, operation_id TEXT);
            CREATE TABLE operation_source(realm_id TEXT, source_key TEXT, operation_id TEXT);
            """);
        var window = EconomyLedgerReader.Read(_path, Catalog, "realm", 0, 0);
        Assert.Contains(window.Issues, i => i.Kind == EconomyIssueKind.UnknownSchema && i.Key == "ledger/schema");
    }
}
