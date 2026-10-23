using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TaskConfig (data/config/TaskConfig.json).</summary>
[ConfigTable("TaskConfig")]
public readonly partial record struct TaskConfig(JsonElement Row)
{
    [ConfigField("_iAward")] public partial long Award { get; }
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iDungeonID")] public partial long DungeonId { get; }
    [ConfigField("_iIfGiveUp")] public partial long IfGiveUp { get; }
    [ConfigField("_iIfReset")] public partial long IfReset { get; }
    [ConfigField("_iName")] public partial JsonElement? Name { get; }
    [ConfigField("_iStrongerRank")] public partial long StrongerRank { get; }
    [ConfigField("_iTaskID")] public partial long TaskId { get; }
    [ConfigField("_iTaskSonType")] public partial long TaskSonType { get; }
    [ConfigField("_iTaskType")] public partial long TaskType { get; }
    [ConfigField("_kAcceptAction")] public partial JsonElement? AcceptAction { get; }
    [ConfigField("_kAcceptTask")] public partial long[] AcceptTask { get; }
    [ConfigField("_kDeliverTask")] public partial long[] DeliverTask { get; }
    [ConfigField("_kEXECAction")] public partial JsonElement? EXECAction { get; }
    [ConfigField("_kEXP")] public partial long[] Exp { get; }
    [ConfigField("_kMultiverseArray", Optional = true)] public partial long[] MultiverseArray { get; }
    [ConfigField("_kPreTask")] public partial long[] PreTask { get; }
    [ConfigField("_kProgressTarget")] public partial long[][] ProgressTarget { get; }
    [ConfigField("_kTrack")] public partial JsonElement? Track { get; }
}
