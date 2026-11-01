using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// A High Priest never saw the skill window's Lineup tab (26 Oct). The client lists it only when its skill list holds
    /// the job's own Lineup skill (JobConfig._iQueueSkillId, 1400001 for the Acolyte line, SkillConfig _iSystemType 6),
    /// which is in no skill tree row, so the server never sent it. The Lineup button joins the wheel once a skill is in it.
    /// </summary>
    [ClientDataFact]
    public async Task TheAcolyteLineGetsTheLineupTabAndItsButtonOnceASkillIsInIt()
    {
        const int Blessing = 14001, LineupGroup = 14000, LineupRow = 1400001;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99 40");
        await Gm("alice", "!job 4300");
        var skills = _app.Services.GetRequiredService<SkillService>();
        Task<DBSkillData> Built() => Loop.InvokeAsync(() => Task.FromResult(skills.Build(Session("alice"))));

        var before = await Built();
        Assert.Contains(LineupGroup, before.SkillList);
        Assert.Equal(1, before.SkillAssignPlans[before.CurrentPlanIndex].SkillAssigns[LineupGroup].SkillLevel);
        Assert.Null(before.SkillWheel.SkillQueue);

        var learn = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
        learn.LevelUpList[14013] = new SkillData { SkillId = 14013, SkillLevel = 5 }; // Blessing needs it at 5
        learn.LevelUpList[Blessing] = new SkillData { SkillId = Blessing, SkillLevel = 1 };
        await alice.SendGameAsync(SkillService.PlanUpdate, learn);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        await alice.SendGameAsync(SkillService.QueueUpdate, new SkillQueueUpdateReq
        {
            Plan = 0, OpType = SkillLoadOperation.SkillLoadOpLoad, QueueIndex = 0,
            Info = new SkillData { SkillId = Blessing, SkillLevel = 1 }
        });
        Assert.Equal(0, (await Next(alice, SkillService.QueueUpdate, SkillQueueUpdateResp.Parser)).Result);

        var after = await Built();
        Assert.Equal(LineupRow, after.SkillWheel.SkillQueue.SkillConfigId);
        // LC_SkillGrowthHelper.GetCanUseSkillQueueSkillId reads the slot map, not skillWheel.skillQueue.
        int queueSlot = (int)SkillWheelSlotType.SkillWheelSkillQueue;
        Assert.Equal(LineupRow, after.SkillWheel.WhellSkills[queueSlot].SkillConfigId);
        Assert.True(after.SkillWheel.WhellSkills[queueSlot].Enable);
        Assert.Equal(LineupRow, after.OriginSkillWheel.WhellSkills[queueSlot].SkillConfigId);
        Assert.Contains(LineupGroup, after.SkillList);
    }

    /// <summary>A Novice has no Lineup skill: no tab to show.</summary>
    [ClientDataFact]
    public async Task ANoviceHasNoLineupSkill()
    {
        await using var alice = await InWorld("Alice");
        var skills = _app.Services.GetRequiredService<SkillService>();
        var data = await Loop.InvokeAsync(() => Task.FromResult(skills.Build(Session("alice"))));
        Assert.DoesNotContain(14000, data.SkillList);
        Assert.Equal(0, skills.QueueGroup(1000));
    }

    /// <summary>
    /// The Skill Bar button (SkillConfig _iSystemType 6, script jinengduilie1) casts the Lineup: each skill in it, in slot
    /// order, one after the other, once. A skill that cannot be cast is skipped.
    /// </summary>
    [ClientDataFact]
    public async Task TheSkillBarCastsEverySkillInTheLineupInOrder()
    {
        const int Blessing = 14001, IncreaseAgi = 14010, SkillBar = 1400001, BlessingBuff = 1400101, IncreaseAgiBuff = 1401001;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99 40");
        await Gm("alice", "!job 4300");
        var learn = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
        foreach (var (group, level) in new[] { (14013, 5), (Blessing, 1), (14007, 5), (IncreaseAgi, 1) })
            learn.LevelUpList[group] = new SkillData { SkillId = group, SkillLevel = level };
        await alice.SendGameAsync(SkillService.PlanUpdate, learn);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        int slot = 0;
        foreach (int group in new[] { IncreaseAgi, Blessing }) // slot order, not skill order
        {
            await alice.SendGameAsync(SkillService.QueueUpdate, new SkillQueueUpdateReq
            {
                Plan = 0, OpType = SkillLoadOperation.SkillLoadOpLoad, QueueIndex = slot++,
                Info = new SkillData { SkillId = group, SkillLevel = 1 }
            });
            Assert.Equal(0, (await Next(alice, SkillService.QueueUpdate, SkillQueueUpdateResp.Parser)).Result);
        }

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = SkillBar, TargetId = Session("alice").Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var first = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, x => x.Buffs.Any(b => b.ConfigId is IncreaseAgiBuff or BlessingBuff));
        Assert.Contains(first.Buffs, b => b.ConfigId == IncreaseAgiBuff);
        var second = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, x => x.Buffs.Any(b => b.ConfigId == BlessingBuff));
        Assert.Contains(second.Buffs, b => b.ConfigId == BlessingBuff);
    }
}
