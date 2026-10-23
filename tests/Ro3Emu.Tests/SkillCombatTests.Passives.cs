using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int TestSword = 2020120; // ItemConfig _iSubType 2

    // BuffConfig 1310401 has Beastbane's id but is Steel Crow's ("Increases the percentage damage coefficient of [Blitz
    // Beat]"): it counts for Steel Crow, not for Beastbane.
    [ClientDataFact]
    public async Task APassiveOwnsTheBuffOfItsNameNotOfItsId()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 3200");
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Teach("alice", 13104);
        var beastbane = await Loop.InvokeAsync(() => Task.FromResult(combat.PassiveModifiers(Session("alice"))));
        Assert.DoesNotContain(beastbane, p => p.Modifier.Group == 13102);
        await Teach("alice", 13105);
        var steelCrow = await Loop.InvokeAsync(() => Task.FromResult(combat.PassiveModifiers(Session("alice"))));
        Assert.Contains(steelCrow, p => p.Modifier.Group == 13102 && p.Modifier.Type == 29);
    }

    // Sword Mastery: "When equipped with a sword, Physical ATK increases by ${1}." Its own row's _kLogicData names
    // 1109901 ([103, 1] at level 1), which counts only while a sword is held.
    [ClientDataFact]
    public async Task SwordMasteryAddsItsAttackOnlyWithASword()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 1100"); await Teach("alice", 11009);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var bare = await Loop.InvokeAsync(() => Task.FromResult(combat.PassiveAttributes(Session("alice")).ToList()));
        Assert.DoesNotContain((103L, 1d), bare);
        var armed = await Loop.InvokeAsync(() =>
        {
            Session("alice").Items.Add(new InvItem { Guid = 990002, Entry = TestSword, Stack = 1, Grid = 1 });
            return Task.FromResult(combat.PassiveAttributes(Session("alice")).ToList());
        });
        Assert.Contains((103L, 1d), armed);
    }

    // Hilt Binding: "Increases the duration of Adrenaline Rush, Power Thrust, and Maximize Power by ${3} sec." Its tuple
    // [16105, 33, add, 60000, 0, 2] adds 60 s to column 2 (the duration) of Adrenaline Rush's first _kBuff1 tuple.
    [ClientDataFact]
    public async Task HiltBindingLengthensAdrenalineRushsBuff()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 6200"); await Teach("alice", 16101);
        var data = _app.Services.GetRequiredService<GameData>();
        var row = await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            await _app.Services.GetRequiredService<CombatService>().RefreshSkillParametersAsync(s);
            return SkillParameterRules.Effective(s, data, SkillConfig.Get(data, 1610501)!.Value.Row);
        });
        Assert.Equal(120000 + 60000, row.GetProperty("_kBuff1")[0][2].GetInt64());
        Assert.Equal(120000 + 60000, row.GetProperty("_kBuff1")[1][2].GetInt64());
    }

    // Double Holy Light: "[Judex] cast by you has a ${1}% Chance to deal one Extra instance of ... magic damage ... and a
    // ${1}% Chance to deal one Extra instance of ... Physical damage" (_kLogicData [3000, 1431601, 3000, 1431701]).
    [ClientDataFact]
    public async Task DoubleHolyLightFollowsJudex()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 4400"); await Teach("alice", 14309); await Teach("alice", 14311);
        _app.Services.GetRequiredService<PassiveSkills>().Roll = _ => 0;
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430901, TargetId = monster.Uid, TargetPos = monster.Pos.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var magic = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == 1431601);
        Assert.Contains(magic.Dsts, d => d.DstID == monster.Uid && d.Damage > 0);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == 1431701);
    }

    // Energy Coat: "When you take damage, gain [Energy Coat] for ${1} sec. ... Cooldown: ${3} sec." Its own row 1201401
    // names the reduction buff 1201501 for 1000 ms.
    [ClientDataFact]
    public async Task EnergyCoatAnswersDamageTakenOncePerCooldown()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2200"); await Teach("alice", 12003);
        var monster = await Target("alice", stationary: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        int coats = await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            foreach (var handler in combat.TakenAsync) await handler(monster, s, 100);
            int first = s.Buffs.Count(b => b.ConfigId == 1201501);
            foreach (var buff in s.Buffs.Where(b => b.ConfigId == 1201501).ToArray()) s.Buffs.Remove(buff);
            foreach (var handler in combat.TakenAsync) await handler(monster, s, 100); // within the 10 s cooldown
            return first * 10 + s.Buffs.Count(b => b.ConfigId == 1201501);
        });
        Assert.Equal(10, coats);
    }

    // Prey on the Weak: "Damage dealt to enemies afflicted with status effects is increased by ${1}%." (5 at level 10).
    [ClientDataFact]
    public async Task PreyOnTheWeakHitsAStunnedMonsterHarder()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 5400");
        var monster = await Target("alice", stationary: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var (calm, stunned) = await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            s.Progress!.Skills[15316] = 10;
            var row = SkillConfig.Get(data, 1500001)!.Value.Row;
            long Hit() => combat.DamageAdjusters.Aggregate(10000L, (damage, adjust) => adjust(s, monster, row, DamageHitResult.Normal, damage));
            long before = Hit();
            await _app.Services.GetRequiredService<BuffService>().ShowOnUnitAsync(monster, 901601, 5000, s.Role!.Id);
            return (before, Hit());
        });
        Assert.Equal(calm * 105 / 100, stunned);
    }

    // Wood Spirit's Blessing: "when you take lethal damage, you gain one chance to [Respawn] ... you recover ${2}% Life
    // and enter the [Withered] state for ${3} minutes. During this time, [Respawn] cannot trigger again."
    [ClientDataFact]
    public async Task WoodSpiritsBlessingSavesOnePerWitheredTime()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 4400"); await Teach("alice", 14307);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        var (first, hp, second) = await Loop.InvokeAsync(async () =>
        {
            var s = Session("alice");
            bool saved = await buffs.TryPreventLethalAsync(s);
            var stats = character.Stats(s);
            return (saved, stats[2000] * 100 / stats[101], await buffs.TryPreventLethalAsync(s));
        });
        Assert.True(first);
        Assert.InRange(hp, 29, 30); // ${2} = 30% of Max Life
        Assert.False(second);
    }
}
