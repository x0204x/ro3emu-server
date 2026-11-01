using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private const int SceneFlyableCondition = 2009;

    // 配對LC_InteractCondition.CheckSceneFlyable不讀參數1，從NowMapInfo.mapId取得MapConfig。
    // mapId是SceneEntry設定ID，非分線／副本的WorldScene；此處只驗施放條件，不實作起飛。
    internal bool? EvaluateFlyMountCastCondition(GameSession source, long[] values) =>
        values is [SceneFlyableCondition, _]
            ? FlyMountMapAllowed(MapConfig.Get(_data, source.SceneEntry)?.Row) : null;

    /// <summary>Lua_CFG_MapConfigBase.CanFlyMount只讀_kRiderForbidden第三欄；不是_iFly。
    /// 原件找不到地圖為false；缺少／格式未知的欄位保留unknown，不推定允許或改用其他旗標。</summary>
    internal static bool? FlyMountMapAllowed(JsonElement? map)
    {
        if (map is not { } row) return false;
        if (row.ValueKind != JsonValueKind.Object
            || !row.TryGetProperty("_kRiderForbidden", out var flags)
            || flags.ValueKind != JsonValueKind.Array || flags.GetArrayLength() < 3
            || flags[2].ValueKind != JsonValueKind.Number || !flags[2].TryGetInt64(out _)) return null;
        return new MapConfig(row).RiderForbidden[2] == 0;
    }
}
