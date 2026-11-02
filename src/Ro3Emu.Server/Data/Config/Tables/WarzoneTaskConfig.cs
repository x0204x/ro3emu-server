using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's WarzoneTaskConfig (data/config/WarzoneTaskConfig.json, registered in Lua_DBManager but read by no
/// client Lua): a territory battle's personal score per action. _iType 1 (_iTargetID 0, 50) is the last hit on a
/// capture point's actor; _iType 2 is a kill of the NPC _iTargetID. Its two type 2 rows name 5000001 Poring (+20) and
/// 5000002 Lunatic (+10), which only the starting fields place (prt/pay/gef_fild scene editors), never a territory
/// battle scene: test rows that cannot score in a real battle, read as the table says all the same.
/// </summary>
[ConfigTable("WarzoneTaskConfig")]
public readonly partial record struct WarzoneTaskConfig(JsonElement Row)
{
    public const long CaptureLastHit = 1, Kill = 2;

    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iType")] public partial long Type { get; }
    [ConfigField("_iTargetID")] public partial long TargetId { get; }
    [ConfigField("_iScore")] public partial long Score { get; }

    /// <summary>The score of the first row of a type and target (0 when none).</summary>
    public static long ScoreOf(GameData data, long type, long target) =>
        All(data).Where(r => r.Type == type && r.TargetId == target).Select(r => r.Score).FirstOrDefault();
}
