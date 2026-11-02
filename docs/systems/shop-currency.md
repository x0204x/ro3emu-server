# Currency Redemption and paid products

## Currency Redemption (2005 ITEM_EXCHANGE)

The shop's Currency Redemption window (functions 7005/7006 under shop 70, `UI_Sys_CoinExchangeWidgetCtrl`) turns one
currency into another at the rate of the client's `ItemExchangeConfig`. The widget opens on the currency it buys,
picks the currency it pays (Zeny is bought with Sacred Relic Crystal, Minted Coin with Zeny), reads the row keyed by
the paid item, shows `1:rate`, shows `count x rate` as what the player gets and caps the count at
`min(owned, single limit)`. It sends `ItemExchangeReq {itemId = paid item, count}`.

`ShopService.ExchangeCurrencyAsync` checks the row, its function, the count and the single limit, then pays `count`
of the paid item and grants `count x rate` of the target in one bag transaction. A request above the single limit is
refused with 209 "Single-exchange limit exceeded" instead of being trimmed (the client never sends one). The widget
shows "Redeemed" for result 0 and "Exchange failed" for anything else; balances arrive with the usual currency sync.

## Paid products without payment (2001-2004)

The client buys through `Lua_SDK_PayMgr.ReqPurchaseRequest`: 2001 `CreateOrderReq {chargeType, chargeId}`, with charge
type 1 a `RechargeConfig` top-up (`UI_Sys_TopUpListWidgetCtrl`) and 2 a `ProductConfig` product: the pass
(`LC_BattlePassHelper.RequestBuyBattlePass`, `enum_ProductId.BattlePass` 7, the gift token 8) and the month cards
(`LC_MonthCardHelper.Purchase`, products 4-6). On the PC build `_onCreateOrderResult` hides the "Awaiting payment..."
loader, shows `ErrMsg` for a nonzero result and otherwise opens `orderData.data.transaction_url` if there is one.

By owner decision (2026-10-31) a purchase costs nothing on this server: `Ro3Emu:Shop:FreeProducts` (on by default;
off answers every order "feature unavailable", as before). `NewActivityService.Purchases` then:

- answers 2001 with result 0, an order id and `orderData` `{}` (so nothing is opened), and grants the goods in the
  order's own transaction, straight into the bag (inferred: the original paid after the store's callback);
- for a top-up grants `_kAward`, plus `_kFirstChargeAward` the first time the role buys that package or `_kAwardBonus`
  after (the panel shows "First Purchase Bonus" for the ids 2003 returns, else "Extra Bonus"), and sends 2004 with the
  package id when the first-purchase bonus was paid;
- for a product grants its `_iAward` (a MultiverseGroupConfig id, or for the gift pack an AwardConfig id), plus the
  month card's `_iBuyAward`; the pass and the month cards then take their entitlement items 703 / 701 / 702 exactly as
  for a gift (see [battle pass and month card](activities-battlepass-monthcard.md));
- answers 2003 with the PC packages whose first-purchase bonus is still open, and 2002 with 0 for an order it issued.

Only the PC rows are sold (`_iPlatform` 3, the panel's platform code without Android or iOS). The client data has no
purchase limit, so the caps are inferred, to keep a free shop from being an endless faucet:

| Charge | Cap |
|---|---|
| top-up package | `Ro3Emu:Shop:FreeTopUpsPerDay` (1) per game day (05:00); its first-purchase bonus once per role |
| gift pack (product type 1) | once per role |
| month card (type 2) | while the card's days stay below its `_iDayLimit` (`LC_MonthCardHelper.JustIsUpperLimit`), counting owned unconverted cards |
| pass (type 3) | while an open pass that takes the item is not premium and the item is not already owned |
| pass gift token | while none is owned, once per game day |

Purchases are counted per role in `activity_runtime` (`Purchases`, by `chargeType:chargeId`).

In the current client the shop's recharge button and the "+" on Sacred Relic Crystal open
`PublicParamConfig.RechargeExternalUrl` instead of the top-up panel; the client hooks send that link to the server's
purchase address or nowhere (`UrlRewrite`). The pass and month card buttons send 2001 themselves. The top-up panel opens
only while `RechargeExternalUrl` is empty.
