using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LifeDropConfig (data/config/LifeDropConfig.json).</summary>
[ConfigTable("LifeDropConfig")]
public readonly partial record struct LifeDropConfig(JsonElement Row)
{
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iDropRate")] public partial long DropRate { get; }
    [ConfigField("_iIsMain")] public partial long IsMain { get; }
    [ConfigField("_iItemId")] public partial long[] ItemId { get; }
    [ConfigField("_iMultiGroupIDId")] public partial long MultiGroupIdId { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_kUnlockcost")] public partial JsonElement? Unlockcost { get; }
}
