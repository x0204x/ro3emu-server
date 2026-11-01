using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private sealed class StormBoundaryRolls(int proc) : CombatRolls
    {
        public override double Hit() => 0;
        public override double DamageFloat() => 0.5;
        public override double Crit() => 1;
        public override int Proc(int max) => Math.Min(proc, max - 1);
    }

    [ClientDataTheory]
    [InlineData(3)]
    [InlineData(10)]
    public async Task StormGustHasFiveWavesCappedAtFiveTargetsInsideItsRange(int level)
    {
        await using var alice = await InWorld("StormWaves");
        await Gm("stormwaves", "!job 2200");
        await Teach("stormwaves", 12108, level);
        await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq
        { GmType = (GmOpEnum)33, ParamString = $"{alice.Self!.Uuid},104,1000" });
        Assert.Equal(0, (await Next(alice, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser)).Result);
        _app.Services.GetRequiredService<CombatService>().Rolls = new StormBoundaryRolls(200);
        var targets = new List<MonsterUnit>();
        for (int i = 0; i < 7; i++) targets.Add(await Target("stormwaves", stationary: true));
        await Loop.InvokeAsync(async () =>
        {
            var pos = Session("stormwaves").Position;
            var world = _app.Services.GetRequiredService<World>();
            var map = world.Map(Session("stormwaves").WorldScene);
            // 這是獨立測試伺服器：移除原生野怪，避免把附近重生的波利算入五人上限。
            foreach (var ambient in map.Units.OfType<MonsterUnit>().Where(m => !targets.Contains(m)).ToArray())
            {
                ambient.Hp = 0; ambient.Dead = true;
                await world.RemoveUnitAsync(ambient);
            }
            for (int i = 0; i < targets.Count; i++)
            {
                targets[i].X = pos.X + (i == 6 ? 7 : 0.2f * (i + 1));
                targets[i].Z = pos.Z;
                targets[i].Y = pos.Y;
                targets[i].Hp = 1_000_000;
                targets[i].Template = targets[i].Template with { Mdef = 0, Element = 1 };
                targets[i].StaysOutOfBattle = true;
                targets[i].KeepsDamage = true;
                map.Moved(targets[i]);
            }
        });
        int skillId = 1210800 + level;
        long matk = Character.Stats(Session("stormwaves"))[104];
        Assert.Equal(1000, matk); // 經既有 GM 面板設定，確保倍率也參與驗證，不只比到固定傷害。
        long expectedPerWave = (long)Math.Round(matk * (level == 3 ? 650 : 1000) / 10000.0 + (level == 3 ? 81 : 130));
        Assert.True(expectedPerWave > (level == 3 ? 81 : 130));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = skillId, TargetPos = Session("stormwaves").Position.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, x => x.SkillID == skillId);
        var hits = new List<SkillHitInfo>();
        for (int wave = 0; wave < 5; wave++)
        {
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == skillId);
            Assert.Equal(5, hit.Dsts.Count);
            Assert.All(hit.Dsts, d => Assert.Equal(expectedPerWave, d.Damage));
            Assert.Equal(targets.Take(5).Select(t => t.Uid).Order(), hit.Dsts.Select(d => d.DstID).Order());
            hits.Add(hit);
        }
        await Task.Delay(1200);
        Assert.Equal(5, alice.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == skillId));
        foreach (var target in targets.Take(5))
        {
            var damage = hits.Select(h => Assert.Single(h.Dsts, d => d.DstID == target.Uid).Damage).ToArray();
            Assert.All(damage, d => Assert.Equal(damage[0], d));
            Assert.Equal(1_000_000 - damage.Sum(), target.Hp);
        }
        Assert.Equal(1_000_000, targets[5].Hp); // 範圍內第六隻超出每波目標上限。
        Assert.Equal(1_000_000, targets[6].Hp); // 範圍外的目標不受傷。
    }

    [ClientDataTheory]
    [InlineData(199, true)]
    [InlineData(200, false)]
    public async Task StormGustOrdinaryMonsterSlowAndFreezeFollowTheAuthoredProcBoundary(int proc, bool freeze)
    {
        await using var alice = await InWorld("StormControl");
        await Gm("stormcontrol", "!job 2200");
        await Teach("stormcontrol", 12108, 3);
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new StormBoundaryRolls(proc);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var target = await Target("stormcontrol", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            target.Hp = 1_000_000;
            target.StaysOutOfBattle = true;
            target.KeepsDamage = true;
            return Task.CompletedTask;
        });
        int beforeSpeed = target.SpeedCm(MonsterService.NowMs);
        Assert.True(beforeSpeed > 0);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1210803, TargetPos = target.Pos });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, x => x.SkillID == 1210803);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            x => x.SkillId == 1210803 && x.Dsts.Any(d => d.DstID == target.Uid));
        if (freeze)
        {
            var frozen = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
                b => b.UnitId == target.Uid && b.Buffs.Any(x => x.ConfigId == 902001));
            Assert.Equal(1000, Assert.Single(frozen.Buffs, b => b.ConfigId == 902001).Duration);
            await Next(alice, CombatService.AoiAttrChange, AOIAttrChange.Parser,
                e => e.EntityUuid == (ulong)target.Uid && e.StaggerInfo?.StaggerMask == 1UL << 11);
        }
        // 預先等未來封包，避免誤取施加冰凍前緩速同步的舊零值。
        using var clearStagger = alice.Inbox.Expect(e => e.IsGame(CombatService.AoiAttrChange)
            && AOIAttrChange.Parser.ParseFrom(e.Body) is var change && change.EntityUuid == (ulong)target.Uid
            && change.StaggerInfo?.StaggerMask == 0);
        await Loop.InvokeAsync(async () =>
        {
            Assert.True(buffs.UnitHasGroup(target, 12111));
            Assert.Equal((int)(beforeSpeed * 0.8), target.SpeedCm(MonsterService.NowMs));
            Assert.Equal(freeze, buffs.UnitHasGroup(target, 9020));
            Assert.Equal(freeze, buffs.UnitForbidden(target, ForbiddenState.Walk));
            Assert.False(buffs.UnitForbidden(target, ForbiddenState.CastSkill));
            Assert.Equal(freeze, buffs.UnitActionBlocked(target, ForbiddenState.CastSkill));
            Assert.Equal(freeze ? 1UL << 11 : 0, buffs.UnitStaggerMask(target));
            var entity = target.Entity();
            buffs.PopulateEntity(target, entity);
            Assert.Equal(freeze ? 1UL << 11 : 0, entity.ObjStagger?.StaggerMask ?? 0);
            await combat.ClearSkillsAsync(Session("stormcontrol"));
        });
        await Task.Delay(1150); // 1 秒冰凍先到期，3 秒緩速仍需保留。
        if (freeze) await clearStagger.WaitAsync(Wait);
        await Loop.InvokeAsync(() =>
        {
            Assert.False(buffs.UnitHasGroup(target, 9020));
            Assert.False(buffs.UnitForbidden(target, ForbiddenState.Walk));
            Assert.False(buffs.UnitForbidden(target, ForbiddenState.CastSkill));
            Assert.False(buffs.UnitActionBlocked(target, ForbiddenState.CastSkill));
            Assert.Equal(0UL, buffs.UnitStaggerMask(target));
            var entity = target.Entity();
            buffs.PopulateEntity(target, entity);
            Assert.Equal(0UL, entity.ObjStagger?.StaggerMask ?? 0);
            Assert.True(buffs.UnitHasGroup(target, 12111));
            Assert.Equal((int)(beforeSpeed * 0.8), target.SpeedCm(MonsterService.NowMs));
            return Task.CompletedTask;
        });
        await Task.Delay(2150); // 停止地面技能後共 3.3 秒，緩速也自然到期。
        await Loop.InvokeAsync(() =>
        {
            Assert.False(buffs.UnitHasGroup(target, 12111));
            Assert.False(buffs.UnitForbidden(target, ForbiddenState.Walk));
            Assert.False(buffs.UnitForbidden(target, ForbiddenState.CastSkill));
            Assert.Equal(beforeSpeed, target.SpeedCm(MonsterService.NowMs));
            return Task.CompletedTask;
        });
    }
}
