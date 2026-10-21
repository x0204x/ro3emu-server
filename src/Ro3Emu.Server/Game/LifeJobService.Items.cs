using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class LifeJobService
{
    /// <summary>
    /// ItemConfig actions 607 EatMagicFood_Sever and 315 RecoverTimeLimitValue; LC_LifeJobHelper:3180..3268 and
    /// LifeMagicFoodAttrConfig provide per-item counts, limits and permanent season attributes, not timed BuffConfig ids.
    /// The caller validates inventory/death/cooldown and holds StateLock. Consumption and count/balance share a transaction.
    /// </summary>
    private async Task<int?> UseLifeItemAsync(GameSession s, InvItem item, JsonElement itemRow, int count)
    {
        if (!itemRow.TryGetProperty("_iUseEffect", out var effects) || effects.ValueKind != JsonValueKind.Array
            || effects.GetArrayLength() != 1 || effects[0].ValueKind != JsonValueKind.Array) return null;
        var effect = effects[0];
        long Arg(int n) => effect.GetArrayLength() > n ? (long)(GameData.Number(effect[n]) ?? 0) : 0;
        bool food = Arg(0) == 607, vigor = Arg(0) == 315 && Arg(1) == VigorId;
        if (!food && !vigor) return null;
        if (s.Role == null || s.Progress == null || s.LifeJob == null || s.Restrictions == null)
            return ErrorCodes.FeatureUnavailable;
        if (!Current(itemRow) || s.Progress!.Level < GameData.Int(itemRow, "_iLevelNeed")) return ErrorCodes.LifeRequirements;
        if (!_functions.ConditionMet(s, GameData.Int(itemRow, "_iUseLimit"))) return ErrorCodes.LifeRequirements;
        var next = s.LifeJob.Clone(); var restrict = s.Restrictions.Clone(); var runtime = Copy(s.LifeRuntime);
        Reconcile(next, restrict, runtime);
        if (food)
        {
            if (Arg(1) != item.Entry || _data.Row("LifeMagicFoodAttrConfig", item.Entry) is not { } config || !Current(config)) return ErrorCodes.LifeConfig;
            long used = next.MagicFoodTimes.GetValueOrDefault(item.Entry), limit = GameData.Int(config, "_kLimit");
            if (count > limit - used) return ErrorCodes.AttemptsExhausted;
            next.MagicFoodTimes[item.Entry] = used + count;
        }
        else
        {
            var energy = restrict.RestrictCount[VigorId]; var uses = restrict.RestrictCount[VigorUsesId];
            long amount = checked(PotionAmount(s, Arg(2)) * count);
            if (amount <= 0) return ErrorCodes.LifeConfig;
            // Extra consumable energy uses VitalityExtraLimit, distinct from RestrictConfig's ordinary limitCount.
            if (amount > Param("VitalityExtraLimit", 5000) - energy.Count) return ErrorCodes.LifeVigorOverflow;
            if (uses.Count < count) return ErrorCodes.AttemptsExhausted;
            energy.Count += amount; uses.Count -= count; uses.HistoryUseCount += count;
        }
        if (await CommitAsync(s, food ? "cd13c/life-food" : "cd13c/life-vigor-item", next, restrict, runtime,
            [], [], new Dictionary<long, int> { [item.Guid] = count },
            result: new { Item = item.Entry, Count = count }) == null)
            return ErrorCodes.ItemsShort;
        if (food) await _character.RefreshStatsAsync(s);
        return 0;
    }

    /// <summary>Magic food adds _kAttrs once per accepted use (inferred aggregation into the ordinary stat pipeline).</summary>
    private IEnumerable<(long Id, double Value)> FoodAttributes(GameSession s)
    {
        if (s.LifeJob == null || s.LifeRuntime.FoodBook != _seasons.PlayBook) yield break;
        foreach (var (entry, count) in s.LifeJob.MagicFoodTimes)
            if (_data.Row("LifeMagicFoodAttrConfig", entry) is { } row && Current(row))
                foreach (var (id, value) in GameData.Pairs(row, "_kAttrs"))
                    yield return (id, value * Math.Clamp(count, 0, GameData.Int(row, "_kLimit")));
    }
}
