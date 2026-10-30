using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The GM panel's combat and entity debug ops on real sockets (GENERAL_PROC_GM 30300): HP, kills, NPCs, attributes,
/// buffs, the combat switches (no cooldown, fixed damage, invincible) and casts, with GMConfig's parameter orders and
/// GMCombinationConfig's presets.
/// </summary>
public sealed class GmCombatOpTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-gmcombat-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private GameLoop Loop => _app.Services.GetRequiredService<GameLoop>();
    private CharacterService Character => _app.Services.GetRequiredService<CharacterService>();
    private MonsterService Monsters => _app.Services.GetRequiredService<MonsterService>();
    private GameSession Session(string name) => _app.Services.GetRequiredService<SessionRegistry>().All()
        .Single(s => string.Equals(s.Account, name, StringComparison.OrdinalIgnoreCase));

    public async Task InitializeAsync()
    {
        var ports = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = ports.Item1; o.MicroGatePort = ports.Item2; o.GameGatePort = ports.Item3;
        });
        _app.Services.GetRequiredService<CombatService>().Rolls = new SwingingRolls();
        await _app.StartAsync(); _http = $"http://127.0.0.1:{ports.Item1}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    /// <summary>Every hit lands without a crit; the damage float swings between its ends, so only GmDamageFix evens it.</summary>
    private sealed class SwingingRolls : CombatRolls
    {
        private int _floats;
        public override double Hit() => 0;
        public override double Crit() => 1;
        public override double DamageFloat() => _floats++ % 2 == 0 ? 0 : 0.999;
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

    /// <summary>One panel button: the op with its comma-separated parameters; the reply text.</summary>
    private static async Task<string> Panel(BotClient bot, int op, string param)
    {
        await bot.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)op, ParamString = param });
        return (await Next(bot, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser)).ErrInfo;
    }

    /// <summary>A preset button (GMCombinationConfig row).</summary>
    private static async Task<string> Preset(BotClient bot, int id)
    {
        await bot.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmListId = id });
        return (await Next(bot, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser)).ErrInfo;
    }

    private Task<long> Stat(string name, int attr) => Loop.InvokeAsync(() => Task.FromResult(Character.Stats(Session(name))[attr]));

    private Task<MonsterUnit> Unit(long uid) => Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<World>()
        .AllUnits().OfType<MonsterUnit>().Single(m => m.Uid == uid)));

    /// <summary>"Spawned ... (5099000, uid 123)": the new unit's uid.</summary>
    private static long SpawnedUid(string reply) => long.Parse(Regex.Match(reply, @"uid (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);

    [ClientDataFact]
    public async Task SwitchesFollowTheButtonsAndPresets()
    {
        await using var alice = await InWorld("Alice");
        var s = Session("alice");
        Assert.Contains("No cooldown on", await Panel(alice, 52, "1"));
        Assert.Equal(GmCombatSwitches.NoCooldown, s.GmCombat);
        Assert.Contains("Fixed damage", await Panel(alice, 53, "1"));
        Assert.Contains("No cooldown off", await Preset(alice, 10002)); // GmUnlimit 0,1
        Assert.Equal(GmCombatSwitches.FixedDamage, s.GmCombat);
        Assert.Contains("Invincible, no costs on", await Preset(alice, 10001)); // GmUnlimit 1,2
        Assert.Contains("Invincible, no costs off", await Panel(alice, 80, "0")); // the button: one parameter
        await Panel(alice, 53, "0");
        Assert.Equal(GmCombatSwitches.None, s.GmCombat);
        // Ops without meaning here refuse in one line that names them.
        Assert.StartsWith("GmReloadSkillBlueprint:", await Panel(alice, 58, ""));
        Assert.StartsWith("GmOpenLog:", await Panel(alice, 92, "1,1"));
        Assert.StartsWith("GmBattle:", await Panel(alice, 20000, ""));
    }

    [ClientDataFact]
    public async Task ChangeHpAndKillTheGm()
    {
        await using var alice = await InWorld("Alice");
        long max = await Stat("alice", 101);
        Assert.Contains($"-> {max - 10}", await Panel(alice, 23, "0,0,10")); // target, heal, damage
        Assert.Equal(max - 10, await Stat("alice", 2000));
        Assert.Contains($"-> {max - 6}", await Panel(alice, 23, ",4,"));
        Assert.Contains("killed", await Panel(alice, 23, "0,0," + max));
        Assert.True(Session("alice").Dead);
        Assert.Equal(0, await Stat("alice", 2000));
        var dead = await Next(alice, CombatService.EntityBaseChange, EntityBase.Parser, e => e.State == EntityState.Dead);
        Assert.Equal((ulong)Session("alice").Role!.Id, dead.Uuid);
        Assert.Contains("already dead", await Panel(alice, 30, "0"));
    }

    [ClientDataFact]
    public async Task AnNpcFromThePanelTakesAttributesHpCampAndDeath()
    {
        await using var alice = await InWorld("Alice");
        long uid = SpawnedUid(await Panel(alice, 27, "5099000")); // a preset's training dummy
        var dummy = await Unit(uid);
        Assert.Equal(5099000, dummy.Template.NpcId);
        Assert.Contains($"103 atk {dummy.Template.Atk}", await Panel(alice, 77, $"103,{uid}")); // attribute, target
        Assert.Contains($"101 maxhp {dummy.Template.MaxHp}", await Panel(alice, 32, $"{uid}"));
        Assert.Contains("-> 777", await Panel(alice, 33, $"{uid},103,777")); // target, attribute, value
        Assert.Equal(777, dummy.Template.Atk);
        Assert.Equal(5099000, Monsters.Template(5099000)!.NpcId);
        Assert.NotEqual(777, Monsters.Template(5099000)!.Atk); // the kind's template is untouched
        long hp = dummy.Hp;
        await Panel(alice, 34, $"{uid},2000,-10"); // unit, attribute, change
        await Panel(alice, 23, $"{uid},0,5");
        Assert.Equal(hp - 15, dummy.Hp);
        Assert.Contains("camp 2", await Panel(alice, 95, $"{uid},2"));
        Assert.Equal(2, dummy.CampOverride);
        var camp = await Next(alice, WorldEffectsService.Camp, EntityChangeCampNotify.Parser);
        Assert.Equal((uid, 2), (camp.EntityId, camp.Camp));
        Assert.Contains("killed (no rewards)", await Panel(alice, 30, $"{uid}"));
        Assert.True(dummy.Dead);
        Assert.StartsWith("No entity 42", await Panel(alice, 30, "42"));
        Assert.Contains("not a monster", await Panel(alice, 27, "1"));
    }

    [ClientDataFact]
    public async Task PlayerAttributesGoThroughTheStatPipeline()
    {
        await using var alice = await InWorld("Alice");
        long str = await Stat("alice", 1), atk = await Stat("alice", 103);
        Assert.Contains($"1 str: {str} -> {str + 10}", await Panel(alice, 34, "0,1,10"));
        Assert.True(await Stat("alice", 103) > atk, "STR feeds ATK through AttributeConversionConfig");
        long target = await Stat("alice", 103) + 100;
        await Panel(alice, 33, $"0,103,{target}");
        Assert.Equal(target, await Stat("alice", 103));
        await Panel(alice, 33, "0,2000,1");
        Assert.Equal(1, await Stat("alice", 2000));
        Assert.Contains("No attribute 99999", await Panel(alice, 33, "0,99999,1"));
    }

    [ClientDataFact]
    public async Task BuffPresetsAddShowAndRemove()
    {
        await using var alice = await InWorld("Alice");
        var s = Session("alice");
        string added = await Preset(alice, 14011); // four GmAddBuff steps: buff, 0 = its own duration, 0 = self
        Assert.DoesNotContain("Not implemented", added);
        foreach (int buff in new[] { 1132601, 1132701, 1132901, 1133101 })
            Assert.Contains(s.Buffs, b => b.ConfigId == buff);
        Assert.Contains("1132601", await Panel(alice, 76, ""));
        string removed = await Preset(alice, 14012); // four GmRemoveBuff steps
        Assert.DoesNotContain("Not implemented", removed);
        Assert.DoesNotContain(s.Buffs, b => b.ConfigId is 1132601 or 1132701 or 1132901 or 1133101);
        Assert.Contains("has no buffs", await Panel(alice, 76, "0"));
        Assert.StartsWith("No buff 1", await Panel(alice, 35, "1,0,0"));
    }

    [ClientDataFact]
    public async Task TheGmCastsAnUnlearnedSkillWithoutCooldown()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        await Panel(alice, 52, "1");
        Assert.Contains("casts 1100201", await Panel(alice, 81, "1100201")); // Swordsman's area skill, never learned
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var s = Session("alice");
        Assert.Equal(0, s.GmCastSkill);
        Assert.Empty(s.SkillCds);
        Assert.DoesNotContain(s.SkillModeCds.Values, end => end > MonsterService.NowMs);
        // Without the switch the ordinary refusal stands: the skill is not learned.
        await Panel(alice, 52, "0");
        await Task.Delay(1500); // the first cast's action ends
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100201 });
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task InvincibleTakesNothingAndFixedDamageDoesNotFloat()
    {
        await using var alice = await InWorld("Alice");
        var s = Session("alice");
        long roleId = s.Role!.Id;
        await Panel(alice, 80, "1,2");
        var poring = await Loop.InvokeAsync(async () =>
        {
            var template = Monsters.Template(5000001)! with { RoamDistance = 0, SurroundDistance = 0 };
            var unit = await Monsters.SpawnSingleAsync(s.SceneEntry, 5000001, new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, 0),
                s.InstanceId, customTemplate: template);
            Monsters.Engage(unit!, s);
            return unit!;
        });
        // A hard-hitting poring against a GM who can take it: the float's 3% shows on hundreds of damage.
        await Panel(alice, 33, "0,101,1000000");
        await Panel(alice, 78, "");
        await Panel(alice, 33, $"{poring.Uid},103,3000");
        long max = await Stat("alice", 101);
        var shielded = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == poring.Uid);
        Assert.Equal(0, Assert.Single(shielded.Dsts, d => d.DstID == roleId).Damage);
        Assert.Equal(max, await Stat("alice", 2000));
        // Fixed damage first: the hits before invincibility ends land as 0 and do not count.
        await Panel(alice, 53, "1");
        await Panel(alice, 80, "0,2");
        var first = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == poring.Uid && h.Dsts.Any(d => d.Damage > 0));
        var second = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == poring.Uid && h.Dsts.Any(d => d.Damage > 0));
        Assert.Equal(first.Dsts.Single(d => d.DstID == roleId).Damage, second.Dsts.Single(d => d.DstID == roleId).Damage);
    }
}
