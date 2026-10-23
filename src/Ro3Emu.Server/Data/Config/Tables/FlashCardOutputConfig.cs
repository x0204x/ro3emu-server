using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's FlashCardOutputConfig (data/config/FlashCardOutputConfig.json).</summary>
[ConfigTable("FlashCardOutputConfig")]
public readonly partial record struct FlashCardOutputConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iOpenDay")] public partial long OpenDay { get; }
    [ConfigField("_iQuality")] public partial long Quality { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kOutputPool")] public partial long[] OutputPool { get; }
}
