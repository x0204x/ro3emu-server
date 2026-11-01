using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task EmblemVectorLowAndHighestApplyTheAuthoredSlowToLegalPvpTargets(int level)
    {
        await using var alice = await InWorld("EchoVectorPvp");
        await using var bob = await InWorld("EchoVectorTarget");
        await Gm("echovectorpvp", "!job 2400"); await Teach("echovectorpvp", 12342, level);
        var source = Session("echovectorpvp"); var target = Session("echovectortarget");
        await CardOffenceRoom((alice, source), (bob, target));
        await EmblemMarksAsync(source, 1002, 10);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.SkillCastCompleted.Add((s, _, _) =>
        { if (s == source) done.TrySetResult(); return Task.CompletedTask; });
        await Loop.InvokeAsync(() =>
        {
            source.Position = target.Position.Clone();
            combat.SetEventPvPHandlers((a, b) => a == source && b == target, (_, _) => Task.CompletedTask);
            return Task.CompletedTask;
        });
        long speed = Character.Stats(target)[113]; Assert.True(speed > 0);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1234200 + level, TargetId = target.Role!.Id, TargetPos = target.Position.Clone() });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        long damage = 0;
        for (int branch = 0; branch < 2; branch++)
        {
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            var dst = Assert.Single(hit.Dsts); Assert.Equal(target.Role.Id, dst.DstID);
            Assert.True(dst.Damage > 0); damage += dst.Damage;
        }
        await done.Task.WaitAsync(Wait);
        Assert.Equal(1_000_000 - damage, target.CurrentHp);
        var slow = Assert.Single(target.Buffs, b => b.ConfigId == 1236710);
        Assert.Equal(5000, slow.DurationMs); Assert.Equal(source.Role!.Id, slow.SourceId);
        Assert.Contains(slow.Changes, c => c.Attr == 1131 && c.Value == -2000);
        Assert.Equal((long)Math.Ceiling(speed * .8 - 1e-6), Character.Stats(target)[113]);
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task EmblemVectorExactStageRunsBothBranchesSlowAndReadingCopyWithoutDuplicateCosts(int level)
    {
        await using var bot = await InWorld("EchoVector");
        IsolateDirectHealing();
        await Gm("echovector", "!job 2400"); await Teach("echovector", 12342, level);
        await HealingStat(bot, 102, 10000);
        var source = Session("echovector"); var target = await Target("echovector", true, immobile: true);
        await CardOffenceDummyAsync(target); await IsolateAuthoredStageTargetAsync(source, target);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var emblems = _app.Services.GetRequiredService<EmblemService>();
        int requested = 1234200 + level, completions = 0;
        combat.SkillCastCompleted.Add((s, _, _) =>
        { if (s == source) completions++; return Task.CompletedTask; });

        SkillConfig Row(int group) => new(combat.EffectiveSkillRow(source, group * 100 + level)
            ?? throw new InvalidOperationException("Vector effective row required."));
        void AssertMode(int mode)
        {
            Assert.Equal(mode, Row(12342).Mode); Assert.Equal(mode, Row(12347).Mode);
            Assert.DoesNotContain(112304, source.Progress!.Skills.Keys);
        }

        async Task Cast(bool copied)
        {
            await HealingStat(bot, 104, 1000);
            await Loop.InvokeAsync(async () =>
            {
                await combat.InterruptAsync(source);
                source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear();
                await Character.SetHpSpAsync(source, null, 10000);
                if (copied)
                {
                    await buffs.ReconcileEnergyAsync(source);
                    source.Energy[240001].Value = 8; source.Energy[240001].NextRecoverMs = long.MaxValue;
                    await buffs.AddAsync(source, 1231001, 60000, source.Role!.Id);
                }
            });
            var row = Row(12342); var stats = Character.Stats(source);
            long before = target.Hp; int beforeLevel = source.Progress!.Level;
            long Expected(int branch) => (long)Math.Round(stats[104]
                * (row.DamageParam1[branch] + Math.Min(stats[5], 100) * 10) / 10000d * .75);
            using var cdWire = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
                && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(c => c.SkillId == 12342));
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
            { SkillId = requested, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = source.Face });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
            // 原列 CdModeGroup=1 不寫共享 SkillCds；伺服器按實際 mode 保存，31113 按所請 mode 顯示。
            long sp = source.CurrentSp ?? -1, cooldown = source.SkillModeCds.GetValueOrDefault((12342, (int)row.Mode));
            Assert.Equal(copied ? 10000 : 9935, sp); Assert.True(cooldown > 0);
            var cd = Assert.Single(SkillCdNotify.Parser.ParseFrom((await cdWire.WaitAsync(Wait)).Body).Infos, c => c.SkillId == 12342);
            Assert.Equal(0, cd.SkillMode); Assert.Equal(cooldown, cd.CdEndTimestampMs);
            Assert.Equal(15000, cd.CdEndTimestampMs - cd.CdStartTimestampMs);
            long total = 0;
            async Task Hits(long session)
            {
                for (int branch = 0; branch < 2; branch++)
                {
                    var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == session);
                    var dst = Assert.Single(hit.Dsts); Assert.Equal(target.Uid, dst.DstID);
                    Assert.Equal(Expected(branch), dst.Damage); Assert.Equal(branch == 0 ? 3 : 1, hit.Split);
                    total += dst.Damage;
                }
            }
            await Hits(accepted.SessionId);
            if (copied)
            {
                var copy = await Next(bot, CombatService.ExtraUseInSkill, UseInSkillInfo.Parser,
                    n => n.SkillID == 1234710 + level);
                Assert.False(copy.PlayAct); Assert.Equal(0, copy.ChantMs); await Hits(copy.SessionId);
            }
            await Loop.InvokeAsync(() =>
            {
                Assert.Equal(before - total, target.Hp); Assert.Equal(beforeLevel, source.Progress.Level);
                Assert.Equal(sp, source.CurrentSp);
                Assert.Equal(cooldown, source.SkillModeCds.GetValueOrDefault((12342, (int)row.Mode)));
                Assert.Equal(copied ? 2 : 1, completions);
                if (copied)
                {
                    Assert.Equal(3, source.Energy[240001].Value);
                    var slow = buffs.UnitActiveBuff(target, 1236710, source.Role!.Id);
                    Assert.NotNull(slow); Assert.Equal(5000, slow.DurationMs);
                    Assert.Contains(buffs.UnitAttributes(target), a => a.Id == 1131 && a.Value == -2000);
                }
                else Assert.Null(buffs.UnitActiveBuff(target, 1236710, source.Role!.Id));
                return Task.CompletedTask;
            });
        }

        await EmblemMarksAsync(source, 1002, 9); AssertMode(0);
        var original = Row(12342).DamageParam1;
        Assert.Null(combat.EffectiveSkillRow(source, requested + 10));
        await Cast(false);
        using var modeWire = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillAffectedEx)
            && SkillBeAffectedParamExNotify.Parser.ParseFrom(e.Body) is var n && n.IsAll
            && n.AffectParamDict.TryGetValue(12342, out var main) && main.AffectParam.TryGetValue(15, out var mainMode) && mainMode.AffectValue == 1);
        await EmblemMarksAsync(source, 1002, 10); AssertMode(1);
        var notice = SkillBeAffectedParamExNotify.Parser.ParseFrom((await modeWire.WaitAsync(Wait)).Body);
        Assert.Equal(source.Role!.Id, notice.Uuid);
        // 31132僅發已學技能；12347是讀書owner的內部副本，不應為了通知而永久授技。
        // 副本模式另由上方EffectiveRow及下方真31121/兩段HP驗證。
        Assert.DoesNotContain(12347, source.Progress!.Skills.Keys);
        Assert.DoesNotContain(12347, source.Progress.GrantedSkills.Keys);
        Assert.False(notice.AffectParamDict.ContainsKey(12347));
        Assert.Equal(original[0] + 6000, Row(12342).DamageParam1[0]);
        Assert.Equal(original[1] + 12000, Row(12342).DamageParam1[1]);
        await Loop.InvokeAsync(() => buffs.AddSupportAsync(source, 1235301, 60000, source.Role!.Id));
        AssertMode(1); Assert.Equal(original[0] + 6000, Row(12342).DamageParam1[0]);
        await Cast(true);
        await Loop.InvokeAsync(() => buffs.RemoveAsync(source, source.Buffs.Single(b => b.ConfigId == 1235301)));
        using var resetWire = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillAffectedEx)
            && SkillBeAffectedParamExNotify.Parser.ParseFrom(e.Body) is var n && n.IsAll
            && !n.AffectParamDict.ContainsKey(12342) && !n.AffectParamDict.ContainsKey(12347));
        await EmblemOwnedAsync(source, async () =>
        { source.Emblems.CurrentPlan = 1; await emblems.SyncGenrePlanAsync(source, source.EmblemPower); });
        await resetWire.WaitAsync(Wait);
        AssertMode(0);
        await EmblemOwnedAsync(source, async () =>
        { source.Emblems.CurrentPlan = 0; await emblems.SyncGenrePlanAsync(source, source.EmblemPower); });
        AssertMode(1);
        await Gm("echovector", "!job 1000"); AssertMode(0);
        await Gm("echovector", "!job 2400"); AssertMode(1);
        await EmblemMarksAsync(source, 1002, 0); AssertMode(0);
    }
}
