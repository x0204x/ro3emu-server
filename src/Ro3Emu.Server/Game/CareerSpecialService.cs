using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Special Class Items (FunctionConfig 16, the Profession panel): MG_CareerSpecial 32721/32722 and AUTO_SYNC 5002
/// DBCareerSpecial. A lease is free and consumes nothing: the panel lists every CartRemouldTable row whose
/// _kProfessionID holds the career (LC_EquipmentHelper.GetProfessionItems, lua-src Logic/Equipment/LC_EquipmentHelper.lua:1603-1690)
/// and offers Rent when CanWearProfessionItem holds (career, _iUnlockBaseLv, learned _kUnlockSkillID group; :1692-1801).
/// _iType 1 fills the pendant slot (carts, falcon), _iType 2 the mount slot (Peco Peco, dragon, gryphon, Madogear, wolf).
/// Others see a leased pendant through EntityBase.pendantInfo / 31015 (PendantConfig id _iPendantId) and a ridden
/// mount through EntityBase.curRider (a CartRemouldTable id is a Battle mount, LC_MountHelper.GetMountType).
/// Server behaviour the client does not show (refusal codes, repair, the battle slot) is marked inferred where it is.
/// </summary>
public sealed class CareerSpecialService
{
    public const uint Sync = 5002, Lease = 32721, CancelLease = 32722;
    // ErrorCodeConfig 1101-1111, the special Job item family.
    public const int ErrMissing = 1101, ErrLevel = 1102, ErrJob = 1103, ErrQuest = 1104, ErrSkill = 1105;
    public const int ErrNotLeased = 1107, ErrCartNotEmpty = 1111;
    private const int Function = 16, PendantType = 1, MountType = 2, CartSubType = 1, CartPack = 100, BattleSlotKey = 2;

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly SkillService _skills;
    private readonly FunctionService _functions;
    private readonly MountService _mounts;
    private readonly RoleStore _roles;
    private readonly World _world;
    private readonly GameLoop _loop;

    public CareerSpecialService(GameData data, CharacterService character, SkillService skills, FunctionService functions,
        MountService mounts, RoleStore roles, World world, GameLoop loop)
    {
        this._data = data; this._character = character; this._skills = skills; this._functions = functions;
        this._mounts = mounts; this._roles = roles; this._world = world; this._loop = loop;
        // A job change can leave a lease the new career may not hold (GM !job, or a line without the item; inferred).
        character.JobChanged.Add(ReconcileAsync);
    }

    /// <summary>The mount slot's lease entitles MountService's battle slot 2.</summary>
    public static bool IsLeasedMount(GameSession s, long id) => id != 0 && s.CareerSpecial?.MountsSlot?.CartRemouldCfg == id;

    /// <summary>TaskConfig target 309003 RentCareerSpecialSubType [309003, _iSubType]: a lease of that subtype is held now.</summary>
    public static bool HoldsSubType(GameSession s, GameData data, long subType) =>
        s.CareerSpecial is { } info && new[] { info.PendantSlot?.CartRemouldCfg ?? 0, info.MountsSlot?.CartRemouldCfg ?? 0 }
            .Any(id => id != 0 && data.Row("CartRemouldTable", id) is { } row && GameData.Int(row, "_iSubType") == subType);

    /// <summary>EntityBase.pendantInfo of the leased items (self and AOI). Piles 1 is inferred: these rows have _iMaxLayer 0.</summary>
    public static void PopulateEntity(GameSession s, EntityBase entity)
    {
        foreach (int pendant in s.CareerPendants) entity.PendantInfo[pendant] = new PendantData { PendantId = pendant, Piles = 1 };
    }

    // ------------------------------------------------------------------ load / 5002

    /// <summary>Before MountService.Load: its battle slot entitlement reads the lease.</summary>
    public void Load(GameSession s)
    {
        var bytes = _roles.LoadCareerSpecial(s.Role!.Id);
        var info = bytes is { Length: > 0 } ? DBCareerSpecial.Parser.ParseFrom(bytes) : new DBCareerSpecial();
        Shape(info);
        if (Repair(s, info)) _roles.SaveCareerSpecial(s.Role.Id, info.ToByteArray());
        s.CareerSpecial = info;
        s.CareerPendants = Pendants(info);
    }

    /// <summary>5002: the equipment window reads SpecialPropData.pendantSlot/mountsSlot unguarded, so it always goes out.</summary>
    public Task LoginAsync(GameSession s) => CharacterService.SyncAsync(s, Sync, s.CareerSpecial!.Clone());

    // Both slots always present: the Lua handlers compare cartRemouldCfg with 0 (LC_DAL_Equipment.lua:515-560).
    private static void Shape(DBCareerSpecial info)
    {
        info.PendantSlot ??= new DBCareerSpecialPendantSlot();
        info.MountsSlot ??= new DBCareerSpecialMountsSlot();
    }

    private JsonElement? Row(long id) => id == 0 ? null : _data.Row("CartRemouldTable", id);

    private int[] Pendants(DBCareerSpecial info) => new[] { info.PendantSlot.CartRemouldCfg, info.MountsSlot.CartRemouldCfg }
        .Select(id => Row(id) is { } row ? (int)GameData.Int(row, "_iPendantId") : 0)
        .Where(p => p != 0 && _data.Row("PendantConfig", p) != null).Distinct().ToArray();

    /// <summary>Who may lease: LC_EquipmentHelper.CanWearProfessionItem, plus the unused _iTaskId (error 1104).</summary>
    private int Entitlement(GameSession s, JsonElement? found)
    {
        if (found is not { } row) return ErrMissing;
        var p = s.Progress!;
        bool job = row.TryGetProperty("_kProfessionID", out var jobs) && jobs.ValueKind == JsonValueKind.Array
            && jobs.EnumerateArray().Any(v => GameData.Number(v) == p.Career);
        if (!job) return ErrJob;
        if (p.Level < GameData.Int(row, "_iUnlockBaseLv")) return ErrLevel;
        if (GameData.Int(row, "_iTaskId") is var task and > 0 && !TaskService.IsDelivered(s, task)) return ErrQuest;
        long skill = GameData.Int(row, "_kUnlockSkillID");
        if (skill > 0 && (_data.Row("SkillConfig", skill) is not { } config
            || _skills.Level(p, (int)GameData.Int(config, "_iSkillID")) <= 0)) return ErrSkill;
        return 0;
    }

    /// <summary>Clears a slot the role no longer qualifies for (inferred). True when anything changed.</summary>
    private bool Repair(GameSession s, DBCareerSpecial info)
    {
        bool changed = false;
        if (info.PendantSlot.CartRemouldCfg != 0 && (Entitlement(s, Row(info.PendantSlot.CartRemouldCfg)) != 0
            || GameData.Int(Row(info.PendantSlot.CartRemouldCfg)!.Value, "_iType") != PendantType))
        { info.PendantSlot.CartRemouldCfg = 0; changed = true; }
        if (info.MountsSlot.CartRemouldCfg != 0 && (Entitlement(s, Row(info.MountsSlot.CartRemouldCfg)) != 0
            || GameData.Int(Row(info.MountsSlot.CartRemouldCfg)!.Value, "_iType") != MountType))
        { info.MountsSlot.CartRemouldCfg = 0; changed = true; }
        return changed;
    }

    private static bool CartHasItems(GameSession s) => s.Items.Any(i => i.Grid == 0 && i.Pack == CartPack && i.Stack > 0);

    private bool IsCart(long id) => Row(id) is { } row && GameData.Int(row, "_iSubType") == CartSubType;

    // ------------------------------------------------------------------ 32721 / 32722

    /// <summary>32721: the item goes into its slot, replacing a previous lease there. Nothing is consumed.</summary>
    public async Task LeaseAsync(GameSession s, LeaseCareerSpecialItemReq req)
    {
        using var gate = await CharacterService.Lock(s);
        long id = req.CartRemouldCfgId;
        var row = Row(id);
        int type = row is { } r ? (int)GameData.Int(r, "_iType") : 0;
        int result = s.CareerSpecial == null ? ErrorCodes.FeatureUnavailable
            : row == null || type is not (PendantType or MountType) ? ErrMissing
            : !_functions.IsOpen(s, Function) ? ErrorCodes.FunctionLocked
            : Entitlement(s, row);
        long previous = result != 0 ? 0 : type == PendantType ? s.CareerSpecial!.PendantSlot.CartRemouldCfg : s.CareerSpecial!.MountsSlot.CartRemouldCfg;
        // Trading the cart for a non-cart would strand pack 100 (the client's own 23034 rule, inferred for a swap).
        if (result == 0 && previous != id && IsCart(previous) && !IsCart(id) && CartHasItems(s)) result = ErrCartNotEmpty;
        bool changed = result == 0 && previous != id;
        var shown = s.CareerPendants;
        if (changed)
        {
            var next = s.CareerSpecial!.Clone();
            if (type == PendantType) next.PendantSlot.CartRemouldCfg = id;
            else next.MountsSlot.CartRemouldCfg = id;
            await CommitAsync(s, next, "lease", type == MountType ? BattleSlot(s, id, previous) : null);
        }
        await s.SendGameAsync(Lease, new LeaseCareerSpecialItemRsp { ErrCode = result });
        if (changed) await PublishAsync(s, type == MountType, shown);
    }

    /// <summary>32722: empties the slot holding that item. A cart with items in pack 100 stays (error 1111, client 23034).</summary>
    public async Task CancelAsync(GameSession s, CancelLeaseCareerSpecialItemReq req)
    {
        using var gate = await CharacterService.Lock(s);
        long id = req.CartRemouldCfgId;
        var row = Row(id);
        int type = row is { } r ? (int)GameData.Int(r, "_iType") : 0;
        long held = s.CareerSpecial == null ? 0 : type == PendantType ? s.CareerSpecial.PendantSlot.CartRemouldCfg
            : type == MountType ? s.CareerSpecial.MountsSlot.CartRemouldCfg : 0;
        int result = s.CareerSpecial == null ? ErrorCodes.FeatureUnavailable
            : row == null || type is not (PendantType or MountType) ? ErrMissing
            : held != id ? ErrNotLeased
            : IsCart(id) && CartHasItems(s) ? ErrCartNotEmpty : 0;
        var shown = s.CareerPendants;
        if (result == 0)
        {
            var next = s.CareerSpecial!.Clone();
            if (type == PendantType) next.PendantSlot.CartRemouldCfg = 0;
            else next.MountsSlot.CartRemouldCfg = 0;
            await CommitAsync(s, next, "cancel", type == MountType ? BattleSlot(s, 0, id) : null);
        }
        await s.SendGameAsync(CancelLease, new CancelLeaseCareerSpecialItemRsp { ErrCode = result });
        if (result == 0) await PublishAsync(s, type == MountType, shown);
    }

    /// <summary>
    /// 5012 battle slot 2 follows the mount lease: the HUD job-mount button (UI_Sys_Main_MountJobWidgetCtrl) and 32745
    /// tp 2/3 read ActivationMounts[Battle], which no client request selects (32743 omits tp). A new lease starts unmounted
    /// (inferred). An owned CartRemouldTable mount (item action 201) keeps its own selection. Null: 5012 unchanged.
    /// </summary>
    private DBMountsInfo? BattleSlot(GameSession s, long lease, long previous)
    {
        if (s.Mounts == null) return null;
        var selected = s.Mounts.MountIds.GetValueOrDefault(BattleSlotKey);
        if (lease == 0 && (selected == null || selected.MountId != previous || s.Mounts.Mounts.ContainsKey(previous))) return null;
        var next = s.Mounts.Clone();
        next.MountIds[BattleSlotKey] = new MountInfo { MountId = lease, IsRiding = false };
        return next;
    }

    private async Task CommitAsync(GameSession s, DBCareerSpecial next, string action, DBMountsInfo? nextMounts)
    {
        var tx = _roles.NewTransaction();
        tx.ItemDelta(s.Role!.Id, s.Items, s.Items);
        tx.RoleBlob(s.Role.Id, "career_special", next.ToByteArray());
        if (nextMounts != null) tx.RoleBlob(s.Role.Id, "mounts", _mounts.EncodeBlob(s, nextMounts));
        // Free and itemless: an empty family receipt (as MountService's own selection), no balance and no asset.
        tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/mount-career", [], [], new
        {
            Action = action, Pendant = next.PendantSlot.CartRemouldCfg, Mount = next.MountsSlot.CartRemouldCfg,
            Mounts = nextMounts == null ? null : Convert.ToHexString(SHA256.HashData(nextMounts.ToByteArray())),
        }));
        await _roles.CommitAsync(tx, "career-special/" + action, _ =>
        {
            s.CareerSpecial = next;
            s.CareerPendants = Pendants(next);
            if (nextMounts != null) s.Mounts = nextMounts;
        });
    }

    /// <summary>5002, then 5012 and the rider when the battle slot moved, the pendants to self and viewers, quest targets.</summary>
    private async Task PublishAsync(GameSession s, bool mountSlot, int[] shown)
    {
        await LoginAsync(s);
        if (mountSlot) await _mounts.PublishAsync(s);
        // A talent can depend on the leased pendant (Falcon's Might: SpecialEffect type 20 on the falcon's subtype).
        else await _character.RefreshStatsAsync(s);
        if (!shown.SequenceEqual(s.CareerPendants)) await PendantsAsync(s, shown);
        foreach (var handler in _character.StateChanged) handler(s);
    }

    /// <summary>
    /// 31015 PendantUpdate. LC_Pendant_Manager.UpdatePendantList skips a nil map, and lua-protobuf decodes an empty map
    /// as nil (no_decode_default_array), so a pendant that went away is sent hidden (isHide) rather than omitted.
    /// </summary>
    private async Task PendantsAsync(GameSession s, int[] shown)
    {
        var packet = new PendantUpdate { EntityID = s.Role!.Id };
        foreach (int pendant in s.CareerPendants) packet.PendantInfo[pendant] = new PendantData { PendantId = pendant, Piles = 1 };
        foreach (int gone in shown.Except(s.CareerPendants)) packet.PendantInfo[gone] = new PendantData { PendantId = gone, Piles = 1, IsHide = true };
        await s.SendGameAsync(WorldEffectsService.Pendant, packet);
        if (!s.Owners!.AllowsPresentation(s)) return;
        foreach (var viewer in _world.Viewers(s))
        {
            if (!s.Owners!.AllowsPresentation(s)) break;
            await viewer.SendGameAsync(WorldEffectsService.Pendant, packet);
        }
    }

    // ------------------------------------------------------------------ job change

    public Task ReconcileAsync(GameSession s) => _loop.IsOnLoop ? ReconcileCoreAsync(s) : _loop.InvokeAsync(() => ReconcileCoreAsync(s));

    private async Task ReconcileCoreAsync(GameSession s)
    {
        if (s.CareerSpecial == null || s.Role == null) return;
        using var gate = await CharacterService.Lock(s);
        if (s.CareerSpecial == null) return;
        var next = s.CareerSpecial.Clone();
        long oldMount = next.MountsSlot.CartRemouldCfg;
        if (!Repair(s, next)) return;
        bool mountSlot = oldMount != next.MountsSlot.CartRemouldCfg;
        var shown = s.CareerPendants;
        await CommitAsync(s, next, "repair", mountSlot ? BattleSlot(s, 0, oldMount) : null);
        await PublishAsync(s, mountSlot, shown);
    }
}
