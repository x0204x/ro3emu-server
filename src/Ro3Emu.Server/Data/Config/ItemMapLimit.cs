using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// Whether the scene a role stands in lets an item be used, as the client decides it (LC_ItemHelper, the check around
/// Lua_CFG_MapConfigBase.GetLimitType). GetEntryPtr finds the scene's row: ids 1-99999 in MapConfig, ids 1000001-9999999
/// in DungeonConfig. A scene without a row, or with _iLimitType 0, limits nothing; _iLimitType 1 forbids and 2 allows only
/// the _kLimitItem entries, [1, type] or [2, subtype].
/// <para>
/// ItemConfig._kUseLimit and ItemUseLimitConfig are not enforced. (inferred) No client Lua reads that table, and every
/// other map rule is mirrored in the client. Its rows list 10110/10111, but both maps set MapConfig._iFly 1, the flag that
/// bans wings everywhere else. "Usable only there" is refuted by mandatory objectives that use these items on other maps;
/// "forbidden there" has no support beyond the field's name, and 15 of its 23 items cannot be used at all. A wrong refusal
/// costs the player an error with no cue from the client, and not refusing breaks nothing.
/// </para>
/// </summary>
internal static class ItemMapLimit
{
    private const long ForbidsListed = 1, AllowsOnlyListed = 2;

    /// <summary>The scene's row by Lua_CFG_MapConfigBase.GetEntryPtr, or null.</summary>
    private static JsonElement? SceneRow(GameData data, long scene) => scene switch
    {
        >= 1 and <= 99999 => data.Row("MapConfig", scene),
        >= 1000001 and <= 9999999 => data.Row("DungeonConfig", scene),
        _ => null,
    };

    public static bool Allows(GameData data, long scene, JsonElement item)
    {
        if (SceneRow(data, scene) is not { } row) return true;
        long mode = GameData.Int(row, "_iLimitType");
        if (mode is not (ForbidsListed or AllowsOnlyListed) || !row.TryGetProperty("_kLimitItem", out var limits)) return true;
        var rule = new ItemConfig(item);
        // An empty list comes out of the export as {} or "": no entries, as an empty table is in the client.
        bool listed = limits.ValueKind == JsonValueKind.Array && limits.EnumerateArray().Any(entry =>
            entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() >= 2 && (GameData.Number(entry[0]), GameData.Number(entry[1])) switch
            {
                (1, var type) => type == rule.Type,
                (2, var subType) => subType == rule.SubType,
                _ => false,
            });
        return mode == ForbidsListed ? !listed : listed;
    }
}
