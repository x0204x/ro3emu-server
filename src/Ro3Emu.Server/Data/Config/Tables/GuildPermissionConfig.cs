using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildPermissionConfig (data/config/GuildPermissionConfig.json).</summary>
[ConfigTable("GuildPermissionConfig")]
public readonly partial record struct GuildPermissionConfig(JsonElement Row)
{
    [ConfigField("_iDetail")] public partial long[][] Detail { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIsShow")] public partial long IsShow { get; }
}
