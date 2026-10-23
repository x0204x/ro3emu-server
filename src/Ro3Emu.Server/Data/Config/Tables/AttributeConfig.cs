using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AttributeConfig (data/config/AttributeConfig.json).</summary>
[ConfigTable("AttributeConfig")]
public readonly partial record struct AttributeConfig(JsonElement Row)
{
    [ConfigField("_iCalculateType")] public partial long CalculateType { get; }
    [ConfigField("_iDataType")] public partial long DataType { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iPower")] public partial double Power { get; }
    [ConfigField("_iPreAttri")] public partial long PreAttri { get; }
}
