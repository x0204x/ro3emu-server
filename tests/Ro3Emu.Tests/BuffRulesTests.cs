using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The shipped buff tables the server now runs: BuffTagConfig, SpecialStatusDecay, StateExclusionConfig, HaloConfig,
/// scope-1 skill parameters and periodic damage/heal. The table facts are read from the exported data; the wire tests
/// boot a server.
/// </summary>
public sealed class BuffRulesTests : IAsyncLifetime
{
    private static readonly Lazy<GameData> Exported = new(() => new GameData(
        Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance));

    private static GameData Data => Exported.Value;
    private static BuffRuleTables Rules => BuffRuleTables.For(Data);

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-buffrules-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication? _app;
    private string _http = "";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app == null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<WebApplication> Server()
    {
        var ports = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = ports.Item1; o.MicroGatePort = ports.Item2; o.GameGatePort = ports.Item3;
        });
        SureHitRolls.Install(_app);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{ports.Item1}";
        return _app;
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

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(
            e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait)).Body);

    private async Task Gm(string account, string line)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private GameSession Session(string name) => _app!.Services.GetRequiredService<SessionRegistry>().All()
        .Single(s => s.Account == name.ToLowerInvariant());
    private GameLoop Loop => _app!.Services.GetRequiredService<GameLoop>();
    private BuffService Buffs => _app!.Services.GetRequiredService<BuffService>();
    private Task Run(Func<Task> work) => Loop.InvokeAsync(work);

    private async Task Eventually(Func<bool> check, string what)
    {
        var until = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < until)
        {
            bool ok = false;
            await Run(() => { ok = check(); return Task.CompletedTask; });
            if (ok) return;
            await Task.Delay(50);
        }
        Assert.Fail("Timed out waiting for " + what);
    }

    private Task<MonsterUnit> Monster(string name, float dx = 0) => Loop.InvokeAsync(async () =>
    {
        var s = Session(name);
        var monsters = _app!.Services.GetRequiredService<MonsterService>();
        var template = monsters.Template(5000001)! with { RoamDistance = 0, Atk = 1, Matk = 1 }; // a harmless applier
        var unit = await monsters.SpawnSingleAsync(s.SceneEntry, 5000001,
            new MapPoint(s.Position.X + dx, s.Position.Y, s.Position.Z, 0), s.InstanceId, customTemplate: template);
        Assert.NotNull(unit);
        return unit!;
    });

    // ---- 1. BuffTagConfig ----

    [ClientDataFact]
    public void TagPairsScaleChanceOrDurationAsTheirAttributesSay()
    {
        Assert.Equal(new BuffRuleTables.TagRule(1, 711, 712, 2), Assert.Single(Rules.TagsByBuff[903001])); // Poisoned
        Assert.Equal(new BuffRuleTables.TagRule(4, 717, 718, 1), Assert.Single(Rules.TagsByBuff[905101])); // Curse
        Assert.Equal(new BuffRuleTables.TagRule(13, 707, 708, 2), Assert.Single(Rules.TagsByBuff[901601])); // Stun
        // A freeze-type slow ([17,29,37,1013,33,24,79]) only has the Slow tag among BuffTagConfig rows.
        Assert.Equal([33], Rules.TagsByBuff[902601].Select(t => t.Tag));
        Assert.DoesNotContain(1007, Rules.Tags.Keys); // Damage Sharing: no attribute pair
        Assert.Equal(14, Rules.Tags.Count);
        // The split between formulas is the attributes' own tips.
        foreach (var tag in Rules.Tags.Values)
        {
            string tip = Data.TextOf(Data.Row("AttributeConfig", tag.AttackAttr)!.Value, "_kTips");
            Assert.Contains(tag.Formula == 1 ? "Chance to inflict" : "duration of", tip);
        }
        Assert.Equal(1.2, BuffRuleTables.TagFactor(3000, 1000), 6);
        Assert.Equal(0.9, BuffRuleTables.TagFactor(0, 1000), 6);
        Assert.Equal(0, BuffRuleTables.TagFactor(0, 20000));
    }

    [ClientDataFact]
    public async Task SlowResistanceShortensAMonsterSkillsSlow()
    {
        await Server();
        await using var alice = await InWorld("Alice");
        var monster = await Monster("alice", 1);
        var combat = _app!.Services.GetRequiredService<CombatService>();
        // Head Crush's _kBuff2 [[1120210 Slow, 10000, 1000]], with its damage cut to the 1-point minimum.
        var node = System.Text.Json.Nodes.JsonNode.Parse(Data.Row("SkillConfig", 1120201)!.Value.GetRawText())!.AsObject();
        node["_kDamageParam1"] = new System.Text.Json.Nodes.JsonArray(0);
        node["_kDamageParam2"] = new System.Text.Json.Nodes.JsonArray(0);
        var headCrush = JsonSerializer.SerializeToElement(node);
        var p = Session("alice");
        await Run(async () =>
        {
            Assert.Equal(1, await combat.MonsterSkillHitAsync(monster, headCrush, [p], monster.Uid));
            Assert.False(p.Dead);
        });
        await Eventually(() => p.Buffs.Any(b => b.ConfigId == 1120210), "the slow");
        Assert.Equal(1000, p.Buffs.Single(b => b.ConfigId == 1120210).DurationMs);
        // 11531701 grants Slow Resistance 728 +1000: 1 + (0 - 1000) / 10000 = 90 % of the duration.
        await Run(async () =>
        {
            await Buffs.RemoveAsync(p, p.Buffs.Single(b => b.ConfigId == 1120210));
            await Buffs.AddAsync(p, 11531701, 60000, p.Role!.Id);
            Assert.Equal(1000, _app.Services.GetRequiredService<CharacterService>().Stats(p)[728]);
            await combat.MonsterSkillHitAsync(monster, headCrush, [p], monster.Uid);
        });
        await Eventually(() => p.Buffs.Any(b => b.ConfigId == 1120210), "the resisted slow");
        Assert.Equal(900, p.Buffs.Single(b => b.ConfigId == 1120210).DurationMs);
    }

    // ---- 2. SpecialStatusDecay ----

    [ClientDataFact]
    public void DecayRowsApplyByMapTypeWithTenThousandAsTheDefault()
    {
        Assert.Equal(43, Data.Table("DungeonConfig").Values.Count(d => GameData.Int(d, "_iDecayType") == 10001));
        Assert.DoesNotContain(Data.Table("DungeonConfig").Values, d => GameData.Int(d, "_iDecayType") == 10000);
        Assert.All(Data.Table("WorldConfig").Values, w => Assert.Equal(0, GameData.Int(w, "_iDecayType")));
        Assert.Equal(BuffRuleTables.DefaultDecayType, Rules.DecayTypeOf(10100)); // a world map
        Assert.Equal(BuffRuleTables.DefaultDecayType, Rules.DecayTypeOf(2100001)); // an ordinary dungeon says 0
        Assert.Equal(10001, Rules.DecayTypeOf(2230101)); // War of Emperium
        // Stun is tag 13 (row 2, default maps) and control 25 (row 3, battlefields); Silence only 26 (row 3).
        Assert.Equal([2, 3], Rules.DecaysByBuff[901601].Select(r => r.Id));
        Assert.Equal([3], Rules.DecaysByBuff[902301].Select(r => r.Id));
        Assert.Equal([1, 3], Rules.DecaysByBuff[902001].Select(r => r.Id)); // Frozen: tag 9 and control 25
        Assert.False(Rules.DecaysByBuff.ContainsKey(903001)); // Poisoned never decays
    }

    [ClientDataFact]
    public void DecayHalvesTheSecondControlAndTheThirdStartsImmunity()
    {
        var stun = Rules.Decays.Single(r => r.Id == 2);
        Assert.Equal((5000, 3000, 3, 10000), (stun.RatioBp, stun.ImmunityMs, stun.Detections, stun.WindowMs));
        var track = new BuffRuleTables.DecayTrack();
        Assert.True(BuffRuleTables.Land(stun, track, 1_000, out double first));
        Assert.True(BuffRuleTables.Land(stun, track, 2_000, out double second));
        Assert.False(BuffRuleTables.Land(stun, track, 3_000, out _)); // 3rd: immune for 3 s instead
        Assert.False(BuffRuleTables.Land(stun, track, 5_999, out _));
        Assert.True(BuffRuleTables.Land(stun, track, 6_000, out double after)); // immunity over: counts again
        Assert.Equal((1.0, 0.5, 1.0), (first, second, after));
        // A quiet window restarts the count.
        Assert.True(BuffRuleTables.Land(stun, track, 6_000 + 10_001, out double quiet));
        Assert.Equal(1.0, quiet);
        Assert.Equal(5000, Rules.Decays.Single(r => r.Id == 3).ImmunityMs);
    }

    [ClientDataFact]
    public async Task RepeatedStunsOnAPlayerDecayThenAreRefused()
    {
        await Server();
        await using var alice = await InWorld("Alice");
        var monster = await Monster("alice", 1);
        var p = Session("alice");
        int Stun() => p.Buffs.SingleOrDefault(b => b.ConfigId == 901601)?.DurationMs ?? 0;
        await Run(() => Buffs.AddAsync(p, 901601, 3000, monster.Uid));
        Assert.Equal(3000, Stun());
        await Run(() => Buffs.AddAsync(p, 901601, 3000, monster.Uid));
        Assert.Equal(1500, Stun());
        long guid = p.Buffs.Single(b => b.ConfigId == 901601).Guid;
        await Run(() => Buffs.AddAsync(p, 901601, 3000, monster.Uid));
        Assert.Equal((1500, guid), (Stun(), p.Buffs.Single(b => b.ConfigId == 901601).Guid)); // refused: immune
        // A self-applied buff is no hostile control and never decays.
        await Run(() => Buffs.AddAsync(p, 901601, 3000, p.Role!.Id));
        Assert.Equal(3000, Stun());
    }

    // ---- 3. StateExclusionConfig ----

    [ClientDataFact]
    public void ExclusiveStatesAreTheBehaviourStateMachinesOneSlot()
    {
        // The three rows are ObjectState 13 SMIdle, 14 SMEmojiPlay, 17 SMEmojiPlayPrepare: the states of
        // AoiBehaviorStateMachineInfo.curState, of which a unit has exactly one (EmojiService.Emote.State).
        Assert.Equal([13, 14, 17], Rules.ExclusiveStates);
        Assert.Equal([(int)ObjectState.EObjectStateSmidle, (int)ObjectState.EObjectStateSmemojiPlay,
            (int)ObjectState.EObjectStateSmemojiPlayPrepare], Rules.ExclusiveStates);
        foreach (var row in Data.Table("StateExclusionConfig").Values)
            Assert.Equal([1, 1, 1], row.GetProperty("_iConcatenate").EnumerateArray().Select(v => v.GetInt32()));
        // Only 14 has a 2 (in _iStateC); no client code reads the table to tell what it changes.
        Assert.Equal(2, GameData.Int(Data.Row("StateExclusionConfig", 2)!.Value, "_iStateC"));
        Assert.Single(typeof(EmojiService.Emote).GetProperties(), p => p.PropertyType == typeof(ObjectState));
    }

    // ---- 4. HaloConfig ----

    [ClientDataFact]
    public void HalosJoinTheirBuffRowsTargetsAndSides()
    {
        var focus = Rules.Halos[15302101];
        Assert.Equal((10f, 200, 10, false, true), (focus.Radius, focus.TickMs, focus.TargetMax, focus.Hostile, focus.IncludeSelf));
        Assert.Equal([15302201], focus.Applied);
        var frozen = Rules.Halos[15303901];
        Assert.Equal((3f, true, false), (frozen.Radius, frozen.Hostile, frozen.IncludeSelf));
        Assert.Equal([15304001], frozen.Applied);
        Assert.Equal([1150201], Rules.Halos[1150501].Applied);
        Assert.True(Rules.Halos[1150501].Hostile);
        Assert.True(Rules.Halos[1150502].Hostile); // type 0 with a debuff: enemies
        Assert.False(Rules.Halos[1161401].Hostile); // type 0 with a buff: allies
        Assert.False(Rules.Halos[1161401].IncludeSelf);
        Assert.Equal([2328301], Rules.Halos[2328101].Applied); // its own id is not re-applied
        Assert.Empty(Rules.Halos[4901701].Applied); // Madogear protection: no target buff in the data
        Assert.False(Rules.Halos.ContainsKey(1161801)); // no BuffConfig row
        // Battle Chant's skill puts the aura on its caster.
        Assert.Contains(Data.Row("SkillConfig", 1150301)!.Value.GetProperty("_kBuff1").EnumerateArray(), t => t[0].GetInt32() == 1150501);
    }

    [ClientDataFact]
    public async Task AnAuraGivesNearbyAlliesAndEnemiesItsBuffAndTakesItBack()
    {
        await Server();
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var a = Session("alice");
        var b = Session("bob");
        await Run(() => _app!.Services.GetRequiredService<SceneService>().TeleportInMapAsync(b,
            new MapPoint(a.Position.X + 2, a.Position.Y, a.Position.Z, 0)));
        await Run(() => Buffs.AddAsync(a, 15302101, 30000, a.Role!.Id)); // Focus Orbit: allies and self, 10 m
        await Eventually(() => a.Buffs.Any(x => x.ConfigId == 15302201) && b.Buffs.Any(x => x.ConfigId == 15302201), "Focus Orbit on both");
        Assert.Equal(a.Role!.Id, b.Buffs.Single(x => x.ConfigId == 15302201).SourceId);
        Assert.Equal(1, Buffs.HaloCount);
        await Run(() => _app!.Services.GetRequiredService<SceneService>().TeleportInMapAsync(b,
            new MapPoint(a.Position.X + 40, a.Position.Y, a.Position.Z, 0)));
        await Eventually(() => !b.Buffs.Any(x => x.ConfigId == 15302201), "Bob leaving the aura");
        Assert.Contains(a.Buffs, x => x.ConfigId == 15302201);

        // An enemy aura (Frozen Orbit, 3 m) slows a monster standing in it.
        var monster = await Monster("alice", 1);
        await Run(() => Buffs.AddAsync(a, 15303901, 30000, a.Role.Id));
        await Eventually(() => Buffs.UnitAttributes(monster).Contains((1131, -3000)), "Frozen Orbit on the monster");

        // The carrier losing the aura takes it from every target.
        await Run(() => Buffs.RemoveAsync(a, a.Buffs.Single(x => x.ConfigId == 15302101)));
        await Eventually(() => !a.Buffs.Any(x => x.ConfigId == 15302201), "Focus Orbit ending");
        await Run(() => Buffs.RemoveAsync(a, a.Buffs.Single(x => x.ConfigId == 15303901)));
        await Eventually(() => !Buffs.UnitAttributes(monster).Any(), "Frozen Orbit ending");
        await Eventually(() => Buffs.HaloCount == 0, "no aura left to tick");
    }

    // ---- 5. scope-1 skill parameters ----

    private static SkillParameterRules.Modifier Parse(string json) =>
        SkillParameterRules.Parse(JsonDocument.Parse(json).RootElement)!.Value;

    [ClientDataFact]
    public void ScopeOneSelectorsAreSkillCategoryTags()
    {
        var all = Parse("""["1","-1","29","1","1500"]""");
        Assert.Equal((1, 0, 29, 1, 1500L), (all.Scope, all.Group, all.Type, all.Operation, all.Value));
        Assert.Null(all.Tags);
        var any = Parse("""["1","3012or3015","27","1","5"]""");
        Assert.Equal([3012, 3015], any.Tags!);
        Assert.False(any.AllTags);
        var both = Parse("""["1","1000and1001","29","5","10000","-1"]""");
        Assert.True(both.AllTags);
        Assert.Equal(-1, both.Index);
        Assert.True(SkillParameterRules.Matches(all, 11001, []));
        Assert.True(SkillParameterRules.Matches(any, 1, [9, 3015]));
        Assert.False(SkillParameterRules.Matches(any, 1, [3013]));
        Assert.False(SkillParameterRules.Matches(both, 1, [1000]));
        Assert.True(SkillParameterRules.Matches(both, 1, [1001, 1000]));
        var group = Parse("""["0","11001","29","1","230"]""");
        Assert.True(SkillParameterRules.Matches(group, 11001, [3015]));
        Assert.False(SkillParameterRules.Matches(group, 11002, []));

        // Every exported scope-1 selector parses and names SkillConfig._kSkillCategory values.
        var categories = Data.Table("SkillConfig").Values.SelectMany(r => SkillParameterRules.CategoriesOf(Data, r)).ToHashSet();
        int count = 0;
        foreach (var buff in Data.Table("BuffConfig").Values)
            if (buff.TryGetProperty("_kSkillAttributeList", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Array && GameData.Number(item[0]) == 1)
                    {
                        count++;
                        var mod = SkillParameterRules.Parse(item);
                        Assert.NotNull(mod);
                        Assert.All(mod!.Value.Tags ?? [], t => Assert.Contains(t, categories));
                    }
        Assert.Equal(477, count);

        // Wind-Pressure Blade 1130301: +1500 on the first damage ratio of any skill; list types 33-38 stay unapplied.
        var row = Data.Row("SkillConfig", 1100101)!.Value;
        var mods = new[] { all, Parse("""["1","-1","33","1","1"]""") };
        var effective = SkillParameterRules.Effective(row, mods, SkillParameterRules.CategoriesOf(Data, row));
        Assert.Equal(row.GetProperty("_kDamageParam1")[0].GetInt64() + 1500, effective.GetProperty("_kDamageParam1")[0].GetInt64());
        Assert.Equal(row.GetProperty("_kBuff1").GetRawText(), effective.GetProperty("_kBuff1").GetRawText());
        Assert.Null(SkillParameterRules.Field(33));
    }

    [ClientDataFact]
    public async Task WindPressureBladeRaisesEveryLearnedSkillOverTheWire()
    {
        await Server();
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        var learn = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
        learn.LevelUpList[11001] = new SkillData { SkillId = 11001, SkillLevel = 1 };
        await alice.SendGameAsync(SkillService.PlanUpdate, learn);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        var p = Session("alice");
        var combat = _app!.Services.GetRequiredService<CombatService>();
        long baseRatio = combat.EffectiveSkillRow(p, 1100101)!.Value.GetProperty("_kDamageParam1")[0].GetInt64();
        await Run(() => Buffs.AddAsync(p, 1130301, 10000, p.Role!.Id));
        var affected = await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            m => m.AffectParamDict.ContainsKey(11001));
        Assert.Equal(baseRatio + 1500, affected.AffectParamDict[11001].AffectParam[29].AffectValueList[0]);
        Assert.Equal(baseRatio + 1500, combat.EffectiveSkillRow(p, 1100101)!.Value.GetProperty("_kDamageParam1")[0].GetInt64());
        await Run(() => Buffs.RemoveAsync(p, p.Buffs.Single(b => b.ConfigId == 1130301)));
        Assert.Equal(baseRatio, combat.EffectiveSkillRow(p, 1100101)!.Value.GetProperty("_kDamageParam1")[0].GetInt64());
    }

    // ---- 6. periodic damage and healing ----

    [ClientDataFact]
    public void PeriodicBuffsComeFromTheirDamageFieldsAndDescriptions()
    {
        BuffRuleTables.PeriodicRule P(int id) => Rules.Periodic[id];
        Assert.Equal((BuffRuleTables.PeriodicKind.Damage, 500, true, 4, 6000L), (P(902701).Kind, P(902701).IntervalMs, P(902701).Magic, P(902701).Element, P(902701).RatioBp)); // Burn
        Assert.Equal((500, false, 6, 500L), (P(903001).IntervalMs, P(903001).Magic, P(903001).Element, P(903001).RatioBp)); // Poisoned
        Assert.Equal((500, false, 1), (P(902901).IntervalMs, P(902901).Magic, P(902901).Element)); // Bleeding
        Assert.Equal((2000, true, 7, 7500L), (P(5001801).IntervalMs, P(5001801).Magic, P(5001801).Element, P(5001801).RatioBp)); // Divine Retribution
        Assert.Equal((2000, true, 8), (P(5002301).IntervalMs, P(5002301).Magic, P(5002301).Element)); // Dark Curse
        Assert.Equal(500, P(4902701).IntervalMs); // Continual Burn "every @{1} seconds"
        Assert.Equal(1000, P(4119501).IntervalMs); // "Periodically takes damage" (inferred 1 s)
        // Shields with a damage type, one-shot blasts and the Electrified stun are no damage ticks.
        foreach (int id in new[] { 1411701, 1430301, 905401, 4902601, 1631601, 14740201 })
            Assert.False(Rules.Periodic.ContainsKey(id), id.ToString());
        // Heals: Breath Medicine 30 per second, Tension Relax 350 x 5 = the 1750 its description totals.
        Assert.Equal((BuffRuleTables.PeriodicKind.Heal, 1000, 30L), (P(980101).Kind, P(980101).IntervalMs, P(980101).HealFlat));
        Assert.Equal(350L, P(1120701).HealFlat);
        Assert.Contains("1750", Data.TextOf(Data.Row("BuffConfig", 1120701)!.Value, "_iDescription"));
        // T_AXadd0_etc rows (Meditate, the Survivor Battle drain) are applied by their owners, not ticked here.
        Assert.False(Rules.Periodic.ContainsKey(905201));
        Assert.False(Rules.Periodic.ContainsKey(15999999));
    }

    [ClientDataFact]
    public async Task FieldCurseHurtsItsBearerEveryTwoSeconds()
    {
        await Server();
        await using var alice = await InWorld("Alice");
        var monster = await Monster("alice", 1);
        var p = Session("alice");
        long before = 0;
        await Run(async () =>
        {
            before = _app!.Services.GetRequiredService<CharacterService>().Stats(p)[2000];
            await Buffs.AddAsync(p, 5001801, 15000, monster.Uid);
            Assert.Equal(1, Buffs.PeriodicCount);
        });
        var started = DateTime.UtcNow;
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == monster.Uid && h.Dsts.Any(d => d.DstID == p.Role!.Id));
        Assert.True(DateTime.UtcNow - started > TimeSpan.FromMilliseconds(1500), "the first tick comes one interval after landing");
        Assert.Equal(0, hit.SkillId);
        long damage = hit.Dsts.Single().Damage;
        Assert.True(damage >= 1);
        await Eventually(() => _app!.Services.GetRequiredService<CharacterService>().Stats(p)[2000] <= before - damage, "the HP loss");
        // Removing the curse ends the ticks.
        await Run(() => Buffs.RemoveAsync(p, p.Buffs.Single(b => b.ConfigId == 5001801)));
        await Eventually(() => Buffs.PeriodicCount == 0, "the tick entry dropping");
    }

    [ClientDataFact]
    public async Task BurnOnAMonsterTicksFromItsApplier()
    {
        await Server();
        await using var alice = await InWorld("Alice");
        var monster = await Monster("alice", 1);
        var p = Session("alice");
        await Run(async () =>
        {
            monster.Hp = 100_000;
            await Buffs.ShowOnUnitAsync(monster, 902701, 3000, p.Role!.Id);
        });
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == p.Role!.Id && h.Dsts.Any(d => d.DstID == monster.Uid));
        Assert.Equal(4, hit.SrcElement); // Fire
        var stat = await Next(alice, CombatService.DamageStatistics, DamageStatisticsNotify.Parser, s => s.Damages.Any(d => d.BuffId == 902701));
        Assert.Equal(monster.Uid, stat.Damages.Single().DamageTargetId);
        await Eventually(() => monster.Hp < 100_000, "the monster losing HP");
    }

    [ClientDataFact]
    public async Task BreathMedicineHealsThirtyEverySecond()
    {
        await Server();
        await using var alice = await InWorld("Alice");
        var p = Session("alice");
        var character = _app!.Services.GetRequiredService<CharacterService>();
        await Run(async () =>
        {
            await character.SetHpSpAsync(p, 10, null);
            await Buffs.AddAsync(p, 980101, 0, p.Role!.Id);
        });
        var heal = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.Dsts.Any(d => d.DstID == p.Role!.Id && d.DamageType == DamageType.Heal));
        Assert.Equal(30, heal.Dsts.Single().Damage);
        await Eventually(() => character.Stats(p)[2000] >= 40, "the healed HP");
    }
}
