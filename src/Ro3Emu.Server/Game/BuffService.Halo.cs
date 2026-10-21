using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// HaloConfig auras: a player carrying a buff whose id has a HaloConfig row (Battle Chant 1150501/1150502 from its
/// skill's _kBuff1, King's Grace 1161401/1162001, Shield Defense 1161602, Holy Light 2328101, the adventure-card orbits
/// 15302101-15303901) puts the aura buff's target buffs (BuffRuleTables.HaloRule.Applied) on up to _iTargetMax units
/// within _iRadius, re-evaluated every _iTickTime. Only carried auras with target buffs are registered; an idle server
/// ticks nothing. The candidates come from the AOI grids (World.CollectPlayers, MapInstance.CollectNear) into reused
/// lists, and a tick sends nothing unless a target entered, left or lost its aura buff.
/// (inferred) A target keeps the aura buff while it stays in range, with the carrier buff's remaining time (an hour
/// for a permanent carrier, refreshed before it ends); leaving range or the carrier losing the aura removes it.
/// _ilsSuperposition 0 everywhere: one instance per target whoever's aura it is (the first carrier's stays). Nearest
/// targets first when more than the cap stand in range. Monster-carried auras (Madogear protection, siege orbits)
/// have no target buff in their rows and are not simulated.
/// </summary>
public sealed partial class BuffService
{
    private sealed class HaloInstance
    {
        public required GameSession Carrier { get; init; }
        public required long CarrierId { get; init; }
        public required long Guid { get; init; }
        public required BuffRuleTables.HaloRule Rule { get; init; }
        public long NextMs { get; set; }
        public bool Busy { get; set; }
        /// <summary>A target buff ending before this is given again (two ticks ahead, never past the carrier's own end).</summary>
        public long RefreshBefore { get; set; }
        /// <summary>Targets that hold this aura's buffs (GameSession or WorldUnit).</summary>
        public HashSet<object> Affected { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<object> Desired { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class NearestFirst : IComparer<(float D2, object Target)>
    {
        public static readonly NearestFirst Instance = new();
        public int Compare((float D2, object Target) a, (float D2, object Target) b) => a.D2.CompareTo(b.D2);
    }

    private readonly List<HaloInstance> _halos = new();
    private readonly List<GameSession> _haloPlayers = new();
    private readonly List<WorldUnit> _haloUnits = new();
    private readonly List<(float D2, object Target)> _haloCandidates = new();

    /// <summary>CombatService: may this player's aura treat that player as an enemy (territory/event PvP).</summary>
    internal Func<GameSession, GameSession, bool>? HaloHostilePlayer { get; set; }
    /// <summary>CombatService: may this player's aura reach that monster (same rule as its hits).</summary>
    internal Func<GameSession, MonsterUnit, bool>? HaloEnemyMonster { get; set; }

    internal int HaloCount => _halos.Count;

    private void TrackHalo(GameSession carrier, ActiveBuff buff)
    {
        if (carrier.Role == null || !Rules.Halos.TryGetValue(buff.ConfigId, out var rule) || rule.Applied.Length == 0) return;
        foreach (var halo in _halos) if (halo.Guid == buff.Guid && ReferenceEquals(halo.Carrier, carrier)) return;
        _halos.Add(new HaloInstance
        {
            Carrier = carrier, CarrierId = carrier.Role.Id, Guid = buff.Guid, Rule = rule, NextMs = MonsterService.NowMs,
        });
    }

    private static ActiveBuff? CarrierBuff(HaloInstance halo, long now)
    {
        var carrier = halo.Carrier;
        if (carrier.Role?.Id != halo.CarrierId || carrier.Dead || carrier.SceneLoading) return null;
        foreach (var buff in carrier.Buffs) if (buff.Guid == halo.Guid) return buff.ExpireMs > now ? buff : null;
        return null;
    }

    private void TickHalos(long now)
    {
        for (int i = _halos.Count - 1; i >= 0; i--)
        {
            var halo = _halos[i];
            if (halo.Busy || now < halo.NextMs) continue;
            halo.NextMs = now + halo.Rule.TickMs;
            var carried = CarrierBuff(halo, now);
            if (carried == null)
            {
                _halos[i] = _halos[^1];
                _halos.RemoveAt(_halos.Count - 1);
                halo.Desired.Clear();
                if (halo.Affected.Count == 0) continue;
                halo.Busy = true;
                _loop.TrackRuntimeDetached("aura end", () => ReconcileHaloAsync(halo, 0));
                continue;
            }
            CollectHaloTargets(halo);
            halo.RefreshBefore = Math.Min(now + 2L * halo.Rule.TickMs, carried.ExpireMs - 1);
            bool changed = !halo.Desired.SetEquals(halo.Affected);
            // A target that lost the buff (dispelled, cleared by death) or whose buff ends within two ticks gets it again.
            if (!changed)
                foreach (var target in halo.Affected)
                    if (!HoldsAura(target, halo, halo.RefreshBefore)) { changed = true; break; }
            if (!changed) continue;
            long remaining = carried.ExpireMs == long.MaxValue ? 3_600_000 : carried.ExpireMs - now;
            int duration = (int)Math.Clamp(remaining, halo.Rule.TickMs * 3L, int.MaxValue);
            halo.Busy = true;
            _loop.TrackRuntimeDetached("aura tick", () => ReconcileHaloAsync(halo, duration));
        }
    }

    /// <summary>The aura's current targets into <c>Desired</c>, nearest first up to the cap.</summary>
    private void CollectHaloTargets(HaloInstance halo)
    {
        var carrier = halo.Carrier;
        var rule = halo.Rule;
        float x = carrier.Position.X, z = carrier.Position.Z;
        long scene = carrier.WorldScene;
        _haloCandidates.Clear();
        _haloPlayers.Clear();
        _world.CollectPlayers(scene, x, z, rule.Radius, _haloPlayers);
        foreach (var p in _haloPlayers)
        {
            if (p.Role == null || p.Dead || p.SceneLoading) continue;
            if (ReferenceEquals(p, carrier))
            {
                if (rule.IncludeSelf) _haloCandidates.Add((-1, p)); // the carrier first, inside the cap
                continue;
            }
            if ((HaloHostilePlayer?.Invoke(carrier, p) == true) != rule.Hostile) continue;
            float dx = p.Position.X - x, dz = p.Position.Z - z;
            _haloCandidates.Add((dx * dx + dz * dz, p));
        }
        if (rule.Hostile)
        {
            _haloUnits.Clear();
            _world.Map(scene).CollectNear(x, z, rule.Radius, _haloUnits);
            foreach (var unit in _haloUnits)
            {
                if (unit is not MonsterUnit m || m.Dead || m.Hp <= 0 || HaloEnemyMonster?.Invoke(carrier, m) == false) continue;
                float dx = m.X - x, dz = m.Z - z;
                _haloCandidates.Add((dx * dx + dz * dz, m));
            }
        }
        if (_haloCandidates.Count > rule.TargetMax) _haloCandidates.Sort(NearestFirst.Instance);
        halo.Desired.Clear();
        for (int k = 0; k < _haloCandidates.Count && k < rule.TargetMax; k++) halo.Desired.Add(_haloCandidates[k].Target);
    }

    /// <summary>Does the target hold a live buff of every one the aura applies, from any carrier (superposition 0).</summary>
    private bool HoldsAura(object target, HaloInstance halo, long until)
    {
        foreach (int id in halo.Rule.Applied)
        {
            bool found = false;
            if (target is GameSession p)
            {
                foreach (var b in p.Buffs) if (b.ConfigId == id && b.ExpireMs > until) { found = true; break; }
            }
            else if (target is WorldUnit u && _unitBuffs.TryGetValue(u.Uid, out var list))
                foreach (var b in list) if (b.ConfigId == id && b.ExpireMs > until) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }

    private async Task ReconcileHaloAsync(HaloInstance halo, int durationMs)
    {
        try
        {
            foreach (var target in halo.Affected)
                if (!halo.Desired.Contains(target)) await TakeAuraAsync(target, halo);
            foreach (var target in halo.Desired)
                if (!halo.Affected.Contains(target) || !HoldsAura(target, halo, halo.RefreshBefore))
                    await GiveAuraAsync(target, halo, durationMs);
            halo.Affected.Clear();
            halo.Affected.UnionWith(halo.Desired);
        }
        finally { halo.Busy = false; }
    }

    private async Task GiveAuraAsync(object target, HaloInstance halo, int durationMs)
    {
        if (durationMs <= 0) return;
        foreach (int id in halo.Rule.Applied)
        {
            if (target is GameSession p)
            {
                if (p.Role == null || p.Buffs.Any(b => b.ConfigId == id && b.ExpireMs > halo.RefreshBefore)) continue;
                await AddAsync(p, id, durationMs, halo.CarrierId);
            }
            else if (target is WorldUnit unit && _world.Map(unit.WorldScene).Contains(unit))
            {
                if (_unitBuffs.TryGetValue(unit.Uid, out var list) && list.Any(b => b.ConfigId == id && b.ExpireMs > halo.RefreshBefore)) continue;
                await AddSupportAsync(unit, id, durationMs, halo.CarrierId);
            }
        }
    }

    /// <summary>Remove this carrier's aura buffs from a target (another carrier's instance stays).</summary>
    private async Task TakeAuraAsync(object target, HaloInstance halo)
    {
        foreach (int id in halo.Rule.Applied)
        {
            if (target is GameSession p)
            {
                if (p.Buffs.FirstOrDefault(b => b.ConfigId == id && b.SourceId == halo.CarrierId) is { } buff) await RemoveAsync(p, buff);
            }
            else if (target is WorldUnit unit && _unitBuffs.TryGetValue(unit.Uid, out var list)
                     && list.FirstOrDefault(b => b.ConfigId == id && b.SourceId == halo.CarrierId) is { } unitBuff)
                await RemoveUnitBuffAsync(unit, unitBuff);
        }
    }
}
