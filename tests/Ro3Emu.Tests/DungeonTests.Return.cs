using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task FailedPaidReviveCommitPreservesItemAndDeath()
    {
        await using var alice = await InWorld("Alice");
        ChangeRow("MapConfig", RoleStore.SpawnSceneEntry.ToString(), row => row["_kItemRevive"] = new JsonArray(1112001, 1));
        await Gm(alice, "!item 1112001 2");
        var session = Session(alice);
        long before = Items(alice, 1112001);
        await OnLoop(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(session, 0, 0);
            session.Dead = true;
        });
        FixtureSql.Execute(Service<AccountStore>(), $"CREATE TRIGGER fail_paid_revive BEFORE UPDATE OF currency ON role "
            + $"WHEN NEW.id={session.Role!.Id} BEGIN SELECT RAISE(ABORT, 'paid revive rollback'); END");
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => OnLoop(() => Service<WorldCoreService>().ItemReviveAsync(session)));
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_paid_revive"); }
        Assert.True(session.Dead);
        Assert.Equal(before, Items(alice, 1112001));
        Assert.Equal(0, Service<CharacterService>().Stats(session)[2000]);
        await OnLoop(() => Service<WorldCoreService>().ItemReviveAsync(session));
        Assert.False(session.Dead); // the failed receipt released the exclusive revive claim
        Assert.Equal(before - 1, Items(alice, 1112001));
    }

    [ClientDataFact]
    public async Task PaidReviveSurvivesPostCommitHookFailureWithoutSecondCharge()
    {
        await using var alice = await InWorld("Alice");
        ChangeRow("MapConfig", RoleStore.SpawnSceneEntry.ToString(), row => row["_kItemRevive"] = new JsonArray(1112001, 1));
        await Gm(alice, "!item 1112001 2");
        var session = Session(alice);
        long before = Items(alice, 1112001);
        await OnLoop(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(session, 0, 0);
            session.Dead = true;
        });
        Action<GameSession> fail = s => { if (s == session) throw new InvalidOperationException("postcommit test hook"); };
        Service<CharacterService>().StateChanged.Add(fail);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => OnLoop(() => Service<WorldCoreService>().ItemReviveAsync(session)));
        }
        finally { Service<CharacterService>().StateChanged.Remove(fail); }
        Assert.False(session.Dead);
        Assert.True(Service<CharacterService>().Stats(session)[2000] > 0);
        Assert.Equal(before - 1, Items(alice, 1112001));
        await alice.SendGameAsync(WorldCoreService.ReliveItem, new EntityReliveCostItemReq());
        Assert.Equal(ErrorCodes.TargetAlive,
            (await Next(alice, WorldCoreService.ReliveItem, EntityReliveCostItemResp.Parser)).Result);
        Assert.Equal(before - 1, Items(alice, 1112001));
    }

    [ClientDataFact]
    public async Task PaidReviveApplyFaultRequiresRestartAndKeepsOneDebit()
    {
        long role, before;
        string source;
        await using (var alice = await InWorld("Alice"))
        {
            ChangeRow("MapConfig", RoleStore.SpawnSceneEntry.ToString(),
                row => row["_kItemRevive"] = new JsonArray(1112001, 1));
            await Gm(alice, "!item 1112001 2");
            var session = Session(alice);
            role = session.Role!.Id;
            before = Items(alice, 1112001);
            await OnLoop(async () =>
            {
                await Service<CharacterService>().SetHpSpAsync(session, 0, 0);
                session.Dead = true;
            });
            var accounts = Service<AccountStore>();
            accounts.BeforeOwnedApplyForTest = label => label == "economy/world-revive"
                ? throw new IOException("paid revive committed before owned Apply") : Task.CompletedTask;
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    OnLoop(() => Service<WorldCoreService>().ItemReviveAsync(session)));
            }
            finally { accounts.BeforeOwnedApplyForTest = null; }
            source = $"world-revive/{Service<CharacterService>().RewardRealmId}/{role}/{session.ReviveDeathSource}";
            Assert.True(await Service<CharacterService>().RewardSourceCommittedAsync(source));
            Assert.True(session.Dead);
            Assert.Equal(Ro3Emu.Server.Game.Ownership.RoleOwnerState.RecoveryRequired, session.Owner!.State);
            Assert.True(session.Channel.PeerClosed);
            Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(WorldCoreService.ReliveItem));
            Assert.Equal(before - 1, Items(alice, 1112001));
        }
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await InitializeAsync();
        await using var recovered = await InWorld("Alice", create: false);
        Assert.Equal(role, (long)recovered.Self!.Uuid);
        Assert.Equal(before - 1, Items(recovered, 1112001));
        Assert.True(await Service<CharacterService>().RewardSourceCommittedAsync(source));
    }

    [ClientDataFact]
    public async Task PaidReviveDebitSurvivesSocketAbortAndReconnect()
    {
        var alice = await InWorld("Alice");
        ChangeRow("MapConfig", RoleStore.SpawnSceneEntry.ToString(), row => row["_kItemRevive"] = new JsonArray(1112001, 1));
        await Gm(alice, "!item 1112001 2");
        var session = Session(alice);
        long before = Items(alice, 1112001);
        await OnLoop(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(session, 0, 0);
            session.Dead = true;
        });
        bool aborted = false;
        Action<GameSession> abort = s =>
        {
            if (s != session) return;
            aborted = true;
            s.Channel.Abort(); // StateChanged follows the durable receipt and the revive claim's synchronous Apply.
        };
        Service<CharacterService>().StateChanged.Add(abort);
        try
        {
            try { await OnLoop(() => Service<WorldCoreService>().ItemReviveAsync(session)); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
        finally { Service<CharacterService>().StateChanged.Remove(abort); }
        Assert.True(aborted);
        Assert.False(session.Dead);
        Assert.Equal(before - 1, Items(alice, 1112001));
        await alice.DisposeAsync();
        await using var reconnect = await InWorld("Alice", create: false);
        Assert.Equal(before - 1, Items(reconnect, 1112001));
    }

    [ClientDataFact]
    public async Task PaidReviveDetachedDuringPublicationDoesNotFault()
    {
        await using var alice = await InWorld("Alice");
        ChangeRow("MapConfig", RoleStore.SpawnSceneEntry.ToString(System.Globalization.CultureInfo.InvariantCulture), row => row["_kItemRevive"] = new JsonArray(1112001, 1));
        await Gm(alice, "!item 1112001 2");
        var session = Session(alice);
        var role = session.Role;
        long before = Items(alice, 1112001);
        await OnLoop(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(session, 0, 0);
            session.Dead = true;
        });
        var combat = Service<CombatService>();
        // RoleHandover.cs:100 clears Role while PublishReviveAsync is still awaiting its packets.
        combat.ReviveBroadcastedForTest = s => { s.Role = null; return Task.CompletedTask; };
        try { await OnLoop(() => Service<WorldCoreService>().ItemReviveAsync(session)); }
        finally
        {
            await OnLoop(() =>
            {
                combat.ReviveBroadcastedForTest = null;
                session.Role = role;
                return Task.CompletedTask;
            });
        }
        Assert.False(session.Dead);
        Assert.Equal(before - 1, Items(alice, 1112001));
    }

    [ClientDataFact]
    public async Task FailedSecondMemberReturnPointAbortsWholeBarrierBeforeRoomEntry()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await MovePublic(alice, 10101);
        var roles = Service<RoleStore>();
        var first = Session(alice);
        var second = Session(bob);
        await OnLoop(() =>
        {
            roles.SavePosition(first.Role!.Id, RoleStore.SpawnSceneEntry, 1, 2, 3, 4);
            return Task.CompletedTask;
        });
        var beforeFirst = roles.Get(first.Role!.Id)!;
        var beforeSecond = roles.Get(second.Role!.Id)!;
        int switches = alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine));
        FixtureSql.Execute(Service<AccountStore>(), $"CREATE TRIGGER fail_second_return BEFORE UPDATE OF scene_entry ON role "
            + $"WHEN NEW.id={second.Role.Id} BEGIN SELECT RAISE(ABORT, 'second return point'); END");
        try
        {
            await OnLoop(() =>
            {
                Assert.Throws<SqliteException>(() => Service<InstanceService>().Create(Scene, [first, second]));
                return Task.CompletedTask;
            });
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_second_return"); }
        Assert.Equal(beforeFirst, roles.Get(first.Role.Id));
        Assert.Equal(beforeSecond, roles.Get(second.Role.Id));
        Assert.Equal(switches, alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine)));
        Assert.Equal(0, first.InstanceId);
        Assert.Equal(0, second.InstanceId);
    }

    [ClientDataFact]
    public async Task ActorWaveClearDoesNotInvalidatePendingPrivateSceneAcknowledgment()
    {
        SyntheticSolo();
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice));
        var sw = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        var session = Session(alice);
        var room = Service<InstanceService>().Find(session.InstanceId)!;
        await OnLoop(() => Service<InstanceService>().ClearMonstersAsync(room));
        Assert.False(room.Closed);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
    }

    [ClientDataFact]
    public async Task OldRoomReservationCannotEnterAfterPublicSceneGenerationChanges()
    {
        await using var alice = await InWorld("Alice");
        var session = Session(alice);
        InstanceService.Room? room = null;
        await OnLoop(() =>
        {
            room = Service<InstanceService>().Create(Scene, [session]);
            return Task.CompletedTask;
        });
        await MovePublic(alice, 10101);
        int privateSwitches = alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine)
            && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == Scene);
        await OnLoop(async () => Assert.False(await Service<InstanceService>().EnterAsync(room!, session, 3)));
        Assert.True(room!.Closed);
        Assert.Equal(privateSwitches, alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine)
            && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == Scene));
    }

    [ClientDataFact]
    public async Task ClosingDuringSpawnDrainsStaleActorAndShutdownCompletion()
    {
        SyntheticSolo();
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice));
        long instance = await Load(alice);
        // BaseInfo is sent before World.JoinAsync finishes. A subsequent request on the same
        // socket fences that frame, so the held UnitEntered callback belongs to this test spawn.
        await alice.SendGameAsync(DungeonService.StatisticsCurrent, new GetDungeonStatisticsInDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.StatisticsCurrent,
            GetDungeonStatisticsInDungeonResp.Parser)).Result);
        // EntitySelf is sent before SceneLoaded finishes Join. Finish that initial publication
        // before installing a hook meant to suspend Spawn itself, not a concurrent viewer Join.
        // WorldTests covers closing during a suspended Join/Move callback separately.
        await OnLoop(() => Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().RunAsync("spawn race setup",
            [Ro3Emu.Server.Game.Ownership.ReservationKey.Realm], () => Task.CompletedTask));
        var session = Session(alice);
        var room = session.DungeonAttempt!.Room;
        var world = Service<World>();
        long priorUid = world.AllUnits().Where(u => u.InstanceId == instance).Select(u => u.Uid).DefaultIfEmpty().Max();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(() =>
        {
            world.UnitEntered.Add(async (viewer, unit) =>
            {
                if (viewer != session || unit.InstanceId != instance || unit.Uid <= priorUid) return;
                entered.TrySetResult();
                await release.Task;
                throw new IOException("optional unit-enter notice failed after close");
            });
            return Task.CompletedTask;
        });
        Task<MonsterUnit?>? spawn = null;
        try
        {
            await OnLoop(() =>
            {
                spawn = Service<InstanceService>().SpawnAsync(room, Boss,
                    Service<SceneService>().BornPoint(Scene)!.Value);
                return Task.CompletedTask;
            });
            await entered.Task.WaitAsync(Wait);
            await OnLoop(() => Service<InstanceService>().AbandonAsync(room, session)).WaitAsync(Wait);
            await OnLoop(() => Service<InstanceService>().AbandonAsync(room, session)).WaitAsync(Wait);
            Assert.True(room.Closed);
            Assert.False(room.PendingDone.Task.IsCompleted);
            var stopping = _app.StopAsync();
            Assert.False(stopping.IsCompleted);
            release.TrySetResult();
            Assert.Null(await spawn!.WaitAsync(Wait));
            await stopping.WaitAsync(Wait);
            Assert.True(room.PendingDone.Task.IsCompleted);
            Assert.Equal(0, room.PendingCount);
            Assert.DoesNotContain(world.AllUnits(), u => u.InstanceId == instance);
            Assert.Empty(world.PlayersIn(instance));
            Assert.DoesNotContain(session.VisibleUnits, u => u.InstanceId == instance);
            var drain = Service<GameLoop>().DrainStatus;
            Assert.True(drain.Clean, $"Drain debt: {string.Join(", ", drain.Debt.Select(d => d.Name))}; failed: {string.Join(", ", drain.FailedObligations)}");
            Assert.Empty(drain.FailedObligations);
        }
        finally { release.TrySetResult(); }
    }

    private async Task<EntitySelf> ReturnFromDungeon(BotClient bot, long outside)
    {
        await bot.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(bot, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(outside, sw.SceneEntry);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = outside });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        return await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
    }

    private async Task MovePublic(BotClient bot, long destination)
    {
        await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(Session(bot), destination)));
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, n => n.SceneEntry == destination);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = destination });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
    }

    [ClientDataTheory]
    [InlineData(false, 1021851L, 5000025, 10101L)] // Eastern Payon: its elite Mandragora at entry
    [InlineData(true, Scene, Boss, RoleStore.SpawnSceneEntry)]
    public async Task LethalDungeonHitReturnsAliveThrough30402AfterDeathPublication(
        bool multi, long dungeonScene, int npc, long outside)
    {
        if (multi) SyntheticSolo();
        await using var alice = await InWorld("Alice");
        if (multi) await Gm(alice, "!lvl 99");
        else
        {
            await Gm(alice, "!quest accept 10102200");
            // A private room must retain the exact outside scene, including Izlude.
            await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(Session(alice), outside)));
            await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, n => n.SceneEntry == outside);
            await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = outside });
            Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        }
        Assert.Equal(0, await Enter(alice, multi ? Request() : new EnterDungeonReq { SceneEntry = dungeonScene }));
        long instance = await Load(alice, dungeonScene);
        var monster = await BossIn(instance, npc);
        var session = Session(alice);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(() =>
        {
            Service<CombatService>().PlayerDied.Add(async player =>
            {
                if (player != session) return;
                entered.TrySetResult();
                await release.Task;
            });
            return Task.CompletedTask;
        });
        try
        {
            await OnLoop(async () =>
            {
                await Service<CharacterService>().SetHpSpAsync(session, 1, 1);
                await Service<SceneService>().TeleportInMapAsync(session,
                    new MapPoint(monster.X + 1, monster.Y, monster.Z, monster.Face));
                Service<MonsterService>().Engage(monster, session);
            });
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
                hit => hit.SrcID == monster.Uid && hit.Dsts.Any(d => d.DstID == (long)alice.Self!.Uuid && d.IsDead));
            await entered.Task.WaitAsync(Wait);
            Assert.True(session.Dead);
            Assert.Equal(0, Service<CharacterService>().Stats(session)[2000]);

            // An attempt with the wrong room identity is not a field revival or a usable 30402.
            await OnLoop(() => { session.InstanceId = 0; return Task.CompletedTask; });
            await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
            Assert.Equal(ErrorCodes.SceneMissing,
                (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
            Assert.True(session.Dead);
            await OnLoop(() => { session.InstanceId = instance; return Task.CompletedTask; });

            await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
            Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
            var sw = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            Assert.Equal(outside, sw.SceneEntry);
            int selfCount = alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.EntitySelf));
            await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = outside });
            Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Task.Delay(100);
            Assert.Equal(selfCount, alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.EntitySelf)));
            Assert.True(session.Dead);
        }
        finally { release.TrySetResult(); }

        var self = await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Equal(EntityState.Alive, self.Base.State);
        Assert.Equal(self.Base.Property.Single(p => p.Idx == 101).Data,
            self.Base.Property.Single(p => p.Idx == 2000).Data);
        Assert.Equal(self.Base.Property.Single(p => p.Idx == 102).Data,
            self.Base.Property.Single(p => p.Idx == 2001).Data);
        Assert.False(session.Dead);
        Assert.Equal(0, session.InstanceId);
        Assert.Null(session.DungeonAttempt);
        Assert.Null(Service<InstanceService>().Find(instance));
        await Next(alice, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser,
            info => !info.BDead && info.SceneEntry == outside);
        Assert.Empty(session.Dungeons!.Records);
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(ErrorCodes.SceneMissing, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        _clock.Advance(TimeSpan.FromSeconds(10));
        await alice.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(ErrorCodes.TargetAlive, (await Next(alice, CombatService.Relive, EntityReliveAck.Parser)).Result);
    }

    [ClientDataTheory]
    [InlineData(false, 1022001L)]
    [InlineData(true, Scene)]
    public async Task LivingDungeonExitRetainsWoundedHpSpAndLoadingExitIsRefused(bool multi, long dungeonScene)
    {
        if (multi) SyntheticSolo();
        await using var alice = await InWorld("Alice");
        if (multi) await Gm(alice, "!lvl 99");
        else await Gm(alice, "!quest accept 10102200");
        Assert.Equal(0, await Enter(alice, multi ? Request() : new EnterDungeonReq { SceneEntry = dungeonScene }));
        var pending = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(dungeonScene, pending.SceneEntry);
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(ErrorCodes.SceneMissing, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = dungeonScene });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        var session = Session(alice);
        long hp = Service<CharacterService>().Stats(session)[101] / 2;
        long sp = Service<CharacterService>().Stats(session)[102] / 2;
        await OnLoop(async () =>
        {
            foreach (var monster in session.DungeonAttempt!.Room.Monsters) monster.Dead = true;
            session.LastCombatMs = MonsterService.NowMs;
            session.StatusBits |= 1L << 15;
            await Service<CharacterService>().SetHpSpAsync(session, hp, sp);
        });
        var self = await ReturnFromDungeon(alice, RoleStore.SpawnSceneEntry);
        Assert.Equal(EntityState.Alive, self.Base.State);
        Assert.Equal(hp, self.Base.Property.Single(p => p.Idx == 2000).Data);
        Assert.Equal(sp, self.Base.Property.Single(p => p.Idx == 2001).Data);
        Assert.False(session.Dead);
        Assert.Null(session.DungeonAttempt);
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DungeonReturnWaitsForCompetingReviveClaimAndCancelsOnNewHandoff(bool interrupt)
    {
        SyntheticSolo();
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice));
        await Load(alice);
        var session = Session(alice);
        var combat = Service<CombatService>();
        await OnLoop(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(session, 0, 0);
            session.Dead = true; // isolated claim race; genuine wire death is covered above
            Assert.True(combat.TryClaimRevive(session));
        });
        int selfCount = alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.EntitySelf));
        try
        {
            await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
            Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
            var sw = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            Assert.Equal(RoleStore.SpawnSceneEntry, sw.SceneEntry);
            await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
            Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Task.Delay(100);
            Assert.Equal(selfCount, alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.EntitySelf)));
            Assert.True(session.Dead);
            if (interrupt)
            {
                // Same destination and instance ID still represent a new handoff; the old 30005 must lose.
                await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(session, sw.SceneEntry)));
                await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            }
        }
        finally { combat.ReleaseReviveClaim(session); }
        if (interrupt)
        {
            await alice.SendGameAsync(MsgId.PlayerAsset, new PlayerAssetReq());
            await Next(alice, MsgId.PlayerAsset, PlayerAssetResp.Parser);
            Assert.Equal(selfCount, alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.EntitySelf)));
            Assert.True(session.Dead);
            await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = session.SceneEntry });
            Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            var self = await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
            Assert.Equal(EntityState.Dead, self.Base.State);
            Assert.Equal(0, self.Base.Property.Single(p => p.Idx == 2000).Data);
            Assert.True(session.Dead);
        }
        else
        {
            var self = await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
            Assert.Equal(EntityState.Alive, self.Base.State);
            Assert.Equal(self.Base.Property.Single(p => p.Idx == 101).Data,
                self.Base.Property.Single(p => p.Idx == 2000).Data);
            await Next(alice, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, n => !n.BDead);
            Assert.False(session.Dead);
        }
    }

    [ClientDataTheory]
    [InlineData(0)] // the original queued return is admitted
    [InlineData(1)] // explicit queue cancellation, followed by an unrelated town transition
    [InlineData(2)] // a same-map generic request replaces the queued return while preserving FIFO
    public async Task QueuedIzludeReturnUsesOnlyItsLiveAdmissionIntent(int mode)
    {
        const long izlude = 10101, ordinary = 1022001;
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!quest accept 10102200");
        await MovePublic(alice, izlude);
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = ordinary }));
        long instance = await Load(alice, ordinary);
        await using var bob = await InWorld("Bob");
        await MovePublic(bob, izlude);
        // Host-local capacity fixture: Bob fills Izlude while Alice is in her private room.
        ChangeRow("MapConfig", izlude.ToString(), row => { row["_iRoleCntLimit"] = 1; row["_iAllowCreateLine"] = 0; });
        var session = Session(alice);
        await OnLoop(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(session, 0, 0);
            session.Dead = true; // queue protocol fixture; genuine lethal wire hits are exercised above
        });
        int switches = alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine));
        await alice.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(alice, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        await Next(alice, WorldCoreService.SceneQueue, SceneQueuingRes.Parser,
            q => q.SceneId == izlude && q.LineOrder == 1);
        Assert.Equal(switches, alice.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine)));
        Assert.Equal(instance, session.InstanceId);
        Assert.True(session.Dead);

        if (mode == 1)
        {
            await alice.SendGameAsync(WorldCoreService.QueueCancel, new SceneQueuingCancel { SceneId = izlude });
            await alice.SendGameAsync(WorldCoreService.SceneQueue, new SceneQueuingReq { SceneId = izlude });
            await Next(alice, 99999, APIErrorMsg.Parser, e => e.OriginMsgId == WorldCoreService.SceneQueue);
            Assert.Equal(0, session.WorldState.QueueScene);
        }
        else if (mode == 2)
        {
            await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(session, izlude)));
            await Next(alice, WorldCoreService.SceneQueue, SceneQueuingRes.Parser,
                q => q.SceneId == izlude && q.LineOrder == 1);
        }

        await MovePublic(bob, RoleStore.SpawnSceneEntry);
        _clock.Advance(TimeSpan.FromSeconds(2)); // WorldCore's queued-admission tick uses this host's manual clock.
        if (mode == 1)
            await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(session, izlude)));
        else
            await Next(alice, WorldCoreService.SceneQueue, SceneQueuingRes.Parser,
                q => q.SceneId == izlude && q.LineOrder == 0);
        var sw = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
            n => n.SceneEntry == izlude);
        Assert.Equal(izlude, sw.SceneEntry);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = izlude });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        var self = await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        if (mode == 0)
        {
            Assert.Equal(EntityState.Alive, self.Base.State);
            Assert.Equal(self.Base.Property.Single(p => p.Idx == 101).Data,
                self.Base.Property.Single(p => p.Idx == 2000).Data);
            Assert.Equal(self.Base.Property.Single(p => p.Idx == 102).Data,
                self.Base.Property.Single(p => p.Idx == 2001).Data);
            await Next(alice, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser,
                n => !n.BDead && n.SceneEntry == izlude);
            Assert.False(session.Dead);
        }
        else
        {
            Assert.Equal(EntityState.Dead, self.Base.State);
            Assert.Equal(0, self.Base.Property.Single(p => p.Idx == 2000).Data);
            Assert.True(session.Dead);
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.ReviveInfo)
                && !SyncClientPlayerReviveInfoNtf.Parser.ParseFrom(e.Body).BDead);
        }
        Assert.Null(session.DungeonAttempt);
        Assert.Null(Service<InstanceService>().Find(instance));
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterQueueSnapshotCannotRunCanceledOrReplacedDungeonReturn(bool replace)
    {
        const long izlude = 10101, ordinary = 1022001;
        await using var carol = await InWorld("Carol");
        await Gm(carol, "!quest accept 10102200");
        await MovePublic(carol, izlude);
        Assert.Equal(0, await Enter(carol, new EnterDungeonReq { SceneEntry = ordinary }));
        long instance = await Load(carol, ordinary);
        await using var bob = await InWorld("Bob");
        await using var dave = await InWorld("Dave");
        await using var alice = await InWorld("Alice");
        await MovePublic(bob, izlude);
        await MovePublic(dave, izlude);
        ChangeRow("MapConfig", izlude.ToString(), row => { row["_iRoleCntLimit"] = 2; row["_iAllowCreateLine"] = 0; });
        var carolSession = Session(carol);
        await OnLoop(async () =>
        {
            await Service<CharacterService>().SetHpSpAsync(carolSession, 0, 0);
            carolSession.Dead = true;
        });

        // Alice is FIFO first, Carol's source-bound dungeon return second.
        await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(Session(alice), izlude)));
        await Next(alice, WorldCoreService.SceneQueue, SceneQueuingRes.Parser, q => q.LineOrder == 1);
        await carol.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(carol, DungeonService.Leave, LeaveDungeonRsp.Parser)).Result);
        await Next(carol, WorldCoreService.SceneQueue, SceneQueuingRes.Parser, q => q.LineOrder == 2);
        int carolSwitches = carol.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(() =>
        {
            Service<SceneService>().Leaving.Add(async s =>
            {
                if (s != Session(alice)) return;
                entered.TrySetResult();
                await release.Task;
            });
            return Task.CompletedTask;
        });
        await MovePublic(bob, RoleStore.SpawnSceneEntry); // one place opens for the first queued member
        _clock.Advance(TimeSpan.FromSeconds(2));
        try
        {
            await entered.Task.WaitAsync(Wait); // AdmitQueuedAsync already holds Carol's old snapshot entry
            if (replace)
            {
                // Capacity is still occupied by Dave and Alice's reservation; keep the FIFO slot, drop recovery.
                await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(carolSession, izlude)));
                await Next(carol, WorldCoreService.SceneQueue, SceneQueuingRes.Parser, q => q.LineOrder == 1);
            }
            else
            {
                await carol.SendGameAsync(WorldCoreService.QueueCancel, new SceneQueuingCancel { SceneId = izlude });
                await carol.SendGameAsync(WorldCoreService.SceneQueue, new SceneQueuingReq { SceneId = izlude });
                await Next(carol, 99999, APIErrorMsg.Parser, e => e.OriginMsgId == WorldCoreService.SceneQueue);
            }
            await MovePublic(dave, RoleStore.SpawnSceneEntry); // a second place opens before the stale snapshot resumes
        }
        finally { release.TrySetResult(); }

        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, n => n.SceneEntry == izlude);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = izlude });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        await OnLoop(() => Task.CompletedTask); // allow the suspended queue snapshot to finish
        Assert.Equal(carolSwitches, carol.Inbox.Snapshot().Count(e => e.IsGame(MsgId.SwitchSceneLine)));
        Assert.Equal(instance, carolSession.InstanceId);
        Assert.True(carolSession.Dead);
        if (replace)
        {
            _clock.Advance(TimeSpan.FromSeconds(2));
            await Next(carol, WorldCoreService.SceneQueue, SceneQueuingRes.Parser, q => q.LineOrder == 0);
        }
        else
            await OnLoop(async () => Assert.True(await Service<SceneService>().EnterSceneAsync(carolSession, izlude)));
        await Next(carol, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, n => n.SceneEntry == izlude);
        await carol.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = izlude });
        Assert.Equal(0, (await Next(carol, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        var self = await Next(carol, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Equal(EntityState.Dead, self.Base.State);
        Assert.Equal(0, self.Base.Property.Single(p => p.Idx == 2000).Data);
        Assert.True(carolSession.Dead);
        Assert.Null(Service<InstanceService>().Find(instance));
    }
}
