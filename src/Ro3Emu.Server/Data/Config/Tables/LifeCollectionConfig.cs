using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's LifeCollectionConfig (data/config/LifeCollectionConfig.json).</summary>
[ConfigTable("LifeCollectionConfig")]
public readonly partial record struct LifeCollectionConfig(JsonElement Row)
{
    [ConfigField("_iCollectionId")] public partial long CollectionId { get; }
    [ConfigField("_iCondition")] public partial long Condition { get; }
    [ConfigField("_iDropId")] public partial long[] DropId { get; }
    [ConfigField("_iGameplay")] public partial long[][] Gameplay { get; }
    [ConfigField("_iJobId")] public partial long JobId { get; }
    [ConfigField("_iMapIds")] public partial long[] MapIds { get; }
    [ConfigField("_iPlayParam")] public partial long[] PlayParam { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
}
