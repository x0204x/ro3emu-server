using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The RO1 aggression reference (Reconstructed/ro1-aggressive-monsters.json, switch Ro3Emu:Ro1AggressiveMonsters): field
/// monsters whose Ragnarok Online 1 counterpart is aggressive in rAthena's pre-renewal mob_db attack on sight like the
/// client's AIConfig 20000004 monsters. It is an RO1 reference, not client evidence.
/// </summary>
public sealed class Ro1AggressiveMonstersTests
{
    /// <summary>Listed: rAthena 1071 PIRATE_SKEL, Ai 04 (AIConfig 20000001 in the client).</summary>
    private const int PirateSkeleton = 5000064;
    /// <summary>AIConfig 20000004, aggressive by the client's own data.</summary>
    private const int Familiar = 5000019;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void TheEmbeddedListLoadsAndEveryEntryHasItsFields()
    {
        var monsters = MonsterService.LoadRo1AggressiveMonsters();
        Assert.NotEmpty(monsters);
        foreach (var (npcId, m) in monsters)
        {
            Assert.Equal(npcId, m.NpcId);
            Assert.True(m.NpcId > 0 && m.Ro1Id > 0, $"{npcId}: ids");
            Assert.False(string.IsNullOrWhiteSpace(m.Name), $"{npcId}: name");
            Assert.False(string.IsNullOrWhiteSpace(m.AegisName), $"{npcId}: aegisName");
            Assert.False(string.IsNullOrWhiteSpace(m.Ro1Name), $"{npcId}: ro1 name");
            Assert.True(m.Match.StartsWith("English name", StringComparison.Ordinal) || m.Match.StartsWith("variant:", StringComparison.Ordinal),
                $"{npcId}: match {m.Match}");
            Assert.Contains("rAthena", m.Why, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(m.Why), $"{npcId}: why");
        }

        // The file names its sources, and every unmatched name (none today) its reason.
        using var stream = typeof(MonsterService).Assembly.GetManifestResourceStream(MonsterService.Ro1AggressiveResource)!;
        using var doc = JsonDocument.Parse(stream);
        string about = doc.RootElement.GetProperty("about").GetString()!;
        Assert.Contains("https://raw.githubusercontent.com/rathena/rathena/master/db/pre-re/mob_db.yml", about);
        Assert.Contains("https://raw.githubusercontent.com/rathena/rathena/master/doc/mob_db_mode_list.txt", about);
        Assert.Contains("https://github.com/rathena/rathena/tree/master/npc/pre-re/mobs", about);
        Assert.Contains("not client evidence", about, StringComparison.OrdinalIgnoreCase);
        var unmatched = doc.RootElement.GetProperty("unmatched").EnumerateObject().ToArray();
        Assert.All(unmatched, u => Assert.False(string.IsNullOrWhiteSpace(u.Value.GetProperty("reason").GetString())));
        Assert.All(unmatched, u => Assert.False(monsters.ContainsKey(int.Parse(u.Name, System.Globalization.CultureInfo.InvariantCulture))));
    }

    [ClientDataFact]
    public async Task WithTheSwitchOnAListedMonsterEngagesAPlayerInView()
    {
        await using var host = await Host.StartAsync(ro1Aggressive: true);
        var monsters = host.Service<MonsterService>();
        Assert.True(monsters.Template(PirateSkeleton)!.Aggressive);
        Assert.True(monsters.Template(Familiar)!.Aggressive);

        await using var alice = await host.InWorld("Alice");
        var s = await host.SessionOf(alice);
        var pirate = await host.SpawnBeside(s, PirateSkeleton, 1.5f);
        var familiar = await host.SpawnBeside(s, Familiar, -1.5f);
        // Nobody hits them: both take Alice as their target and swing at her.
        await host.Until(() => ReferenceEquals(pirate.Target, s) && ReferenceEquals(familiar.Target, s), "both monsters take Alice");
        await alice.Inbox.WaitAsync(e => e.IsGame(CombatService.UseInSkill)
            && Romsg.UseInSkillInfo.Parser.ParseFrom(e.Body).SrcID == pirate.Uid, Wait);
    }

    [ClientDataFact]
    public async Task WithTheSwitchOffOnlyTheClientsAggressiveMonsterEngages()
    {
        await using var host = await Host.StartAsync(ro1Aggressive: false);
        var monsters = host.Service<MonsterService>();
        Assert.False(monsters.Template(PirateSkeleton)!.Aggressive);
        Assert.True(monsters.Template(Familiar)!.Aggressive);

        await using var alice = await host.InWorld("Alice");
        var s = await host.SessionOf(alice);
        var pirate = await host.SpawnBeside(s, PirateSkeleton, 1.5f);
        var familiar = await host.SpawnBeside(s, Familiar, -1.5f);
        await host.Until(() => ReferenceEquals(familiar.Target, s), "the 20000004 monster takes Alice");
        // The scan has run for the listed monster too (same tick, Alice in its view); a few more ticks change nothing.
        await Task.Delay(GameLoop.TickInterval * 6);
        Assert.Null(await host.OnLoop(() => pirate.Target));
    }

    [Fact]
    public void AListedMonsterFarAboveItsMapsLowestMonsterIsGated()
    {
        // Invented ids. A pool: 1 (level 10, max 0: never spawned), 2 (20, x10), 3 (40, x10), 6 (40, x10, not gateable:
        // the client's own aggressive AI or a boss), 7 (5, x1: under 10% of the population); a fixed group of 4 (30) and
        // 5 (36) on one point each.
        var monsters = new Dictionary<int, (int Level, bool Gated)>
        {
            [1] = (10, true), [2] = (20, false), [3] = (40, true), [4] = (30, true), [5] = (36, true), [6] = (40, false),
            [7] = (5, false),
        };
        var pool = new SpawnGroup(1, true, 31, 5, [(1, 0, 0), (2, 10, 10), (3, 10, 10), (6, 10, 10), (7, 1, 1)], []);
        var fixedGroup = new SpawnGroup(2, false, 2, 5, [], [new SpawnPoint(1, 0, 0, 0, 0, 4), new SpawnPoint(2, 0, 0, 0, 0, 5)]);
        (int, bool)? Monster(int npc) => monsters.TryGetValue(npc, out var m) ? m : null;
        // The lowest common level is 20 (7 is too rare to count): 3 (40) and 5 (36) are more than 15 above it; 4 (30) is
        // not; 6 is never gated.
        int[] gated = [3, 5], wider = [3];
        Assert.Equal(gated, MonsterService.Ro1PassiveNpcs([pool, fixedGroup], Monster, 15).Order());
        Assert.Equal(wider, MonsterService.Ro1PassiveNpcs([pool, fixedGroup], Monster, 16));
        Assert.Empty(MonsterService.Ro1PassiveNpcs([], Monster, 15));
        // With 7 common (x5 of 37), the lowest is 5: 4 (30) is gated as well.
        var commonWeak = pool with { Limits = [(2, 10, 10), (3, 10, 10), (6, 10, 10), (7, 5, 5)] };
        int[] all = [3, 4, 5];
        Assert.Equal(all, MonsterService.Ro1PassiveNpcs([commonWeak, fixedGroup], Monster, 15).Order());
    }

    [ClientDataFact]
    public async Task PayonForestKeepsItsFarStrongerListedMonstersPassive()
    {
        // sc_pay_fild10_001 pool 494: Horn 16, Boa 18, Elder Willow 34, Beetle King 55, Greatest General 55, Savage 59.
        await using var host = await Host.StartAsync(ro1Aggressive: true);
        var monsters = host.Service<MonsterService>();
        Assert.True(monsters.Template(GreatestGeneral)!.Aggressive); // still on the list
        int[] gated = [GreatestGeneral, ElderWillow];
        Assert.Equal(gated, (await host.OnLoop(() => monsters.Ro1PassiveOn(PayonForest))).Order());
        // Southeast Payon's lowest common monster is Caramel 25: Elder Willow (34) keeps the RO1 aggression there.
        Assert.DoesNotContain(ElderWillow, await host.OnLoop(() => monsters.Ro1PassiveOn(SoutheastPayon)));
        // Dungeons keep it whatever their levels: Payon Cave (MapConfig _iSceneType 1 but _iCaveBelongsTo 10201) spawns
        // Zombies at 17 among Archer Skeletons at 45; Orc Cave is a Cave scene (6).
        Assert.Empty(await host.OnLoop(() => monsters.Ro1PassiveOn(PayonCave)));
        Assert.Empty(await host.OnLoop(() => monsters.Ro1PassiveOn(OrcCave)));

        await using var alice = await host.InWorld("Alice");
        await host.Warp(alice, PayonForest, 30.8f, 292f);
        var s = await host.SessionOf(alice);
        var general = await host.SpawnBeside(s, GreatestGeneral, 1.5f);
        var pirate = await host.SpawnBeside(s, PirateSkeleton, -1.5f); // listed, but not one of this map's pools
        await host.Until(() => ReferenceEquals(pirate.Target, s), "the Pirate Skeleton takes Alice");
        await Task.Delay(GameLoop.TickInterval * 6);
        Assert.Null(await host.OnLoop(() => general.Target));
    }

    [ClientDataFact]
    public async Task AGapOfZeroLeavesEveryListedMonsterAggressive()
    {
        await using var host = await Host.StartAsync(ro1Aggressive: true, maxLevelGap: 0);
        Assert.Empty(await host.OnLoop(() => host.Service<MonsterService>().Ro1PassiveOn(PayonForest)));
    }

    private const int GreatestGeneral = 5000070, ElderWillow = 5000087;
    private const long PayonForest = 10220, SoutheastPayon = 10217, PayonCave = 10240, OrcCave = 10340;

    /// <summary>One server with the switch set: each test owns its host (several run in one process).</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-ro1aggr-" + Guid.NewGuid().ToString("N"));
        private WebApplication _app = null!;
        private string _http = "";

        public static async Task<Host> StartAsync(bool ro1Aggressive, int maxLevelGap = 15)
        {
            var host = new Host();
            var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
            host._app = ServerHost.Create([], o =>
            {
                o.DataDir = host._dataDir;
                o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
                o.HttpPort = httpPort;
                o.MicroGatePort = microPort;
                o.GameGatePort = gamePort;
                o.Ro1AggressiveMonsters = ro1Aggressive;
                o.Ro1AggressiveMaxLevelGap = maxLevelGap;
            });
            await host._app.StartAsync();
            host._http = $"http://127.0.0.1:{httpPort}";
            return host;
        }

        public T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

        public Task<T> OnLoop<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

        public async Task<BotClient> InWorld(string name)
        {
            var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
            await bot.LoginAsync(name.ToLowerInvariant(), "pw");
            await bot.EnterGateAsync();
            var role = await bot.CreateRoleAsync(name);
            await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
            using var http = new HttpClient();
            // Sturdy enough to outlast the swings while the test watches.
            (await http.PostAsync($"{_http}/ro3emu/gm?account={name.ToLowerInvariant()}", Operator.Command("!lvl 99"))).EnsureSuccessStatusCode();
            return bot;
        }

        /// <summary>A GM warp, then the client's 30005 scene-loaded report.</summary>
        public async Task Warp(BotClient bot, long scene, float x, float z)
        {
            using var sw = bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
            using var http = new HttpClient();
            (await http.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}",
                Operator.Command(FormattableString.Invariant($"!warp {scene} {x:0.0} {z:0.0}")))).EnsureSuccessStatusCode();
            long loaded = Romsg.SwitchSceneLineResp.Parser.ParseFrom((await sw.WaitAsync(Wait)).Body).SceneEntry;
            using var self = bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
            await bot.SendGameAsync(MsgId.SceneLoaded, new Romsg.SceneLoadedReq { SceneId = loaded });
            await self.WaitAsync(Wait);
        }

        public Task<GameSession> SessionOf(BotClient bot) =>
            OnLoop(() => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null));

        /// <summary>A monster <paramref name="dx"/> metres beside the player, inside its view (AIConfig _iViewRange 500).</summary>
        public Task<MonsterUnit> SpawnBeside(GameSession s, int npcId, float dx) =>
            Service<GameLoop>().InvokeAsync(async () =>
                (await Service<MonsterService>().SpawnSingleAsync(s.RuntimeMap, npcId,
                    new MapPoint(s.Position.X + dx, s.Position.Y, s.Position.Z, 0)))!);

        public async Task Until(Func<bool> condition, string what)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (!await OnLoop(condition))
            {
                if (elapsed.Elapsed >= Wait) throw new TimeoutException(what);
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dataDir, true); } catch (IOException) { }
        }
    }
}
