using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    private async Task<string> RefuseTerminalKillAsync(BotClient alice, MonsterUnit actor)
    {
        await OnLoop(async () =>
        {
            await Service<SceneService>().TeleportInMapAsync(Session(alice),
                new MapPoint(actor.X + 1, actor.Y, actor.Z, actor.Face));
            Service<MonsterService>().Engage(actor, Session(alice));
            actor.Hp = 1;
        });
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<GameSession, MonsterUnit, Task> stageNotice = (_, monster) =>
        {
            if (ReferenceEquals(monster, actor)) staged.TrySetResult();
            return Task.CompletedTask;
        };
        var hooks = Service<CombatService>().BeforeKilled;
        hooks.Insert(hooks.FindIndex(h => h.Target is RewardService), stageNotice);
        FixtureSql.Execute(Service<AccountStore>(), """
            CREATE TRIGGER fail_terminal_kill BEFORE INSERT ON operation_receipt
            WHEN NEW.command_kind='monster-kill' BEGIN SELECT RAISE(ABORT, 'kill receipt fault'); END
            """);
        try
        {
            await SwingWait.UntilDoneAsync(Service<GameLoop>(), Session(alice), actor.Uid);
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = actor.Uid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await staged.Task.WaitAsync(Wait);
            bool restored = false;
            for (int i = 0; i < 100 && !restored; i++)
            {
                restored = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
                    !actor.Dead && actor.RewardSettlement == null && actor.RewardCommit == null));
                if (!restored) await Task.Delay(20);
            }
            Assert.True(restored, "Failed ordinary kill source did not restore the live terminal monster.");
            await OnLoop(() => { Session(alice).Attack = null; return Task.CompletedTask; });
            var source = actor.RewardSource!.Key;
            Assert.False(await Service<CharacterService>().RewardSourceCommittedAsync(source));
            Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(CombatService.SkillHit)
                && SkillHitInfo.Parser.ParseFrom(packet.Body).Dsts.Any(hit => hit.DstID == actor.Uid && hit.IsDead));
            Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(DungeonService.Result));
            return source;
        }
        finally
        {
            hooks.Remove(stageNotice);
            FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_terminal_kill");
        }
    }

    [ClientDataFact]
    public async Task FrozenDungeonRecipientSurvivesFailedDeliveryAndRestart()
    {
        SyntheticSolo(daily: true);
        long room = 0, liveDamage = 0;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm(alice, "!lvl 99");
            Assert.Equal(0, await Enter(alice));
            room = await Load(alice);
            // The terminal kill can establish the recipient obligation, while this fault refuses its
            // item + allowance delivery. Recovery must use the frozen award rather than roll it again.
            FixtureSql.Execute(Service<AccountStore>(),
                "CREATE TRIGGER fail_dungeons BEFORE UPDATE OF dungeons ON role BEGIN SELECT RAISE(ABORT, 'dungeon delivery fault'); END");
            await Kill(alice, await BossIn(room));
            var pending = Assert.Single(Service<DungeonObligationStore>().Pending((long)alice.Self!.Uuid));
            Assert.Equal(room, pending.RoomId);
            Assert.Contains(pending.Goods, good => good.Entry == 1250900 && good.Count == 100);
            Assert.Equal(0, Items(alice, 1250900));
            Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(DungeonService.Result));
            liveDamage = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
                Session(alice).DungeonAttempt!.Stats[(long)alice.Self.Uuid].TotalDamage));
            Assert.True(liveDamage > 0);
            FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_dungeons");
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await InitializeAsync();

        await using var recovered = await InWorld("Alice", create: false);
        Assert.Equal(100, Items(recovered, 1250900));
        Assert.Equal(90000, Currency(recovered, 2));
        Assert.Empty(Service<DungeonObligationStore>().Pending((long)recovered.Self!.Uuid));
        var report = DungeonStatisticsInfoWithDungeon.Parser.ParseFrom(Session(recovered).Dungeons!.Statistics[room]);
        Assert.Equal(liveDamage, report.SceneStatistics[room].RoleStatistics[(long)recovered.Self.Uuid].TotalDamage);
        await OnLoop(() => Service<DungeonService>().DeliverPendingAsync(Session(recovered)));
        Assert.Equal(100, Items(recovered, 1250900));
        Assert.Equal(90000, Currency(recovered, 2));
    }

    [ClientDataFact]
    public async Task CommittedDungeonDeliveryApplyFaultReloadsWithoutSecondAward()
    {
        SyntheticSolo(daily: true);
        long room, role;
        string source;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm(alice, "!lvl 99");
            role = (long)alice.Self!.Uuid;
            Assert.Equal(0, await Enter(alice));
            room = await Load(alice);
            var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Held from here on: the fault closes the connection, after which the registry may already have dropped it.
            var session = Session(alice);
            var accounts = Service<AccountStore>();
            accounts.BeforeOwnedApplyForTest = label =>
            {
                if (label != "economy/dungeon-reward") return Task.CompletedTask;
                faulted.TrySetResult();
                throw new IOException("dungeon delivery committed before owned Apply");
            };
            try
            {
                await Kill(alice, await BossIn(room));
                await faulted.Task.WaitAsync(Wait);
            }
            finally { accounts.BeforeOwnedApplyForTest = null; }
            source = $"dungeon/{Service<CharacterService>().RewardRealmId}/{room}/{role}";
            Assert.True(await Service<CharacterService>().RewardSourceCommittedAsync(source));
            for (int i = 0; i < 100 && session.Owner?.State !=
                     Ro3Emu.Server.Game.Ownership.RoleOwnerState.RecoveryRequired; i++) await Task.Delay(20);
            Assert.Equal(Ro3Emu.Server.Game.Ownership.RoleOwnerState.RecoveryRequired, session.Owner!.State);
            for (int i = 0; i < 100 && !session.Channel.PeerClosed; i++) await Task.Delay(20);
            Assert.True(session.Channel.PeerClosed);
            Assert.DoesNotContain(alice.Inbox.Snapshot(), packet => packet.IsGame(DungeonService.Result));
            Assert.Equal(1, FrozenRowCount(room, activated: 1, completed: 1));
            Assert.Empty(Service<DungeonObligationStore>().Pending(role));
            Assert.Equal(100, Items(alice, 1250900));
        }
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await InitializeAsync();
        await using var recovered = await InWorld("Alice", create: false);
        Assert.Equal(100, Items(recovered, 1250900));
        Assert.Equal(90000, Currency(recovered, 2));
        Assert.Empty(Service<DungeonObligationStore>().Pending(role));
        Assert.True(await Service<CharacterService>().RewardSourceCommittedAsync(source));
    }

    [ClientDataFact]
    public async Task ThreeRecipientsPartiallyDeliverThenRestartPaysOnlyPendingMember()
    {
        long room, aliceRole, bobRole, carolRole;
        await using (var alice = await InWorld("Alice"))
        await using (var bob = await InWorld("Bob"))
        await using (var carol = await InWorld("Carol"))
        {
            foreach (var bot in new[] { alice, bob, carol }) await Gm(bot, "!lvl 99");
            aliceRole = (long)alice.Self!.Uuid;
            bobRole = (long)bob.Self!.Uuid;
            carolRole = (long)carol.Self!.Uuid;
            await Party(alice, bob, carol);
            Assert.Equal(0, await Enter(alice));
            foreach (var member in new[] { bob, carol })
            {
                await member.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
                Assert.Equal(0, (await Next(member, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
            }
            room = await Load(alice);
            Assert.Equal(room, await Load(bob));
            Assert.Equal(room, await Load(carol));
            FixtureSql.Execute(Service<AccountStore>(), $"""
                CREATE TRIGGER fail_bob_dungeons BEFORE UPDATE OF dungeons ON role
                WHEN NEW.id={bobRole} BEGIN SELECT RAISE(ABORT, 'bob delivery fault'); END
                """);
            try
            {
                await Kill(alice, await BossIn(room));
                foreach (var paid in new[] { alice, carol })
                {
                    Assert.Equal((DungeonRes)1, (await Next(paid, DungeonService.Result, DungeonResNotify.Parser)).Res);
                    Assert.Equal(100, Items(paid, 1250900));
                    Assert.Equal(90000, Currency(paid, 2));
                }
                Assert.DoesNotContain(bob.Inbox.Snapshot(), p => p.IsGame(DungeonService.Result));
                Assert.Equal(0, Items(bob, 1250900));
                Assert.Equal(0, Currency(bob, 2));
                Assert.True(Service<DungeonObligationStore>().IsActivated(room));
                Assert.Equal(3, FrozenRowCount(room, activated: 1));
                Assert.Equal(2, FrozenRowCount(room, activated: 1, completed: 1));
                Assert.Equal(3, EarnedSourceCount(room));
                Assert.Single(Service<DungeonObligationStore>().Pending(bobRole));
                Assert.Empty(Service<DungeonObligationStore>().Pending(aliceRole));
                Assert.Empty(Service<DungeonObligationStore>().Pending(carolRole));
            }
            finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_bob_dungeons"); }
        }
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await InitializeAsync();
        foreach (var (name, role) in new[] { ("Bob", bobRole), ("Alice", aliceRole), ("Carol", carolRole) })
        {
            await using var recovered = await InWorld(name, create: false);
            Assert.Equal(role, (long)recovered.Self!.Uuid);
            Assert.Equal(100, Items(recovered, 1250900));
            Assert.Equal(90000, Currency(recovered, 2));
            Assert.Empty(Service<DungeonObligationStore>().Pending(role));
        }
        Assert.Equal(3, FrozenRowCount(room, activated: 1, completed: 1));
        Assert.Equal(3, EarnedSourceCount(room));
    }

    private long FrozenRowCount(long room, int activated, int? completed = null)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_dataDir, "ro3emu.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM dungeon_reward_obligation WHERE room_id=$room AND activated=$active"
            + (completed == null ? "" : " AND completed=$completed");
        cmd.Parameters.AddWithValue("$room", room);
        cmd.Parameters.AddWithValue("$active", activated);
        if (completed != null) cmd.Parameters.AddWithValue("$completed", completed.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    private long EarnedSourceCount(long room)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_dataDir, "ro3emu.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='dungeon-obligation-earn'"
            + " AND source_key LIKE $source";
        cmd.Parameters.AddWithValue("$source", $"dungeon/{Service<CharacterService>().RewardRealmId}/{room}/%/earned");
        return (long)cmd.ExecuteScalar()!;
    }

    private EnterDungeonReq ConfigureTwoActorStory()
    {
        // A story scene without an encounter source or a reconstruction (1000201 is now a scripted scene).
        const long story = 1054001;
        var born = Service<WorldData>().Map(story)!.Born!.Value;
        var request = new EnterDungeonReq { SceneEntry = story };
        string original = Service<GameData>().Row("DungeonConfig", story)!.Value
            .GetProperty("_kResSceneEditorName").GetString()!;
        string source = Path.Combine(RepoRoot(), "data", "sceneeditor", original + ".json");
        string directory = Path.Combine(_dataDir, "sceneeditor");
        Directory.CreateDirectory(directory);
        string stem = Path.Combine(directory, "synthetic_aoe_1054001");
        var editor = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        JsonObject Point(int id) => new()
        {
            ["Id"] = id, ["GroupId"] = 1, ["monsterType"] = 0, ["npcId"] = 5300036,
            ["direction"] = 0, ["position"] = new JsonObject { ["x"] = born.X, ["y"] = born.Y, ["z"] = born.Z + 3 },
        };
        editor["m_kMonsterSpawnPoints"] = new JsonObject { ["1001"] = Point(1001), ["1002"] = Point(1002) };
        editor["m_kGroups"] = new JsonObject();
        File.WriteAllText(stem + ".json", editor.ToJsonString());
        ChangeRow("DungeonConfig", story.ToString(), row =>
        {
            row["_kResSceneEditorName"] = stem;
            row["_iRoleCntLimit"] = 2;
        });
        Assert.Equal(2, Assert.Single(Service<DungeonCatalog>().Resolve(request)!.Waves).Spawns.Count);
        return request;
    }

    [ClientDataFact]
    public async Task FinalTwoActorsInOneAreaHitFreezeOneCompleteReportBeforeResult()
    {
        const long story = 1054001;
        var request = ConfigureTwoActorStory();
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!job 1100");
        Assert.Equal(0, await Enter(alice, request));
        long room = await Load(alice, story);
        await OnLoop(() => { Session(alice).Progress!.Skills[11002] = 1; return Task.CompletedTask; });
        MonsterUnit[] actors = [];
        for (int i = 0; i < 100; i++)
        {
            actors = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
                .OfType<MonsterUnit>().Where(m => m.InstanceId == room && m.Template.NpcId == 5300036).ToArray()));
            if (actors.Length == 2) break;
            await Task.Delay(20);
        }
        Assert.Equal(2, actors.Length);
        await OnLoop(async () =>
        {
            await Service<SceneService>().TeleportInMapAsync(Session(alice),
                new MapPoint(actors[0].X + 1, actors[0].Y, actors[0].Z, actors[0].Face));
            foreach (var actor in actors)
            {
                Service<MonsterService>().Engage(actor, Session(alice));
                actor.Hp = 1;
            }
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100201 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            packet => actors.All(actor => packet.Dsts.Any(d => d.DstID == actor.Uid && d.IsDead)));
        Assert.Equal(2, hit.Dsts.Count(d => actors.Any(actor => d.DstID == actor.Uid && d.IsDead)));
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        Assert.Equal(1, EarnedSourceCount(room));
        var report = DungeonStatisticsInfoWithDungeon.Parser.ParseFrom(Session(alice).Dungeons!.Statistics[room]);
        var stats = report.SceneStatistics[room].RoleStatistics[(long)alice.Self!.Uuid];
        Assert.Equal(2, stats.KillNpcData[5300036].LongValue);
        Assert.Equal(hit.Dsts.Where(d => actors.Any(actor => d.DstID == actor.Uid)).Sum(d => d.Damage),
            stats.TotalDamage);
    }

    [ClientDataFact]
    public async Task OverlappingLastTwoCastsWaitForFirstKillSourceAndFreezeBothRecipients()
    {
        var request = ConfigureTwoActorStory();
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        foreach (var bot in new[] { alice, bob }) await Gm(bot, "!lvl 99");
        await Party(alice, bob);
        Assert.Equal(0, await Enter(alice, request));
        await bob.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonReq { IsAgree = true });
        Assert.Equal(0, (await Next(bob, DungeonService.Confirm, ConfirmTeamDungeonRsp.Parser)).Result);
        long room = await Load(alice, request.SceneEntry);
        Assert.Equal(room, await Load(bob, request.SceneEntry));
        MonsterUnit[] actors = [];
        for (int i = 0; i < 100; i++)
        {
            actors = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
                .OfType<MonsterUnit>().Where(m => m.InstanceId == room).OrderBy(m => m.Uid).ToArray()));
            if (actors.Length == 2) break;
            await Task.Delay(20);
        }
        Assert.Equal(2, actors.Length);
        await OnLoop(async () =>
        {
            foreach (var (bot, actor) in new[] { (alice, actors[0]), (bob, actors[1]) })
            {
                await Service<SceneService>().TeleportInMapAsync(Session(bot),
                    new MapPoint(actor.X + 1, actor.Y, actor.Z, actor.Face));
                Service<MonsterService>().Engage(actor, Session(bot));
                actor.Hp = 1;
            }
        });
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accounts = Service<AccountStore>();
        int blocks = 0;
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/monster-kill" || Interlocked.Exchange(ref blocks, 1) != 0)
                return Task.CompletedTask;
            held.TrySetResult();
            return release.Task;
        };
        try
        {
            await alice.SendGameAsync(CombatService.UseSkill,
                new CastSkillReq { SkillId = 1000001, TargetId = actors[0].Uid });
            await bob.SendGameAsync(CombatService.UseSkill,
                new CastSkillReq { SkillId = 1000001, TargetId = actors[1].Uid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            Assert.Equal(0, (await Next(bob, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await held.Task.WaitAsync(Wait);
            bool secondWaiting = false;
            for (int i = 0; i < 100 && !secondWaiting; i++)
            {
                secondWaiting = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
                    actors[1].Hp == 0 && !actors[1].DeathDurable && !actors[1].Dead));
                if (!secondWaiting) await Task.Delay(20);
            }
            Assert.True(secondWaiting, "The second lethal cast did not wait behind the first room source.");
            Assert.DoesNotContain(alice.Inbox.Snapshot(), p => p.IsGame(DungeonService.Result));
            Assert.DoesNotContain(bob.Inbox.Snapshot(), p => p.IsGame(DungeonService.Result));
        }
        finally { release.TrySetResult(); accounts.BeforeOwnedCommitForTest = null; }
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.SrcID == (long)alice.Self!.Uuid && hit.Dsts.Any(d => d.DstID == actors[0].Uid && d.IsDead));
        await Next(bob, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.SrcID == (long)bob.Self!.Uuid && hit.Dsts.Any(d => d.DstID == actors[1].Uid && d.IsDead));
        foreach (var bot in new[] { alice, bob })
            Assert.Equal((DungeonRes)1, (await Next(bot, DungeonService.Result, DungeonResNotify.Parser)).Res);
        Assert.Equal(2, FrozenRowCount(room, activated: 1, completed: 1));
        Assert.Equal(2, EarnedSourceCount(room));
        foreach (var actor in actors)
            Assert.True(await Service<CharacterService>().RewardSourceCommittedAsync(actor.RewardSource!.Key));
        var report = DungeonStatisticsInfoWithDungeon.Parser.ParseFrom(Session(alice).Dungeons!.Statistics[room]);
        Assert.Equal(1, report.SceneStatistics[room].RoleStatistics[(long)alice.Self!.Uuid]
            .KillNpcData[5300036].LongValue);
        Assert.Equal(1, report.SceneStatistics[room].RoleStatistics[(long)bob.Self!.Uuid]
            .KillNpcData[5300036].LongValue);
    }

    [ClientDataFact]
    public async Task PreparedFinalKillRetriesSameMonsterAndEarnsOnce()
    {
        SyntheticSolo(daily: true);
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice));
        long room = await Load(alice);
        var actor = await BossIn(room);
        string source = await RefuseTerminalKillAsync(alice, actor);
        var prepared = Assert.Single(Service<DungeonObligationStore>().FrozenRoom(room));
        Assert.True(prepared.RequiresKill);
        Assert.Equal(source, prepared.KillSourceKey);
        Assert.Empty(Service<DungeonObligationStore>().Pending((long)alice.Self!.Uuid));
        Assert.Equal(0, Items(alice, 1250900));
        await OnLoop(() => { actor.Hp = 2; return Task.CompletedTask; });
        await Kill(alice, actor);
        Assert.Equal(source, actor.RewardSource!.Key);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        Assert.True(await Service<CharacterService>().RewardSourceCommittedAsync(source));
        Assert.Equal(100, Items(alice, 1250900));
        Assert.Empty(Service<DungeonObligationStore>().Pending((long)alice.Self.Uuid));
    }

    [ClientDataFact]
    public async Task PreparedFinalKillWithoutOrdinaryReceiptCannotPayAfterRestart()
    {
        SyntheticSolo(daily: true);
        long room, role;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm(alice, "!lvl 99");
            Assert.Equal(0, await Enter(alice));
            room = await Load(alice);
            role = (long)alice.Self!.Uuid;
            await RefuseTerminalKillAsync(alice, await BossIn(room));
            Assert.Single(Service<DungeonObligationStore>().FrozenRoom(room));
            Assert.Empty(Service<DungeonObligationStore>().Pending(role));
        }
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await InitializeAsync();
        await using var recovered = await InWorld("Alice", create: false);
        Assert.Empty(Service<DungeonObligationStore>().Pending(role));
        Assert.Single(Service<DungeonObligationStore>().FrozenRoom(room));
        Assert.Equal(0, Items(recovered, 1250900));
        Assert.DoesNotContain(Session(recovered).Dungeons!.Records.Values, record => record.Passed);
    }
}
