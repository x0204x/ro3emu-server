using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Surrounding a player. Client data: AIConfig _iIsSurround 1 on 23 rows, with _iSurroundDistance 100, 200, 220 or 0
/// cm (20000001: 100; 24000001/24000002: 220). How the server used them is not in the client; the rule here is
/// inferred: the attackers of one player stand on a circle around it at the distance the straight chase walks to
/// (<see cref="ApproachShare"/> of the attack range), on slots _iSurroundDistance apart along the circle. A monster
/// takes the free slot nearest to its bearing from the player and keeps it while it fights that player; when every slot
/// is taken it shares the nearest one (the old stacking). Slots move with the player. A slot the nav grid cannot reach
/// from the player falls back to the straight approach, and so do tower pets and companions as targets.
/// </summary>
public sealed partial class MonsterService
{
    /// <summary>One circle of slots: the attackers of one player that share a radius and a slot count.</summary>
    private readonly record struct SurroundRing(GameSession Target, float Radius, int Slots);

    private readonly Dictionary<SurroundRing, Dictionary<MonsterUnit, int>> _surroundSlots = [];
    private readonly Dictionary<MonsterUnit, SurroundRing> _surroundRings = [];

    /// <summary>Slot spacing in metres: AIConfig _iSurroundDistance where _iIsSurround is 1, else 0.</summary>
    private static float SurroundSpacing(GameData data, int ai) =>
        AIConfig.Get(data, ai) is { IsSurround: 1, SurroundDistance: > 0 and var cm } ? cm / 100f : 0;

    /// <summary>Where the monster stands around its player, or null for the straight approach.</summary>
    private (float X, float Z)? SurroundSlot(MonsterUnit m, GameSession target)
    {
        float spacing = m.Template.SurroundDistance;
        if (spacing <= 0 || m.Template.SpeedCm <= 0) return null;
        float radius = m.Template.AttackRange * ApproachShare;
        var ring = new SurroundRing(target, radius, Math.Max(1, (int)(2 * MathF.PI * radius / spacing)));
        float angle = 2 * MathF.PI * ClaimSlot(m, ring) / ring.Slots;
        var at = target.Position;
        float x = at.X + radius * MathF.Cos(angle), z = at.Z + radius * MathF.Sin(angle);
        var nav = _worldData.Map(m.SceneEntry)?.Nav;
        if (nav == null || nav.CanWalk(at.X, at.Y, at.Z, x, z)) return (x, z);
        ReleaseSurroundSlot(m); // the straight approach holds no slot
        return null;
    }

    /// <summary>
    /// In reach, a due swing and the swing's action come first, exactly as for a monster without surround: it moves
    /// to its slot only between swings (inferred), so its attack rate stays the original one.
    /// </summary>
    private bool SwingHolds(MonsterUnit m, long now) =>
        Attack != null
        && (now >= m.NextSwingMs || now < m.NextSwingMs - m.Template.SwingIntervalMs + m.Template.SkillMs);

    /// <summary>
    /// True when walking to the slot is this tick's action. In reach and between swings, a monster finishes its walk
    /// to the slot, also one a swing interrupted, then stands while it stays within half a spacing of the slot and
    /// within the margin between the ring and the attack range (the player moved a little); farther, it walks to the
    /// slot again (inferred). A walk that cannot start leaves the straight approach to the caller.
    /// </summary>
    private bool WalksToSlot(MonsterUnit m, MapInstance map, (float X, float Z) slot, bool inReach, long now)
    {
        float away = Distance(m.X, m.Z, slot.X, slot.Z);
        float settled = Math.Min(m.Template.SurroundDistance / 2, m.Template.AttackRange * (1 - ApproachShare));
        bool heading = Distance(m.ToX, m.ToZ, slot.X, slot.Z) <= MinChaseStep;
        if (inReach && !m.Moving && (away <= MinChaseStep || away <= settled && !heading)) return false;
        if (m.Moving && (inReach && heading || now < m.NextChaseMs)) return true;
        if (away <= MinChaseStep) return false;
        float go = Math.Min(MaxSegment, away);
        if (!WalkTo(m, map, m.X + (slot.X - m.X) / away * go, m.Z + (slot.Z - m.Z) / away * go, 1f)) return false;
        m.NextChaseMs = now + ChaseRepathMs;
        return true;
    }

    private int ClaimSlot(MonsterUnit m, SurroundRing ring)
    {
        if (_surroundRings.TryGetValue(m, out var held) && held == ring) return _surroundSlots[ring][m];
        ReleaseSurroundSlot(m);
        if (!_surroundSlots.TryGetValue(ring, out var taken)) _surroundSlots[ring] = taken = [];
        var at = ring.Target.Position;
        int slot = NearestSlot(ring.Slots, MathF.Atan2(m.Z - at.Z, m.X - at.X), taken);
        taken[m] = slot;
        _surroundRings[m] = ring;
        return slot;
    }

    /// <summary>The free slot nearest to the bearing (the lower index on a tie), or the nearest one when all are
    /// taken.</summary>
    private static int NearestSlot(int slots, float bearing, Dictionary<MonsterUnit, int> taken)
    {
        int free = -1, nearest = 0;
        float freeGap = float.MaxValue, nearestGap = float.MaxValue;
        for (int i = 0; i < slots; i++)
        {
            float gap = MathF.Abs(MathF.IEEERemainder(2 * MathF.PI * i / slots - bearing, 2 * MathF.PI));
            if (gap < nearestGap) (nearestGap, nearest) = (gap, i);
            if (gap < freeGap && !taken.ContainsValue(i)) (freeGap, free) = (gap, i);
        }
        return free >= 0 ? free : nearest;
    }

    private void ReleaseSurroundSlot(MonsterUnit m)
    {
        if (!_surroundRings.Remove(m, out var ring)) return;
        if (_surroundSlots.TryGetValue(ring, out var taken) && taken.Remove(m) && taken.Count == 0)
            _surroundSlots.Remove(ring);
    }

    private Task ForgetSurroundSlot(WorldUnit unit)
    {
        if (unit is MonsterUnit monster) ReleaseSurroundSlot(monster);
        return Task.CompletedTask;
    }
}
