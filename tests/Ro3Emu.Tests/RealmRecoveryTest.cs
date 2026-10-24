using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Tests;

/// <summary>Waits until a forcibly terminated server's realm can be reopened on Windows.</summary>
internal static class RealmRecoveryTest
{
    public static async Task WaitAsync(string dataDir)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                using var held = RealmLock.Acquire(dataDir);
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(dataDir, "ro3emu.db"),
                    Mode = SqliteOpenMode.ReadWrite,
                    Pooling = false,
                }.ToString());
                db.Open();
                using var integrity = db.CreateCommand();
                integrity.CommandText = "PRAGMA integrity_check";
                Assert.Equal("ok", integrity.ExecuteScalar());
                return;
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 10 && DateTime.UtcNow < deadline)
            {
                // Windows can finish releasing the killed process's WAL mapping after WaitForExitAsync returns.
                await Task.Delay(50);
            }
            catch (InvalidOperationException error) when (error.InnerException is IOException && DateTime.UtcNow < deadline)
            {
                // The killed server's exclusive realm-lock handle can outlive the process briefly on Windows.
                await Task.Delay(50);
            }
        }
    }
}
