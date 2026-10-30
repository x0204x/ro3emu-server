using System.Globalization;
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
/// Battle for Prontera invaders (PronteraDefenseService, MonsterService.March) on the real client data: they walk from
/// their point to the city's heart, take on the players they meet, City Defense falls each minute by the intensity and
/// a kill restores it, and the waves keep coming. The round is opened with the !defense test aid.
/// </summary>
public sealed class PronteraDefenseInvaderTests : IAsyncLifetime
{
    private const long Prontera = 10100;
    private const long MaxDefense = 100000;
    /// <summary>sc_prontera_001 born point 9, the heart the invaders walk to.</summary>
    private const float HeartX = 156.0406f, HeartZ = 181.9116f;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-defense-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void AdvanceSeconds(int count) => Interlocked.Add(ref _seconds, count);
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> OnLoop<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.PlayBook = 101; o.SeasonStart = "2026-10-05";
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [ClientDataFact]
    public async Task InvadersWalkFromTheirPointsToTheCityHeart()
    {
        await using var bot = await InWorld("Watcher");
        // Round 1, 30 s in: points 24 (845, 15 s), 25 (847, 20 s) and 21 (837, the South Gate, 30 s) have sent seven
        // Thief Bugs; points 1-10 (no waves) hold one actor each, all over the city (874 is 15 m from the heart).
        await OpenRoundAsync(bot, 30);
        var invaders = await Until(() => Invaders().Count(m => m.Template.NpcId == 4400001) >= 7 ? Invaders().ToArray() : null,
            "the first three waves");
        Assert.All(invaders, m => Assert.NotNull(m.March));
        var start = await OnLoop(() => invaders.ToDictionary(m => m, ToHeart));
        Assert.All(invaders.Where(m => m.Template.NpcId == 4400001), m => Assert.True(start[m] > 30, $"{start[m]:0.0} m at the start"));

        // They head for the heart over the ticks, and all arrive (the farthest, 845 and 880, are 150-170 m away).
        await Until(() => invaders.All(m => start[m] < 10 || ToHeart(m) < start[m] - 5) ? true : (bool?)null,
            "every invader closer to the heart", TimeSpan.FromSeconds(15));
        try
        {
            await Until(() => invaders.All(m => m.March is { Arrived: true }) ? true : (bool?)null,
                "every invader at the heart", TimeSpan.FromSeconds(90));
        }
        catch (TimeoutException)
        {
            Assert.Fail(await OnLoop(() => string.Join("; ", invaders.Where(m => m.March is not { Arrived: true }).Select(m =>
                string.Create(CultureInfo.InvariantCulture, $"{m.Template.NpcId} at {m.X:0.0},{m.Z:0.0} from {m.Home.X:0.0},{m.Home.Z:0.0} "
                    + $"failures {m.March!.Failures} route {m.March.Route.Count} moving {m.Moving} goal {m.March.GoalX:0.0},{m.March.GoalZ:0.0}")))));
        }
        await OnLoop(() =>
        {
            foreach (var m in invaders)
            {
                var march = m.March!;
                Assert.True(MonsterService.Distance(m.X, m.Z, march.GoalX, march.GoalZ) <= march.ArriveRadius + 0.3f,
                    $"{m.Uid} stopped at {m.X:0.0},{m.Z:0.0}, goal {march.GoalX:0.0},{march.GoalZ:0.0}");
                Assert.True(ToHeart(m) <= PronteraDefenseService.HeartSpread + PronteraDefenseService.HeartRadius + 1.5f);
                Assert.Equal(0, march.Failures);
            }
            return true;
        });
    }

    [ClientDataFact]
    public async Task AnInvaderTakesOnAPlayerItMeetsOnItsWay()
    {
        await using var bot = await InWorld("Defender");
        await Gm(bot, "!lvl 99");
        await WarpAsync(bot, Prontera);
        await OpenRoundAsync(bot, 30);
        var invader = await Until(() => Invaders().FirstOrDefault(m => m.Moving && ToHeart(m) > 40), "a marching invader");
        var session = await OnLoop(() => Session(bot));
        // Stand a few metres ahead of it on its way: inside its 5 m view (AIConfig 20200006), although nobody hits it.
        var (x, z) = await OnLoop(() =>
        {
            var (tx, tz) = invader.March!.Route.Count > 0 ? (invader.March.Route[0].X, invader.March.Route[0].Z) : (invader.ToX, invader.ToZ);
            float dx = tx - invader.X, dz = tz - invader.Z, len = MathF.Max(0.1f, MathF.Sqrt(dx * dx + dz * dz)), ahead = MathF.Min(3f, len);
            return (invader.X + dx / len * ahead, invader.Z + dz / len * ahead);
        });
        await Gm(bot, string.Create(CultureInfo.InvariantCulture, $"!jump {x:0.00} {z:0.00}"));
        await Until(() => ReferenceEquals(invader.Target, session) ? true : (bool?)null, "the invader takes the player");
        await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.UseInSkill)
            && UseInSkillInfo.Parser.ParseFrom(e.Body).SrcID == invader.Uid, Wait);
        // Its leash is centred where it left its route, not at its spawn point.
        await OnLoop(() =>
        {
            Assert.True(MonsterService.Distance(invader.March!.LeashX, invader.March.LeashZ, x, z) < 6f);
            return true;
        });
    }

    [ClientDataFact]
    public async Task CityDefenseFallsEachMinuteByTheIntensityAKillRestoresItAndTheWavesGoOn()
    {
        await using var bot = await InWorld("Keeper");
        await OpenRoundAsync(bot, 30);
        await Until(() => Invaders().Count() >= 7 ? true : (bool?)null, "the first waves");
        Assert.Equal(MaxDefense, await Defense());

        // Opening intensity 80000 (PronteraDefenseIntensityConfig stage [0, 80000]): 800 a minute.
        long loss = 80000 / PronteraDefenseService.ErosionDivisor;
        _clock.AdvanceSeconds(60);
        await UntilAsync(async () => await Defense() == MaxDefense - loss, "one minute of erosion");
        _clock.AdvanceSeconds(60);
        await UntilAsync(async () => await Defense() == MaxDefense - 2 * loss, "two minutes of erosion");

        // A Thief Bug (selector 10, difficulty 1) restores PronteraDefenseDifficultyConfig 1's _iKillPoint 800.
        var victim = await OnLoop(() => Invaders().First(m => m.Template.NpcId == 4400001));
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            var session = Session(bot);
            await Service<MonsterService>().KillAsync(victim);
            foreach (var killed in Service<CombatService>().Killed) await killed(session, victim);
        });
        await UntilAsync(async () => await Defense() == MaxDefense - loss, "the kill's restore");
        await OnLoop(() => { Assert.DoesNotContain(victim, Invaders()); return true; });
        Assert.Equal(1, await OnLoop(() => Session(bot).Defense!.ContributionPoint));

        // 180 s in: point 21's second wave (four Thief Bugs; its rift 90 has no monster template) and point 22's
        // second wave (two Thief Bugs, one Angeling) join.
        int before = await OnLoop(() => Invaders().Count());
        _clock.AdvanceSeconds(60);
        await Until(() => Invaders().Count() >= before + 7 ? true : (bool?)null, "the 180 s waves");
        Assert.DoesNotContain(await OnLoop(() => Invaders().ToArray()), m => m.Template.NpcId == 21001068);
    }

    private IEnumerable<MonsterUnit> Invaders() => Service<PronteraDefenseService>().Invaders.Where(m => !m.Dead);

    private static float ToHeart(MonsterUnit m) => MonsterService.Distance(m.X, m.Z, HeartX, HeartZ);

    private Task<long> Defense() => OnLoop(() =>
        Service<PronteraDefenseService>().BuildGlobal().MapInfo[Prontera].DefensePoint);

    /// <summary>!defense round 1 opens round 1 now; the clock then moves <paramref name="seconds"/> into it.</summary>
    private async Task OpenRoundAsync(BotClient bot, int seconds)
    {
        await Gm(bot, "!defense round 1");
        await Until(() => Service<PronteraDefenseService>().ProjectionPhase(_clock.GetUtcNow().ToUnixTimeSeconds()).Round == 1
            ? true : (bool?)null, "round 1 open");
        _clock.AdvanceSeconds(seconds);
    }

    private GameSession Session(BotClient bot) =>
        Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    /// <summary>A warp completed as a real client does it (ActivityAdventuresTests).</summary>
    private async Task WarpAsync(BotClient bot, long scene)
    {
        if (await OnLoop(() => Session(bot).SceneEntry) == scene) return;
        await Gm(bot, $"!warp {scene}");
        var change = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = change.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private async Task<T> Until<T>(Func<T?> read, string what, TimeSpan? within = null) where T : class
    {
        var end = DateTime.UtcNow + (within ?? Wait);
        while (true)
        {
            if (await OnLoop(read) is { } found) return found;
            if (DateTime.UtcNow > end) throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    private async Task<bool> Until(Func<bool?> read, string what, TimeSpan? within = null)
    {
        var end = DateTime.UtcNow + (within ?? Wait);
        while (true)
        {
            if (await OnLoop(read) is true) return true;
            if (DateTime.UtcNow > end) throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> check, string what)
    {
        var end = DateTime.UtcNow + Wait;
        while (!await check())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException(what);
            await Task.Delay(50);
        }
    }
}
