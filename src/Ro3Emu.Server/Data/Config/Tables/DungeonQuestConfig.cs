using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DungeonQuestConfig (data/config/DungeonQuestConfig.json).</summary>
[ConfigTable("DungeonQuestConfig")]
public readonly partial record struct DungeonQuestConfig(JsonElement Row)
{
    [ConfigField("_iDungeonID")] public partial long DungeonId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iQuestID")] public partial long QuestId { get; }
}
