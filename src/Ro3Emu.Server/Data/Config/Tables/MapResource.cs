using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's MapResource (data/config/MapResource.json).</summary>
[ConfigTable("MapResource")]
public readonly partial record struct MapResource(JsonElement Row)
{
    [ConfigField("_kNavDataName")] public partial string NavDataName { get; }
    /// <summary>The Unity scene the client loads for the map ("sc_prontera_001", Lua_CFG_MapConfigBase.GetSceneResName).</summary>
    [ConfigField("_kResource")] public partial string Resource { get; }
}
