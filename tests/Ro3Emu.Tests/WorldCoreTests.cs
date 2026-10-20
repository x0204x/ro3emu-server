using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>World subscriptions, owner validation, flight, revive invitations and durable visits over the game wire.</summary>
public sealed class WorldCoreTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ro3-world-core-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    private GameSession Player(string account) => Service<SessionRegistry>().All().Single(s => s.Account == account);

    public async Task InitializeAsync()
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        app = ServerHost.Create([], o => { o.DataDir = directory; o.GameDataDir = Path.Combine(RepoRoot(), "data"); o.HttpPort = hp; o.MicroGatePort = mp; o.GameGatePort = gp; });
        await app.StartAsync(); http = $"http://127.0.0.1:{hp}";
    }
    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }
    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id); return bot;
    }
    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }
    private async Task ChangeScene(BotClient bot, GameSession session, long map, long instance = 0)
    {
        Assert.True(await Service<GameLoop>().InvokeAsync(() => Service<SceneService>().EnterSceneAsync(session, map, instanceId: instance)));
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, p => p.SceneEntry == map);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = map });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, p => p.SceneId == map && p.Result == 0);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
    }

    [ClientDataFact]
    public async Task VisitedMapIsPublishedOnlyAfterItsOwnedCommit()
    {
        await using var bot = await InWorld("Alice");
        await using var other = await InWorld("Bob");
        var session = Player("alice");
        var loop = Service<GameLoop>();
        var roles = Service<RoleStore>();
        long role = session.Role!.Id;
        byte[] previous = roles.LoadWorldState(role)!;
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await loop.InvokeAsync(() => Service<SceneService>().EnterSceneAsync(session, 10100)));
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, p => p.SceneEntry == 10100);
        await loop.InvokeAsync(() =>
        {
            Service<AccountStore>().BeforeOwnedCommitForTest = label =>
            {
                if (label != "world/visited-map") return Task.CompletedTask;
                staged.TrySetResult(); return release.Task;
            };
            return Task.CompletedTask;
        });
        try
        {
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10100 });
            await staged.Task.WaitAsync(Wait);
            Assert.DoesNotContain(10100L, session.WorldState.Saved.VisitedMaps);
            Assert.Equal(previous, roles.LoadWorldState(role));
            Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(WorldCoreService.VisitedMaps)
                && SyncRoleVisitMapInfoNtf.Parser.ParseFrom(e.Body).Mapid.Contains(10100));
            await loop.InvokeAsync(() => Service<DungeonService>().SetGuideAsync(Player("bob"),
                new MultiDungeonSetJobPositionGuideFlagReq { GuideFlag = true })).WaitAsync(Wait);
            Assert.True(Player("bob").Dungeons!.FirstSetted);
        }
        finally
        {
            release.TrySetResult();
            Service<AccountStore>().BeforeOwnedCommitForTest = null;
        }
        await Next(bot, WorldCoreService.VisitedMaps, SyncRoleVisitMapInfoNtf.Parser, p => p.Mapid.Contains(10100));
        Assert.Contains(10100L, session.WorldState.Saved.VisitedMaps);
        Assert.Contains(10100L, System.Text.Json.JsonSerializer.Deserialize<WorldRoleState>(roles.LoadWorldState(role)!)!.VisitedMaps);
    }

    [ClientDataFact]
    public async Task SupersededVisitCommitKeepsTheVisitWithoutPublishingTheOldSceneHook()
    {
        await using var bot = await InWorld("Alice");
        await Next(bot, WorldCoreService.AoiInfo, AoIInfoTotal.Parser);
        var session = Player("alice");
        var loop = Service<GameLoop>();
        var scene = Service<SceneService>();
        long previousScene = session.SceneEntry;
        Assert.True(await loop.InvokeAsync(() => scene.EnterSceneAsync(session, 10100)));
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, p => p.SceneEntry == 10100);
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != "world/visited-map") return Task.CompletedTask;
            staged.TrySetResult(); return release.Task;
        };
        var loaded = loop.InvokeAsync(() => scene.SceneLoadedAsync(session, new SceneLoadedReq { SceneId = 10100 }));
        Task<bool>? transfer = null;
        try
        {
            await staged.Task.WaitAsync(Wait);
            long generation = session.SceneGeneration;
            transfer = loop.InvokeAsync(() => scene.EnterSceneAsync(session, previousScene));
            await loop.InvokeAsync(() =>
            {
                Assert.True(session.SceneGeneration > generation);
                return Task.CompletedTask;
            }).WaitAsync(Wait);
        }
        finally { release.TrySetResult(); Service<AccountStore>().BeforeOwnedCommitForTest = null; }
        await loaded.WaitAsync(Wait);
        Assert.True(await transfer!.WaitAsync(Wait));
        // A later response fences delivery of every packet already queued by the old hook.
        await bot.SendGameAsync(WorldCoreService.PlayerDetail, new GetPlayerInSceneDetailReq());
        await Next(bot, WorldCoreService.PlayerDetail, GetPlayerInSceneDetailResp.Parser);
        Assert.Single(bot.Inbox.Snapshot(), e => e.IsGame(WorldCoreService.AoiInfo));
        Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(WorldCoreService.VisitedMaps)
            && SyncRoleVisitMapInfoNtf.Parser.ParseFrom(e.Body).Mapid.Contains(10100));
        Assert.Contains(10100L, session.WorldState.Saved.VisitedMaps);
        Assert.True(session.SceneLoading);
    }

    [ClientDataFact]
    public async Task SnapshotsAreInstanceScopedAndAssetReplayAdvancesVersion()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        var a = Player("alice"); var b = Player("bob");
        Assert.Equal((ulong)a.Role!.Id, Assert.Single((await Next(alice, WorldCoreService.NameBoards, NameBoardListNotify.Parser)).Datas).Uid);
        var clock = await Next(alice, WorldCoreService.ServerInfo, ServerBasicInfo.Parser);
        Assert.True(clock.ServerLogicTime > 1_000_000_000);
        var visits = await Next(alice, WorldCoreService.VisitedMaps, SyncRoleVisitMapInfoNtf.Parser);
        Assert.Contains(a.SceneEntry, visits.Mapid);
        await alice.SendGameAsync(31043, new MiniMapStatus { IsOpen = true });
        var mini = await Next(alice, WorldCoreService.MiniMap, MiniMapMsg.Parser, p => p.MiniInfo.Any(i => i.Uid == (ulong)b.Role!.Id));
        Assert.Contains(mini.MiniInfo, i => i.Uid == (ulong)a.Role!.Id);
        await alice.SendGameAsync(WorldCoreService.PlayerDetail, new GetPlayerInSceneDetailReq { Ids = { (ulong)b.Role!.Id }, BGetName = true });
        Assert.Equal("Bob", Assert.Single((await Next(alice, WorldCoreService.PlayerDetail, GetPlayerInSceneDetailResp.Parser)).Data).Name);
        await alice.SendGameAsync(30007, new ChangeViewCfgReq { NameBoardMaxCount = 1 });
        await Next(alice, 30007, ChangeViewCfgResp.Parser);
        var far = Service<WorldData>().Map(b.SceneEntry)!.Grounded(new MapPoint(156, 0, 192, 0));
        await Service<GameLoop>().InvokeAsync(() => Service<SceneService>().TeleportInMapAsync(b, far));
        await Next(alice, WorldCoreService.NameBoards, NameBoardListNotify.Parser, p => p.Datas.Any(d => d.Uid == (ulong)b.Role!.Id));
        await ChangeScene(bob, b, b.SceneEntry, 987654321);
        await Next(alice, WorldCoreService.NameBoards, NameBoardListNotify.Parser, p => p.Datas.Count == 1 && p.Datas[0].Uid == (ulong)a.Role.Id);
        await alice.SendGameAsync(WorldCoreService.PlayerDetail, new GetPlayerInSceneDetailReq { Ids = { (ulong)b.Role.Id }, BGetName = true });
        Assert.Empty((await Next(alice, WorldCoreService.PlayerDetail, GetPlayerInSceneDetailResp.Parser)).Data);
        await alice.SendGameAsync(WorldCoreService.MonitorCustom, new MonitorCustomDataReq { EntityIds = { b.Role.Id } });
        Assert.NotEqual(0, (await Next(alice, WorldCoreService.MonitorCustom, MonitorCustomDataResp.Parser)).Result);
        await alice.SendGameAsync(31043, new MiniMapStatus { IsOpen = true });
        await Next(alice, WorldCoreService.MiniMap, MiniMapMsg.Parser, p => p.MiniInfo.All(i => i.Uid != (ulong)b.Role.Id));
        var before = a.SyncVersions[CharacterService.ItemsSync];
        await alice.SendGameAsync(MsgId.PlayerAsset, new PlayerAssetReq { AssetType = { 6 } });
        Assert.Contains(6L, (await Next(alice, MsgId.PlayerAsset, PlayerAssetResp.Parser)).AssetType);
        Assert.True(a.SyncVersions[CharacterService.ItemsSync] > before);
        long scene = a.SceneEntry;
        await alice.SendGameAsync(WorldCoreService.SceneQueue, new SceneQueuingReq { SceneId = 10100 });
        Assert.NotEqual(0, (await Next(alice, 99999, APIErrorMsg.Parser, p => p.OriginMsgId == WorldCoreService.SceneQueue)).ErrorCode);
        Assert.Equal(scene, a.SceneEntry);
    }

    [ClientDataFact]
    public async Task FullPublicMapQueuesCancelsAndAdmitsWithoutPrematureSceneChange()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        var a = Player("alice"); var b = Player("bob");
        await Service<GameLoop>().InvokeAsync(() =>
        {
            // App-local authored-capacity fixture; the shared config files remain read-only.
            var rows = (IDictionary<string, System.Text.Json.JsonElement>)Service<Ro3Emu.Server.Data.GameData>().Table("MapConfig");
            var row = System.Text.Json.Nodes.JsonNode.Parse(rows["10100"].GetRawText())!;
            row["_iRoleCntLimit"] = 1; row["_iAllowCreateLine"] = 0; // Exercise the single-line FIFO, independent of line creation.
            rows["10100"] = System.Text.Json.JsonSerializer.SerializeToElement(row);
            return Task.CompletedTask;
        });
        await ChangeScene(alice, a, 10100);
        await bob.SendGameAsync(30012, new SceneTeleportReq { TargetMapId = 10100 });
        await Next(bob, WorldCoreService.SceneQueue, SceneQueuingRes.Parser, p => p.LineOrder == 1);
        Assert.Equal(10111, b.SceneEntry); Assert.False(b.SceneLoading);
        await bob.SendGameAsync(WorldCoreService.QueueCancel, new SceneQueuingCancel { SceneId = 10100 });
        await bob.SendGameAsync(WorldCoreService.SceneQueue, new SceneQueuingReq { SceneId = 10100 });
        await Next(bob, 99999, APIErrorMsg.Parser, p => p.OriginMsgId == WorldCoreService.SceneQueue);
        Assert.Equal(0, b.WorldState.QueueScene);
        await bob.SendGameAsync(30012, new SceneTeleportReq { TargetMapId = 10100 });
        await Next(bob, WorldCoreService.SceneQueue, SceneQueuingRes.Parser, p => p.LineOrder == 1);
        await ChangeScene(alice, a, 10111);
        await Next(bob, WorldCoreService.SceneQueue, SceneQueuingRes.Parser, p => p.LineOrder == 0);
        await Next(bob, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, p => p.SceneEntry == 10100);
        await bob.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10100 });
        await Next(bob, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Equal(10100, b.SceneEntry); Assert.Equal(0, b.WorldState.QueueScene);
    }

    [ClientDataFact]
    public async Task CapacityReservationSurvivesAwaitingOldSceneLeave()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        var a = Player("alice"); var b = Player("bob");
        var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            var rows = (IDictionary<string, System.Text.Json.JsonElement>)Service<Ro3Emu.Server.Data.GameData>().Table("MapConfig");
            var row = System.Text.Json.Nodes.JsonNode.Parse(rows["10100"].GetRawText())!;
            row["_iRoleCntLimit"] = 1; row["_iAllowCreateLine"] = 0;
            rows["10100"] = System.Text.Json.JsonSerializer.SerializeToElement(row);
            Service<SceneService>().Leaving.Add(async s => { if (s == a) { left.TrySetResult(); await release.Task; } });
            return Task.CompletedTask;
        });
        var first = Service<GameLoop>().InvokeAsync(() => Service<SceneService>().EnterSceneAsync(a, 10100));
        try
        {
            await left.Task.WaitAsync(Wait);
            await bob.SendGameAsync(30012, new SceneTeleportReq { TargetMapId = 10100 });
            await Next(bob, WorldCoreService.SceneQueue, SceneQueuingRes.Parser, p => p.LineOrder == 1);
            Assert.Equal(10111, b.SceneEntry); Assert.False(b.SceneLoading);
        }
        finally { release.TrySetResult(); }
        Assert.True(await first);
        Assert.Equal(10100, a.SceneEntry); Assert.Equal(10111, b.SceneEntry);
    }

    [ClientDataFact]
    public async Task InterruptedResurrectionChantCannotCreateAnInvitation()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        var a = Player("alice"); var b = Player("bob");
        var combat = Service<CombatService>();
        int offers = 0;
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            a.Progress!.Level = 50; a.Progress.Skills[14112] = 1; b.Dead = true;
            await Service<CharacterService>().SetHpSpAsync(a, null, long.MaxValue);
            await Service<CharacterService>().SetHpSpAsync(b, 0, 0);
            // Count the actual scheduled producer; existing wire test covers its normal delivery.
            combat.ReviveOffer = (_, _, _) => { offers++; return Task.CompletedTask; };
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1411201, TargetId = b.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq());
        Assert.True((await Next(alice, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        var afterChant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            Service<GameLoop>().Schedule(TimeSpan.FromMilliseconds(1200), () => { afterChant.TrySetResult(); return Task.CompletedTask; });
            return Task.CompletedTask;
        });
        await afterChant.Task.WaitAsync(Wait);
        Assert.Equal(0, offers); Assert.True(b.Dead); Assert.Equal(0, a.WorldState.ResurrectionCastId);
    }

    [ClientDataFact]
    public async Task FlightRequiresBuffAndRejectsRepeatedTakeoffNonfiniteAndTeleport()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        var s = Player("alice");
        await alice.SendGameAsync(WorldCoreService.FlySwitch, new FlySwitchReq { IsFly = true, Height = 2 });
        await Next(alice, 99999, APIErrorMsg.Parser, p => p.OriginMsgId == WorldCoreService.FlySwitch);
        Assert.False(s.WorldState.Flying);
        await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().AddAsync(s, 3110201, 30000, s.Role!.Id));
        await alice.SendGameAsync(WorldCoreService.FlySwitch, new FlySwitchReq { IsFly = true, Height = 2 });
        var start = await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser, p => p.TransType == MovementTransitionType.MoveTransFlyUp);
        Assert.True(s.WorldState.Flying);
        await alice.SendGameAsync(WorldCoreService.FlySwitch, new FlySwitchReq { IsFly = true, Height = 40 });
        await Next(alice, 99999, APIErrorMsg.Parser, p => p.OriginMsgId == WorldCoreService.FlySwitch);
        Assert.Equal(start.MovementInfo.Pos.Y, s.Position.Y);
        await alice.SendGameAsync(WorldCoreService.FlyMove, new FlyMoveReq { Pos = new Vec3 { X = float.NaN }, Dir = new Vec3(), Face = 0 });
        await Next(alice, 99999, APIErrorMsg.Parser, p => p.OriginMsgId == WorldCoreService.FlyMove);
        Assert.True(float.IsFinite(s.Position.X));
        var far = s.Position.Clone(); far.X += 500;
        await alice.SendGameAsync(WorldCoreService.FlyMove, new FlyMoveReq { Pos = far, Dir = new Vec3 { X = 1 } });
        await Next(alice, 99999, APIErrorMsg.Parser, p => p.OriginMsgId == WorldCoreService.FlyMove);
        Assert.NotEqual(far.X, s.Position.X);
        await alice.SendGameAsync(WorldCoreService.FlyMove, new FlyMoveReq { Pos = s.Position.Clone(), Dir = new Vec3 { X = 1 }, Face = 90 });
        var moved = await Next(bob, GameAoiSink.MovementChange, MovementChangeNtf.Parser, p => p.Uid == s.Role!.Id && p.MovementInfo.MoveType == MovementType.MoveTypeMove);
        Assert.True(moved.MovementInfo.MovementMove.StartVelocity.X > 0);
        await alice.SendGameAsync(WorldCoreService.FreeFall, new FreeFallJumpReq { ToPos = s.Position.Clone(), Face = 90 });
        await Next(alice, WorldCoreService.FixLand, FixLandNtf.Parser);
        Assert.False(s.WorldState.Flying);
    }

    [ClientDataFact]
    public async Task ResurrectionRequiresRealSkillAndMatchingSingleUseInvitation()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        var a = Player("alice"); var b = Player("bob");
        await Service<GameLoop>().InvokeAsync(async () => { b.Dead = true; await Service<CharacterService>().SetHpSpAsync(b, 0, 0); });
        await bob.SendGameAsync(WorldCoreService.ReliveHelp, new EntityReliveHelpReq { TargetEntityID = b.Role!.Id });
        Assert.Equal(0, (await Next(bob, WorldCoreService.ReliveHelp, EntityReliveHelpResp.Parser)).Result);
        await bob.SendGameAsync(WorldCoreService.ReliveHelp, new EntityReliveHelpReq { TargetEntityID = b.Role.Id });
        Assert.NotEqual(0, (await Next(bob, WorldCoreService.ReliveHelp, EntityReliveHelpResp.Parser)).Result);
        await bob.SendGameAsync(WorldCoreService.ReliveAccept, new EntityReliveInviteAcceptReq { SourceEntityID = a.Role!.Id, IsAccept = true });
        Assert.NotEqual(0, (await Next(bob, WorldCoreService.ReliveAccept, EntityReliveInviteAcceptResp.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1411201, TargetId = b.Role.Id });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Service<GameLoop>().InvokeAsync(() => { a.Progress!.Skills[14112] = 1; return Task.CompletedTask; });
        await Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().SetHpSpAsync(a, null, 0));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1411201, TargetId = b.Role.Id });
        Assert.Equal(3013, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            a.Progress!.Level = 50;
            await Service<CharacterService>().SetHpSpAsync(a, null, long.MaxValue);
            Assert.True(Service<CharacterService>().Stats(a)[2001] >= 30);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1411201, TargetId = b.Role.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var invitation = await Next(bob, WorldCoreService.ReliveInvitation, EntityReliveReceiveInviteNotify.Parser);
        Assert.Equal(a.Role.Id, invitation.SourceEntityID); Assert.True(invitation.EndTimestampMs > 0);
        await bob.SendGameAsync(WorldCoreService.ReliveAccept, new EntityReliveInviteAcceptReq { SourceEntityID = a.Role.Id, IsAccept = true });
        Assert.Equal(0, (await Next(bob, WorldCoreService.ReliveAccept, EntityReliveInviteAcceptResp.Parser)).Result);
        await Next(bob, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, p => !p.BDead);
        Assert.False(b.Dead); Assert.True(Service<CharacterService>().Stats(b)[2000] > 0);
        await bob.SendGameAsync(WorldCoreService.ReliveAccept, new EntityReliveInviteAcceptReq { SourceEntityID = a.Role.Id, IsAccept = true });
        Assert.Equal(ErrorCodes.TargetAlive, (await Next(bob, WorldCoreService.ReliveAccept, EntityReliveInviteAcceptResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task CustomMonitoringAndTemporaryLeaveHaveActualStateAndVisitsSurviveRelog()
    {
        await using (var alice = await InWorld("Alice"))
        {
            var s = Player("alice"); var core = Service<WorldCoreService>();
            await alice.SendGameAsync(WorldCoreService.MonitorCustom, new MonitorCustomDataReq { EntityIds = { s.Role!.Id } });
            Assert.Equal(0, (await Next(alice, WorldCoreService.MonitorCustom, MonitorCustomDataResp.Parser)).Result);
            await Service<GameLoop>().InvokeAsync(() => core.SetCustomAsync(s, new CustomData { Id = 123, CustomParam = new CustomParam { Tp = (CustomParam.Types.ParamType)3, LongParam = 42 } }));
            Assert.Equal(42, Assert.Single((await Next(alice, WorldCoreService.MonitorSync, CustomDataChange.Parser)).CustomDatas).CustomParam.LongParam);
            await Service<GameLoop>().InvokeAsync(() => core.BeginTemporaryLeaveAsync(s, 30));
            Assert.Equal(30, (await Next(alice, WorldCoreService.TempLeave, TempLeaveNtf.Parser)).CountdownSeconds);
            await alice.SendGameAsync(WorldCoreService.RecoverLeave, new RecureTempLeaveReq());
            await Next(alice, WorldCoreService.TempLeave, TempLeaveNtf.Parser, p => p.CountdownSeconds == 0);
            Assert.Equal(0, s.WorldState.TemporaryLeaveUntilMs);
            await Service<GameLoop>().InvokeAsync(() => core.SetSceneVariableAsync(s.WorldScene, new CommonPublicVar { VarId = 99, Vi64 = { 42 } }));
            Assert.Equal(42, (await Next(alice, WorldCoreService.SceneEvent, SceneEventNotify.Parser)).Vars[99].Vi64[0]);
            await using (var observer = await InWorld("Observer"))
                Assert.Equal(42, (await Next(observer, WorldCoreService.SceneVariables, SceneBaseInfoNotify.Parser)).Vars[99].Vi64[0]);
            await Service<GameLoop>().InvokeAsync(() => core.SetSceneVariableAsync(s.WorldScene, new CommonPublicVar { VarId = 99, IsDelete = true }));
            Assert.True((await Next(alice, WorldCoreService.SceneEvent, SceneEventNotify.Parser, p => p.Vars[99].IsDelete)).Vars[99].IsDelete);
            await ChangeScene(alice, s, 10100);
            await Next(alice, WorldCoreService.VisitedMaps, SyncRoleVisitMapInfoNtf.Parser, p => p.Mapid.Contains(10100));
        }
        await using var again = await InWorld("Alice", false);
        var saved = await Next(again, WorldCoreService.VisitedMaps, SyncRoleVisitMapInfoNtf.Parser, p => p.Mapid.Contains(10100));
        Assert.Contains(10111, saved.Mapid);
    }

    private static string RepoRoot()
    {
        var p = new DirectoryInfo(AppContext.BaseDirectory);
        while (p != null && !Directory.Exists(Path.Combine(p.FullName, "data", "config"))) p = p.Parent;
        return p?.FullName ?? throw new DirectoryNotFoundException("data/config");
    }
}
