using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Maps;

/// <summary>What players see of each other; implemented with the client's AOI messages.</summary>
public interface IAoiSink
{
    /// <summary><paramref name="subject"/> became visible to <paramref name="viewer"/>.</summary>
    Task EnterAsync(GameSession viewer, GameSession subject);

    /// <summary><paramref name="subject"/>, already visible to <paramref name="viewer"/>, moved (or stopped).</summary>
    Task MoveAsync(GameSession viewer, GameSession subject, WalkReq walk);

    /// <summary><paramref name="subject"/>, visible to <paramref name="viewer"/>, turned in place.</summary>
    Task TurnAsync(GameSession viewer, GameSession subject, float face);

    /// <summary><paramref name="subject"/> is no longer visible to <paramref name="viewer"/>.</summary>
    Task LeaveAsync(GameSession viewer, GameSession subject);

    /// <summary>Server units that appeared for / disappeared from <paramref name="viewer"/>, in one message.</summary>
    Task UnitsChangedAsync(GameSession viewer, IReadOnlyList<WorldUnit> entered, IReadOnlyList<long> left);

    /// <summary>A unit visible to <paramref name="viewer"/> moved.</summary>
    Task UnitMoveAsync(GameSession viewer, MovementChangeNtf movement);

    /// <summary>A unit moved and every viewer in the list shows it (one move, many viewers).</summary>
    async Task UnitMoveManyAsync(IReadOnlyList<GameSession> viewers, MovementChangeNtf movement)
    {
        foreach (var viewer in viewers) await UnitMoveAsync(viewer, movement);
    }

    /// <summary><paramref name="subject"/> moved and every viewer in the list shows it (one move, many viewers).</summary>
    async Task MoveManyAsync(IReadOnlyList<GameSession> viewers, GameSession subject, WalkReq walk, MovementChangeNtf? movement)
    {
        foreach (var viewer in viewers)
            await (movement != null ? UnitMoveAsync(viewer, movement) : MoveAsync(viewer, subject, walk));
    }

    /// <summary>Owner-bound pet AOI operations. Default no-ops preserve existing unit-only sinks.</summary>
    Task PetEnterAsync(GameSession viewer, PetFollowerActor pet) => Task.CompletedTask;
    Task PetLeaveAsync(GameSession viewer, PetFollowerActor pet) => Task.CompletedTask;
    Task PetMoveAsync(GameSession viewer, GameSession owner, PetFollowerActor pet, WalkReq walk) => Task.CompletedTask;
    Task PetTurnAsync(GameSession viewer, PetFollowerActor pet, float face) => Task.CompletedTask;
}

/// <summary>
/// Players per scene (map) and who sees whom. Movement is client-authoritative: positions come from WALK.
/// Visibility is a radius check with a little hysteresis so walkers at the edge don't flicker, found through a
/// per-scene grid (a move looks at 3x3 cells, not at every player of the map), and directed: each viewer shows at
/// most its own <see cref="GameSession.ViewLimit"/> players, nearest first when several qualify at once; a viewer
/// that loses one refills from its neighbourhood. <see cref="GameSession.Visible"/> is what a client shows,
/// <see cref="GameSession.Observers"/> who shows it.
/// </summary>
public sealed partial class World(IAoiSink sink, GameLoop loop, ILogger<World> log)
{
    internal void TrackDetached(string name, Func<Task> work) => loop.TrackRuntimeDetached(name, work);
    /// <summary>Map units are the client's world coordinates (spawn 156,192 on a ~300-unit field).</summary>
    public const float ViewRadius = 60f;
    private const float LeaveRadius = ViewRadius + 8f;
    /// <summary>QS_LodLevelCtrl's highest player cap (PC Extreme); lower tiers are 39/49/79/99.</summary>
    public const int MaxViewLimit = 149;
    /// <summary>Grid cell edge: at least the leave radius, so a 3x3 block holds everything a move can affect.</summary>
    private const float CellSize = LeaveRadius;

    private readonly Dictionary<long, List<GameSession>> _scenes = new();
    private readonly Dictionary<long, Dictionary<(int X, int Z), List<GameSession>>> _grids = new();
    private readonly Dictionary<GameSession, (long Scene, int X, int Z)> _cells = new();
    private readonly Dictionary<long, MapInstance> _maps = new();
    private readonly object _sync = new();
    // AOI membership is chosen under _sync, while writes await sockets. Serialize player/pet transitions across
    // both phases so a lineup refresh cannot send a pet before a simultaneous owner's 31037 enter.
    private readonly SemaphoreSlim _aoiDispatch = new(1, 1);
    // MoveCoreAsync runs only under _aoiDispatch (not reentrant), so its per-walk lists are reused (stage G: they were
    // 238 MB a minute of garbage at 500 players).
    private readonly List<(GameSession Viewer, GameSession Subject, Change Kind)> _moveWork = new();
    private readonly List<GameSession> _moveEdges = new(), _moveMovers = new();

    /// <summary>Persistent per-unit presentation follows its 31037 base for each newly entering viewer.</summary>
    public List<Func<GameSession, WorldUnit, Task>> UnitEntered { get; } = new();
    /// <summary>Player presentation follows its AOI base for each new viewer.</summary>
    public List<Func<GameSession, GameSession, Task>> PlayerEntered { get; } = new();
    /// <summary>Presentation owners release per-unit state after any removal path, including instance cleanup.</summary>
    public List<Func<WorldUnit, Task>> UnitRemoved { get; } = new();

    private async Task AfterUnitEnteredAsync(GameSession viewer, WorldUnit unit)
    {
        long generation = viewer.SceneGeneration;
        foreach (var handler in UnitEntered)
        {
            if (viewer.SceneGeneration != generation || unit.Removed || !viewer.VisibleUnits.Contains(unit)) return;
            await Safe(viewer, () => handler(viewer, unit));
        }
    }

    // ---- server units (monsters...) ----

    /// <summary>The simulation of a map, created and put on the game loop's tick on first use.</summary>
    public MapInstance Map(long sceneEntry)
    {
        if (!_maps.TryGetValue(sceneEntry, out var map))
        {
            _maps[sceneEntry] = map = new MapInstance(sceneEntry, this);
            loop.Add(map);
        }
        return map;
    }

    /// <summary>Every server unit on every map.</summary>
    public IEnumerable<WorldUnit> AllUnits() => _maps.Values.SelectMany(m => m.Units);

    public async Task AddUnitAsync(WorldUnit unit)
    {
        var entered = new List<GameSession>();
        await unit.PresentationGate.WaitAsync();
        try
        {
            // Escort carts deliberately remove and re-add the same object in another scene.
            unit.Removed = false;
            Map(unit.WorldScene).Add(unit);
            List<GameSession> near;
            lock (_sync) near = NearLocked(unit.WorldScene, unit.X, unit.Z).ToList();
            foreach (var player in near.Where(p => InRange(p, unit, ViewRadius)))
            {
                unit.Viewers.Add(player);
                player.VisibleUnits.Add(unit);
                await Safe(player, () => sink.UnitsChangedAsync(player, [unit], []));
                entered.Add(player);
            }
        }
        finally { unit.PresentationGate.Release(); }
        foreach (var player in entered)
            if (!unit.Removed && player.VisibleUnits.Contains(unit)) await AfterUnitEnteredAsync(player, unit);
    }

    public async Task RemoveUnitAsync(WorldUnit unit)
    {
        await unit.PresentationGate.WaitAsync();
        try
        {
            if (unit.Removed) return;
            unit.Removed = true;
            // A delayed corpse removal must not recreate a dungeon already disposed on exit.
            if (_maps.TryGetValue(unit.WorldScene, out var map)) map.Remove(unit);
            foreach (var player in unit.Viewers.ToList())
            {
                player.VisibleUnits.Remove(unit);
                await Safe(player, () => sink.UnitsChangedAsync(player, [], [unit.Uid]));
            }
            unit.Viewers.Clear();
        }
        finally { unit.PresentationGate.Release(); }
        foreach (var handler in UnitRemoved) await handler(unit);
    }

    /// <summary>
    /// The unit moved (its position is already updated): players still around get the movement, players it reached
    /// get it as a new unit, players it left lose it.
    /// </summary>
    public async Task MoveUnitAsync(WorldUnit unit, MovementChangeNtf movement)
    {
        var entered = new List<GameSession>();
        await unit.PresentationGate.WaitAsync();
        try
        {
            if (unit.Removed) return;
            Map(unit.WorldScene).Moved(unit);
            List<GameSession> candidates;
            lock (_sync) candidates = NearLocked(unit.WorldScene, unit.X, unit.Z).Concat(unit.Viewers).Distinct().ToList();
            var movers = new List<GameSession>();
            foreach (var player in candidates)
            {
                bool visible = unit.Viewers.Contains(player);
                if (!visible && InRange(player, unit, ViewRadius))
                {
                    unit.Viewers.Add(player);
                    player.VisibleUnits.Add(unit);
                    await Safe(player, () => sink.UnitsChangedAsync(player, [unit], []));
                    entered.Add(player);
                }
                else if (visible && !InRange(player, unit, LeaveRadius))
                {
                    unit.Viewers.Remove(player);
                    player.VisibleUnits.Remove(unit);
                    await Safe(player, () => sink.UnitsChangedAsync(player, [], [unit.Uid]));
                }
                else if (visible) movers.Add(player);
            }
            if (movers.Count > 0) await Safe(movers[0], () => sink.UnitMoveManyAsync(movers, movement));
        }
        finally { unit.PresentationGate.Release(); }
        foreach (var player in entered)
            if (!unit.Removed && player.VisibleUnits.Contains(unit)) await AfterUnitEnteredAsync(player, unit);
    }

    /// <summary>Sends a message about the unit to every player that shows it (HP change, attack...).</summary>
    public async Task ToUnitViewersAsync(WorldUnit unit, uint msgId, Google.Protobuf.IMessage body)
    {
        var viewers = unit.Viewers.ToList();
        if (viewers.Count == 0) return;
        var prepared = new PreparedMessage(msgId, body); // serialized once for every viewer
        foreach (var player in viewers) await Safe(player, () => player.SendPreparedAsync(prepared));
    }

    /// <summary>Units the player should see from where it stands now, compared with what it shows.</summary>
    private async Task RefreshUnitsAsync(GameSession player, List<WorldUnit> entered)
    {
        if (!_maps.TryGetValue(player.WorldScene, out var map)) return;
        var left = new List<long>();
        // Candidates: units near the player plus the ones it shows (they may have walked away).
        var candidates = map.Near(player.Position.X, player.Position.Z, LeaveRadius).Concat(player.VisibleUnits)
            .Distinct().OrderBy(unit => unit.Uid).ToArray();
        var held = new List<WorldUnit>(candidates.Length);
        try
        {
            // Join/Move already owns _aoiDispatch. Unit removal only takes PresentationGate, so
            // every base in this grouped 31037 reaches the client before its eventual leave.
            foreach (var unit in candidates)
            {
                await unit.PresentationGate.WaitAsync();
                held.Add(unit);
            }
            foreach (var unit in candidates)
            {
                if (unit.Removed) continue;
                bool visible = player.VisibleUnits.Contains(unit);
                // A unit can be removed and re-added on another map while this refresh waits
                // for an earlier candidate's publication. The snapshot no longer admits it here.
                if (unit.WorldScene != player.WorldScene || player.WorldScene != map.SceneEntry || !map.Contains(unit))
                {
                    if (visible)
                    {
                        player.VisibleUnits.Remove(unit);
                        unit.Viewers.Remove(player);
                        left.Add(unit.Uid);
                    }
                    continue;
                }
                if (!visible && InRange(player, unit, ViewRadius))
                {
                    player.VisibleUnits.Add(unit);
                    unit.Viewers.Add(player);
                    entered.Add(unit);
                }
                else if (visible && !InRange(player, unit, LeaveRadius))
                {
                    player.VisibleUnits.Remove(unit);
                    unit.Viewers.Remove(player);
                    left.Add(unit.Uid);
                }
            }
            if (entered.Count > 0 || left.Count > 0) await Safe(player, () => sink.UnitsChangedAsync(player, entered, left));
        }
        finally
        {
            for (int i = held.Count - 1; i >= 0; i--) held[i].PresentationGate.Release();
        }
    }

    /// <summary>Unit bases stay ordered under the AOI gates, but extension callbacks must not retain
    /// the player gate needed by room closure/Leave (C/D design L6). A preceding callback may unlink
    /// this viewer or remove another unit, so recheck visibility before each remaining notification.</summary>
    private async Task AfterUnitsEnteredAsync(GameSession player, List<WorldUnit> entered)
    {
        long generation = player.SceneGeneration;
        foreach (var unit in entered)
        {
            if (player.SceneGeneration != generation) return;
            if (!unit.Removed && player.VisibleUnits.Contains(unit)) await AfterUnitEnteredAsync(player, unit);
        }
    }

    private static bool InRange(GameSession player, WorldUnit unit, float radius)
    {
        float dx = player.Position.X - unit.X, dz = player.Position.Z - unit.Z;
        return dx * dx + dz * dz <= radius * radius;
    }

    /// <summary>The session's player is now in its scene (after ENTITYSELF).</summary>
    public async Task JoinAsync(GameSession session, Func<bool>? current = null)
    {
        var entered = new List<WorldUnit>();
        await _aoiDispatch.WaitAsync();
        try { if (current?.Invoke() != false) await JoinCoreAsync(session, entered); }
        finally { _aoiDispatch.Release(); }
        await AfterUnitsEnteredAsync(session, entered);
    }

    private async Task JoinCoreAsync(GameSession session, List<WorldUnit> entered)
    {
        List<(GameSession Viewer, GameSession Subject)> enters = new();
        if (!_rebalancing) { _rebalancing = true; loop.Add(new Rebalancer(this)); }
        lock (_sync)
        {
            RemoveLocked(session, out _);
            session.PetActor?.AtOwner(session);
            session.PetActor?.Viewers.Clear();
            var members = Members(session.WorldScene);
            foreach (var other in ByDistance(session, NearLocked(session.WorldScene, session.Position.X, session.Position.Z)))
            {
                if (!InRange(session, other, ViewRadius)) continue;
                if (session.Visible.Count < session.ViewLimit && Observable(session, other)) { See(session, other); enters.Add((session, other)); }
                if (other.Visible.Count < other.ViewLimit && Observable(other, session)) { See(other, session); enters.Add((other, session)); }
            }
            members.Add(session);
            Index(session);
        }
        log.LogInformation("{Name} joins scene {Scene}; sees {Count}", session.Role?.Name, session.SceneEntry, session.Visible.Count);
        foreach (var (viewer, subject) in enters) await EnterPlayerAsync(viewer, subject);
        if (session.PetActor is { } pet) await EnterPetAsync(session, pet);
        await RefreshUnitsAsync(session, entered);
    }

    /// <summary>
    /// After a silent reconnect the client destroyed every server unit (LC_UnitManager.DestroyAllServerUnit): re-send
    /// the enter of each player, pet and unit this session already shows, to this viewer only. Membership is server
    /// truth and stays as it is, so nobody else sees a leave or a second enter.
    /// </summary>
    public async Task ReplayViewAsync(GameSession session, Func<bool>? current = null)
    {
        var units = new List<WorldUnit>();
        await _aoiDispatch.WaitAsync();
        try { if (current?.Invoke() != false) await ReplayViewCoreAsync(session, units); }
        finally { _aoiDispatch.Release(); }
        await AfterUnitsEnteredAsync(session, units);
    }

    private async Task ReplayViewCoreAsync(GameSession session, List<WorldUnit> units)
    {
        List<GameSession> players;
        PetFollowerActor? ownPet;
        lock (_sync)
        {
            if (!_scenes.TryGetValue(session.WorldScene, out var members) || !members.Contains(session)) return;
            players = ByDistance(session, session.Visible).ToList();
            ownPet = session.PetActor is { } pet && pet.Viewers.Contains(session) ? pet : null;
        }
        foreach (var other in players)
        {
            bool shown;
            lock (_sync) shown = session.Visible.Contains(other);
            if (shown) await EnterPlayerAsync(session, other);
        }
        if (ownPet != null) await Safe(session, () => sink.PetEnterAsync(session, ownPet));
        var candidates = session.VisibleUnits.OrderBy(unit => unit.Uid).ToArray();
        var held = new List<WorldUnit>(candidates.Length);
        try
        {
            foreach (var unit in candidates)
            {
                await unit.PresentationGate.WaitAsync();
                held.Add(unit);
            }
            foreach (var unit in candidates)
                if (!unit.Removed && session.VisibleUnits.Contains(unit) && unit.WorldScene == session.WorldScene)
                    units.Add(unit);
            if (units.Count > 0) await Safe(session, () => sink.UnitsChangedAsync(session, units, []));
        }
        finally
        {
            for (int i = held.Count - 1; i >= 0; i--) held[i].PresentationGate.Release();
        }
    }

    /// <summary>Replace the deployed actor without spawning before the owner enters the world.</summary>
    public async Task SetPetAsync(GameSession owner, PetFollowerActor? next)
    {
        await _aoiDispatch.WaitAsync();
        try { await SetPetCoreAsync(owner, next); }
        finally { _aoiDispatch.Release(); }
    }

    private async Task SetPetCoreAsync(GameSession owner, PetFollowerActor? next)
    {
        PetFollowerActor? previous;
        List<GameSession> oldViewers, newViewers;
        lock (_sync)
        {
            previous = owner.PetActor;
            if (ReferenceEquals(previous, next)) return;
            oldViewers = previous?.Viewers.ToList() ?? [];
            previous?.Viewers.Clear();
            owner.PetActor = next;
            bool joined = _scenes.TryGetValue(owner.WorldScene, out var members) && members.Contains(owner);
            if (joined && next != null)
            {
                next.AtOwner(owner);
                newViewers = [owner, .. owner.Observers];
                foreach (var viewer in newViewers) next.Viewers.Add(viewer);
            }
            else newViewers = [];
        }
        // remove a bound pet before replacing it; create only with owner visible.
        if (previous != null)
            foreach (var viewer in oldViewers) await Safe(viewer, () => sink.PetLeaveAsync(viewer, previous));
        if (next != null)
            foreach (var viewer in newViewers) await Safe(viewer, () => sink.PetEnterAsync(viewer, next));
    }

    private async Task EnterPlayerAsync(GameSession viewer, GameSession subject)
    {
        await Safe(viewer, () => sink.EnterAsync(viewer, subject));
        foreach (var handler in PlayerEntered) await Safe(viewer, () => handler(viewer, subject));
        if (subject.PetActor is { } pet) await EnterPetAsync(viewer, pet);
    }

    private async Task EnterPetAsync(GameSession viewer, PetFollowerActor pet)
    {
        lock (_sync) pet.Viewers.Add(viewer);
        await Safe(viewer, () => sink.PetEnterAsync(viewer, pet));
    }

    private async Task LeavePlayerAsync(GameSession viewer, GameSession subject)
    {
        if (subject.PetActor is { } pet)
        {
            lock (_sync) pet.Viewers.Remove(viewer);
            await Safe(viewer, () => sink.PetLeaveAsync(viewer, pet));
        }
        await Safe(viewer, () => sink.LeaveAsync(viewer, subject));
    }

    public async Task MoveAsync(GameSession session, WalkReq walk, bool forceTeleport = false, MovementChangeNtf? movement = null)
    {
        var entered = new List<WorldUnit>();
        await _aoiDispatch.WaitAsync();
        try { await MoveCoreAsync(session, walk, forceTeleport, movement, entered); }
        finally { _aoiDispatch.Release(); }
        await AfterUnitsEnteredAsync(session, entered);
    }

    private enum Change { Enter, Move, Leave }

    private async Task MoveCoreAsync(GameSession session, WalkReq walk, bool forceTeleport, MovementChangeNtf? movement,
        List<WorldUnit> entered)
    {
        // In order: for each nearby player, its view of the mover, then the mover's view of it (an enter both ways
        // keeps the old "they enter, then I enter" order; a leave likewise). Refills of viewers that lost an edge
        // follow the removals.
        var work = _moveWork;
        work.Clear();
        lock (_sync)
        {
            if (!_scenes.TryGetValue(session.WorldScene, out var members) || !members.Contains(session)) return;
            session.PetActor?.Follow(session, walk, forceTeleport);
            Index(session);
            List<GameSession>? refill = null;
            const float leave2 = LeaveRadius * LeaveRadius, view2 = ViewRadius * ViewRadius;
            // 1. Existing edges: observers get the move or lose the mover; players the mover shows may leave.
            _moveEdges.Clear();
            _moveEdges.AddRange(session.Observers);
            foreach (var other in _moveEdges)
            {
                if (Distance2(session, other) > leave2 || !Observable(other, session)) { Unsee(other, session); work.Add((other, session, Change.Leave)); (refill ??= new()).Add(other); }
                else work.Add((other, session, Change.Move));
            }
            _moveEdges.Clear();
            _moveEdges.AddRange(session.Visible);
            foreach (var other in _moveEdges)
                if (Distance2(session, other) > leave2 || !Observable(session, other)) { Unsee(session, other); work.Add((session, other, Change.Leave)); (refill ??= new()).Add(session); }
            // 2. New edges, nearest first, only where a cap still has room: a saturated crowd sorts nothing.
            bool selfRoom = session.Visible.Count < session.ViewLimit;
            List<(GameSession Player, float D2)>? fresh = null;
            foreach (var other in NearLocked(session.WorldScene, session.Position.X, session.Position.Z))
            {
                if (other == session) continue;
                bool theyCan = other.Visible.Count < other.ViewLimit && !other.Visible.Contains(session) && Observable(other, session);
                bool iCan = selfRoom && !session.Visible.Contains(other) && Observable(session, other);
                if (!theyCan && !iCan) continue;
                float d2 = Distance2(session, other);
                if (d2 <= view2) (fresh ??= new()).Add((other, d2));
            }
            if (fresh != null)
                foreach (var (other, _) in fresh.OrderBy(x => x.D2).ThenBy(x => Key(x.Player)))
                {
                    if (other.Visible.Count < other.ViewLimit && !other.Visible.Contains(session) && Observable(other, session))
                    { See(other, session); work.Add((other, session, Change.Enter)); }
                    if (session.Visible.Count < session.ViewLimit && !session.Visible.Contains(other) && Observable(session, other))
                    { See(session, other); work.Add((session, other, Change.Enter)); }
                }
            if (refill != null)
                foreach (var viewer in refill.Distinct())
                    foreach (var added in RefillLocked(viewer)) work.Add((viewer, added, Change.Enter));
        }
        await RefreshUnitsAsync(session, entered);
        // Each viewer gets at most one of enter/move/leave about the mover, so the moves can go out as one batch
        // (serialized once) without reordering anything a single client sees.
        var movers = _moveMovers;
        movers.Clear();
        foreach (var (viewer, subject, kind) in work)
        {
            switch (kind)
            {
                case Change.Enter: await EnterPlayerAsync(viewer, subject); break;
                case Change.Move: movers.Add(viewer); break;
                case Change.Leave: await LeavePlayerAsync(viewer, subject); break;
            }
        }
        if (movers.Count > 0)
        {
            try { await sink.MoveManyAsync(movers, session, walk, movement); }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
                log.LogDebug("AOI move fan-out from {Name} failed: {Error}", session.Role?.Name, e.Message);
            }
            if (session.PetActor is { HasUpdate: true } movedPet)
                foreach (var viewer in movers) await Safe(viewer, () => sink.PetMoveAsync(viewer, session, movedPet, walk));
        }
        if (session.PetActor is { HasUpdate: true } selfPet)
            await Safe(session, () => sink.PetMoveAsync(session, session, selfPet, walk));
    }

    public async Task TurnAsync(GameSession session, float face)
    {
        await _aoiDispatch.WaitAsync();
        try { await TurnCoreAsync(session, face); }
        finally { _aoiDispatch.Release(); }
    }

    private async Task TurnCoreAsync(GameSession session, float face)
    {
        List<GameSession> watchers;
        PetFollowerActor? pet;
        bool petTurned;
        lock (_sync)
        {
            watchers = session.Observers.ToList();
            pet = session.PetActor;
            petTurned = pet?.Turn(face) ?? false;
        }
        foreach (var viewer in watchers) await Safe(viewer, () => sink.TurnAsync(viewer, session, face));
        if (pet == null || !petTurned) return;
        foreach (var viewer in pet.Viewers.ToList())
            await Safe(viewer, () => sink.PetTurnAsync(viewer, pet, face));
    }

    /// <summary>The player left the scene (disconnect, logout, role switch, map change).</summary>
    public async Task LeaveAsync(GameSession session, Func<bool>? current = null)
    {
        await _aoiDispatch.WaitAsync();
        try { if (current?.Invoke() != false) await LeaveCoreAsync(session); }
        finally { _aoiDispatch.Release(); }
    }

    private async Task LeaveCoreAsync(GameSession session)
    {
        List<GameSession> watchers;
        List<GameSession> petViewers;
        PetFollowerActor? pet;
        List<(GameSession Viewer, GameSession Subject)> refills = new();
        lock (_sync)
        {
            if (!RemoveLocked(session, out watchers)) return;
            pet = session.PetActor;
            petViewers = pet?.Viewers.ToList() ?? [];
            pet?.Viewers.Clear();
            foreach (var viewer in watchers)
                foreach (var added in RefillLocked(viewer)) refills.Add((viewer, added));
        }
        log.LogInformation("{Name} leaves scene {Scene}", session.Role?.Name, session.SceneEntry);
        // LC_Unit_Pet.OnCreateOver binds a positive owner without a nil guard.
        if (pet != null)
            foreach (var viewer in petViewers) await Safe(viewer, () => sink.PetLeaveAsync(viewer, pet));
        foreach (var viewer in watchers) await Safe(viewer, () => sink.LeaveAsync(viewer, session));
        foreach (var (viewer, subject) in refills) await EnterPlayerAsync(viewer, subject);
    }

    /// <summary>
    /// 30005 / 30007: the client's player cap. A lower cap drops the farthest shown players; a higher one fills
    /// from the neighbourhood. Zero or absent keeps the current cap.
    /// </summary>
    public async Task SetViewLimitAsync(GameSession viewer, ulong requested)
    {
        if (requested == 0) return;
        await _aoiDispatch.WaitAsync();
        try
        {
            List<GameSession> dropped = new(), added = new();
            lock (_sync)
            {
                viewer.ViewLimit = (int)Math.Min(requested, (ulong)MaxViewLimit);
                if (!_cells.ContainsKey(viewer)) return; // not in a scene yet: the join applies the cap
                foreach (var subject in viewer.Visible.OrderByDescending(p => Distance2(viewer, p)).ThenByDescending(Key)
                             .Take(Math.Max(0, viewer.Visible.Count - viewer.ViewLimit)).ToList())
                { Unsee(viewer, subject); dropped.Add(subject); }
                added.AddRange(RefillLocked(viewer));
            }
            foreach (var subject in dropped) await LeavePlayerAsync(viewer, subject);
            foreach (var subject in added) await EnterPlayerAsync(viewer, subject);
        }
        finally { _aoiDispatch.Release(); }
    }

    // ---- nearest-first rebalancing at the cap ----

    /// <summary>A closer player outside a full view replaces the farthest shown one only when it is this much
    /// nearer (map units), so two players circling at the edge of a crowd do not swap every second.</summary>
    internal const float RebalanceMargin = 5f;
    /// <summary>Swaps per viewer per pass: a crowd rushing in converges over a few seconds instead of one burst.</summary>
    internal const int RebalanceSwapsPerPass = 10;
    /// <summary>Each viewer is revisited once a second; the viewers are spread over the 20 ticks of that second.</summary>
    private const int RebalanceSlots = 20;
    private bool _rebalancing, _rebalanceRunning;
    private int _rebalanceSlot;

    private sealed class Rebalancer(World world) : IGameTicker
    {
        public void Tick(GameTime time)
        {
            int slot = world._rebalanceSlot;
            world._rebalanceSlot = (slot + 1) % RebalanceSlots;
            if (world._rebalanceRunning) return; // the previous slot is still sending; its viewers wait a second
            world._rebalanceRunning = true;
            world.TrackDetached("aoi rebalance", async () =>
            {
                try { await world.RebalanceAsync(s => (int)((ulong)Key(s) % RebalanceSlots) == slot); }
                finally { world._rebalanceRunning = false; }
            });
        }
    }

    /// <summary>
    /// Admission is nearest-first, but a view that filled up keeps whoever came first: a player who walks up to a
    /// saturated viewer (a siege, a crowded square) stayed a name board while someone at the edge kept the full
    /// model. Each pass swaps the farthest shown players for nearer ones beyond the margin.
    /// </summary>
    internal async Task<int> RebalanceAsync(Func<GameSession, bool> include)
    {
        await _aoiDispatch.WaitAsync();
        try
        {
            List<(GameSession Viewer, GameSession Out, GameSession In)> swaps = new();
            lock (_sync)
            {
                foreach (var (viewer, cell) in _cells)
                {
                    if (viewer.Visible.Count < viewer.ViewLimit || !include(viewer)) continue;
                    RebalanceLocked(viewer, cell.Scene, swaps);
                }
            }
            foreach (var (viewer, gone, came) in swaps)
            {
                await LeavePlayerAsync(viewer, gone);
                await EnterPlayerAsync(viewer, came);
            }
            return swaps.Count;
        }
        finally { _aoiDispatch.Release(); }
    }

    private void RebalanceLocked(GameSession viewer, long scene, List<(GameSession, GameSession, GameSession)> swaps)
    {
        const float view2 = ViewRadius * ViewRadius;
        List<(GameSession Player, float D)>? closer = null;
        float farthest = 0;
        foreach (var shown in viewer.Visible) farthest = MathF.Max(farthest, Distance2(viewer, shown));
        float bar = MathF.Sqrt(farthest) - RebalanceMargin;
        if (bar <= 0) return;
        foreach (var other in NearLocked(scene, viewer.Position.X, viewer.Position.Z))
        {
            if (other == viewer || viewer.Visible.Contains(other) || !Observable(viewer, other)) continue;
            float d2 = Distance2(viewer, other);
            if (d2 > view2 || d2 >= bar * bar) continue;
            (closer ??= new()).Add((other, MathF.Sqrt(d2)));
        }
        if (closer == null) return;
        var incoming = closer.OrderBy(x => x.D).ThenBy(x => Key(x.Player)).Take(RebalanceSwapsPerPass).ToList();
        var outgoing = viewer.Visible.Select(p => (Player: p, D: MathF.Sqrt(Distance2(viewer, p))))
            .OrderByDescending(x => x.D).ThenByDescending(x => Key(x.Player)).Take(incoming.Count).ToList();
        for (int i = 0; i < incoming.Count && i < outgoing.Count; i++)
        {
            if (incoming[i].D + RebalanceMargin >= outgoing[i].D) break;
            Unsee(viewer, outgoing[i].Player);
            See(viewer, incoming[i].Player);
            swaps.Add((viewer, outgoing[i].Player, incoming[i].Player));
        }
    }

    /// <summary>Players whose clients currently show <paramref name="session"/> (its observers).</summary>
    public List<GameSession> Viewers(GameSession session)
    {
        lock (_sync) return session.Observers.ToList();
    }

    public IReadOnlyList<GameSession> PlayersIn(long sceneEntry)
    {
        lock (_sync) return _scenes.TryGetValue(sceneEntry, out var members) ? members.ToList() : [];
    }

    /// <summary>Release an empty private instance and its ticker after its owner leaves.</summary>
    public void RemoveInstance(long instanceId)
    {
        TryRemoveInstance(instanceId);
    }

    public bool TryRemoveInstance(long instanceId)
    {
        if (instanceId == 0 || _scenes.TryGetValue(instanceId, out var players) && players.Count != 0) return false;
        if (_maps.TryGetValue(instanceId, out var map))
        {
            if (map.Units.Count != 0) return false;
            _maps.Remove(instanceId);
            loop.Remove(map);
        }
        _scenes.Remove(instanceId);
        _grids.Remove(instanceId);
        return true;
    }

    /// <param name="watchers">The players that showed <paramref name="session"/> and must be told it left.</param>
    private bool RemoveLocked(GameSession session, out List<GameSession> watchers)
    {
        watchers = session.Observers.ToList();
        foreach (var other in watchers) other.Visible.Remove(session);
        session.Observers.Clear();
        foreach (var other in session.Visible)
        {
            other.Observers.Remove(session);
            other.PetActor?.Viewers.Remove(session);
        }
        session.Visible.Clear();
        Unindex(session);
        // The client drops its units on a map change itself; just forget what this player was shown.
        foreach (var unit in session.VisibleUnits) unit.Viewers.Remove(session);
        session.VisibleUnits.Clear();
        bool removed = false;
        foreach (var members in _scenes.Values) removed |= members.Remove(session);
        return removed;
    }

    private static void See(GameSession viewer, GameSession subject) { viewer.Visible.Add(subject); subject.Observers.Add(viewer); }
    private static void Unsee(GameSession viewer, GameSession subject) { viewer.Visible.Remove(subject); subject.Observers.Remove(viewer); }

    /// <summary>A viewer below its cap takes the nearest players in range it does not show yet.</summary>
    private List<GameSession> RefillLocked(GameSession viewer)
    {
        var added = new List<GameSession>();
        if (viewer.Visible.Count >= viewer.ViewLimit || !_cells.TryGetValue(viewer, out var cell)) return added;
        foreach (var other in ByDistance(viewer, NearLocked(cell.Scene, viewer.Position.X, viewer.Position.Z)))
        {
            if (viewer.Visible.Count >= viewer.ViewLimit) break;
            if (other == viewer || viewer.Visible.Contains(other) || !InRange(viewer, other, ViewRadius) || !Observable(viewer, other)) continue;
            See(viewer, other);
            added.Add(other);
        }
        return added;
    }

    /// <summary>Nearest first; ties by role id, so admission at a cap is deterministic.</summary>
    private static IEnumerable<GameSession> ByDistance(GameSession from, IEnumerable<GameSession> players) =>
        players.OrderBy(p => Distance2(from, p)).ThenBy(Key);

    private static long Key(GameSession p) => p.Role?.Id ?? 0;

    private static (int X, int Z) Cell(float x, float z) => ((int)MathF.Floor(x / CellSize), (int)MathF.Floor(z / CellSize));

    private void Index(GameSession session)
    {
        long scene = session.WorldScene;
        var (x, z) = Cell(session.Position.X, session.Position.Z);
        if (_cells.TryGetValue(session, out var old))
        {
            if (old == (scene, x, z)) return;
            Unindex(session);
        }
        if (!_grids.TryGetValue(scene, out var grid)) _grids[scene] = grid = new();
        if (!grid.TryGetValue((x, z), out var list)) grid[(x, z)] = list = new();
        list.Add(session);
        _cells[session] = (scene, x, z);
    }

    private void Unindex(GameSession session)
    {
        if (!_cells.Remove(session, out var old)) return;
        if (_grids.TryGetValue(old.Scene, out var grid) && grid.TryGetValue((old.X, old.Z), out var list))
        {
            list.Remove(session);
            if (list.Count == 0) grid.Remove((old.X, old.Z));
            if (grid.Count == 0) _grids.Remove(old.Scene);
        }
    }

    /// <summary>Players of the scene in the 3x3 cells around a point (a superset of everyone within the leave radius).</summary>
    /// <summary>Appends the players of a world scene within <paramref name="radius"/> of (x, z) to <paramref name="into"/>,
    /// from the AOI grid and without allocating (aura ticks run several times a second).</summary>
    public void CollectPlayers(long scene, float x, float z, float radius, List<GameSession> into)
    {
        float r2 = radius * radius;
        lock (_sync)
        {
            if (!_grids.TryGetValue(scene, out var grid)) return;
            var (x0, z0) = Cell(x - radius, z - radius);
            var (x1, z1) = Cell(x + radius, z + radius);
            for (int cx = x0; cx <= x1; cx++)
                for (int cz = z0; cz <= z1; cz++)
                    if (grid.TryGetValue((cx, cz), out var list))
                        foreach (var p in list)
                        {
                            float dx = p.Position.X - x, dz = p.Position.Z - z;
                            if (dx * dx + dz * dz <= r2) into.Add(p);
                        }
        }
    }

    private IEnumerable<GameSession> NearLocked(long scene, float x, float z)
    {
        if (!_grids.TryGetValue(scene, out var grid)) yield break;
        var (cx, cz) = Cell(x, z);
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue((cx + dx, cz + dz), out var list))
                    foreach (var p in list) yield return p;
    }

    private static float Distance2(GameSession a, GameSession b)
    {
        float dx = a.Position.X - b.Position.X, dz = a.Position.Z - b.Position.Z;
        return dx * dx + dz * dz;
    }

    private List<GameSession> Members(long sceneEntry)
    {
        if (!_scenes.TryGetValue(sceneEntry, out var members)) _scenes[sceneEntry] = members = new();
        return members;
    }

    private static bool InRange(GameSession a, GameSession b, float radius)
    {
        float dx = a.Position.X - b.Position.X, dz = a.Position.Z - b.Position.Z;
        return dx * dx + dz * dz <= radius * radius;
    }

    /// <summary>A send to one viewer must not stop updates to the others (its socket may be closing).</summary>
    private async Task Safe(GameSession viewer, Func<Task> send)
    {
        try { await send(); }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            log.LogDebug("AOI send to {Name} failed: {Error}", viewer.Role?.Name, e.Message);
        }
    }
}
