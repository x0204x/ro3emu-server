using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int TestDagger = 2010120; // ItemConfig _iSubType 1

    // Double Attack: "When a Dagger is equipped, each Basic Attack has a ${1}% Chance to deal ${2} Extra hits ...". Its
    // BuffConfig row's _kLogicData[0] is the chance.
    [ClientDataFact]
    public async Task DoubleAttackStrikesAgainWithADagger()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 5200"); await Teach("alice", 15000); await Teach("alice", 15002);
        await Loop.InvokeAsync(() =>
        {
            Session("alice").Items.Add(new InvItem { Guid = 990001, Entry = TestDagger, Stack = 1, Grid = 1 });
            return Task.CompletedTask;
        });
        _app.Services.GetRequiredService<TalentProcs>().Roll = _ => 0;
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1500001, TargetId = monster.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
        var extra = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            x => x.SkillId == 1500001 && x.SessionId != accepted.SessionId);
        Assert.Contains(extra.Dsts, d => d.DstID == monster.Uid && d.Damage > 0);
    }

    // Deadly Poison's buff (_kLogicData [3200, Venom Sting 1523001]): "After attacking, there is a @{2}% Chance to inflict
    // [Venom] damage on the target".
    [ClientDataFact]
    public async Task DeadlyPoisonInflictsVenomAfterAnAttack()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 5200"); await Teach("alice", 15000);
        await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            return _app.Services.GetRequiredService<BuffService>().AddAsync(s, 1521101, 120000, s.Role!.Id);
        });
        _app.Services.GetRequiredService<TalentProcs>().Roll = _ => 0;
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1500001, TargetId = monster.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var venom = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == 1523001);
        Assert.Contains(venom.Dsts, d => d.DstID == monster.Uid && d.Damage > 0);
    }

    // Charged Shield Bash (10102106 for the Crusader line): "After every ${1} (2) casts of [Smite], the next [Smite]'s
    // percentage damage multiplier and fixed damage increase". The second Smite arms Smite's bonus buff 1153301; the third
    // uses it up and does not count toward the next.
    [ClientDataFact]
    public async Task ChargedShieldBashArmsTheSmiteAfterEverySecond()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 1210"); await Teach("alice", 11409);
        await ChooseProfessionTalent(alice, "alice", 10102106);
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        async Task<bool> SmiteAsync()
        {
            await Loop.InvokeAsync(() =>
            {
                var s = Session("alice");
                s.SkillCds.Clear();
                s.SkillModeCds.Clear();
                return Task.CompletedTask;
            });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1140901, TargetId = monster.Uid });
            var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.Equal(0, accepted.Result);
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
            await Task.Delay(1500);
            return await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Buffs.Any(b => b.ConfigId == 1153301)));
        }

        Assert.False(await SmiteAsync());
        Assert.True(await SmiteAsync());
        Assert.False(await SmiteAsync());
    }

    // Magic Cannon (10103102 for the Wizard line): "After every ${1} (2) casts of [Magic Crasher], the skill multiplier of the
    // next @{1} casts of [Magic Crasher] increases by ${2}% ..." Its buff carries nothing, so the charge is skill tuples.
    [ClientDataFact]
    public async Task MagicCannonChargesTheMagicCrasherAfterEverySecond()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2400"); await Teach("alice", 12201);
        await ChooseProfessionTalent(alice, "alice", 10103102);
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        async Task<int> CrasherAsync()
        {
            await Loop.InvokeAsync(() =>
            {
                var s = Session("alice");
                s.SkillCds.Clear();
                s.SkillModeCds.Clear();
                return Task.CompletedTask;
            });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1220101, TargetId = monster.Uid });
            var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.Equal(0, accepted.Result);
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
            await Task.Delay(1500);
            return await Loop.InvokeAsync(() => Task.FromResult(Session("alice").TalentChargeModifiers.Count));
        }

        Assert.Equal(0, await CrasherAsync());
        Assert.Equal(2, await CrasherAsync()); // [12201, 29, add, 2000] and [12201, 30, add, 10]
        Assert.Equal(0, await CrasherAsync());
    }

    // Sacred Divine Light (10102106 for the Priest line): "[Heal] restores an Extra MATK*${2}%+${3} HP to targets below
    // ${1}% HP." Its extra heal is the proc row 1421601.
    [ClientDataFact]
    public async Task SacredDivineLightHealsAnAllyBelowItsThreshold()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 4200"); await Teach("alice", 14007);
        await ChooseProfessionTalent(alice, "alice", 10102106);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<CharacterService>().SetHpSpAsync(Session("alice"), 10, null));

        long self = Session("alice").Role!.Id;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400701, TargetId = self });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var extra = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == 1421601);
        Assert.Contains(extra.Dsts, d => d.DstID == self && d.DamageType == DamageType.Heal);
    }

    // Earthshatter: "For every @{1} enemies hit by [Hammer Fall], the percentage damage multiplier increases by ${1}%, up to
    // ${2}%." Magnus_ExorcismusBD (Holy Cross Exorciser's mode) deals its third and fourth damage values to a single enemy.
    [ClientDataFact]
    public async Task TheCountOfEnemiesHitShapesTheDamageRow()
    {
        await using var alice = await InWorld("Alice");
        var data = _app.Services.GetRequiredService<GameData>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var (three, six, single, pair) = await Loop.InvokeAsync(() =>
        {
            var s = Session("alice");
            s.TalentProcBuffs = [(1642301, ["5", "25"])];
            var hammer = SkillConfig.Get(data, 1610401)!.Value.Row;
            var magnus = SkillConfig.Get(data, 1410911)!.Value.Row;
            return Task.FromResult((combat.TargetCountRow!(s, hammer, 3), combat.TargetCountRow(s, hammer, 6),
                combat.TargetCountRow(s, magnus, 1), combat.TargetCountRow(s, magnus, 2)));
        });
        Assert.Equal(5500 + 1500, three.GetProperty("_kDamageParam1")[0].GetInt64());
        Assert.Equal(5500 + 2500, six.GetProperty("_kDamageParam1")[0].GetInt64());
        Assert.Equal([1000, 1300], single.GetProperty("_kDamageParam1").EnumerateArray().Select(v => v.GetInt64()));
        Assert.Equal(4, pair.GetProperty("_kDamageParam1").GetArrayLength());
    }

    // Advanced Katar Mastery's own BuffConfig row (1522001) carries "[Assassin Normal Attack] CRIT Chance increases by
    // ${5}%" as [15000, 10, add, 4000]; a learned passive's tuples reach the cast like a buff's.
    [ClientDataFact]
    public async Task ALearnedPassivesTuplesReachTheNormalAttack()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 5300"); await Teach("alice", 15220);
        var data = _app.Services.GetRequiredService<GameData>();
        double crit = await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            await _app.Services.GetRequiredService<CombatService>().RefreshSkillParametersAsync(s);
            return SkillParameterRules.CritChance(s, data, SkillConfig.Get(data, 1500001)!.Value.Row);
        });
        Assert.Equal(0.4, crit, 6);
    }

    // Gravity Trap: "When [Gravitation Field] ends, inflicts [Immobilize] on enemies in the area for ${1} seconds." The
    // field resolves at once, so it ends its _kLogicData[1] (3900 ms) later (10104103 for the Wizard line).
    [ClientDataFact]
    public async Task GravityTrapImmobilizesWhenGravitationFieldEnds()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2400"); await Teach("alice", 12212);
        await ChooseProfessionTalent(alice, "alice", 10104103);
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1221201, TargetPos = monster.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            x => x.UnitId == monster.Uid && x.Buffs.Any(b => b.ConfigId == 902401));
    }

    // Soul Reaper (10103106 for the Wizard line): "When [Soul Drain] scores a Kill, gain ${1} MATK, up to ${2}."
    [ClientDataFact]
    public async Task SoulReaperAddsMatkForAKill()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2400"); await Teach("alice", 12209); await Teach("alice", 12004);
        await ChooseProfessionTalent(alice, "alice", 10103106);
        var character = _app.Services.GetRequiredService<CharacterService>();
        long before = await Loop.InvokeAsync(() => Task.FromResult(character.Stats(Session("alice"))[104]));
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = monster.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.Dsts.Any(d => d.DstID == monster.Uid && d.IsDead));
        await Task.Delay(300);
        long after = await Loop.InvokeAsync(() => Task.FromResult(character.Stats(Session("alice"))[104]));
        Assert.True(after > before, $"MATK {before} -> {after}");
    }

    // Ember Zone (10102127 for the Wizard line): "After casting [Heaven's Drive], the next [Meteor Storm] cast within ${1}
    // sec. creates an [Ember Zone] ... Every ${3} seconds, it deals Fire Ranged Magic damage". The zone is NPC 1200021.
    [ClientDataFact]
    public async Task EmberZoneFollowsHeavensDriveAndMeteorStorm()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2200"); await Teach("alice", 12109); await Teach("alice", 12106);
        await ChooseProfessionTalent(alice, "alice", 10102127);
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210901, TargetPos = monster.Pos.Clone() });
        var drive = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, drive.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == drive.SessionId);
        await Task.Delay(1500);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210601, TargetPos = monster.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser, x => x.SessionId != drive.SessionId)).Result);
        await Task.Delay(1500);
        long zone = await Loop.InvokeAsync(() =>
            Task.FromResult(Session("alice").SkillObjects.FirstOrDefault(o => o.NpcId == 1200021)?.Uid ?? 0));
        Assert.NotEqual(0, zone);
        var tick = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SrcID == zone);
        Assert.Contains(tick.Dsts, d => d.DstID == monster.Uid && d.Damage > 0);
    }
}
