using System.Runtime.CompilerServices;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Session-only private map lifecycle shared by tower and ordinary dungeons. SceneService owns the 70010/30005
/// handoff and World owns AOI; this service owns the instance id, each participant's outside return point, and
/// spawned actors. The return-on-reconnect and last-participant cleanup policies are (inferred).
/// </summary>
public sealed class InstanceService
{
    private readonly RoleStore _roles;
    private readonly SceneService _scene;
    private readonly World _world;
    private readonly MonsterService _monsters;
    private readonly CombatService _combat;
    private readonly GameLoop _loop;
    private readonly Dictionary<long, Room> _rooms = new();
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<GameSession, ReturnRecovery> _pendingReturns = new();

    public InstanceService(RoleStore roles, SceneService scene, World world, MonsterService monsters, CombatService combat, GameLoop loop)
    {
        this._roles = roles;
        this._scene = scene;
        this._world = world;
        this._monsters = monsters;
        this._combat = combat;
        this._loop = loop;
        scene.Leaving.Add(AbandonAsync);
        scene.BeforeSelf.Add(RecoverOnReturnAsync);
        scene.AfterLoaded.Add(NotifyRecoveredAsync);
    }

    private sealed class ReturnRecovery(Role role, AttachmentLease? lease, long generation, long outsideScene, long sourceInstance)
    {
        public Role Role { get; } = role;
        public AttachmentLease? Lease { get; } = lease;
        public long Generation { get; } = generation;
        public long OutsideScene { get; } = outsideScene;
        public long SourceInstance { get; } = sourceInstance;
        public bool Revived { get; set; }
        public TaskCompletionSource Invalidated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private ReturnRecovery? PendingReturn(GameSession session)
    {
        lock (_pendingReturns) return _pendingReturns.TryGetValue(session, out var state) ? state : null;
    }

    private void SetPendingReturn(GameSession session, ReturnRecovery state)
    {
        lock (_pendingReturns)
        {
            if (_pendingReturns.TryGetValue(session, out var previous)) previous.Invalidated.TrySetResult();
            _pendingReturns.Remove(session);
            _pendingReturns.Add(session, state);
        }
    }

    private void ClearPendingReturn(GameSession session, ReturnRecovery? expected = null)
    {
        lock (_pendingReturns)
            if (_pendingReturns.TryGetValue(session, out var current)
                && (expected == null || ReferenceEquals(current, expected)))
            {
                _pendingReturns.Remove(session);
                current.Invalidated.TrySetResult();
            }
    }

    private bool Returned(GameSession session, ReturnRecovery state) =>
        ReferenceEquals(PendingReturn(session), state)
        && ReferenceEquals(session.Role, state.Role)
        && ReferenceEquals(session.Attachment, state.Lease) && state.Lease?.Cancelled != true
        && session.SceneGeneration == state.Generation && session.Handoff?.Generation == state.Generation
        && session.InstanceId == 0 && session.SceneEntry == state.OutsideScene && !session.SceneLoading
        && session.TowerAttempt == null && session.DungeonAttempt == null;

    /// <summary>(inferred) Only an opted-in private-room return restores a dead member before ENTITYSELF.
    /// Wait for death publication and competing item revival; a living member retains current HP/SP.</summary>
    private async Task RecoverOnReturnAsync(GameSession session)
    {
        if (PendingReturn(session) is not { } state) return;
        while (true)
        {
            if (!Returned(session, state)) { ClearPendingReturn(session, state); return; }
            if (!session.Dead) return;
            var available = _combat.WaitForReviveAvailabilityAsync(session);
            if (!available.IsCompleted) await Task.WhenAny(available, state.Invalidated.Task);
            if (state.Invalidated.Task.IsCompleted) return;
            if (!Returned(session, state)) { ClearPendingReturn(session, state); return; }
            if (!session.Dead) return;
            if (!_combat.TryClaimRevive(session)) continue;
            try
            {
                state.Revived = _combat.CommitReviveClaim(session);
                if (state.Revived) await _combat.RefreshRevivedAsync(session);
            }
            finally { _combat.ReleaseReviveClaim(session); }
            return;
        }
    }

    private async Task NotifyRecoveredAsync(GameSession session)
    {
        if (PendingReturn(session) is not { } state) return;
        bool recovered = state.Revived && Returned(session, state);
        ClearPendingReturn(session, state);
        if (recovered)
            await session.SendGameAsync(CombatService.ReviveInfo,
                new SyncClientPlayerReviveInfoNtf { BDead = false, SceneEntry = session.SceneEntry });
    }

    public sealed record Member(GameSession Session, long ReturnScene, MapPoint ReturnPoint, Role Role,
        AttachmentLease? Lease, long SourceScene, long SourceInstance, long SourceGeneration);

    public sealed class Room
    {
        internal readonly object Gate = new();
        internal readonly HashSet<GameSession> Remaining;
        internal readonly List<Member> Participants;
        internal readonly List<MonsterUnit> Actors = new();
        /// <summary>Non-combat scene units (scripted scene actors), removed with the monsters.</summary>
        internal readonly List<WorldUnit> Props = new();
        internal readonly SemaphoreSlim Cleanup = new(1, 1);
        internal TaskCompletionSource PendingDone = Completed();
        internal bool FinalCleanupScheduled;
        internal int PendingCount;
        internal int Generation;
        internal int EntryGeneration;

        internal Room(long id, long sceneEntry, IReadOnlyList<Member> members)
        {
            Id = id;
            SceneEntry = sceneEntry;
            Participants = members.ToList();
            Remaining = new HashSet<GameSession>(members.Select(m => m.Session));
        }

        public long Id { get; }
        public long SceneEntry { get; }
        /// <summary>Participants and their latest entry's world return point; one member per session.</summary>
        public IReadOnlyList<Member> Members { get { lock (Gate) return Participants.ToArray(); } }
        public bool Closed { get { lock (Gate) return _closed; } }
        public IReadOnlyList<MonsterUnit> Monsters { get { lock (Gate) return Actors.ToArray(); } }
        public IReadOnlyList<Member> ActiveMembers { get { lock (Gate) return Members.Where(m => Remaining.Contains(m.Session)).ToArray(); } }

        private bool _closed;
        internal bool IsClosed { get => _closed; set => _closed = value; }
        internal Member? MemberFor(GameSession session)
        {
            lock (Gate) return Remaining.Contains(session)
                ? Members.FirstOrDefault(m => ReferenceEquals(m.Session, session)) : null;
        }
        private static TaskCompletionSource Completed()
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            done.SetResult();
            return done;
        }

        internal void SpawnStarted()
        {
            if (PendingCount++ == 0) PendingDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal void SpawnFinished()
        {
            if (--PendingCount == 0) PendingDone.SetResult();
        }
    }

    public Room? Find(long instanceId)
    {
        lock (_gate) return _rooms.GetValueOrDefault(instanceId) is { Closed: false } room ? room : null;
    }

    /// <summary>
    /// Reserve a later entrant in a shared event room. The event owner validates membership and its schedule;
    /// the existing room's identity, outside return point and last-member cleanup remain authoritative here.
    /// </summary>
    public bool AddParticipant(Room room, GameSession session, bool continuation = false)
    {
        if (session.Role == null || session.SceneLoading) return false;
        var previous = continuation ? Find(session.InstanceId)?.MemberFor(session) : null;
        if (session.InstanceId != 0 && previous == null) return false;
        lock (room.Gate)
        {
            if (room.IsClosed) return false;
            if (room.Remaining.Contains(session))
                return room.MemberFor(session) is { } existing && ReferenceEquals(session.Role, existing.Role)
                    && ReferenceEquals(session.Attachment, existing.Lease)
                    && session.SceneGeneration == existing.SourceGeneration;
            var point = new MapPoint(session.Position.X, session.Position.Y, session.Position.Z, session.Face);
            if (previous == null) _roles.SaveReturnPositions([new RoleReturnPosition(session.Role.Id,
                session.SceneEntry, point.X, point.Y, point.Z, point.Face)]);
            // Shared event rooms survive another member's departure. Re-entry replaces that session's
            // reservation, otherwise ActiveMembers would enumerate the same participant twice.
            room.Participants.RemoveAll(member => ReferenceEquals(member.Session, session));
            room.Participants.Add(new Member(session, previous?.ReturnScene ?? session.SceneEntry,
                previous?.ReturnPoint ?? point, session.Role, session.Attachment,
                session.SceneEntry, session.InstanceId, session.SceneGeneration));
            room.Remaining.Add(session);
            return true;
        }
    }

    /// <summary>
    /// Allocate one private map for a fixed participant set. All map and product gates must be checked by the
    /// caller; the map's born point and every member's session state are checked here before any position is saved.
    /// A continuation retains the original outside return point (inferred).
    /// </summary>
    public Room Create(long sceneEntry, IReadOnlyList<GameSession> participants)
    {
        if (_scene.BornPoint(sceneEntry) == null || participants.Count == 0
            || participants.Distinct().Count() != participants.Count)
            throw new ArgumentException("A private room needs a configured map and distinct participants.");

        var members = new List<Member>(participants.Count);
        foreach (var session in participants)
        {
            if (session.Role == null || session.SceneLoading)
                throw new InvalidOperationException("A room participant must be in a loaded role scene.");
            var previous = Find(session.InstanceId)?.MemberFor(session);
            if (session.InstanceId != 0 && previous == null)
                throw new InvalidOperationException("A participant is in an unrelated private room.");
            members.Add(new Member(session, previous?.ReturnScene ?? session.SceneEntry,
                previous?.ReturnPoint ?? new MapPoint(session.Position.X, session.Position.Y, session.Position.Z, session.Face),
                session.Role, session.Attachment, session.SceneEntry, session.InstanceId, session.SceneGeneration));
        }

        // One durable barrier covers every member before allocating or publishing a private room.
        _roles.SaveReturnPositions(members.Where(member => member.Session.InstanceId == 0).Select(member =>
            new RoleReturnPosition(member.Role.Id, member.ReturnScene, member.ReturnPoint.X,
                member.ReturnPoint.Y, member.ReturnPoint.Z, member.ReturnPoint.Face)).ToArray());

        Room room;
        lock (_gate)
        {
            room = new Room(_roles.AllocateInstanceId(), sceneEntry, members.ToArray());
            _rooms.Add(room.Id, room);
        }
        return room;
    }

    /// <summary>Use SceneService's normal handoff. On refusal or failure, detach this pending member.</summary>
    public async Task<bool> EnterAsync(Room room, GameSession session, int reason)
    {
        int entryGeneration;
        Member? member;
        lock (room.Gate)
        {
            if (room.IsClosed || !room.Remaining.Contains(session)) return false;
            entryGeneration = room.EntryGeneration;
            member = room.MemberFor(session);
        }
        bool CurrentMember(GameSession current)
        {
            lock (room.Gate) return !room.IsClosed && room.EntryGeneration == entryGeneration
                && ReferenceEquals(room.MemberFor(current), member) && ReferenceEquals(current.Role, member!.Role)
                && ReferenceEquals(current.Attachment, member.Lease) && member.Lease?.Cancelled != true;
        }
        bool Begin(GameSession current) => CurrentMember(current) && current.SceneEntry == member!.SourceScene
            && current.InstanceId == member.SourceInstance && current.SceneGeneration == member.SourceGeneration;
        bool Valid(GameSession current) => CurrentMember(current)
            && current.SceneGeneration == member!.SourceGeneration + 1;
        var born = _scene.BornPoint(room.SceneEntry);
        if (born == null) { await AbandonAsync(room, session); return false; }
        try
        {
            if (await _scene.EnterSceneAsync(session, room.SceneEntry, born, reason, room.Id,
                transfer: new SceneService.TransferStart(Begin, _ => { }, Valid))) return true;
        }
        catch
        {
            await AbandonAsync(room, session);
            throw;
        }
        await AbandonAsync(room, session);
        return false;
    }

    /// <summary>Return a participant to its saved world scene; the caller sends 30402. Recovery is opt-in.</summary>
    public async Task<bool> LeaveAsync(GameSession session, bool recoverDeadOnReturn = false)
    {
        var room = Find(session.InstanceId);
        var member = room?.MemberFor(session);
        if (member == null || !ReferenceEquals(session.Role, member.Role)
            || !ReferenceEquals(session.Attachment, member.Lease) || member.Lease?.Cancelled == true) return false;
        ReturnRecovery? recovery = null;
        SceneService.TransferStart? transfer = null;
        if (recoverDeadOnReturn)
        {
            if (session.Role == null || session.SceneLoading || session.SceneEntry != room!.SceneEntry) return false;
            var role = session.Role;
            // The public destination may queue. Carry an unarmed intent with that admission, then validate
            // the original member and arm just before the real Leaving/70010; fast 30005 still sees it.
            transfer = new SceneService.TransferStart(
                current =>
                {
                    if (!ReferenceEquals(current, session) || !ReferenceEquals(current.Role, role)
                        || current.SceneLoading || current.InstanceId != room.Id || current.SceneEntry != room.SceneEntry
                        || !ReferenceEquals(Find(room.Id), room) || room.MemberFor(current) != member) return false;
                    recovery = new ReturnRecovery(role, current.Attachment, current.SceneGeneration + 1,
                        member.ReturnScene, room.Id);
                    SetPendingReturn(current, recovery);
                    return true;
                },
                current => { if (recovery != null) ClearPendingReturn(current, recovery); });
        }
        try
        {
            bool left = await _scene.EnterSceneAsync(session, member.ReturnScene, member.ReturnPoint,
                reason: 0, transfer: transfer);
            // A fast 30005 can already have completed, so SceneLoading is deliberately not required here.
            if (recovery != null && (!left || !ReferenceEquals(session.Role, recovery.Role)
                || session.InstanceId != 0 || session.SceneEntry != recovery.OutsideScene))
                ClearPendingReturn(session, recovery);
            return left;
        }
        catch
        {
            if (recovery != null) ClearPendingReturn(session, recovery);
            throw;
        }
    }

    /// <summary>SceneService.Leaving and disconnect both detach by the current instance identity.</summary>
    public Task AbandonAsync(GameSession session)
    {
        // During an opted-in return, Leaving still sees the source room. Preserve recovery for the outside
        // 30005; a later transition/disconnect from the outside scene cancels it.
        if (PendingReturn(session) is { } recovery
            && (session.InstanceId != recovery.SourceInstance || !ReferenceEquals(session.Role, recovery.Role)))
            ClearPendingReturn(session, recovery);
        var room = Find(session.InstanceId);
        return room == null ? Task.CompletedTask : AbandonAsync(room, session);
    }

    /// <summary>Also detaches a reserved participant if a multi-member entry fails before its scene switch.</summary>
    public Task AbandonAsync(Room room, GameSession session)
        => _loop.TrackAsync("room handover", () => AbandonCoreAsync(room, session));

    private async Task AbandonCoreAsync(Room room, GameSession session)
    {
        bool last;
        lock (room.Gate)
        {
            if (!room.Remaining.Remove(session)) return;
            last = room.Remaining.Count == 0;
            if (last) { room.IsClosed = true; room.Generation++; room.EntryGeneration++; }
        }
        // The source identity is captured before yielding; a replacement scene must not be unlinked.
        long sceneGeneration = session.SceneGeneration;
        var role = session.Role;
        var lease = session.Attachment;
        try
        {
            if (session.InstanceId == room.Id)
                await _world.LeaveAsync(session, () => session.InstanceId == room.Id
                    && session.SceneGeneration == sceneGeneration && ReferenceEquals(session.Role, role)
                    && ReferenceEquals(session.Attachment, lease));
        }
        finally
        {
            if (last)
            {
                // The closing command releases its ordinary owner scope. Spawn/removal completions can then finish.
                lock (room.Gate)
                    if (!room.FinalCleanupScheduled)
                    {
                        room.FinalCleanupScheduled = true;
                        _loop.TrackRuntimeDetached("room final cleanup", () => FinalCleanupAsync(room));
                    }
            }
        }
    }

    private async Task FinalCleanupAsync(Room room)
    {
        try
        {
            await ClearMonstersCoreAsync(room);
            Task pending;
            lock (room.Gate) pending = room.PendingDone.Task;
            await pending;
            // Stale spawns remove their own actor before signaling PendingDone. No unfinished actor may survive.
            await room.Cleanup.WaitAsync();
            try
            {
                lock (room.Gate)
                    if (room.PendingCount != 0 || room.Actors.Count != 0 || room.Props.Count != 0 || room.Remaining.Count != 0)
                        throw new InvalidOperationException($"Room {room.Id} still has cleanup obligations.");
                if (!_world.TryRemoveInstance(room.Id))
                    throw new InvalidOperationException($"Room {room.Id} still owns a world scene after cleanup.");
                lock (_gate) _rooms.Remove(room.Id);
            }
            finally { room.Cleanup.Release(); }
        }
        catch
        {
            _loop.ReportCleanupFailure($"room {room.Id} final destruction");
            throw;
        }
    }

    /// <summary>One no-respawn monster tracked for settlement and final room cleanup.</summary>
    public Task<MonsterUnit?> SpawnAsync(Room room, int npcId, MapPoint point,
        int hpRatio = 10000, int damageRatio = 10000, MonsterTemplate? customTemplate = null)
        => _loop.TrackAsync("room spawn", () => SpawnCoreAsync(room, npcId, point, hpRatio, damageRatio, customTemplate));

    private async Task<MonsterUnit?> SpawnCoreAsync(Room room, int npcId, MapPoint point,
        int hpRatio, int damageRatio, MonsterTemplate? customTemplate)
    {
        int generation;
        lock (room.Gate)
        {
            if (room.IsClosed) return null;
            generation = room.Generation;
            room.SpawnStarted();
        }
        try
        {
            var actor = await _monsters.SpawnSingleAsync(room.SceneEntry, npcId, point, room.Id, hpRatio, damageRatio, customTemplate);
            if (actor == null) return null;
            bool keep;
            lock (room.Gate)
            {
                keep = !room.IsClosed && room.Generation == generation;
                if (keep) room.Actors.Add(actor);
            }
            if (keep) return actor;
            actor.Dead = true;
            actor.Target = null;
            await _world.RemoveUnitAsync(actor);
            return null;
        }
        finally { lock (room.Gate) room.SpawnFinished(); }
    }

    /// <summary>
    /// A non-combat unit of the room (a scripted scene actor): shown to the room through the world AOI, tracked so that
    /// settlement and final room cleanup remove it like the monsters. False when the room closed or was cleared meanwhile.
    /// </summary>
    public Task<bool> AddPropAsync(Room room, WorldUnit prop)
        => _loop.TrackAsync("room prop", () => AddPropCoreAsync(room, prop));

    private async Task<bool> AddPropCoreAsync(Room room, WorldUnit prop)
    {
        int generation;
        lock (room.Gate)
        {
            if (room.IsClosed) return false;
            generation = room.Generation;
            room.SpawnStarted();
        }
        try
        {
            await _world.AddUnitAsync(prop);
            bool keep;
            lock (room.Gate)
            {
                keep = !room.IsClosed && room.Generation == generation;
                if (keep) room.Props.Add(prop);
            }
            if (!keep) await _world.RemoveUnitAsync(prop);
            return keep;
        }
        finally { lock (room.Gate) room.SpawnFinished(); }
    }

    public Task RemovePropAsync(Room room, WorldUnit prop) => _loop.TrackAsync("room prop removal", async () =>
    {
        lock (room.Gate) room.Props.Remove(prop);
        await _world.RemoveUnitAsync(prop);
    });

    /// <summary>Remove one tracked monster of the room (an event boss whose phase ended); the room's other actors stay.</summary>
    public Task RemoveMonsterAsync(Room room, MonsterUnit actor) => _loop.TrackAsync("room actor removal", async () =>
    {
        bool tracked;
        lock (room.Gate) tracked = room.Actors.Remove(actor);
        if (!tracked) return; // already cleared with the room
        actor.Dead = true;
        actor.Target = null;
        await _world.RemoveUnitAsync(actor);
    });

    /// <summary>Idempotent actor removal, including a generation barrier for spawns already in flight.</summary>
    public Task ClearMonstersAsync(Room room)
        => _loop.TrackAsync("room removal", () => ClearMonstersCoreAsync(room));

    private async Task ClearMonstersCoreAsync(Room room)
    {
        await room.Cleanup.WaitAsync();
        try
        {
            MonsterUnit[] actors;
            WorldUnit[] props;
            lock (room.Gate)
            {
                room.Generation++;
                actors = room.Actors.ToArray();
                room.Actors.Clear();
                props = room.Props.ToArray();
                room.Props.Clear();
            }
            foreach (var prop in props) await _world.RemoveUnitAsync(prop);
            foreach (var actor in actors)
            {
                actor.Dead = true;
                actor.Target = null;
                await _world.RemoveUnitAsync(actor);
            }
        }
        finally { room.Cleanup.Release(); }
    }
}
