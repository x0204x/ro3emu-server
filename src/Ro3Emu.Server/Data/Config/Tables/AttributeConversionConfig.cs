using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's AttributeConversionConfig (data/config/AttributeConversionConfig.json).</summary>
[ConfigTable("AttributeConversionConfig")]
public readonly partial record struct AttributeConversionConfig(JsonElement Row)
{
    [ConfigField("_kAttribute1")] public partial double[][] Attribute1 { get; }
    [ConfigField("_kAttribute2")] public partial double[][] Attribute2 { get; }
}
