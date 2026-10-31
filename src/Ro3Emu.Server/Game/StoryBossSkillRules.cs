using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>2026-10-31 原服任務收錄證實的技能繫結；不把單次施放順序當成 AI 規則。</summary>
internal static class StoryBossSkillRules
{
    internal const int Tiger = 5300001, Maya = 5300006, AirshipBoss = 5300018, Peco = 5300082;
    internal const int MayaStateSkill = 5360601, MayaStateRelease = 5365401, MayaStateBuff = 5366001;
    internal const int PecoAreaSkill = 5370201, PecoExtraSkill = 5370501;
    internal const int PecoWarning = 5370202, PecoCircle = 5370203;

    internal static bool IsObservedStoryBoss(long instanceId, int npcId) =>
        instanceId > 0 && npcId is Tiger or Maya or AirshipBoss or Peco;

    internal static bool UsesHighLevel(long instanceId, int npcId) => IsObservedStoryBoss(instanceId, npcId);

    internal static bool UsesExtra(long instanceId, int npcId, int skillId) =>
        instanceId > 0 && npcId == Peco && skillId == PecoExtraSkill;

    internal static long[] ObservedNormalIds(int npcId) => npcId switch
    {
        Tiger => [5300101, 5300601],
        AirshipBoss => [5352001, 5352101, 5352201],
        _ => [],
    };

    internal static bool TargetsSelf(int npcId, int skillId) => (npcId, skillId) is
        (Peco, PecoAreaSkill) or (Maya, MayaStateSkill) or (Maya, MayaStateRelease) or (Tiger, 5300901);

    // 5360601 的腳本名稱為空；實包 125303/125309 將它繫結到蟻后與自體狀態。
    internal static IEnumerable<long> AdditionalCandidates(GameData data, int npcId) =>
        npcId == Maya && SkillConfig.Get(data, MayaStateSkill) != null ? [MayaStateSkill] : [];

    internal static bool HasUnknownImpact(int npcId, int skillId) =>
        (npcId, skillId) is (Peco, PecoAreaSkill) or (Maya, MayaStateSkill);
}
