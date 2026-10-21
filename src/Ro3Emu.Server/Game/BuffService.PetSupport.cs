using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    private static long LiveShield(IEnumerable<ActiveBuff> buffs) => buffs
        .Where(b => b.ExpireMs > MonsterService.NowMs && b.ShieldRemaining > 0)
        .Aggregate(0L, (total, buff) => buff.ShieldRemaining > long.MaxValue - total
            ? long.MaxValue : total + buff.ShieldRemaining);

    private IEnumerable<(int Id, long Value)> SupportShieldProperties(GameSession player)
    {
        yield return (2003, LiveShield(player.Buffs));
    }

    /// <summary>The currently unspent shield owned by timed player buffs.</summary>
    public long ShieldAmount(GameSession player) => LiveShield(player.Buffs);

    /// <summary>The currently unspent shield owned by timed unit buffs.</summary>
    public long ShieldAmount(WorldUnit unit) => _unitBuffs.TryGetValue(unit.Uid, out var buffs) ? LiveShield(buffs) : 0;

    /// <summary>Active raw attribute contributions; the combat owner passes these through StatCalculator.</summary>
    /// <summary>The attribute changes of a guardian's live buffs on the unit. (inferred scope) Only these alter a monster's
    /// combat stats, damage and speed: ordinary unit buffs keep their previous, display-only behaviour.</summary>
    internal IEnumerable<(long Id, double Value)> GuardianUnitAttributes(WorldUnit unit) =>
        _unitBuffs.TryGetValue(unit.Uid, out var buffs)
            ? buffs.Where(b => b.IsGuardianOwned && b.ExpireMs > MonsterService.NowMs)
                .SelectMany(b => b.Changes.Select(change => (change.Attr, change.Value))).ToArray()
            : [];

    /// <summary>A player's live guardian buffs alone, through the same attribute calculator. (inferred scope) Healing,
    /// SP cost and chant modifiers read these, so equipment and ordinary buffs keep their previous effect.</summary>
    internal StatSheet GuardianAttributes(GameSession p)
    {
        var raw = new Dictionary<int, double>();
        long now = MonsterService.NowMs;
        foreach (var buff in p.Buffs.Where(b => b.IsGuardianOwned && b.ExpireMs > now))
            foreach (var (attr, value) in buff.Changes) raw[(int)attr] = raw.GetValueOrDefault((int)attr) + value;
        return StatCalculator.Compute(raw, _data);
    }

    internal bool HasGuardianUnitBuffs(WorldUnit unit) => _unitBuffs.TryGetValue(unit.Uid, out var buffs)
        && buffs.Any(b => b.IsGuardianOwned && b.ExpireMs > MonsterService.NowMs);

    public IEnumerable<(long Id, double Value)> UnitAttributes(WorldUnit unit) =>
        _unitBuffs.TryGetValue(unit.Uid, out var buffs)
            ? buffs.Where(b => b.ExpireMs > MonsterService.NowMs)
                .SelectMany(b => b.Changes.Select(change => (change.Attr, change.Value))).ToArray()
            : [];

    /// <summary>Actual remaining time after hostile-buff decay; movement effects must not outlive their buff.</summary>
    internal int UnitBuffRemainingMs(WorldUnit unit, int configId, long sourceId) =>
        _unitBuffs.TryGetValue(unit.Uid, out var buffs)
            ? (int)Math.Clamp(buffs.Where(b => b.ConfigId == configId && b.SourceId == sourceId)
                .Select(b => b.ExpireMs - MonsterService.NowMs).DefaultIfEmpty(0).Max(), 0, int.MaxValue) : 0;

    /// <summary>Add live buff icons and shield property 2003 to a unit's initial 31037 snapshot.</summary>
    public void PopulateEntity(WorldUnit unit, EntityBase entity)
    {
        if (!_unitBuffs.TryGetValue(unit.Uid, out var buffs)) return;
        ulong status = UnitStatus(unit), forbidden = UnitForbiddenMask(unit);
        if (status != 0)
        {
            entity.ObjState ??= new AoiObjStateInfo { Data = new CommonBigMask() };
            if (entity.ObjState.Data.Values.Count == 0) entity.ObjState.Data.Values.Add(0);
            entity.ObjState.Data.Values[0] |= status;
        }
        if (forbidden != 0)
        {
            entity.ObjForbid ??= new AoiObjForbidInfo { Data = new CommonBigMask() };
            if (entity.ObjForbid.Data.Values.Count == 0) entity.ObjForbid.Data.Values.Add(0);
            entity.ObjForbid.Data.Values[0] |= forbidden;
        }
        var live = buffs.Where(b => b.ExpireMs > MonsterService.NowMs && (!b.IsPassive || b.ShowIcon)).ToArray();
        if (live.Length > 0)
        {
            entity.BuffData ??= new AoIBuffInfo();
            entity.BuffData.AddBuffs.Add(live.Select(Wire));
        }
        long shield = ShieldAmount(unit);
        var property = entity.Property.FirstOrDefault(p => p.Idx == 2003);
        if (property != null) property.Data = shield;
        else if (shield > 0) entity.Property.Add(new AoIProperty { Idx = 2003, Data = shield });
    }

    /// <summary>Consume only live support shields before HP damage; nonpositive damage consumes nothing.</summary>
    public async Task<long> AbsorbAsync(GameSession target, long damage)
    {
        if (damage <= 0) return 0;
        if (target.Role == null) return damage;
        foreach (var expired in target.Buffs.Where(b => b.ShieldRemaining > 0 && b.ExpireMs <= MonsterService.NowMs).ToArray())
            await RemoveAsync(target, expired);
        // (inferred) Older buffs absorb first. BuffConfig does not identify an order for independent shields.
        foreach (var buff in target.Buffs.Where(b => b.ShieldRemaining > 0 && b.ExpireMs > MonsterService.NowMs)
                     .OrderBy(b => b.Guid).ToArray())
        {
            long absorbed = Math.Min(damage, buff.ShieldRemaining);
            damage -= absorbed;
            if (absorbed == buff.ShieldRemaining) await RemoveAsync(target, buff);
            else
            {
                target.Buffs[target.Buffs.IndexOf(buff)] = buff with { ShieldRemaining = buff.ShieldRemaining - absorbed };
                await _character.RefreshStatsAsync(target);
            }
            if (damage == 0) break;
        }
        return damage;
    }

    /// <summary>Consume only live unit support shields; fully spent buffs are removed and published.</summary>
    public async Task<long> AbsorbAsync(WorldUnit target, long damage)
    {
        if (damage <= 0) return 0;
        if (!_unitBuffs.TryGetValue(target.Uid, out var buffs)) return damage;
        foreach (var expired in buffs.Where(b => b.ShieldRemaining > 0 && b.ExpireMs <= MonsterService.NowMs).ToArray())
            await RemoveUnitBuffAsync(target, expired);
        // (inferred) Older buffs absorb first, matching the player path.
        foreach (var buff in buffs.Where(b => b.ShieldRemaining > 0 && b.ExpireMs > MonsterService.NowMs)
                     .OrderBy(b => b.Guid).ToArray())
        {
            long absorbed = Math.Min(damage, buff.ShieldRemaining);
            damage -= absorbed;
            if (absorbed == buff.ShieldRemaining) await RemoveUnitBuffAsync(target, buff);
            else
            {
                buffs[buffs.IndexOf(buff)] = buff with { ShieldRemaining = buff.ShieldRemaining - absorbed };
                await _world.ToUnitViewersAsync(target, AoiAttrChange, new AOIAttrChange
                {
                    EntityUuid = (ulong)target.Uid,
                    Property = { new AoIProperty { Idx = 2003, Data = ShieldAmount(target) } },
                });
            }
            if (damage == 0) break;
        }
        return damage;
    }

    /// <summary>Clear all buff lifetimes on a dead unit; removal from World also clears state.</summary>
    public async Task ClearUnitAsync(WorldUnit unit)
    {
        if (!_unitBuffs.TryGetValue(unit.Uid, out var buffs)) return;
        foreach (var buff in buffs.ToArray()) await RemoveUnitBuffAsync(unit, buff);
    }

    private async Task RemoveUnitBuffAsync(WorldUnit unit, ActiveBuff buff)
    {
        if (!_unitBuffs.TryGetValue(unit.Uid, out var buffs) || !buffs.Remove(buff)) return;
        if (buffs.Count == 0) _unitBuffs.Remove(unit.Uid);
        await _world.ToUnitViewersAsync(unit, BuffRemove,
            new BuffRemoveNotify { UnitId = unit.Uid, BuffGuids = { buff.Guid } });
        var changed = new AOIAttrChange
        {
            EntityUuid = (ulong)unit.Uid, BuffInfo = new AoIBuffInfo { RemoveBuffs = { buff.Guid } },
        };
        if (buff.ShieldRemaining > 0)
            changed.Property.Add(new AoIProperty { Idx = 2003, Data = ShieldAmount(unit) });
        await _world.ToUnitViewersAsync(unit, AoiAttrChange, changed);
        if (buff.Changes.Count > 0 || buff.StateOrdinal > 0)
            foreach (var handler in UnitChanged) await handler(unit);
    }
}
