using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    /// <summary>C/D 13a: keep surviving instance identities while publishing one committed inventory revision.</summary>
    internal static void ApplyStagedItems(GameSession session, List<InvItem> staged, long[] ids)
    {
        ItemSnapshotWritePlan.ApplyGuids(staged, ids);
        var surviving = session.Items.ToDictionary(item => item.Guid);
        session.Items.Clear();
        foreach (var item in staged)
        {
            if (surviving.TryGetValue(item.Guid, out var live))
            { CopyItem(live, item); session.Items.Add(live); }
            else session.Items.Add(item);
        }
    }

    private static DBEquipInfo? PrunedEquipmentPlans(GameSession session, IReadOnlyList<InvItem> items)
    {
        var owned = items.Select(i => i.Guid).ToHashSet();
        var next = session.EquipmentPlans.Clone();
        bool changed = false;
        foreach (var plan in next.EquipPlans.Values)
            foreach (int grid in plan.ItemsEquipGrid.Where(p => !owned.Contains(p.Value)).Select(p => p.Key).ToArray())
                changed |= plan.ItemsEquipGrid.Remove(grid);
        return changed ? next : null;
    }

    /// <summary>Stage is completed by the caller under StateLock/WholeRole. Apply contains no awaits or hooks.</summary>
    private async Task CommitCharacterAsync(GameSession session, string operation, List<InvItem>? nextItems = null,
        RoleProgress? progress = null, IReadOnlyDictionary<string, byte[]>? blobs = null, Action? apply = null,
        IReadOnlyList<(int Entry, long Count)>? costs = null, IReadOnlyList<(int Entry, int Count)>? goods = null)
    {
        RequireOwnedRole(session).BindPresentation(session);
        long roleId = session.Role!.Id;
        nextItems ??= [.. session.Items.Select(i => i.Clone())];
        var pruned = blobs?.ContainsKey("equip_plans") == true ? null : PrunedEquipmentPlans(session, nextItems);
        var tx = roles.NewTransaction();
        int itemReceipt = tx.ItemDelta(roleId, session.Items, nextItems);
        if (progress != null) tx.Progress(roleId, progress);
        foreach (var (column, bytes) in blobs ?? new Dictionary<string, byte[]>()) tx.RoleBlob(roleId, column, bytes);
        if (pruned != null) tx.RoleBlob(roleId, "equip_plans", pruned.ToByteArray());
        tx.WithEconomy(FamilyEconomy(session, "cd13a/character-" + operation, costs ?? [], goods ?? [],
            new { Operation = operation, Items = nextItems, Progress = progress, Blobs = blobs }));
        await roles.CommitAsync(tx, "character/" + operation, receipts =>
        {
            ApplyStagedItems(session, nextItems, (long[])receipts[itemReceipt]!);
            if (progress != null) CopyProgress(session.Progress!, progress);
            if (pruned != null) session.EquipmentPlans = pruned;
            apply?.Invoke();
        });
    }

    private Task CommitBagAsync(GameSession session, string operation, DBItemsInfo next) =>
        CommitCharacterAsync(session, operation, blobs: new Dictionary<string, byte[]> { ["bag_state"] = next.ToByteArray() },
            apply: () => session.BagState.Info = next);

    private Task CommitStatsAsync(GameSession session, string operation, RoleProgress progress, BaseAttrInfo plans) =>
        CommitCharacterAsync(session, operation, progress: progress,
            blobs: new Dictionary<string, byte[]> { ["stat_plans"] = plans.ToByteArray() },
            apply: () => session.StatPlans.Info = plans);
}
