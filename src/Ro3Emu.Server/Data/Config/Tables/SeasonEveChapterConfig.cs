using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SeasonEveChapterConfig (data/config/SeasonEveChapterConfig.json).</summary>
[ConfigTable("SeasonEveChapterConfig")]
public readonly partial record struct SeasonEveChapterConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iClueCount")] public partial long ClueCount { get; }
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iId")] public partial long Id { get; }
    [ConfigField("_iNoteTypeCount")] public partial long NoteTypeCount { get; }
    [ConfigField("_kClueList")] public partial long[] ClueList { get; }
}
