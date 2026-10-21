using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Valhalla Trials (ValhallaService): the layer gates, a one-time boss clear with its _iPassReward recorded in the
/// dungeon records, the 5046 state, the Tower Trial quest target 334001 and the Blessing statue.
/// OpenContent opens function 96 as the realm does.
/// </summary>
public sealed class ValhallaTests : IAsyncLifetime
{
    private const long FirstLayer = 100001, SecondLayer = 100002, Scene = 3301101;
    private const int FirstBoss = 5900011, Coin = 2000001;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-valhalla-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(2)));

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.SeasonStart = "2026-10-08";
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.OpenContent.Enabled = true;
        }, _clock);
        SureHitRolls.Install(_app);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
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

    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private static async Task<long> Challenge(BotClient bot, long layer)
    {
        await bot.SendGameAsync(ValhallaService.Challenge, new HeroicSpiritChallengeReq { ChallengeId = layer });
        return (await Next(bot, ValhallaService.Challenge, HeroicSpiritChallengeRes.Parser)).Err;
    }

    private static async Task<long> Load(BotClient bot)
    {
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == Scene);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = Scene });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == Scene)).Result);
        var info = await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, m => m.DungeonId == sw.SceneID);
        Assert.Equal(FirstLayer, info.InitData.HeroicSpiritDungeonInitData.ChallengeId);
        return sw.SceneID;
    }

    private async Task<MonsterUnit> BossIn(long instance)
    {
        for (int i = 0; i < 150; i++)
        {
            var actor = await Read(() => Service<World>().AllUnits().OfType<MonsterUnit>()
                .SingleOrDefault(m => m.InstanceId == instance && m.Template.NpcId == FirstBoss && !m.Dead));
            if (actor != null) return actor;
            await Task.Delay(20);
        }
        throw new TimeoutException("The layer's boss did not spawn");
    }

    private async Task Kill(BotClient bot, MonsterUnit actor)
    {
        await OnLoop(async () =>
        {
            await Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(actor.X + 1, actor.Y, actor.Z, actor.Face));
            Service<MonsterService>().Engage(actor, Session(bot));
            actor.Hp = 1;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = actor.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.Dsts.Any(d => d.DstID == actor.Uid && d.IsDead));
    }

    [ClientDataFact]
    public async Task ALayerIsGatedClearedOnceAndPaysItsRewardWithTheRecord()
    {
        await using var alice = await InWorld("Alice");
        var start = await Next(alice, ValhallaService.Sync, DBHeroicSpiritInfoIncrement.Parser);
        Assert.NotNull(start);

        Assert.Equal(ValhallaService.ErrPrevious, await Challenge(alice, SecondLayer)); // layer 1 not cleared
        Assert.Equal(ValhallaService.ErrLevel, await Challenge(alice, FirstLayer)); // base level 11 needed
        await Gm(alice, "!lvl 30");

        Assert.Equal(0, await Challenge(alice, FirstLayer));
        long instance = await Load(alice);
        await Kill(alice, await BossIn(instance));
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal((DungeonRes)1, result.Res);
        Assert.Equal(5, result.Rewards[Coin]); // AwardConfig 100201001: 5 Einherjar Coins, 10 Talent Remnants, 15000 Base EXP

        Assert.Contains(FirstLayer, await Read(() => DungeonService.ValhallaCleared(Session(alice).Dungeons).ToArray()));
        Assert.Equal(5, await Read(() => Service<CharacterService>().CountOf(Session(alice), Coin)));
        await Next(alice, ValhallaService.Sync, DBHeroicSpiritInfoIncrement.Parser, sync => sync.ToString().Contains(FirstLayer.ToString()));

        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        for (int i = 0; i < 150 && await Read(() => Session(alice).InstanceId) != 0; i++) await Task.Delay(20);
        Assert.Equal(ValhallaService.ErrDone, await Challenge(alice, FirstLayer)); // one-time
        Assert.Equal(5, await Read(() => Service<CharacterService>().CountOf(Session(alice), Coin)));

        // The Tower Trial quest "Clear Valhalla Mistress" (target 334001, layer 100001) is complete at once.
        await Gm(alice, "!quest accept 302040");
        for (int i = 0; i < 100 && !await Read(() => Session(alice).Tasks!.Infos.TryGetValue(302040, out var t) && t.Status == TaskState.Finish
            || TaskService.IsDelivered(Session(alice), 302040)); i++) await Task.Delay(20);
        Assert.True(await Read(() => Session(alice).Tasks!.Infos.TryGetValue(302040, out var t) && t.Status == TaskState.Finish
            || TaskService.IsDelivered(Session(alice), 302040)));
    }

    [ClientDataFact]
    public async Task ABlessingSpendsWhatTheLevelNeedsAndLevelsTheStatue()
    {
        await using var alice = await InWorld("Alice");
        async Task<long> Pray()
        {
            await alice.SendGameAsync(ValhallaService.Pray, new HeroicSpiritPrayReq());
            return (await Next(alice, ValhallaService.Pray, HeroicSpiritPrayRes.Parser)).Err;
        }
        Assert.Equal(ValhallaService.ErrCoins, await Pray()); // no coins
        await Gm(alice, $"!item {Coin} 4");
        Assert.Equal(0, await Pray());
        // Level 0 needs 5 EXP: 3 coins at 2 EXP each, level 1 with 1 EXP over; one coin stays.
        var statue = await Read(() => Session(alice).OdinStatue!.Clone());
        Assert.Equal(1, statue.CurLv);
        Assert.Equal(1, statue.CurExp);
        Assert.Equal(1, await Read(() => Service<CharacterService>().CountOf(Session(alice), Coin)));
        Assert.Equal(ValhallaService.ErrCoins, await Pray()); // level 1 needs 9 more EXP: 5 coins
        Assert.Equal(1, await Read(() => Session(alice).OdinStatue!.CurLv));
    }
}
