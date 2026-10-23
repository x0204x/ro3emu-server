using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BuffConfig (data/config/BuffConfig.json).</summary>
[ConfigTable("BuffConfig")]
public readonly partial record struct BuffConfig(JsonElement Row)
{
    [ConfigField("_iBuffID")] public partial long BuffId { get; }
    [ConfigField("_iDeadClear")] public partial long DeadClear { get; }
    [ConfigField("_iDescription")] public partial JsonElement? Description { get; }
    [ConfigField("_iDisperse")] public partial long Disperse { get; }
    [ConfigField("_iDmgType")] public partial long DmgType { get; }
    [ConfigField("_iEleType")] public partial long EleType { get; }
    [ConfigField("_iHandRemove")] public partial long HandRemove { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iMaxStack")] public partial long MaxStack { get; }
    [ConfigField("_iMultiSource")] public partial long MultiSource { get; }
    [ConfigField("_iName")] public partial double[] Name { get; }
    [ConfigField("_iSceneClear")] public partial long SceneClear { get; }
    [ConfigField("_iShowIcon")] public partial long ShowIcon { get; }
    [ConfigField("_iStackType")] public partial long StackType { get; }
    [ConfigField("_iTargetMax")] public partial long TargetMax { get; }
    [ConfigField("_iTime")] public partial long Time { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_kBuffAttributeList")] public partial JsonElement? BuffAttributeList { get; }
    [ConfigField("_kBuffCategory")] public partial long[] BuffCategory { get; }
    [ConfigField("_kChangeAttri")] public partial long[][] ChangeAttri { get; }
    [ConfigField("_kDamageFormulaExtra")] public partial string[] DamageFormulaExtra { get; }
    [ConfigField("_kDamageParamExtra")] public partial long[][] DamageParamExtra { get; }
    [ConfigField("_kData1")] public partial long[] Data1 { get; }
    [ConfigField("_kDescData")] public partial string[] DescData { get; }
    [ConfigField("_kLogicData")] public partial long[] LogicData { get; }
    [ConfigField("_kSkillAttributeList")] public partial JsonElement? SkillAttributeList { get; }
}
