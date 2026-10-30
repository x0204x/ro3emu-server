using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's JobProfessPointConfig (data/config/JobProfessPointConfig.json): a profession's recommended
/// stats at a base level, id = profession * 1000 + level (Lua_CFG_JobProfessPointConfig.GetAddPointConfigByProfessionId).</summary>
[ConfigTable("JobProfessPointConfig")]
public readonly partial record struct JobProfessPointConfig(JsonElement Row)
{
    [ConfigField("_iAgi")] public partial long Agi { get; }
    [ConfigField("_iBaseLv")] public partial long BaseLv { get; }
    [ConfigField("_iDex")] public partial long Dex { get; }
    [ConfigField("_iInt")] public partial long Intelligence { get; }
    [ConfigField("_iLuk")] public partial long Luk { get; }
    [ConfigField("_iStr")] public partial long Str { get; }
    [ConfigField("_iVii")] public partial long Vii { get; }

    /// <summary>The six stats in the order of the stat plan (1 Str, 2 Agi, 3 Vit, 4 Int, 5 Dex, 6 Luk: LC_DAL_PlayerInfo).</summary>
    public int[] Stats => [(int)Str, (int)Agi, (int)Vii, (int)Intelligence, (int)Dex, (int)Luk];
}
