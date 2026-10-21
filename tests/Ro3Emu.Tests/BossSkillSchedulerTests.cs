using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The boss special-skill scheduler on real sockets: bosses cast their own skills over time within their cooldowns, phase
/// skills fire at their HP threshold, an area hits the players inside it only, and bosses without skill data swing as before.
/// </summary>
public sealed class BossSkillSchedulerTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-bossskills-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private GameLoop Loop => _app.Services.GetRequiredService<GameLoop>();
    private MonsterService Monsters => _app.Services.GetRequiredService<MonsterService>();
    private BossSkillOptions Options => _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.BossSkills;
    private GameSession Session(string name) => _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == name.ToLowerInvariant());

    public async Task InitializeAsync()
    {
        var ports = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = ports.Item1; o.MicroGatePort = ports.Item2; o.GameGatePort = ports.Item3;
            // Specials start at once and land no damage: a level 1 bot outlives a raid boss's rotation.
            o.BossSkills.FirstCastDelayMs = 0;
            o.BossSkills.DamagePercent = 0;
        });
        SureHitRolls.Install(_app);
        await _app.StartAsync(); _http = $"http://127.0.0.1:{ports.Item1}";
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
        var role = await bot.CreateRoleAsync(name); await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }

    /// <summary>The boss on the player's spot, not roaming or swinging (its normal attack stays out of the way), engaged on her.</summary>
    private Task<MonsterUnit> Boss(string name, int npcId, bool engage = true) => Loop.InvokeAsync(async () =>
    {
        var s = Session(name);
        var template = Monsters.Template(npcId);
        Assert.NotNull(template);
        var boss = await Monsters.SpawnSingleAsync(s.SceneEntry, npcId, new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, 0),
            s.InstanceId, customTemplate: template with { RoamDistance = 0, SpeedCm = 0 });
        Assert.NotNull(boss);
        if (engage)
        {
            Monsters.Engage(boss, s);
            boss.NextSwingMs = long.MaxValue;
        }
        return boss;
    });

    private static List<UseInSkillInfo> Casts(BotClient bot, long uid) => bot.Inbox.Snapshot()
        .Where(e => e.IsGame(CombatService.UseInSkill) || e.IsGame(CombatService.UseInSkillHigh))
        .Select(e => UseInSkillInfo.Parser.ParseFrom(e.Body)).Where(u => u.SrcID == uid).ToList();

    [ClientDataFact]
    public async Task ABossCastsItsSkillsOverTimeWithinTheirCooldowns()
    {
        await using var alice = await InWorld("Alice");
        Options.GlobalCooldownMs = 0; // only the rows' own timing spaces the casts
        // Phantom Realm Drake: its own 432xxxx block; each cast holds it for its action time and the next waits out the
        // row's _iPubCD (4320101 "tjpd": 7267 ms action, 9667 ms public cooldown).
        var drake = await Boss("alice", 43200000);
        var book = drake.Template.Skills;
        Assert.NotNull(book);
        await Task.Delay(TimeSpan.FromSeconds(16));
        var casts = Casts(alice, drake.Uid);
        Assert.True(casts.Count >= 2, $"{casts.Count} casts");
        Assert.All(casts, c => Assert.Contains(book.Skills, k => k.Id == c.SkillID));
        Assert.True(casts.Select(c => c.SkillID).Distinct().Count() >= 2);
        for (int i = 1; i < casts.Count; i++)
        {
            var previous = book.Skills.Single(k => k.Id == casts[i - 1].SkillID);
            // The next special waits for the previous one's cast and action to end and for its public cooldown.
            long gap = Math.Max(previous.BusyMs, previous.PublicCdMs);
            Assert.True(casts[i].StartTime - casts[i - 1].StartTime >= gap - 60,
                $"{casts[i].SkillID} {casts[i].StartTime - casts[i - 1].StartTime} ms after {previous.Id} ({gap} ms)");
        }
        foreach (var group in casts.GroupBy(c => c.SkillID))
        {
            var skill = book.Skills.Single(k => k.Id == group.Key);
            var times = group.Select(c => c.StartTime).Order().ToArray();
            for (int i = 1; i < times.Length; i++)
                Assert.True(times[i] - times[i - 1] >= skill.CdMs - 60, $"{skill.Id} recast after {times[i] - times[i - 1]} ms, cooldown {skill.CdMs}");
        }
        // Each cast carries its row's action time and a hit follows at the show's attack point.
        var first = casts[0];
        Assert.Equal(book.Skills.Single(k => k.Id == first.SkillID).ActionMs, first.SkillMs);
        Assert.True(first.SessionId > 0);
    }

    [ClientDataFact]
    public async Task AMiniBossWaitsOutItsCooldownThenCastsAgain()
    {
        await using var alice = await InWorld("Alice");
        // Rocker: one special, "Rocker Skills" 5101201 (21 s cooldown, 6.7 s action).
        var rocker = await Boss("alice", 5100000);
        var cast = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == rocker.Uid && u.SkillID == 5101201);
        var (ready, last) = await Loop.InvokeAsync(() => Task.FromResult((rocker.BossSkills!.ReadyMs[0], rocker.BossSkills.LastCastMs[0])));
        Assert.Equal(21000, ready - last);
        await Task.Delay(TimeSpan.FromSeconds(8)); // past its action: only the cooldown holds it back
        Assert.Single(Casts(alice, rocker.Uid), c => c.SkillID == 5101201);
        // The cooldown ends: it casts again (a new cast session).
        await Loop.InvokeAsync(() => { rocker.BossSkills!.ReadyMs[0] = MonsterService.NowMs; return Task.CompletedTask; });
        var again = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == rocker.Uid && u.SessionId != cast.SessionId);
        Assert.Equal(5101201, again.SkillID);
    }

    [ClientDataFact]
    public async Task PhaseVariantsOpenAtTheirHpThreshold()
    {
        await using var alice = await InWorld("Alice");
        Options.FirstCastDelayMs = 60_000; // nothing from the first phase gets in the way
        // Phantom Realm Orc Hero: "ssj"/"ssj2", "TD1"/"TD2", "DSJL"/"DSJL2", "DMLD"/"DMLD2" are its phase versions.
        var orc = await Boss("alice", 43100000);
        var book = orc.Template.Skills!;
        Assert.Equal(2, book.Phases);
        Assert.Equal([5000], book.PhaseHpBp);
        await Task.Delay(800);
        Assert.Empty(Casts(alice, orc.Uid));
        await Loop.InvokeAsync(() => { orc.Hp = orc.Template.MaxHp * 49 / 100; return Task.CompletedTask; });
        var opener = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == orc.Uid);
        int[] phaseTwo = [4314601, 4314701, 4314801, 4315001];
        Assert.Contains(opener.SkillID, phaseTwo);
        // Its other phase-two skills follow it before anything else; the first-phase versions are out of the rotation.
        await Task.Delay(TimeSpan.FromSeconds(12));
        var casts = Casts(alice, orc.Uid);
        Assert.All(casts, c => Assert.Contains(c.SkillID, phaseTwo));
        Assert.DoesNotContain(casts, c => c.SkillID is 4310701 or 4310801 or 4311201 or 4314301);
        Assert.Equal(1, await Loop.InvokeAsync(() => Task.FromResult(orc.BossSkills!.Phase)));
        // _iImportantSkill 1: each cast starts its cooldown on the boss panel (31125, absolute end time).
        var shown = await Next(alice, CombatService.BossSkillCd, BossSkillShowCdNotify.Parser, n => n.BossUId == orc.Uid && n.SkillId == opener.SkillID);
        Assert.Equal(opener.StartTime + book.Skills.Single(k => k.Id == opener.SkillID).CdMs, shown.EndSkillCdTime);
    }

    [ClientDataFact]
    public async Task AOnceOnlySkillFiresOnceAtTheThreshold()
    {
        await using var alice = await InWorld("Alice");
        Options.FirstCastDelayMs = 60_000;
        // Evil Warrior 5300042 lists 5350301 "MainStory_EvilKnightSkill3" with a 9999999 ms cooldown.
        var warrior = await Boss("alice", 5300042);
        var once = Assert.Single(warrior.Template.Skills!.Skills, k => k.Once);
        Assert.Equal(5350301, once.Id);
        await Loop.InvokeAsync(() => { warrior.Hp = warrior.Template.MaxHp / 3; return Task.CompletedTask; });
        await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == warrior.Uid && u.SkillID == 5350301);
        await Loop.InvokeAsync(() => { warrior.Hp = warrior.Template.MaxHp / 10; return Task.CompletedTask; });
        await Task.Delay(TimeSpan.FromSeconds(9)); // its 6.9 s action and more
        Assert.Single(Casts(alice, warrior.Uid), c => c.SkillID == 5350301);
        Assert.Equal(long.MaxValue, await Loop.InvokeAsync(() =>
            Task.FromResult(warrior.BossSkills!.ReadyMs[Array.FindIndex(warrior.Template.Skills!.Skills, k => k.Once)])));
    }

    [ClientDataFact]
    public async Task AnAreaSkillHitsThePlayersInsideAndNotOutside()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        Options.FirstCastDelayMs = 60_000;
        Options.DamagePercent = 1; // real damage, a level 99 Alice survives it
        await Gm("alice", "!lvl 99");
        // Field Orc Hero (not MvpService's: spawned here) and its Terrifying Roar 5203001: an 8 m circle around itself.
        var orc = await Boss("alice", 5020004);
        var roar = Assert.Single(orc.Template.Skills!.Skills, k => k.Id == 5203001);
        Assert.Equal((BossSkillArea.AroundSelf, 8f), (roar.Area, roar.Radius));
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(Session("bob"),
            new MapPoint(orc.X + 12, orc.Y, orc.Z, 0)));
        await Gm("alice", "!boss cast 5203001");
        var cast = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == orc.Uid && u.SkillID == 5203001);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == orc.Uid && h.SkillId == 5203001);
        Assert.Equal(cast.SessionId, hit.SessionId);
        long aliceId = Session("alice").Role!.Id, bobId = Session("bob").Role!.Id;
        Assert.Contains(hit.Dsts, d => d.DstID == aliceId && d.Damage > 0);
        await Task.Delay(500);
        foreach (var bot in new[] { alice, bob })
            Assert.DoesNotContain(bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
                .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SrcID == orc.Uid && h.Dsts.Any(d => d.DstID == bobId));
    }

    [ClientDataFact]
    public async Task BossesWithoutSkillDataSwingAsBefore()
    {
        await using var alice = await InWorld("Alice");
        Assert.Null(Monsters.Template(5000001)!.Skills);  // a field Poring
        Assert.Null(Monsters.Template(5020018)!.Skills);  // Kraken: an MVP-type boss with no AI script and no skills
        var kraken = await Boss("alice", 5020018, engage: false);
        await Loop.InvokeAsync(() => { Monsters.Engage(kraken, Session("alice")); return Task.CompletedTask; });
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.All(Casts(alice, kraken.Uid), c => Assert.Equal(kraken.Template.NormalSkill, c.SkillID));
        Assert.Null(kraken.BossSkills);
    }

    [ClientDataFact]
    public async Task DeathInterruptsTheCastAndRemovesTheSummons()
    {
        await using var alice = await InWorld("Alice");
        Options.FirstCastDelayMs = 60_000;
        // Boss Challenge Angeling runs AngelingMVP without MvpService: its Summon Self-Destruct Poring is generic here.
        var angeling = await Boss("alice", 81000001);
        await Gm("alice", "!boss cast 5202101");
        var cast = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == angeling.Uid && u.SkillID == 5202101);
        var world = _app.Services.GetRequiredService<World>();
        bool summoned = false;
        for (int attempt = 0; attempt < 100 && !summoned; attempt++)
        {
            summoned = await Loop.InvokeAsync(() => Task.FromResult(world.AllUnits().OfType<MonsterUnit>()
                .Count(m => m.Template.NpcId == 5000036 && !m.Dead) == 3));
            if (!summoned) await Task.Delay(100);
        }
        Assert.True(summoned, "three Self-Destruct Porings");
        // A second cast is under way when the boss dies: it ends interrupted (31103) and the porings go with it.
        await Gm("alice", "!boss cast 5201301");
        var ring = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == angeling.Uid && u.SkillID == 5201301);
        await Gm("alice", "!boss kill");
        var end = await Next(alice, CombatService.SkillEnd, SkillEndNotify.Parser, n => n.SrcID == angeling.Uid);
        Assert.Equal((ring.SessionId, SkillEndNotify.Types.SkillEndType.Interrupted), (end.SessionId, end.Type));
        Assert.NotEqual(cast.SessionId, end.SessionId);
        bool gone = false;
        for (int attempt = 0; attempt < 50 && !gone; attempt++)
        {
            gone = await Loop.InvokeAsync(() => Task.FromResult(!world.AllUnits().OfType<MonsterUnit>().Any(m => m.Template.NpcId == 5000036)));
            if (!gone) await Task.Delay(100);
        }
        Assert.True(gone, "the summons leave with their boss");
        // No hit of the interrupted Ring of Light lands afterwards.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)), e => SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == ring.SessionId);
    }

    [ClientDataFact]
    public async Task TheGmListsABossSkillSchedule()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!boss skills 43100000");
        var reply = await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
            && Encoding.UTF8.GetString(e.Body).Contains("Phantom Realm"), Wait);
        Assert.Contains("phases at HP 50%", Encoding.UTF8.GetString(reply.Body));
        await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify) && Encoding.UTF8.GetString(e.Body).Contains("4314601"), Wait);
        // "!boss spawn" puts the boss beside the GM, engaged on her.
        await Gm("alice", "!boss spawn 5100000");
        await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify) && Encoding.UTF8.GetString(e.Body).Contains("Spawned"), Wait);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<World>().AllUnits().OfType<MonsterUnit>()
            .Any(m => m.Template.NpcId == 5100000 && ReferenceEquals(m.Target, Session("alice"))))));
    }
}
