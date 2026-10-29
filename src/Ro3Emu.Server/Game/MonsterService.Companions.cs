using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Monsters answering allied companions (CompanionUnit). (inferred) A monster a companion hits keeps it as a second
/// target: while a player holds it (MonsterUnit.Target) it fights the player, and once none does it chases and swings
/// at that companion with its normal attack, until the companion falls, leaves or is out of its leash. A player comes
/// first: an aggressive monster answering a companion still looks for players as when idle, and one it finds becomes
/// its target. A monster that cannot move stops answering a companion out of its reach. A monster keeps the companion
/// it answers while that one stands; the server script of the original fight is not in the client.
/// Tower pets (TowerPetUnit, MonsterUnit.PetTarget) are a separate path and do not change.
/// </summary>
public sealed partial class MonsterService
{
    // (inferred) The tower pets' timings (FightPet): the first swing's delay, how often a chase re-aims, the reach slack
    // and how deep into its attack range a monster walks.
    private const int CompanionFirstSwingMs = 300, CompanionChaseMs = 400;
    private const float CompanionReachSlack = 0.5f, CompanionApproachShare = 0.8f;

    private readonly Dictionary<MonsterUnit, CompanionUnit> _companionTargets = [];

    /// <summary>Set by CombatService: a monster's normal attack on a companion (the brain decides when).</summary>
    internal Func<MonsterUnit, CompanionUnit, Task>? AttackCompanion { get; set; }

    /// <summary>A companion's hit: the monster may answer it (see the type's summary).</summary>
    internal void Engage(MonsterUnit m, CompanionUnit companion)
    {
        if (m.Dead || m.Hp <= 0 || companion.Dead || companion.Hp <= 0 || m.WorldScene != companion.WorldScene) return;
        if (_companionTargets.TryGetValue(m, out var held) && !held.Dead) return;
        if (m.Target == null) m.NextSwingMs = NowMs + CompanionFirstSwingMs;
        _companionTargets[m] = companion;
    }

    private Task ForgetCompanionTargets(WorldUnit unit)
    {
        if (_companionTargets.Count == 0) return Task.CompletedTask;
        if (unit is MonsterUnit monster) _companionTargets.Remove(monster);
        else if (unit is CompanionUnit companion)
            foreach (var m in _companionTargets.Where(p => ReferenceEquals(p.Value, companion)).Select(p => p.Key).ToArray())
                _companionTargets.Remove(m);
        return Task.CompletedTask;
    }

    /// <summary>Tick's step for a monster no player holds: true when it is fighting a companion (or took a player).</summary>
    private bool FightsCompanion(MonsterUnit m, MapInstance map, long now)
    {
        if (!_companionTargets.TryGetValue(m, out var target)) return false;
        if (target.Dead || target.Hp <= 0 || target.WorldScene != m.WorldScene
            || Distance(m.Home.X, m.Home.Z, target.X, target.Z) > m.Template.LeashDistance)
        {
            _companionTargets.Remove(m);
            Disengage(m, map);
            return true;
        }
        float dist = Distance(m.X, m.Z, target.X, target.Z);
        bool inReach = m.BusyUntilMs > now || dist <= m.Template.AttackRange + CompanionReachSlack;
        if (!inReach && m.Template.SpeedCm <= 0)
        {
            // It cannot walk to the companion: it stops answering it, and the rest of the tick (player scan, return
            // home) runs as when idle. A later hit of the companion draws it again only while it is in reach.
            _companionTargets.Remove(m);
            return false;
        }
        if (TakesPlayer(m)) return true;
        m.Returning = false;
        if (inReach)
        {
            if (m.Moving)
            {
                m.Moving = false;
                SendStop(m, now);
            }
            if (m.BusyUntilMs <= now && now >= m.NextSwingMs && AttackCompanion != null) SwingAtCompanion(m, target, now);
            return true;
        }
        if (!m.Moving || now >= m.NextChaseMs)
        {
            m.NextChaseMs = now + CompanionChaseMs;
            float go = Math.Min(MaxSegment, dist - m.Template.AttackRange * CompanionApproachShare);
            if (go > 0.2f) WalkTo(m, map, m.X + (target.X - m.X) / dist * go, m.Z + (target.Z - m.Z) / dist * go, 1f);
        }
        return true;
    }

    /// <summary>A player comes first: the aggressive scan Tick runs for an idle monster; one in sight becomes its target.</summary>
    private bool TakesPlayer(MonsterUnit m)
    {
        if (!m.Template.Aggressive || m.Viewers.Count == 0 || m.Returning || NearestTarget(m) is not { } near) return false;
        Engage(m, near);
        return ReferenceEquals(m.Target, near);
    }

    // The lambda lives here, not in FightsCompanion, so a tick that does not swing allocates no closure.
    private void SwingAtCompanion(MonsterUnit m, CompanionUnit target, long now)
    {
        m.NextSwingMs = now + m.Template.SwingIntervalMs;
        m.Face = Angle(m.X, m.Z, target.X, target.Z);
        _loop.TrackRuntimeDetached("monster companion attack", () => AttackCompanion!(m, target));
    }
}
