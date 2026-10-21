using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

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
    /// (530 = 2) leaves it unchanged. A missing entry is created locked, as first sight does.
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
