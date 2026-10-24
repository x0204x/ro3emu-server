using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Owner policy "everything open" (ServerOptions.OpenContent): functions the original never opened
/// but whose client panel works are open, server-day conditions count as met from the first day, and the playbook stays
/// 101. Off by default, the original's rules hold.
/// </summary>
public sealed class OpenContentTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-open-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.OpenContent.Enabled = true;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task<string> Gm(string account, string line)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [ClientDataFact]
    public async Task WorkingNeverOpenedFunctionsAndServerDayGatesAreOpenOnTheFirstDay()
    {
        await using var alice = await InWorld("Alice");
        var open = await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser, m => m.SyncAllMark);
        // Base level 9999/999 in FunctionConfig; their client panels work.
        Assert.True(open.FunctionOpenInfos[7201].All);  // Realm War map
        Assert.True(open.FunctionOpenInfos[70122].All); // Totem Shop
        Assert.True(open.FunctionOpenInfos[1000000].All); // Adv. Guild (AdventureGroupService), condition 2000335 = level 9999
        // Novice Challenge (90) stays closed: its event texts are filler (owner decision, ServerOptions.OpenContent).
        Assert.False(open.FunctionOpenInfos.ContainsKey(90));
        // Server day 1 meets "open day 3" (Guild League 60, [3031,3]), "day 4" (Ilmata 1324, [3031,4]) and "day 5"
        // (guild challenge 1323 "Gloom over Dragoncoffin Island", [3031,5]). None has a level gate, so every character
        // sees them: ClientHooks.ServerDayGates reads exactly this (a function reported open before its own day) as
        // "the server opens server-day gates".
        Assert.True(open.FunctionOpenInfos[60].All);
        Assert.True(open.FunctionOpenInfos[1324].All);
        Assert.True(open.FunctionOpenInfos[1323].All);
        // Feedback 41 opens with its backend (FeedbackService; the panel comes from ClientHooks.FeedbackButton).
        Assert.True(open.FunctionOpenInfos[41].All);
        // Title 14 opens with its backend (TitleService; the widget is hosted by ClientHooks.TitleButton).
        Assert.True(open.FunctionOpenInfos[14].All);
        // Shells without a working panel stay closed: Academy 34.
        Assert.False(open.FunctionOpenInfos.ContainsKey(34));
        // Ordinary level gates are untouched: friends open at level 15.
        Assert.False(open.FunctionOpenInfos.ContainsKey(45));
    }

    [ClientDataFact]
    public async Task TheExpiredNoviceWindowRepeatsSoTheEventAuctionTabsOpen()
    {
        // ActivityConfig 10000001 ran 2024-06-01..2025-08-01; it gates Event Auction tabs 3001-3003 in the client.
        var events = _app.Services.GetRequiredService<TimedEventService>();
        Assert.True(events.IsActive(10000001));
        Assert.True(events.WindowEnd(10000001) > DateTimeOffset.UtcNow);
        await using var alice = await InWorld("Alice");
        var status = await Next(alice, TimedEventService.StatusNotify, ActivityDataNtf.Parser, n => n.ActivityId == 10000001);
        Assert.Equal(ActivityStatus.Start, status.ActivityStatus);
        Assert.Equal(0, await _app.Services.GetRequiredService<AuctionService>().PublishEventAuctionAsync(
            "open-content", 3002, [(1200000, 1, 0)], [(long)alice.Self!.Uuid]));
    }

    [ClientDataFact]
    public async Task AForcedActivitySkipsItsOwnUnreachableCondition()
    {
        await using var alice = await InWorld("Alice");
        var services = _app.Services;
        var open = services.GetRequiredService<SeasonService>().OpenContent;
        Assert.False(open.ForcesActivity(112)); // Novice Challenge is not forced by default
        // Activity 112's own condition 21000010 is [1002,999]. Listing it forces the activity, not the condition.
        open.Activities = [112];
        await alice.SendGameAsync(NewActivityService.GetList, new GetActivityListReq());
        await Next(alice, NewActivityService.GetList, GetActivityListResp.Parser);
        var session = await services.GetRequiredService<GameLoop>().InvokeAsync(() => Task.FromResult(
            services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == alice.Account && s.Role != null)));
        var functions = services.GetRequiredService<FunctionService>();
        Assert.False(await services.GetRequiredService<GameLoop>().InvokeAsync(() => Task.FromResult(functions.ConditionMet(session, 21000010))));
        var info = await services.GetRequiredService<GameLoop>().InvokeAsync(() => Task.FromResult(session.NewActivities!.ActivityInfo[112]));
        Assert.False(info.IsShielded);
    }

    [ClientDataFact]
    public void ServerOpenAndSeasonStageWindowsAreOpenFromTheFirstDayAndNeverClose()
    {
        var clock = _app.Services.GetRequiredService<ActivityClock>();
        long now = clock.UtcNow.ToUnixTimeSeconds(), year = 365L * 86400;
        // 1012 Battle for Prontera: server open days 9-30. 1080 Pass I: season stage 0. 1066 card pool: days 10-14.
        foreach (long id in new long[] { 1012, 1080, 1066 })
        {
            var window = Assert.Single(clock.Segments(id, now));
            Assert.True(window.Contains(now), $"time limit {id} is closed");
            Assert.True(window.Contains(now + 10 * year), $"time limit {id} closes");
        }
        // Settlements keep their original bounds: 1021 Midterm Settlement starts with stage 101003, not today.
        Assert.DoesNotContain(clock.Segments(1021, now), w => w.Contains(now));
        // Otherworldly Invasion (102): 2026-01-01..11 absolute; it repeats with its 10-day length.
        var invasion = clock.ResolveActivity(_app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>().Row("NewActivityConfig", 102)!.Value, now, now, 101);
        Assert.True(invasion.Contains(now));
        Assert.Equal(864000, invasion.End - invasion.Start);
        Assert.True(clock.ResolveActivity(_app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>().Row("NewActivityConfig", 102)!.Value,
            invasion.End, now, 101).Start == invasion.End);
    }

    [ClientDataFact]
    public async Task EveryPetHatcheryRoundTakesEggsOnTheFirstDay()
    {
        // PetIncubationSlotConfig rounds follow activities 1015-1023 (server-open days 0-170). Perpetual, the client
        // offers the last round of each pool: 10009 (Permanent) and 10008 (Limited-Time).
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10104330");
        await Gm("alice", "!lvl 1");
        await Gm("alice", "!quest done 10105010");
        await Gm("alice", "!item 1250709 2");
        foreach (long round in new long[] { 10009, 10008 })
        {
            await alice.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = round, SlotId = 1 });
            Assert.Equal(0, (await Next(alice, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        }
    }

    [ClientDataFact]
    public async Task TheActivityListShowsTheReopenedEventsButNotTheTestPools()
    {
        await using var alice = await InWorld("Alice");
        await alice.SendGameAsync(NewActivityService.GetList, new GetActivityListReq());
        var list = await Next(alice, NewActivityService.GetList, GetActivityListResp.Parser);
        var ids = list.ActivityList.Select(a => a.Id).ToHashSet();
        Assert.Contains(118L, ids);  // Battle for Prontera
        Assert.Contains(1002L, ids); // S1 large card pool
        Assert.DoesNotContain(1011L, ids); // "Test Always Display Pool"
        Assert.DoesNotContain(1014L, ids);
    }

    [ClientDataFact]
    public void TerritoryWarsRepeatTheExportedBlockEveryCycleAsASeasonOfItsOwn()
    {
        var clock = _app.Services.GetRequiredService<TerritoryWarsClock>();
        long now = clock.Now, period = 14 * 86400L;
        // The exported block (2026-05-19..27) is long gone; the running or next repeat is at most one period away.
        Assert.True(clock.Cycle > 0);
        long blockStart = clock.Stages[0].StartTime, blockEnd = clock.Stages.Max(s => s.EndTime);
        Assert.InRange(blockEnd - blockStart, 8 * 86400L, 10 * 86400L);
        Assert.True(blockEnd > now && blockStart < now + period, $"block {blockStart}..{blockEnd} is not current or next at {now}");
        // Every repeat is a territory season of its own, so a cycle's awards are not keyed like the previous one's.
        Assert.EndsWith($":{clock.Cycle}", clock.Epoch);
        Assert.Equal($"{clock.SeasonStart}:{clock.PlayBook}:{clock.Cycle}", clock.Epoch);
    }

    [ClientDataFact]
    public async Task TheEscortSeasonFollowsTheWarCycle()
    {
        // Escort scores and claimed rounds are keyed by the escort season; with a repeating war block every cycle is a
        // season of its own, or round rewards of cycle 2 would read as already claimed in cycle 1.
        await using var alice = await InWorld("Alice");
        var clock = _app.Services.GetRequiredService<TerritoryWarsClock>();
        var state = await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => Task.FromResult(
            _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice").TerritoryEscort!));
        Assert.True(clock.Cycle > 0);
        Assert.Equal(clock.Cycle, state.Cycle);
        Assert.Equal(clock.SeasonStart, state.SeasonStart);
    }

    [Fact]
    public async Task SeasonCommandsDoNotLeavePlaybook101()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!season 102");
        await Next(alice, GmCommands.BroadcastNotify, BroadCastNotify.Parser, m => m.Content.StartsWith("OpenContent keeps playbook 101"));
        await Gm("alice", "!seasonnext 102 2026-11-01");
        await Next(alice, GmCommands.BroadcastNotify, BroadCastNotify.Parser, m => m.Content.StartsWith("OpenContent: there is no season rollover"));
        Assert.Equal(101, _app.Services.GetRequiredService<SeasonService>().PlayBook);
    }
}
