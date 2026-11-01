using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(12302, 1, 1, 0)]
    [InlineData(12302, 10, 1, 0)]
    [InlineData(12308, 1, 3, 0)]
    [InlineData(12308, 5, 3, 0)]
    [InlineData(12310, 1, 3, 0)]
    [InlineData(12310, 5, 3, 0)]
    [InlineData(12312, 1, 3, 0)]
    [InlineData(12312, 5, 3, 0)]
    [InlineData(12314, 1, 3, 0)]
    [InlineData(12314, 5, 3, 0)]
    [InlineData(12315, 1, 5, 0)]
    [InlineData(12315, 5, 5, 0)]
    [InlineData(12316, 1, 5, 0)]
    [InlineData(12316, 5, 5, 0)]
    [InlineData(12342, 1, 5, 0)]
    [InlineData(12342, 10, 5, 0)]
    [InlineData(12302, 10, 1, 1)]
    public async Task ReadingSpellbookEightSkillsLowAndMaxReallyCopyDamageOnce(int group, int level, int pages, int mode)
    {
        await using var bot = await InWorld("ReadingCast");
        IsolateDirectHealing();
        await Gm("readingcast", "!job 2400");
        await Teach("readingcast", group, level);
        await HealingStat(bot, 102, 10000);
        await HealingStat(bot, 104, 1000);
        var source = Session("readingcast");
        var target = await Target("readingcast", stationary: true, immobile: true);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        int completions = 0;
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 1000000; target.StaysOutOfBattle = true;
            await Character.SetHpSpAsync(source, null, 10000);
            await buffs.ReconcileEnergyAsync(source);
            source.Energy[240001].Value = 8;
            source.Energy[240001].NextRecoverMs = long.MaxValue;
            await buffs.AddAsync(source, 1231001, 60000, source.Role!.Id);
            if (mode == 1) await buffs.AddAsync(source, 1234901, 60000, source.Role.Id);
            _app.Services.GetRequiredService<CombatService>().SkillCastCompleted.Add((s, _, _) =>
            { if (s == source) completions++; return Task.CompletedTask; });
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = group * 100 + level, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = source.Face });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        long sp = source.CurrentSp ?? -1, cd = source.SkillCds.GetValueOrDefault(group);
        var first = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, n => n.SessionId == accepted.SessionId);
        long firstDamage = Assert.Single(first.Dsts, d => d.DstID == target.Uid).Damage;
        int stages = group == 12316 ? 4 : group is 12302 or 12342 ? 2 : 1;
        for (int stage = 1; stage < stages; stage++)
        {
            var next = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, n => n.SessionId == accepted.SessionId);
            firstDamage += Assert.Single(next.Dsts, d => d.DstID == target.Uid).Damage;
        }
        int copyId = CombatService.ReadingCopyGroup(group) * 100 + mode * 10 + level;
        var extra = await Next(bot, CombatService.ExtraUseInSkill, UseInSkillInfo.Parser, n => n.SkillID == copyId);
        Assert.False(extra.PlayAct);
        Assert.Equal(0, extra.ChantMs);
        Assert.Equal(target.Uid, extra.TargetId);
        var copied = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, n => n.SessionId == extra.SessionId);
        long copiedDamage = Assert.Single(copied.Dsts, d => d.DstID == target.Uid).Damage;
        for (int stage = 1; stage < stages; stage++)
        {
            var next = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, n => n.SessionId == extra.SessionId);
            copiedDamage += Assert.Single(next.Dsts, d => d.DstID == target.Uid).Damage;
        }
        Assert.True(firstDamage > 0 && copiedDamage > 0);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(1000000 - firstDamage - copiedDamage, target.Hp);
            Assert.Equal(8 - pages, source.Energy[240001].Value);
            Assert.Equal(sp, source.CurrentSp);
            Assert.Equal(cd, source.SkillCds.GetValueOrDefault(group));
            Assert.Equal(1, completions);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public void ReadingSpellbookEveryLevelAndModeHasItsOwnMatchingCopyAndCost()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        int count = 0;
        foreach (var row in SkillConfig.All(data).Where(r => CombatService.ReadingCopyGroup(r.SkillId) != 0))
        {
            int expected = row.SkillId switch { 12302 => 1, 12315 or 12316 or 12342 => 5, _ => 3 };
            Assert.Equal(expected, CombatService.ReadingPageCost(row));
            var copy = SkillConfig.Get(data, CombatService.ReadingCopyGroup(row.SkillId) * 100L + row.Mode * 10 + row.Level);
            Assert.NotNull(copy);
            Assert.Equal(row.DamageParam1, copy.Value.DamageParam1);
            Assert.Equal(row.Level, copy.Value.Level);
            Assert.Equal(row.Mode, copy.Value.Mode);
            count++;
        }
        Assert.True(count >= 100);
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingSpellbookInsufficientPagesOrClearedLeaseCannotDealAnExtraHit(bool clear)
    {
        await using var bot = await InWorld("ReadingStop");
        IsolateDirectHealing();
        await Gm("readingstop", "!job 2400"); await Teach("readingstop", 12302);
        await HealingStat(bot, 102, 10000); await HealingStat(bot, 104, 1000);
        var source = Session("readingstop"); var target = await Target("readingstop", true, immobile: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 1000000; target.StaysOutOfBattle = true;
            await Character.SetHpSpAsync(source, null, 10000);
            await buffs.ReconcileEnergyAsync(source);
            source.Energy[240001].Value = clear ? 8 : 0;
            source.Energy[240001].NextRecoverMs = long.MaxValue;
            await buffs.AddAsync(source, 1231001, 60000, source.Role!.Id);
            combat.SkillCastCompleted.Add((s, _, _) =>
            { if (s == source) completed.TrySetResult(); return Task.CompletedTask; });
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1230201, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var first = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, n => n.SessionId == accepted.SessionId);
        var second = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, n => n.SessionId == accepted.SessionId);
        long originalDamage = Assert.Single(first.Dsts, d => d.DstID == target.Uid).Damage
            + Assert.Single(second.Dsts, d => d.DstID == target.Uid).Damage;
        await completed.Task.WaitAsync(Wait);
        if (clear) await Loop.InvokeAsync(() => combat.ClearSkillsAsync(source));
        await Task.Delay(1800);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(1000000 - originalDamage, target.Hp);
            Assert.Equal(clear ? 7 : 0, source.Energy[240001].Value);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task ReadingSpellbookRealToggleOffRestoresModeAndEnergyRecoversOnlyToEight()
    {
        await using var bot = await InWorld("ReadingToggle");
        await Gm("readingtoggle", "!job 2400"); await Teach("readingtoggle", 12319);
        var source = Session("readingtoggle"); var buffs = _app.Services.GetRequiredService<BuffService>();
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1231901, TargetId = source.Role!.Id });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == 1231001));
        await Task.Delay(600);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1231941, TargetId = source.Role.Id });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, BuffService.BuffRemove, BuffRemoveNotify.Parser);
        await Loop.InvokeAsync(async () =>
        {
            Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1231001);
            await buffs.ReconcileEnergyAsync(source);
            var meter = source.Energy[240001]; meter.Value = 6;
            meter.NextRecoverMs = MonsterService.NowMs - 5500;
            buffs.Tick(default);
            Assert.Equal(8, meter.Value);
            Assert.InRange(meter.NextRecoverMs - MonsterService.NowMs, 4000, 5000);
        });
    }
}
