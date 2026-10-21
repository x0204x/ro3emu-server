using System.Text.Json;
using System.Text.Json.Serialization;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Economy;

public static partial class EconomySnapshotReader
{
    // The persisted type itself validates nested members, with unmapped members rejected. The pinned reader
    // inventory above still requires an explicit entry for every role column, so a new blob cannot pass by default.
    private static readonly Dictionary<string, string> JsonRoleTypes = new(StringComparer.Ordinal)
    {
        ["task_runtime"] = "TaskRuntimeState", ["shop"] = "ShopService+ShopState",
        ["guild_challenge"] = "GuildChallengeService+PersonalState", ["daily"] = "DailyActivityService+DailyState",
        ["activity_runtime"] = "ActivityRuntimeState", ["timed_activity"] = "PronteraDefenseService+State",
        ["timed_battle"] = "TimedBattleService+State", ["lottery"] = "LotteryService+LotterySave",
        ["wild_afk"] = "WildAfkService+State", ["dungeons"] = "DungeonService+State",
        ["territory_wars"] = "TerritoryWarsService+RoleState",
        ["territory_escort"] = "TerritoryEscortService+RoleState",
        ["life_runtime"] = "LifeJobService+RuntimeState", ["season_page_meta"] = "SeasonPageService+PageMeta",
        ["equip_levels"] = "EquipLevels", ["world_state"] = "WorldRoleState",
        ["emblems"] = "EmblemService+EmblemState", ["mounts"] = "MountService+SavedState",
        ["achievement_runtime"] = "AchievementService+RuntimeState"
    };
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow
    };
    // StrictJson with unknown members skipped: a value strict reading refuses but this one reads has only unknown
    // members wrong (an unknown schema). A value with an unknown member and another fault is corruption: the fault
    // is real whatever the schema. Declared after StrictJson in the same file, so it is initialized after it.
    private static readonly JsonSerializerOptions UnmappedJson = new(StrictJson)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    private static bool OnlyUnmappedMembers(JsonElement root, Type type)
    {
        try { return root.Deserialize(type, UnmappedJson) != null; }
        catch (JsonException) { return false; }
    }

    private sealed partial class State
    {
        private void ValidateRoleJson(string name, JsonElement root, string key)
        {
            if (name == "pet_books")
            {
                _ = root.Deserialize<Dictionary<int, Dictionary<int, long>>>(StrictJson)
                    ?? throw new JsonException("Null pet book ledger.");
                return;
            }
            if (!JsonRoleTypes.TryGetValue(name, out string? typeName))
            { Issue(EconomyIssueKind.UnknownSchema, key, "No strict role JSON profile."); return; }
            Type? type = typeof(ShopService).Assembly.GetType("Ro3Emu.Server.Game." + typeName);
            if (type == null) { Issue(EconomyIssueKind.UnknownSchema, key, "Persisted JSON type unavailable: " + typeName); return; }
            try { _ = root.Deserialize(type, StrictJson) ?? throw new JsonException("Null persisted JSON value."); }
            catch (JsonException e) when (OnlyUnmappedMembers(root, type))
            { throw new UnknownPersistedFieldException(e.Message); }
            catch (NotSupportedException e) { throw new UnknownPersistedFieldException(e.Message); }
            if (name == "mounts" && Try(root, "Info", out var info) && info.ValueKind == JsonValueKind.String)
                ValidateProto(info.GetBytesFromBase64(), DBMountsInfo.Descriptor);
            if (name == "lottery" && Try(root, "Data", out var data) && data.ValueKind == JsonValueKind.String)
                ValidateProto(data.GetBytesFromBase64(), DBLotteryData.Descriptor);
            if (name == "daily" && Try(root, "Rows", out var rows) && rows.ValueKind == JsonValueKind.String)
                ValidateProto(rows.GetBytesFromBase64(), DBDailyActivityInfo.Descriptor);
        }
    }
}
