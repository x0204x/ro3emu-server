using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Tests;

/// <summary>
/// One identity per account name: a spelling that differs only in letter case is the same account (it needs that
/// account's password and gets its stored name), never a second account that inherits the first one's GM rights.
/// </summary>
public sealed class AccountIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-ident-" + Guid.NewGuid().ToString("N"));

    private AccountStore Store() => new(Options.Create(new ServerOptions { DataDir = _dir }));

    [Fact]
    public async Task CaseVariantIsTheSameAccount()
    {
        using var store = Store();
        Assert.Equal(new AccountStore.SignIn(AccountStore.LoginResult.Registered, "admin"), await store.SignInAsync("admin", "secret"));
        Assert.Equal(new AccountStore.SignIn(AccountStore.LoginResult.BadPassword, "admin"), await store.SignInAsync("Admin", "other"));
        Assert.Equal(new AccountStore.SignIn(AccountStore.LoginResult.Ok, "admin"), await store.SignInAsync("ADMIN", "secret"));
        Assert.Equal("admin", await store.VerifiedAccountAsync("aDmIn", "secret"));
        Assert.Null(await store.VerifiedAccountAsync("Admin", "other"));
        Assert.Equal(1L, Count(store));
    }

    [Fact]
    public async Task LegacyCaseCollisionsStaySeparateAndOnlyTheListedSpellingIsGm()
    {
        using (var store = Store()) await store.SignInAsync("admin", "secret");
        SqliteConnection.ClearAllPools();
        // A database from before the fix can already hold a second spelling.
        using (var raw = new SqliteConnection($"Data Source={Path.Combine(_dir, "ro3emu.db")}"))
        {
            raw.Open();
            using var insert = raw.CreateCommand();
            insert.CommandText = "INSERT INTO account(name,password_hash,salt,created_utc) SELECT 'Admin',password_hash,salt,created_utc FROM account WHERE name='admin'";
            Assert.Equal(1, insert.ExecuteNonQuery());
        }
        using (var store = Store())
        {
            Assert.Equal("admin", (await store.SignInAsync("admin", "secret")).Account);
            Assert.Equal("Admin", (await store.SignInAsync("Admin", "secret")).Account);
            Assert.Equal("admin", (await store.SignInAsync("ADMIN", "secret")).Account);
        }
        var options = new ServerOptions { GmAccounts = ["admin"] };
        Assert.True(GmCommands.Allowed(options, "admin", loopbackTool: false));
        Assert.False(GmCommands.Allowed(options, "Admin", loopbackTool: false));
    }

    [Fact]
    public void MicroArgumentLogsHideLoginSecrets()
    {
        var args = new Dictionary<object, object?>
        {
            ["Ticket"] = "0123456789abcdef0123456789abcdef", ["Uid"] = "100001",
            ["Inner"] = new Dictionary<object, object?> { ["Sign"] = "fedcba9876543210", ["Auth"] = new byte[16] },
        };
        string described = MicroServiceRouter.Describe(args);
        Assert.DoesNotContain("0123456789abcdef", described);
        Assert.DoesNotContain("fedcba9876543210", described);
        Assert.Contains("Uid=\"100001\"", described);
        Assert.Contains("Ticket=<redacted>", described);
    }

    [Fact]
    public void ClientMsgpackIsReadAsUntrusted()
    {
        object nested = 1;
        for (int i = 0; i < 64; i++) nested = new object[] { nested };
        byte[] deep = MessagePackSerializer.Serialize(nested);
        Assert.ThrowsAny<MessagePackSerializationException>(() => MessagePackSerializer.Deserialize<object>(deep, MicroServiceRouter.Untrusted));
        var map = MessagePackSerializer.Deserialize<object>(MessagePackSerializer.Serialize(new Dictionary<string, object> { ["Uid"] = "7" }),
            MicroServiceRouter.Untrusted);
        Assert.Equal("7", MicroServiceRouter.MapString(map, "Uid"));
    }

    [Fact]
    public async Task TheCaseInsensitiveLookupUsesItsIndex()
    {
        using var store = Store();
        await store.SignInAsync("admin", "secret");
        using var db = store.Open();
        using var plan = db.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT name,password_hash,salt FROM account WHERE name='Admin' COLLATE NOCASE";
        using var reader = plan.ExecuteReader();
        var steps = new List<string>();
        while (reader.Read()) steps.Add(reader.GetString(3));
        Assert.Contains(steps, s => s.Contains("account_name_nocase", StringComparison.Ordinal));
    }

    private static long Count(AccountStore store)
    {
        using var db = store.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM account";
        return (long)cmd.ExecuteScalar()!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
