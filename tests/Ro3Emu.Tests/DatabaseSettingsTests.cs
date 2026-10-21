using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Tests;

/// <summary>The durability model the hardening plan states: WAL journal, synchronous FULL on every pooled connection.</summary>
public sealed class DatabaseSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-db-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void StoreOpensWalWithFullSync()
    {
        using var store = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));
        for (int i = 0; i < 3; i++) // fresh and pooled connections alike
        {
            using var db = store.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", (string)cmd.ExecuteScalar()!);
            cmd.CommandText = "PRAGMA synchronous;";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!); // 2 = FULL
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
