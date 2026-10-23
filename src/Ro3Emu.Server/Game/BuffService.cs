using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>A buff on a player: BuffConfig id, its attribute changes, when it ends (absolute server ms).</summary>
public sealed record ActiveBuff(long Guid, int ConfigId, long ExpireMs, int DurationMs, IReadOnlyList<(long Attr, double Value)> Changes, long SourceId,
    int PileCount = 1, bool IsPassive = false, bool IsTotemOwned = false, int StateOrdinal = 0, bool ShowIcon = true,
    long ShieldRemaining = 0, bool IsGuardianOwned = false, bool ShieldDebuffImmunity = false, int HpFloorBasisPoints = 0);

/// <summary>
/// Buff lifetime and control state. The explicit state map below is inferred from
/// BuffConfig names, VFX/category and SpecialStatusConfig. 31115-31117 are empty legacy carriers; the client
/// consumes 2701/2702/2711 through 31048 instead.
/// </summary>
public sealed partial class BuffService : IGameTicker
{
    public const uint BuffUpdate = 31104, BuffRemove = 31105, WidgetUpdate = 31130, WidgetRemove = 31131;
    public const uint WorldTreeRevive = 31331, ClientRemoveBuff = 31332, BuffEffectCd = 31333, EnergyNotify = 31334;
    private const uint AoiAttrChange = 31048;

    private readonly GameData _data;
    private readonly World _world;
    private readonly SessionRegistry _sessions;
    private readonly CharacterService _character;
    private readonly GameLoop _loop;
    private readonly ILogger<BuffService> _log;
    private long _guids = 900000;
    private readonly Dictionary<long, List<ActiveBuff>> _unitBuffs = new();

    /// <summary>Subscribers recompute effective skill parameters/slots after a buff changes.</summary>
    public List<Func<GameSession, Task>> Changed { get; } = new();

    /// <summary>The applier's changes to a buff's _kChangeAttri pairs before piles (TalentProcs: talent buff attributes).</summary>
    internal Func<long, JsonElement, IReadOnlyList<(long Id, double Value)>, IReadOnlyList<(long Id, double Value)>>? SourceAttributes { get; set; }

    /// <summary>Subscribers publish effective unit stats after a timed attribute buff changes.</summary>
    public List<Func<WorldUnit, Task>> UnitChanged { get; } = new();

    public BuffService(GameData data, World world, SessionRegistry sessions, CharacterService character, GameLoop loop, ILogger<BuffService> log)
    {
        this._data = data;
        this._world = world;
        this._sessions = sessions;
        this._character = character;
        this._loop = loop;
        this._log = log;
        character.GiveBuff = (p, buff, ms) => AddAsync(p, buff, ms, p.Role!.Id);
        character.IsItemUseForbidden = p => HasForbidden(p, ForbiddenState.UseItem);
        character.DynamicProperties.Add(SupportShieldProperties);
        character.DynamicProperties.Add(BlindProperties);
        world.UnitRemoved.Add(unit => { _unitBuffs.Remove(unit.Uid); return Task.CompletedTask; });
    }

    // BuffConfig._iBuffID of the control groups; StateFor names the SpecialStatusConfig row each one shows.
    internal const int StunGroup = 9016, KnockBackGroup = 9017, KnockUpGroup = 9018, FreezeGroup = 9020, PetrifyGroup = 9021,
        SleepGroup = 9022, SilenceGroup = 9023, RootGroup = 9024, BlindGroup = 9025, SuperArmorGroup = 9031,
        ControlImmunityGroup = 9032, DebuffImmunityGroup = 9033;

    // Inferred: these exact buff groups are named Stun/Sleep/Silence/Freeze/Invincible; matching
    // SpecialStatusConfig rows specify forbidden actions. No general category interpreter was recovered.
    private static int StateFor(int buffGroup, bool guardianOwned) => buffGroup switch
    {
        StunGroup => 18, // Stun
        SleepGroup => 19, // Sleep
        SilenceGroup => 20, // Silence
        12003 => 11,
        9034 => 25,
        _ => guardianOwned ? GuardianStateFor(buffGroup) : 0,
    };

    /// <summary>Further named groups and their SpecialStatusConfig rows. (inferred scope) Only guardian-applied buffs
    /// carry these states so far: the same groups from ordinary skills keep their previous, display-only behaviour
    /// until each state is established and tested for every source.</summary>
    private static int GuardianStateFor(int buffGroup) => buffGroup switch
    {
        KnockUpGroup => 3, // Knock-Up -> FlyHit
        FreezeGroup => 11, // Freeze
        PetrifyGroup => 12, // Petrify
        RootGroup => 21, // Root
        SuperArmorGroup => 22, // Super Armor
        ControlImmunityGroup => 23, // Control immunity
        DebuffImmunityGroup => 24, // Debuff immunity
        GuardianGroup.ShinobiStealth => 2, // Stealth
        _ => 0,
    };

    /// <summary>Place or refresh a validated buff. Zero-time source rows retain the previous 60s fallback (inferred).</summary>
    public Task AddAsync(GameSession p, int configId, int durationMs, long sourceId) =>
        AddCoreAsync(p, configId, durationMs, sourceId, null);

    /// <summary>Set a territory-owned pile count from the authoritative occupation/fatigue state (inferred).
    /// Ordinary AddAsync still follows BuffConfig stack and multi-source rules.</summary>
    public Task SetPileCountAsync(GameSession p, int configId, int durationMs, long sourceId, int pileCount) =>
        AddCoreAsync(p, configId, durationMs, sourceId, Math.Max(1, pileCount));

    /// <summary>Apply a timed buff with an optional finite shield. The caller evaluates any shield formula;
    /// BuffConfig._kChangeAttri is a verified data source, while shield stacking and rounding are inferred.</summary>
    public Task AddSupportAsync(GameSession p, int configId, int durationMs, long sourceId, long shieldAmount = 0) =>
        AddCoreAsync(p, configId, durationMs, sourceId, null, shieldAmount);

    internal Task AddGuardianAsync(GameSession p, int configId, int durationMs, long sourceId, long shieldAmount = 0,
        bool shieldDebuffImmunity = false, int hpFloorBasisPoints = 0, double attributeMultiplier = 1) =>
        AddCoreAsync(p, configId, durationMs, sourceId, null, shieldAmount, guardianOwned: true,
            shieldDebuffImmunity, hpFloorBasisPoints, attributeMultiplier);

    private async Task AddCoreAsync(GameSession p, int configId, int durationMs, long sourceId, int? explicitPileCount,
        long shieldAmount = 0, bool guardianOwned = false, bool shieldDebuffImmunity = false, int hpFloorBasisPoints = 0,
        double attributeMultiplier = 1)
    {
        if (p.Role == null || _data.Row("BuffConfig", configId) is not { } row || GameData.Int(row, "_iBuffID") <= 0) return;
        if (await RejectIncomingAsync(p, row, guardianOwned, sourceId)) return;
        if (durationMs <= 0) durationMs = (int)GameData.Int(row, "_iTime");
        if (durationMs <= 0) durationMs = 60000;
        if (sourceId != p.Role.Id && !ApplyDecay(p.Role.Id, p.SceneEntry, configId, ref durationMs)) return;
        long now = MonsterService.NowMs;
        int group = (int)GameData.Int(row, "_iBuffID");
        bool multiSource = GameData.Int(row, "_iMultiSource") != 0;
        var existing = p.Buffs.FirstOrDefault(b => !b.IsPassive && !b.IsTotemOwned && _data.Row("BuffConfig", b.ConfigId) is { } old
            && GameData.Int(old, "_iBuffID") == group && (!multiSource || b.SourceId == sourceId));
        int stackType = (int)GameData.Int(row, "_iStackType");
        int maxStack = (int)Math.Max(1, GameData.Int(row, "_iMaxStack"));
        // Inferred server policy from enum_BuffStackType: named layer types add a pile; others refresh.
        int pile = explicitPileCount ?? (existing != null && stackType is 1 or 2 or 3 or 7
            ? Math.Min(maxStack, existing.PileCount + 1) : 1);
        IEnumerable<(long Id, double Value)> pairs = GameData.Pairs(row, "_kChangeAttri");
        if (SourceAttributes != null) pairs = SourceAttributes(sourceId, row, [.. pairs]);
        var changes = pairs.Select(c => (c.Id, c.Value * pile * attributeMultiplier)).ToList();
        var buff = new ActiveBuff(existing?.Guid ?? Interlocked.Increment(ref _guids), configId,
            now + durationMs, durationMs, changes, sourceId, pile, StateOrdinal: StateFor(group, guardianOwned),
            ShieldRemaining: Math.Max(0, shieldAmount), IsGuardianOwned: guardianOwned,
            ShieldDebuffImmunity: shieldDebuffImmunity, HpFloorBasisPoints: hpFloorBasisPoints);
        if (existing != null) p.Buffs.Remove(existing);
        p.Buffs.Add(buff);
        TrackPeriodic(p, null, buff);
        TrackHalo(p, buff);
        _log.LogInformation("{Name} gets buff {Buff} x{Pile} for {Ms} ms", p.Role.Name, configId, pile, durationMs);
        var info = Wire(buff);
        await ToPlayerAndViewersAsync(p, BuffUpdate, new BuffUpdateNotify { UnitId = p.Role.Id, Buffs = { info } });
        await ToPlayerAndViewersAsync(p, AoiAttrChange, new AOIAttrChange
        {
            EntityUuid = (ulong)p.Role.Id, BuffInfo = new AoIBuffInfo { AddBuffs = { info } },
        });
        if (existing?.StateOrdinal != buff.StateOrdinal || buff.StateOrdinal != 0) await RefreshControlAsync(p);
        if (guardianOwned && group == BlindGroup) await PublishBlindAsync(p);
        if (buff.Changes.Count > 0 || existing?.Changes.Count > 0 || buff.ShieldRemaining > 0 || existing?.ShieldRemaining > 0)
            await _character.RefreshStatsAsync(p);
        foreach (var handler in Changed) await handler(p);
    }

    /// <summary>Source-owned passive configuration. Zero-time totem passives are permanent until that source is removed;
    /// they are executable server registrations, not a fabricated 60-second icon. Socketed-card passives
    /// (<paramref name="totemOwned"/> false, CardEffects) share the lifetime rule under their card GUID.</summary>
    public async Task AddPassiveAsync(GameSession p, int configId, long sourceId, bool totemOwned = true)
    {
        if (_data.Row("BuffConfig", configId) is not { } row
            || p.Buffs.Any(b => b.IsPassive && b.IsTotemOwned == totemOwned && b.ConfigId == configId && b.SourceId == sourceId)) return;
        var buff = new ActiveBuff(++_guids, configId, long.MaxValue, 0,
            GameData.Pairs(row, "_kChangeAttri").Where(c => c.Value != 0).ToArray(), sourceId, IsPassive: true, IsTotemOwned: totemOwned,
            ShowIcon: GameData.Int(row, "_iShowIcon") != 0);
        p.Buffs.Add(buff);
        TrackHalo(p, buff);
        // SceneService resends visible passives after ENTITYSELF when the client has created the unit.
        if (!p.SceneLoading && GameData.Int(row, "_iShowIcon") != 0)
            await ToPlayerAndViewersAsync(p, BuffUpdate, new BuffUpdateNotify
            {
                UnitId = p.Role!.Id, IsViewSync = false,
                Buffs = { new BuffInfo { BuffGuid = buff.Guid, ConfigId = configId, ExpireTime = 0,
                    Duration = 0, PileCount = 1, SourceId = sourceId } },
            });
    }

    /// <summary>Apply a timed totem stack by source; refresh one GUID and broadcast the authoritative pile count.</summary>
    public async Task AddStackAsync(GameSession p, int configId, int durationMs, long sourceId, int maximum)
    {
        if (_data.Row("BuffConfig", configId) is not { } row || durationMs <= 0 || maximum <= 0) return;
        var existing = p.Buffs.FirstOrDefault(b => b.IsTotemOwned && !b.IsPassive && b.ConfigId == configId && b.SourceId == sourceId);
        int count = Math.Min(maximum, (existing?.PileCount ?? 0) + 1);
        var changes = GameData.Pairs(row, "_kChangeAttri").Where(c => c.Value != 0)
            .Select(c => (c.Id, c.Value * count)).ToArray();
        long now = MonsterService.NowMs;
        var buff = new ActiveBuff(existing?.Guid ?? ++_guids, configId, now + durationMs, durationMs, changes, sourceId, count,
            IsTotemOwned: true);
        if (existing != null) p.Buffs.Remove(existing);
        p.Buffs.Add(buff);
        await ToPlayerAndViewersAsync(p, BuffUpdate, new BuffUpdateNotify
        {
            UnitId = p.Role!.Id,
            Buffs = { new BuffInfo { BuffGuid = buff.Guid, ConfigId = configId, ExpireTime = buff.ExpireMs,
                Duration = durationMs, PileCount = count, SourceId = sourceId } },
            IsViewSync = false,
        });
        if (changes.Length > 0) await _character.RefreshStatsAsync(p);
    }

    /// <summary>Timed totem proc. Unlike the ordinary single-config buff path, independent item sources retain their own GUID.</summary>
    public async Task AddSourcedAsync(GameSession p, int configId, int durationMs, long sourceId)
    {
        if (_data.Row("BuffConfig", configId) is not { } row || durationMs <= 0) return;
        var existing = p.Buffs.FirstOrDefault(b => b.IsTotemOwned && !b.IsPassive && b.ConfigId == configId && b.SourceId == sourceId);
        long now = MonsterService.NowMs;
        var buff = new ActiveBuff(existing?.Guid ?? ++_guids, configId, now + durationMs, durationMs,
            GameData.Pairs(row, "_kChangeAttri").Where(c => c.Value != 0).ToArray(), sourceId, IsTotemOwned: true);
        if (existing != null) p.Buffs.Remove(existing);
        p.Buffs.Add(buff);
        await ToPlayerAndViewersAsync(p, BuffUpdate, new BuffUpdateNotify
        {
            UnitId = p.Role!.Id,
            Buffs = { new BuffInfo { BuffGuid = buff.Guid, ConfigId = configId, ExpireTime = buff.ExpireMs,
                Duration = durationMs, PileCount = 1, SourceId = sourceId } },
            IsViewSync = false,
        });
        if (buff.Changes.Count > 0) await _character.RefreshStatsAsync(p);
    }

    /// <summary>A guardian's buff on a unit: unlike ordinary unit buffs, it carries its control state and counts
    /// toward the unit's combat attributes (<see cref="GuardianUnitAttributes"/>).</summary>
    internal Task AddGuardianAsync(WorldUnit unit, int configId, int durationMs, long sourceId, double attributeMultiplier = 1) =>
        AddSupportAsync(unit, configId, durationMs, sourceId, attributeMultiplier: attributeMultiplier, guardianOwned: true);

    /// <summary>Legacy monster buff entry point; unit buffs now share the support lifecycle.</summary>
    public Task ShowOnUnitAsync(WorldUnit unit, int configId, int durationMs, long sourceId) =>
        AddSupportAsync(unit, configId, durationMs, sourceId);

    /// <summary>Apply or refresh a timed WorldUnit buff. BuffConfig._iBuffID, _iMultiSource and _kChangeAttri
    /// are verified data fields; applying player-like stacking and replacing the shield on refresh are inferred.</summary>
    public async Task AddSupportAsync(WorldUnit unit, int configId, int durationMs, long sourceId, long shieldAmount = 0,
        double attributeMultiplier = 1, bool guardianOwned = false)
    {
        if (_data.Row("BuffConfig", configId) is not { } row || GameData.Int(row, "_iBuffID") <= 0) return;
        if (_unitBuffs.TryGetValue(unit.Uid, out var live) && ImmuneTo(live, row, guardianOwned)) return;
        if (live != null && sourceId != unit.Uid && UnitResistsControl(live, row))
        {
            foreach (var handler in ControlResisted) await handler(unit, sourceId, row);
            return;
        }
        if (durationMs <= 0) durationMs = (int)GameData.Int(row, "_iTime");
        if (durationMs <= 0) durationMs = 60000;
        if (sourceId != unit.Uid && !ApplyDecay(unit.Uid, unit.SceneEntry, configId, ref durationMs)) return;
        if (!_unitBuffs.TryGetValue(unit.Uid, out var list)) _unitBuffs[unit.Uid] = list = new();
        int group = (int)GameData.Int(row, "_iBuffID");
        bool multiSource = GameData.Int(row, "_iMultiSource") != 0;
        var existing = list.FirstOrDefault(b => _data.Row("BuffConfig", b.ConfigId) is { } old
            && GameData.Int(old, "_iBuffID") == group && (!multiSource || b.SourceId == sourceId));
        int stackType = (int)GameData.Int(row, "_iStackType");
        int maxStack = (int)Math.Max(1, GameData.Int(row, "_iMaxStack"));
        // (inferred) Match the player path: named layer types increase piles; other types refresh.
        int pile = existing != null && stackType is 1 or 2 or 3 or 7
            ? Math.Min(maxStack, existing.PileCount + 1) : 1;
        var buff = new ActiveBuff(existing?.Guid ?? Interlocked.Increment(ref _guids), configId,
            MonsterService.NowMs + durationMs, durationMs,
            GameData.Pairs(row, "_kChangeAttri").Select(c => (c.Id, c.Value * pile * attributeMultiplier)).ToArray(), sourceId, pile,
            ShieldRemaining: Math.Max(0, shieldAmount), IsGuardianOwned: guardianOwned,
            StateOrdinal: guardianOwned ? StateFor(group, true) : 0);
        if (existing != null) list.Remove(existing);
        list.Add(buff);
        TrackPeriodic(null, unit, buff);
        var info = Wire(buff);
        await _world.ToUnitViewersAsync(unit, BuffUpdate, new BuffUpdateNotify { UnitId = unit.Uid, Buffs = { info } });
        var changed = new AOIAttrChange { EntityUuid = (ulong)unit.Uid, BuffInfo = new AoIBuffInfo { AddBuffs = { info } } };
        if (buff.ShieldRemaining > 0 || existing?.ShieldRemaining > 0)
            changed.Property.Add(new AoIProperty { Idx = 2003, Data = ShieldAmount(unit) });
        await _world.ToUnitViewersAsync(unit, AoiAttrChange, changed);
        if (buff.Changes.Count > 0 || existing?.Changes.Count > 0 || buff.StateOrdinal > 0 || existing?.StateOrdinal > 0)
            foreach (var handler in UnitChanged) await handler(unit);
    }

    public async Task RemoveAsync(GameSession p, ActiveBuff buff)
    {
        if (p.Role == null || !p.Buffs.Remove(buff)) return;
        // Totem reconciliation during role loading rebuilds the snapshot before the new unit exists.
        if (buff.IsTotemOwned && p.SceneLoading)
        {
            // This also covers expiry during a map load, before TotemEffects is called again.
            if (buff.Changes.Count > 0) _character.Refresh(p);
            return;
        }
        if (Visible(buff)) await ToPlayerAndViewersAsync(p, BuffRemove, new BuffRemoveNotify { UnitId = p.Role.Id, BuffGuids = { buff.Guid } });
        if (Visible(buff)) await ToPlayerAndViewersAsync(p, AoiAttrChange, new AOIAttrChange
        {
            EntityUuid = (ulong)p.Role.Id, BuffInfo = new AoIBuffInfo { RemoveBuffs = { buff.Guid } },
        });
        if (buff.StateOrdinal != 0) await RefreshControlAsync(p);
        if (buff.IsGuardianOwned && buff.ConfigId / 100 == BlindGroup) await PublishBlindAsync(p);
        if (buff.Changes.Count > 0 || buff.ShieldRemaining > 0) await _character.RefreshStatsAsync(p);
        foreach (var handler in Changed) await handler(p);
    }

    /// <summary>31332: only a current own GUID with BuffConfig._iHandRemove may be removed (inferred server rule).</summary>
    public async Task RemoveByRequestAsync(GameSession p, ClientRemoveBuffReq req)
    {
        var buff = p.Buffs.FirstOrDefault(b => b.Guid == req.BuffGuid && b.ExpireMs > MonsterService.NowMs);
        int result = buff != null && _data.Row("BuffConfig", buff.ConfigId) is { } row
            && GameData.Int(row, "_iHandRemove") != 0 ? 0 : ErrorCodes.Parameter;
        await p.SendGameAsync(ClientRemoveBuff, new ClientRemoveBuffRsp { Result = result, BuffGuid = req.BuffGuid });
        if (result == 0) await RemoveAsync(p, buff!);
    }

    /// <summary>31331: successful WorldTreeSkillId 2200101 revive names the actual revived role.</summary>
    public Task NotifyWorldTreeReviveAsync(GameSession recipient, string revivedPlayerName)
    {
        if (recipient.Role == null || string.IsNullOrWhiteSpace(revivedPlayerName))
            throw new ArgumentException("A successful World Tree revive needs its actual player name", nameof(revivedPlayerName));
        return recipient.SendGameAsync(WorldTreeRevive, new ItemWorldTreeReviveNtf
        {
            WorldTreePlayerName = revivedPlayerName,
        });
    }

    /// <summary>SkillConfig._kCost type 3 consumes piles of a BuffConfig group. The tuple's id is a group (inferred).</summary>
    public bool HasBuffPiles(GameSession p, int buffGroup, int count) => count > 0
        && p.Buffs.Where(b => b.ExpireMs > MonsterService.NowMs && _data.Row("BuffConfig", b.ConfigId) is { } row
            && GameData.Int(row, "_iBuffID") == buffGroup).Sum(b => b.PileCount) >= count;

    /// <summary>Call after the cast succeeds; each partial reduction emits 31104 with its original instance GUID.</summary>
    public async Task<bool> ConsumeBuffPilesAsync(GameSession p, int buffGroup, int count)
    {
        if (!HasBuffPiles(p, buffGroup, count) || p.Role == null) return false;
        foreach (var buff in p.Buffs.Where(b => _data.Row("BuffConfig", b.ConfigId) is { } row
            && GameData.Int(row, "_iBuffID") == buffGroup).ToList())
        {
            int take = Math.Min(count, buff.PileCount);
            count -= take;
            if (take == buff.PileCount) await RemoveAsync(p, buff);
            else
            {
                p.Buffs.Remove(buff);
                var row = _data.Row("BuffConfig", buff.ConfigId)!.Value;
                var replacement = buff with
                {
                    PileCount = buff.PileCount - take,
                    Changes = GameData.Pairs(row, "_kChangeAttri")
                        .Select(c => (c.Id, c.Value * (buff.PileCount - take))).ToList(),
                };
                p.Buffs.Add(replacement);
                var info = Wire(replacement);
                await p.SendGameAsync(BuffUpdate, new BuffUpdateNotify { UnitId = p.Role.Id, Buffs = { info } });
                await ToPlayerAndViewersAsync(p, AoiAttrChange, new AOIAttrChange
                {
                    EntityUuid = (ulong)p.Role.Id, BuffInfo = new AoIBuffInfo { AddBuffs = { info } },
                });
                if (buff.Changes.Count > 0) await _character.RefreshStatsAsync(p);
                foreach (var handler in Changed) await handler(p);
            }
            if (count == 0) break;
        }
        return true;
    }

    public Task ClearOnDeathAsync(GameSession p) => ClearWhereAsync(p, "_iDeadClear");
    public async Task ClearOnSceneAsync(GameSession p)
    {
        // A scene change must not turn a pending lethal-hit decision into free survival.
        // The offer/recovery keep their original deadline; role selection resets them separately.
        await ClearWhereAsync(p, "_iSceneClear");
        await RefreshControlAsync(p);
    }

    private async Task ClearWhereAsync(GameSession p, string field)
    {
        foreach (var b in p.Buffs.ToList())
            if (_data.Row("BuffConfig", b.ConfigId) is not { } row || GameData.Int(row, field) == 1)
                await RemoveAsync(p, b);
    }

    /// <summary>Resend only a nonempty list: LC_Buff_Component.lua takes # of the decoded list without a nil guard.</summary>
    public async Task ResendAsync(GameSession p)
    {
        long now = MonsterService.NowMs;
        foreach (var buff in p.Buffs.Where(b => b.ExpireMs <= now).ToList()) await RemoveAsync(p, buff);
        if (p.Role == null || p.Buffs.Count == 0) return;
        var all = new BuffUpdateNotify { UnitId = p.Role.Id, IsViewSync = true };
        all.Buffs.Add(p.Buffs.Where(Visible).Select(Wire));
        if (all.Buffs.Count == 0) return;
        await p.SendGameAsync(BuffUpdate, all);
    }

    /// <summary>Called by entity creation for 31037/ENTITYSELF snapshot; buff array only when nonempty.</summary>
    public static void PopulateEntity(GameSession p, EntityBase entity)
    {
        var live = p.Buffs.Where(b => b.ExpireMs > MonsterService.NowMs && (!b.IsPassive || b.ShowIcon)).ToList();
        if (live.Count > 0)
        {
            entity.BuffData = new AoIBuffInfo();
            entity.BuffData.AddBuffs.Add(live.Select(Wire));
        }
        entity.ObjState = new AoiObjStateInfo { Data = Mask((ulong)p.StatusBits) };
        entity.ObjForbid = new AoiObjForbidInfo { Data = Mask(p.ForbiddenBits) };
        entity.ObjStagger = new AoiObjStaggerInfo { StaggerMask = p.StaggerBits };
    }

    public bool HasForbidden(GameSession p, int ordinal) => p.ForbiddenOrdinals.Contains(ordinal)
        || (p.ImmuneDeathOffer != null && ordinal == ForbiddenState.Damage)
        || (p.ImmuneRecoveryUntilMs > MonsterService.NowMs
            && ordinal is ForbiddenState.Walk or ForbiddenState.CastSkill or ForbiddenState.NormalAttack or ForbiddenState.Damage);

    private async Task RefreshControlAsync(GameSession p)
    {
        if (p.Role == null) return;
        ulong oldStatus = (ulong)p.StatusBits, oldForbidden = p.ForbiddenBits, oldStagger = p.StaggerBits;
        RecomputeControl(p);
        if ((ulong)p.StatusBits == oldStatus && p.ForbiddenBits == oldForbidden && p.StaggerBits == oldStagger) return;
        await ToPlayerAndViewersAsync(p, AoiAttrChange, new AOIAttrChange
        {
            EntityUuid = (ulong)p.Role.Id,
            StateInfo = new AoiObjStateInfo { Data = Mask((ulong)p.StatusBits) },
            ForbidInfo = new AoiObjForbidInfo { Data = Mask(p.ForbiddenBits) },
            StaggerInfo = new AoiObjStaggerInfo { StaggerMask = p.StaggerBits },
        });
    }

    private void RecomputeControl(GameSession p)
    {
        ulong status = 0, forbidden = 0, stagger = 0;
        p.ForbiddenOrdinals.Clear();
        foreach (var buff in p.Buffs.Where(b => b.StateOrdinal is > 0 and < 33 && b.ExpireMs > MonsterService.NowMs))
        {
            status |= 1UL << (buff.StateOrdinal - 1); // inferred beyond the directly observed Battling(16) bit.
            if (_data.Row("SpecialStatusConfig", buff.StateOrdinal) is not { } row) continue;
            if (row.TryGetProperty("_kForbidden", out var actions) && actions.ValueKind == JsonValueKind.Array)
                foreach (var action in actions.EnumerateArray())
                    if (GameData.Number(action) is { } ordinal)
                    {
                        int id = (int)ordinal;
                        p.ForbiddenOrdinals.Add(id);
                        forbidden |= ForbiddenMask(id);
                    }
            int staggerType = (int)GameData.Int(row, "_iStaggerType");
            if (staggerType is > 0 and < 64) stagger |= 1UL << staggerType; // LC_UnitSpecialStatus.lua:90-138.
        }
        if (p.ImmuneDeathOffer != null)
        {
            status |= 1UL << 24; // Invincible(25) while the 31134 choice is pending (inferred).
            forbidden |= ForbiddenMask(9);
        }
        if (p.ImmuneRecoveryUntilMs > MonsterService.NowMs)
        {
            status |= (1UL << 24) | (1UL << 8) | (1UL << 9); // Invincible/CanNotMove/CanNotSkill.
            forbidden |= ForbiddenMask(3) | ForbiddenMask(4) | ForbiddenMask(5) | ForbiddenMask(9);
        }
        p.StatusBits = (long)(((ulong)p.StatusBits & ~p.BuffStatusBits) | status);
        p.BuffStatusBits = status;
        p.ForbiddenBits = forbidden;
        p.StaggerBits = stagger;
    }

    // Lua_GameDefine.enum_ForbiddenState: masks are explicit and do not line up with every proto ordinal.
    private static ulong ForbiddenMask(int ordinal) => ordinal switch
    {
        1 => 1,
        2 => 2,
        3 => 4,
        4 => 8,
        5 => 16,
        6 => 32,
        7 => 64,
        8 => 128,
        9 => 256,
        10 => 512,
        11 => 1024,
        12 => 2048,
        13 => 4096,
        14 => 8192,
        15 => 16384,
        16 => 65536,
        23 => 4194304,
        24 => 8388608,
        25 => 16777216,
        27 => 67108864,
        _ => 0, // Lua enum has no named mask for the remaining server ordinals.
    };

    private static CommonBigMask Mask(ulong bits) => new() { Values = { bits } }; // include zero: Lua indexes values[1].
    private bool Visible(ActiveBuff buff) => !buff.IsPassive
        || _data.Row("BuffConfig", buff.ConfigId) is { } row && GameData.Int(row, "_iShowIcon") != 0;

    private static BuffInfo Wire(ActiveBuff buff) => new()
    {
        BuffGuid = buff.Guid, ConfigId = buff.ConfigId, ExpireTime = buff.IsPassive ? 0 : buff.ExpireMs,
        Duration = buff.DurationMs, PileCount = buff.PileCount, IsTotalTime = true, SourceId = buff.SourceId,
    };

    public void Tick(GameTime time)
    {
        long now = MonsterService.NowMs;
        // Ticks first: a periodic tick due at the expiry moment still lands before the expiry removal below.
        TickPeriodic(now);
        TickHalos(now);
        SweepDecay(now);
        foreach (var s in _sessions.All())
        {
            foreach (var b in s.Buffs.Where(b => b.ExpireMs <= now).ToList()) _loop.TrackLegacyDetached("buff expiration", () => RemoveAsync(s, b));
            TickEnergy(s, now);
            TickImmuneDeath(s, now);
        }
        foreach (var (uid, list) in _unitBuffs.ToList())
        {
            foreach (var buff in list.Where(b => b.ExpireMs <= now).ToList())
            {
                var unit = _world.AllUnits().FirstOrDefault(u => u.Uid == uid);
                if (unit != null) _loop.TrackLegacyDetached("unit buff expiration", () => RemoveUnitBuffAsync(unit, buff));
                else list.Remove(buff);
            }
            if (list.Count == 0) _unitBuffs.Remove(uid);
        }
    }

    private async Task ToPlayerAndViewersAsync(GameSession p, uint msgId, Google.Protobuf.IMessage body)
    {
        await p.SendGameAsync(msgId, body);
        foreach (var viewer in _world.Viewers(p).Where(v => v != p)) await viewer.SendGameAsync(msgId, body);
    }
}
