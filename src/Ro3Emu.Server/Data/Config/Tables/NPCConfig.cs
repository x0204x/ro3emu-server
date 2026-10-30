using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's NPCConfig (data/config/NPCConfig.json).</summary>
[ConfigTable("NPCConfig")]
public readonly partial record struct NPCConfig(JsonElement Row)
{
    [ConfigField("_iAI")] public partial long Ai { get; }
    [ConfigField("_iAISpecial")] public partial string AISpecial { get; }
    [ConfigField("_iAttribute")] public partial long Attribute { get; }
    [ConfigField("_iAttributeVariated")] public partial long AttributeVariated { get; }
    [ConfigField("_iBattleAttributeVariated")] public partial long BattleAttributeVariated { get; }
    [ConfigField("_iBodyRemainTime")] public partial long BodyRemainTime { get; }
    [ConfigField("_iUnitID")] public partial long UnitId { get; }
    [ConfigField("_iCAward")] public partial long CAward { get; }
    [ConfigField("_iCTimes")] public partial long[] CTimes { get; }
    [ConfigField("_iCamp")] public partial long Camp { get; }
    [ConfigField("_iDistanceType")] public partial long DistanceType { get; }
    [ConfigField("_iDrop")] public partial long Drop { get; }
    [ConfigField("_iElement")] public partial long Element { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iImportantSkill")] public partial long ImportantSkill { get; }
    [ConfigField("_iInteractDistance")] public partial double InteractDistance { get; }
    [ConfigField("_iInteractID")] public partial long[] InteractId { get; }
    [ConfigField("_iInterval")] public partial long Interval { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iNPCSubType")] public partial long NPCSubType { get; }
    [ConfigField("_iNPCType")] public partial long NPCType { get; }
    [ConfigField("_iNeedNotifyGlobalData")] public partial long NeedNotifyGlobalData { get; }
    [ConfigField("_iNoSelected")] public partial long NoSelected { get; }
    [ConfigField("_iNormalDrop")] public partial long[] NormalDrop { get; }
    /// <summary>The model's radius in cm (the City Gate's 1000, the ram's 500).</summary>
    [ConfigField("_iPrefabRadius")] public partial long PrefabRadius { get; }
    [ConfigField("_iRace")] public partial long Race { get; }
    [ConfigField("_iSize")] public partial long Size { get; }
    [ConfigField("_iSpeed")] public partial long Speed { get; }
    [ConfigField("_iSubTypeValue")] public partial long SubTypeValue { get; }
    [ConfigField("_iWildMonsters")] public partial long WildMonsters { get; }
    [ConfigField("_kAttriScale")] public partial long[][] AttriScale { get; }
    [ConfigField("_kBornEventBuff")] public partial long[][] BornEventBuff { get; }
    /// <summary>The portrait the client shows for the NPC or monster (a sprite name, "headicon_monster_poring.png").</summary>
    [ConfigField("_kHeadIcon")] public partial string HeadIcon { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kName")] public partial long[] Name { get; }
    [ConfigField("_kNpcShowCondition")] public partial long NpcShowCondition { get; }
    [ConfigField("_kNpcTag")] public partial long[] NpcTag { get; }
    [ConfigField("_kRespawnSkill")] public partial long RespawnSkill { get; }
    [ConfigField("_kSkills")] public partial long[] Skills { get; }
}
