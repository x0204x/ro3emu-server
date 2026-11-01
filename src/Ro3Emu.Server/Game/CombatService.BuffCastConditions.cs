using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private const int ConditionHaveBuffGroup = 1004, ConditionBuffStack = 2000, ConditionNotRiding = 2004;

    /// <summary>
    /// 1004雖叫GetBuffByConfigID，配對BuffMap實以_iBuffID分桶；2000才比較封包configId。
    /// Buff葉使用真正施放actor的狀態，不借用主角；來源ID不限自己。
    /// 2000原Lua遇首個同config即回傳，多來源判定相異而pairs順序未知時回null。
    /// </summary>
    internal bool? EvaluateBuffCastCondition(GameSession source, long[] values, IEnumerable<ActiveBuff>? actorBuffs)
    {
        if (values is [ConditionNotRiding, var mount] && mount >= 0)
            return CurrentBattleMount(source) != mount;
        if (actorBuffs == null) return null;
        if (values is [ConditionHaveBuffGroup, var group] && group > 0)
            return actorBuffs.Any(b => (b.IsPassive || b.ExpireMs > Now)
                && BuffConfig.Get(_data, b.ConfigId) is { } config && config.BuffId == group);
        if (values is not [ConditionBuffStack, var configId, var upper, var lower]
            || configId <= 0 || lower < 0 || upper < lower) return null;
        bool? result = null;
        foreach (var buff in actorBuffs.Where(b => b.ConfigId == configId && (b.IsPassive || b.ExpireMs > Now)))
        {
            bool matches = buff.PileCount >= lower && buff.PileCount <= upper;
            if (result is { } previous && previous != matches) return null;
            result = matches;
        }
        return result ?? false;
    }
}
