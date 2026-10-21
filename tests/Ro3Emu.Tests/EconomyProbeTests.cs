using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

public sealed class EconomyProbeTests : IDisposable
{
    private readonly string _directory = Path.Combine(RepoRoot(), ".loadtest-temp", "e01-cli-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;
    private readonly long _role;
    private string Database => Path.Combine(_directory, "ro3emu.db");
    private string Data => SyntheticData.Dir;
    public EconomyProbeTests()
    {
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory, GameDataDir = SyntheticData.Dir }));
        _roles = new RoleStore(_accounts); _items = new ItemStore(_accounts);
        _role = _roles.Create("cli", 1, "Cli", 1, 1000, 1)!.Id;
        var p = _roles.LoadProgress(_role); p.Currency[1] = 1000; _roles.SaveProgress(_role, p);
    }

    [Fact]
    public void SnapshotCompareReceiptAndRestoreCommandsStayReadOnlyAndNeverHideIncompleteCoverage()
    {
        string opening = Path.Combine(_directory, "opening");
        Assert.Equal(2, RunReadOnly("snapshot", Database, Data, "auto", opening));
        Assert.True(File.Exists(opening + ".md"));
        var p = _roles.LoadProgress(_role); p.Currency[1] -= 100;
        var builder = _roles.NewTransaction();
        builder.ItemDelta(_role, [], [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        builder.Progress(_role, p); builder.RoleBlob(_role, "shop", Encoding.UTF8.GetBytes("{\"Bought\":{\"118\":{\"Count\":1,\"Period\":\"\"}},\"Favorites\":[]}"));
        builder.WithEconomy(new EconomyMutation("cli-purchase", "cli", _role, "npc-shop-buy", "118/1/100", "cli-source", 1,
            [new(new(EconomyAssetKind.Currency, 1), 100)], [new(new(EconomyAssetKind.Item, 1000000), 1)], [1]));
        _roles.Commit(builder, "test/cli-purchase");
        string compare = Path.Combine(_directory, "compare");
        Assert.Equal(2, RunReadOnly("compare", Database, Data, "auto", opening + ".json", compare));
        using (var json = JsonDocument.Parse(File.ReadAllBytes(compare + ".json")))
        {
            var check = json.RootElement.GetProperty("Check");
            Assert.Equal((int)EconomyVerdict.NotMeasured, check.GetProperty("Verdict").GetInt32());
            Assert.All(check.GetProperty("Assets").EnumerateArray(), row => Assert.Equal("0", row.GetProperty("Residual").GetString()));
        }
        Assert.Equal(2, RunReadOnly("verify-receipts", Database, Data, "auto", "0", "1", Path.Combine(_directory, "receipts")));
        Assert.Equal(2, RunReadOnly("restore-check", Database, Data, "auto", opening + ".json", Path.Combine(_directory, "restore")));
        FixtureSql.Execute(_accounts, "DELETE FROM economy_entry WHERE leg_no=1");
        Assert.Equal(1, RunReadOnly("verify-receipts", Database, Data, "auto", "0", "1", Path.Combine(_directory, "damaged")));
    }

    [Fact]
    public void CliRejectsWrongRealmMalformedEvidenceAndOutputOverwrite()
    {
        string opening = Path.Combine(_directory, "opening");
        Assert.Equal(2, RunReadOnly("snapshot", Database, Data, "auto", opening));
        byte[] original = File.ReadAllBytes(opening + ".json");
        Assert.Equal(1, RunReadOnly("snapshot", Database, Data, "auto", opening));
        Assert.Equal(original, File.ReadAllBytes(opening + ".json"));
        Assert.Equal(1, RunReadOnly("snapshot", Database, Data, "wrong-realm", Path.Combine(_directory, "wrong")));
        string malformed = Path.Combine(_directory, "malformed.json");
        File.WriteAllText(malformed, "{\"Snapshot\":{}}");
        Assert.Equal(1, RunReadOnly("compare", Database, Data, "auto", malformed, Path.Combine(_directory, "invalid")));
        Assert.Equal(64, EconomyProbeCli.Run([]));
        Assert.Equal(64, EconomyProbeCli.Run(["verify-receipts", Database, Data, "auto", "2", "1", "unused"]));
        FixtureSql.Execute(_accounts, "DELETE FROM economy_meta");
        Assert.Equal(1, RunReadOnly("verify-receipts", Database, Data, "auto", "0", "0", Path.Combine(_directory, "missing-meta")));
    }

    private int RunReadOnly(params string[] args)
    {
        long Writes() => JsonSerializer.SerializeToElement(_accounts.AccessMetrics.Snapshot()).GetProperty("writerCompleted").GetInt64();
        long before = Writes();
        byte[] dbBefore = ReadShared(Database);
        byte[]? walBefore = File.Exists(Database + "-wal") ? ReadShared(Database + "-wal") : null;
        int result = EconomyProbeCli.Run(args);
        Assert.Equal(before, Writes());
        Assert.Equal(dbBefore, ReadShared(Database));
        if (walBefore != null) Assert.Equal(walBefore, ReadShared(Database + "-wal"));
        return result;
    }
    private static byte[] ReadShared(string path)
    {
        // SQLite keeps a write-capable handle open even while idle; inspect bytes without denying that handle.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream(); file.CopyTo(bytes); return bytes.ToArray();
    }
    public void Dispose()
    {
        _accounts.Dispose();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
