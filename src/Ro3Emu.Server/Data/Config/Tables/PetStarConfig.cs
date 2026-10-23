using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PetStarConfig (data/config/PetStarConfig.json).</summary>
[ConfigTable("PetStarConfig")]
public readonly partial record struct PetStarConfig(JsonElement Row)
{
    [ConfigField("_iBaseAttr")] public partial long BaseAttr { get; }
    [ConfigField("_iFightOrAssistStrengthMultiple")] public partial long FightOrAssistStrengthMultiple { get; }
    [ConfigField("_iPassiveMain")] public partial long PassiveMain { get; }
    [ConfigField("_iPetID")] public partial long PetId { get; }
    [ConfigField("_iProtectSkill")] public partial long ProtectSkill { get; }
    [ConfigField("_iStage")] public partial long Stage { get; }
    [ConfigField("_iStar")] public partial long Star { get; }
    [ConfigField("_iStarAssistStrength")] public partial long StarAssistStrength { get; }
    [ConfigField("_iStarFightStrength")] public partial long StarFightStrength { get; }
    [ConfigField("_kActiveSkill")] public partial long[] ActiveSkill { get; }
    [ConfigField("_kAssistSkill")] public partial JsonElement? AssistSkill { get; }
    [ConfigField("_kCollectAttr")] public partial long[][] CollectAttr { get; }
    [ConfigField("_kFightSkill")] public partial long[][] FightSkill { get; }
    [ConfigField("_kPiece")] public partial long[] Piece { get; }
    [ConfigField("_kStageSkill")] public partial JsonElement? StageSkill { get; }
}
