using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>C/D13c and E05c: LifeJob state, payment and receipt cross one owned commit boundary.</summary>
public sealed class LifeJobOwnerEconomyTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-life-owner-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Session(string account) => Service<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        }, _clock);
        await _app.StartAsync(); _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter(string account)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(account, "pw"); await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync(account == "alice" ? "Alice" : "Bob")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role); return bot;
    }

    private async Task Gm(string account, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync(_http + $"/ro3emu/gm?account={account}", new StringContent(command)))
            .EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(10))).Body);

    private SqliteConnection OpenDb()
    {
        var db = new SqliteConnection($"Data Source={Path.Combine(_directory, "ro3emu.db")}");
        db.Open(); return db;
    }

    private long ReceiptCount(string kind)
    {
        using var db = OpenDb(); using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind=$kind";
        command.Parameters.AddWithValue("$kind", kind);
        return (long)command.ExecuteScalar()!;
    }

    private (string Operation, string Source, long Legs, byte[] Outcome) Receipt(string kind)
    {
        using var db = OpenDb(); using var command = db.CreateCommand();
        command.CommandText = "SELECT operation_id,source_key,leg_count,outcome FROM operation_receipt WHERE command_kind=$kind ORDER BY commit_seq DESC LIMIT 1";
        command.Parameters.AddWithValue("$kind", kind);
        using var reader = command.ExecuteReader(); Assert.True(reader.Read());
        return (reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetFieldValue<byte[]>(3));
    }

    private (long Asset, long Delta)[] Legs(string operation)
    {
        using var db = OpenDb(); using var command = db.CreateCommand();
        command.CommandText = "SELECT asset_id,delta FROM economy_entry WHERE operation_id=$operation";
        command.Parameters.AddWithValue("$operation", operation);
        using var reader = command.ExecuteReader();
        var rows = new List<(long, long)>();
        while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return rows.ToArray();
    }

    [ClientDataFact]
    public async Task HeldCertificateCommitKeepsLifeAndBagPrivateWhileOtherRoleProceeds()
    {
        await using var alice = await Enter("alice");
        await using var bob = await Enter("bob");
        await Gm("alice", "!item 1291000 1");
        await Gm("bob", "!item 1291000 1");
        var session = Session("alice");
        long role = session.Role!.Id;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            Service<AccountStore>().BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/cd13c/life-switch") return Task.CompletedTask;
                if (entered.Task.IsCompleted) return Task.CompletedTask;
                entered.TrySetResult(); return release.Task;
            };
            Service<OwnerRegistry>().DeferredForTest = origin =>
            { if (origin == "GM") deferred.TrySetResult(); };
            return Task.CompletedTask;
        });
        Task? sameRole = null;
        try
        {
            await alice.SendGameAsync(LifeJobService.SwitchLife,
                new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(session.LifeJob!.ActiveLifejob);
            Assert.Equal(1, session.Items.Where(i => i.Entry == 1291000).Sum(i => i.Stack));
            Assert.Equal(0, ReceiptCount("cd13c/life-switch"));
            sameRole = Gm("alice", "!item 1291000 1");
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(sameRole.IsCompleted);
            await bob.SendGameAsync(LifeJobService.SwitchLife,
                new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
            Assert.Equal(0, (await Next(bob, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
            Assert.Equal(1, Session("bob").LifeJob!.ActiveLifejob[1]);
        }
        finally
        {
            release.TrySetResult();
            await Service<GameLoop>().InvokeAsync(() =>
            {
                Service<AccountStore>().BeforeOwnedCommitForTest = null;
                Service<OwnerRegistry>().DeferredForTest = null;
                return Task.CompletedTask;
            });
        }
        Assert.Equal(0, (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        await sameRole!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, session.LifeJob!.ActiveLifejob[1]);
        Assert.Equal(1, session.Items.Where(i => i.Entry == 1291000).Sum(i => i.Stack));
        var receipt = Receipt("cd13c/life-switch");
        Assert.StartsWith($"cd13c/life-switch/{role}/", receipt.Source);
        Assert.Equal(receipt.Legs, Legs(receipt.Operation).Length);
        Assert.Contains(Legs(receipt.Operation), leg => leg.Asset == 1291000 && leg.Delta == -1);
        using var outcome = JsonDocument.Parse(receipt.Outcome);
        Assert.True(outcome.RootElement.TryGetProperty("LifeHash", out _));
        Assert.Equal(1, DBLifeJobInfo.Parser.ParseFrom(Service<RoleStore>().LoadLifeJob(role)!).ActiveLifejob[1]);
    }

    [ClientDataFact]
    public async Task LedgerFailureRollsBackCertificateAndProfessionThenAllowsFreshRequest()
    {
        await using var alice = await Enter("alice");
        await Gm("alice", "!item 1291000 1");
        var session = Session("alice");
        long role = session.Role!.Id;
        FixtureSql.Execute(Service<AccountStore>(), """
            CREATE TRIGGER reject_life_economy BEFORE INSERT ON economy_entry
            WHEN NEW.asset_id=1291000 BEGIN SELECT RAISE(ABORT,'life economy rollback'); END
            """);
        try
        {
            var failure = await Assert.ThrowsAsync<SqliteException>(() => Service<GameLoop>().InvokeAsync(() =>
                Service<OwnerRegistry>().RunSessionAsync("dispatcher", [ReservationKey.Role(role)], session,
                    () => Service<LifeJobService>().SwitchAsync(session,
                        new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 }))));
            Assert.Contains("life economy rollback", failure.Message);
            Assert.Empty(session.LifeJob!.ActiveLifejob);
            Assert.Equal(1, session.Items.Where(i => i.Entry == 1291000).Sum(i => i.Stack));
            Assert.Empty(DBLifeJobInfo.Parser.ParseFrom(Service<RoleStore>().LoadLifeJob(role)!).ActiveLifejob);
            Assert.Equal(0, ReceiptCount("cd13c/life-switch"));
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER reject_life_economy"); }
        await alice.SendGameAsync(LifeJobService.SwitchLife,
            new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        Assert.Equal(0, (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        Assert.Equal(0, session.Items.Where(i => i.Entry == 1291000).Sum(i => i.Stack));
        Assert.Equal(1, ReceiptCount("cd13c/life-switch"));
    }

    [ClientDataFact]
    public async Task VigorItemConsumptionAndQuotaShareOneCoveredReceipt()
    {
        await using var alice = await Enter("alice");
        await Gm("alice", "!lvl 34"); await Gm("alice", "!item 1291002 1");
        var session = Session("alice");
        long role = session.Role!.Id;
        long guid = session.Items.Single(i => i.Entry == 1291002).Guid;
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        Assert.Equal(1500, session.Restrictions!.RestrictCount[LifeJobService.VigorId].Count);
        Assert.Equal(4, session.Restrictions.RestrictCount[LifeJobService.VigorUsesId].Count);
        Assert.DoesNotContain(session.Items, i => i.Guid == guid);
        var receipt = Receipt("cd13c/life-vigor-item");
        Assert.StartsWith($"cd13c/life-vigor-item/{role}/", receipt.Source);
        Assert.Contains(Legs(receipt.Operation), leg => leg.Asset == 1291002 && leg.Delta == -1);
        var saved = DBRestrictInfo.Parser.ParseFrom(Service<RoleStore>().LoadLifeRestrict(role)!);
        Assert.Equal(session.Restrictions.ToByteArray(), saved.ToByteArray());
        Assert.Equal(1, ReceiptCount("cd13c/life-vigor-item"));
    }

    [ClientDataFact]
    public async Task ForgeDebitsMaterialsGrantsResolvedOutputAndSpendsVigorInOneReceipt()
    {
        await using var alice = await Enter("alice");
        await Gm("alice", "!lvl 34"); await Gm("alice", "!item 1291000 1");
        await alice.SendGameAsync(LifeJobService.SwitchLife,
            new SwitchLifeJobReq { NewLifejobId = (LifeJobType)2, OpJobType = 2 });
        Assert.Equal(0, (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        await Gm("alice", "!item 2 10000");
        await alice.SendGameAsync(LifeJobService.StudyRecipe, new StudyRecipeReq { FormulaID = 20001 });
        Assert.Equal(0, (await Next(alice, LifeJobService.StudyRecipe, StudyRecipeResp.Parser)).Result);
        foreach (int ingredient in new[] { 1293022, 1293050, 1294010 }) await Gm("alice", $"!item {ingredient} 8");
        var session = Session("alice");
        long beforeVigor = session.Restrictions!.RestrictCount[LifeJobService.VigorId].Count;
        await alice.SendGameAsync(LifeJobService.ForgeTime, new GetForgeCostTimeReq { ForgeID = 20001 });
        var quote = await Next(alice, LifeJobService.ForgeTime, GetForgeCostTimeResp.Parser);
        Assert.Equal(0, quote.Result);
        await Task.Delay(quote.CostTime + 100);
        await alice.SendGameAsync(LifeJobService.StartForge, new StartForgeReq { ItemUniqueId = 20001, ItemNum = 1 });
        var forged = await Next(alice, LifeJobService.StartForge, StartForgeResp.Parser);
        Assert.Equal(0, forged.Result);
        Assert.Equal(beforeVigor - 20, session.Restrictions.RestrictCount[LifeJobService.VigorId].Count);
        var receipt = Receipt("cd13c/life-forge");
        Assert.Equal(receipt.Legs, Legs(receipt.Operation).Length);
        Assert.Contains(Legs(receipt.Operation), leg => leg.Asset == 1293022 && leg.Delta < 0);
        var output = forged.OutputItemMap.Values.SelectMany(row => row.Items).First(i => i.ItemEntry is 1001101 or 1001201);
        Assert.Contains(Legs(receipt.Operation), leg => leg.Asset == output.ItemEntry && leg.Delta > 0);
        using var outcome = JsonDocument.Parse(receipt.Outcome);
        Assert.Equal(20001, outcome.RootElement.GetProperty("Result").GetProperty("Recipe").GetInt64());
        Assert.Equal(beforeVigor - 20, outcome.RootElement.GetProperty("Vigor").GetInt64());
        Assert.True(outcome.RootElement.GetProperty("Grants").GetArrayLength() > 0);
        Assert.Equal(1, ReceiptCount("cd13c/life-forge"));
    }

    [ClientDataFact]
    public async Task LostCertificateCommitAcknowledgementResolvesWithoutSecondDebit()
    {
        await using var alice = await Enter("alice");
        await Gm("alice", "!item 1291000 1");
        Service<AccountStore>().FailNextEconomyAcknowledgementForTest();
        await alice.SendGameAsync(LifeJobService.SwitchLife,
            new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        Assert.Equal(0, (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        var session = Session("alice");
        Assert.Equal(1, session.LifeJob!.ActiveLifejob[1]);
        Assert.Equal(0, session.Items.Where(i => i.Entry == 1291000).Sum(i => i.Stack));
        Assert.Equal(1, ReceiptCount("cd13c/life-switch"));
        await alice.SendGameAsync(LifeJobService.SwitchLife,
            new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        Assert.Equal(ErrorCodes.LifeAlreadyLearned,
            (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        Assert.Equal(1, ReceiptCount("cd13c/life-switch"));
    }

    [ClientDataFact]
    public async Task HeldForgeCrossesFiveAmAndQueuedVigorUseReconcilesAfterCommit()
    {
        var localBefore = DateTime.Today.AddHours(4).AddMinutes(59);
        var beforeCutoff = new DateTimeOffset(localBefore, TimeZoneInfo.Local.GetUtcOffset(localBefore));
        _clock.Set(beforeCutoff);
        await using var alice = await Enter("alice");
        await Gm("alice", "!lvl 34"); await Gm("alice", "!item 1291000 1");
        await alice.SendGameAsync(LifeJobService.SwitchLife,
            new SwitchLifeJobReq { NewLifejobId = (LifeJobType)2, OpJobType = 2 });
        Assert.Equal(0, (await Next(alice, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser)).Result);
        await Gm("alice", "!item 2 10000");
        await alice.SendGameAsync(LifeJobService.StudyRecipe, new StudyRecipeReq { FormulaID = 20001 });
        Assert.Equal(0, (await Next(alice, LifeJobService.StudyRecipe, StudyRecipeResp.Parser)).Result);
        foreach (int ingredient in new[] { 1293022, 1293050, 1294010 }) await Gm("alice", $"!item {ingredient} 8");
        await Gm("alice", "!item 1291002 1");
        var session = Session("alice");
        long role = session.Role!.Id;
        long potionGuid = session.Items.Single(i => i.Entry == 1291002).Guid;
        await alice.SendGameAsync(LifeJobService.ForgeTime, new GetForgeCostTimeReq { ForgeID = 20001 });
        var quote = await Next(alice, LifeJobService.ForgeTime, GetForgeCostTimeResp.Parser);
        Assert.Equal(0, quote.Result);
        await Task.Delay(quote.CostTime + 100);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            Service<AccountStore>().BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/cd13c/life-forge") return Task.CompletedTask;
                entered.TrySetResult(); return release.Task;
            };
            Service<OwnerRegistry>().DeferredForTest = _ => { deferred.TrySetResult(); };
            return Task.CompletedTask;
        });
        Task<ItemUseResp>? potionReply = null;
        try
        {
            await alice.SendGameAsync(LifeJobService.StartForge,
                new StartForgeReq { ItemUniqueId = 20001, ItemNum = 1 });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(500, session.Restrictions!.RestrictCount[LifeJobService.VigorId].Count);
            Assert.Equal(8, session.Items.Where(i => i.Entry == 1293022).Sum(i => i.Stack));
            Assert.Equal(0, ReceiptCount("cd13c/life-forge"));
            _clock.Set(beforeCutoff.AddMinutes(2));
            await alice.SendGameAsync(CharacterService.ItemUse,
                new ItemUseReq { ItemGuid = potionGuid, ItemCount = 1 });
            potionReply = Next(alice, CharacterService.ItemUse, ItemUseResp.Parser);
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(potionReply.IsCompleted);
            Assert.Equal(500, session.Restrictions.RestrictCount[LifeJobService.VigorId].Count);
            Assert.Equal(0, ReceiptCount("cd13c/life-vigor-item"));
        }
        finally
        {
            release.TrySetResult();
            await Service<GameLoop>().InvokeAsync(() =>
            {
                Service<AccountStore>().BeforeOwnedCommitForTest = null;
                Service<OwnerRegistry>().DeferredForTest = null;
                return Task.CompletedTask;
            });
        }
        var forged = await Next(alice, LifeJobService.StartForge, StartForgeResp.Parser);
        Assert.Equal(0, forged.Result);
        Assert.Equal(0, (await potionReply!.WaitAsync(TimeSpan.FromSeconds(10))).Result);
        Assert.Equal(1480, session.Restrictions!.RestrictCount[LifeJobService.VigorId].Count);
        Assert.Equal(4, session.Restrictions.RestrictCount[LifeJobService.VigorUsesId].Count);
        Assert.Equal(0, session.LifeRuntime.EnergySpent);
        Assert.Equal(DateOnly.FromDateTime(beforeCutoff.AddMinutes(2).LocalDateTime.AddHours(-5)).DayNumber,
            session.LifeRuntime.EnergyDay);
        Assert.Contains(forged.OutputItemMap.Values.SelectMany(row => row.Items), item =>
            session.Items.Any(owned => owned.Entry == item.ItemEntry && owned.Guid == item.ItemGuid));
        Assert.Equal(session.Restrictions.ToByteArray(), Service<RoleStore>().LoadLifeRestrict(role));
        Assert.Equal(1, ReceiptCount("cd13c/life-forge"));
        Assert.Equal(1, ReceiptCount("cd13c/life-vigor-item"));
        Assert.NotEqual(Receipt("cd13c/life-forge").Source, Receipt("cd13c/life-vigor-item").Source);
    }

    private sealed class ManualClock(DateTimeOffset initial) : TimeProvider
    {
        private long _ticks = initial.UtcTicks;
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
