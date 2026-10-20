using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

/// <summary>Bound pet AOI lifecycle, including the positive-owner creation hazard.</summary>
public sealed class PetAoiTests
{
    private sealed class RecordingSink : IAoiSink
    {
        public List<string> Events { get; } = new();
        public Func<GameSession, GameSession, Task>? OnEnter { get; set; }

        private Task Add(string value) { Events.Add(value); return Task.CompletedTask; }
        public async Task EnterAsync(GameSession viewer, GameSession subject)
        {
            await Add($"{viewer.Role!.Name}+owner:{subject.Role!.Name}");
            if (OnEnter != null) await OnEnter(viewer, subject);
        }
        public Task MoveAsync(GameSession viewer, GameSession subject, WalkReq walk) => Add($"{viewer.Role!.Name}~owner:{subject.Role!.Name}");
        public Task TurnAsync(GameSession viewer, GameSession subject, float face) => Task.CompletedTask;
        public Task LeaveAsync(GameSession viewer, GameSession subject) => Add($"{viewer.Role!.Name}-owner:{subject.Role!.Name}");
        public Task UnitsChangedAsync(GameSession viewer, IReadOnlyList<WorldUnit> entered, IReadOnlyList<long> left) => Task.CompletedTask;
        public Task UnitMoveAsync(GameSession viewer, MovementChangeNtf movement) => Task.CompletedTask;
        public Task PetEnterAsync(GameSession viewer, PetFollowerActor pet) => Add($"{viewer.Role!.Name}+pet:{pet.PetId}");
        public Task PetLeaveAsync(GameSession viewer, PetFollowerActor pet) => Add($"{viewer.Role!.Name}-pet:{pet.PetId}");
        public Task PetMoveAsync(GameSession viewer, GameSession owner, PetFollowerActor pet, WalkReq walk) => Add($"{viewer.Role!.Name}~pet:{pet.PetId}");
        public Task PetTurnAsync(GameSession viewer, PetFollowerActor pet, float face) => Add($"{viewer.Role!.Name}^pet:{pet.PetId}");
    }

    private readonly RecordingSink _sink = new();
    private readonly World _world;

    public PetAoiTests() => _world = new World(_sink, new GameLoop(NullLogger<GameLoop>.Instance), NullLogger<World>.Instance);

    private static GameSession Player(string name, long id, float x = 100, float z = 100, long scene = 10111)
    {
        var channel = new PhonestChannel(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), NullLogger.Instance);
        return new GameSession(channel, null!)
        {
            Role = new Role(id, "acc", 1, name, 1, 1000, 1, 0, 0, 0, scene, x, 0, z, 0),
            SceneEntry = scene,
            Position = new Vec3 { X = x, Z = z },
            PreviousPosition = new Vec3 { X = x, Z = z },
        };
    }

    private static PetFollowerActor Pet(GameSession owner, long id = 1L << 52, int petId = 10001) =>
        new(id, owner.Role!.Id, petId, 2300440, 1, owner.Position.Clone(), owner.Face, 700, 1);

    private static WalkReq To(GameSession owner, float x, float z, bool stop = false)
    {
        owner.PreviousPosition = owner.Position;
        owner.Position = new Vec3 { X = x, Z = z };
        return new WalkReq { Pos = owner.Position, BStop = stop, SpeedRate = 1, Dir = 90 };
    }

    [Fact]
    public async Task JoinShowsOwnerBeforePetIncludingOwnerSelfAndLateViewer()
    {
        var owner = Player("Owner", 11);
        var pet = Pet(owner);
        await _world.SetPetAsync(owner, pet);
        Assert.Empty(_sink.Events); // no pre-world actor

        await _world.JoinAsync(owner);
        var viewer = Player("Viewer", 12, 120);
        await _world.JoinAsync(viewer);
        Assert.Equal(new[] { "Owner+pet:10001", "Viewer+owner:Owner", "Viewer+pet:10001", "Owner+owner:Viewer" }, _sink.Events);
        Assert.Equal((ulong)owner.Role!.Id, (ulong)pet.Entity().Owner);
        Assert.Equal(7, pet.Entity().EntityType);
        Assert.Equal(2300440UL, pet.Entity().EntityTemplateID);
        Assert.Contains(pet.Entity().Property, property => property.Idx == 113 && property.Data == 700);
        Assert.NotEqual((ulong)owner.Role.Id, pet.Entity().Uuid);
        Assert.NotEqual((ulong)pet.PetId, pet.Entity().Uuid);
        Assert.InRange(Distance(pet.Position, owner.Position), 2.99f, 3.01f);
    }

    [Fact]
    public async Task AcceptedWalkStopAndTeleportMovePetToSelfAndVisiblePeer()
    {
        var owner = Player("Owner", 11);
        var viewer = Player("Viewer", 12, 110);
        var pet = Pet(owner);
        await _world.SetPetAsync(owner, pet);
        await _world.JoinAsync(owner);
        await _world.JoinAsync(viewer);
        _sink.Events.Clear();

        var walk = To(owner, 102, 100);
        await _world.MoveAsync(owner, walk);
        Assert.InRange(Distance(pet.Position, owner.Position), 2.99f, 3.01f);
        var movement = GameAoiSink.PetMovement(owner, pet, walk);
        var ownerMovement = GameAoiSink.Movement(owner, walk);
        Assert.Equal(pet.Uid, movement.Uid);
        Assert.Equal(MovementType.MoveTypeWalk, movement.MovementInfo.MoveType);
        Assert.True(movement.MovementInfo.MoveDuration > 0);
        Assert.NotNull(movement.MovementInfo.MovementWalk);
        Assert.InRange(Distance(movement.MovementInfo.Pos, ownerMovement.MovementInfo.Pos), 2.99f, 3.01f);
        Assert.Equal(ownerMovement.MovementInfo.MoveDuration, movement.MovementInfo.MoveDuration);
        Assert.Equal(ownerMovement.MovementInfo.MovementWalk.Speed, movement.MovementInfo.MovementWalk.Speed);
        Assert.Equal(ownerMovement.MovementInfo.MovementWalk.Dir, movement.MovementInfo.MovementWalk.Dir);
        Assert.InRange(Distance(Project(movement), Project(ownerMovement)), 2.99f, 3.01f);

        var stop = To(owner, 104, 100, stop: true);
        await _world.MoveAsync(owner, stop);
        Assert.InRange(Distance(pet.Position, owner.Position), 2.99f, 3.01f);
        Assert.Equal(MovementType.MoveTypeNone, GameAoiSink.PetMovement(owner, pet, stop).MovementInfo.MoveType);

        var jump = To(owner, 130, 100, stop: true);
        await _world.MoveAsync(owner, jump);
        Assert.InRange(Distance(pet.Position, owner.Position), 2.99f, 3.01f);
        Assert.Equal(MovementTransitionType.MoveTransForceTeleport, GameAoiSink.PetMovement(owner, pet, jump).TransType);
        Assert.Equal(new[]
        {
            "Viewer~owner:Owner", "Viewer~pet:10001", "Owner~pet:10001",
            "Viewer~owner:Owner", "Viewer~pet:10001", "Owner~pet:10001",
            "Viewer~owner:Owner", "Viewer~pet:10001", "Owner~pet:10001",
        }, _sink.Events);
    }

    [Fact]
    public async Task ReversalShortTeleportTurnAndIdleKeepPetSeparatedWithoutIdlePetPackets()
    {
        var owner = Player("Owner", 11);
        var viewer = Player("Viewer", 12, 110);
        var pet = Pet(owner);
        await _world.SetPetAsync(owner, pet);
        await _world.JoinAsync(owner);
        await _world.JoinAsync(viewer);
        _sink.Events.Clear();

        var forward = To(owner, 104, 100);
        await _world.MoveAsync(owner, forward);
        var back = To(owner, 102, 100);
        back.Dir = 270;
        await _world.MoveAsync(owner, back);
        var petBack = GameAoiSink.PetMovement(owner, pet, back);
        var ownerBack = GameAoiSink.Movement(owner, back);
        Assert.InRange(Distance(petBack.MovementInfo.Pos, ownerBack.MovementInfo.Pos), 2.99f, 3.01f);
        Assert.InRange(Distance(Project(petBack), Project(ownerBack)), 2.99f, 3.01f);

        _sink.Events.Clear();
        owner.Face = 45;
        await _world.TurnAsync(owner, 45);
        Assert.True(pet.IsMoving);
        Assert.Equal(45f, pet.Face);
        Assert.Equal(2, _sink.Events.Count(e => e.EndsWith("^pet:10001")));
        Assert.DoesNotContain(_sink.Events, e => e.Contains("~pet:"));

        var shortJump = To(owner, 106, 100, stop: true);
        await _world.MoveAsync(owner, shortJump, forceTeleport: true);
        var petJump = GameAoiSink.PetMovement(owner, pet, shortJump);
        Assert.Equal(MovementTransitionType.MoveTransForceTeleport, petJump.TransType);
        Assert.Equal(MovementType.MoveTypeNone, petJump.MovementInfo.MoveType);
        Assert.InRange(Distance(petJump.MovementInfo.Pos, owner.Position), 2.99f, 3.01f);

        _sink.Events.Clear();
        owner.Face = 90;
        await _world.TurnAsync(owner, 90);
        Assert.Equal(90f, pet.Face);
        Assert.InRange(Distance(pet.Position, owner.Position), 2.99f, 3.01f);
        Assert.Equal(2, _sink.Events.Count(e => e.EndsWith("^pet:10001"))); // turn reaches owner and peer
        Assert.DoesNotContain(_sink.Events, e => e.Contains("~pet:"));

        _sink.Events.Clear();
        var idle = To(owner, 106, 100, stop: true);
        idle.Face = 90;
        await _world.MoveAsync(owner, idle);
        Assert.DoesNotContain(_sink.Events, e => e.Contains("~pet:"));
    }

    [Fact]
    public async Task SamePositionWalkStartAndStopUpdatePetButRepeatedIdleStopDoesNot()
    {
        var owner = Player("Owner", 11);
        var viewer = Player("Viewer", 12, 110);
        var pet = Pet(owner);
        await _world.SetPetAsync(owner, pet);
        await _world.JoinAsync(owner);
        await _world.JoinAsync(viewer);
        _sink.Events.Clear();

        var start = To(owner, 100, 100);
        await _world.MoveAsync(owner, start);
        Assert.True(pet.IsMoving);
        Assert.Equal(2, _sink.Events.Count(e => e.EndsWith("~pet:10001")));
        Assert.Equal(MovementType.MoveTypeWalk, GameAoiSink.PetMovement(owner, pet, start).MovementInfo.MoveType);

        _sink.Events.Clear();
        await _world.MoveAsync(owner, To(owner, 100, 100));
        Assert.Equal(2, _sink.Events.Count(e => e.EndsWith("~pet:10001")));

        _sink.Events.Clear();
        var stop = To(owner, 100, 100, stop: true);
        await _world.MoveAsync(owner, stop);
        Assert.False(pet.IsMoving);
        Assert.Equal(2, _sink.Events.Count(e => e.EndsWith("~pet:10001")));
        Assert.Equal(MovementType.MoveTypeNone, GameAoiSink.PetMovement(owner, pet, stop).MovementInfo.MoveType);

        _sink.Events.Clear();
        await _world.MoveAsync(owner, To(owner, 100, 100, stop: true));
        Assert.DoesNotContain(_sink.Events, e => e.Contains("~pet:"));
    }

    private static float Distance(Vec3 a, Vec3 b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static Vec3 Project(MovementChangeNtf movement)
    {
        var info = movement.MovementInfo;
        if (info.MoveType != MovementType.MoveTypeWalk) return info.Pos;
        float angle = info.MovementWalk.Dir * MathF.PI / 180f;
        float travel = info.MovementWalk.Speed * info.MoveDuration / 1000f;
        return new Vec3
        {
            X = info.Pos.X + MathF.Sin(angle) * travel, Y = info.MovementWalk.EndHeight,
            Z = info.Pos.Z + MathF.Cos(angle) * travel
        };
    }

    [Fact]
    public async Task RangeExitAndDisconnectRemovePetBeforeOwner()
    {
        var owner = Player("Owner", 11);
        var viewer = Player("Viewer", 12, 110);
        var pet = Pet(owner);
        await _world.SetPetAsync(owner, pet);
        await _world.JoinAsync(owner);
        await _world.JoinAsync(viewer);
        _sink.Events.Clear();

        await _world.MoveAsync(owner, To(owner, 200, 100, stop: true));
        Assert.Equal(new[] { "Viewer-pet:10001", "Viewer-owner:Owner", "Owner-owner:Viewer", "Owner~pet:10001" }, _sink.Events);
        Assert.DoesNotContain(viewer, pet.Viewers);

        _sink.Events.Clear();
        await _world.MoveAsync(owner, To(owner, 120, 100, stop: true));
        Assert.Equal(new[] { "Viewer+owner:Owner", "Viewer+pet:10001", "Owner+owner:Viewer", "Owner~pet:10001" }, _sink.Events);

        _sink.Events.Clear();
        await _world.LeaveAsync(owner);
        Assert.Equal(new[] { "Owner-pet:10001", "Viewer-pet:10001", "Viewer-owner:Owner" }, _sink.Events);
        Assert.Empty(pet.Viewers);
        Assert.Empty(viewer.Visible);
    }

    [Fact]
    public async Task ViewerLeavingRadiusLosesOwnersPetBeforeOwner()
    {
        var owner = Player("Owner", 11);
        var viewer = Player("Viewer", 12, 110);
        var pet = Pet(owner);
        await _world.SetPetAsync(owner, pet);
        await _world.JoinAsync(owner);
        await _world.JoinAsync(viewer);
        _sink.Events.Clear();

        await _world.MoveAsync(viewer, To(viewer, 300, 100, stop: true));
        Assert.Equal(new[] { "Owner-owner:Viewer", "Viewer-pet:10001", "Viewer-owner:Owner" }, _sink.Events);
        Assert.DoesNotContain(viewer, pet.Viewers);
        Assert.Contains(owner, pet.Viewers);
    }

    [Fact]
    public async Task RefreshReplacementRemovesOldPetBeforeAddingNewAndClearsOnUnequip()
    {
        var owner = Player("Owner", 11);
        var viewer = Player("Viewer", 12, 110);
        await _world.JoinAsync(owner);
        await _world.JoinAsync(viewer);
        _sink.Events.Clear();

        var old = Pet(owner);
        var next = Pet(owner, (1L << 52) + 1, 10002);
        await _world.SetPetAsync(owner, old);
        await _world.SetPetAsync(owner, next);
        await _world.SetPetAsync(owner, null);
        Assert.Equal(new[]
        {
            "Owner+pet:10001", "Viewer+pet:10001",
            "Owner-pet:10001", "Viewer-pet:10001", "Owner+pet:10002", "Viewer+pet:10002",
            "Owner-pet:10002", "Viewer-pet:10002",
        }, _sink.Events);
        Assert.Empty(old.Viewers);
        Assert.Empty(next.Viewers);
    }

    [Fact]
    public async Task ConcurrentViewerEnterAndRefreshCannotSendPetBeforeOwner()
    {
        var owner = Player("Owner", 11);
        var viewer = Player("Viewer", 12, 110);
        await _world.JoinAsync(owner);
        var ownerEnterStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishOwnerEnter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _sink.OnEnter = async (v, s) =>
        {
            if (v != viewer || s != owner) return;
            ownerEnterStarted.SetResult(true);
            await finishOwnerEnter.Task;
        };

        var join = _world.JoinAsync(viewer);
        await ownerEnterStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var refresh = _world.SetPetAsync(owner, Pet(owner));
        bool refreshWaited = !refresh.IsCompleted;
        finishOwnerEnter.SetResult(true);
        await Task.WhenAll(join, refresh);

        Assert.True(refreshWaited);
        Assert.True(_sink.Events.IndexOf("Viewer+owner:Owner") < _sink.Events.IndexOf("Viewer+pet:10001"));
        Assert.Equal(1, _sink.Events.Count(e => e == "Viewer+pet:10001"));
    }

    [Fact]
    public async Task PrivateTowerInstancesKeepPetRefreshMovementAndExitWithTheirOwner()
    {
        var owner = Player("Owner", 11, scene: 2100001);
        var viewer = Player("Viewer", 12, 110, scene: 2100001);
        var other = Player("Other", 13, 110, scene: 2100001);
        owner.InstanceId = viewer.InstanceId = 900001;
        other.InstanceId = 900002;
        var old = Pet(owner);
        await _world.SetPetAsync(owner, old);
        await _world.JoinAsync(owner);
        await _world.JoinAsync(viewer);
        await _world.JoinAsync(other);
        Assert.Equal(new[] { "Owner+pet:10001", "Viewer+owner:Owner", "Viewer+pet:10001", "Owner+owner:Viewer" }, _sink.Events);
        Assert.Empty(other.Visible);
        Assert.DoesNotContain(other, old.Viewers);
        _sink.Events.Clear();

        // A lineup change in an instance must find its membership by instance ID, not the shared map entry.
        var next = Pet(owner, (1L << 52) + 1, 10002);
        await _world.SetPetAsync(owner, next);
        Assert.Equal(new[] { "Owner-pet:10001", "Viewer-pet:10001", "Owner+pet:10002", "Viewer+pet:10002" }, _sink.Events);
        Assert.Empty(old.Viewers);
        Assert.DoesNotContain(other, next.Viewers);
        _sink.Events.Clear();

        await _world.MoveAsync(owner, To(owner, 104, 100, stop: true));
        Assert.InRange(Distance(next.Position, owner.Position), 2.99f, 3.01f);
        Assert.Equal(new[] { "Viewer~owner:Owner", "Viewer~pet:10002", "Owner~pet:10002" }, _sink.Events);
        _sink.Events.Clear();

        await _world.LeaveAsync(owner);
        Assert.Equal(new[] { "Owner-pet:10002", "Viewer-pet:10002", "Viewer-owner:Owner" }, _sink.Events);
        Assert.Empty(next.Viewers);
        Assert.Empty(viewer.Visible);
        Assert.Same(other, Assert.Single(_world.PlayersIn(other.WorldScene)));
        _sink.Events.Clear();

        // The ordinary world still shows the owner before its surviving follower on return.
        var outside = Player("Outside", 14);
        await _world.JoinAsync(outside);
        owner.InstanceId = 0;
        owner.SceneEntry = outside.SceneEntry;
        await _world.JoinAsync(owner);
        Assert.Equal(new[] { "Owner+owner:Outside", "Outside+owner:Owner", "Outside+pet:10002", "Owner+pet:10002" }, _sink.Events);
        Assert.InRange(Distance(next.Position, owner.Position), 2.99f, 3.01f);
    }

    [Fact]
    public async Task OwnedSupportPetDoesNotCreateFollower()
    {
        var owner = Player("Owner", 11);
        var plan = new DBPetSetUpPlan();
        plan.SupportPets[1] = 10001;
        var roster = new DBPetInfo { CurrentPlan = 1 };
        roster.SetupPlans[1] = plan;
        roster.ListPet[10001] = new PetData { PetId = 10001, Level = 1, Star = 1 };
        owner.Pets = roster;

        await new PetFollowerService(_world, null!, null!).RefreshAsync(owner);
        await _world.JoinAsync(owner);
        Assert.Null(owner.PetActor);
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public async Task PersistedSetupCannotSpawnBeforeFunctionStateIsLoaded()
    {
        var owner = Player("Owner", 11);
        var plan = new DBPetSetUpPlan();
        plan.SetUpPets[1] = 10001;
        var roster = new DBPetInfo { CurrentPlan = 1 };
        roster.SetupPlans[1] = plan;
        roster.ListPet[10001] = new PetData { PetId = 10001, Level = 1, Star = 1 };
        owner.Pets = roster;

        await new PetFollowerService(_world, null!, null!).RefreshAsync(owner);
        await _world.JoinAsync(owner);
        Assert.Null(owner.PetActor);
        Assert.Empty(_sink.Events);
    }
}
