using Romsg;
using static Ro3Emu.Server.Game.ChickenRules;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Survivor Battle poison zone. Transport is proven: DungeonVars 100010002 (countdown, 7 values) and 100010003
/// (shrink of the Main area, 11 values) in 30401.vars / 30404 eventType 4; LC_DunGeon_Chicken.lua:554-607 divides the
/// times by 1000 and the centres by 100, keeps radii as they are, and LC_SceneCom_ChickenPoison.lua:345-490 only
/// interpolates. The schedule itself is (inferred) <see cref="ChickenRules.ZoneSchedule"/>; the damage is authoritative
/// here, once per <see cref="ChickenRules.PoisonTickMs"/>, with the HP-drain buff shown while outside.
/// </summary>
public sealed partial class TimedBattleService
{
    private sealed record ZoneEvent(long AtMs, int VarId, long[] Values);
    private sealed record ZoneShrink(long StartMs, long EndMs, float FromX, float FromZ, float ToX, float ToZ,
        float FromRadius, float ToRadius, int Circle);

    /// <summary>Rolls this match's circle centres (each new circle inside the previous one, a walkable centre when one
    /// is found) and the timed var events of <see cref="ChickenRules.ZoneSchedule"/>.</summary>
    private void PlanZone(ChickenRun run, long start)
    {
        run.Zone.Clear();
        run.ZonePublished = -1;
        var plan = run.Shrinks;
        plan.Clear();
        float cx = IslandX, cz = IslandZ, radius = IslandRadius;
        long at = start;
        int total = ZoneSchedule.Length;
        for (int k = 0; k < total; k++)
        {
            var step = ZoneSchedule[k];
            float room = Math.Max(0, radius - step.EndRadius);
            var (nx, nz) = (cx, cz);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                (nx, nz) = RandomPoint(run, cx, cz, room);
                if (_worldData.Map(ChickenScene)?.Nav?.WalkHeight(nx, nz, 10) != null) break;
            }
            long shrinkStart = at + step.WaitSeconds * 1000L, shrinkEnd = shrinkStart + step.ShrinkSeconds * 1000L;
            run.Zone.Add(new ZoneEvent(at, VarPoisonCountDown,
                [AreaMain, at, shrinkStart, Centi(nx), Centi(nz), step.EndRadius, 1]));
            run.Zone.Add(new ZoneEvent(shrinkStart, VarPoisonShrinkMain,
                [AreaMain, shrinkStart, shrinkEnd, Centi(cx), Centi(cz), Centi(nx), Centi(nz), (long)radius, step.EndRadius, k + 1, total]));
            plan.Add(new ZoneShrink(shrinkStart, shrinkEnd, cx, cz, nx, nz, radius, step.EndRadius, k + 1));
            (cx, cz, radius, at) = (nx, nz, step.EndRadius, shrinkEnd);
        }
    }

    private static long Centi(float v) => (long)Math.Round(v * 100);

    /// <summary>The safe circle now and the circle index the poison strength is read for.</summary>
    private static (float X, float Z, float Radius, int Circle) CurrentCircle(ChickenRun run, long now)
    {
        var plan = run.Shrinks;
        if (plan.Count == 0) return (IslandX, IslandZ, IslandRadius, 0);
        foreach (var s in plan)
        {
            if (now < s.StartMs) return (s.FromX, s.FromZ, s.FromRadius, s.Circle - 1);
            if (now < s.EndMs)
            {
                float f = (float)(now - s.StartMs) / (s.EndMs - s.StartMs);
                return (s.FromX + (s.ToX - s.FromX) * f, s.FromZ + (s.ToZ - s.FromZ) * f,
                    s.FromRadius + (s.ToRadius - s.FromRadius) * f, s.Circle);
            }
        }
        var last = plan[^1];
        return (last.ToX, last.ToZ, last.ToRadius, last.Circle);
    }

    private async Task PublishZoneAsync(Match match, ChickenRun run, long now)
    {
        var due = new List<DungeonVar>();
        while (run.ZonePublished + 1 < run.Zone.Count && run.Zone[run.ZonePublished + 1].AtMs <= now)
        {
            var next = run.Zone[++run.ZonePublished];
            due.Add(Var(next.VarId, next.Values));
        }
        if (due.Count > 0) await BroadcastVarsAsync(match, due);
    }

    /// <summary>The latest published countdown and shrink, for a 30401 (a late or reloaded client).</summary>
    private static IEnumerable<DungeonVar> ZoneVars(ChickenRun run)
    {
        foreach (int id in new[] { VarPoisonCountDown, VarPoisonShrinkMain })
        {
            ZoneEvent? latest = null;
            for (int i = 0; i <= run.ZonePublished && i < run.Zone.Count; i++)
                if (run.Zone[i].VarId == id) latest = run.Zone[i];
            if (latest != null) yield return Var(latest.VarId, latest.Values);
        }
    }

    /// <summary>Living participants outside the circle and their poison: max HP x chicken_poison[circle] / 10000 (inferred).</summary>
    private IEnumerable<(GameSession Player, long Damage)> PoisonVictims(Match match, ChickenRun run, long now)
    {
        var (x, z, radius, circle) = CurrentCircle(run, now);
        long strength = PoisonBasisPoints(circle);
        foreach (var player in InRoom(match).ToArray())
        {
            if (player.Dead || run.Players[player].State != RoundRoleStatus.InWar || player.Progress == null) continue;
            if (MonsterService.Distance(player.Position.X, player.Position.Z, x, z) <= radius) continue;
            long maxHp = _character.Stats(player)[101];
            yield return (player, Math.Max(1, maxHp * strength / 10000));
        }
    }
}
