using System.Text.Json;
using System.Security.Cryptography;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// MG_Mount / 5012. Ownership, selected slots and the visible rider are distinct.
/// LC_MountServer omits tp on 32743; zero therefore selects the system slot, not an invalid enum.
/// Mount attributes use the normal StatCalculator pipeline, including per_speed 1131.
/// </summary>
public sealed class MountService : IGameTicker
{
    public const uint Sync = 5012, SetConfig = 32743, ScoreAward = 32744, Ride = 32745, Unlock = 32747;
    public const uint EtchantRefresh = 32748, EtchantSave = 32749, EtchantPlan = 32750;
    private readonly GameData data;
    private readonly CharacterService character;
    private readonly RoleStore roles;
    private readonly FunctionService functions;
    private readonly World world;
    private readonly GameLoop loop;
    private readonly SessionRegistry sessions;
    private readonly Deadlines deadlines;
    private readonly TimeProvider clock;
    private readonly WardrobeService wardrobe;
    private TimeSpan lastTick;
    private const string ExpiryKey = "mount-expiry";

    /// <summary>5012 plus durable claim/preview/quest state not carried by that dataset (inferred storage layout).</summary>
    public sealed class SavedState
    {
        public byte[] Info { get; set; } = [];
        public HashSet<long> Claimed { get; set; } = new();
        public Dictionary<string, int> Previews { get; set; } = new();
        public int QuestMount { get; set; }
    }

    public MountService(GameData data, CharacterService character, RoleStore roles, FunctionService functions,
        World world, GameLoop loop, SessionRegistry sessions, SceneService scenes, Deadlines deadlines, TimeProvider clock,
        WardrobeService wardrobe)
    {
        this.wardrobe = wardrobe;
        this.data = data; this.character = character; this.roles = roles; this.functions = functions;
        this.world = world; this.loop = loop; this.sessions = sessions; this.deadlines = deadlines; this.clock = clock;
        character.ExtraAttributes.Add(Attributes);
        character.SpecialItemUse.Add(TryUseItemAsync);
        wardrobe.MountGrant = PlanShopGrant;
        character.JobChanged.Add(ReconcileAsync);
        scenes.BeforeSelf.Add(ReconcileAsync);
    }

    public void Load(GameSession s)
    {
        s.MountState = roles.LoadMounts(s.Role!.Id) is { } bytes ? JsonSerializer.Deserialize<SavedState>(bytes) ?? new() : new();
        s.Mounts = s.MountState.Info.Length > 0 ? DBMountsInfo.Parser.ParseFrom(s.MountState.Info) : new();
        Normalize(s);
        AdoptBoughtLooks(s);
        Save(s);
        ArmExpiry(s);
    }

    /// <summary>
    /// Repair for a role that bought a Fashion-shop mount look before the purchase granted the mount: a permanently lit,
    /// listed type-6 look whose system mount is not owned permanently gets it, as PlanShopGrant does now. No other
    /// path lights a mount look without its mount (WardrobeService.Mounts.cs), so this only completes those purchases.
    /// </summary>
    private void AdoptBoughtLooks(GameSession s)
    {
        if (roles.LoadWardrobe(s.Role!.Id) is not { Length: > 0 } saved) return;
        foreach (var lit in DBWardRobeInfo.Parser.ParseFrom(saved).AllLightUpAppearances.Values)
        {
            int id = lit.OverdueTimeMs == 0 ? wardrobe.SystemMount(lit.AppearanceId) : 0;
            if (id != 0 && Config(id) != null) Grant(s.Mounts!, id);
        }
    }

    /// <summary>Permanent ownership as TryUseItemAsync builds it; false when the mount is already owned permanently.</summary>
    private bool Grant(DBMountsInfo info, int id)
    {
        if (info.Mounts.TryGetValue(id, out var owned) && owned.OverdueTimeMs == 0) return false;
        var mount = owned?.Clone() ?? new DBMount { MountId = id, UniqueId = id, Type = Slot(id) };
        mount.OverdueTimeMs = 0;
        InitializeEtchants(mount);
        info.Mounts[id] = mount;
        return true;
    }

    /// <summary>
    /// WardrobeService.BuyFromShopLockedAsync (caller holds StateLock): a bought type-6 look also owns its system mount,
    /// permanently, in the shop's own transaction. A held timed entry becomes permanent and keeps its etchants.
    /// </summary>
    private (int Error, WardrobeService.LinkedMountGrant? Grant) PlanShopGrant(GameSession s, IReadOnlyList<int> ids)
    {
        if (s.Mounts == null) return (ErrorCodes.FeatureUnavailable, null);
        if (ids.Any(id => Config(id) == null)) return (ErrorCodes.MountConfig, null);
        var next = s.Mounts.Clone();
        var granted = new List<int>();
        foreach (int id in ids.Distinct())
            if (Grant(next, id)) granted.Add(id);
        if (granted.Count == 0) return (0, null);
        byte[] saved = Encode(s, next);
        return (0, new WardrobeService.LinkedMountGrant(granted, Convert.ToHexString(SHA256.HashData(saved)),
            tx => tx.RoleBlob(s.Role!.Id, "mounts", saved),
            () => { s.Mounts = next; ArmExpiry(s); },
            () => PublishOwnedAsync(s)));
    }

    public Task LoginAsync(GameSession s) => CharacterService.SyncAsync(s, Sync, s.Mounts!.Clone());

    private byte[] Encode(GameSession s, DBMountsInfo? info = null, SavedState? state = null)
    {
        state ??= s.MountState;
        // A copy prevents a failed payment from publishing a paid preview or ownership change.
        return JsonSerializer.SerializeToUtf8Bytes(new SavedState { Info = (info ?? s.Mounts!).ToByteArray(),
            Claimed = state.Claimed, Previews = state.Previews, QuestMount = state.QuestMount });
    }

    private void Save(GameSession s) => roles.SaveMounts(s.Role!.Id, Encode(s));

    /// <summary>The "mounts" blob for <paramref name="next"/>: a Special Class Item lease selects the battle slot 2.</summary>
    internal byte[] EncodeBlob(GameSession s, DBMountsInfo next) => Encode(s, next);

    /// <summary>Publishes a committed 5012 change: the dataset, stats and the rider to self and viewers.</summary>
    internal Task PublishAsync(GameSession s) => ChangedAsync(s);

    public static long Rider(GameSession s) => s.MountState.QuestMount != 0 ? s.MountState.QuestMount
        : s.Mounts?.MountIds.OrderBy(p => p.Key).FirstOrDefault(p => p.Value.IsRiding).Value?.MountId ?? 0;

    private IEnumerable<(long Id, double Value)> Attributes(GameSession s) =>
        Config((int)Rider(s)) is { } row ? GameData.Pairs(row, "_kAttr") : [];

    private JsonElement? Config(long id) => data.Row("MountsConfig", id) ?? data.Row("CartRemouldTable", id);
    private int Slot(long id) => data.Row("MountsConfig", id) != null ? 1 : data.Row("CartRemouldTable", id) != null ? 2 : 0;
    private int Param(string name, int fallback) => data.Table("PublicParamConfig").TryGetValue(name, out var v) ? (int)(GameData.Number(v) ?? fallback) : fallback;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    private void ArmExpiry(GameSession s)
    {
        long next = s.Mounts?.Mounts.Values.Where(m => m.OverdueTimeMs > 0)
            .Select(m => m.OverdueTimeMs).DefaultIfEmpty().Min() ?? 0;
        if (next == 0) deadlines.Cancel(s, ExpiryKey);
        else deadlines.Set(s, ExpiryKey, DateTimeOffset.FromUnixTimeMilliseconds(next),
            (session, due, lease, ct) => ExpireAsync(session, lease));
    }

    private async Task ExpireAsync(GameSession session, DeadlineLease lease)
    {
        try { await ReconcileCoreAsync(session, lease); }
        catch
        {
            // The old polling path retried after a failed DB reconcile. Only the still-current
            // deadline may re-arm; detached or superseded work must not resurrect itself.
            if (deadlines.IsCurrent(session, lease))
                deadlines.Set(session, ExpiryKey, clock.GetUtcNow().AddSeconds(1),
                    (current, due, next, ct) => ExpireAsync(current, next));
            throw;
        }
    }

    private int OwnedError(GameSession s, long id)
    {
        if (Config(id) is not { } row) return ErrorCodes.MountConfig;
        // A leased Special Class Item mount (5002 mountsSlot) is entitled by its lease: CartRemouldTable._kProfessionID,
        // level and skill, checked by CareerSpecialService. The client never reads _kJob (Grand Peco lists the Knight line).
        if (CareerSpecialService.IsLeasedMount(s, id)) return 0;
        if (s.Mounts == null || !s.Mounts.Mounts.TryGetValue(id, out var mount)) return ErrorCodes.MountNotOwned;
        if (mount.OverdueTimeMs > 0 && mount.OverdueTimeMs <= Now) return ErrorCodes.MountExpired;
        if (row.TryGetProperty("_kJob", out var jobs) && jobs.ValueKind == JsonValueKind.Array && jobs.GetArrayLength() > 0
            && !jobs.EnumerateArray().Any(v => GameData.Number(v) is 0 || GameData.Number(v) == s.Progress!.Career)) return ErrorCodes.MountJob;
        return 0;
    }

    /// <summary>MapConfigBase.CanRideBattle/System/FlyMount reads _kRiderForbidden[1/2/3].</summary>
    private int RidingError(GameSession s, long id, int slot)
    {
        if (s.Dead) return ErrorCodes.MountStateInvalid;
        if (s.SceneLoading) return ErrorCodes.SwitchingScenes;
        if (slot == 1 && (s.StatusBits & (1L << 15)) != 0) return ErrorCodes.MountCombat;
        if (data.Row("MapConfig", s.SceneEntry) is { } map && map.TryGetProperty("_kRiderForbidden", out var flags)
            && flags.ValueKind == JsonValueKind.Array)
        {
            int index = slot == 2 ? 0 : 1;
            if (flags.GetArrayLength() > index && GameData.Number(flags[index]) != 0) return ErrorCodes.MountArea;
        }
        return 0;
    }

    public async Task SetAsync(GameSession s, PlayerMountsSetCfgReq req)
    {
        using var gate = await CharacterService.Lock(s);
        int slot = (int)req.Tp == 0 ? 1 : (int)req.Tp;
        int error = !functions.IsOpen(s, 27) ? ErrorCodes.FunctionLocked : slot is < 1 or > 2 ? ErrorCodes.Parameter
            : req.MountId != 0 && Slot(req.MountId) != slot ? ErrorCodes.MountConfig
            : req.MountId != 0 ? OwnedError(s, req.MountId) : 0;
        bool riding = s.Mounts!.MountIds.TryGetValue(slot, out var old) && old.IsRiding;
        if (error == 0 && riding && req.MountId != 0) error = RidingError(s, req.MountId, slot);
        if (error == 0)
        {
            var next = s.Mounts.Clone();
            next.MountIds[slot] = new MountInfo { MountId = req.MountId, IsRiding = riding && req.MountId != 0 };
            var state = CopyState(s); state.QuestMount = 0;
            await CommitAsync(s, next, state);
        }
        await s.SendGameAsync(SetConfig, new PlayerMountsSetCfgResp { Result = error, ChangeTp = slot, MountId = req.MountId });
    }

    public async Task RideAsync(GameSession s, PlayerRiderMountReq req)
    {
        using var gate = await CharacterService.Lock(s);
        int tp = (int)req.Tp, slot = tp < 2 ? 1 : 2;
        bool on = tp is 1 or 3;
        var selected = s.Mounts!.MountIds.GetValueOrDefault(slot);
        int error = tp is < 0 or > 3 ? ErrorCodes.Parameter : on && !functions.IsOpen(s, 27) ? ErrorCodes.FunctionLocked
            : on ? OwnedError(s, selected?.MountId ?? 0) : 0;
        if (error == 0 && on) error = RidingError(s, selected!.MountId, slot);
        if (error == 0)
        {
            var next = s.Mounts.Clone(); var state = CopyState(s); state.QuestMount = 0;
            foreach (var (key, value) in next.MountIds) if (key == slot || on) value.IsRiding = key == slot && on;
            await CommitAsync(s, next, state);
        }
        await s.SendGameAsync(Ride, new PlayerRiderMountResp { Result = error, Tp = tp });
    }

    private SavedState CopyState(GameSession s) => JsonSerializer.Deserialize<SavedState>(Encode(s))!;

    /// <param name="look">The linked wardrobe change (WardrobeService.Mounts.cs) committed in the same transaction.</param>
    private async Task CommitAsync(GameSession s, DBMountsInfo next, SavedState? state = null, DBWardRobeInfo? look = null)
    {
        state ??= s.MountState;
        var before = s.Wardrobe;
        var tx = roles.NewTransaction();
        tx.ItemDelta(s.Role!.Id, s.Items, s.Items);
        tx.RoleBlob(s.Role.Id, "mounts", Encode(s, next, state));
        if (look != null) tx.RoleBlob(s.Role.Id, "wardrobe", look.ToByteArray());
        tx.WithEconomy(character.FamilyEconomy(s, "cd13b/mount-state", [], [],
            new { StateHash = Convert.ToHexString(SHA256.HashData(Encode(s, next, state))) }));
        await roles.CommitAsync(tx, "mount/state", _ =>
        {
            s.Mounts = next; s.MountState = state;
            if (look != null) wardrobe.AdoptCommitted(s, look);
        });
        await ChangedAsync(s);
        if (look != null) await wardrobe.PublishCommittedAsync(s, before!);
    }

    /// <summary>After a committed 5012 change (ownership, riding): BuffService adds or drops mount energy meters.</summary>
    public List<Func<GameSession, Task>> Changed { get; } = new();

    /// <summary>5012 after a committed change, then the <see cref="Changed"/> listeners.</summary>
    private async Task PublishOwnedAsync(GameSession s)
    {
        await LoginAsync(s);
        foreach (var handler in Changed) await handler(s);
    }

    private async Task ChangedAsync(GameSession s)
    {
        ArmExpiry(s);
        await PublishOwnedAsync(s);
        await character.RefreshStatsAsync(s);
        await character.CombatLoadoutChangedAsync(s);
        var entity = RoleSessionService.PlayerEntity(s);
        entity.NotifyType = EntityBase.Types.NotifyType.Rider;
        await s.SendGameAsync(CharacterService.EntityBaseChange, entity);
        if (!s.Owners!.AllowsPresentation(s)) return;
        foreach (var viewer in world.Viewers(s))
        {
            if (!s.Owners!.AllowsPresentation(s)) break;
            await viewer.SendGameAsync(CharacterService.EntityBaseChange, entity);
        }
    }

    private bool Normalize(GameSession s, DBMountsInfo? target = null)
    {
        var info = target ?? s.Mounts!;
        bool changed = false;
        foreach (var (id, mount) in info.Mounts.ToArray())
            if (mount.OverdueTimeMs > 0 && mount.OverdueTimeMs <= Now) { info.Mounts.Remove(id); changed = true; }
        foreach (var (slot, choice) in info.MountIds)
        {
            if (choice.MountId == 0) continue;
            if (OwnedError(s, choice.MountId) != 0) { choice.MountId = 0; choice.IsRiding = false; changed = true; }
            // Scene-loading itself does not cancel a saved mount; the destination's actual restrictions do.
            else if (choice.IsRiding && !s.SceneLoading && RidingError(s, choice.MountId, slot) != 0)
            { choice.IsRiding = false; changed = true; }
        }
        return changed;
    }

    public Task ReconcileAsync(GameSession s) => loop.IsOnLoop
        ? ReconcileCoreAsync(s, deadlines.Capture(s))
        : loop.InvokeAsync(() => ReconcileCoreAsync(s, deadlines.Capture(s)));

    private async Task ReconcileCoreAsync(GameSession s, DeadlineLease lease)
    {
        if (s.Mounts == null) return;
        using var gate = await CharacterService.Lock(s);
        if (!deadlines.IsCurrent(s, lease) || s.Mounts == null) return;
        var next = s.Mounts.Clone();
        // A timed mount and its appearance share one deadline; drop both in the expiry commit.
        if (Normalize(s, next)) await CommitAsync(s, next, look: wardrobe.WithoutExpired(s));
    }

    public void Tick(GameTime time)
    {
        if (time.Now - lastTick < TimeSpan.FromSeconds(1)) return;
        lastTick = time.Now;
        // Combat/death can invalidate riding without a MountService event. Preserve that small gate for
        // riders only; expiry itself is handled by the removable deadline above.
        foreach (var s in sessions.All().Where(s => s.Role != null && s.Mounts != null && Rider(s) != 0))
        {
            var lease = deadlines.Capture(s);
            loop.PostOwned(s, () => ReconcileCoreAsync(s, lease));
        }
    }

    /// <summary>Action 201: duration is seconds (inferred from 60/600 trial items), permanent zero. One owned entry per config ID, as LC_MountHelper indexes it.</summary>
    private async Task<int?> TryUseItemAsync(GameSession s, ItemUseReq req, InvItem item, JsonElement row, int count)
    {
        if (!row.TryGetProperty("_iUseEffect", out var effects) || effects.ValueKind != JsonValueKind.Array || effects.GetArrayLength() != 1
            || effects[0].ValueKind != JsonValueKind.Array || effects[0].GetArrayLength() != 3 || GameData.Number(effects[0][0]) != 201) return null;
        int id = (int)(GameData.Number(effects[0][2]) ?? 0), duration = (int)(GameData.Number(effects[0][1]) ?? -1);
        int error = count != 1 || duration < 0 ? ErrorCodes.Parameter : Config(id) == null ? ErrorCodes.MountConfig
            : s.Progress!.Level < GameData.Int(row, "_iLevelNeed") ? ErrorCodes.FunctionLocked
            : s.Mounts!.Mounts.TryGetValue(id, out var owned) && owned.OverdueTimeMs == 0 ? ErrorCodes.MountAlreadyOwned : 0;
        if (error == 0)
        {
            var next = s.Mounts!.Clone();
            long expiry = duration == 0 ? 0 : Math.Max(Now, next.Mounts.GetValueOrDefault(id)?.OverdueTimeMs ?? 0) + duration * 1000L;
            var mount = next.Mounts.GetValueOrDefault(id)?.Clone() ?? new DBMount { MountId = id, UniqueId = id, Type = Slot(id) };
            mount.OverdueTimeMs = expiry;
            InitializeEtchants(mount);
            next.Mounts[id] = mount;
            // The wardrobe Mount tab lights only from 5034, so the appearance commits with the ownership.
            var look = wardrobe.WithMount(s, mount);
            if (await character.ExchangeLockedAsync(s, [], [], new Dictionary<long, int> { [item.Guid] = 1 }, popup: false,
                persist: tx =>
                {
                    tx.RoleBlob(s.Role!.Id, "mounts", Encode(s, next));
                    if (look != null) tx.RoleBlob(s.Role!.Id, "wardrobe", look.ToByteArray());
                    tx.WithEconomy(character.FamilyEconomy(s, "cd13b/mount-unlock", [(item.Entry, 1L)], [],
                        new { Item = item.Entry, Mount = id,
                            StateHash = Convert.ToHexString(SHA256.HashData(Encode(s, next))) }));
                }, committed: () =>
                {
                    s.Mounts = next; ArmExpiry(s);
                    if (look != null) wardrobe.AdoptCommitted(s, look);
                }, asyncCommit: true) == null) error = ErrorCodes.ItemsShort;
            else
            {
                await PublishOwnedAsync(s);
                // 5034 only: 32747 already pops the unlock tip; 31189 would show a second reward tip (LC_DAL_Fashion:253-265).
                if (look != null) await CharacterService.SyncAsync(s, WardrobeService.WardrobeSync, s.Wardrobe!.Clone());
                await s.SendGameAsync(Unlock, new PlayerMountUnLockNtf { RiderId = id, EndTime = expiry });
            }
        }
        return error;
    }

    private IEnumerable<JsonElement> Etchants(long mount, int part) => data.Table("AppearanceEtchConfig").Values
        .Where(r => GameData.Int(r, "_iAppearanceID") == mount && (part == 0 || GameData.Int(r, "_iPart") == part));

    private void InitializeEtchants(DBMount mount)
    {
        var defaults = Etchants(mount.MountId, 0).Where(r => GameData.Int(r, "_iDefault") == 1).ToArray();
        if (defaults.Length == 0) return;
        for (int plan = 0; plan < Param("EtchantPlanMax", 4); plan++)
        {
            if (mount.EtChantInfo.ContainsKey(plan)) continue;
            var selection = new MountEtchant();
            foreach (var row in defaults) selection.EtChantData[(int)GameData.Int(row, "_iPart")] = (int)GameData.Int(row, "_iID");
            mount.EtChantInfo[plan] = selection;
        }
    }

    private int EtchantError(GameSession s, int mount, int plan, int part = 0) => OwnedError(s, mount) is var owned and not 0 ? owned
        : plan < 0 || plan >= Param("EtchantPlanMax", 4) || !Etchants(mount, part).Any() ? ErrorCodes.Parameter : 0;

    private static string PreviewKey(int mount, int plan, int part) => $"{mount}:{plan}:{part}";

    public async Task RefreshEtchantAsync(GameSession s, MountEtchantRefreshReq req)
    {
        using var gate = await CharacterService.Lock(s);
        int error = req.PlanId == 0 || req.Part <= 0 ? ErrorCodes.Parameter : EtchantError(s, req.MountId, req.PlanId, req.Part), selected = 0;
        var cost = new[] { (Entry: Param("EtchantRefreshItemId", 2), Count: (long)Param("EtchantRefreshItemCount", 1000)) };
        if (error == 0)
        {
            var candidates = Etchants(req.MountId, req.Part).Where(r => GameData.Int(r, "_iWeight") > 0).ToArray();
            // (inferred) Weighted independent draw over this appearance/part; default plan 0 stays immutable.
            long roll = Random.Shared.NextInt64(candidates.Sum(r => GameData.Int(r, "_iWeight")));
            foreach (var row in candidates) if ((roll -= GameData.Int(row, "_iWeight")) < 0) { selected = (int)GameData.Int(row, "_iID"); break; }
            var state = JsonSerializer.Deserialize<SavedState>(Encode(s))!;
            state.Previews[PreviewKey(req.MountId, req.PlanId, req.Part)] = selected;
            if (await character.ExchangeLockedAsync(s, cost, [], popup: false, persist: tx =>
                {
                    tx.RoleBlob(s.Role!.Id, "mounts", Encode(s, state: state));
                    tx.WithEconomy(character.FamilyEconomy(s, "cd13b/mount-etchant-refresh", cost, [],
                        new { Mount = req.MountId, Part = req.Part, Selected = selected,
                            StateHash = Convert.ToHexString(SHA256.HashData(Encode(s, state: state))) }));
                }, committed: () => s.MountState = state, asyncCommit: true) == null)
                error = character.ShortageOf(s, cost);
        }
        await s.SendGameAsync(EtchantRefresh, new MountEtchantRefreshResp { Result = error, CfgId = error == 0 ? selected : 0, Req = req });
    }

    public async Task SaveEtchantAsync(GameSession s, MountEtchantSaveReq req)
    {
        using var gate = await CharacterService.Lock(s);
        int error = req.PlanId == 0 || req.Part <= 0 ? ErrorCodes.Parameter : EtchantError(s, req.MountId, req.PlanId, req.Part);
        var key = PreviewKey(req.MountId, req.PlanId, req.Part);
        if (error == 0 && (!s.MountState.Previews.TryGetValue(key, out int id) || id != req.CfgId)) error = ErrorCodes.Parameter;
        if (error == 0)
        {
            var next = s.Mounts!.Clone(); var state = CopyState(s);
            var mount = next.Mounts[req.MountId];
            InitializeEtchants(mount);
            mount.EtChantInfo[req.PlanId].EtChantData[req.Part] = req.CfgId;
            state.Previews.Remove(key);
            await CommitAsync(s, next, state);
        }
        await s.SendGameAsync(EtchantSave, new MountEtchantSaveResp { Result = error });
    }

    public async Task SetEtchantPlanAsync(GameSession s, MountSetEtchantPlanReq req)
    {
        using var gate = await CharacterService.Lock(s);
        int error = EtchantError(s, req.MountId, req.PlanId);
        if (error == 0)
        {
            var next = s.Mounts!.Clone(); next.Mounts[req.MountId].EtChantId = req.PlanId;
            await CommitAsync(s, next);
        }
        await s.SendGameAsync(EtchantPlan, new MountSetEtchantPlanResp { Result = error });
    }

    /// <summary>32744: configured score thresholds, once per role. Score from permanent mount appearance FashionScore (inferred legacy score source).</summary>
    public async Task ClaimScoreAsync(GameSession s, PlayerGetMountScoreAwardReq req)
    {
        using var gate = await CharacterService.Lock(s);
        var resp = new PlayerGetMountScoreAwardResp();
        long score = s.Mounts!.Mounts.Values.Where(m => m.OverdueTimeMs == 0).Sum(m => data.Row("WardRobeConfig", m.MountId) is { } r ? GameData.Int(r, "_iFashionScore") : 0);
        var goods = new List<(int Entry, int Count)>();
        foreach (long index in req.Index)
        {
            var row = data.Table("MountsSpecialConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iSLev") == index);
            if (row.ValueKind != JsonValueKind.Object) { resp.Result = ErrorCodes.RewardMissing; break; }
            if (s.MountState.Claimed.Contains(index)) { resp.Result = ErrorCodes.RewardClaimed; break; }
            if (GameData.Int(row, "_iSValue") > score) { resp.Result = ErrorCodes.PurchaseRequirements; break; }
            goods.AddRange(GameData.Pairs(row, "_kAward").Select(p => ((int)p.Id, (int)p.Value)));
        }
        if (req.Index.Count == 0 || req.Index.Distinct().Count() != req.Index.Count) resp.Result = ErrorCodes.Parameter;
        if (resp.Result == 0)
        {
            var state = JsonSerializer.Deserialize<SavedState>(Encode(s))!;
            state.Claimed.UnionWith(req.Index);
            if (await character.ExchangeLockedAsync(s, [], goods, persist: tx =>
                {
                    tx.RoleBlob(s.Role!.Id, "mounts", Encode(s, state: state));
                    tx.WithEconomy(character.FamilyEconomy(s, "cd13b/mount-score", [], goods,
                        new { Indices = req.Index.ToArray(),
                            StateHash = Convert.ToHexString(SHA256.HashData(Encode(s, state: state))) }));
                }, committed: () => s.MountState = state, asyncCommit: true) == null)
                resp.Result = ErrorCodes.ItemNoConfig;
            else resp.Index.Add(req.Index);
        }
        await s.SendGameAsync(ScoreAward, resp);
    }

    /// <summary>Quest actions 412/54 use temporary ride state, committed inside the quest award transaction.</summary>
    public bool IsConfigured(int mount) => Config(mount) != null;

    public void PersistQuestRide(GameSession s, int mount, Ro3Emu.Server.Accounts.Persistence.RoleItemPlanBuilder builder)
    {
        if (mount != 0 && !IsConfigured(mount)) throw new ArgumentException("Invalid configured quest mount", nameof(mount));
        var state = JsonSerializer.Deserialize<SavedState>(Encode(s))!;
        state.QuestMount = mount;
        builder.RoleBlob(s.Role!.Id, "mounts", Encode(s, state: state));
    }

    public async Task PublishQuestRideAsync(GameSession s, int mount)
    {
        using var gate = await CharacterService.Lock(s);
        await ChangedAsync(s);
    }

    /// <summary>The quest award's owned Apply publishes its committed mount before any packet or hook.</summary>
    internal void ApplyQuestRideCore(GameSession s, Ownership.OwnerContext context, int mount)
    {
        context.AssertRole(s.Role!.Id);
        s.MountState.QuestMount = mount;
    }
}
