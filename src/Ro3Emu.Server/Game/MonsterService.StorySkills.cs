using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class MonsterService
{
    /// <summary>保留兩／三種已觀察普攻。各種機率與選擇條件 UNKNOWN，等機率選取為 inferred。</summary>
    private void PrepareStoryNormalAttack(MonsterUnit monster)
    {
        if (!StoryBossSkillRules.IsObservedStoryBoss(monster.InstanceId, monster.Template.NpcId)) return;
        var variants = BossAttacks.VariantsFor(monster.Template.NpcId);
        if (variants.Length < 2) return;
        int id = (int)variants[Random.Shared.Next(variants.Length)];
        if (SkillConfig.Get(_data, id) is not { } skill) return;
        float hitPoint = monster.Template.HitPointSeconds;
        if (_data.SkillTemplate(skill.ScriptShow) is { } show
            && new SkillTimelineData(show).AttackPoints is { Length: > 0 } points && points[0] >= 0)
            hitPoint = (float)points[0];
        int action = (int)Math.Max(300, skill.ActionTime);
        // 僅替換這一個單位的 immutable template；CombatService 的延遲命中持有施放時快照。
        monster.Template = monster.Template with
        {
            NormalSkill = id,
            SkillMs = action,
            HitPointSeconds = hitPoint,
            SwingIntervalMs = (int)Math.Max(action, skill.Cd),
            AttackRange = Math.Max(1f, skill.DistanceMax / 100f),
        };
    }
}
