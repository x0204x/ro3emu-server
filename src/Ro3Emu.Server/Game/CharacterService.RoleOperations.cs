using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>Only the 5009 preference fields; inventory instances remain in ItemStore.</summary>
public sealed class RoleBagState
{
    public DBItemsInfo Info { get; set; }
    public int LastNotifiedCartSize { get; set; }
    public RoleBagState(byte[]? bytes) => Info = bytes is { Length: > 0 } ? DBItemsInfo.Parser.ParseFrom(bytes) : new();
}

/// <summary>5067 plan collection. RoleProgress.Stats is the authoritative active plan's allocated values.</summary>
public sealed class StatPlanState
{
    public BaseAttrInfo Info { get; set; }
    public StatPlanState(byte[]? bytes) => Info = bytes is { Length: > 0 } ? BaseAttrInfo.Parser.ParseFrom(bytes) : new();
}

public sealed partial class CharacterService
{
    public const uint CareerSpecialSizeChange = 31308, QuickUseSave = 31314, MedicinesAutoUseSet = 31320;
    public const uint ArrowSwitch = 31324, ItemsCdNotify = 31325, CareerSpecialTransfer = 31335;
    public const uint ShortcutBarSave = 31336, ShortcutBarUse = 31337;
    public const uint ChangeStatPlan = 31504, RenameStatPlan = 31505;

    // PublicParamConfig.CartRemouldBagNum, clientB Lua lines 1207-1222. This only grants capacity
    // to an actual Merchant-line character with the corresponding learned cart skill (inferred entitlement).
    // The keys are SkillConfig row ids (group*100 + level): 1600301 Cart Use 1, 1600501.. Vending 1..; RoleProgress.Skills
    // is keyed by the group (_iSkillID), so a row counts once its group reaches that level.
    private static readonly (int Skill, int Size)[] CartSizes =
        [(1600301, 50), (1600501, 55), (1600502, 60), (1600503, 70), (1600504, 80), (1600505, 90)];
    private const int StatPlanCount = 5; // PublicParamConfig.SchemeNum=5 (inferred for attribute plans).
    private const int StatPlanNameLimit = 8; // PublicParamConfig.SchemeNameNumMax=8.
    // D Asia's PC main-bar controller constructs exactly six item slots. Its incremental handler only updates a
    // slot that was present in the first 5009 snapshot, so BuildItems also projects all six positions on the wire.
    private const int ShortcutSlotLimit = 6;

    private void LoadStatPlans(GameSession session)
    {
        var info = session.StatPlans.Info;
        if (info.CurPlanIndex is < 0 or >= StatPlanCount) info.CurPlanIndex = 0;
        for (int index = 0; index < StatPlanCount; index++)
        {
            if (!info.AllPlanBaseProperty.TryGetValue(index, out var plan))
            {
                plan = new PlanBaseProperty { AttrPoints = TotalPoints(session.Progress!) };
                for (int stat = 1; stat <= 6; stat++) plan.BaseProperty[stat] = 1;
                info.AllPlanBaseProperty[index] = plan;
            }
        }
        var current = CurrentPlan(session.Progress!, info.AllPlanBaseProperty[info.CurPlanIndex]);
        info.AllPlanBaseProperty[info.CurPlanIndex] = current;
    }

    private void RefreshPlanBudgets(BaseAttrInfo info, RoleProgress progress)
    {
        int budget = TotalPoints(progress);
        foreach (var plan in info.AllPlanBaseProperty.Values)
        {
            if (plan.BaseProperty.Count != 6) continue;
            int used = Enumerable.Range(1, 6).Sum(i => StatCost((int)plan.BaseProperty.GetValueOrDefault(i)));
            plan.UsedAttrPoints = used;
            plan.AttrPoints = Math.Max(0, budget - used);
        }
    }

    private void SaveStatPlans(GameSession session)
    {
        var info = session.StatPlans.Info;
        var current = CurrentPlan(session.Progress!, info.AllPlanBaseProperty[info.CurPlanIndex]);
        info.AllPlanBaseProperty[info.CurPlanIndex] = current;
        roles.SaveStatPlanProgress(session.Role!.Id, session.Progress!, info.ToByteArray());
    }

    /// <summary>31504 changes the active 5067 plan and derived stats; all plans share the earned point budget.</summary>
    public async Task ChangeStatPlanAsync(GameSession session, PlanAttrPointChangeReq req)
    {
        using var gate = await LockAsync(session);
        var old = session.StatPlans.Info;
        int dest = req.DestPlanIndex;
        old.AllPlanBaseProperty.TryGetValue(dest, out var plan);
        int result = dest is < 0 or >= StatPlanCount || plan == null ? ErrorCodes.Parameter : 0;
        if (result == 0 && plan!.BaseProperty.Count != 6) result = ErrorCodes.Parameter;
        var wanted = result == 0 ? Enumerable.Range(1, 6).Select(i => (int)plan!.BaseProperty.GetValueOrDefault(i)).ToArray() : [];
        if (result == 0 && (wanted.Any(v => v is < 1 or > MaxStat) || wanted.Sum(StatCost) > TotalPoints(session.Progress!)))
            result = ErrorCodes.StatPointsShort;
        if (result == 0 && dest != old.CurPlanIndex)
        {
            var staged = CloneProgress(session.Progress!);
            wanted.CopyTo(staged.Stats, 0);
            var next = old.Clone();
            var outgoing = CurrentPlan(session.Progress!, next.AllPlanBaseProperty[next.CurPlanIndex]);
            next.AllPlanBaseProperty[next.CurPlanIndex] = outgoing;
            next.CurPlanIndex = dest;
            var active = CurrentPlan(staged, next.AllPlanBaseProperty[dest]);
            next.AllPlanBaseProperty[dest] = active;
            await CommitStatsAsync(session, "stat-switch", staged, next);
        }
        // The Lua reply consumer changes curPlanIndex even on error. Echo the retained index on refusal.
        await session.SendGameAsync(ChangeStatPlan, new PlanAttrPointChangeResq
            { Result = result, DestPlanIndex = result == 0 ? dest : old.CurPlanIndex, Flag = result == 0 ? 0 : 1 });
        if (result != 0 || dest == old.CurPlanIndex) return;
        await SendStatPlanAsync(session);
        await SendStatsAsync(session);
        foreach (var handler in ProgressHandlers) await handler(session);
        foreach (var handler in StateChanged) handler(session);
    }

    /// <summary>31505 reply must echo the persisted name: the client writes it locally without checking result.</summary>
    public async Task RenameStatPlanAsync(GameSession session, PlanAttrPointNameChangeReq req)
    {
        using var gate = await LockAsync(session);
        bool indexOk = session.StatPlans.Info.AllPlanBaseProperty.TryGetValue(req.PlanIndex, out var plan);
        int result = !indexOk || string.IsNullOrWhiteSpace(req.PlanName) || req.PlanName.Any(char.IsControl)
            || req.PlanName.EnumerateRunes().Count() > StatPlanNameLimit ? ErrorCodes.Parameter : 0;
        if (!indexOk)
        {
            await session.SendGameAsync(TaskService.ApiError, new APIErrorMsg { ErrorCode = result, OriginMsgId = RenameStatPlan });
            return;
        }
        if (result == 0)
        {
            var next = session.StatPlans.Info.Clone();
            next.AllPlanBaseProperty[req.PlanIndex].PlanName = req.PlanName;
            await CommitStatsAsync(session, "stat-rename", CloneProgress(session.Progress!), next);
            plan = next.AllPlanBaseProperty[req.PlanIndex];
        }
        await session.SendGameAsync(RenameStatPlan, new PlanAttrPointNameChangeResq
            { Result = result, PlanIndex = req.PlanIndex, PlanName = plan!.PlanName });
        if (result == 0) await SendStatPlanAsync(session);
    }

    private bool IsMerchantCareer(int career)
    {
        for (int depth = 0; depth < 8 && career > 0; depth++)
        {
            if (career == 6100) return true;
            career = data.Row("JobConfig", career) is { } row ? (int)GameData.Int(row, "_iParentId") : 0;
        }
        return false;
    }

    private int CareerPackCapacity(GameSession session) => !IsMerchantCareer(session.Progress!.Career) ? 0
        : CartSizes.Where(t => session.Progress.Skills.GetValueOrDefault(t.Skill / 100) >= t.Skill % 100)
            .Select(t => t.Size).DefaultIfEmpty(0).Max();

    /// <summary>SkillService.Learned hook: a newly learned cart skill changes the visible pack size.</summary>
    public async Task NotifyCareerCapacityAsync(GameSession session)
    {
        using var gate = await LockAsync(session);
        int size = CareerPackCapacity(session);
        if (size <= 0 || size == session.BagState.LastNotifiedCartSize) return;
        await session.SendGameAsync(CareerSpecialSizeChange, new NotifyCareerSpecialPackSizeChange { Size = size });
        session.BagState.LastNotifiedCartSize = size;
        if (session.Items.Any(i => i.Grid == 0 && i.Pack == 100)) await SyncItemsAsync(session);
    }

    /// <summary>31314 persists the quick-use entry, not an instance GUID. The client reply takes slot 0.</summary>
    public async Task QuickUseSaveAsync(GameSession session, QuickUseSaveReq req)
    {
        using var gate = await LockAsync(session);
        int result = req.LieQuickUse != 0 ? ErrorCodes.Parameter
            : req.EntryItem != 0 && !session.Items.Any(i => i.Grid == 0 && i.Entry == req.EntryItem && i.Stack > 0)
                ? ErrorCodes.ItemMissing
            : req.EntryItem != 0 && (data.Row("ItemConfig", req.EntryItem) is not { } row || !CanUseItemEffects(row))
                ? ErrorCodes.FeatureUnavailable : 0;
        if (result == 0)
        {
            var next = session.BagState.Info.Clone();
            next.QuickUseInfos.Clear();
            next.QuickUseInfos.Add(new QuickUseSaveInfo { LieQuickUse = 0, ItemEntry = req.EntryItem });
            await CommitBagAsync(session, "bag-setting", next);
        }
        var reply = new QuickUseSaveRes { Result = result };
        reply.QuickSaveInfos.Add(session.BagState.Info.QuickUseInfos);
        await session.SendGameAsync(QuickUseSave, reply);
        if (result == 0) await SyncItemsAsync(session);
    }

    /// <summary>31320 stores each HP/SP policy in the distinct 5009 medicine object.</summary>
    public async Task MedicinesAutoUseSetAsync(GameSession session, MedicinesAutoUseSetReq req)
    {
        using var gate = await LockAsync(session);
        bool validType = req.Type is MedicineAutoUseType.Hp or MedicineAutoUseType.Sp;
        int result = !validType || req.OpType is < MedicineAutoUseOpType.Switch or > MedicineAutoUseOpType.Medicine
            || req.SetData == null || req.SetData.Condition is < 0 or > 100 || req.SetData.ItemEntrys.Count > 8
            ? ErrorCodes.Parameter : 0;
        // The client keeps empty medicine positions as zeroes. HP entries must match its own candidate rule:
        // ItemConfig._iCanAuto contains both 1 and 3. Ignore zero placeholders when validating removals.
        if (result == 0 && req.SetData!.ItemEntrys.Where(entry => entry != 0).Any(entry => entry < 0
            || data.Row("ItemConfig", entry) is not { } row || GameData.Int(row, "_iIsUse") == 0
            || req.Type == MedicineAutoUseType.Hp && (!HasAutoMode(row, 1) || !HasAutoMode(row, 3))))
            result = ErrorCodes.ItemNoConfig;
        var nextInfo = session.BagState.Info.Clone();
        var saved = nextInfo.MedicinesAutoUse ?? new MedicinesAutoUseSet();
        if (result == 0)
        {
            if (req.Type == MedicineAutoUseType.Hp) saved.HpConfig = req.SetData!.Clone();
            else saved.SpConfig = req.SetData!.Clone();
            nextInfo.MedicinesAutoUse = saved;
            await CommitBagAsync(session, "medicine-setting", nextInfo);
        }
        await session.SendGameAsync(MedicinesAutoUseSet, new MedicinesAutoUseSetResp
        { Result = result, Type = req.Type, SetData = (req.Type == MedicineAutoUseType.Hp ? saved.HpConfig : saved.SpConfig)?.Clone() });
        if (result == 0) await SyncItemsAsync(session);
    }

    private static bool HasAutoMode(System.Text.Json.JsonElement row, int mode) =>
        row.TryGetProperty("_iCanAuto", out var modes) && modes.ValueKind == System.Text.Json.JsonValueKind.Array
        && modes.EnumerateArray().Any(value => GameData.Number(value) == mode);

    /// <summary>31324 sets the equipped arrow entry; count comes from actually owned bag stacks.</summary>
    public async Task ArrowSwitchAsync(GameSession session, ItemArrowSwitchReq req)
    {
        using var gate = await LockAsync(session);
        int count = session.Items.Where(i => i.Grid == 0 && i.Entry == req.ItemId).Sum(i => i.Stack);
        int result = req.ItemId <= 0 ? ErrorCodes.Parameter
            : data.Row("ItemConfig", req.ItemId) is not { } row || GameData.Int(row, "_iType") != 2 || GameData.Int(row, "_iSubType") != 207
                ? ErrorCodes.ItemNoConfig : count == 0 ? ErrorCodes.ItemMissing : 0;
        if (result == 0)
        {
            var next = session.BagState.Info.Clone();
            next.CurrentArrowID = req.ItemId;
            await CommitBagAsync(session, "bag-setting", next);
        }
        // _processChangeArrowId writes the echoed id/count without inspecting result.
        int echoed = result == 0 ? req.ItemId : (int)session.BagState.Info.CurrentArrowID;
        int echoedCount = session.Items.Where(i => i.Grid == 0 && i.Entry == echoed).Sum(i => i.Stack);
        await session.SendGameAsync(ArrowSwitch, new ItemArrowSwitchResp { Result = result, ItemId = echoed, ItemCount = echoedCount });
        if (result == 0) await SyncItemsAsync(session);
    }

    /// <summary>31335 moves owned GUID/count batches between normal tabs and Merchant cart pack 100.</summary>
    public async Task CareerSpecialTransferAsync(GameSession session, CareerSpecialTransferItemsReq req)
    {
        using var gate = await LockAsync(session);
        int capacity = CareerPackCapacity(session);
        int source = req.TransType == CareerSpecialTransferItemsType.ToCart ? 0 : 100;
        int result = req.TransType is not (CareerSpecialTransferItemsType.ToCart or CareerSpecialTransferItemsType.ToNormal)
            || req.ItemGuidCountMap.Count == 0 ? ErrorCodes.Parameter
            : capacity <= 0 ? ErrorCodes.FunctionLocked : 0;
        var next = session.Items.Select(i => i.Clone()).ToList();
        if (result == 0)
        {
            foreach (var (guid, count) in req.ItemGuidCountMap)
            {
                var item = next.FirstOrDefault(i => i.Guid == guid && i.Grid == 0 && (source == 0 ? i.Pack != 100 : i.Pack == 100));
                if (item == null) { result = ErrorCodes.ItemMissing; break; }
                if (count <= 0 || count > item.Stack) { result = count <= 0 ? ErrorCodes.Parameter : ErrorCodes.ItemsShort; break; }
                if (item.IsLocked) { result = ErrorCodes.ItemLocked; break; }
                if (TotemService.IsReserved(session, item)) { result = ErrorCodes.ItemLocked; break; }
                if (data.Row("ItemConfig", item.Entry) is not { } row) { result = ErrorCodes.ItemNoConfig; break; }
                if (source == 0 && GameData.Int(row, "_kCanCart") == 0) { result = ErrorCodes.Parameter; break; }
                int destination = source == 0 ? 100 : TotemService.IsTotem(data, item.Entry) ? 4
                    : EmblemService.IsEmblem(data, item.Entry) ? 5 : ConfigPack(row);
                if (count == item.Stack)
                {
                    item.Pack = destination;
                    item.Sort = next.Where(i => i.Grid == 0 && i.Pack == destination && i != item).Select(i => i.Sort).DefaultIfEmpty(0).Max() + 1;
                }
                else
                {
                    item.Stack -= (int)count;
                    var moved = item.Clone();
                    moved.Guid = 0;
                    moved.Stack = (int)count;
                    moved.Pack = destination;
                    moved.Sort = next.Where(i => i.Grid == 0 && i.Pack == destination).Select(i => i.Sort).DefaultIfEmpty(0).Max() + 1;
                    next.Add(moved);
                }
            }
            if (result == 0)
            {
                var packs = next.Where(i => i.Grid == 0).GroupBy(i => i.Pack);
                foreach (var pack in packs)
                {
                    int limit = pack.Key == 100 ? capacity : PackCapacity(pack.Key);
                    if (limit <= 0 || pack.Count() > limit) { result = ErrorCodes.BagFull; break; }
                }
            }
        }
        if (result == 0)
        {
            await CommitCharacterAsync(session, "career-transfer", nextItems: next);
        }
        await session.SendGameAsync(CareerSpecialTransfer, new CareerSpecialTransferItemsRsp { Result = result });
        if (result == 0) await SyncItemsAsync(session);
    }

    /// <summary>31336 stores an owned GUID at a zero-based slot. GUID 0 clears it.</summary>
    public async Task ShortcutBarSaveAsync(GameSession session, ShortcutBarSaveReq req)
    {
        using var gate = await LockAsync(session);
        int result = req.SlotIndex is < 0 or >= ShortcutSlotLimit ? ErrorCodes.Parameter
            : req.ItemGuid != 0 && !session.Items.Any(i => i.Guid == req.ItemGuid && i.Grid == 0) ? ErrorCodes.ItemMissing
            : req.ItemGuid != 0 && session.Items.First(i => i.Guid == req.ItemGuid) is { } item
                && (data.Row("ItemConfig", item.Entry) is not { } row || !CanUseItemEffects(row))
                ? ErrorCodes.FeatureUnavailable : 0;
        if (result == 0)
        {
            var next = session.BagState.Info.Clone();
            var list = next.ShortcutBarInfos;
            foreach (var old in list.Where(i => i.SlotIndex == req.SlotIndex).ToList()) list.Remove(old);
            if (req.ItemGuid != 0) list.Add(new ShortcutBarInfo { SlotIndex = req.SlotIndex, ItemGuid = req.ItemGuid });
            await CommitBagAsync(session, "bag-setting", next);
        }
        await session.SendGameAsync(ShortcutBarSave, new ShortcutBarSaveRes { Result = result });
        if (result == 0) await SyncItemsAsync(session);
    }

    /// <summary>31337 uses the bound item through the same validated 31323 effect path (inferred), then echoes the slot.</summary>
    public async Task ShortcutBarUseAsync(GameSession session, ShortcutBarUseReq req)
    {
        using var owner = session.Owners is { } owners
            ? await owners.EnterAsync("character shortcut use", Ownership.ReservationKey.Role(session.Role!.Id)) : null;
        int result = req.SlotIndex is < 0 or >= ShortcutSlotLimit ? ErrorCodes.Parameter : 0;
        long guid = session.BagState.Info.ShortcutBarInfos.FirstOrDefault(i => i.SlotIndex == req.SlotIndex)?.ItemGuid ?? 0;
        if (result == 0 && guid == 0) result = ErrorCodes.ItemMissing;
        if (result == 0) result = await UseItemCoreAsync(session, new ItemUseReq { ItemGuid = guid, ItemCount = 1 }, reply: false);
        await session.SendGameAsync(ShortcutBarUse, new ShortcutBarUseRes { Result = result, SlotIndex = req.SlotIndex });
    }
}
