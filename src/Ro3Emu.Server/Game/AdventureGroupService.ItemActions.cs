using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class AdventureGroupService
{
    internal const int UnlockBackgroundAction = 118;
    internal const int ErrCardUnlocked = 10247; // "This Adventurer Guild Card has already been unlocked. Do not Repeat the unlock."

    /// <summary>
    /// 118 UnlockBrotherhoodBackground [118, AdventureGroupCardConfig id]: 1200900 [118,101] unlocks the group's profile
    /// card background 101 "Netherworld - Shattered Tower" (_iDefaultUnlock 0, "Unlocked after using the item"). The
    /// unlock is the group's: the card list combines the default cards with MyAdventureGroup availablebackground
    /// (LC_AdventureGroupHelper.IsBackgroundCardUnlocked, UI_Sys_AdventureGroup_CardListWidgetCtrl), which arrives as
    /// AdventureGroupBaseInfo.availablebackground (21) in the 34012 Change_BaseData push; the group log gains an
    /// EventType_UnlockBg naming the member (eventFirstParam, UI_Sys_AdventureGroup_HomeEventWidgetCtrl). The item and
    /// the group record commit together; Edit_Background then accepts the card (CardUnlocked). Not in a group: 10206;
    /// already unlocked: 10247. (inferred) Any member may use it; the client names no position. Adventure Group work
    /// keeps the Realm reservation (AdventureGroupMessages), so the use reserves it too.
    /// </summary>
    private async Task<int> UseBackgroundItemAsync(ItemActionUse use)
    {
        var s = use.Session;
        if (use.Count != 1) return ErrorCodes.Parameter;
        if (Own(s) is not { } g || !Normal(g)) return ErrNotInGroup;
        var cards = use.Effects.Select(e => e.Length > 1 ? (int)e[1] : 0).Distinct().ToList();
        if (cards.Any(card => _data.Row("AdventureGroupCardConfig", card) == null)) return ErrorCodes.ItemNoConfig;
        if (cards.All(card => CardUnlocked(g, card))) return ErrCardUnlocked;
        var backgrounds = g.AvailableBackgrounds.ToList();
        var events = g.Events.ToList();
        long updated = g.LastUpdateTime;
        foreach (int card in cards.Where(card => !CardUnlocked(g, card))) g.AvailableBackgrounds.Add(card);
        AddEvent(g, AdventureGroupEventType.EventTypeUnlockBg, s.Role!.Id);
        g.LastUpdateTime = Now;
        bool done = false;
        try
        {
            done = await _character.CommitSharedItemActionAsync(use, "item-action/advguild-background",
                tx => _store.EnlistSave(g, tx), null);
        }
        finally
        {
            if (!done) { g.AvailableBackgrounds = backgrounds; g.Events.Clear(); g.Events.AddRange(events); g.LastUpdateTime = updated; }
        }
        if (!done) return ErrorCodes.ItemsShort;
        await BroadcastBaseAsync(g);
        foreach (var member in OnlineMembers(g)) await SyncRelationAsync(member, g);
        return 0;
    }
}
