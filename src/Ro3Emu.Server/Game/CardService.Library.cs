using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

public sealed partial class CardService
{
    /// <summary>
    /// 5057 LibraryCardInfo.cardStar / treasured from an owned instance (item attrs 502 star, 530 card type). The client
    /// lights a CardConfig tier row when the entry is unlocked and cardStar >= _iTier (LC_Library_Helper.GetCardRewardHas,
    /// lua:601-655), shows the highest lit row (GetAllCardMap, lua:1323-1377) and plays the star-up animation when
    /// GetCardTierBySeverID rises (UI_Sys_LibraryList_CardWidgetCtrl.CheckStarUpAnim, lua:299-340). GetCardRewardHas only
    /// accepts treasured 0 or 1, and the tip / red-dot paths (LC_System_Library.lua:101-140, LC_Library_Helper.CanTipPop)
    /// read 1 as enum_CardType.Flash. (inferred) cardStar is the highest star any owned copy reached and never drops (a
    /// return resets the copy, not the collection); treasured turns 1 once a Flash copy (530 = 1) is owned, a Gray copy
    /// (530 = 2) leaves it unchanged. A missing entry is created locked, as first sight does; QueueCollection collects it.
    /// </summary>
    private bool Observe(DBLibraryInfo library, InvItem card)
    {
        if (!_character.IsCard(card.Entry)) return false;
        bool changed = false;
        if (!library.Cards.TryGetValue(card.Entry, out var entry))
        {
            library.Cards[card.Entry] = entry = new LibraryCardInfo { CardId = card.Entry };
            changed = true;
        }
        int star = (int)(card.Attrs?.GetValueOrDefault(502) ?? 0);
        if (star > entry.CardStar)
        {
            entry.CardStar = star;
            changed = true;
        }
        if ((card.Attrs?.GetValueOrDefault(530) ?? 0) == 1 && entry.Treasured != 1)
        {
            entry.Treasured = 1;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// A card is collected by obtaining it: the client has no request for it. UI_Sys_LibraryList_Card_ListWidgetCtrl
    /// ._refreshSelectCardId hands a tapped card to UI_Sys_LibraryListPanelModel.SendCardActive, whose body is empty
    /// (lua:1054-1057; the bytecode is one return0 over source lines 483-488), and nothing calls
    /// LC_DAL_Library.SendLibraryCardActivate, the only sender of 82404. The help says "Obtaining a Card for the first time
    /// grants Collection Points" (Localization 10960000100), and the tip "Card [${1}] unlocked" (57095) pops on the 5057
    /// change that brings unlock with cardStar 0 (LC_Library_Helper.CanTipPop). (inferred) So every entry is collected as
    /// soon as it exists, and an entry only exists once a copy was owned, so a copy spent since still counts. A Flash copy
    /// also collects the Collector page (treasureUnlock, quality 4+ as GetCardExcelTreasuredByCardID, 57085). The points
    /// reach the client as a balance: the tip is the notice, and a backlog at login must not raise one popup per card.
    /// Cards with no _iShowLibrary row stay out: the window lists 202 cards, but _refreshCardActiveNumShow counts every
    /// unlocked entry it is sent, so one of them would push the counter past its total.
    /// </summary>
    private void QueueCollection(GameSession session)
    {
        if (session.Library?.Cards.Values.Any(Uncollected) == true)
            _loop.PostOwned(session, () => CollectOwnedAsync(session), "card collection");
    }

    private const int CollectorQuality = 4; // LC_Library_Helper.GetCardExcelTreasuredByCardID

    private bool Uncollected(LibraryCardInfo entry) => UncollectedNormal(entry) || UncollectedTreasure(entry);

    private bool UncollectedNormal(LibraryCardInfo entry) => !entry.Unlock && CollectionCards.Contains(entry.CardId);

    private bool UncollectedTreasure(LibraryCardInfo entry) => entry.Treasured == 1 && !entry.TreasureUnlock
        && CollectionCards.Contains(entry.CardId) && ItemConfig.Get(_data, entry.CardId) is { Quality: >= CollectorQuality };

    private HashSet<int> CollectionCards => _collectionCards ??=
        [.. CardConfig.All(_data).Where(row => row.ShowLibrary == 1).Select(row => (int)row.CardId)];

    [LoggerMessage(Level = LogLevel.Error, Message = "Card {Card} collection failed for {Role}")]
    private static partial void LogCollectionFailed(ILogger logger, Exception fault, int card, long role);

    private async Task CollectOwnedAsync(GameSession session)
    {
        using var owner = await session.Owners!.EnterAsync("card/collect", ReservationKey.Role(session.Role!.Id));
        var context = owner.Context;
        context.AssertRole(session.Role.Id);
        context.BindPresentation(session);
        var attachment = session.Attachment;
        bool collected = false;
        using (await CharacterService.Lock(session, context))
        {
            foreach (int card in session.Library?.Cards.Values.Where(Uncollected).Select(entry => entry.CardId).ToList() ?? [])
                try
                {
                    // Read after each commit: it replaces the library. A failed commit keeps the entry for the next card or login.
                    if (UncollectedNormal(session.Library!.Cards[card])) collected |= await CommitUnlockAsync(session, card, false, notice: false);
                    if (UncollectedTreasure(session.Library!.Cards[card])) collected |= await CommitUnlockAsync(session, card, true, notice: false);
                }
                catch (Exception fault) when (fault is not OperationCanceledException)
                {
                    LogCollectionFailed(_log, fault, card, session.Role.Id);
                }
        }
        if (collected) await PublishUnlockAsync(session, context, attachment);
    }

    /// <summary>
    /// PublicParamConfig.LibraryCardUnlockReward[ItemConfig._iQuality].PointValue[tier]: the Collection Points of one
    /// CardConfig tier row. The tier popup shows PointValue[tier] of the card point item, masked as owned by
    /// GetCardRewardHas (UI_Sys_Library_PopFilterPanelCtrl._refreshCardSelectShow, lua:570-660); the card widget shows
    /// tier "0" (UI_Sys_LibraryList_CardWidgetCtrl, lua:906-921). Every tier above 0 holds 0 in this client.
    /// </summary>
    private int TierPoints(int card, int tier)
    {
        if (_data.Row("ItemConfig", card) is not { } row
            || !_data.Table("PublicParamConfig").TryGetValue("LibraryCardUnlockReward", out var table)
            || table.ValueKind != JsonValueKind.Object) return 0;
        long quality = GameData.Int(row, "_iQuality");
        return table.TryGetProperty(quality.ToString(), out var reward)
            && reward.ValueKind == JsonValueKind.Object && reward.TryGetProperty("PointValue", out var values)
            && values.ValueKind == JsonValueKind.Object && values.TryGetProperty(tier.ToString(), out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int points) ? Math.Max(0, points) : 0;
    }

    /// <summary>(inferred) The tier rows an unlocked entry newly reaches pay their PointValue once: tiers above
    /// <paramref name="fromExclusive"/> up to <paramref name="toInclusive"/>.</summary>
    private int TierPoints(int card, int fromExclusive, int toInclusive)
    {
        long total = 0;
        for (int tier = Math.Max(0, fromExclusive + 1); tier <= toInclusive; tier++) total += TierPoints(card, tier);
        return (int)Math.Min(int.MaxValue, total);
    }
}
