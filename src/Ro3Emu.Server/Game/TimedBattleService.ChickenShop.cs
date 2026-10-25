using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Server.Game.ChickenRules;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>A Survivor Battle NPC that is not a chest: the Kafra Shop and the Equipment Merchant.</summary>
internal sealed class ChickenNpcUnit : WorldUnit
{
    public required int NpcId { get; init; }
    public required string Name { get; init; }

    /// <summary>Shaped as the world's NPCs (SceneService.NpcEntity), so the client interacts with it (32700).</summary>
    public override EntityBase Entity() => new()
    {
        Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = 2, Name = Name, EntityCamp = 3, Face = Face,
        State = EntityState.Alive, NotifyType = EntityBase.Types.NotifyType.All,
        MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs, MoveType = MovementType.MoveTypeNone },
    };
}

/// <summary>
/// The Survivor Battle shops (ShopConfig _iShopType 8 = enum_ShopType.ChickenShop): the Kafra Shop 15000054 (shop 801:
/// items, equipment, skills) and the Equipment Merchant 15000060 (shop 802), whose interactions 2900003 / 2900011 open
/// UI_Sys_Chicken_ShopNpcPanel client side. The panel asks 82334 GetDungeonShopReq{npcGuid} (shopId stays 0) and lists
/// only the commodities of the answer (LC_DAL_Shop.OnDungeonShopResult merges them into DungeonShopResultData), then
/// buys with the ordinary 82332 BuyCommodityReq{commodityId, count, price, npcId} paid in the match's Kaka Coin 10006
/// (LC_ChickenHelper.GetChickenItemCount). Inferred, as no client data says it: where and how many shops stand (the
/// scene editor's refresh regions 877 / 866, a few per lobby), and the stock model: each ShopConfig row draws its
/// _kGroup [[group, picks]] commodities by _iWeight, every NPC sells _iGlobalPurchase of one, a player buys at most
/// _iPurchaseNum of it in the match (_iPurchaseTimesLimit 6, a family the ordinary shops do not have).
/// </summary>
public sealed partial class TimedBattleService
{
    public const uint DungeonShop = 82334;
    private const int ChickenShopType = 8;
    private const int KafraShop = 15000054, EquipmentMerchant = 15000060;
    /// <summary>Scene-editor regions 877 "shop and ambulance refresh area" and 866 "weapon merchant (large circle)".</summary>
    private static readonly (int Npc, float X, float Z, float Radius, int PerPlayers, int Min)[] ShopSpawns =
        [(KafraShop, 441, 424, 400, 10, 2), (EquipmentMerchant, 391, 421, 380, 20, 1)];

    private sealed class ChickenShop
    {
        public required ChickenNpcUnit Unit { get; init; }
        /// <summary>Drawn commodity id -> its ShopCommodityConfig row.</summary>
        public Dictionary<long, JsonElement> Goods { get; } = [];
        public Dictionary<long, long> Sold { get; } = [];
    }

    private async Task SpawnShopsAsync(Match match, ChickenRun run)
    {
        foreach (var (npc, cx, cz, radius, perPlayers, min) in ShopSpawns)
        {
            if (_data.Row("NPCConfig", npc) is not { } row) continue;
            int count = Math.Max(min, match.Players.Length / perPlayers);
            for (int i = 0; i < count && !match.Settled; i++)
            {
                var (x, z) = RandomPoint(run, cx, cz, radius * 0.8f);
                if (WalkablePoint(x, z) is not { } at) continue;
                var unit = new ChickenNpcUnit
                {
                    Uid = ChestUidBase | Interlocked.Increment(ref _chestSerial), SceneEntry = ChickenScene, InstanceId = match.Room!.Id,
                    X = at.X, Y = at.Y, Z = at.Z, Face = (float)(run.Rng.NextDouble() * 360), NpcId = npc, Name = NpcName(row),
                };
                var shop = new ChickenShop { Unit = unit };
                StockShop(run, shop);
                if (await _instances.AddPropAsync(match.Room, unit)) run.Shops.Add(shop);
            }
        }
    }

    /// <summary>Every ShopConfig row of this NPC's chicken shop draws its groups' picks (weighted, no repeats).</summary>
    private void StockShop(ChickenRun run, ChickenShop shop)
    {
        var commodities = _data.Table("ShopCommodityConfig").Values.Where(r => r.ValueKind == JsonValueKind.Object).ToArray();
        foreach (var config in _data.Table("ShopConfig").Values.Where(r => r.ValueKind == JsonValueKind.Object
            && new ShopConfig(r).ShopType == ChickenShopType && new ShopConfig(r).NpcId == shop.Unit.NpcId))
        {
            long classId = new ShopConfig(config).ClassId;
            foreach (var pair in new ShopConfig(config).Group.Where(p => p.Length >= 2))
            {
                var pool = commodities.Where(c => new ShopCommodityConfig(c).ClassId == classId && new ShopCommodityConfig(c).Group == pair[0]).ToList();
                for (int n = 0; n < pair[1] && pool.Count > 0; n++)
                {
                    long total = pool.Sum(c => Math.Max(1, new ShopCommodityConfig(c).Weight));
                    long pick = run.Rng.NextInt64(total);
                    var chosen = pool.First(c => (pick -= Math.Max(1, new ShopCommodityConfig(c).Weight)) < 0);
                    pool.Remove(chosen);
                    shop.Goods[new ShopCommodityConfig(chosen).Id] = chosen;
                }
            }
        }
    }

    /// <summary>A live match's shop NPC (or, with <paramref name="dice"/>, also a Magic Die).</summary>
    private bool IsMatchNpc(long uid, bool dice) => uid > 0 && _matches.Values.Any(m => m.Chicken is { } run
        && (run.Shops.Any(s => s.Unit.Uid == uid) || dice && run.Dice.Any(d => d.Uid == uid)));

    private static DungeonCommodityInfo GoodsInfo(ChickenShop shop, ChickenPlayer me, long id, JsonElement row) => new()
    {
        CommodityId = id,
        RemainCount = Math.Max(0, new ShopCommodityConfig(row).GlobalPurchase - shop.Sold.GetValueOrDefault(id)),
        CanBuyCount = Math.Max(0, new ShopCommodityConfig(row).PurchaseNum - me.Bought.GetValueOrDefault(id)),
    };

    private static ChickenShop? ShopInReach(ChickenRun run, GameSession session, long uid) =>
        run.Shops.FirstOrDefault(s => s.Unit.Uid == uid
            && MonsterService.Distance(session.Position.X, session.Position.Z, s.Unit.X, s.Unit.Z) <= PickupReach);

    /// <summary>82334: the stock of the shop NPC the panel opened at. An empty map would break the unguarded pairs(), so
    /// a refused request gets no answer.</summary>
    public async Task GetDungeonShopAsync(GameSession session, GetDungeonShopReq req)
    {
        GetDungeonShopResp? resp = null;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken shop", () =>
            {
                if (ShopInReach(run, session, req.NpcGuid) is { Goods.Count: > 0 } shop)
                {
                    resp = new GetDungeonShopResp();
                    foreach (var (id, row) in shop.Goods) resp.CommodityInfo[id] = GoodsInfo(shop, me, id, row);
                }
                return Task.CompletedTask;
            });
        if (resp != null) await session.SendGameAsync(DungeonShop, resp);
    }

    /// <summary>
    /// 82332 at a match shop (its npcId is a shop NPC of the player's match): paid from the match pack, not the bag,
    /// so ShopService never sees it. The price is the config's (the quoted one only has to match), the goods go into
    /// the pack like a pickup. The panel refreshes nothing after a buy, so the changed stock follows in 82334.
    /// Returns false for a purchase that is not a match shop's.
    /// </summary>
    public async Task<bool> TryBuyAsync(GameSession session, BuyCommodityReq req)
    {
        if (Chicken(session) is not (var match, var run, var me) || run.Shops.All(s => s.Unit.Uid != req.NpcId)) return false;
        int result = ErrorCodes.Parameter, price = 0;
        GetDungeonShopResp? stock = null;
        await RunMatchTransitionAsync(match, "chicken buy", async () =>
        {
            if ((result = CanAct(run, me, session)) != 0) return;
            if (ShopInReach(run, session, req.NpcId) is not { } shop) { result = ErrorCodes.NpcTooFar; return; }
            if (req.Count < 1 || !shop.Goods.TryGetValue(req.CommodityId, out var row)) { result = ErrorCodes.Parameter; return; }
            var info = GoodsInfo(shop, me, req.CommodityId, row);
            if (req.Count > info.RemainCount || req.Count > info.CanBuyCount) { result = ErrorCodes.PurchaseLimit; return; }
            // _kItemPerPrice is one flat [currency, price] pair.
            long[] cost = new ShopCommodityConfig(row).ItemPerPrice;
            if (cost.Length < 2) { result = ErrorCodes.ItemNoConfig; return; }
            price = (int)cost[1];
            var goods = new ShopCommodityConfig(row);
            int entry = (int)goods.ItemId, amount = req.Count * (int)Math.Max(1, goods.ItemPerMount);
            long total = cost[1] * req.Count;
            if (cost[0] != CurrencyItem || ItemRow(entry) == null) { result = ErrorCodes.ItemNoConfig; return; }
            if (me.Pack.Values.Where(i => i.Entry == CurrencyItem).Sum(i => (long)i.Stack) < total) { result = ErrorCodes.ItemsShort; return; }
            if (Fits(me, entry, amount) < amount) { result = ErrorCodes.BagFull; return; }
            long left = total;
            foreach (var coins in me.Pack.Values.Where(i => i.Entry == CurrencyItem).OrderBy(i => i.Stack).ToArray())
            {
                int take = (int)Math.Min(left, coins.Stack);
                RemoveFromPack(me, coins.Guid, take);
                if ((left -= take) == 0) break;
            }
            AddToPack(run, me, entry, amount);
            shop.Sold[req.CommodityId] = shop.Sold.GetValueOrDefault(req.CommodityId) + req.Count;
            me.Bought[req.CommodityId] = me.Bought.GetValueOrDefault(req.CommodityId) + req.Count;
            if (ItemType(entry) == EquipmentType)
            {
                int quality = (int)new EatChickenItemConfig(ItemRow(entry)!.Value).Quality;
                me.EquipQuality[quality] = me.EquipQuality.GetValueOrDefault(quality) + amount;
            }
            result = 0;
            await SyncRoundAsync(match, run, session);
            stock = new GetDungeonShopResp();
            stock.CommodityInfo[req.CommodityId] = GoodsInfo(shop, me, req.CommodityId, row);
        });
        await session.SendGameAsync(ShopService.BuyCommodity, new BuyCommodityResp
        {
            Result = result, CommodityId = req.CommodityId, Count = result == 0 ? req.Count : 0, Price = price,
            BuyTimes = result == 0 ? (int)me.Bought.GetValueOrDefault(req.CommodityId) : 0,
        });
        if (stock != null) await session.SendGameAsync(DungeonShop, stock);
        return true;
    }

    internal IReadOnlyList<(long Uid, int NpcId, float X, float Z, long[] Goods)> ShopsForTest(GameSession session) =>
        Chicken(session) is (_, var run, _) ? [.. run.Shops.Select(s => (s.Unit.Uid, s.Unit.NpcId, s.Unit.X, s.Unit.Z, s.Goods.Keys.ToArray()))] : [];

    /// <summary>32700 on a shop NPC: the panel is client-hosted (ActionConfig 1 OpenUI, _iEXECHost 0), so only acknowledge.</summary>
    private async Task<bool> TryShopInteractAsync(GameSession session, NpcInteractionReq req)
    {
        if (Chicken(session) is not (_, var run, _) || run.Shops.All(s => s.Unit.Uid != req.NpcUid)) return false;
        await session.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = 0, Req = req });
        return true;
    }
}
