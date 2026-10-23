using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MultiverseDan (data/config/MultiverseDan.json).</summary>
[ConfigTable("MultiverseDan")]
public readonly partial record struct MultiverseDan(JsonElement Row)
{
    [ConfigField("_iDanLevel")] public partial long DanLevel { get; }
    [ConfigField("_iExpLimit")] public partial long ExpLimit { get; }
    [ConfigField("_iFunctionID")] public partial long FunctionId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iNormalRewardGroup")] public partial long NormalRewardGroup { get; }
}
