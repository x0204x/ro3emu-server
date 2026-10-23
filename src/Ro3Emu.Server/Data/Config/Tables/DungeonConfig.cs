using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's DungeonConfig (data/config/DungeonConfig.json).</summary>
[ConfigTable("DungeonConfig")]
public readonly partial record struct DungeonConfig(JsonElement Row)
{
    [ConfigField("_iDLoadLv")] public partial long DLoadLv { get; }
    [ConfigField("_iDecayType")] public partial long DecayType { get; }
    [ConfigField("_iDungeonType")] public partial long DungeonType { get; }
    [ConfigField("_iGamePlayType")] public partial long GamePlayType { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLimitTime")] public partial long LimitTime { get; }
    [ConfigField("_iMapResourceID")] public partial long MapResourceId { get; }
    [ConfigField("_iName")] public partial long[] Name { get; }
    [ConfigField("_iPerformanceForbidden")] public partial long PerformanceForbidden { get; }
    [ConfigField("_iRoleCntLimit")] public partial long RoleCntLimit { get; }
    [ConfigField("_kAdventureGroupWhitelist")] public partial long[] AdventureGroupWhitelist { get; }
    [ConfigField("_kGuildWhitelist")] public partial long[] GuildWhitelist { get; }
    [ConfigField("_kResSceneEditorName")] public partial string ResSceneEditorName { get; }
}
