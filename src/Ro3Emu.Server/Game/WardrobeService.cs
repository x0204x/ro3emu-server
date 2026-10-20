using System.Text.Json;
using System.Security.Cryptography;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The current 5034 wardrobe (MG_DB.proto), rather than the legacy 31181 view.
/// Collection, worn slots, attribute sources, five outfit plans and likes share one per-role DBWardRobeInfo.
/// WardRobeConfig supplies default ownership, slot, sex, material cost and disabled state. The 31183/31185 senders
/// are UI_Sys_WardrobePanelCtrl / UI_Sys_Wardrobe_PlanPanelCtrl; LC_DAL_Fashion receives 5034 and 31186.
/// </summary>
public sealed partial class WardrobeService
{
    private readonly GameData data;
    private readonly CharacterService character;
    private readonly RoleStore roles;
    private readonly World world;
    private readonly ILogger<WardrobeService> log;
    private readonly GameLoop loop;
    private readonly Deadlines deadlines;
    private readonly TimeProvider clock;
    private const string ExpiryKey = "wardrobe-expiry";

    public WardrobeService(GameData data, CharacterService character, RoleStore roles, World world,
        Deadlines deadlines, TimeProvider clock, GameLoop loop, SceneService scene, ILogger<WardrobeService> log)
    {
        this.data = data;
        this.character = character;
        this.roles = roles;
        this.world = world;
        this.log = log;
        this.loop = loop;
        this.deadlines = deadlines;
        this.clock = clock;
        character.SpecialItemUse.Add(TryUseAppearanceItemAsync);
        scene.BeforeSelf.Add(ReconcileAsync);
    }

    public const uint WardrobeSync = 5034, Use = 31183, UseAttr = 31184, SavePlan = 31185,
        ApplyPlan = 31186, SetLike = 31187, Redeem = 31188, UnlockNotice = 31189;
    private const int HairSlot = 7, MountSlot = 6, MaxSlot = 9, PlanCount = 5; // FashionMatchNum = 5.

    /// <summary>
    /// Fashion score, as LC_FashionHelper.UpdateFashionScore (:496-530) sums it: the _iFashionScore of every lit
    /// appearance owned for good (overdueTimeMs 0). The profile card shows it from Base.fashionScore; the Fashion
    /// Rating board ranks it.
    /// </summary>
    public static long FashionScore(GameData data, DBWardRobeInfo? info) => info == null ? 0
        : info.AllLightUpAppearances.Values.Where(a => a.OverdueTimeMs == 0)
            .Sum(a => data.Row("WardRobeConfig", a.AppearanceId) is { } row ? GameData.Int(row, "_iFashionScore") : 0);

    /// <summary>Load durable ownership before ENTITYSELF/AOI; a new role receives every sex-matching default hair.</summary>
    public async Task LoginAsync(GameSession session)
    {
        var saved = roles.LoadWardrobe(session.Role!.Id);
        var info = saved is { Length: > 0 } ? DBWardRobeInfo.Parser.ParseFrom(saved) : new DBWardRobeInfo();
        bool changed = RemoveExpired(info);
        if (session.Mounts != null) changed |= LightMounts(info, session.Mounts.Mounts.Values); // repair pre-link roles
        changed |= EnsureDefaults(session, info);
        session.Wardrobe = info;
        if (changed || saved == null) roles.SaveWardrobe(session.Role.Id, info.ToByteArray());
        ArmExpiry(session);
        await CharacterService.SyncAsync(session, WardrobeSync, info.Clone());
    }

    /// <summary>31183 carries the full selected nonzero appearance ID list (wardrobe panel close, lines 1070-1126).</summary>
    public async Task UseAsync(GameSession session, WardRobeUseAppearanceReq req)
    {
        using var _ = await CharacterService.Lock(session);
        await RefreshExpiredAsync(session);
        var now = session.Wardrobe!;
        var selected = new Dictionary<int, int>();
        int result = 0;
        foreach (int id in req.AppearanceIds)
        {
            var row = Row(id);
            int slot = row is { } r ? (int)GameData.Int(r, "_iType") : 0;
            result = Validate(session, now, id, requireOwned: true);
            if (result != 0 || slot is < 1 or > MaxSlot || !selected.TryAdd(slot, id))
            {
                if (result == 0) result = ErrorCodes.Parameter;
                break;
            }
        }
        if (result == 0)
        {
            // (inferred) Hair is a permanent base look; the sender normally includes it, and omission must not erase it.
            if (!selected.ContainsKey(HairSlot) && now.CurUseAppearances.TryGetValue(HairSlot, out int hair)) selected[HairSlot] = hair;
            var next = now.Clone();
            next.CurUseAppearances.Clear();
            foreach (var (slot, id) in selected) next.CurUseAppearances[slot] = id;
            await CommitAsync(session, next, visible: true);
        }
        log.LogInformation("{Name} wears {Count} appearances: {Result}", session.Role!.Name, req.AppearanceIds.Count, result);
        await session.SendGameAsync(Use, new WardRobeUseAppearanceRes { Result = result, Req = req.Clone() });
    }

    /// <summary>31184: select the owned appearance supplying a slot's wardrobe attributes (inferred sender semantics).</summary>
    public async Task UseAttrAsync(GameSession session, WardRobeUseAppearanceAttrReq req)
    {
        using var _ = await CharacterService.Lock(session);
        await RefreshExpiredAsync(session);
        int result = Validate(session, session.Wardrobe!, req.AppearanceId, requireOwned: true);
        if (result == 0)
        {
            var next = session.Wardrobe!.Clone();
            int slot = (int)GameData.Int(Row(req.AppearanceId)!.Value, "_iType");
            next.CurUseAppearanceAttrs[slot] = req.AppearanceId;
            await CommitAsync(session, next);
            await character.RefreshStatsAsync(session); // Future configured attributes may use this source.
        }
        await session.SendGameAsync(UseAttr, new WardRobeUseAppearanceAttrRes { Result = result, Req = req.Clone() });
    }

    /// <summary>31185: save all slot IDs in a zero-based plan; FashionMatchNum is five.</summary>
    public async Task SavePlanAsync(GameSession session, WardRobeSaveAppearancePlanReq req)
    {
        using var _ = await CharacterService.Lock(session);
        await RefreshExpiredAsync(session);
        int result = PlanError(req.Index);
        if (result == 0) result = ValidateMap(session, session.Wardrobe!, req.Appearances);
        if (result == 0)
        {
            var next = session.Wardrobe!.Clone();
            if (!next.AppearancePlans.TryGetValue(req.Index, out var plan))
                plan = new DBAppearancePlan { Index = req.Index, IsUnlock = true, Name = $"Plan {req.Index + 1}" };
            else plan = plan.Clone();
            if (!plan.IsUnlock) result = ErrorCodes.FeatureUnavailable;
            else
            {
                plan.Appearances.Clear();
                foreach (var (slot, id) in req.Appearances) if (id != 0) plan.Appearances[slot] = id;
                // LC_DAL_Fashion.OnPlanUpdate calls pairs(plan.appearances); preserve a real hair entry.
                if (!plan.Appearances.ContainsKey(HairSlot) && next.CurUseAppearances.TryGetValue(HairSlot, out int hair))
                    plan.Appearances[HairSlot] = hair;
                next.AppearancePlans[req.Index] = plan;
                await CommitAsync(session, next);
            }
        }
        await session.SendGameAsync(SavePlan, new WardRobeSaveAppearancePlanRes { Result = result, Req = req.Clone() });
    }

    /// <summary>31186 must echo req: LC_DAL_Fashion.OnApplyAppearancePlan reads req.index unconditionally.</summary>
    public async Task ApplyPlanAsync(GameSession session, WardRobeApplyAppearancePlanReq req)
    {
        using var _ = await CharacterService.Lock(session);
        await RefreshExpiredAsync(session);
        int result = PlanError(req.Index);
        var current = session.Wardrobe!;
        if (result == 0 && (!current.AppearancePlans.TryGetValue(req.Index, out var plan) || !plan.IsUnlock))
            result = ErrorCodes.FeatureUnavailable;
        if (result == 0) result = ValidateMap(session, current, current.AppearancePlans[req.Index].Appearances);
        if (result == 0)
        {
            var next = current.Clone();
            next.CurUseAppearances.Clear();
            foreach (var (slot, id) in current.AppearancePlans[req.Index].Appearances)
                if (id != 0) next.CurUseAppearances[slot] = id;
            if (!next.CurUseAppearances.ContainsKey(HairSlot) && current.CurUseAppearances.TryGetValue(HairSlot, out int hair))
                next.CurUseAppearances[HairSlot] = hair;
            await CommitAsync(session, next, visible: true);
        }
        await session.SendGameAsync(ApplyPlan, new WardRobeApplyAppearancePlanRes { Result = result, Req = req.Clone() });
    }

    /// <summary>31187 typo appreanceId is on the wire. enum_FashionLikeState: Like=0, Dislike=1.</summary>
    public async Task SetLikeAsync(GameSession session, WardRobeSetLikeReq req)
    {
        using var _ = await CharacterService.Lock(session);
        await RefreshExpiredAsync(session);
        int result = Row(req.AppreanceId) == null || req.OpType is not (0 or 1) ? ErrorCodes.Parameter : 0;
        if (result == 0)
        {
            var next = session.Wardrobe!.Clone();
            if (req.OpType == 0) next.LikeAppearance[req.AppreanceId] = 0;
            else next.LikeAppearance.Remove(req.AppreanceId);
            await CommitAsync(session, next);
        }
        await session.SendGameAsync(SetLike, new WardRobeSetLikeResp { Result = result });
    }

    /// <summary>31188: configured material costs and 5034 ownership commit in one SQLite transaction (inferred invariant).</summary>
    public async Task RedeemAsync(GameSession session, WardRobeRedeemUnlockAppearanceReq req)
    {
        using var _ = await CharacterService.Lock(session);
        await RefreshExpiredAsync(session);
        var current = session.Wardrobe!;
        var row = Row(req.AppearanceId);
        int result = row == null ? ErrorCodes.Parameter : Validate(session, current, req.AppearanceId, requireOwned: false);
        if (result == 0 && Owns(current, req.AppearanceId)) result = ErrorCodes.Parameter; // (inferred) already unlocked.
        var costs = row is { } r ? Materials(r) : [];
        if (result == 0 && costs.Count == 0) result = ErrorCodes.FeatureUnavailable; // No free unlock path inferred.
        if (result == 0 && costs.Any(c => c.Entry <= 0 || c.Count <= 0 || data.Row("ItemConfig", c.Entry) == null))
            result = ErrorCodes.FeatureUnavailable;
        if (result == 0)
        {
            var next = current.Clone();
            next.AllLightUpAppearances[req.AppearanceId] = new DBAppearanceInfo { AppearanceId = req.AppearanceId };
            var paid = await character.ExchangeLockedAsync(session, costs, [], popup: false,
                persist: tx =>
                {
                    tx.RoleBlob(session.Role!.Id, "wardrobe", next.ToByteArray());
                    tx.WithEconomy(character.FamilyEconomy(session, "cd13b/wardrobe-redeem", costs, [],
                        new
                        {
                            Appearance = req.AppearanceId,
                            StateHash = Convert.ToHexString(SHA256.HashData(next.ToByteArray()))
                        }));
                }, committed: () => { session.Wardrobe = next; ArmExpiry(session); }, asyncCommit: true);
            if (paid == null) result = character.ShortageOf(session, costs);
            else
            {
                await CharacterService.SyncAsync(session, WardrobeSync, next.Clone());
                await session.SendGameAsync(UnlockNotice, new WardRobeUnLockNtf { AppearanceId = req.AppearanceId });
            }
        }
        log.LogInformation("{Name} redeems wardrobe {Id}: {Result}", session.Role!.Name, req.AppearanceId, result);
        await session.SendGameAsync(Redeem, new WardRobeRedeemUnlockAppearanceResp { Result = result });
    }

    /// <summary>
    /// 31323 action 200 ActivatedAppearance: ItemConfig._iUseEffect contains the appearance ID at index 2.
    /// CharacterService calls this under its StateLock and owns the use reply, cooldowns and ItemUsed notification.
    /// One item instance and one wardrobe unlock commit together (inferred crash-safety invariant).
    /// </summary>
    public async Task<int?> TryUseAppearanceItemAsync(GameSession session, ItemUseReq req, InvItem item, JsonElement row, int count)
    {
        if (!row.TryGetProperty("_iUseEffect", out var effects) || effects.ValueKind != JsonValueKind.Array
            || effects.GetArrayLength() == 0 || effects[0].ValueKind != JsonValueKind.Array
            || effects[0].GetArrayLength() == 0 || (long)(GameData.Number(effects[0][0]) ?? 0) != 200) return null;
        await RefreshExpiredAsync(session);
        int result = count != 1 ? ErrorCodes.Parameter : 0;
        int appearanceId = 0;
        if (result == 0)
        {
            if (GameData.Int(row, "_iIsUse") == 0 || effects.GetArrayLength() != 1
                || effects[0].GetArrayLength() < 3)
                result = ErrorCodes.FeatureUnavailable;
            else appearanceId = (int)(GameData.Number(effects[0][2]) ?? 0);
        }
        if (result == 0) result = Validate(session, session.Wardrobe!, appearanceId, requireOwned: false);
        if (result == 0 && Owns(session.Wardrobe!, appearanceId)) result = ErrorCodes.Parameter;
        if (result == 0)
        {
            var next = session.Wardrobe!.Clone();
            next.AllLightUpAppearances[appearanceId] = new DBAppearanceInfo { AppearanceId = appearanceId };
            var spent = await character.ExchangeLockedAsync(session, [], [], new Dictionary<long, int> { [item.Guid] = 1 },
                popup: false, persist: tx =>
                {
                    tx.RoleBlob(session.Role!.Id, "wardrobe", next.ToByteArray());
                    tx.WithEconomy(character.FamilyEconomy(session, "cd13b/wardrobe-item", [(item.Entry, 1L)], [],
                        new
                        {
                            Appearance = appearanceId, Item = item.Entry,
                            StateHash = Convert.ToHexString(SHA256.HashData(next.ToByteArray()))
                        }));
                }, committed: () => { session.Wardrobe = next; ArmExpiry(session); }, asyncCommit: true);
            if (spent == null) result = ErrorCodes.ItemsShort;
            else
            {
                await CharacterService.SyncAsync(session, WardrobeSync, next.Clone());
                await session.SendGameAsync(UnlockNotice, new WardRobeUnLockNtf { AppearanceId = appearanceId });
            }
        }
        return result;
    }

    /// <summary>
    /// A Fashion-tab commodity (ShopCommodityConfig._iAppearanceId, _iItemId 0) bought through 82332/82340. The caller
    /// (ShopService) holds StateLock and has admitted the shop gates and the purchase limit; this pays, unlocks, grants
    /// the bonus award and joins the caller's counter blob in one transaction. Returns an ErrorCodes result.
    /// <list type="bullet">
    /// <item>Owned means allLightUpAppearances[_iAppearanceId] (UI_Sys_AppearanceShopWidgetCtrl.lua:2455-2464 shows
    /// "already owned"; :3484-3500 and :3550-3582 leave lit looks out of the buyable cart). The client never offers an
    /// owned look, so a request for one is refused with 2316 "Appearance already unlocked" and nothing is charged.</item>
    /// <item>A suit unlocks itself and its pieces. The wardrobe's suit tab lights a suit only when every piece of
    /// GetSuitPieces (WardRobeConfig rows whose _iMain is the suit: Lua_CFG_WardRobeConfig.lua:66-88, :644) is in
    /// allLightUpAppearances, and never checks the suit id (UI_Sys_WardrobePanelCtrl.lua:1724-1740); the shop checks
    /// only the suit id. The shop previews GetSuitPieces, else _kAppearanceParts (UI_Sys_AppearanceShopWidgetCtrl.lua
    /// :2255-2310). Every commodity's _kAppearanceParts equals its suit's pieces in the export; both are unlocked.</item>
    /// <item>The bonus is _iAppearanceAwardId, a MultiverseGroupConfig id the shop previews with GetSeasonAwardList
    /// (UI_Sys_AppearanceShopWidgetCtrl.lua:2002-2066) and the wardrobe shows as a lit look's giveaway
    /// (WardRobeConfig._iGiftAward, UI_Sys_WardrobePanelCtrl.lua:5564-5575). Unresolvable: 2324, nothing charged.</item>
    /// <item>One 31189 for the bought look: LC_DAL_Fashion.lua:253-265 pops one ManifestTypeUnlockAppearance tip per
    /// notice (one per suit rather than per piece is inferred).</item>
    /// </list>
    /// </summary>
    internal async Task<int> BuyFromShopLockedAsync(GameSession session, int appearanceId, IReadOnlyList<int> parts,
        IReadOnlyList<(int Entry, long Count)> cost, AwardPlan bonus, Action<RoleItemPlanBuilder> persistShop,
        Action committedShop, object shopOutcome)
    {
        if (session.Wardrobe == null) return ErrorCodes.FeatureUnavailable;
        await RefreshExpiredAsync(session);
        var current = session.Wardrobe!;
        var looks = new List<int> { appearanceId };
        looks.AddRange(parts.Concat(SuitPieces(appearanceId)).Where(id => id != appearanceId).Distinct());
        foreach (int id in looks)
        {
            if (Row(id) is not { } row || GameData.Int(row, "_iType") is < 1 or > MaxSlot) return ErrorCodes.ShopAppearanceMissing;
            int result = Validate(session, current, id, requireOwned: false);
            if (result != 0) return result;
        }
        if (Owns(current, appearanceId)) return ErrorCodes.ShopAppearanceOwned;
        if (bonus.Grants.Any(g => g.ExpireTimeStamp != 0)) return ErrorCodes.ShopRewardMissing; // No expiring bonus path.
        // A mount look (Fashion class 50202 sells 5000008-5000014 and 5000038) is also the system mount 32743 rides:
        // its 5012 ownership commits here too, or the lit look would answer MountNotOwned (WardrobeService.Mounts.cs).
        var mounts = looks.Select(SystemMount).Where(id => id > 0).Distinct().ToList();
        LinkedMountGrant? ride = null;
        if (mounts.Count > 0)
        {
            if (MountGrant == null) return ErrorCodes.FeatureUnavailable;
            (int error, ride) = MountGrant(session, mounts);
            if (error != 0) return error;
        }
        var next = current.Clone();
        foreach (int id in looks)
            if (!Owns(next, id)) next.AllLightUpAppearances[id] = new DBAppearanceInfo { AppearanceId = id };
        var goods = bonus.Grants.Select(g => (g.Entry, g.Count, Bind: g.BindType)).ToList();
        var economy = character.FamilyEconomy(session, Economy.CharacterFamilyEconomy.ShopAppearanceKind, cost,
            goods.Select(g => (g.Entry, g.Count)).ToList(), new
            {
                Version = 1, Shop = shopOutcome, Appearances = looks, Award = bonus.GroupId, AwardConfig = bonus.AwardId,
                Grants = goods.Select(g => new { g.Entry, g.Count, g.Bind }),
                StateHash = Convert.ToHexString(SHA256.HashData(next.ToByteArray())),
                Mounts = ride?.Mounts ?? [], MountStateHash = ride?.StateHash,
            });
        var paid = await character.ExchangeLockedAsync(session, cost, [], boundGoods: goods,
            persist: tx =>
            {
                persistShop(tx);
                tx.RoleBlob(session.Role!.Id, "wardrobe", next.ToByteArray());
                ride?.Persist(tx);
                tx.WithEconomy(economy);
            },
            committed: () => { committedShop(); session.Wardrobe = next; ArmExpiry(session); ride?.Committed(); }, asyncCommit: true);
        if (paid == null) return character.ShortageOf(session, cost);
        await CharacterService.SyncAsync(session, WardrobeSync, next.Clone());
        // 5012 only: 31189 below is the purchase's one unlock tip; 32747 would pop a second (LC_DAL_Mount.lua:186-218).
        if (ride != null) await ride.PublishAsync();
        await session.SendGameAsync(UnlockNotice, new WardRobeUnLockNtf { AppearanceId = appearanceId });
        log.LogInformation("{Name} buys appearance {Id} (+{Parts} pieces, award {Award})", session.Role!.Name,
            appearanceId, looks.Count - 1, bonus.GroupId);
        return 0;
    }

    /// <summary>Lua_CFG_WardRobeConfig.GetSuitPieces: the rows whose _iMain names the suit.</summary>
    private IEnumerable<int> SuitPieces(int suit) => suit == 0 ? [] : data.Table("WardRobeConfig").Values
        .Where(r => GameData.Int(r, "_iMain") == suit).Select(r => (int)GameData.Int(r, "_iID")).Where(id => id > 0).Order();

    private async Task RefreshExpiredAsync(GameSession session)
    {
        var next = session.Wardrobe!.Clone();
        if (!RemoveExpired(next)) { ArmExpiry(session); return; }
        EnsureDefaults(session, next);
        await CommitAsync(session, next, visible: true);
    }

    /// <summary>Clear time-limited ownership before ENTITYSELF and in the running world, including its AOI look.</summary>
    public Task ReconcileAsync(GameSession session) => loop.IsOnLoop
        ? ReconcileCoreAsync(session, deadlines.Capture(session))
        : loop.InvokeAsync(() => ReconcileCoreAsync(session, deadlines.Capture(session)));

    private async Task ReconcileCoreAsync(GameSession session, DeadlineLease lease)
    {
        if (session.Role == null || session.Wardrobe == null) return;
        using var _ = await CharacterService.Lock(session);
        if (!deadlines.IsCurrent(session, lease) || session.Wardrobe == null) return;
        await RefreshExpiredAsync(session);
    }

    private void ArmExpiry(GameSession session)
    {
        long next = session.Wardrobe?.AllLightUpAppearances.Values
            .Where(a => a.OverdueTimeMs > 0).Select(a => a.OverdueTimeMs).DefaultIfEmpty().Min() ?? 0;
        if (next == 0) deadlines.Cancel(session, ExpiryKey);
        else deadlines.Set(session, ExpiryKey, DateTimeOffset.FromUnixTimeMilliseconds(next),
            (current, due, lease, ct) => ExpireAsync(current, lease));
    }

    private async Task ExpireAsync(GameSession session, DeadlineLease lease)
    {
        try { await ReconcileCoreAsync(session, lease); }
        catch
        {
            if (deadlines.IsCurrent(session, lease))
                deadlines.Set(session, ExpiryKey, clock.GetUtcNow().AddSeconds(1),
                    (current, due, next, ct) => ExpireAsync(current, next));
            throw;
        }
    }

    private async Task CommitAsync(GameSession session, DBWardRobeInfo next, bool visible = false)
    {
        // LC_DAL_Fashion.OnLikeUpdate indexes the existing Lua table. A zero-entry protobuf map is omitted on login,
        // so introduce the first favorite with a whole dataset instead of an incremental child callback (inferred).
        bool firstLike = session.Wardrobe!.LikeAppearance.Count == 0 && next.LikeAppearance.Count > 0;
        var tx = roles.NewTransaction();
        tx.ItemDelta(session.Role!.Id, session.Items, session.Items);
        tx.RoleBlob(session.Role.Id, "wardrobe", next.ToByteArray());
        tx.WithEconomy(character.FamilyEconomy(session, "cd13b/wardrobe-state", [], [],
            new { StateHash = Convert.ToHexString(SHA256.HashData(next.ToByteArray())) }));
        await roles.CommitAsync(tx, "wardrobe/state", _ =>
        {
            session.Wardrobe = next;
            ArmExpiry(session);
            if (firstLike) session.Synced.Remove(WardrobeSync);
        });
        await CharacterService.SyncAsync(session, WardrobeSync, next.Clone());
        if (visible) await PublishAppearanceAsync(session);
    }

    private async Task PublishAppearanceAsync(GameSession session)
    {
        var change = new EntityBase { Uuid = (ulong)session.Role!.Id, NotifyType = EntityBase.Types.NotifyType.WardRobe };
        change.CurUseAppearances.Add(session.Wardrobe!.CurUseAppearances);
        await session.SendGameAsync(CharacterService.EntityBaseChange, change);
        foreach (var viewer in world.Viewers(session))
            try { await viewer.SendGameAsync(CharacterService.EntityBaseChange, change); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private JsonElement? Row(int id) => data.Row("WardRobeConfig", id);
    private int ValidateMap(GameSession session, DBWardRobeInfo info, IDictionary<int, int> map)
    {
        if (map.Count > MaxSlot) return ErrorCodes.Parameter;
        foreach (var (slot, id) in map)
        {
            if (slot is < 1 or > MaxSlot) return ErrorCodes.Parameter;
            if (id == 0) continue; // Explicit empty slot in a plan.
            if (Row(id) is not { } row || GameData.Int(row, "_iType") != slot) return ErrorCodes.Parameter;
            int result = Validate(session, info, id, requireOwned: true);
            if (result != 0) return result;
        }
        return 0;
    }

    private int Validate(GameSession session, DBWardRobeInfo info, int id, bool requireOwned)
    {
        if (Row(id) is not { } row || GameData.Int(row, "_iType") is < 1 or > MaxSlot) return ErrorCodes.Parameter;
        if (GameData.Int(row, "_iDisable") > 0) return ErrorCodes.FeatureUnavailable;
        int sex = (int)GameData.Int(row, "_iSexNeed");
        if (sex != 0 && sex != session.Role!.Sex) return ErrorCodes.Parameter;
        // WardRobeConfig has no job column; mount appearances have a MountsConfig._kJob allowlist.
        if (GameData.Int(row, "_iType") == MountSlot && data.Row("MountsConfig", id) is { } mount
            && mount.TryGetProperty("_kJob", out var jobs) && jobs.ValueKind == JsonValueKind.Array
            && jobs.GetArrayLength() > 0 && !jobs.EnumerateArray().Any(v => (long)(GameData.Number(v) ?? 0) is 0 || (long)(GameData.Number(v) ?? 0) == session.Progress!.Career))
            return ErrorCodes.FeatureUnavailable;
        if (requireOwned && !Owns(info, id)) return ErrorCodes.FeatureUnavailable;
        return 0;
    }

    private bool Owns(DBWardRobeInfo info, int id) => info.AllLightUpAppearances.TryGetValue(id, out var owned)
        && (owned.OverdueTimeMs == 0 || owned.OverdueTimeMs > clock.GetUtcNow().ToUnixTimeMilliseconds());

    private static int PlanError(int index) => index is >= 0 and < PlanCount ? 0 : ErrorCodes.Parameter;

    private static List<(int Entry, long Count)> Materials(JsonElement row)
    {
        var result = new List<(int Entry, long Count)>();
        if (!row.TryGetProperty("_kMaterial", out var materials) || materials.ValueKind != JsonValueKind.Array) return result;
        foreach (var pair in materials.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2) return [];
            result.Add(((int)(GameData.Number(pair[0]) ?? 0), (long)(GameData.Number(pair[1]) ?? 0)));
        }
        return result;
    }

    private bool EnsureDefaults(GameSession session, DBWardRobeInfo info)
    {
        bool changed = !info.IsInit;
        info.IsInit = true;
        foreach (var row in data.Table("WardRobeConfig").Values)
        {
            if (GameData.Int(row, "_iDefaultLightUp") <= 0 || GameData.Int(row, "_iDisable") > 0) continue;
            int sex = (int)GameData.Int(row, "_iSexNeed"), id = (int)GameData.Int(row, "_iID");
            if (sex != 0 && sex != session.Role!.Sex || id <= 0) continue;
            if (!info.AllLightUpAppearances.ContainsKey(id))
            {
                info.AllLightUpAppearances[id] = new DBAppearanceInfo { AppearanceId = id };
                changed = true;
            }
        }
        int hair = session.Role!.Hair;
        if (!Owns(info, hair) || Row(hair) is not { } hairRow || GameData.Int(hairRow, "_iType") != HairSlot
            || (GameData.Int(hairRow, "_iSexNeed") is var need && need != 0 && need != session.Role.Sex))
            hair = info.AllLightUpAppearances.Keys.Order().FirstOrDefault(id => Row(id) is { } r && GameData.Int(r, "_iType") == HairSlot);
        if (!info.CurUseAppearances.TryGetValue(HairSlot, out int selectedHair) || !Owns(info, selectedHair))
        {
            if (hair != 0) info.CurUseAppearances[HairSlot] = hair;
            changed = true;
        }
        // (inferred) Five actual plans, all selectable: absent plans are shown unlocked by PlanPanelCtrl:608-650.
        for (int index = 0; index < PlanCount; index++)
            if (!info.AppearancePlans.ContainsKey(index))
            {
                var plan = new DBAppearancePlan { Index = index, IsUnlock = true, Name = index == 0 ? "Default" : $"Plan {index + 1}" };
                if (info.CurUseAppearances.TryGetValue(HairSlot, out int wornHair)) plan.Appearances[HairSlot] = wornHair;
                info.AppearancePlans[index] = plan;
                changed = true;
            }
        foreach (var plan in info.AppearancePlans.Values)
            if (!plan.Appearances.ContainsKey(HairSlot) && info.CurUseAppearances.TryGetValue(HairSlot, out int wornHair))
            { plan.Appearances[HairSlot] = wornHair; changed = true; }
        return changed;
    }

    private bool RemoveExpired(DBWardRobeInfo info)
    {
        long now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var expired = info.AllLightUpAppearances.Where(e => e.Value.OverdueTimeMs > 0 && e.Value.OverdueTimeMs <= now)
            .Select(e => e.Key).ToHashSet();
        if (expired.Count == 0) return false;
        foreach (int id in expired) info.AllLightUpAppearances.Remove(id);
        foreach (int slot in info.CurUseAppearances.Where(e => expired.Contains(e.Value)).Select(e => e.Key).ToArray()) info.CurUseAppearances.Remove(slot);
        foreach (int slot in info.CurUseAppearanceAttrs.Where(e => expired.Contains(e.Value)).Select(e => e.Key).ToArray()) info.CurUseAppearanceAttrs.Remove(slot);
        foreach (var plan in info.AppearancePlans.Values)
            foreach (int slot in plan.Appearances.Where(e => expired.Contains(e.Value)).Select(e => e.Key).ToArray()) plan.Appearances.Remove(slot);
        return true;
    }
}
