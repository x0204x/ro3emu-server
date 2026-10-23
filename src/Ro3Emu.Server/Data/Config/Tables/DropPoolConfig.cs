using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DropPoolConfig (data/config/DropPoolConfig.json).</summary>
[ConfigTable("DropPoolConfig")]
public readonly partial record struct DropPoolConfig(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
}
