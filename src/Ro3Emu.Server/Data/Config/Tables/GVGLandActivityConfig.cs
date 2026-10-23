using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GVGLandActivityConfig (data/config/GVGLandActivityConfig.json).</summary>
[ConfigTable("GVGLandActivityConfig")]
public readonly partial record struct GVGLandActivityConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iExtraAward")] public partial long ExtraAward { get; }
    [ConfigField("_iGamePlayType")] public partial long GamePlayType { get; }
    [ConfigField("_iMapResourceID")] public partial long MapResourceId { get; }
    [ConfigField("_iNPCid")] public partial long NPCid { get; }
    [ConfigField("_iOwnerRange")] public partial long OwnerRange { get; }
    [ConfigField("_iPointID")] public partial long PointId { get; }
    [ConfigField("_iRespawnCD")] public partial long RespawnCD { get; }
    [ConfigField("_iRestrictId")] public partial long RestrictId { get; }
}
