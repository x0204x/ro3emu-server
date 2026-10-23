using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LifeGamePlayAward (data/config/LifeGamePlayAward.json).</summary>
[ConfigTable("LifeGamePlayAward")]
public readonly partial record struct LifeGamePlayAward(JsonElement Row)
{
    [ConfigField("_iExtraAwardNum")] public partial long ExtraAwardNum { get; }
}
