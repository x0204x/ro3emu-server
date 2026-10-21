using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The PvE layer of Battle of Dragon Coffin Island (TimedBattleService.DragonIsland): two camps, the 30 s preparation,
/// the Dragonblood Crystal waves, the Demonic Dragon Soul and Fafnir, each scored by BattleEventPointConfig into the camp
/// score var 304. A two-player lobby on this host's cached DragonIslandMatchConfig row (the export asks for thirty).
/// </summary>
public sealed class DragonIslandTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-dragon-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    public async Task InitializeAsync()
    {
        var (port, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = port; o.MicroGatePort = micro; o.GameGatePort = game;
            o.SeasonStart = "2026-10-05";
        }, _clock);
        SureHitRolls.Install(_app);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{port}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
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

    private async Task TickAt(long second)
    {
        _clock.Set(second);
        await OnLoop(() => { Service<TimedBattleService>().Tick(default); return Task.CompletedTask; });
    }

    private Task<MonsterUnit[]> Units(long instance, Func<MonsterUnit, bool> which) => Service<GameLoop>().InvokeAsync(() =>
        Task.FromResult(Service<World>().AllUnits().OfType<MonsterUnit>().Where(m => m.InstanceId == instance && !m.Dead && which(m)).ToArray()));

    private async Task<MonsterUnit[]> WaitUnits(long instance, Func<MonsterUnit, bool> which)
    {
        for (int i = 0; i < 150; i++)
        {
            var found = await Units(instance, which);
            if (found.Length > 0) return found;
            await Task.Delay(20);
        }
        throw new TimeoutException("Expected Dragon Island units did not spawn");
    }

    private Task Beside(BotClient bot, float x, float y, float z) =>
        OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(x + 1, y, z, 0)));

    private static List<long> Camps(DungeonBaseInfoNotify info) => info.Vars.Single(v => v.VarId == 304).Vi64.ToList();

    [ClientDataFact]
    public async Task CrystalsSoulAndFafnirScoreTheCampsThroughTheBattle()
    {
        await OnLoop(() =>
        {
            var table = (Dictionary<string, JsonElement>)Service<GameData>().Table("DragonIslandMatchConfig");
            var row = JsonNode.Parse(table["6"].GetRawText())!.AsObject();
            row["_kStartQuantity"] = 2; row["_kTeamQuantity"] = 1;
            table["6"] = JsonSerializer.SerializeToElement(row);
            return Task.CompletedTask;
        });
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        foreach (var bot in new[] { alice, bob }) await Gm(bot, "!lvl 99");
        var window = ScheduleRows.Weekly(126, new DateTime(2026, 11, 10), _clock.LocalTimeZone, Service<GameData>());
        _clock.Set(window.Start + 300);
        foreach (var bot in new[] { alice, bob })
        {
            await bot.SendGameAsync(TimedBattleService.StartMatch, new StartMatchReq { MatchEntryId = 6 });
            Assert.Equal(0, (await Next(bot, TimedBattleService.StartMatch, StartMatchResp.Parser)).Result);
        }
        foreach (var bot in new[] { alice, bob })
        {
            await Next(bot, TimedBattleService.ReadyNotify, MatchReadyNotify.Parser);
            await bot.SendGameAsync(TimedBattleService.Ready, new MatchReadyReq { MatchEntryId = 6, PlayerId = (long)bot.Self!.Uuid, IsReady = true });
            Assert.Equal(0, (await Next(bot, TimedBattleService.Ready, MatchReadyResp.Parser)).Result);
        }
        long instance = 0;
        DungeonBaseInfoNotify loaded = null!;
        foreach (var bot in new[] { alice, bob })
        {
            var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 3010303);
            instance = sw.SceneID;
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 3010303 });
            loaded = await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => s.DungeonId == instance);
        }
        // Two camps, not six factions; each starts at its own point.
        Assert.Equal(1, Session(alice).EventFaction);
        Assert.Equal(2, Session(bob).EventFaction);
        Assert.Equal(0, loaded.Stage);
        long open = loaded.StageModuleData.StageStartTime / 1000;
        Assert.Equal(open + 30, loaded.StageModuleData.StageEndTime / 1000);
        Assert.Equal(open + 300, loaded.StageModuleData.NextStageEndTime / 1000);

        // 30 s: the battle stage and the first Dragonblood Crystal wave (2 groups x 6, DungeonLua_3010303).
        await TickAt(open + 31);
        await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => s.Stage == 1);
        int[] glasses = [16340002, 16340003, 16340004, 16340005];
        var crystals = await WaitUnits(instance, m => glasses.Contains(m.Template.NpcId));
        Assert.InRange(crystals.Length, 1, 12);
        var crystal = crystals[0];
        int skill = (int)GameData.Number(Service<GameData>().Row("NPCConfig", crystal.Template.NpcId)!.Value.GetProperty("_iInteractID")[0])!;
        await Beside(alice, crystal.X, crystal.Y, crystal.Z);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = crystal.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.Equal([1L, 10, 2, 0], Camps(await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser,
            s => Camps(s)[1] == 10)));
        Assert.Contains(Session(alice).Buffs, b => b.ConfigId == skill); // _kLogicData [buff = skill id, 30000]
        Assert.Empty(await Units(instance, m => m.Uid == crystal.Uid));

        // 50-60 s: the Demonic Dragon Soul; taking it is 50 points and var 925 names the carrier.
        await TickAt(open + 61);
        var soul = (await WaitUnits(instance, m => m.Template.NpcId == 16340007)).Single();
        await Beside(alice, soul.X, soul.Y, soul.Z);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 16340501, TargetId = soul.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var carried = await Next(alice, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => Camps(s)[1] == 60);
        Assert.Equal([(long)alice.Self!.Uuid], carried.Vars.Single(v => v.VarId == 925).Vi64);

        // 90 s: Fafnir; the first strike is 100 points for the striker's camp.
        await TickAt(open + 91);
        var boss = (await WaitUnits(instance, m => m.Template.NpcId == 16340001)).Single();
        await Beside(bob, boss.X, boss.Y, boss.Z);
        await bob.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = boss.Uid });
        Assert.Equal(0, (await Next(bob, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        // Scores are broadcast only when they change, so this one notice carries the camp's score before the kill.
        long redBefore = Camps(await Next(bob, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => Camps(s)[3] >= 100))[3];
        Assert.True(redBefore >= 100);

        // Killing the soul's carrier: the kill's 20 and the seized soul's 50 go to Bob's camp, and the soul with them.
        await OnLoop(() => Service<TimedBattleService>().PlayerKilledAsync(Session(bob), Session(alice)));
        var seized = await Next(bob, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, s => Camps(s)[3] >= redBefore + 70);
        Assert.Equal([(long)bob.Self!.Uuid], seized.Vars.Single(v => v.VarId == 925).Vi64);

        // The countdown's end: the higher camp wins.
        await TickAt(open + 301);
        var won = await Next(bob, TimedBattleService.DragonSettle, DragonIslandSettleNtf.Parser);
        Assert.Equal(1, won.ResultType);
        Assert.Equal(2, won.Snapshot.WinCamp);
        Assert.Equal((long)bob.Self.Uuid, long.Parse(won.Snapshot.MetaData["mvpPlayerId"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(2, (await Next(alice, TimedBattleService.DragonSettle, DragonIslandSettleNtf.Parser)).ResultType);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        public void Set(long value) => Interlocked.Exchange(ref _seconds, value);
    }
}
