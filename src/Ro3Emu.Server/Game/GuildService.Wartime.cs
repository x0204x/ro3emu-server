using System.Collections.Concurrent;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The war warehouse and the Tactics panel's war machine permissions (DBGuildData guildWartimeInfo,
/// docs/systems/territory-wars.md "War warehouse and machine permissions").
/// 32273 is the warehouse (machine item and count; LC_DAL_Guild.ReceiveWartimeStoreItemNtf feeds the War Manager page and
/// the siege battle bag). 32284 lists both machine types' operator slots, 32282 sets or clears one slot (roleGuildId 0)
/// and 32283 swaps two slots of a type (LC_SiegeHelper SendGetPermissionListReq / SendModifyPermissionReq /
/// SendExchangePermissionReq, UI_Sys_Siege_PermissionManageWidgetCtrl). Each change goes to the online members as 32286
/// (LC_DAL_Siege keys it by bagIndex, the list's 1-based position), and a member whose own machine type changed gets 32285
/// (SiegePermissionSelfData, which filters the battle bag to that type). Editing needs GuildPermissionConfig 13
/// War Machine Management, as the panel's Management button.
/// </summary>
public sealed partial class GuildService
{
    public const uint WartimeStore = 32273, ToolAssign = 32282, ToolExchange = 32283, ToolList = 32284,
        ToolPermission = 32285, ToolChange = 32286;
    public const uint Prepay = 32270, BuildCannon = 32271, CancelPrepay = 32272;

    /// <summary>
    /// Set by TerritoryWarsService: the side (1 attack, 2 defense) the role fights for in an active siege it is loaded
    /// into, 3 in an active territory battle of DungeonConfig _iGamePlayType 18, else 0. Machines are reserved and built
    /// only there (inferred: the battle bag that offers them, LC_WartimeItemHelper.TryBuildCannon, opens from the
    /// Instrument button of the siege's and the type-18 territory battle's button widgets).
    /// </summary>
    public Func<GameSession, int>? WarSideOf { get; set; }

    /// <summary>Set by TerritoryWarsService: places and raises the cannons of the siege a member fights in.</summary>
    public IWarMachineSite? WarMachines { get; set; }

    /// <summary>The siege side of building a cannon (TerritoryWarsService.WarMachines).</summary>
    public interface IWarMachineSite
    {
        /// <summary>0, or 530 (<see cref="ErrorCodes.GuildCannonPlace"/>) when the blueprint stands where the client
        /// would refuse it.</summary>
        int CheckPlace(GameSession builder, JsonElement cannon, Vec3 pos);
        /// <summary>The builder's 32702: BuildingCannon with <paramref name="state"/>, Idle when null.</summary>
        Task BuildStateAsync(GameSession builder, BuildCannonState? state);
        /// <summary>The cannon raised for a builder still fighting there; its unit id, or 0.</summary>
        Task<long> RaiseAsync(GameSession builder, JsonElement cannon, Vec3 pos, float face);
        /// <summary>The vehicle NPC <paramref name="npcId"/> raised beside its owner still fighting there; its unit id, or 0.</summary>
        Task<long> RaiseVehicleAsync(GameSession owner, int npcId);
        /// <summary>A cannon or vehicle of a running siege still stands.</summary>
        bool Standing(long machine);
        /// <summary>Destroys a standing cannon or vehicle; whoever is on it gets off first.</summary>
        Task RemoveAsync(long machine);
        /// <summary>The repair zone of a repair item's GVG_S0_RepairArea skill at the member's feet; false if it could not stand.</summary>
        Task<bool> RaiseRepairZoneAsync(GameSession user, int item, long skill);
    }

    /// <summary>
    /// A member's cannon or vehicle was destroyed in the siege: the slot shows nothing in use any more (32285 to the member,
    /// 32286 to the guild).
    /// </summary>
    internal async Task MachineGoneAsync(long role, long machine)
    {
        bool gone = _cannonOf.TryRemove(new KeyValuePair<long, long>(role, machine))
            | _vehicleOf.TryRemove(new KeyValuePair<long, long>(role, machine));
        if (!gone || store.ForRole(role) is not { } owner) return;
        await RunGuildAsync(null, [owner.Id], [], async () =>
        {
            if (store.Get(owner.Id) is not { } g || !g.Members.ContainsKey(role)) return;
            await PushToolPermissionAsync(g, role);
            await PushToolChangesAsync(g, [.. SlotsOf(g, role)]);
        });
    }

    /// <summary>Each member's standing cannon (the roleUsingToolId of its slot); runtime only, cannons end with the
    /// siege.</summary>
    private readonly ConcurrentDictionary<long, long> _cannonOf = new();
    /// <summary>Builds in progress: role to the build's token and the cannon item it used up.</summary>
    private readonly ConcurrentDictionary<long, (long Token, int Item)> _building = new();
    private long _buildSerial;

    /// <summary>The machine a member uses now with the machine type of its slot (the roleUsingToolId of that slot): its
    /// standing cannon on the Firearm page, its standing vehicle on the Vehicle page, else 0.</summary>
    private long MachineOf(long role, WartimeToolType type) => type switch
    {
        WartimeToolType.Firearm => _cannonOf.TryGetValue(role, out long cannon) && WarMachines?.Standing(cannon) == true ? cannon : 0,
        WartimeToolType.Vehicle => _vehicleOf.TryGetValue(role, out long vehicle) && WarMachines?.Standing(vehicle) == true ? vehicle : 0,
        _ => 0,
    };

    /// <summary>
    /// A member lost the slot they held (32282 cleared it or gave it to another member, or they left the guild): the war
    /// machine is taken from them. The Tactics panel asks first when the slot's holder has a machine (LC_SiegeHelper
    /// SendModifyPermissionReq, UIWords 61027 "Revoke Access", 61028 "This Member is currently using a war machine. Revoke
    /// their access?"). (inferred) Taking it destroys it as building another does (UIWords 61001): whoever is on it gets
    /// off first and nothing goes back to the warehouse. A machine left standing without a holder would stay the former
    /// operator's (interaction 4900005 checks CheckBuilder against the entity's owner, not the permission) or nobody's,
    /// and the client has no state for a machine without a slot. A reserved machine and a cannon still being built go
    /// back to the warehouse, as when the builder dies. Returns whether the warehouse changed (the caller saves it).
    /// </summary>
    private async Task<bool> TakeMachinesAsync(GuildStore.State g, long role)
    {
        bool stock = ReturnPrepay(g, role);
        var site = WarMachines;
        if (_building.TryRemove(role, out var build))
        {
            g.WartimeItems[build.Item] = g.WartimeItems.GetValueOrDefault(build.Item) + 1;
            stock = true;
            if (site != null && Online(role) is { } builder) await site.BuildStateAsync(builder, null);
        }
        if (_cannonOf.TryRemove(role, out long cannon) && site != null) await site.RemoveAsync(cannon);
        if (_vehicleOf.TryRemove(role, out long vehicle) && site != null) await site.RemoveAsync(vehicle);
        return stock;
    }
    private const int MachineRight = 13;

    /// <summary>(inferred) Operator slots per machine type: no config row sets them, and the panel shows as many as it
    /// receives (UI_Sys_Siege_PermissionManagePanelModel counts the list).</summary>
    internal const int FirearmSlots = 10, VehicleSlots = 10;

    private static readonly WartimeToolType[] ToolTypes =
        [WartimeToolType.Firearm, WartimeToolType.Vehicle];

    private static int SlotCount(WartimeToolType type) => type switch
    {
        WartimeToolType.Firearm => FirearmSlots,
        WartimeToolType.Vehicle => VehicleSlots,
        _ => 0,
    };

    private static bool ValidSlot(WartimeToolType type, int index) => index >= 1 && index <= SlotCount(type);

    /// <summary>The role in a slot; a member who left holds nothing.</summary>
    private static long Holder(GuildStore.State g, WartimeToolType type, int index) =>
        g.WartimeSlots.TryGetValue((int)type, out var slots) && slots.TryGetValue(index, out long role)
            && g.Members.ContainsKey(role) ? role : 0;

    /// <summary>The machine type a member operates (a member holds at most one slot), or None.</summary>
    internal static WartimeToolType ToolOf(GuildStore.State g, long role) => role == 0 || !g.Members.ContainsKey(role)
        ? WartimeToolType.None
        : ToolTypes.FirstOrDefault(type => g.WartimeSlots.TryGetValue((int)type, out var slots)
            && slots.Any(p => p.Value == role && ValidSlot(type, p.Key)));

    /// <summary>
    /// The warehouse: the guild's own stock once something changed it, else PublicParamConfig SiegeInitItemList
    /// [[item, count], ...] (inferred: a guild starts with that list; the name and the client say no more).
    /// </summary>
    internal IReadOnlyDictionary<int, int> WartimeStock(GuildStore.State g)
    {
        if (g.WartimeStocked) return g.WartimeItems;
        var stock = new SortedDictionary<int, int>();
        if (data.Table("PublicParamConfig").TryGetValue("SiegeInitItemList", out var list)
            && list.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var pair in list.EnumerateArray())
                if (pair.ValueKind == System.Text.Json.JsonValueKind.Array && pair.GetArrayLength() == 2
                    && GameData.Number(pair[0]) is { } item && GameData.Number(pair[1]) is { } count && count > 0)
                    stock[(int)item] = stock.GetValueOrDefault((int)item) + (int)count;
        return stock;
    }

    /// <summary>The GVGCannonConfig row building cannon item <paramref name="item"/> (_iItemID): the machines 32270
    /// reserves.</summary>
    private JsonElement? CannonRow(int item) => item <= 0 ? null
        : data.Table("GVGCannonConfig").Values.Where(r => new GVGCannonConfig(r).ItemId == item)
            .Select(r => (JsonElement?)r).FirstOrDefault();

    private bool IsCannonItem(int item) => CannonRow(item) != null;

    /// <summary>The guild's own stock from now on: the initial list becomes its starting stock (inferred).</summary>
    private void Stock(GuildStore.State g)
    {
        if (g.WartimeStocked) return;
        g.WartimeItems = new Dictionary<int, int>(WartimeStock(g));
        g.WartimeStocked = true;
    }

    /// <summary>A member's reserved machine back into the warehouse; false when there was none.</summary>
    private static bool ReturnPrepay(GuildStore.State g, long role)
    {
        if (!g.WartimePrepay.Remove(role, out int item)) return false;
        g.WartimeItems[item] = g.WartimeItems.GetValueOrDefault(item) + 1;
        return true;
    }

    /// <summary>32273 with the current warehouse to every online member.</summary>
    private async Task PushStoreAsync(GuildStore.State g)
    {
        var ntf = StoreOf(g);
        foreach (var m in g.Members.Values)
            if (Online(m.RoleId) is { } peer) await peer.SendGameAsync(WartimeStore, ntf);
    }

    public Task PrepayAsync(GameSession s, GuildWartimeStorePrepayReq req) =>
        RunGuildAsync(s, [], [], () => PrepayCoreAsync(s, req));
    public Task CancelPrepayAsync(GameSession s) => RunGuildAsync(s, [], [], () => CancelPrepayCoreAsync(s));

    /// <summary>
    /// 32270, the cannon item clicked in the siege battle bag: one machine leaves the warehouse for the member, and the
    /// answer's prepayItem starts the blueprint (LC_DAL_Guild.ReceiveWartimeStoreItemPrepay -> DoPrepayAction ->
    /// LC_GVGBuildCannon.StartBuild); a refusal shows its code instead (LC_AssetManager DecodeError). (inferred) The
    /// member needs the Firearm permission of the Tactics panel (the battle bag only lists that type, 32285), the item a
    /// cannon of GVGCannonConfig, stock left (535 "Not enough guild supplies") and a running siege; a reservation it
    /// already holds goes back first, and it always reserves one machine.
    /// </summary>
    private async Task PrepayCoreAsync(GameSession s, GuildWartimeStorePrepayReq req)
    {
        var g = Own(s);
        int item = req.Item?.ItemId ?? 0;
        int code = g == null ? ErrorCodes.TerritoryGuild
            : WarSideOf?.Invoke(s) is not > 0 ? ErrorCodes.FeatureUnavailable
            : !IsCannonItem(item) ? ErrorCodes.Parameter
            : ToolOf(g, s.Role!.Id) != WartimeToolType.Firearm ? ErrorCodes.GuildPermission : 0;
        if (code == 0)
        {
            Stock(g!);
            long role = s.Role!.Id;
            int available = g!.WartimeItems.GetValueOrDefault(item) + (g.WartimePrepay.GetValueOrDefault(role) == item ? 1 : 0);
            if (available <= 0) code = ErrorCodes.GuildSupplies;
            else
            {
                ReturnPrepay(g, role);
                g.WartimeItems[item]--;
                g.WartimePrepay[role] = item;
            }
        }
        if (code != 0)
        {
            await s.SendGameAsync(Prepay, new GuildWartimeStorePrepayResp { Result = code });
            return;
        }
        await SaveGuildAsync(g!);
        await s.SendGameAsync(Prepay, new GuildWartimeStorePrepayResp
        { PrepayItem = new GuildWartimeItem { ItemId = item, ItemCount = 1 } });
        await PushStoreAsync(g!);
    }

    /// <summary>
    /// 32272, Esc on the blueprint (LC_GVGBuildCannon.onClickEsc -> LC_WartimeItemHelper.CancelPrepay): the reserved
    /// machine goes back. Nothing reserved is not an error (inferred): the client sends it without checking and has no
    /// handler for the answer, so a refusal would only pop an error up.
    /// </summary>
    private async Task CancelPrepayCoreAsync(GameSession s)
    {
        var g = Own(s);
        if (g == null)
        {
            await s.SendGameAsync(CancelPrepay, new GuildWartimeStoreItemCancelUseResp { Result = ErrorCodes.TerritoryGuild });
            return;
        }
        int item = g.WartimePrepay.GetValueOrDefault(s.Role!.Id);
        if (!ReturnPrepay(g, s.Role.Id))
        {
            await s.SendGameAsync(CancelPrepay, new GuildWartimeStoreItemCancelUseResp());
            return;
        }
        await SaveGuildAsync(g);
        await s.SendGameAsync(CancelPrepay, new GuildWartimeStoreItemCancelUseResp
        { Item = new GuildWartimeItem { ItemId = item, ItemCount = 1 } });
        await PushStoreAsync(g);
    }

    public Task BuildCannonAsync(GameSession s, GuildAttackCityBuildReq req) =>
        RunGuildAsync(s, [], [], () => BuildCannonCoreAsync(s, req));

    /// <summary>
    /// 32271, the blueprint confirmed (LC_GVGBuildCannon.Build: its position and facing). The reserved machine is used
    /// up and the answer closes the blueprint (LC_DunGeon_Siege.GUILD_ATTACK_CITY_BUILD); the builder then works for
    /// GVGCannonConfig _iBuildTime in 32702 BuildingCannon (LC_GamePlayStatusMgr.BuildingCannon, GameplayStatusConfig
    /// _iBuildStatusID 1), and the cannon stands after it. A cannon the member already had is destroyed (UIWords 61001,
    /// "Building another will destroy the current one"). Refusals: 6001, 28 outside a siege, 82 without a reserved
    /// machine or while building, 530 for a spot the client would refuse.
    /// </summary>
    private async Task BuildCannonCoreAsync(GameSession s, GuildAttackCityBuildReq req)
    {
        var g = Own(s);
        long role = s.Role!.Id;
        int item = g?.WartimePrepay.GetValueOrDefault(role) ?? 0;
        var cannon = CannonRow(item);
        var site = WarMachines;
        int code = g == null ? ErrorCodes.TerritoryGuild
            : WarSideOf?.Invoke(s) is not > 0 || site == null ? ErrorCodes.FeatureUnavailable
            : cannon == null || req.Pos == null || _building.ContainsKey(role) ? ErrorCodes.Parameter
            : site.CheckPlace(s, cannon.Value, req.Pos);
        if (code != 0)
        {
            await s.SendGameAsync(BuildCannon, new GuildAttackCityBuildResp { Result = code });
            return;
        }
        g!.WartimePrepay.Remove(role);
        await SaveGuildAsync(g);
        await s.SendGameAsync(BuildCannon, new GuildAttackCityBuildResp { Item = new GuildWartimeItem { ItemId = item, ItemCount = 1 } });
        var row = cannon!.Value;
        long token = Interlocked.Increment(ref _buildSerial);
        _building[role] = (token, item);
        long buildMs = Math.Max(0, new GVGCannonConfig(row).BuildTime);
        await site!.BuildStateAsync(s, new BuildCannonState
        {
            Progress = BuildCannonState.Types.EBuildProcess.Building, GvgCannonConfigId = new GVGCannonConfig(row).Id,
            CompleteTime = NowMs() + buildMs,
        });
        var pos = req.Pos!.Clone();
        float face = req.Face;
        long guild = g.Id;
        loop.ScheduleRuntime(TimeSpan.FromMilliseconds(buildMs), () => FinishCannonAsync(s, guild, role, token, row, pos, face));
    }

    /// <summary>
    /// The build time is over: the cannon stands for a builder still alive in the siege, the member's slot shows it
    /// (roleUsingToolId in 32285 and 32286) and the cannon it had before is destroyed. (inferred) A builder who died or
    /// left meanwhile gets nothing and the machine goes back to the warehouse.
    /// </summary>
    private async Task FinishCannonAsync(GameSession s, long guild, long role, long token, JsonElement row, Vec3 pos, float face)
    {
        if (!_building.TryGetValue(role, out var current) || current.Token != token) return;
        _building.TryRemove(role, out _);
        var site = WarMachines!;
        bool here = s.Role?.Id == role && !s.Dead && WarSideOf?.Invoke(s) > 0;
        long uid = here ? await site.RaiseAsync(s, row, pos, face) : 0;
        if (here) await site.BuildStateAsync(s, null);
        await RunGuildAsync(null, [guild], [], async () =>
        {
            var g = store.Get(guild);
            if (g == null || !g.Members.ContainsKey(role))
            {
                if (uid != 0) await site.RemoveAsync(uid);
                return;
            }
            if (uid != 0 && ToolOf(g, role) != WartimeToolType.Firearm)
            {
                // The Firearm permission was revoked while the cannon was raised: as a build cut short.
                await site.RemoveAsync(uid);
                uid = 0;
            }
            if (uid == 0)
            {
                int item = (int)new GVGCannonConfig(row).ItemId;
                g.WartimeItems[item] = g.WartimeItems.GetValueOrDefault(item) + 1;
                await SaveGuildAsync(g);
                await PushStoreAsync(g);
                return;
            }
            if (_cannonOf.TryGetValue(role, out long old) && old != uid) await site.RemoveAsync(old);
            _cannonOf[role] = uid;
            await PushToolPermissionAsync(g, role);
            await PushToolChangesAsync(g, [.. SlotsOf(g, role)]);
        });
    }

    /// <summary>The slots a member holds (at most one).</summary>
    private static IEnumerable<(WartimeToolType Type, int Index)> SlotsOf(GuildStore.State g, long role) =>
        g.WartimeSlots.SelectMany(p => p.Value.Where(slot => slot.Value == role && ValidSlot((WartimeToolType)p.Key, slot.Key))
            .Select(slot => ((WartimeToolType)p.Key, slot.Key)));

    private GuildWartimeStoreItemNtf StoreOf(GuildStore.State? g)
    {
        var ntf = new GuildWartimeStoreItemNtf();
        if (g != null)
            ntf.WartimeItems.AddRange(WartimeStock(g).Where(p => p.Value > 0).OrderBy(p => p.Key)
                .Select(p => new GuildWartimeItem { ItemId = p.Key, ItemCount = p.Value }));
        return ntf;
    }

    private GetGuildWartimeToolBelongListRsp ToolListOf(GuildStore.State g)
    {
        var rsp = new GetGuildWartimeToolBelongListRsp();
        foreach (var type in ToolTypes)
        {
            var page = new GuildWartimeItemPermissionClient { ToolType = type };
            for (int index = 1; index <= SlotCount(type); index++)
            {
                long holder = Holder(g, type, index);
                page.InfoList.Add(new GuildWartimeToolBelongInfo { RoleGuildId = holder, RoleUsingToolId = MachineOf(holder, type) });
            }
            rsp.ItemPermissionAllData.Add(page);
        }
        return rsp;
    }

    /// <summary>A member's warehouse view and own permission, with the full guild info (login, join, refresh).</summary>
    private async Task SendWartimeAsync(GameSession s, GuildStore.State g)
    {
        await s.SendGameAsync(WartimeStore, StoreOf(g));
        if (s.Role != null && ToolOf(g, s.Role.Id) is var type && type != WartimeToolType.None)
            await s.SendGameAsync(ToolPermission,
                new GuildWartimeToolRolePermissionNtf { ToolType = type, RoleUsingToolId = MachineOf(s.Role.Id, type) });
    }

    /// <summary>A former member: an empty warehouse and no permission (the battle bag empties).</summary>
    private static async Task ClearWartimeAsync(GameSession s)
    {
        await s.SendGameAsync(WartimeStore, new GuildWartimeStoreItemNtf());
        await s.SendGameAsync(ToolPermission, new GuildWartimeToolRolePermissionNtf());
    }

    public Task ToolListAsync(GameSession s) => RunGuildAsync(s, [], [], () => ToolListCoreAsync(s));
    public Task ToolAssignAsync(GameSession s, GuildWartimeToolBelongOperReq req) =>
        RunGuildAsync(s, [], [], () => ToolAssignCoreAsync(s, req));
    public Task ToolExchangeAsync(GameSession s, GuildWartimeToolExchangeOperReq req) =>
        RunGuildAsync(s, [], [], () => ToolExchangeCoreAsync(s, req));

    /// <summary>32284: both pages (any member may read them; the guild window's War Machine page does too).</summary>
    private Task ToolListCoreAsync(GameSession s) => s.SendGameAsync(ToolList,
        Own(s) is { } g ? ToolListOf(g) : new GetGuildWartimeToolBelongListRsp { Result = ErrorCodes.TerritoryGuild });

    /// <summary>
    /// 32282: put a member in a slot or clear it (roleGuildId 0). dungeonId is the siege's (enum_DungeonIdMap) and does not
    /// change the guild-wide slots (inferred). The client offers only members without a slot; anyone else is 82 (inferred).
    /// The member who held the slot loses their machine (<see cref="TakeMachinesAsync"/>), so 32285/32286 show nothing in
    /// use and no later page shows it under another machine type.
    /// </summary>
    private async Task ToolAssignCoreAsync(GameSession s, GuildWartimeToolBelongOperReq req)
    {
        var g = Own(s);
        long target = req.RoleGuildId;
        int code = g == null ? ErrorCodes.TerritoryGuild : !HasRight(g, s.Role!.Id, MachineRight) ? ErrorCodes.GuildPermission
            : !ValidSlot(req.ToolType, req.BagIndex) ? ErrorCodes.Parameter
            : target != 0 && !g.Members.ContainsKey(target) ? ErrorCodes.GuildMemberMissing
            : target != 0 && target != Holder(g, req.ToolType, req.BagIndex)
                && ToolOf(g, target) != WartimeToolType.None ? ErrorCodes.Parameter : 0;
        var rsp = new GuildWartimeToolBelongOperRsp
        { Result = code, ToolType = req.ToolType, BagIndex = req.BagIndex, RoleGuildId = target };
        long before = code == 0 ? Holder(g!, req.ToolType, req.BagIndex) : 0;
        if (code != 0 || before == target)
        {
            await s.SendGameAsync(ToolAssign, rsp);
            return;
        }
        if (!g!.WartimeSlots.TryGetValue((int)req.ToolType, out var slots)) g.WartimeSlots[(int)req.ToolType] = slots = [];
        if (target == 0) slots.Remove(req.BagIndex); else slots[req.BagIndex] = target;
        bool stock = before != 0 && await TakeMachinesAsync(g, before);
        await SaveGuildAsync(g);
        await s.SendGameAsync(ToolAssign, rsp);
        await PushToolChangesAsync(g, [(req.ToolType, req.BagIndex)]);
        await PushToolPermissionAsync(g, before);
        await PushToolPermissionAsync(g, target);
        if (stock) await PushStoreAsync(g);
    }

    /// <summary>32283: swap two slots of one type (dragging a name onto another slot); an empty slot moves the name.</summary>
    private async Task ToolExchangeCoreAsync(GameSession s, GuildWartimeToolExchangeOperReq req)
    {
        var g = Own(s);
        int code = g == null ? ErrorCodes.TerritoryGuild : !HasRight(g, s.Role!.Id, MachineRight) ? ErrorCodes.GuildPermission
            : !ValidSlot(req.ToolType, req.SrcBagIndex) || !ValidSlot(req.ToolType, req.TargetBagIndex)
                || req.SrcBagIndex == req.TargetBagIndex ? ErrorCodes.Parameter : 0;
        var rsp = new GuildWartimeToolExchangeOperRsp
        { Result = code, ToolType = req.ToolType, SrcBagIndex = req.SrcBagIndex, TargetBagIndex = req.TargetBagIndex };
        long source = code == 0 ? Holder(g!, req.ToolType, req.SrcBagIndex) : 0;
        long destination = code == 0 ? Holder(g!, req.ToolType, req.TargetBagIndex) : 0;
        if (code != 0 || source == destination)
        {
            await s.SendGameAsync(ToolExchange, rsp);
            return;
        }
        if (!g!.WartimeSlots.TryGetValue((int)req.ToolType, out var slots)) g.WartimeSlots[(int)req.ToolType] = slots = [];
        Place(slots, req.SrcBagIndex, destination);
        Place(slots, req.TargetBagIndex, source);
        await SaveGuildAsync(g);
        await s.SendGameAsync(ToolExchange, rsp);
        await PushToolChangesAsync(g, [(req.ToolType, req.SrcBagIndex), (req.ToolType, req.TargetBagIndex)]);
    }

    private static void Place(Dictionary<int, long> slots, int index, long role)
    {
        if (role == 0) slots.Remove(index); else slots[index] = role;
    }

    /// <summary>A member left: every slot they held is freed. Returns the freed slots for <see cref="PushToolChangesAsync"/>.</summary>
    private static List<(WartimeToolType Type, int Index)> DropWartime(GuildStore.State g, long role)
    {
        var freed = new List<(WartimeToolType, int)>();
        foreach (var (type, slots) in g.WartimeSlots)
        {
            int[] held = [.. slots.Where(p => p.Value == role).Select(p => p.Key)];
            foreach (int index in held)
            {
                slots.Remove(index);
                if (ValidSlot((WartimeToolType)type, index)) freed.Add(((WartimeToolType)type, index));
            }
        }
        return freed;
    }

    /// <summary>32286 with the slots' current holders to every online member.</summary>
    private async Task PushToolChangesAsync(GuildStore.State g, List<(WartimeToolType Type, int Index)> changed)
    {
        if (changed.Count == 0) return;
        var ntf = new GuildWartimeToolBelongInfoChangeNtf();
        ntf.InfoList.AddRange(changed.Select(c => new GuildWartimeToolBelongDetailInfo
        {
            ToolType = c.Type, BagIndex = c.Index,
            ToolBelongInfo = new GuildWartimeToolBelongInfo
            {
                RoleGuildId = Holder(g, c.Type, c.Index), RoleUsingToolId = MachineOf(Holder(g, c.Type, c.Index), c.Type),
            },
        }));
        foreach (var m in g.Members.Values)
            if (Online(m.RoleId) is { } peer) await peer.SendGameAsync(ToolChange, ntf);
    }

    /// <summary>32285 to an online member with the machine type they may now use (None when revoked).</summary>
    private async Task PushToolPermissionAsync(GuildStore.State g, long role)
    {
        if (role == 0 || Online(role) is not { } peer) return;
        var type = ToolOf(g, role);
        await peer.SendGameAsync(ToolPermission,
            new GuildWartimeToolRolePermissionNtf { ToolType = type, RoleUsingToolId = MachineOf(role, type) });
    }
}
