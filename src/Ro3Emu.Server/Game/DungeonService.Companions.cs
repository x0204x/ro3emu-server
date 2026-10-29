namespace Ro3Emu.Server.Game;

/// <summary>
/// A scripted step's companions (ReconstructedDungeons.ScriptedStep.Companions, CompanionUnit): shown like the step's
/// scene actors, at their scene-editor points, and fighting the step's monsters beside the participants (they walk up to
/// a monster and back to their points, CompanionUnit). They are room
/// props, not room monsters: they never count in Spawned or Killed. A kill of theirs is credited to the first present,
/// loaded participant, a living one first (inferred). One that falls stays down for the rest of the run (inferred: NPCConfig
/// _kRespawnSkill is not used).
/// </summary>
public sealed partial class DungeonService
{
    private long _companionSerial;

    /// <summary>Shows exactly the step's companions: one already shown at the same point stays, the others leave.</summary>
    private async Task SyncCompanionsAsync(Attempt a, IReadOnlyList<ReconstructedDungeons.SceneActor> wanted)
    {
        foreach (var gone in a.Companions.Where(c => !wanted.Any(w => w.NpcId == c.NpcId && w.Point == c.Point)).ToArray())
        {
            a.Companions.Remove(gone);
            await _instances.RemovePropAsync(a.Room, gone);
        }
        foreach (var spec in wanted)
        {
            if (a.Settled || a.Room.Closed) return;
            if (a.Companions.Any(c => c.NpcId == spec.NpcId && c.Point == spec.Point) || a.FallenCompanions.Contains((spec.NpcId, spec.Point)))
                continue;
            if (_catalog.ScenePoint(a.Plan.SceneEntry, spec.Point) is not { } at
                || CompanionUnit.Build(_data, _monsters, spec.NpcId) is not { } kit)
                continue; // the catalog validated both; a changed export only loses the companion
            var unit = new CompanionUnit(kit, _world, _combat)
            {
                Uid = CompanionUnit.UidBase | Interlocked.Increment(ref _companionSerial), SceneEntry = a.Plan.SceneEntry,
                InstanceId = a.Room.Id, X = at.X + spec.Dx, Y = at.Y, Z = at.Z + spec.Dz, Face = at.Face, Point = spec.Point,
                HomeX = at.X + spec.Dx, HomeZ = at.Z + spec.Dz,
                Credit = () => CompanionCredit(a), Fell = fallen => CompanionFellAsync(a, fallen),
            };
            if (await _instances.AddPropAsync(a.Room, unit)) a.Companions.Add(unit);
        }
    }

    /// <summary>(inferred) The participant a companion's hits and kills count for: the first present, loaded one who is
    /// alive, else the first present, loaded one (so a companion's kill still completes the run).</summary>
    private static GameSession? CompanionCredit(Attempt a) => a.Settled || a.Room.Closed ? null
        : CompanionCreditAmong([.. Present(a).Where(s => a.Loaded.Contains(s) && !s.SceneLoading)]);

    internal static GameSession? CompanionCreditAmong(IReadOnlyList<GameSession> loaded) =>
        loaded.FirstOrDefault(s => !s.Dead) ?? (loaded.Count > 0 ? loaded[0] : null);

    /// <summary>A companion fell: it is no longer the step's, and its body leaves after the template's remain time.</summary>
    private Task CompanionFellAsync(Attempt a, CompanionUnit fallen)
    {
        a.Companions.Remove(fallen);
        a.FallenCompanions.Add((fallen.NpcId, fallen.Point));
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(fallen.Template.BodyRemainMs),
            () => a.Room.Closed || fallen.Removed ? Task.CompletedTask : _instances.RemovePropAsync(a.Room, fallen));
        return Task.CompletedTask;
    }
}
