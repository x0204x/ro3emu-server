using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GVGCannonConfig (data/config/GVGCannonConfig.json): the siege cannons a war machine item builds.</summary>
[ConfigTable("GVGCannonConfig")]
public readonly partial record struct GVGCannonConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iItemID")] public partial long ItemId { get; }
    [ConfigField("_iNPCID")] public partial long NpcId { get; }
    [ConfigField("_iBuildRadius")] public partial long BuildRadius { get; }
    [ConfigField("_iExclusiveRadius")] public partial long ExclusiveRadius { get; }
    [ConfigField("_iBuildTime")] public partial long BuildTime { get; }
    [ConfigField("_iSkillID")] public partial long SkillId { get; }
    [ConfigField("_iBulletNumLimit")] public partial long BulletNumLimit { get; }
    [ConfigField("_iSingleShotNum")] public partial long SingleShotNum { get; }
    [ConfigField("_iReloadTime")] public partial long ReloadTime { get; }
    [ConfigField("_iExitInteractID")] public partial long ExitInteractId { get; }
    [ConfigField("_kButtonActionIDs")] public partial long[] ButtonActionIds { get; }
    [ConfigField("_kFireOffset")] public partial long[] FireOffset { get; }
}
