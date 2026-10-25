using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using static Ro3Emu.Server.Game.ChickenRules;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle Encounters (roguelike cards). The Magic Die 15000301 (interaction 2900010: ActionConfig 325
/// TriggerAdventureEvent [10001, 1] then 3 RemoveInteractionNpc, both server-hosted) offers cards of AdventureEventConfig
/// 10001's function 1003, deck pool 1003 (AdventureDeckConfig: _kRewardStagePersonal [3, 1] = 3 shown, 1 chosen;
/// _kRewardStageAuction [3 Pay, 10006, 1000, 2000, 3000, 4000] = refresh costs in Kaka Coin; _iLimit 10 refreshes;
/// _iAutoSelection 10 s). The offer reaches the client only through AUTO_SYNC 5039 DBAdventureInfo: a new needSelectMap
/// entry of functionInfos[curActiveFunctionId].deck[curActiveSourceId] opens UI_Sys_Chicken_AdventurePanel
/// (LC_DAL_Roguelike._onAdventureSelectNotify). 83421 picks, 83422 re-rolls; the client removes an offer from its
/// queue only on the 83421 reply, and re-reads a refresh from the 5039 that follows the 83422 reply. Inferred: the
/// die count and places (scene-editor region 899), that one die serves one player and goes away, the deck key (the
/// match), and the server's own pick of the first cards when the countdown ends.
/// </summary>
public sealed partial class TimedBattleService
{
    public const uint AdventureSelect = 83421, AdventureRefresh = 83422, AdventureSync = 5039;
    private const int MagicDie = 15000301, AdventureEvent = 10001;
    private const int RefreshPay = 3; // enum_RoguelikeRefreshType.Pay
    private const int CardGiveBuff = 203, CardGainItem = 326, CardUpgradeAll = 338; // ActionConfig
    /// <summary>Scene-editor region 899 "adventure '?' refresh area".</summary>
    private const float DieX = 426, DieZ = 415, DieRadius = 400;
    private const int DiePerPlayers = 10, MinDice = 2;

    private sealed class AdventureOffer
    {
        public required long OperateId { get; init; }
        public required int PoolId { get; init; }
        public required int ChooseCount { get; init; }
        public required long EndSec { get; init; }
        public int RefreshCount;
        public long[] Cards = [];
    }

    private async Task SpawnDiceAsync(Match match, ChickenRun run)
    {
        if (_data.Row("NPCConfig", MagicDie) is not { } row || _data.Row("AdventureEventConfig", AdventureEvent) == null) return;
        int count = Math.Max(MinDice, match.Players.Length / DiePerPlayers);
        for (int i = 0; i < count && !match.Settled; i++)
        {
            var (x, z) = RandomPoint(run, DieX, DieZ, DieRadius * 0.8f);
            if (WalkablePoint(x, z) is not { } at) continue;
            var unit = new ChickenNpcUnit
            {
                Uid = ChestUidBase | Interlocked.Increment(ref _chestSerial), SceneEntry = ChickenScene, InstanceId = match.Room!.Id,
                X = at.X, Y = at.Y, Z = at.Z, Face = (float)(run.Rng.NextDouble() * 360), NpcId = MagicDie, Name = NpcName(row),
            };
            if (await _instances.AddPropAsync(match.Room, unit)) run.Dice.Add(unit);
        }
    }

    private (int Pool, int Show, int Choose, int Limit, int AutoSec, long[] Costs)? Deck()
    {
        if (_data.Row("AdventureEventConfig", AdventureEvent) is not { } evt
            || _data.Table("AdventureDeckConfig").Values.FirstOrDefault(r => r.ValueKind == JsonValueKind.Object
                && new AdventureDeckConfig(r).PoolId == new AdventureEventConfig(evt).FunctionId) is not { ValueKind: JsonValueKind.Object } row) return null;
        var deck = new AdventureDeckConfig(row);
        long[] personal = deck.RewardStagePersonal, auction = deck.RewardStageAuction;
        return ((int)deck.PoolId, personal.Length > 0 ? (int)personal[0] : 3, personal.Length > 1 ? (int)personal[1] : 1,
            (int)deck.Limit, (int)deck.AutoSelection,
            auction.Length > 2 && auction[0] == RefreshPay ? auction[2..] : []);
    }


    /// <summary>`show` different cards of the pool, by AdventureCardConfig._iWeight.</summary>
    private long[] DrawCards(ChickenRun run, int pool, int show)
    {
        var cards = _data.Table("AdventureCardConfig").Values.Where(r => r.ValueKind == JsonValueKind.Object
            && new AdventureCardConfig(r).PoolId == pool).Select(r => new AdventureCardConfig(r))
            .Select(c => (Id: c.CardId, Weight: Math.Max(1, c.Weight))).ToList();
        var drawn = new List<long>();
        while (drawn.Count < show && cards.Count > 0)
        {
            long pick = run.Rng.NextInt64(cards.Sum(c => c.Weight));
            var card = cards.First(c => (pick -= c.Weight) < 0);
            cards.Remove(card);
            drawn.Add(card.Id);
        }
        return [.. drawn];
    }

    internal IReadOnlyList<(long Uid, float X, float Z)> DiceForTest(GameSession session) =>
        Chicken(session) is (_, var run, _) ? [.. run.Dice.Select(d => (d.Uid, d.X, d.Z))] : [];

    /// <summary>32700 on a Magic Die in reach: a new offer for this player, and the die is gone.</summary>
    private async Task<bool> TryDieInteractAsync(GameSession session, NpcInteractionReq req)
    {
        if (Chicken(session) is not (var match, var run, var me) || run.Dice.FirstOrDefault(d => d.Uid == req.NpcUid) is not { } die) return false;
        int result = ErrorCodes.Parameter;
        await RunMatchTransitionAsync(match, "chicken die", async () =>
        {
            if ((result = CanAct(run, me, session)) != 0) return;
            if (!run.Dice.Contains(die)) { result = ErrorCodes.NpcMissing; return; }
            if (MonsterService.Distance(session.Position.X, session.Position.Z, die.X, die.Z) > PickupReach) { result = ErrorCodes.NpcTooFar; return; }
            if (Deck() is not { } deck) { result = ErrorCodes.FeatureUnavailable; return; }
            long now = NowMs() / 1000;
            var offer = new AdventureOffer
            {
                OperateId = ++run.NextOperateId, PoolId = deck.Pool, ChooseCount = deck.Choose, EndSec = now + deck.AutoSec,
                Cards = DrawCards(run, deck.Pool, deck.Show),
            };
            if (offer.Cards.Length < offer.ChooseCount) { result = ErrorCodes.FeatureUnavailable; return; }
            me.Offers[offer.OperateId] = offer;
            me.EventTriggers++;
            run.Dice.Remove(die);
            result = 0;
            await session.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = 0, Req = req });
            await _instances.RemovePropAsync(match.Room!, die);
            await SyncAdventureAsync(match, session, me);
        });
        if (result != 0) await session.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = result, Req = req });
        return true;
    }

    /// <summary>83422: re-roll an offer's cards for the next refresh price (the client's GetCostOfEachRefresh).</summary>
    public async Task RefreshAdventureAsync(GameSession session, AdventureRefreshReq req)
    {
        int result = ErrorCodes.Parameter;
        bool changed = false;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken adventure refresh", async () =>
            {
                if ((result = CanAct(run, me, session)) != 0) return;
                if (!me.Offers.TryGetValue(req.OperateId, out var offer) || Deck() is not { } deck) { result = ErrorCodes.Parameter; return; }
                if (offer.RefreshCount >= deck.Limit || deck.Costs.Length == 0) { result = ErrorCodes.PurchaseLimit; return; }
                long cost = deck.Costs[Math.Min(offer.RefreshCount, deck.Costs.Length - 1)];
                if (me.Pack.Values.Where(i => i.Entry == CurrencyItem).Sum(i => (long)i.Stack) < cost) { result = ErrorCodes.ItemsShort; return; }
                PayCoins(me, cost);
                offer.RefreshCount++;
                offer.Cards = DrawCards(run, offer.PoolId, offer.Cards.Length);
                result = 0;
                changed = true;
                await SyncRoundAsync(match, run, session);
            });
        await session.SendGameAsync(AdventureRefresh, new AdventureRefreshResp { Result = result, Req = req });
        if (changed && Chicken(session) is (var m, _, var p)) await RunMatchTransitionAsync(m, "chicken adventure sync", () => SyncAdventureAsync(m, session, p));
    }

    /// <summary>83421: the chosen cards of an offer take effect and join the deck.</summary>
    public async Task SelectAdventureAsync(GameSession session, AdventureEventSelectReq req)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken adventure select", async () =>
            {
                if (!me.Offers.TryGetValue(req.OperateId, out var offer) || req.CardId.Count != offer.ChooseCount
                    || req.CardId.Distinct().Count() != req.CardId.Count || req.CardId.Any(id => !offer.Cards.Contains(id))) return;
                if ((result = CanAct(run, me, session)) != 0) return;
                result = 0;
                await ChooseCardsAsync(match, run, session, me, offer, [.. req.CardId], reply: false);
            });
        await session.SendGameAsync(AdventureSelect, new AdventureEventSelectResp { Result = result, Req = req });
        if (result == 0 && Chicken(session) is (var m, _, var p)) await RunMatchTransitionAsync(m, "chicken adventure sync", () => SyncAdventureAsync(m, session, p));
    }

    private async Task ChooseCardsAsync(Match match, ChickenRun run, GameSession session, ChickenPlayer me, AdventureOffer offer,
        long[] chosen, bool reply)
    {
        me.Offers.Remove(offer.OperateId);
        foreach (long card in chosen)
        {
            me.Cards.Add(card);
            if (_data.Table("AdventureCardConfig").Values.FirstOrDefault(r => r.ValueKind == JsonValueKind.Object
                && new AdventureCardConfig(r).CardId == card) is not { ValueKind: JsonValueKind.Object } row) continue;
            foreach (long[] args in new AdventureCardConfig(row).Effect)
            {
                if (args.Length == 0) continue;
                switch (args[0])
                {
                    case CardGiveBuff when args.Length > 1:
                        await _buffs.AddAsync(session, (int)args[1], 0, session.Role!.Id);
                        break;
                    case CardGainItem when args.Length > 2 && ItemRow((int)args[1]) != null:
                        AddToPack(run, me, (int)args[1], (int)Math.Min(args[2], int.MaxValue));
                        break;
                    case CardUpgradeAll:
                        UpgradeWornGear(run, me);
                        break;
                }
            }
        }
        await SyncRoundAsync(match, run, session);
        if (reply)
        {
            var req = new AdventureEventSelectReq { OperateId = offer.OperateId, CardId = { chosen } };
            await session.SendGameAsync(AdventureSelect, new AdventureEventSelectResp { Result = 0, Req = req });
            await SyncAdventureAsync(match, session, me);
        }
    }

    /// <summary>338 UpgradeList (card 3001014 "Raises the quality of all equipped gear by one tier"): each worn item
    /// becomes its EatChickenItemConfig._iUpgradeItem in the same bar slot.</summary>
    private void UpgradeWornGear(ChickenRun run, ChickenPlayer me)
    {
        foreach (var (slot, guid) in me.EquipBar.Where(worn => me.Pack.ContainsKey(worn.Value)).ToArray())
        {
            if (ItemRow(me.Pack[guid].Entry) is not { } row) continue;
            int next = (int)new EatChickenItemConfig(row).UpgradeItem;
            if (next <= 0 || ItemRow(next) == null) continue;
            RemoveFromPack(me, guid, 1);
            me.EquipBar[slot] = AddToPack(run, me, next, 1)[^1].Guid;
        }
    }

    private void PayCoins(ChickenPlayer me, long amount)
    {
        foreach (var coins in me.Pack.Values.Where(i => i.Entry == CurrencyItem).OrderBy(i => i.Stack).ToArray())
        {
            int take = (int)Math.Min(amount, coins.Stack);
            RemoveFromPack(me, coins.Guid, take);
            if ((amount -= take) == 0) return;
        }
    }

    /// <summary>Per second: an offer whose countdown ran out takes its first cards (the client's own fallback pick,
    /// UI_Sys_Chicken_AdventurePanelCtrl._checkCountAandAutoSelect), with an unasked 83421 reply so the queue clears.</summary>
    private async Task TickAdventureAsync(Match match, ChickenRun run, long now)
    {
        long sec = now / 1000;
        foreach (var (player, me) in run.Players.ToArray())
            foreach (var offer in me.Offers.Values.Where(o => sec >= o.EndSec).ToArray())
            {
                if (Chicken(player) == null || player.Dead || me.State != RoundRoleStatus.InWar) { me.Offers.Remove(offer.OperateId); continue; }
                await ChooseCardsAsync(match, run, player, me, offer, offer.Cards[..offer.ChooseCount], reply: true);
            }
    }

    /// <summary>
    /// 5039 of this player's encounters: one deck of function 1003 under the match's key, its chosen cards keyed from 0,
    /// the open offers (cards keyed 0..n-1: the panel loops `for i = 0, #cards`; endTime in seconds), and the event's
    /// trigger count. AutoSync sends the change since the last send, the whole object the first time.
    /// </summary>
    private Task SyncAdventureAsync(Match match, GameSession session, ChickenPlayer me)
    {
        if (session.Role == null || !InRoom(match).Contains(session) || Deck() is not { } deck) return Task.CompletedTask;
        var info = new DBAdventureInfo { CurActiveFunctionId = deck.Pool, CurActiveSourceId = match.Room!.Id };
        var cardDeck = new AdventureCardDeck { FunctionId = deck.Pool, CreateTime = match.Open };
        for (int i = 0; i < me.Cards.Count; i++) cardDeck.Cards[i] = new AdventureCardInfo { CardId = me.Cards[i] };
        foreach (var offer in me.Offers.Values)
        {
            var select = new AdventureEventSelectInfo
            {
                OperateId = offer.OperateId, PoolId = offer.PoolId, RefreshCount = offer.RefreshCount,
                ChooseCount = offer.ChooseCount, EndTime = offer.EndSec,
            };
            for (int i = 0; i < offer.Cards.Length; i++) select.Cards[i] = offer.Cards[i];
            cardDeck.NeedSelectMap[offer.OperateId] = select;
        }
        if (me.EventTriggers > 0)
            cardDeck.ProgressInfo[AdventureEvent] = new AdventureProgress { AdventureId = AdventureEvent, TriggerTimes = me.EventTriggers };
        var function = new AdventureFunctionInfo();
        function.Deck[match.Room.Id] = cardDeck;
        info.FunctionInfos[deck.Pool] = function;
        return CharacterService.SyncAsync(session, AdventureSync, info);
    }

    /// <summary>Leaving the match: the deck goes away (the client clears its own queue on leaving too).</summary>
    private static Task ClearAdventureAsync(GameSession session) =>
        session.Synced.ContainsKey(AdventureSync) ? CharacterService.SyncAsync(session, AdventureSync, new DBAdventureInfo()) : Task.CompletedTask;
}
