using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>An actual terminal grant, never a preview entry.</summary>
public sealed record AwardGrant(int Entry, int Count, int BindType = 0, long ExpireTimeStamp = 0);

/// <summary>A resolved and validated AwardConfig grant. Source services own eligibility and once-only claims.</summary>
public sealed class AwardPlan(long groupId, long awardId, IEnumerable<AwardGrant> grants)
{
    public long GroupId { get; } = groupId;
    public long AwardId { get; } = awardId;
    public IReadOnlyList<AwardGrant> Grants { get; } = new ReadOnlyCollection<AwardGrant>(grants.ToArray());
}

public sealed class AwardResolutionException(string message) : InvalidOperationException(message);

/// <summary>
/// Resolves MultiverseGroupConfig type 0 to AwardConfig, including fixed tuples and actual DropPool terminals.
/// Pool slot chance/roll/pick and candidate weight/count semantics follow the current RewardService (inferred);
/// unsupported server policy fails the source grant instead of silently omitting an obligation.
/// </summary>
public sealed class AwardService(GameData data, IOptions<ServerOptions>? options = null)
{
    private const int BaseExpItem = 100;
    private const int JobExpItem = 101;
    private const int ZenyItem = 1;
    // Small resolver-only tests do not need to build the full options container.
    private readonly RewardRateOptions rates = options?.Value.RewardRates ?? new();

    public AwardPlan Resolve(GameSession session, long multiverseGroupId, int? playBookId = null)
        => ResolveGroup(session?.Progress, multiverseGroupId, playBookId);

    /// <summary>Resolve a persisted role's award when the recipient is offline (guild caravan help/compensation).</summary>
    public AwardPlan ResolveForProgress(RoleProgress progress, long multiverseGroupId, int? playBookId = null)
        => ResolveGroup(progress, multiverseGroupId, playBookId);

    /// <summary>
    /// An item's _iAward (box use). Rows with _kServerOpenDay pick the first inclusive range holding the server's open
    /// day, past the last range the latest started one (inferred); a [2, minLevel, maxLevel] _kCondition is the level
    /// range UI_Com_RewardSelectWidgetCtrl.BuildOptionList checks, unmet it throws <see cref="AwardConditionException"/>.
    /// </summary>
    public AwardPlan ResolveItemAward(GameSession session, long multiverseGroupId, int? playBookId, int serverOpenDay)
        => ResolveGroup(session?.Progress, multiverseGroupId, playBookId, serverOpenDay);

    private AwardPlan ResolveGroup(RoleProgress? progress, long multiverseGroupId, int? playBookId, int? serverOpenDay = null)
    {
        if (multiverseGroupId == 0) return new AwardPlan(0, 0, []);
        var group = data.Row("MultiverseGroupConfig", multiverseGroupId)
            ?? throw Fail($"missing MultiverseGroupConfig {multiverseGroupId}");
        if (GameData.Int(group, "_itype") != 0) throw Fail($"group {multiverseGroupId} is not an AwardConfig route");

        // Lua_CFG_MultiverseGroupConfig.GetSeasonValueID: nonzero book value wins, zero falls back to default.
        long awardId = playBookId is > 0
            ? GameData.Int(group, $"_iValue_{playBookId}") is var selected and > 0 ? selected : GameData.Int(group, "_iValue_default")
            : GameData.Int(group, "_iValue_default");
        if (awardId <= 0) throw Fail($"group {multiverseGroupId} has no configured award value");
        return ResolveAward(progress, multiverseGroupId, awardId, serverOpenDay);
    }

    /// <summary>Resolve a direct AwardConfig ID, used only where BattlePassRewardConfig 9201 names awards directly.</summary>
    public AwardPlan ResolveDirect(GameSession session, long awardId) => ResolveAward(session?.Progress, 0, awardId);

    private AwardPlan ResolveAward(RoleProgress? progress, long multiverseGroupId, long awardId, int? serverOpenDay = null)
    {
        if (data.Row("AwardConfig", awardId) is not { ValueKind: JsonValueKind.Array } rows || rows.GetArrayLength() == 0)
            throw Fail($"missing AwardConfig {awardId}");
        if (rows.GetArrayLength() != 1 && serverOpenDay == null)
            throw Fail($"AwardConfig {awardId} has multiple open-day rows; authoritative selector unresolved");
        var row = rows.GetArrayLength() == 1 ? rows[0] : OpenDayRow(rows, serverOpenDay!.Value, awardId);
        if (serverOpenDay != null && HasValues(row, "_kCondition")) CheckLevelCondition(row, progress, awardId);
        if (HasValues(row, "_kEXP") || serverOpenDay == null && HasValues(row, "_kCondition"))
            throw Fail($"AwardConfig {awardId} has unresolved EXP or condition policy");
        if (data.Table("AwardRestricConfig").Values.Any(r => GameData.Int(r, "_iRewardID") == awardId))
            throw Fail($"AwardConfig {awardId} has unresolved restriction/replacement policy");

        var grants = new List<AwardGrant>();
        if (row.TryGetProperty("_kAward", out var fixedItems))
        {
            if (fixedItems.ValueKind == JsonValueKind.Object && !fixedItems.EnumerateObject().Any()) { /* empty export */ }
            else if (fixedItems.ValueKind != JsonValueKind.Array) throw Fail($"AwardConfig {awardId} has malformed _kAward");
            if (fixedItems.ValueKind == JsonValueKind.Array) foreach (var tuple in fixedItems.EnumerateArray())
            {
                if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() is < 3 or > 4)
                    throw Fail($"AwardConfig {awardId} has malformed fixed tuple");
                int entry = ExactInt(tuple[0], "item"), count = ExactInt(tuple[1], "count"), bind = ExactInt(tuple[2], "bind");
                long expiry = tuple.GetArrayLength() == 4 ? ExactLong(tuple[3], "expiry") : 0;
                ValidateGrant(entry, count, bind, expiry);
                grants.Add(new AwardGrant(entry, count, bind, expiry));
            }
        }
        long poolId = GameData.Int(row, "_iDropPool");
        if (poolId < 0) throw Fail($"AwardConfig {awardId} has invalid pool id");
        if (poolId > 0) RollPool(poolId, progress, grants);
        return new AwardPlan(multiverseGroupId, awardId, ScaleRewardGrants(grants));
    }

    /// <summary>
    /// Applies only the server reward families represented by AwardConfig. Costs and ordinary items remain exact;
    /// monster item rates are handled by RewardService because an AwardConfig is not necessarily a monster drop.
    /// </summary>
    internal IReadOnlyList<AwardGrant> ScaleRewardGrants(IEnumerable<AwardGrant> grants)
    {
        var scaled = new List<AwardGrant>();
        foreach (var grant in grants)
        {
            int count = rates.ScaleConfiguredAward(grant.Entry, grant.Count);
            if (count > 0) scaled.Add(grant with { Count = count });
        }
        return scaled;
    }

    internal long ScaleQuestBaseExperience(long amount) => rates.ScaleBaseExperience(amount);

    private void RollPool(long poolId, RoleProgress? progress, List<AwardGrant> grants)
    {
        var pool = data.Row("DropPoolConfig", poolId) ?? throw Fail($"missing DropPoolConfig {poolId}");
        for (int slot = 1; slot <= 16; slot++)
        {
            if (!pool.TryGetProperty($"_iDropGroup_{slot}", out var tuple) || tuple.ValueKind == JsonValueKind.Object && !tuple.EnumerateObject().Any()) continue;
            if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() != 4)
                throw Fail($"DropPoolConfig {poolId} slot {slot} has unresolved tuple");
            long groupId = ExactLong(tuple[0], "drop group");
            int chance = ExactInt(tuple[1], "chance"), rolls = ExactInt(tuple[2], "rolls"), picks = ExactInt(tuple[3], "picks");
            if (groupId <= 0 || chance is < 0 or > 1_000_000 || rolls is < 1 or > 1000 || picks is < 1 or > 1000)
                throw Fail($"DropPoolConfig {poolId} slot {slot} has unsupported range");
            var candidates = EligibleRows(groupId, progress);
            long weight = candidates.Sum(DropWeight);
            if (weight <= 0) continue;
            // (inferred) RewardService's current [group,chance-per-million,rolls,picks] interpretation.
            for (int r = 0; r < rolls; r++)
            {
                if (Random.Shared.NextInt64(1_000_000) >= chance) continue;
                for (int p = 0; p < picks; p++)
                {
                    long choice = Random.Shared.NextInt64(weight);
                    foreach (var candidate in candidates)
                    {
                        choice -= DropWeight(candidate);
                        if (choice >= 0) continue;
                        int min = checked((int)DropQuantity(candidate, "_iMinNumber"));
                        int max = checked((int)DropQuantity(candidate, "_iMaxNumber"));
                        if (min < 1 || max < min) throw Fail($"DropGroupConfig {groupId} has invalid quantity range");
                        int count = checked((int)Random.Shared.NextInt64(min, (long)max + 1));
                        int entry = DropEntry(candidate, groupId);
                        int bind = PoolBindType(candidate, groupId);
                        ValidateGrant(entry, count, bind, 0);
                        grants.Add(new AwardGrant(entry, count, bind));
                        break;
                    }
                }
            }
        }
    }

    private List<JsonElement> EligibleRows(long groupId, RoleProgress? progress)
    {
        if (data.Row("DropGroupConfig", groupId) is not { } raw || !GameData.TryGroupedRows(raw, out var rows))
            throw Fail($"missing DropGroupConfig {groupId}");
        var eligible = new List<JsonElement>();
        foreach (var row in rows)
        {
            long weight = DropWeight(row);
            if (weight <= 0) continue;
            // A declared but empty/zero-weight pool still resolves fixed grants without a recipient.
            // Only an eligible weighted candidate needs role level and career.
            if (progress == null) throw Fail($"DropGroupConfig {groupId} requires a recipient");
            long min = GameData.Int(row, "_iMinLevelLimit"), max = GameData.Int(row, "_iMaxLevelLimit");
            if (progress.Level < min || max > 0 && progress.Level > max) continue;
            if (row.TryGetProperty("_iJobLimit", out var jobs) && jobs.ValueKind == JsonValueKind.Array && jobs.GetArrayLength() > 0
                && !jobs.EnumerateArray().Any(j => GameData.Number(j) == progress.Career)) continue;
            if (row.TryGetProperty("_iJobLimit", out jobs) && jobs.ValueKind == JsonValueKind.Object && jobs.EnumerateObject().Any())
                throw Fail($"DropGroupConfig {groupId} has unresolved job filter");
            // DropAttenuationConfig scales by the player/monster level gap; an award has no slain monster, so a row
            // with _iAttenuationPlanID drops in full (DropAttenuation, inferred).
            if (GameData.Int(row, "_iQuestLimit") != 0 || GameData.Int(row, "_iActivityid") != 0
                || GameData.Int(row, "_kCoefficient") != 0
                || GameData.Int(row, "_iBind") != 0 || HasValues(row, "_kServerLevel"))
                throw Fail($"DropGroupConfig {groupId} has unresolved eligibility or bind policy");
            int bind = PoolBindType(row, groupId);
            int entry = DropEntry(row, groupId);
            int low = checked((int)DropQuantity(row, "_iMinNumber"));
            int high = checked((int)DropQuantity(row, "_iMaxNumber"));
            if (low <= 0 || high < low) throw Fail($"DropGroupConfig {groupId} has invalid quantity range");
            ValidateGrant(entry, low, bind, 0);
            eligible.Add(row);
        }
        return eligible;
    }

    // D Asia omits Lua defaults from repeated DropGroupConfig rows. Genesis serializes these fields explicitly.
    // Preserve an explicitly serialized zero; only an absent field receives the client-table default.
    private static long DropWeight(JsonElement row) =>
        row.TryGetProperty("_iWeights", out _) ? GameData.Int(row, "_iWeights") : 1;

    private static long DropQuantity(JsonElement row, string field) =>
        row.TryGetProperty(field, out _) ? GameData.Int(row, field) : 1;

    private static int DropEntry(JsonElement row, long groupId)
    {
        var config = new DropGroupConfig(row);
        if (config.HasDropItem) return checked((int)config.DropItem);
        // D Asia omits the Lua default item from its level-banded EXP curves. Match RewardService while keeping
        // every other malformed group fail-closed through ValidateGrant's item-zero rejection.
        return groupId is >= 3000 and < 4000 && config.HasMinNumber && config.HasMaxNumber ? BaseExpItem : 0;
    }

    private static int PoolBindType(JsonElement row, long groupId)
    {
        if (!row.TryGetProperty("_kIsBindGive", out var value)
            || value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any()
            || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) return 0;
        // (inferred) The supplied [1] with _iBind=0 means a permanent bound terminal item.
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 1
            && value[0].ValueKind == JsonValueKind.Number && value[0].TryGetInt64(out long bind) && bind == 1) return 1;
        throw Fail($"DropGroupConfig {groupId} has unresolved _kIsBindGive policy");
    }

    private void ValidateGrant(int entry, int count, int bind, long expiry)
    {
        if (entry <= 0 || count <= 0 || data.Row("ItemConfig", entry) == null) throw Fail($"unknown or invalid award item {entry} x{count}");
        if (bind is < 0 or > 2 || expiry < 0 || bind == 2 && expiry <= 0)
            throw Fail($"unsupported binding/expiry for item {entry}");
        if (entry is 100 or 101 && (bind != 0 || expiry != 0)) throw Fail("virtual EXP cannot be bound or expire");
        var item = data.Row("ItemConfig", entry)!.Value;
        if (GameData.Int(item, "_iType") == 1 && (expiry != 0 || bind == 2
            || bind == 1 && (!item.TryGetProperty("_iTrade", out var trade) || GameData.Number(trade) != 0)))
            throw Fail($"currency {entry} has unsupported binding, expiry, or trade policy");
    }

    private static bool HasValues(JsonElement row, string key) => row.TryGetProperty(key, out var value) && (value.ValueKind switch
    {
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        JsonValueKind.Number => GameData.Number(value) != 0,
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        _ => true,
    });

    private static int ExactInt(JsonElement value, string field) => checked((int)ExactLong(value, field));
    private static long ExactLong(JsonElement value, string field) => GameData.Number(value) is { } n && n >= 0 && n <= long.MaxValue && Math.Truncate(n) == n
        ? (long)n : throw Fail($"invalid {field} in award tuple");
    private static AwardResolutionException Fail(string reason) => new(reason);

    private static JsonElement OpenDayRow(JsonElement rows, int openDay, long awardId)
    {
        (long From, long To) Range(JsonElement row) => row.TryGetProperty("_kServerOpenDay", out var days)
            && days.ValueKind == JsonValueKind.Array && days.GetArrayLength() >= 2
            ? ((long)(GameData.Number(days[0]) ?? 1), (long)(GameData.Number(days[1]) ?? long.MaxValue))
            : throw Fail($"AwardConfig {awardId} has a row without an open-day range");
        var all = rows.EnumerateArray().ToArray();
        foreach (var row in all)
            if (Range(row) is var (from, to) && openDay >= from && openDay <= to) return row;
        return all.Where(r => Range(r).From <= openDay).OrderBy(r => Range(r).From).LastOrDefault() is { ValueKind: JsonValueKind.Object } latest
            ? latest : throw Fail($"AwardConfig {awardId} has no row for open day {openDay}");
    }

    private static void CheckLevelCondition(JsonElement row, RoleProgress? progress, long awardId)
    {
        var conditions = row.GetProperty("_kCondition");
        if (conditions.ValueKind != JsonValueKind.Array || progress == null) throw Fail($"AwardConfig {awardId} has unresolved condition policy");
        foreach (var c in conditions.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() != 3 || GameData.Number(c[0]) != 2)
                throw Fail($"AwardConfig {awardId} has unresolved condition policy");
            if (progress.Level < (GameData.Number(c[1]) ?? 0) || progress.Level > (GameData.Number(c[2]) ?? int.MaxValue))
                throw new AwardConditionException($"AwardConfig {awardId} needs level {c[1]}..{c[2]}");
        }
    }
}

/// <summary>The recipient does not meet an AwardConfig _kCondition the source may refuse on.</summary>
public sealed class AwardConditionException(string message) : InvalidOperationException(message);
