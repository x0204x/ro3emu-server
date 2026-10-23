using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JobConfig (data/config/JobConfig.json).</summary>
[ConfigTable("JobConfig")]
public readonly partial record struct JobConfig(JsonElement Row)
{
    [ConfigField("_iDistanceType")] public partial long DistanceType { get; }
    [ConfigField("_iDungeonJobPositionID")] public partial long[] DungeonJobPositionId { get; }
    [ConfigField("_iGenderLimit")] public partial long GenderLimit { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iJob")] public partial long Job { get; }
    [ConfigField("_iJobRank")] public partial long JobRank { get; }
    [ConfigField("_iName")] public partial long[] Name { get; }
    [ConfigField("_iNotTransformJob")] public partial long NotTransformJob { get; }
    [ConfigField("_iParentId")] public partial long ParentId { get; }
    [ConfigField("_iSecondaryRoots")] public partial long SecondaryRoots { get; }
    [ConfigField("_iTotem")] public partial long Totem { get; }
    [ConfigField("_kAttri")] public partial long[][] Attri { get; }
    [ConfigField("_kSkillNormalID")] public partial long[] SkillNormalId { get; }
    [ConfigField("_kSkillReplace")] public partial long[][] SkillReplace { get; }
}
