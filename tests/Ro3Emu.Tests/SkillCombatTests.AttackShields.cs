using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task<List<MonsterUnit>> AttackShieldTargets(string account, int count)
    {
        var player = Session(account);
        var targets = new List<MonsterUnit>();
        for (int i = 0; i < count; i++) targets.Add(await Target(account, stationary: true, immobile: true));
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>();
            foreach (var ambient in world.Map(player.WorldScene).Units.OfType<MonsterUnit>().Where(m => !targets.Contains(m)).ToArray())
            { ambient.Hp = 0; ambient.Dead = true; await world.RemoveUnitAsync(ambient); }
            foreach (var target in targets)
            {
                target.X = player.Position.X; target.Y = player.Position.Y; target.Z = player.Position.Z;
                target.Hp = 1000000; target.StaysOutOfBattle = true; target.KeepsDamage = true;
                target.Template = target.Template with { Def = 0, Mdef = 0, Element = 1, Flee = 0 };
                world.Map(player.WorldScene).Moved(target);
            }
        });
        return targets;
    }

    [ClientDataTheory]
    [InlineData(11301, 1, 3, 1000)]
    [InlineData(11301, 5, 3, 1000)]
    [InlineData(11607, 1, 5, 300)]
    [InlineData(11607, 10, 5, 300)]
    public async Task AttackShieldRealCastCountsUniqueLandedEnemiesAndPreservesConsumedRemainder(int group, int level, int cap, long layer)
    {
        await using var bot = await InWorld("HitShield");
        await Gm("hitshield", $"!job {(group == 11301 ? 1400 : 1410)}");
        await Teach("hitshield", group, level);
        if (group == 11607) await EquipConditionFixtureAsync("hitshield", 2050120);
        await HealingStat(bot, 101, 10000); await HealingStat(bot, 102, 10000);
        var source = Session("hitshield");
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var targets = await AttackShieldTargets("hitshield", 5);
        var data = _app.Services.GetRequiredService<GameData>();
        int shieldId = group == 11301 ? 1130505 : 1160700 + level;
        // 第一個目標的傷害全部被盾吸收，仍是成功命中，必須算一層。
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(source, 10000, 10000);
            await buffs.AddSupportAsync(targets[0], 1430301, 60000, source.Role!.Id, 1000000);
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = group * 100 + level, TargetPos = source.Position.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(5, hit.Dsts.Count);
        Assert.Contains(hit.Dsts, h => h.DstID == targets[0].Uid && h.Damage == 0 && h.SheildDamage > 0);
        await Loop.InvokeAsync(async () =>
        {
            var shield = Assert.Single(source.Buffs, b => b.ConfigId == shieldId);
            Assert.Equal(cap, shield.PileCount); Assert.Equal(cap * layer, shield.ShieldRemaining);
            Assert.Equal(5000, shield.DurationMs);
            Assert.Equal(0, await buffs.AbsorbAsync(source, 500, data.Row("SkillConfig", 1100101)));
            Assert.Equal(cap * layer - 500, buffs.ShieldAmount(source));
            foreach (var target in targets.Skip(1)) await _app.Services.GetRequiredService<World>().RemoveUnitAsync(target);
            source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear();
        });
        await Task.Delay(1100);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = group * 100 + level, TargetPos = source.Position.Clone() });
        accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        await Loop.InvokeAsync(async () =>
        {
            var shield = Assert.Single(source.Buffs, b => b.ConfigId == shieldId);
            Assert.Equal(cap, shield.PileCount);
            Assert.Equal(Math.Min(cap * layer, cap * layer - 500 + layer), shield.ShieldRemaining);
            source.Buffs[source.Buffs.IndexOf(shield)] = shield with { ExpireMs = MonsterService.NowMs - 1 };
            Assert.Equal(7, await buffs.AbsorbAsync(source, 7, data.Row("SkillConfig", 1100101)));
            Assert.Equal(0, buffs.ShieldAmount(source));
        });
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task AttackShieldSpellProducesTheAuthoredFixedFivePercentEvenWithoutAnEnemy(int level)
    {
        await using var bot = await InWorld("SpellShield");
        await Gm("spellshield", "!job 1410"); await Teach("spellshield", 11610, level);
        await EquipConditionFixtureAsync("spellshield", 2500120, 2);
        await HealingStat(bot, 101, 10000); await HealingStat(bot, 102, 10000);
        await AttackShieldTargets("spellshield", 0);
        var source = Session("spellshield");
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(source, null, 10000));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1161000 + level });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == 1161101));
        Assert.Equal(500, Assert.Single(source.Buffs, b => b.ConfigId == 1161101).ShieldRemaining);
    }

    [ClientDataTheory]
    [InlineData(11301, 1, 2000)]
    [InlineData(11607, 1, 600)]
    [InlineData(11607, 5, 1500)]
    public async Task AttackShieldCombinesPvpAndPveUnderOneTargetLimitIncludingFullyAbsorbedPlayer(int group, int monsterCount, long expected)
    {
        await using var bot = await InWorld("MixedShield");
        await using var victimBot = await InWorld("MixedVictim");
        await Gm("mixedshield", $"!job {(group == 11301 ? 1400 : 1410)}"); await Teach("mixedshield", group);
        if (group == 11607) await EquipConditionFixtureAsync("mixedshield", 2050120);
        var source = Session("mixedshield"); var victim = Session("mixedvictim");
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(source.SceneEntry, [source, victim])));
        foreach (var (client, player) in new[] { (bot, source), (victimBot, victim) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, player, reason: 3)));
            await Next(client, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await client.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(client, MsgId.EntitySelf, EntitySelf.Parser);
            await HealingStat(client, 101, 10000); await HealingStat(client, 102, 10000);
        }
        var monsters = await AttackShieldTargets("mixedshield", monsterCount);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            combat.SetEventPvPHandlers((a, b) => a == source && b == victim, (_, _) => Task.CompletedTask);
            victim.Position = source.Position.Clone();
            await Character.SetHpSpAsync(source, 10000, 10000); await Character.SetHpSpAsync(victim, 10000, 10000);
            await buffs.AddSupportAsync(victim, 1430301, 60000, victim.Role!.Id, 1000000);
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = group * 100 + 1, TargetPos = source.Position.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var pvp = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == victim.Role!.Id));
        Assert.Equal(0, Assert.Single(pvp.Dsts).Damage); Assert.True(pvp.Dsts[0].SheildDamage > 0);
        var pve = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => monsters.Any(m => m.Uid == d.DstID)));
        Assert.Equal(Math.Min(monsterCount, 4), pve.Dsts.Count);
        Assert.Equal(10000, victim.CurrentHp);
        Assert.Equal(expected, buffs.ShieldAmount(source));
        Assert.Equal(Math.Min(monsterCount, 4), monsters.Count(m => m.Hp < 1000000));
    }

    [ClientDataTheory]
    [InlineData(11301)]
    [InlineData(11607)]
    public async Task AttackShieldMissAndExtraEffectsCannotCreateShields(int group)
    {
        await using var bot = await InWorld("ShieldMiss");
        await Gm("shieldmiss", $"!job {(group == 11301 ? 1400 : 1410)}"); await Teach("shieldmiss", group);
        if (group == 11607) await EquipConditionFixtureAsync("shieldmiss", 2050120);
        await HealingStat(bot, 102, 10000);
        var source = Session("shieldmiss"); var targets = await AttackShieldTargets("shieldmiss", 1);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        // 這兩技原表 _iSkillHit=10000 不擲命中；用既有命中覆寫接口注入拒絕結果，
        // 驗的是防護分支，不能把它宣稱為原技能會自然 Miss 的證據。
        (long Amount, DamageHitResult Result)? Reject(GameSession s, ActiveSkillCast _, MonsterUnit __, DamageHitResult ___) =>
            s == source ? (0, DamageHitResult.Miss) : null;
        combat.CastDamageOverrides.Add(Reject);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(source, null, 10000));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = group * 100 + 1, TargetPos = source.Position.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(DamageHitResult.Miss, Assert.Single(hit.Dsts).HitResult);
        var buffs = _app.Services.GetRequiredService<BuffService>(); Assert.Equal(0, buffs.ShieldAmount(source));
        combat.CastDamageOverrides.Remove(Reject);
        await Loop.InvokeAsync(() => combat.CastExtraAsync(source, _app.Services.GetRequiredService<GameData>().Row("SkillConfig", group * 100 + 1)!.Value, targets));
        Assert.Equal(0, buffs.ShieldAmount(source));
    }
}
