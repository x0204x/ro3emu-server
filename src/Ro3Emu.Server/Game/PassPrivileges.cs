using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The advanced battle pass privileges a role holds: BattlePassPrivilegeConfig rows named by the
/// PlayerActivityBattlePass.activated_privilege_ids of each premium pass whose activity is open now (the pass buy panel
/// lists them, texts 11490000000-02). The client applies none of them itself; the server does:
/// <list type="bullet">
/// <item>1 StallSlotAdd [n]: "Number of stalls increased by n": n more stall slots (StallsService.MaxGrid; the client
/// shows the server's maxgrid while the stall is open);</item>
/// <item>2 TradeFavAdd [n]: "Trade Exchange Collect limit increased by n": n more Trade Exchange favourites
/// (TradeService.Attention, PublicParam TradeConcernNum);</item>
/// <item>3 CopperEffAdd [currency, percent]: "${1} acquisition efficiency increased by ${2}%": that currency from
/// monster drops raised by the percent, rounded down (RewardService; which gains it covers is inferred).</item>
/// </list>
/// Privileges of passes that share an id (Pass I and II both list 1-3) count once.
/// </summary>
internal static class PassPrivileges
{
    internal const long StallSlotAdd = (long)EBattlePassPrivilegeType.StallSlotAdd;
    internal const long TradeFavAdd = (long)EBattlePassPrivilegeType.TradeFavAdd;
    internal const long CopperEffAdd = (long)EBattlePassPrivilegeType.CopperEffAdd;

    /// <summary>The parameters of each active privilege of <paramref name="type"/>.</summary>
    internal static IEnumerable<long[]> Of(GameData data, GameSession session, long type)
    {
        if (session.NewActivities is not { } state) return [];
        var ids = new HashSet<long>();
        foreach (var pass in BattlePassConfig.All(data))
            if (state.ActivityInfo.TryGetValue(pass.Id, out var info) && info.IsEnterComplete && !info.IsExitComplete
                && info.ActivityParam?.BattlePass is { Premium: true } progress)
                ids.UnionWith(progress.ActivatedPrivilegeIds.Select(id => (long)id));
        return [.. ids.Order().Select(id => BattlePassPrivilegeConfig.Get(data, id))
            .Where(row => row is { } r && r.PrivilegeType == type).Select(row => row!.Value.Params)];
    }

    /// <summary>The sum of the first parameter of the active privileges of <paramref name="type"/> (a slot count).</summary>
    internal static int Added(GameData data, GameSession session, long type) =>
        (int)Math.Clamp(Of(data, session, type).Sum(p => p.Length > 0 ? p[0] : 0), 0, 1000);

    /// <summary>Monster drops with CopperEffAdd applied: each [currency, percent] raises that currency's counts.</summary>
    internal static List<(int Entry, int Count)> BoostDrops(GameData data, GameSession session, List<(int Entry, int Count)> drops)
    {
        long[][] boosts = [.. Of(data, session, CopperEffAdd).Where(p => p is [> 0, > 0, ..])];
        if (boosts.Length == 0) return drops;
        return [.. drops.Select(d => (d.Entry, (int)Math.Min(int.MaxValue,
            d.Count + (long)d.Count * boosts.Where(p => p[0] == d.Entry).Sum(p => p[1]) / 100)))];
    }
}
