using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MapConfig (data/config/MapConfig.json).</summary>
[ConfigTable("MapConfig")]
public readonly partial record struct MapConfig(JsonElement Row)
{
    [ConfigField("_iAllowCreateLine")] public partial long AllowCreateLine { get; }
    [ConfigField("_iFly")] public partial long Fly { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLimitType")] public partial long LimitType { get; }
    [ConfigField("_iMapResourceID")] public partial long MapResourceId { get; }
    [ConfigField("_iName")] public partial long[] Name { get; }
    [ConfigField("_iPerformanceForbidden")] public partial long PerformanceForbidden { get; }
    [ConfigField("_iRoleAlert")] public partial long RoleAlert { get; }
    [ConfigField("_iRoleCntLimit")] public partial long RoleCntLimit { get; }
    [ConfigField("_iSceneType")] public partial long SceneType { get; }
    [ConfigField("_kAdventureGroupWhitelist")] public partial long[] AdventureGroupWhitelist { get; }
    [ConfigField("_kGuildWhitelist")] public partial long[] GuildWhitelist { get; }
    [ConfigField("_kItemRevive")] public partial JsonElement? ItemRevive { get; }
    [ConfigField("_kLimitItem")] public partial string LimitItem { get; }
    [ConfigField("_kPVPMod")] public partial long[][] PVPMod { get; }
    [ConfigField("_kResSceneEditorName")] public partial string ResSceneEditorName { get; }
    [ConfigField("_kRiderForbidden")] public partial long[] RiderForbidden { get; }
}
