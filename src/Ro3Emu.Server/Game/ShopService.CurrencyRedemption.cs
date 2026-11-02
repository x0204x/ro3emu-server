using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 2005 ITEM_EXCHANGE, the shop's Currency Redemption (functions 7005/7006 under shop 70). UI_Sys_CoinExchangeWidgetCtrl
/// opens on the currency it buys (target 1 Zeny is paid with 3 Sacred Relic Crystal, target 2 Minted Coin with 1 Zeny),
/// reads ItemExchangeConfig by the paid item, shows "1:rate" and count x rate as what the player gets, caps the count at
/// min(owned, single limit), and sends ItemExchangeReq {itemId = the paid item, count}. Any nonzero result is the generic
/// "Exchange failed" (23276), 0 is "Redeemed" (23275); the new balances arrive through the usual currency sync.
/// </summary>
public sealed partial class ShopService
{
    public const uint ItemExchange = 2005;

    /// <summary>ErrorCodeConfig 209 "Single-exchange limit exceeded".</summary>
    private const int SingleExchangeLimit = 209;

    public async Task ExchangeCurrencyAsync(GameSession session, ItemExchangeReq req)
    {
        int result = await RedeemAsync(session, req);
        await session.SendGameAsync(ItemExchange, new ItemExchangeResp { Result = result });
    }

    private async Task<int> RedeemAsync(GameSession session, ItemExchangeReq req)
    {
        if (session.Role == null || session.Progress == null) return ErrorCodes.FeatureUnavailable;
        if (ItemExchangeConfig.Get(_data, req.ItemId) is not { } row || row.Rate <= 0 || row.TargetId <= 0
            || _data.Row("ItemConfig", row.TargetId) == null || _data.Row("ItemConfig", req.ItemId) == null) return ErrorCodes.Parameter;
        if (row.Function > 0 && !_functions.IsOpen(session, row.Function)) return ErrorCodes.FunctionLocked;
        if (req.Count <= 0) return ErrorCodes.Parameter;
        // The widget never asks for more than the single limit; a larger request is refused, not trimmed.
        if (row.SingleLimit > 0 && req.Count > row.SingleLimit) return SingleExchangeLimit;
        if (req.Count > int.MaxValue / row.Rate) return SingleExchangeLimit;
        long gained = req.Count * row.Rate;
        (int Entry, long Count)[] cost = [((int)req.ItemId, req.Count)];
        (int Entry, int Count)[] goods = [((int)row.TargetId, (int)gained)];
        using var guard = await CharacterService.Lock(session);
        var economy = _character.FamilyEconomy(session, "cd13a/shop-currency-redemption", cost, goods,
            new { Paid = req.ItemId, req.Count, row.Rate, Goods = row.TargetId, Gained = gained, row.Function });
        bool paid = await _character.ExchangeLockedAsync(session, cost, goods, popup: false,
            persist: tx => tx.WithEconomy(economy), asyncCommit: true) != null;
        return paid ? 0 : _character.ShortageOf(session, cost);
    }
}
