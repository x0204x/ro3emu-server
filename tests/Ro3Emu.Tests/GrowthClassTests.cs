using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Growth Classroom (GrowthClassService, TaskService.GrowthClass.cs): 5045 mirrors the delivered growth quests, a
/// module's next stage opens when its stage is delivered, and 33830 pays a reached rank once.
/// Module 1001 "Basics" (function 26, level 1): stage 1 = quests 1700010-1700012 and 1700022-1700024, stage 2 four more.
/// Ranks: 1 needs 0 delivered quests, 2 needs 10 (BecomeStrongerRankConfig _iStandard 0, 10).
/// </summary>
public sealed class GrowthClassTests : IAsyncLifetime
{
    private const int Basics = 1001;
    private const long StageOneQuest = 1700010, StageTwoQuest = 1700013;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-growth-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private EconomySnapshot Cut() => EconomySnapshotReader.Read(Service<AccountStore>().DatabasePath,
        EconomyCatalog.Load(Path.Combine(ClientData.RepoRoot, "data")), "auto");

    /// <summary>The receipts between two cuts balance exactly.</summary>
    private EconomyLedgerWindow Balanced(EconomySnapshot opening, EconomySnapshot closing)
    {
        var ledger = EconomyLedgerReader.Read(Service<AccountStore>().DatabasePath,
            EconomyCatalog.Load(Path.Combine(ClientData.RepoRoot, "data")), "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        Assert.DoesNotContain(closing.Issues, i => i.Kind is EconomyIssueKind.UnknownSchema or EconomyIssueKind.Corruption);
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Assets, asset => Assert.Equal("0", asset.Residual));
        return ledger;
    }

    /// <summary>What the client was last sent on 5045.</summary>
    private Task<DBGrowthClassInfo?> Sent(GameSession s) =>
        Read(() => (s.Synced.GetValueOrDefault(GrowthClassService.Sync) as DBGrowthClassInfo)?.Clone());

    private async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await Read(condition))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(25);
        }
    }

    private async Task LoggedOut(string account)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (Service<SessionRegistry>().All().Any(s => s.Account == account && s.Role != null) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(200);
    }

    private static async Task<GrowthClassGetRankRewardRsp> Claim(BotClient bot, int rank)
    {
        await bot.SendGameAsync(GrowthClassService.GetRankReward, new GrowthClassGetRankRewardReq { RankId = rank });
        return await Next(bot, GrowthClassService.GetRankReward, GrowthClassGetRankRewardRsp.Parser, r => r.RankId == rank);
    }

    private Task<long> Holdings(GameSession s) =>
        Read(() => s.Items.Sum(i => (long)i.Stack) + s.Progress!.Currency.Values.Sum());

    [ClientDataFact]
    public async Task TheSyncFollowsTheDeliveredQuestsAndOpensTheNextStage()
    {
        await using var alice = await InWorld("Alice");
        var login = await Next(alice, GrowthClassService.Sync, DBGrowthClassInfoIncrement.Parser, m => m.SyncAllMark);
        Assert.Equal(1, login.ModuleUnlockedStage[Basics].All); // every module starts at stage 1 (word 43035 past it)
        Assert.Equal(6, login.ModuleUnlockedStage.Count);
        var s = Session(alice);
        Assert.False(await Read(() => s.Tasks!.Infos.ContainsKey(StageOneQuest))); // function 48 opens at level 10

        await Gm(alice, "!lvl 20");
        await Until(() => s.Tasks!.Infos.TryGetValue(StageOneQuest, out var q) && q.Status == TaskState.Running, "stage 1 accepted");
        Assert.False(await Read(() => s.Tasks!.Infos.ContainsKey(StageTwoQuest))); // stage 2 waits for stage 1

        await Gm(alice, "!growthclass done 1001 1");
        await Until(() => s.Tasks!.Infos.TryGetValue(StageTwoQuest, out var q) && q.Status == TaskState.Running, "stage 2 accepted");
        var sent = (await Sent(s))!;
        Assert.Equal(2, sent.ModuleUnlockedStage[Basics]);
        Assert.Equal(6, sent.ModuleTaskCompleteNum[Basics]);
        Assert.Equal(6, sent.RankProgress);
        Assert.Equal(1, sent.ModuleUnlockedStage[1002]);
        var change = await Next(alice, GrowthClassService.Sync, DBGrowthClassInfoIncrement.Parser,
            m => m.ModuleUnlockedStage.TryGetValue(Basics, out var stage) && (stage.All == 2 || stage.Inc == 2 || stage.Inc == 1));
        Assert.False(change.SyncAllMark);

        await Gm(alice, "!growthclass done 1001 6");
        sent = (await Sent(s))!;
        Assert.Equal(6, sent.ModuleUnlockedStage[Basics]); // the last stage stays open
        Assert.Equal(24, sent.ModuleTaskCompleteNum[Basics]);
    }

    [ClientDataFact]
    public async Task ARankPaysOnceWhenReachedAndTheClaimSurvivesRelog()
    {
        await using (var alice = await InWorld("Alice"))
        {
            var s = Session(alice);
            Assert.Equal(ErrorCodes.FunctionLocked, (await Claim(alice, 1)).Result); // function 48: level 10
            await Gm(alice, "!lvl 20");
            await Until(() => s.Tasks!.Infos.ContainsKey(StageOneQuest), "stage 1 accepted");

            Assert.Equal(ErrorCodes.RewardMissing, (await Claim(alice, 99)).Result);
            Assert.Equal(ErrorCodes.NoRankReward, (await Claim(alice, 2)).Result); // 0 of 10 quests
            long before = await Holdings(s);
            var opening = Cut();
            Assert.Equal(0, (await Claim(alice, 1)).Result); // rank 1 needs nothing
            var ledger = Balanced(opening, Cut());
            Assert.Equal("cd13e/growthclass-award", Assert.Single(ledger.Receipts).CommandKind);
            Assert.All(ledger.Legs.Where(l => l.Kind == EconomyLegKind.Source), l => Assert.Equal(EconomyReason.GrowthClassReward, l.Reason));
            Assert.True(await Holdings(s) > before, "the rank 1 reward arrived");
            Assert.Equal(1, (await Sent(s))!.RankReward[1]);
            long afterFirst = await Holdings(s);
            Assert.Equal(ErrorCodes.RewardClaimed, (await Claim(alice, 1)).Result);
            Assert.Equal(afterFirst, await Holdings(s));

            await Gm(alice, "!growthclass done 1001 2"); // 6 + 4 = 10 quests
            Assert.Equal(10, (await Sent(s))!.RankProgress);
            Assert.Equal(0, (await Claim(alice, 2)).Result);
            Assert.True(await Holdings(s) > afterFirst, "the rank 2 reward arrived");
            Assert.Equal(ErrorCodes.NoRankReward, (await Claim(alice, 3)).Result); // 30 needed

            var stored = DBGrowthClassInfo.Parser.ParseFrom(Service<RoleStore>().LoadGrowthClass(s.Role!.Id)!);
            Assert.Equal([1, 2], stored.RankReward.Keys.Order());
        }
        await LoggedOut("alice");
        await using var again = await InWorld("Alice", create: false);
        var full = await Next(again, GrowthClassService.Sync, DBGrowthClassInfoIncrement.Parser, m => m.SyncAllMark);
        Assert.Equal(1, full.RankReward[1].All);
        Assert.Equal(1, full.RankReward[2].All);
        Assert.Equal(10, full.RankProgress);
        Assert.Equal(3, full.ModuleUnlockedStage[Basics].All);
        Assert.Equal(ErrorCodes.RewardClaimed, (await Claim(again, 2)).Result);
    }
}
