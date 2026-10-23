using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's EquipConfig (data/config/EquipConfig.json).</summary>
[ConfigTable("EquipConfig")]
public readonly partial record struct EquipConfig(JsonElement Row)
{
    [ConfigField("_iEntries")] public partial long Entries { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIfAppraise")] public partial long IfAppraise { get; }
    [ConfigField("_iRank")] public partial long Rank { get; }
    [ConfigField("_kBasicAttribute")] public partial long[][] BasicAttribute { get; }
    [ConfigField("_kFixedEntries")] public partial long[][] FixedEntries { get; }
    [ConfigField("_kFixedSpecialAttribute")] public partial long[] FixedSpecialAttribute { get; }
    [ConfigField("_kSpecial")] public partial long[][] Special { get; }
}
