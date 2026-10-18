using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    public const uint EquipSwitchPlan = 31413, EquipChangePlanName = 31414;

    // (inferred) Three initially available, empty, zero-based plans; the original slot entitlement is not recovered.
    // Name limit: PublicParamConfig.SchemeNameNumMax = 8, used by the common scheme rename UI.
    public const int EquipmentPlanCount = 3;
    private const int EquipmentPlanNameLimit = 8;

    /// <summary>Stage 31413's validated grid move for a Genre composite; caller persists all owners together.</summary>
    internal int StageEquipmentPlan(DBEquipInfo state, List<InvItem> stagedItems, int index, bool replaceCurrent = false)
    {
        if (!state.EquipPlans.TryGetValue(index, out var target)) return ErrorCodes.Parameter;
        if (index == state.CurrentEquipPlan && !replaceCurrent) return 0;
        var incoming = new Dictionary<long, (InvItem Item, int Grid)>();
        foreach (var (grid, guid) in target.ItemsEquipGrid)
        {
            var item = stagedItems.FirstOrDefault(i => i.Guid == guid);
            if (item == null) return ErrorCodes.ItemMissing;
            if (!IsEquipment(item.Entry)) return ErrorCodes.NotEquipment;
            if (item.Grid < 0 || item.Pack != 2 || item.Stack != 1) return ErrorCodes.EquipNotInBag;
            if (!GridsFor(PartOf(item.Entry)).Contains(grid) || !incoming.TryAdd(guid, (item, grid))) return ErrorCodes.EquipSlot;
        }
        if (incoming.Values.Any(i => i.Grid == 1 && PartOf(i.Item.Entry) == 1) && incoming.Values.Any(i => i.Grid == 2))
            return ErrorCodes.EquipSlot;
        var outgoing = stagedItems.Where(i => i.Grid > 0 && i.Pack == 2 && i.Grid <= 8).Where(i => !incoming.ContainsKey(i.Guid)).ToList();
        int bag = stagedItems.Count(i => i.Grid == 0 && i.Pack == 2);
        int projected = bag + outgoing.Count - incoming.Values.Count(i => i.Item.Grid == 0);
        if (projected > PackCapacity(2) && projected > bag) return ErrorCodes.BagFull;
        if (!replaceCurrent || index != state.CurrentEquipPlan)
        {
            var old = state.EquipPlans[state.CurrentEquipPlan];
            old.ItemsEquipGrid.Clear();
            foreach (var item in stagedItems.Where(i => i.Grid > 0 && i.Pack == 2 && i.Grid <= 8)) old.ItemsEquipGrid[item.Grid] = item.Guid;
        }
        long sort = stagedItems.Where(i => i.Pack == 2).Select(i => i.Sort).DefaultIfEmpty().Max();
        foreach (var item in outgoing) { item.Grid = 0; item.Sort = ++sort; }
        foreach (var (item, grid) in incoming.Values) item.Grid = grid;
        state.CurrentEquipPlan = index;
        return 0;
    }

    /// <summary>Migrate existing worn gear into plan 0, keep names and inactive GUID references.</summary>
    private void LoadEquipmentPlans(GameSession session)
    {
        var saved = roles.LoadEquipPlans(session.Role!.Id);
        session.EquipmentPlans = saved != null ? DBEquipInfo.Parser.ParseFrom(saved) : new DBEquipInfo();
        var state = session.EquipmentPlans;
        for (int index = 0; index < EquipmentPlanCount; index++)
            if (!state.EquipPlans.ContainsKey(index)) state.EquipPlans[index] = new DBEquipPlan();
        if (!state.EquipPlans.ContainsKey(state.CurrentEquipPlan)) state.CurrentEquipPlan = 0;
        state.Inited = true;
        RememberEquipment(session);
        PruneEquipmentPlans(session);
        roles.SaveEquipPlans(session.Role.Id, state.ToByteArray());
    }

    /// <summary>31400 edits the selected plan; other plans retain references to the same instances (inferred).</summary>
    private static void RememberEquipment(GameSession session)
    {
        var plan = session.EquipmentPlans.EquipPlans[session.EquipmentPlans.CurrentEquipPlan];
        plan.ItemsEquipGrid.Clear();
        foreach (var item in Equipped(session)) plan.ItemsEquipGrid[item.Grid] = item.Guid;
    }

    /// <summary>Consumed/deleted instances leave all plans; a plan never owns a second copy (inferred invariant).</summary>
    private static bool PruneEquipmentPlans(GameSession session)
    {
        var owned = session.Items.Select(i => i.Guid).ToHashSet();
        bool changed = false;
        foreach (var plan in session.EquipmentPlans.EquipPlans.Values)
            foreach (var grid in plan.ItemsEquipGrid.Where(p => !owned.Contains(p.Value)).Select(p => p.Key).ToArray())
                changed |= plan.ItemsEquipGrid.Remove(grid);
        return changed;
    }

    internal Task SyncEquipmentAsync(GameSession session) => SyncAsync(session, EquipSync, BuildEquip(session));

    private async Task EquipmentAppearanceChangedAsync(GameSession session)
    {
        Refresh(session);
        var look = new EntityBase { Uuid = (ulong)session.Role!.Id, NotifyType = EntityBase.Types.NotifyType.Equip };
        look.EquipInfo.Add(EquipInfo(session));
        await ToSelfAndViewersAsync(session, EntityBaseChange, look);
        await SendStatsAsync(session);
    }

    /// <summary>
    /// 31413 / 5007: validate the whole target before moving any GUID. Outgoing gear returns to
    /// 5009, target gear leaves it, then the current index, model and stats change. Movement/order are (inferred).
    /// Cards and upgrade levels remain on their grids. Selecting the current plan is an idempotent success (inferred).
    /// </summary>
    public async Task SwitchEquipmentPlanAsync(GameSession session, EquipSwitchPlanReq req)
    {
        using var _ = await LockAsync(session);
        var state = session.EquipmentPlans;
        int result = state.EquipPlans.TryGetValue(req.TargetIndex, out var target) ? 0 : ErrorCodes.Parameter;
        var incoming = new Dictionary<long, (InvItem Item, int Grid)>();
        if (result == 0)
        {
            foreach (var (grid, guid) in target!.ItemsEquipGrid)
            {
                var item = session.Items.FirstOrDefault(i => i.Guid == guid);
                if (item == null) { result = ErrorCodes.ItemMissing; break; }
                if (!IsEquipment(item.Entry)) { result = ErrorCodes.NotEquipment; break; }
                if (item.Grid < 0 || item.Pack != 2 || item.Stack != 1) { result = ErrorCodes.EquipNotInBag; break; }
                if (!GridsFor(PartOf(item.Entry)).Contains(grid) || !incoming.TryAdd(guid, (item, grid)))
                { result = ErrorCodes.EquipSlot; break; }
            }
            if (incoming.Values.Any(i => i.Grid == 1 && PartOf(i.Item.Entry) == 1) && incoming.Values.Any(i => i.Grid == 2))
                result = ErrorCodes.EquipSlot;
        }
        if (result == 0 && req.TargetIndex != state.CurrentEquipPlan)
        {
            var outgoing = Equipped(session).Where(i => !incoming.ContainsKey(i.Guid)).ToList();
            int oldBagSize = session.Items.Count(i => i.Grid == 0 && i.Pack == 2);
            int newBagSize = oldBagSize + outgoing.Count - incoming.Values.Count(i => i.Item.Grid == 0);
            // (inferred) A switch may not add cells beyond capacity; a swap that reduces an already overfull bag is allowed.
            if (newBagSize > PackCapacity(2) && newBagSize > oldBagSize) result = ErrorCodes.BagFull;
            else
            {
                var next = state.Clone();
                var staged = session.Items.Select(i => i.Clone()).ToList();
                var oldPlan = next.EquipPlans[next.CurrentEquipPlan];
                oldPlan.ItemsEquipGrid.Clear();
                foreach (var worn in Equipped(session)) oldPlan.ItemsEquipGrid[worn.Grid] = worn.Guid;
                foreach (var old in outgoing)
                {
                    var moved = staged.Single(i => i.Guid == old.Guid);
                    moved.Grid = 0;
                    moved.Sort = staged.Where(i => i.Grid == 0 && i.Pack == moved.Pack)
                        .Select(i => i.Sort).DefaultIfEmpty(0).Max() + 1;
                }
                foreach (var (selected, grid) in incoming.Values) staged.Single(i => i.Guid == selected.Guid).Grid = grid;
                next.CurrentEquipPlan = req.TargetIndex;
                await CommitCharacterAsync(session, "equip-plan-switch", nextItems: staged,
                    blobs: new Dictionary<string, byte[]> { ["equip_plans"] = next.ToByteArray() },
                    apply: () => session.EquipmentPlans = next);
                state = next;
                await SyncItemsAsync(session);
                await SyncEquipmentAsync(session);
            }
        }
        await session.SendGameAsync(EquipSwitchPlan, new EquipSwitchPlanResp { Result = result, CurrentPlan = state.CurrentEquipPlan });
        if (result == 0)
        {
            await EquipmentAppearanceChangedAsync(session);
            await CombatLoadoutChangedAsync(session);
            foreach (var handler in StateChanged) handler(session);
        }
    }

    /// <summary>31414 has no name in the response: persist it and send the 5007 planName increment.</summary>
    public async Task RenameEquipmentPlanAsync(GameSession session, EquipChangePlanNameReq req)
    {
        using var _ = await LockAsync(session);
        // (inferred) Reject blank/control text; count Unicode scalars, preserving the supplied name exactly.
        int result = !session.EquipmentPlans.EquipPlans.ContainsKey(req.TargetIndex)
            || string.IsNullOrWhiteSpace(req.Name) || req.Name.Any(char.IsControl)
            || req.Name.EnumerateRunes().Count() > EquipmentPlanNameLimit ? ErrorCodes.Parameter : 0;
        if (result == 0)
        {
            var next = session.EquipmentPlans.Clone();
            next.EquipPlans[req.TargetIndex].PlanName = req.Name;
            await CommitCharacterAsync(session, "equip-plan-rename",
                blobs: new Dictionary<string, byte[]> { ["equip_plans"] = next.ToByteArray() },
                apply: () => session.EquipmentPlans = next);
            await SyncEquipmentAsync(session);
        }
        await session.SendGameAsync(EquipChangePlanName, new EquipChangePlanNameResp { Result = result });
    }
}
