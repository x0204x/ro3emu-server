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

/// <summary>Raid boss trials (Reconstructed/raid-trials.json, DungeonService.RaidTrials), inferred as a whole: each raid is
/// entered alone (the !dungeonsolo test aid), its battle started and a trial started with the !raidtrial test aid, on the
/// real exported data with all content open (as the realm runs) and the boss specials on.</summary>
public sealed class RaidTrialTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-raid-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.SeasonStart = "2026-10-08";
            o.OpenContent.Enabled = true;
            o.ScriptedScenePacePercent = 10;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        }, TimeProvider.System);
        SureHitRolls.Install(_app);
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

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
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
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", Operator.Command(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All()
        .Single(s => s.Account == bot.Account && s.Role != null);

    private static EnterDungeonReq Request(int multi, int mark, long scene) => new()
    {
        SceneEntry = scene,
        InitData = new DungeonInitData { MultiDungeonInitData = new MultiDungeonInitData { MultiDungeonId = multi, MarkId = mark } },
    };

    private static async Task<long> Load(BotClient bot, long scene)
    {
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == scene);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == scene)).Result);
        await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, m => m.DungeonId == sw.SceneID);
        return sw.SceneID;
    }

    private async Task<MonsterUnit> BossIn(long instance, int npc)
    {
        for (int i = 0; i < 150; i++)
        {
            var actor = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
                .OfType<MonsterUnit>().FirstOrDefault(m => m.InstanceId == instance && m.Template.NpcId == npc && !m.Dead)));
            if (actor != null) return actor;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Boss {npc} did not spawn");
    }

    // Every reference of every trial resolves in the client data: the bosses are raid bosses, their scene has the points
    // and regions, the tips, colours, buffs, NPCs and skills exist, a destructible object can be a monster, an
    // interaction belongs to its NPC, and a breaker or a forced cast is in the boss's own set.
    [ClientDataFact]
    public void EveryRaidTrialResolvesInTheClientData()
    {
        var data = Service<GameData>();
        var raids = Service<ReconstructedDungeons>().AllRaids;
        Assert.NotEmpty(raids);
        foreach (var raid in raids)
            foreach (int boss in raid.Bosses)
            {
                var rows = data.Table("TenGameplayBossConfig").Values.Where(r => GameData.Int(r, "_iBoss") == boss).ToArray();
                Assert.True(rows.Length > 0, $"{raid.Key}: {boss} is no raid boss");
                var book = Service<MonsterService>().Template(boss)?.Skills;
                foreach (long scene in rows.Select(r => GameData.Int(r, "_iDungeon")).Distinct())
                {
                    var map = Service<WorldData>().Map(scene)!;
                    foreach (var trial in raid.Trials)
                    {
                        string where = $"{raid.Key} '{trial.Name}' ({boss} in {scene})";
                        Assert.All(trial.Tips.Concat(trial.TipByCount.Values), tip => Assert.True(data.Row("ChatConfig", tip) != null, $"{where}: tip {tip}"));
                        Assert.All(trial.Colors.Append(trial.MarkBuff).Append(trial.Carry.Buff).Where(b => b != 0),
                            b => Assert.True(data.Row("BuffConfig", b) != null, $"{where}: buff {b}"));
                        foreach (var outcome in new[] { trial.Fail, trial.Success }.OfType<ReconstructedDungeons.TrialOutcome>())
                        {
                            Assert.True(outcome.Skill == 0 || data.Row("SkillConfig", outcome.Skill) != null, $"{where}: skill {outcome.Skill}");
                            Assert.True(outcome.Stagger == 0 || data.Row("SkillConfig", outcome.Stagger) != null, $"{where}: stagger {outcome.Stagger}");
                            Assert.True(outcome.Buff == 0 || data.Row("BuffConfig", outcome.Buff) != null, $"{where}: buff {outcome.Buff}");
                            Assert.True(outcome.Say == 0 || data.Row("ChatConfig", outcome.Say) != null, $"{where}: say {outcome.Say}");
                        }
                        Assert.True(trial.Show == 0 || data.Row("SkillConfig", trial.Show) != null, $"{where}: show {trial.Show}");
                        foreach (long skill in trial.Breakers.Concat(trial.Casts.Select(c => c.Skill)))
                            Assert.True(book?.Skills.Any(k => k.Id == skill) == true, $"{where}: {skill} is not in the boss's set");
                        foreach (var o in trial.Objects)
                        {
                            Assert.True(o.Npcs.Count == 1 || o.Npcs.Count == trial.Colors.Count, $"{where}: one NPC per colour");
                            foreach (int npc in o.Npcs)
                            {
                                Assert.True(data.Row("NPCConfig", npc) != null, $"{where}: NPC {npc}");
                                if (o.Monster) Assert.True(Service<MonsterService>().Template(npc) != null, $"{where}: {npc} cannot be a monster");
                                if (o.Use != ReconstructedDungeons.TrialUse.None)
                                    Assert.NotEmpty(Service<DungeonCatalog>().Interactions(npc));
                            }
                            Assert.All(o.Buffs.Append(o.MarkBuff).Where(b => b != 0), b => Assert.True(data.Row("BuffConfig", b) != null, $"{where}: buff {b}"));
                            Assert.All(o.Place.Points, p => Assert.True(map.Points.ContainsKey(p), $"{where}: point {p}"));
                            Assert.All(o.Place.Regions, r => Assert.True(map.Regions.ContainsKey(r), $"{where}: region {r}"));
                            Assert.True(o.Place.Group == 0 || map.DungeonGroups.ContainsKey(o.Place.Group), $"{where}: group {o.Place.Group}");
                        }
                    }
                }
            }
    }

    [Fact]
    public void ATrialLaneLiesAheadOfItsObject()
    {
        var unit = new DungeonService.SceneActorUnit
        {
            Uid = 1, SceneEntry = 1, X = 0, Z = 0, Face = 0, NpcId = 1, Point = 0, EntityType = 4, Camp = 0, Name = "",
        };
        var spec = new ReconstructedDungeons.TrialObject([1], new ReconstructedDungeons.TrialPlace([], [], 0, false, false, false, 0, 0, 0, 0, 0),
            [], false, 0, false, ReconstructedDungeons.TrialUse.None, false, 0, 0, (40, 6));
        var lane = new DungeonService.TrialUnit { Unit = unit, Spec = spec };
        Assert.True(DungeonService.InTrialArea(lane, 3, 0, 20)); // ahead along its facing (0 degrees: +z)
        Assert.True(DungeonService.InTrialArea(lane, 3, 2.9f, 39));
        Assert.False(DungeonService.InTrialArea(lane, 3, 3.5f, 20)); // beside the lane
        Assert.False(DungeonService.InTrialArea(lane, 3, 0, -10)); // behind it
        var circle = new DungeonService.TrialUnit { Unit = unit, Spec = spec with { Lane = null } };
        Assert.True(DungeonService.InTrialArea(circle, 3, 2, 2));
        Assert.False(DungeonService.InTrialArea(circle, 3, 3, 3));
    }

    /// <summary>A raid entered alone (test aid), its battle started and its boss fighting the player beside it.</summary>
    private async Task<(long Instance, MonsterUnit Boss)> RaidFight(BotClient bot, int multi, int mark, long scene, int boss)
    {
        await Gm(bot, "!lvl 99");
        await Gm(bot, "!dungeonsolo on");
        await bot.SendGameAsync(DungeonService.Enter, Request(multi, mark, scene));
        Assert.Equal(0, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        long instance = await Load(bot, scene);
        if (DungeonService.StartBattleScenes.Contains(scene))
        {
            await bot.SendGameAsync(DungeonService.ModuleButton, new DungeonModuleButtonReq { ButtonId = 1 });
            Assert.Equal(0, (await Next(bot, DungeonService.ModuleButton, DungeonModuleButtonResp.Parser)).Result);
        }
        var actor = await BossIn(instance, boss);
        await OnLoop(async () =>
        {
            // The boss's own blows are not under test: they barely scratch, so the trials' hits are what the player feels.
            actor.Template = actor.Template with { Atk = 1, Matk = 1 };
            await Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(actor.X + 3, actor.Y, actor.Z, actor.Face));
            Service<MonsterService>().Engage(actor, Session(bot));
        });
        return (instance, actor);
    }

    private Task<T[]> InRoom<T>(long instance, Func<T, bool>? match = null) where T : WorldUnit =>
        Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits().OfType<T>()
            .Where(u => u.InstanceId == instance && (match == null || match(u))).ToArray()));

    private async Task<T[]> UntilInRoom<T>(long instance, Func<T, bool> match, int atLeast = 1) where T : WorldUnit
    {
        for (int i = 0; i < 150; i++)
        {
            var found = await InRoom(instance, match);
            if (found.Length >= atLeast) return found;
            await Task.Delay(20);
        }
        throw new TimeoutException("The trial's objects did not appear");
    }

    private Task<int> BuffOf(BotClient bot, IReadOnlyList<int> among) => Service<GameLoop>().InvokeAsync(() =>
        Task.FromResult(Session(bot).Buffs.Select(b => b.ConfigId).FirstOrDefault(among.Contains)));

    private static Task<SystemBoardCastMsgNtf> Tip(BotClient bot, int chat) =>
        Next(bot, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == chat);

    private static Task<SkillHitInfo> MechanicHit(BotClient bot, long skill) => Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
        h => h.SkillId == skill && h.Dsts.Any(d => d.DstID == (long)bot.Self!.Uuid));

    private long MaxHp(BotClient bot) => Service<CharacterService>().Stats(Session(bot))[101];

    // Isti's Ocean Concerto: a note for the player, bubbles of the notes in play; taking one of your note gives Bubble
    // Movement, each one left at the end costs everyone 10% of their maximum HP.
    [ClientDataFact]
    public async Task IstisConcertoRewardsTheMatchingBubbleAndPunishesTheOnesLeft()
    {
        int[] notes = [14720201, 14720501, 14720601, 14720701];
        int[] bubbles = [14720013, 14720014, 14720015, 14720016];
        await using var alice = await InWorld("Alice");
        var (instance, _) = await RaidFight(alice, 100101, 1, 3020101, 14720026);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 4110011);
        int note = await BuffOf(alice, notes);
        Assert.NotEqual(0, note);
        int mine = bubbles[Array.IndexOf(notes, note)];
        var shown = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => bubbles.Contains(u.NpcId), 4);
        Assert.Equal(4, shown.Length); // two per player, at least four
        Assert.All(shown, b => Assert.Equal(mine, b.NpcId)); // alone, every bubble is of the note she holds
        var first = shown[0];
        await Gm(alice, FormattableString.Invariant($"!actors walk {first.X} {first.Z}"));
        Assert.Equal(14720301, await BuffOf(alice, [14720301])); // Bubble Movement
        Assert.Equal(3, (await InRoom<DungeonService.SceneActorUnit>(instance, u => bubbles.Contains(u.NpcId))).Length);
        long max = MaxHp(alice);
        var hit = await MechanicHit(alice, 14721101); // three bubbles reach the orb: 30%
        Assert.Equal(max * 30 / 100, hit.Dsts.Single().Damage);
        Assert.Empty(await InRoom<DungeonService.SceneActorUnit>(instance, u => bubbles.Contains(u.NpcId)));
        Assert.Equal(0, await BuffOf(alice, notes)); // the note ends with the concerto
    }

    private Task WalkTo(BotClient bot, WorldUnit at) => Gm(bot, FormattableString.Invariant($"!actors walk {at.X} {at.Z}"));

    // Nasha's dance test: a dancer in a circle when the 15 s run out keeps up (Perfect State and her praise).
    [ClientDataFact]
    public async Task NashasDancerInTheCircleKeepsUp()
    {
        await using var alice = await InWorld("Alice");
        var (instance, _) = await RaidFight(alice, 100101, 2, 3020102, 14740100);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 4618);
        Assert.Equal("2", (await Tip(alice, 4611)).ParamList[1].Params[0]); // 15 s at the test pace: the window's seconds
        var circle = Assert.Single(await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 14740002));
        Assert.Equal(4, circle.EntityType); // a trigger, as NPCConfig types it
        await WalkTo(alice, circle);
        await Tip(alice, 4616); // "someone who can keep up with my steps"
        Assert.Equal(14742501, await BuffOf(alice, [14742501])); // Perfect State
        Assert.Empty(await InRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 14740002));
    }

    // Ilmo's Beat Prison: the notes are picked up from their NPCs; every circle lit by its note raises the barrier.
    [ClientDataFact]
    public async Task IlmosBeatPrisonIsSolvedByLightingEveryCircleWithItsNote()
    {
        int[] pickups = [14760001, 14760002, 14760003, 14760004], circles = [14760005, 14760006, 14760007, 14760008];
        int[] notes = [14760001, 14760101, 14760201, 14760301];
        await using var alice = await InWorld("Alice");
        var (instance, _) = await RaidFight(alice, 100101, 3, 3020103, 14761100);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 4606);
        var shown = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => circles.Contains(u.NpcId), 2);
        var notesShown = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => pickups.Contains(u.NpcId), 4);
        // Walking into a circle without its note lights nothing.
        await WalkTo(alice, shown[0]);
        Assert.Equal(2, (await InRoom<DungeonService.SceneActorUnit>(instance, u => circles.Contains(u.NpcId))).Length);
        foreach (var circle in shown)
        {
            int color = Array.IndexOf(circles, circle.NpcId);
            var pickup = notesShown.Single(n => n.NpcId == pickups[color]);
            await WalkTo(alice, pickup);
            long interaction = Service<DungeonCatalog>().Interactions(pickup.NpcId).Single();
            await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
            { NpcUid = pickup.Uid, InteractionId = (int)interaction, SceneConfigId = 3020103 });
            Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser, r => r.Req.NpcUid == pickup.Uid)).Result);
            for (int i = 0; i < 100 && await BuffOf(alice, notes) != notes[color]; i++) await Task.Delay(20);
            Assert.Equal(notes[color], await BuffOf(alice, notes));
            await WalkTo(alice, circle);
        }
        for (int i = 0; i < 100 && await BuffOf(alice, [14761401]) == 0; i++) await Task.Delay(20);
        Assert.Equal(14761401, await BuffOf(alice, [14761401])); // Green Energy Barrier
        Assert.Empty(await InRoom<DungeonService.SceneActorUnit>(instance, u => circles.Contains(u.NpcId) || pickups.Contains(u.NpcId)));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 14769000);
    }

    // Mirko: a lone tester gets one safe zone (the two-zone line); leaving it empty lets the beat explode. Then the
    // instruments of her colour stand at the edge; left alive, each plays its full-field AOE.
    [ClientDataFact]
    public async Task MirkosEmptySafeZoneAndLiveInstrumentsCostHp()
    {
        await using var alice = await InWorld("Alice");
        var (instance, _) = await RaidFight(alice, 100101, 4, 3020108, 14780007);
        long max = MaxHp(alice);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 4622);
        Assert.Single(await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 14780006));
        Assert.Equal(max * 50 / 100, (await MechanicHit(alice, 14782101)).Dsts.Single().Damage);

        await Gm(alice, "!raidtrial 2");
        await Tip(alice, 4607);
        int note = await BuffOf(alice, [14788001, 14788101]);
        int horn = note == 14788001 ? 14780014 : 14780015;
        var instruments = await UntilInRoom<MonsterUnit>(instance, m => m.Template.NpcId is 14780014 or 14780015, 2);
        Assert.All(instruments, m => Assert.Equal(horn, m.Template.NpcId)); // alone: both of her colour
        Assert.All(instruments, m => Assert.Equal(0, m.Template.SpeedCm)); // they stand
        var aoe = await MechanicHit(alice, 14783501);
        Assert.Equal(max * 40 / 100, aoe.Dsts.Single().Damage); // two left: 2 x 20%
        Assert.Empty(await InRoom<MonsterUnit>(instance, m => m.Template.NpcId is 14780014 or 14780015 && !m.Dead));
    }

    private async Task Strike(BotClient bot, MonsterUnit target)
    {
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(target.X + 1, target.Y, target.Z, target.Face)));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    private async Task UntilBuffGone(BotClient bot, int buff)
    {
        for (int i = 0; i < 200 && await BuffOf(bot, [buff]) != 0; i++) await Task.Delay(20);
        Assert.Equal(0, await BuffOf(bot, [buff]));
    }

    // Orc Hero: the blade picked up and brought to him breaks his raised shield (no full-screen AOE); the dropped shield
    // covers its carrier from the kunai.
    [ClientDataFact]
    public async Task OrcHerosBladeBreaksHisShieldAndTheShieldBlocksTheKunai()
    {
        await using var alice = await InWorld("Alice");
        var (instance, orc) = await RaidFight(alice, 50101, 2, 3120101, 43100003);
        await Gm(alice, "!raidtrial 1");
        Assert.Equal("2", (await Tip(alice, 4311)).ParamList[1].Params[0]);
        var blade = Assert.Single(await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 43100002));
        await WalkTo(alice, blade);
        await Tip(alice, 4314404); // "Use the skill granted by the giant blade while facing Orc Hero!"
        Assert.Equal(4310401, await BuffOf(alice, [4310401]));
        await Strike(alice, orc);
        await Tip(alice, 4314405); // the shield broke and dropped
        await UntilBuffGone(alice, 4310401);
        Assert.True(await Service<GameLoop>().InvokeAsync(() => Task.FromResult(orc.BusyUntilMs > MonsterService.NowMs))); // staggered

        await Gm(alice, "!raidtrial 2");
        await Tip(alice, 4314406);
        Assert.Equal(4311601, await BuffOf(alice, [4311601])); // alone, she is the marked one
        var shield = Assert.Single(await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 43100004));
        await WalkTo(alice, shield);
        Assert.Equal(4310601, await BuffOf(alice, [4310601]));
        await UntilBuffGone(alice, 4311601); // the trial is over

        await Gm(alice, "!raidtrial 3"); // Blade Deflection: alone, nobody for the kunai to ricochet to
        for (int i = 0; i < 100 && await BuffOf(alice, [4311601]) == 0; i++) await Task.Delay(20);
        Assert.Equal(4311601, await BuffOf(alice, [4311601]));
        await UntilBuffGone(alice, 4311601);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId is 4311801 or 4312501 or 4310901);
    }

    // Drake: a barrel landing on a ghost ship sinks it; the ship left charges its lane and hits whoever stands in it.
    // Then the puppets left at the end strike everyone.
    [ClientDataFact]
    public async Task DrakesBarrelsSinkAShipAndTheOtherChargesItsLane()
    {
        await using var alice = await InWorld("Alice");
        var (instance, drake) = await RaidFight(alice, 50101, 3, 3120201, 43200006);
        long max = MaxHp(alice);
        await Gm(alice, "!raidtrial 1");
        Assert.Equal("2", (await Tip(alice, 5002)).ParamList[1].Params[0]);
        var ships = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 43200007, 2);
        Assert.Equal(2, ships.Length);
        Assert.All(ships, s => Assert.Equal(10, s.EntityType)); // a bullet unit, as NPCConfig types it
        var barrels = drake.Template.Skills!.Skills.Single(k => k.Id == 4323101);
        await OnLoop(async () =>
        {
            foreach (var landed in Service<CombatService>().BossAreaLanded) await landed(drake, barrels, ships[1].Pos, ships[1].Pos, 0);
        });
        Assert.Single(await InRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 43200007));
        // 20 m ahead of the other ship, in its lane.
        double turn = ships[0].Face * Math.PI / 180;
        await Gm(alice, FormattableString.Invariant($"!actors walk {ships[0].X + 20 * (float)Math.Sin(turn)} {ships[0].Z + 20 * (float)Math.Cos(turn)}"));
        Assert.Equal(max * 60 / 100, (await MechanicHit(alice, 4323201)).Dsts.Single().Damage);

        await Gm(alice, "!hp 999999999");
        await Gm(alice, "!raidtrial 2");
        await Tip(alice, 5001);
        var puppets = await UntilInRoom<MonsterUnit>(instance, m => m.Template.NpcId == 43200001 && !m.Dead, 2);
        Assert.Equal(2, puppets.Length); // at least two
        Assert.Equal(max * 30 / 100, (await MechanicHit(alice, 4321501)).Dsts.Single().Damage);
    }

    /// <summary>Breaks a trial's destructible object with one hit (its HP brought down first).</summary>
    private async Task Break(BotClient bot, MonsterUnit target)
    {
        await OnLoop(() => { target.Hp = 1; return Task.CompletedTask; });
        await Strike(bot, target);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == target.Uid && d.IsDead));
    }

    // Moonlight Flower: the bell cage on the marked player is broken in time; the charged bell (shown for a moment) is the
    // one to break; standing in the moonlight costs HP and sight.
    [ClientDataFact]
    public async Task MoonlightFlowersBellsAreBrokenAndHerMoonlightPunishesTheStill()
    {
        await using var alice = await InWorld("Alice");
        var (instance, _) = await RaidFight(alice, 50103, 2, 3120801, 4380000);
        long max = MaxHp(alice);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 5005);
        Assert.Equal(4382301, await BuffOf(alice, [4382301]));
        var cage = Assert.Single(await UntilInRoom<MonsterUnit>(instance, m => m.Template.NpcId == 4380002 && !m.Dead));
        await Break(alice, cage);
        await UntilBuffGone(alice, 4382301); // set free

        await Gm(alice, "!raidtrial 2");
        await Tip(alice, 5007);
        await UntilInRoom<MonsterUnit>(instance, m => m.Template.NpcId == 4380004 && !m.Dead, 3);
        var charged = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
            (MonsterUnit)Session(alice).DungeonAttempt!.Raid!.Active!.Objects.Single(o => o.Chosen).Unit));
        await Break(alice, charged);
        for (int i = 0; i < 100 && await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(alice).DungeonAttempt!.Raid!.Active != null)); i++)
            await Task.Delay(20);
        Assert.Empty(await InRoom<MonsterUnit>(instance, m => m.Template.NpcId == 4380004 && !m.Dead));

        await Gm(alice, "!raidtrial 3");
        await Tip(alice, 5006);
        Assert.Single(await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4380006)); // under her
        Assert.Equal(max * 35 / 100, (await MechanicHit(alice, 4380701)).Dsts.Single().Damage);
        Assert.Equal(4381901, await BuffOf(alice, [4381901])); // Blind
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId is 4380501 or 4380601);
    }

    // Golden Thief Bug: a wormhole takes the player to the next one; a storm lane left too late costs HP. Lured onto a
    // burrow when the count ends, he burrows there instead of sweeping the field.
    [ClientDataFact]
    public async Task GoldenThiefBugsWormholesCarryAndHisBurrowIsLured()
    {
        await using var alice = await InWorld("Alice");
        var (instance, bug) = await RaidFight(alice, 50101, 1, 3120401, 4300001);
        long max = MaxHp(alice);
        Pace(50); // the wormhole needs a little time
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 4802);
        var storms = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4300004, 2);
        var holes = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4300007, 4);
        Assert.Equal(4, holes.Length);
        await WalkTo(alice, holes[0]);
        long interaction = Service<DungeonCatalog>().Interactions(4300007)[0];
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { NpcUid = holes[0].Uid, InteractionId = (int)interaction, SceneConfigId = 3120401 });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser, r => r.Req.NpcUid == holes[0].Uid)).Result);
        for (int i = 0; i < 100 && MonsterService.Distance(Session(alice).Position.X, Session(alice).Position.Z, holes[0].X, holes[0].Z) < 10; i++)
            await Task.Delay(20);
        Assert.True(MonsterService.Distance(Session(alice).Position.X, Session(alice).Position.Z, holes[0].X, holes[0].Z) >= 10); // came out of another
        // 10 m into the first storm's lane.
        double turn = storms[0].Face * Math.PI / 180;
        await Gm(alice, FormattableString.Invariant($"!actors walk {storms[0].X + 10 * (float)Math.Sin(turn)} {storms[0].Z + 10 * (float)Math.Cos(turn)}"));
        Assert.Equal(max * 40 / 100, (await MechanicHit(alice, 4305201)).Dsts.Single().Damage);

        await Gm(alice, "!hp 999999999");
        Pace(10);
        await Gm(alice, "!raidtrial 2");
        await Tip(alice, 4803);
        Assert.Equal("2", (await Tip(alice, 4809)).ParamList[1].Params[0]);
        var burrow = (await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4300002, 2))[0];
        await OnLoop(async () =>
        {
            await Service<SceneService>().TeleportInMapAsync(Session(alice), new MapPoint(burrow.X, burrow.Y, burrow.Z, 0));
            // The tank brings him onto it (pinned there: the test does not drive his chase).
            bug.Moving = false;
            bug.X = burrow.X + 1;
            bug.Z = burrow.Z;
            bug.BusyUntilMs = MonsterService.NowMs + 60000;
        });
        await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == bug.Uid && u.SkillID == 4305901); // he burrows
        await UntilGone(instance, 4300002);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 4305301);
    }

    /// <summary>The scripted pace (percent of the real timing) for what follows.</summary>
    private void Pace(int percent) => Service<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value.ScriptedScenePacePercent = percent;

    /// <summary>Walks a player <paramref name="metres"/> ahead of an object along its facing (into its lane).</summary>
    private Task WalkAhead(BotClient bot, WorldUnit from, float metres)
    {
        double turn = from.Face * Math.PI / 180;
        return Gm(bot, FormattableString.Invariant($"!actors walk {from.X + metres * (float)Math.Sin(turn)} {from.Z + metres * (float)Math.Cos(turn)}"));
    }

    // Mistress: a hornet lane costs HP; standing in the targeted zone too; at half HP she grows angry.
    [ClientDataFact]
    public async Task MistresssHornetsPunishTheirLanesAndHerChargeItsZone()
    {
        await using var alice = await InWorld("Alice");
        var (instance, mistress) = await RaidFight(alice, 50102, 1, 3120301, 4330001);
        long max = MaxHp(alice);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 4314409);
        var hornets = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4330006, 8);
        Assert.Equal(8, hornets.Length); // four from the north, four from the west
        await WalkAhead(alice, hornets[0], 30);
        Assert.Equal(max * 40 / 100, (await MechanicHit(alice, 4331301)).Dsts.Single().Damage);

        await Gm(alice, "!hp 999999999");
        await Gm(alice, "!raidtrial 3");
        await Tip(alice, 4314410);
        Assert.Single(await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4330004)); // under her
        Assert.Equal(max * 45 / 100, (await MechanicHit(alice, 4331501)).Dsts.Single().Damage);

        await Gm(alice, "!hp 999999999");
        await OnLoop(() => { mistress.Hp = mistress.Template.MaxHp * 49 / 100; return Task.CompletedTask; });
        await Tip(alice, 4314411); // angry at half HP
        await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4330006, 4);
    }

    /// <summary>Walks a player into an object's shadow: <paramref name="metres"/> past it as seen from the boss.</summary>
    private Task WalkBehind(BotClient bot, WorldUnit cover, MonsterUnit boss, float metres)
    {
        float dx = cover.X - boss.X, dz = cover.Z - boss.Z, d = MathF.Sqrt(dx * dx + dz * dz);
        return Gm(bot, FormattableString.Invariant($"!actors walk {cover.X + dx / d * metres} {cover.Z + dz / d * metres}"));
    }

    // Maya: the stone pillar's shadow keeps the quicksand off; the mist petrifies whoever stands in it; at 40% the meteors.
    [ClientDataFact]
    public async Task MayasPillarsShelterFromTheQuicksandAndHerMistPetrifies()
    {
        await using var alice = await InWorld("Alice");
        var (instance, maya) = await RaidFight(alice, 50102, 2, 3120501, 4340005);
        long max = MaxHp(alice);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 5201);
        var pillars = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4340006, 2);
        Assert.Equal(2, pillars.Length); // at least two
        await WalkBehind(alice, pillars[0], maya, 3);
        await UntilGone(instance, 4340006);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 4342601);

        await Gm(alice, "!raidtrial 2");
        Assert.Single(await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4340015)); // under her
        Assert.Equal(max * 30 / 100, (await MechanicHit(alice, 4342301)).Dsts.Single().Damage);
        Assert.Equal(4340301, await BuffOf(alice, [4340301])); // Petrify

        await Gm(alice, "!hp 999999999");
        await OnLoop(() => { maya.Hp = maya.Template.MaxHp * 39 / 100; return Task.CompletedTask; });
        await Tip(alice, 5202);
        Assert.Equal("1", (await Tip(alice, 5203)).ParamList[1].Params[0]); // 8 s at the test pace
        await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4340006, 2);
        Assert.Equal(max * 50 / 100, (await MechanicHit(alice, 4343401)).Dsts.Single().Damage); // left in the open
    }

    // Osiris: his Dark Pulse, aimed at a player standing behind a Horong, detonates it; every Horong gone, no split. The
    // star-shift circle punishes a player who stands still.
    [ClientDataFact]
    public async Task OsirissDarkPulseDetonatesTheHorongsAndHisStarShiftTheStill()
    {
        await using var alice = await InWorld("Alice");
        var (instance, osiris) = await RaidFight(alice, 50102, 3, 3120601, 4360004);
        long max = MaxHp(alice);
        Pace(50); // a pulse every 2 s
        await OnLoop(() => { osiris.Moving = false; osiris.BusyUntilMs = MonsterService.NowMs + 60000; return Task.CompletedTask; });
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 5003);
        var horongs = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4360006, 2);
        Assert.Equal(2, horongs.Length); // at least two
        foreach (var horong in horongs)
        {
            await OnLoop(() =>
            {
                float dx = horong.X - osiris.X, dz = horong.Z - osiris.Z, d = MathF.Sqrt(dx * dx + dz * dz);
                return Service<SceneService>().TeleportInMapAsync(Session(alice), new MapPoint(horong.X + dx / d * 3, horong.Y, horong.Z + dz / d * 3, 0));
            });
            await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, u => u.SrcID == osiris.Uid && u.SkillID == 4361301);
            for (int i = 0; i < 100 && (await InRoom<DungeonService.SceneActorUnit>(instance, u => u.Uid == horong.Uid)).Length > 0; i++)
                await Task.Delay(20);
            Assert.Empty(await InRoom<DungeonService.SceneActorUnit>(instance, u => u.Uid == horong.Uid)); // detonated
        }
        for (int i = 0; i < 100 && await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(alice).DungeonAttempt!.Raid!.Active != null)); i++)
            await Task.Delay(20);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 4361401);

        Pace(10);
        await Gm(alice, "!raidtrial 2");
        Assert.Equal("1", (await Tip(alice, 5004)).ParamList[1].Params[0]);
        Assert.Single(await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4360008));
        Assert.Equal(max * 35 / 100, (await MechanicHit(alice, 4361701)).Dsts.Single().Damage);
    }

    // Pharaoh: the coffin around the trapped player is broken in time; the pyramid's shadow keeps the Divine Light off;
    // a lone marked player has nobody to hurt; the pyramid's Death Ray burns its lane.
    [ClientDataFact]
    public async Task PharaohsCoffinIsBrokenHisLightShelteredAndHisRayBurnsItsLane()
    {
        await using var alice = await InWorld("Alice");
        var (instance, pharaoh) = await RaidFight(alice, 50103, 1, 3120701, 4370000);
        long max = MaxHp(alice);
        await Gm(alice, "!raidtrial 1");
        Assert.Equal("Alice", (await Tip(alice, 4370001)).ParamList[1].Params[0]); // "Your teammate Alice is trapped"
        Assert.Equal(4370501, await BuffOf(alice, [4370501])); // the Shroud
        var coffin = Assert.Single(await UntilInRoom<MonsterUnit>(instance, m => m.Template.NpcId == 4370002 && !m.Dead));
        await Break(alice, coffin);
        await UntilBuffGone(alice, 4370501);

        await Gm(alice, "!raidtrial 2");
        await Tip(alice, 4370002);
        var pyramids = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4370003, 4);
        Assert.Equal(4, pyramids.Length);
        await WalkBehind(alice, pyramids[0], pharaoh, 3);
        await UntilGone(instance, 4370003);

        await Gm(alice, "!raidtrial 3");
        await Tip(alice, 4370005);
        Assert.Equal(4374901, await BuffOf(alice, [4374901]));
        await UntilBuffGone(alice, 4374901); // alone: nobody beside her

        await Gm(alice, "!raidtrial 4");
        var eyes = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4370004, 2);
        await WalkAhead(alice, eyes[0], 15);
        Assert.Equal(max * 50 / 100, (await MechanicHit(alice, 4375101)).Dsts.Single().Damage);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId is 4372101 or 4371101 or 4370401);
    }

    // Baphomet: a stone pillar's shadow keeps his full-screen attack off, then the pillar blasts those beside it; the
    // illusions stand still and are cut down in time; at 60% his scythes punish whoever stands still.
    [ClientDataFact]
    public async Task BaphometsPillarsShelterThenBlastAndHisIllusionsAreCutDown()
    {
        await using var alice = await InWorld("Alice");
        var (instance, baphomet) = await RaidFight(alice, 50103, 3, 3120901, 4390000);
        long max = MaxHp(alice);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 4314472);
        var pillars = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4390004, 6);
        Assert.Equal(6, pillars.Length);
        await WalkBehind(alice, pillars[0], baphomet, 3);
        await UntilGone(instance, 4390004);

        await Gm(alice, "!raidtrial 2");
        await Tip(alice, 4314475);
        var again = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4390004, 6);
        await WalkTo(alice, again[0]);
        Assert.Equal(max * 30 / 100, (await MechanicHit(alice, 4392501)).Dsts.Single().Damage);

        await Gm(alice, "!hp 999999999");
        await Gm(alice, "!raidtrial 3");
        await Tip(alice, 4314474);
        var illusions = await UntilInRoom<MonsterUnit>(instance, m => m.Template.NpcId == 4390003 && !m.Dead, 2);
        Assert.Equal(2, illusions.Length); // at least two
        Assert.All(illusions, m => Assert.Equal(0, m.Template.SpeedCm)); // they stand
        foreach (var illusion in illusions) await Break(alice, illusion);
        for (int i = 0; i < 100 && await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(alice).DungeonAttempt!.Raid!.Active != null)); i++)
            await Task.Delay(20);

        await Gm(alice, "!hp 999999999");
        await OnLoop(() => { baphomet.Hp = baphomet.Template.MaxHp * 59 / 100; return Task.CompletedTask; });
        await Tip(alice, 4314476);
        Assert.Single(await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 4390002)); // under her
        Assert.Equal(max * 35 / 100, (await MechanicHit(alice, 4391601)).Dsts.Single().Damage);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body) is var hit
            && (hit.SkillId is 4393301 or 4392801 || illusions.Any(m => m.Uid == hit.SrcID)));
    }

    private async Task UntilGone(long instance, int npc)
    {
        for (int i = 0; i < 200 && (await InRoom<WorldUnit>(instance, u => u is DungeonService.SceneActorUnit a && a.NpcId == npc
            || u is MonsterUnit m && m.Template.NpcId == npc && !m.Dead)).Length > 0; i++)
            await Task.Delay(20);
        Assert.Empty(await InRoom<WorldUnit>(instance, u => u is DungeonService.SceneActorUnit a && a.NpcId == npc
            || u is MonsterUnit m && m.Template.NpcId == npc && !m.Dead));
    }
}
