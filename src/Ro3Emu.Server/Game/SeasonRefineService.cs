using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Season refinement of equipment grids (LC_EquipmentServer.lua:240-306 and
/// LC_DAL_Equipment.lua:400-515). 5023 holds the active group and up to three pending choices per slot;
/// 83220 pays RefiningSeasonTable._kDeplete and rolls _kAttrsId, 83221 activates one offered group,
/// and 83222 discards the offer. The group effects come from RefiningSeasonAttrConfig._kAttrs.
/// </summary>
public sealed class SeasonRefineService
{
    public const uint SeasonRefineSync = 5023, Recast = 83220, SelectAttr = 83221, GiveUp = 83222;

    private const int ChoiceCount = 3; // UI_Sys_StrengthenPropertyPanelCtrl.lua:27.
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly RoleStore _roles;
    private readonly ILogger<SeasonRefineService> _log;

    public SeasonRefineService(GameData data, CharacterService character, RoleStore roles, EquipmentService equipment,
        ILogger<SeasonRefineService> log)
    {
        equipment.Refined.Add(OnRefinedAsync);
        this._data = data;
        this._character = character;
        this._roles = roles;
        this._log = log;
        character.ExtraAttributes.Add(ActiveAttributes);
    }

    /// <summary>Load before the first character stat snapshot; the active groups are part of that snapshot.</summary>
    public void Load(GameSession session)
    {
        var saved = _roles.LoadSeasonRefine(session.Role!.Id);
        var state = saved == null ? new DBSeasonRefine() : DBSeasonRefine.Parser.ParseFrom(saved);
        foreach (var row in _data.Table("RefiningSeasonTable").Values)
        {
            int part = (int)GameData.Int(row, "_iEquipType"), slot = (int)GameData.Int(row, "_iID");
            if (part <= 0 || slot <= 0) continue;
            if (!state.EquipParts.TryGetValue(part, out var equip)) state.EquipParts[part] = equip = new DBSeasonRefineEquipPart();
            if (!equip.RefineSlots.ContainsKey(slot)) equip.RefineSlots[slot] = new DBSeasonRefineSlot();
        }
        session.SeasonRefine = state;
    }

    /// <summary>5023 login sync after the season identity (5021).</summary>
    public async Task LoginAsync(GameSession session)
    {
        if (session.SeasonRefine == null) Load(session);
        var state = session.SeasonRefine!;
        if (session.SeasonState?.PlayBookId != 101 || _data.Table("RefiningSeasonTable").Count == 0 || state.EquipParts.Count == 0) return;
        var initial = state.Clone();
        if (RollFirstOffers(session, initial))
        {
            var transaction = _roles.NewTransaction();
            transaction.RoleBlob(session.Role!.Id, "season_refine", initial.ToByteArray());
            await _roles.CommitAsync(transaction, "season-refine/initial-offers", _ => session.SeasonRefine = initial);
        }
        state = session.SeasonRefine!;
        // Missing config: an empty 5023 makes the DAL call pairs(nil).
        // LC_DAL_Equipment._processSeasonRefine calls pairs(slot.canSelectedAttr) even when no offer exists.
        // An absent protobuf map decodes as nil. Seed each empty map with a wire-only zero, then delete it in
        // an increment: LC_PbHelper.ParseMapTypeIncrement creates the map before processing its del marker.
        // The transient zero is never stored or accepted as a selectable group (inferred login workaround).
        var wire = state.Clone();
        bool needsEmptyMap = false;
        foreach (var equip in wire.EquipParts.Values)
            foreach (var slot in equip.RefineSlots.Values)
                if (slot.CanSelectedAttr.Count == 0) { slot.CanSelectedAttr[0] = 0; needsEmptyMap = true; }
        await CharacterService.SyncAsync(session, SeasonRefineSync, wire);
        if (needsEmptyMap) await CharacterService.SyncAsync(session, SeasonRefineSync, state.Clone());
    }

    /// <summary>
    /// First offers. UI_Sys_StrengthenRefine_SeasonItemWidgetCtrl opens UI_Sys_StrengthenPropertyPanel for an unlocked,
    /// never-activated slot without any request, and the panel returns before drawing when the slot offers fewer than
    /// three groups (UI_Sys_StrengthenPropertyPanelCtrl._refreshView); 83220 exists only for activated slots. So every
    /// unlocked slot with no active group and no offer holds three free choices (verified). Rolling them at unlock time
    /// (refine level-up) and at load for already-unlocked slots is (inferred): the client only needs them present.
    /// </summary>
    private bool RollFirstOffers(GameSession session, DBSeasonRefine state)
    {
        bool changed = false;
        foreach (var (part, equip) in state.EquipParts)
            foreach (var (slotId, slot) in equip.RefineSlots)
            {
                if (slot.ActiveAttr != 0 || slot.CanSelectedAttr.Count != 0) continue;
                var (row, error) = Validate(session, part, slotId);
                if (error != 0 || row is not { } configured) continue;
                var offered = DrawGroups(configured);
                if (offered.Count != ChoiceCount) continue;
                foreach (int group in offered) slot.CanSelectedAttr[group] = 0;
                changed = true;
            }
        return changed;
    }

    /// <summary>A refine level-up may unlock slots: give them their first offer and sync 5023.</summary>
    private async Task OnRefinedAsync(GameSession session)
    {
        if (!Ready(session) || session.SeasonState!.PlayBookId != 101) return;
        using var _ = await CharacterService.Lock(session);
        var next = session.SeasonRefine!.Clone();
        if (!RollFirstOffers(session, next)) return;
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(session.Role!.Id, "season_refine", next.ToByteArray());
        await _roles.CommitAsync(transaction, "season-refine/refined-offers", _ => session.SeasonRefine = next);
        await SyncAsync(session);
    }

    /// <summary>Replay the same empty-map initialization as login, leaving the saved refine state untouched.</summary>
    public async Task ReplayAsync(GameSession session)
    {
        session.Synced.Remove(SeasonRefineSync);
        await LoginAsync(session);
    }

    /// <summary>
    /// 83220: roll three distinct configured groups by their relative _kAttrsId weights (inferred: the server's
    /// original draw/replacement policy is not recovered). An existing pending offer can be rerolled; the active
    /// group is retained until 83221. Payment and pending state share the inventory transaction.
    /// </summary>
    public async Task RecastAsync(GameSession session, SeasonRefineRecastReq req)
    {
        if (!Ready(session))
        {
            _log.LogWarning("Season recast before role selection by {Account}", session.Account);
            await session.SendGameAsync(Recast, new SeasonRefineRecastResp { EquipType = req.EquipType, Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        using var _ = await CharacterService.Lock(session);
        var (row, result) = Validate(session, req.EquipType, req.RefineId);
        var cost = row is { } configured ? Costs(configured) : null;
        if (result == 0 && cost == null) result = ErrorCodes.SeasonRefineMaterials;
        var offered = row is { } source ? DrawGroups(source) : [];
        if (result == 0 && offered.Count != ChoiceCount) result = ErrorCodes.SeasonRefineConfig;
        if (result == 0)
        {
            var next = session.SeasonRefine!.Clone();
            var slot = next.EquipParts[req.EquipType].RefineSlots[req.RefineId];
            slot.CanSelectedAttr.Clear();
            // The DAL uses map keys as group IDs; its values are ignored. Zero payload is (inferred).
            foreach (int group in offered) slot.CanSelectedAttr[group] = 0;
            var economy = _character.ProgressionEconomy(session, "cd13c/season-refine-recast", cost!, [],
                new { req.EquipType, req.RefineId, Offers = offered.ToArray() });
            var paid = await _character.ExchangeLockedAsync(session, cost!, [], popup: false, sync: false,
                persist: tx => { tx.RoleBlob(session.Role!.Id, "season_refine", next.ToByteArray()); tx.WithEconomy(economy); },
                committed: () => session.SeasonRefine = next, asyncCommit: true);
            if (paid == null) result = _character.ShortageOf(session, cost!); // (inferred) ordinary item/currency shortage codes.
            else
            {
                await _character.ItemsChangedAsync(session, stats: false);
                await SyncAsync(session);
            }
        }
        _log.LogInformation("{Name} recasts season part {Part}, slot {Slot}: {Result}", session.Role?.Name ?? session.Account,
            req.EquipType, req.RefineId, result);
        await session.SendGameAsync(Recast, new SeasonRefineRecastResp { EquipType = req.EquipType, Result = result });
    }

    /// <summary>83221: only a group offered by 83220 for this exact part and slot may become active.</summary>
    public async Task SelectAttrAsync(GameSession session, SeasonRefineSelectAttrReq req)
    {
        if (!Ready(session))
        {
            _log.LogWarning("Season group selection before role selection by {Account}", session.Account);
            await session.SendGameAsync(SelectAttr, new SeasonRefineSelectAttrResp { EquipType = req.EquipType, Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        using var _ = await CharacterService.Lock(session);
        var (row, result) = Validate(session, req.EquipType, req.RefineId);
        if (result == 0 && !session.SeasonRefine!.EquipParts[req.EquipType].RefineSlots[req.RefineId]
            .CanSelectedAttr.ContainsKey(req.GroupId)) result = ErrorCodes.SeasonRefineChoice;
        if (result == 0 && (_data.Row("RefiningSeasonAttrConfig", req.GroupId) == null
            || !GameData.Pairs(row!.Value, "_kAttrsId").Any(p => p.Id == req.GroupId))) result = ErrorCodes.SeasonRefineConfig;
        if (result == 0)
        {
            var next = session.SeasonRefine!.Clone();
            var slot = next.EquipParts[req.EquipType].RefineSlots[req.RefineId];
            slot.ActiveAttr = req.GroupId;
            slot.CanSelectedAttr.Clear();
            var transaction = _roles.NewTransaction();
            transaction.RoleBlob(session.Role!.Id, "season_refine", next.ToByteArray());
            await _roles.CommitAsync(transaction, "season-refine/select", _ => session.SeasonRefine = next);
            await SyncAsync(session);
            await _character.RefreshStatsAsync(session);
        }
        _log.LogInformation("{Name} selects season group {Group} for {Part}/{Slot}: {Result}", session.Role?.Name ?? session.Account,
            req.GroupId, req.EquipType, req.RefineId, result);
        await session.SendGameAsync(SelectAttr, new SeasonRefineSelectAttrResp { EquipType = req.EquipType, Result = result });
    }

    /// <summary>83222: abandon this slot's pending offer, preserving any already active group.</summary>
    public async Task GiveUpAsync(GameSession session, SeasonRefineGiveUpReq req)
    {
        if (!Ready(session))
        {
            _log.LogWarning("Season offer giveup before role selection by {Account}", session.Account);
            await session.SendGameAsync(GiveUp, new SeasonRefineGiveUpResp { Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        using var _ = await CharacterService.Lock(session);
        var (_, result) = Validate(session, req.EquipType, req.RefineId);
        if (result == 0 && session.SeasonRefine!.EquipParts[req.EquipType].RefineSlots[req.RefineId]
            .CanSelectedAttr.Count == 0) result = ErrorCodes.Parameter; // (inferred) no matching empty-offer code.
        if (result == 0)
        {
            var next = session.SeasonRefine!.Clone();
            next.EquipParts[req.EquipType].RefineSlots[req.RefineId].CanSelectedAttr.Clear();
            var transaction = _roles.NewTransaction();
            transaction.RoleBlob(session.Role!.Id, "season_refine", next.ToByteArray());
            await _roles.CommitAsync(transaction, "season-refine/give-up", _ => session.SeasonRefine = next);
            await SyncAsync(session);
        }
        _log.LogInformation("{Name} gives up season offer for {Part}/{Slot}: {Result}", session.Role?.Name ?? session.Account,
            req.EquipType, req.RefineId, result);
        await session.SendGameAsync(GiveUp, new SeasonRefineGiveUpResp { Result = result });
    }

    private Task SyncAsync(GameSession session) =>
        CharacterService.SyncAsync(session, SeasonRefineSync, session.SeasonRefine!.Clone());

    private static bool Ready(GameSession session) => session.Role != null && session.Progress != null
        && session.SeasonRefine != null && session.SeasonState != null;

    private (JsonElement? Row, int Error) Validate(GameSession session, int part, int slot)
    {
        if (session.SeasonState?.PlayBookId != 101 || part is < 1 or > 7 || slot <= 0 || session.SeasonRefine == null)
            return (null, ErrorCodes.SeasonRefineConfig);
        if (!session.SeasonRefine.EquipParts.TryGetValue(part, out var equip) || !equip.RefineSlots.ContainsKey(slot))
            return (null, ErrorCodes.SeasonRefineConfig);
        var row = _data.Row("RefiningSeasonTable", slot);
        if (row is not { } r || GameData.Int(r, "_iEquipType") != part) return (null, ErrorCodes.SeasonRefineConfig);
        if (GameData.Int(r, "ilevel") > session.EquipLevels.Refine.GetValueOrDefault(part))
            return (null, ErrorCodes.SeasonRefineLevel);
        return (row, 0);
    }

    private static List<(int Entry, long Count)>? Costs(JsonElement row)
    {
        if (!row.TryGetProperty("_kDeplete", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) return null;
        var result = new List<(int, long)>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2
                || GameData.Number(item[0]) is not { } id || GameData.Number(item[1]) is not { } amount
                || id <= 0 || id > int.MaxValue || amount <= 0 || amount > long.MaxValue) return null;
            result.Add(((int)id, (long)amount));
        }
        return result;
    }

    private List<int> DrawGroups(JsonElement row)
    {
        // (inferred) The config weights are relative, and one offer has three different choices.
        var pool = GameData.Pairs(row, "_kAttrsId")
            .Where(p => p.Id > 0 && p.Id <= int.MaxValue && p.Value > 0 && _data.Row("RefiningSeasonAttrConfig", p.Id) != null)
            .GroupBy(p => (int)p.Id).Select(g => (Id: g.Key, Weight: g.Sum(p => p.Value))).ToList();
        var result = new List<int>();
        while (result.Count < ChoiceCount && pool.Count > 0)
        {
            double roll = Random.Shared.NextDouble() * pool.Sum(p => p.Weight);
            int index = 0;
            for (; index < pool.Count - 1 && roll >= pool[index].Weight; index++) roll -= pool[index].Weight;
            result.Add(pool[index].Id);
            pool.RemoveAt(index);
        }
        return result;
    }

    /// <summary>Only selected, unlocked groups contribute; pending choices never affect combat stats.</summary>
    public IEnumerable<(long Id, double Value)> ActiveAttributes(GameSession session) =>
        ActiveGroups(session).SelectMany(group => GameData.Pairs(group, "_kAttrs"));

    /// <summary>The RefiningSeasonAttrConfig rows of the selected, unlocked groups (stats and PowerService).</summary>
    internal IEnumerable<JsonElement> ActiveGroups(GameSession session)
    {
        if (session.SeasonState?.PlayBookId != 101 || session.SeasonRefine == null) yield break;
        foreach (var (part, equip) in session.SeasonRefine.EquipParts)
            foreach (var (slotId, slot) in equip.RefineSlots)
            {
                var (row, error) = Validate(session, part, slotId);
                if (slot.ActiveAttr <= 0 || error != 0 || row is not { } configured
                    || !GameData.Pairs(configured, "_kAttrsId").Any(p => p.Id == slot.ActiveAttr)
                    || _data.Row("RefiningSeasonAttrConfig", slot.ActiveAttr) is not { } group) continue;
                yield return group;
            }
    }
}
