using System.Text.Json;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>A paid failed refinement resolves its materials, level and luck in one durable command.</summary>
public sealed class EquipmentRefineEconomyTests : IAsyncLifetime
{
    private const int Gear = 2550561, Grid = 3, Material = 1200002;
    private const string CrashDirEnv = "RO3_STEP13A_REFINE_CRASH_DIR", CrashBarrierEnv = "RO3_STEP13A_REFINE_BARRIER";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly Lazy<EconomyCatalog> Catalog = new(() =>
        EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")));
    private readonly string _directory = Environment.GetEnvironmentVariable(CrashDirEnv)
        ?? Path.Combine(RepoRoot(), ".loadtest-temp", "ro3-refine-economy-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task Gm(string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync(_http + "/ro3emu/gm?account=alice", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private async Task<BotClient> Prepared()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id);
        await Gm("!lvl 50");
        await Gm($"!item {Gear} 1");
        var character = Service<CharacterService>();
        var costs = Enumerable.Range(1, 6).SelectMany(level =>
            GameData.Pairs(character.RefineRow(Grid, level)!.Value, "_kDeplete"))
            .Where(pair => pair.Item1 != 2).GroupBy(pair => pair.Item1);
        foreach (var group in costs)
            await Gm($"!item {group.Key} {group.Sum(pair => (long)pair.Item2)}");
        await Gm("!item 2 10000000");
        long guid = Session.Items.Single(i => i.Entry == Gear).Guid;
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq
        {
            ItemGUID = guid, Pos = (EquipGridType)Grid, Opr = (EquipItemOprType)0,
        });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        Service<EquipmentService>().RefineRoll = _ => 0;
        for (int level = 1; level <= 5; level++)
        {
            var response = await Refine(bot);
            Assert.Equal((0, level), (response.Result, response.RefineLv));
        }
        Assert.Equal(5, Saved().Refine[Grid]);
        Service<EquipmentService>().RefineRoll = _ => 9999;
        return bot;
    }

    private static async Task<RefineEquipmentRsp> Refine(BotClient bot)
    {
        await bot.SendGameAsync(EquipmentService.Refine, new RefineEquipmentReq { EquipPos = (EquipGridType)Grid });
        return await Next(bot, EquipmentService.Refine, RefineEquipmentRsp.Parser);
    }

    private EquipLevels Saved() => JsonSerializer.Deserialize<EquipLevels>(Service<RoleStore>().LoadEquipLevels(Session.Role!.Id)!)!;
    private EconomySnapshot Cut() => S3EconomyReader.Read(_directory, Catalog.Value);
    private long MaterialCount() => Session.Items.Where(i => i.Entry == Material).Sum(i => (long)i.Stack);
    private long Currency() => Session.Progress!.Currency.GetValueOrDefault(2);
    private (long Material, long Currency) SixthCost()
    {
        var row = Service<CharacterService>().RefineRow(Grid, 6)!.Value;
        var costs = GameData.Pairs(row, "_kDeplete");
        return (costs.Where(p => p.Item1 == Material).Sum(p => (long)p.Item2),
            costs.Where(p => p.Item1 == 2).Sum(p => (long)p.Item2));
    }

    [ClientDataFact]
    public async Task HeldPaidFailurePublishesNeitherDebitNorLuckUntilCommit()
    {
        await using var bot = await Prepared();
        var accounts = Service<AccountStore>();
        var beforeMaterial = MaterialCount(); var beforeCurrency = Currency();
        var cost = SixthCost();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/cd13a/equipment-refine") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        Task<RefineEquipmentRsp> command = Refine(bot);
        try
        {
            await entered.Task.WaitAsync(Wait);
            Assert.False(command.IsCompleted);
            await Service<GameLoop>().InvokeAsync(() =>
            {
                Assert.Equal(5, Session.EquipLevels.Refine[Grid]);
                Assert.Equal(0, Session.EquipLevels.Luck.GetValueOrDefault(Grid));
                Assert.Equal(beforeMaterial, MaterialCount());
                Assert.Equal(beforeCurrency, Currency());
                return Task.CompletedTask;
            });
            Assert.Equal(5, Saved().Refine[Grid]);
        }
        finally { release.TrySetResult(); accounts.BeforeOwnedCommitForTest = null; }
        var reply = await command.WaitAsync(Wait);
        Assert.Equal((0, 5, 500), (reply.Result, reply.RefineLv, reply.RefineLuckyAdd));
        Assert.Equal(500, Saved().Luck[Grid]);
        Assert.Equal(beforeMaterial - cost.Material, MaterialCount());
        Assert.Equal(beforeCurrency - cost.Currency, Currency());
    }

    [ClientDataFact]
    public async Task LedgerFailureRollsBackPaidFailureAndKeepsItsResolvedStatePrivate()
    {
        await using var bot = await Prepared();
        var session = Session;
        var beforeMaterial = MaterialCount(); var beforeCurrency = Currency();
        FixtureSql.Execute(Service<AccountStore>(), """
            CREATE TRIGGER fail_refine_economy BEFORE INSERT ON economy_entry
            BEGIN SELECT RAISE(ABORT,'refine economy fault'); END
            """);
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => Service<GameLoop>().InvokeAsync(() =>
                Service<OwnerRegistry>().RunSessionAsync("dispatcher", [ReservationKey.Role(session.Role!.Id)], session,
                    () => Service<EquipmentService>().RefineAsync(session,
                        new RefineEquipmentReq { EquipPos = (EquipGridType)Grid }))));
            Assert.Equal((5, 0), (session.EquipLevels.Refine[Grid], session.EquipLevels.Luck.GetValueOrDefault(Grid)));
            Assert.Equal((5, 0), (Saved().Refine[Grid], Saved().Luck.GetValueOrDefault(Grid)));
            Assert.Equal(beforeMaterial, MaterialCount());
            Assert.Equal(beforeCurrency, Currency());
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_refine_economy"); }
        Assert.Equal(500, (await Refine(bot)).RefineLuckyAdd);
    }

    [ClientDataFact]
    public async Task LostCommitAcknowledgementResolvesTheSamePaidFailure()
    {
        await using var bot = await Prepared();
        var beforeMaterial = MaterialCount(); var beforeCurrency = Currency();
        var cost = SixthCost();
        var opening = Cut();
        Service<AccountStore>().FailNextEconomyAcknowledgementForTest();
        var reply = await Refine(bot);
        Assert.Equal((0, 5, 500), (reply.Result, reply.RefineLv, reply.RefineLuckyAdd));
        Assert.Equal((5, 500), (Saved().Refine[Grid], Saved().Luck[Grid]));
        Assert.Equal(beforeMaterial - cost.Material, MaterialCount());
        Assert.Equal(beforeCurrency - cost.Currency, Currency());
        var closing = Cut();
        var ledger = S3EconomyReader.ReadLedger(_directory, Catalog.Value, opening, closing)!;
        Assert.True(ledger.CompleteHistory, string.Join(';', ledger.Issues.Select(issue => issue.Detail)));
        var receipt = Assert.Single(ledger.Receipts, r => r.CommandKind == "cd13a/equipment-refine");
        Assert.Single(receipt.SourceKeys!);
        Assert.Equal(2, receipt.LegCount);
        var costs = ledger.Legs.Where(l => l.OperationId == receipt.OperationId && l.Reason == EconomyReason.RefineCost).ToArray();
        Assert.Equal(2, costs.Length);
        Assert.Contains(costs, l => l.Kind == EconomyLegKind.Sink && l.Asset == new EconomyAsset(EconomyAssetKind.Item, Material)
            && l.Delta == -cost.Material);
        Assert.Contains(costs, l => l.Kind == EconomyLegKind.Sink && l.Asset == new EconomyAsset(EconomyAssetKind.Currency, 2)
            && l.Delta == -cost.Currency);
        var exact = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(exact.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(exact.Assets, asset =>
        {
            Assert.Equal("0", asset.Residual);
            Assert.Equal("0", asset.LegResidual);
        });
        Assert.All(exact.Locations, location => Assert.Equal("0", location.Residual));
    }

    private sealed record CrashRecord(long Role, long MaterialBefore, long CurrencyBefore,
        long MaterialCost, long CurrencyCost, int TesthostPid);

    [Fact]
    public async Task CrashChildAfterRefineCommit()
    {
        string? barrier = Environment.GetEnvironmentVariable(CrashBarrierEnv);
        if (barrier == null) return;
        await using var bot = await Prepared();
        var cost = SixthCost();
        var record = new CrashRecord(Session.Role!.Id, MaterialCount(), Currency(), cost.Material, cost.Currency,
            Environment.ProcessId);
        var accounts = Service<AccountStore>();
        accounts.BeforeOwnedApplyForTest = label =>
        {
            if (label != "economy/cd13a/equipment-refine") return Task.CompletedTask;
            string temporary = barrier + ".writing";
            File.WriteAllText(temporary, JsonSerializer.Serialize(record));
            File.Move(temporary, barrier); // Parent observes a complete record from the blocked testhost.
            return Task.Delay(Timeout.InfiniteTimeSpan);
        };
        await bot.SendGameAsync(EquipmentService.Refine, new RefineEquipmentReq { EquipPos = (EquipGridType)Grid });
        await Task.Delay(Timeout.InfiniteTimeSpan); // The parent kills the process after the durable commit.
    }

    [ClientDataFact]
    public async Task ProcessDeathAfterPaidFailureCommitRestartsWithChargeLevelLuckAndReceipt()
    {
        string crashDir = Path.Combine(RepoRoot(), ".loadtest-temp", "ro3-refine-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(crashDir);
        string barrier = Path.Combine(crashDir, "committed.json");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoRoot(), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string argument in new[] { "vstest", typeof(EquipmentRefineEconomyTests).Assembly.Location,
            "--TestCaseFilter:FullyQualifiedName~CrashChildAfterRefineCommit",
            "--ResultsDirectory:" + Path.Combine(crashDir, "results") })
            start.ArgumentList.Add(argument);
        start.Environment[CrashDirEnv] = crashDir;
        start.Environment[CrashBarrierEnv] = barrier;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start refine crash child.");
        Task<string> output = child.StandardOutput.ReadToEndAsync(), errors = child.StandardError.ReadToEndAsync();
        WebApplication? restarted = null;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            while (!File.Exists(barrier) && DateTime.UtcNow < deadline && !child.HasExited)
                await Task.Delay(50);
            if (!File.Exists(barrier))
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                throw new Xunit.Sdk.XunitException($"Refine child missed post-commit barrier: {await output} {await errors}");
            }
            var record = JsonSerializer.Deserialize<CrashRecord>(await File.ReadAllTextAsync(barrier))!;
            if (record.TesthostPid == child.Id)
                throw new Xunit.Sdk.XunitException("Refine barrier was published by the launcher, not testhost.");
            using (var testhost = Process.GetProcessById(record.TesthostPid))
            {
                testhost.Kill(entireProcessTree: true);
                await testhost.WaitForExitAsync();
            }
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            var (web, micro, game) = TestPorts.FreePorts();
            restarted = ServerHost.Create([], o =>
            {
                o.DataDir = crashDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
                o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            });
            await restarted.StartAsync();
            var roles = restarted.Services.GetRequiredService<RoleStore>();
            var saved = JsonSerializer.Deserialize<EquipLevels>(roles.LoadEquipLevels(record.Role)!)!;
            Assert.Equal((5, 500), (saved.Refine[Grid], saved.Luck[Grid]));
            Assert.Equal(record.MaterialBefore - record.MaterialCost,
                restarted.Services.GetRequiredService<ItemStore>().Load(record.Role)
                    .Where(i => i.Entry == Material).Sum(i => (long)i.Stack));
            Assert.Equal(record.CurrencyBefore - record.CurrencyCost,
                roles.LoadProgress(record.Role).Currency.GetValueOrDefault(2));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(crashDir, "ro3emu.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT outcome,leg_count FROM operation_receipt WHERE actor_role=$role AND command_kind=$kind ORDER BY commit_seq DESC LIMIT 1";
                command.Parameters.AddWithValue("$role", record.Role);
                command.Parameters.AddWithValue("$kind", "cd13a/equipment-refine");
                using var rows = command.ExecuteReader();
                Assert.True(rows.Read());
                using var outcome = JsonDocument.Parse((byte[])rows[0]);
                Assert.False(outcome.RootElement.GetProperty("Success").GetBoolean());
                Assert.Equal(9999, outcome.RootElement.GetProperty("SuccessRoll").GetInt32());
                Assert.True(rows.GetInt32(1) >= 2); // Both material and currency have sink legs.
                Assert.False(rows.Read());
            }
            await using var bot = new BotClient($"http://127.0.0.1:{web}",
                File.ReadAllText(Path.Combine(crashDir, "gate_rsa_public.pem")));
            await bot.LoginAsync("alice", "pw"); await bot.EnterGateAsync();
            await bot.EnterWorldAsync(record.Role);
            var session = restarted.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == record.Role);
            Assert.Equal((5, 500), (session.EquipLevels.Refine[Grid], session.EquipLevels.Luck[Grid]));
            Assert.Equal(record.MaterialBefore - record.MaterialCost,
                session.Items.Where(i => i.Entry == Material).Sum(i => (long)i.Stack));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            if (restarted != null) { await restarted.StopAsync(); await restarted.DisposeAsync(); }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(crashDir, true); } catch (IOException) { }
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repo root");
    }
}
