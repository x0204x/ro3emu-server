using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's BattleGloryBase (data/config/BattleGloryBase.json).</summary>
[ConfigTable("BattleGloryBase")]
public readonly partial record struct BattleGloryBase(JsonElement Row)
{
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iKillBase")] public partial long KillBase { get; }
    [ConfigField("_iAssistBase")] public partial long AssistBase { get; }
    [ConfigField("_iDeathBase")] public partial long DeathBase { get; }
}
