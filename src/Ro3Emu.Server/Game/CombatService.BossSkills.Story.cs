using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>已開始的任務首領技能；場景演出自行檢查房間生命週期，不改戰鬥傷害。</summary>
    public event Func<MonsterUnit, int, long, Task>? StoryBossCastStarted;

    private async Task StoryBossSkillStartedAsync(MonsterUnit boss, BossSkill skill, long serial, Vec3 origin)
    {
        // 前面的封包發送可能讓出執行；回來時不能把已清除的狀態加回舊施放。
        if (!BossCastCurrent(boss, serial) || !StoryBossSkillRules.IsObservedStoryBoss(boss.InstanceId, boss.Template.NpcId)) return;
        if (boss.Template.NpcId == StoryBossSkillRules.Maya && skill.Id == StoryBossSkillRules.MayaStateSkill
            && BuffConfig.Get(_data, StoryBossSkillRules.MayaStateBuff) is { Time: -1 })
            await _buffs.AddPermanentAsync(boss, StoryBossSkillRules.MayaStateBuff, boss.Uid);
        if (boss.Template.NpcId == StoryBossSkillRules.Peco && skill.Id == StoryBossSkillRules.PecoAreaSkill)
            await StartStoryWarningAsync(boss, serial, origin, boss.Face);
        if (StoryBossCastStarted is { } handlers)
            foreach (Func<MonsterUnit, int, long, Task> handler in handlers.GetInvocationList())
                if (BossCastCurrent(boss, serial)) await handler(boss, skill.Id, serial);
    }

    private async Task StartStoryWarningAsync(MonsterUnit boss, long serial, Vec3 origin, float face)
    {
        if (StoryWarningData.Find(_data, StoryBossSkillRules.PecoWarning) is not { DurationMs: > 0 } warning) return;
        await SendStoryEffectAsync(boss, serial, origin, face, warning: true);
        // EffectData 的 m_fDuration 控制警示演出；不是傷害時點。原服五次間距約 2.5 秒。
        ScheduleRuntime(boss.WorldScene, TimeSpan.FromMilliseconds(warning.DurationMs),
            () => SendStoryEffectAsync(boss, serial, origin, face, warning: false));
    }

    private Task SendStoryEffectAsync(MonsterUnit boss, long serial, Vec3 origin, float face, bool warning) =>
        BossCastCurrent(boss, serial)
            ? _world.ToUnitViewersAsync(boss, SkillEffectHigh, StoryPecoEffect(boss.Uid, origin, face, warning))
            : Task.CompletedTask;

    /// <summary>31109：警示以首領為 source/target；圓圈效果是固定地點，僅保留 master。</summary>
    internal static SkillEffectDisplayNotify StoryPecoEffect(long caster, Vec3 origin, float face, bool warning) => new()
    {
        SourceId = warning ? caster : 0, TargetId = warning ? caster : 0, MasterId = caster,
        EffectId = warning ? StoryBossSkillRules.PecoWarning : StoryBossSkillRules.PecoCircle,
        EffectType = warning ? 6 : 2, SkillId = StoryBossSkillRules.PecoAreaSkill,
        TargetPos = origin.Clone(), Angle = warning ? face : 0,
    };

    private Task StoryBossImpactCompletedAsync(MonsterUnit boss, BossSkill skill, long serial, bool first) =>
        // 扣血流程會 await；舊釋放回呼不能清掉新一輪施放才取得的常駐狀態。
        first && BossCastCurrent(boss, serial) && boss.InstanceId > 0 && boss.Template.NpcId == StoryBossSkillRules.Maya
            && skill.Id == StoryBossSkillRules.MayaStateRelease
            ? _buffs.RemoveUnitConfigAsync(boss, StoryBossSkillRules.MayaStateBuff) : Task.CompletedTask;

    private Task EndStoryBossStateAsync(MonsterUnit boss) =>
        boss.InstanceId > 0 && boss.Template.NpcId == StoryBossSkillRules.Maya
            ? _buffs.RemoveUnitConfigAsync(boss, StoryBossSkillRules.MayaStateBuff) : Task.CompletedTask;
}
