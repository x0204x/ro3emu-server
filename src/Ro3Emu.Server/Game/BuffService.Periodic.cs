using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Buffs that hurt or heal their bearer on a cadence (BuffRuleTables.Periodic: Burn, Bleeding, Poisoned, the field
/// MVP curses Divine Retribution / Dark Curse, Breath Medicine, Tension Relax). Only buffs that tick are registered,
/// one entry per buff GUID; a tick reads the live buff (its piles, its expiry) and CombatService applies the amount, so
/// an idle server walks an empty list. A buff that is gone (expired, dispelled, death or scene clear, unit removed)
/// drops its entry on its next due time.
/// </summary>
public sealed partial class BuffService
{
    /// <summary>The applier as it stood when the buff landed: the "applier's Fire Magic Damage" the descriptions name
    /// is taken from this snapshot (inferred: no tick re-reads a source that may have died or left).</summary>
    public sealed record PeriodicSource(long Uid, long Atk, long Matk, int Level, int Element, GameSession? Player);

    internal sealed class PeriodicEffect
    {
        public required BuffRuleTables.PeriodicRule Rule { get; init; }
        public GameSession? Player { get; init; }
        public WorldUnit? Unit { get; init; }
        public required long TargetId { get; init; }
        public required long Guid { get; init; }
        public PeriodicSource? Source { get; set; }
        public long NextMs { get; set; }
        public bool Busy { get; set; }
    }

    private readonly List<PeriodicEffect> _periodic = new();
    private readonly Dictionary<long, PeriodicEffect> _periodicByGuid = new();

    /// <summary>CombatService: who applied a buff (a player by role id, a monster by uid) in that world scene.</summary>
    internal Func<long, long, PeriodicSource?>? ResolvePeriodicSource { get; set; }

    /// <summary>CombatService: one due tick of a live buff (damage through the protected HP path, or a heal).</summary>
    internal Func<PeriodicEffect, ActiveBuff, Task>? PeriodicTick { get; set; }

    internal int PeriodicCount => _periodic.Count;

    /// <summary>Register (or refresh the applier of) a buff that ticks. The first tick is one interval after landing,
    /// or at expiry for a buff shorter than that (a 200 ms potion buff heals once).</summary>
    private void TrackPeriodic(GameSession? player, WorldUnit? unit, ActiveBuff buff)
    {
        if (!Rules.Periodic.TryGetValue(buff.ConfigId, out var rule)) return;
        long scene = player?.WorldScene ?? unit!.WorldScene;
        var source = rule.Kind == BuffRuleTables.PeriodicKind.Damage ? ResolvePeriodicSource?.Invoke(buff.SourceId, scene) : null;
        if (_periodicByGuid.TryGetValue(buff.Guid, out var existing))
        {
            if (source != null) existing.Source = source; // a refresh by another applier hurts with its numbers
            return;
        }
        if (rule.Kind == BuffRuleTables.PeriodicKind.Damage && source == null) return; // nothing to scale (inferred)
        var effect = new PeriodicEffect
        {
            Rule = rule, Player = player, Unit = unit, TargetId = player?.Role?.Id ?? unit!.Uid, Guid = buff.Guid, Source = source,
            NextMs = MonsterService.NowMs + Math.Min(rule.IntervalMs, Math.Max(0, buff.DurationMs)),
        };
        _periodic.Add(effect);
        _periodicByGuid[buff.Guid] = effect;
    }

    private ActiveBuff? LiveBuff(PeriodicEffect effect)
    {
        if (effect.Player is { } p)
        {
            if (p.Role?.Id != effect.TargetId) return null;
            foreach (var buff in p.Buffs) if (buff.Guid == effect.Guid) return buff;
            return null;
        }
        if (!_unitBuffs.TryGetValue(effect.TargetId, out var list)) return null;
        foreach (var buff in list) if (buff.Guid == effect.Guid) return buff;
        return null;
    }

    private void TickPeriodic(long now)
    {
        for (int i = _periodic.Count - 1; i >= 0; i--)
        {
            var effect = _periodic[i];
            if (effect.Busy || now < effect.NextMs) continue;
            var buff = LiveBuff(effect);
            // A tick due at the expiry moment still lands (60 s of "per second" is 60 ticks).
            if (buff == null || effect.NextMs > buff.ExpireMs)
            {
                _periodic[i] = _periodic[^1];
                _periodic.RemoveAt(_periodic.Count - 1);
                _periodicByGuid.Remove(effect.Guid);
                continue;
            }
            effect.NextMs += effect.Rule.IntervalMs;
            if (effect.NextMs <= now) effect.NextMs = now + effect.Rule.IntervalMs; // a late loop does not burst
            if (PeriodicTick is not { } tick) continue;
            effect.Busy = true;
            _loop.TrackRuntimeDetached("periodic buff tick", async () =>
            {
                try { await tick(effect, buff); }
                finally { effect.Busy = false; }
            });
        }
    }
}
