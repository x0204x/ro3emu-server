using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    // ---- AUTO_SYNC datasets: build the DB object the client mirrors, send what changed (AutoSync) ----

    /// <summary>
    /// Like <see cref="SyncAsync(GameSession, uint, IMessage, Action{IMessage}?)"/> for a live object: when it still
    /// equals what the client was last sent there is nothing to diff, so no snapshot is taken (talents cloned 5 plans x
    /// 232 nodes on every sync of a login). The version advances exactly as the empty-change path does.
    /// </summary>
    internal static Task SyncAsync(GameSession session, uint msgId, IMessage live, Func<IMessage> snapshot, Action<IMessage>? prepare = null)
    {
        if (session.Synced.TryGetValue(msgId, out var before) && before.Equals(live))
        {
            NextVersion(session, msgId);
            return Task.CompletedTask;
        }
        return SyncAsync(session, msgId, snapshot(), prepare);
    }

    /// <summary>Sends the dataset's change since the last send (the full object, with syncAllMark, the first time).</summary>
    internal static async Task SyncAsync(GameSession session, uint msgId, IMessage now, Action<IMessage>? prepare = null)
    {
        session.PlanPowers?.Invoke(msgId, now);
        var inc = session.Synced.TryGetValue(msgId, out var before)
            ? AutoSync.Changes(before, now, NextVersion(session, msgId))
            : AutoSync.Full(now, NextVersion(session, msgId));
        session.Synced[msgId] = now;
        if (inc != null)
        {
            // Optional wire-only initialization for Lua datasets whose full handlers require empty maps.
            prepare?.Invoke(inc);
            await session.SendGameAsync(msgId, inc);
        }
    }

    /// <summary>
    /// 5009 after a bag change. The client keeps socketed cards in the CardStoragePackInfo dataset, which only the full
    /// handler creates (LC_DAL_Item._processStoragePack); the change handler returns while it does not exist. So when
    /// the first card goes into a socket and the client was sent no storagePack, 5009 goes out whole again.
    /// </summary>
    private async Task SyncItemsAsync(GameSession session, Func<bool>? stillOwner = null)
    {
        var now = BuildItems(session);
        if (now.StoragePack != null && session.Synced.GetValueOrDefault(ItemsSync) is DBItemsInfo { StoragePack: null }) session.Synced.Remove(ItemsSync);
        await SyncAsync(session, ItemsSync, now);
        if (stillOwner?.Invoke() == false) return;
        if (PruneEquipmentPlans(session))
        {
            roles.SaveEquipPlans(session.Role!.Id, session.EquipmentPlans.ToByteArray());
            await SyncEquipmentAsync(session);
        }
    }

    /// <summary>5066: PlayerBasicInfo (id and career are mandatory); exp is the progress inside the level.</summary>
    private static RoleBaseInfo BuildBaseInfo(GameSession session)
    {
        var p = session.Progress!;
        var role = session.Role!;
        return new RoleBaseInfo
        {
            Id = role.Id, Name = role.Name, Sex = role.Sex, Career = p.Career, Level = p.Level, JobLevel = p.JobLevel,
            Exp = p.Exp, JobExp = p.JobExp, CreateTime = role.CreateTime,
            CurPower = PowerService.Current(session), HistoryMaxPower = PowerService.History(session),
        };
    }

    /// <summary>5067: all persisted stat-point plans, with the active plan sourced from RoleProgress.</summary>
    private BaseAttrInfo BuildAttr(GameSession session)
    {
        var p = session.Progress!;
        var info = session.StatPlans.Info.Clone();
        info.AllAttrPoints = TotalPoints(p);
        RefreshPlanBudgets(info, p);
        info.AllPlanBaseProperty.TryGetValue(info.CurPlanIndex, out var saved);
        var active = CurrentPlan(p, saved);
        info.AllPlanBaseProperty[info.CurPlanIndex] = active;
        return info;
    }

    /// <summary>
    /// 5009: bag packs and currencies. A pack the client has stays present (empty) when its last item goes: a full
    /// pack without items decodes to nil and the bag code crashes on it.
    /// </summary>
    private DBItemsInfo BuildItems(GameSession session)
    {
        var p = session.Progress!;
        var info = new DBItemsInfo { AutoDecomposeSetting = session.AutoDecompose.Clone() };
        var preferences = session.BagState.Info;
        // D Asia creates KnapsackShortcutBinding only when quickUseInfos is present in the first 5009 snapshot.
        // Keep the stored form sparse, but always publish its one supported slot to the client.
        info.QuickUseInfos.Add(preferences.QuickUseInfos.FirstOrDefault(i => i.LieQuickUse == 0)?.Clone()
            ?? new QuickUseSaveInfo { LieQuickUse = 0, ItemEntry = 0 });
        // Keep persisted preferences sparse, but make the client-facing list positional and stable. D Asia's Lua
        // change handler ignores an update for an absent slot; a dense first snapshot lets later saves/removals
        // travel as element-wise AUTO_SYNC increments and appear without a scene reload.
        for (int slot = 0; slot < ShortcutSlotLimit; slot++)
        {
            long itemGuid = preferences.ShortcutBarInfos.FirstOrDefault(i => i.SlotIndex == slot)?.ItemGuid ?? 0;
            info.ShortcutBarInfos.Add(new ShortcutBarInfo { SlotIndex = slot, ItemGuid = itemGuid });
        }
        // The D Asia HP panel dereferences HpAutoUseConfig and floors its threshold before it sends 31320. A missing
        // object therefore prevents the first medicine from ever reaching the server. Lua's parser uses 100 as the
        // default threshold, so expose that same default without persisting it until the player changes the setting.
        info.MedicinesAutoUse = preferences.MedicinesAutoUse?.Clone() ?? new MedicinesAutoUseSet();
        info.MedicinesAutoUse.HpConfig ??= new MedicinesAutoUse { Condition = 100 };
        info.CurrentArrowID = preferences.CurrentArrowID;
        info.ItemCds = preferences.ItemCds?.Clone();
        info.ShareCds = preferences.ShareCds?.Clone();
        foreach (var (entry, used) in CurrentUseTimes(preferences)) info.UseTimes[entry] = used;
        var bag = session.Items.Where(i => i.Grid == 0).ToList();
        var packs = bag.Select(i => i.Pack).ToHashSet();
        if (session.Synced.GetValueOrDefault(ItemsSync) is DBItemsInfo sent) packs.UnionWith(sent.PackInfo.Keys);
        foreach (var pack in packs.Order())
        {
            var packInfo = FullPack(bag.Where(i => i.Pack == pack));
            packInfo.PackCapacity = pack == 100 ? CareerPackCapacity(session) : PackCapacity(pack);
            info.PackInfo[pack] = packInfo;
        }
        // Keep the core currencies plus liveness (5) even at zero, and project earned reward currencies such as 407.
        foreach (var id in new[] { ZenyId, 2, 3, 5 }.Concat(p.Currency.Keys).Distinct().Order())
            info.Currency[id] = p.Currency.GetValueOrDefault(id);
        // Cards in equipment sockets: storagePack.cardInfo[grid].cardSlotInfo[socket]. Absent until the
        // first card is inserted; afterwards kept (possibly empty) so a removal travels as map deletions, not a reset.
        var socketed = session.Items.Where(i => i.Grid < 0).ToList();
        if (socketed.Count > 0 || session.Synced.GetValueOrDefault(ItemsSync) is DBItemsInfo { StoragePack: not null })
        {
            info.StoragePack = new DBStoragePack();
            foreach (var card in socketed)
            {
                var (grid, socket) = CardSocket(card);
                if (!info.StoragePack.CardInfo.TryGetValue(grid, out var slots)) info.StoragePack.CardInfo[grid] = slots = new DBStoragePackCardInfo();
                slots.CardSlotInfo[socket] = ItemInfo(card);
            }
        }
        session.NewItems.IntersectWith(bag.Select(i => i.Guid));
        foreach (var guid in session.NewItems) info.GuidToNew[guid] = true;
        return info;
    }

    /// <summary>5007: equipped instances, the plan grid, refine levels of grids 1..7.</summary>
    private DBEquipInfo BuildEquip(GameSession session)
    {
        var info = session.EquipmentPlans.Clone();
        var levels = session.EquipLevels;
        for (int grid = 1; grid <= EquipInfoSlots; grid++)
        {
            info.ItemsRefine[grid] = levels.Refine.GetValueOrDefault(grid);
            info.ItemsStrength[grid] = levels.Strength.GetValueOrDefault(grid);
            if (levels.Luck.GetValueOrDefault(grid) is var luck and > 0) info.RefineLuckyAdd[grid] = luck;
        }
        info.RefineEffectLv = Resonance(session, "RefiningEffectTable");
        foreach (var item in Equipped(session)) info.Items[item.Guid] = ItemInfo(item);
        return info;
    }

    private static long NextVersion(GameSession session, uint msgId)
    {
        lock (session.SyncVersions)
        {
            long v = session.SyncVersions.GetValueOrDefault(msgId) + 1;
            session.SyncVersions[msgId] = v;
            return v;
        }
    }

    private static async Task<IDisposable?> LockIntentAsync(GameSession session, Func<bool>? stillOwner)
    {
        var cancellation = stillOwner == null ? CancellationToken.None : session.PendingCharacterIntent;
        try { return await LockAsync(session, cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || stillOwner?.Invoke() == false) { return null; }
    }

    private static async Task<IDisposable> LockAsync(GameSession session, CancellationToken cancellation = default,
        IReadOnlyList<Ownership.ReservationKey>? additionalKeys = null)
    {
        if (session.Progress == null) throw new InvalidOperationException("not in game");
        Ownership.LegacyOwnerScope? scope = null;
        long? roleId = session.Role?.Id;
        var attachment = session.Attachment;
        bool acquired = false;
        try
        {
            if (session.Owners is { } owners)
                scope = await owners.EnterWithGateAsync("Character.StateLock", session.StateLock,
                    [Ownership.ReservationKey.Role(roleId!.Value), .. additionalKeys ?? []], cancellation);
            else await session.StateLock.WaitAsync(cancellation);
            acquired = true;
            if (session.Role?.Id != roleId || !ReferenceEquals(session.Attachment, attachment))
                throw new OperationCanceledException("Role changed before the character gate was acquired.");
            if (scope != null)
            {
                scope.Context.AssertRole(roleId!.Value);
                if (session.Owner is { } owner) scope.Context.Pin(owner);
            }
        }
        catch { if (acquired) session.StateLock.Release(); scope?.Dispose(); throw; }
        return new Releaser(session.StateLock, scope);
    }

    private sealed class Releaser(SemaphoreSlim gate, IDisposable? scope = null) : IDisposable
    {
        public void Dispose() { gate.Release(); scope?.Dispose(); }
    }
}
