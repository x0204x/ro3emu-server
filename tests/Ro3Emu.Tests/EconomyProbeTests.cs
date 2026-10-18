using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

public sealed class EconomyProbeTests : IDisposable
{
    private readonly string directory = Path.Combine(RepoRoot(), ".loadtest-temp", "e01-cli-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore accounts;
    private readonly RoleStore roles;
    private readonly ItemStore items;
    private readonly long role;
    private string Database => Path.Combine(directory, "ro3emu.db");
    private string Data => SyntheticData.Dir;
    public EconomyProbeTests()
    {
        accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = directory, GameDataDir = SyntheticData.Dir }));
        roles = new RoleStore(accounts); items = new ItemStore(accounts);
        role = roles.Create("cli", 1, "Cli", 1, 1000, 1)!.Id;
        var p = roles.LoadProgress(role); p.Currency[1] = 1000; roles.SaveProgress(role, p);
    }

    [Fact]
    public void SnapshotCompareReceiptAndRestoreCommandsStayReadOnlyAndNeverHideIncompleteCoverage()
    {
        string opening = Path.Combine(directory, "opening");
        Assert.Equal(2, RunReadOnly("snapshot", Database, Data, "auto", opening));
        Assert.True(File.Exists(opening + ".md"));
        var p = roles.LoadProgress(role); p.Currency[1] -= 100;
        var builder = roles.NewTransaction();
        builder.ItemDelta(role, [], [new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = 1 }]);
        builder.Progress(role, p); builder.RoleBlob(role, "shop", Encoding.UTF8.GetBytes("{\"Bought\":{\"118\":{\"Count\":1,\"Period\":\"\"}},\"Favorites\":[]}"));
        builder.WithEconomy(new EconomyMutation("cli-purchase", "cli", role, "npc-shop-buy", "118/1/100", "cli-source", 1,
            [new(new(EconomyAssetKind.Currency, 1), 100)], [new(new(EconomyAssetKind.Item, 1000000), 1)], [1]));
        roles.Commit(builder, "test/cli-purchase");
        string compare = Path.Combine(directory, "compare");
        Assert.Equal(2, RunReadOnly("compare", Database, Data, "auto", opening + ".json", compare));
        using (var json = JsonDocument.Parse(File.ReadAllBytes(compare + ".json")))
        {
            var check = json.RootElement.GetProperty("Check");
            Assert.Equal((int)EconomyVerdict.NotMeasured, check.GetProperty("Verdict").GetInt32());
            Assert.All(check.GetProperty("Assets").EnumerateArray(), row => Assert.Equal("0", row.GetProperty("Residual").GetString()));
        }
        Assert.Equal(2, RunReadOnly("verify-receipts", Database, Data, "auto", "0", "1", Path.Combine(directory, "receipts")));
        Assert.Equal(2, RunReadOnly("restore-check", Database, Data, "auto", opening + ".json", Path.Combine(directory, "restore")));
        FixtureSql.Execute(accounts, "DELETE FROM economy_entry WHERE leg_no=1");
        Assert.Equal(1, RunReadOnly("verify-receipts", Database, Data, "auto", "0", "1", Path.Combine(directory, "damaged")));
    }

    [Fact]
    public void CliRejectsWrongRealmMalformedEvidenceAndOutputOverwrite()
    {
        string opening = Path.Combine(directory, "opening");
        Assert.Equal(2, RunReadOnly("snapshot", Database, Data, "auto", opening));
        byte[] original = File.ReadAllBytes(opening + ".json");
        Assert.Equal(1, RunReadOnly("snapshot", Database, Data, "auto", opening));
        Assert.Equal(original, File.ReadAllBytes(opening + ".json"));
        Assert.Equal(1, RunReadOnly("snapshot", Database, Data, "wrong-realm", Path.Combine(directory, "wrong")));
        string malformed = Path.Combine(directory, "malformed.json");
        File.WriteAllText(malformed, "{\"Snapshot\":{}}");
        Assert.Equal(1, RunReadOnly("compare", Database, Data, "auto", malformed, Path.Combine(directory, "invalid")));
        Assert.Equal(64, EconomyProbeCli.Run([]));
        Assert.Equal(64, EconomyProbeCli.Run(["verify-receipts", Database, Data, "auto", "2", "1", "unused"]));
        FixtureSql.Execute(accounts, "DELETE FROM economy_meta");
        Assert.Equal(1, RunReadOnly("verify-receipts", Database, Data, "auto", "0", "0", Path.Combine(directory, "missing-meta")));
    }

    private int RunReadOnly(params string[] args)
    {
        long Writes() => JsonSerializer.SerializeToElement(accounts.AccessMetrics.Snapshot()).GetProperty("writerCompleted").GetInt64();
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
        accounts.Dispose();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
