using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Pet wire transactions against the exported client tables; no game client or fixed server ports.</summary>
public sealed class PetTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-pet-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication _app = null!;
    private string _http = "";
    private readonly TestClock _clock = new();
    private sealed class TestClock : TimeProvider
    {
        private long _seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void Advance(long count) => Interlocked.Add(ref _seconds, count);
    }

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name = "Alice", bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string command)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync($"{_http}/ro3emu/gm?account=alice", new StringContent(command));
        response.EnsureSuccessStatusCode();
    }

    private async Task Unlock(bool hatch = false)
    {
        await Gm("!quest done 10104330");
        await Gm("!lvl 1");
        if (hatch)
        {
            await Gm("!quest done 10105010");
            // Isolate transaction costs from the quest's one egg material and 100 accelerator items.
            await Gm("!delitem 1250709");
            await Gm("!delitem 1250724");
        }
    }

    private RoleStore Roles => _app.Services.GetRequiredService<RoleStore>();
    private long RoleId(BotClient bot) => (long)bot.Self!.Uuid;
    private DBPetInfo Saved(BotClient bot) => DBPetInfo.Parser.ParseFrom(Roles.LoadPets(RoleId(bot))!);
    private long Count(BotClient bot, int entry) => _app.Services.GetRequiredService<GameData>().Row("ItemConfig", entry) is { } row
        && GameData.Int(row, "_iType") == 1 ? Roles.LoadProgress(RoleId(bot)).Currency.GetValueOrDefault(entry)
        : _app.Services.GetRequiredService<ItemStore>().Load(RoleId(bot)).Where(i => i.Entry == entry && i.Grid == 0).Sum(i => (long)i.Stack);
    private GameSession Session => _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");

    private async Task CombinePet(BotClient bot, int pet = 10001, int fragment = 1610001)
    {
        await Gm($"!item {fragment} 20");
        await bot.SendGameAsync(PetService.Combine, new PetCombineReq { PetId = pet });
        Assert.Equal(0, (await Next(bot, PetService.Combine, PetCombineResp.Parser)).Result);
        await Next(bot, PetService.PetSync, DBPetInfoIncrement.Parser, p => p.ListPet.TryGetValue(pet, out var value) && value.All != null);
    }

    private static float Distance(Vec3 a, Vec3 b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static Vec3 Project(MovementChangeNtf move)
    {
        var info = move.MovementInfo;
        if (info.MoveType != MovementType.MoveTypeWalk) return info.Pos;
        float angle = info.MovementWalk.Dir * MathF.PI / 180f;
        float distance = info.MovementWalk.Speed * info.MoveDuration / 1000f;
        return new Vec3
        {
            X = info.Pos.X + distance * MathF.Sin(angle), Y = info.MovementWalk.EndHeight,
            Z = info.Pos.Z + distance * MathF.Cos(angle)
        };
    }

    private static async Task<EntityBase> FollowSceneChange(BotClient bot)
    {
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        return (await Next(bot, MsgId.EntitySelf, EntitySelf.Parser)).Base;
    }

    [ClientDataFact]
    public async Task EmptyRosterInitializesBeforeFunctionSyncAndGatesAllMutations()
    {
        await using var bot = await InWorld();
        var sync = await Next(bot, PetService.PetSync, DBPetInfoIncrement.Parser);
        Assert.True(sync.SyncAllMark);
        Assert.Empty(sync.ListPet);
        Assert.Equal(0, sync.CurrentPlan); // plan N is "Build N+1"
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, sync.SetupPlans.Keys.Order());
        Assert.Empty(sync.SetupPlans[0].All.SetUpPets);
        Assert.Equal(1, sync.IncubationLv);
        Assert.Equal(1, sync.OnOpenUIRoomLv);
        var events = bot.Inbox.Snapshot().ToList();
        Assert.True(events.FindIndex(e => e.IsGame(5017)) < events.FindIndex(e => e.IsGame(5056)));
        await Gm("!item 1610001 20");
        await bot.SendGameAsync(PetService.Combine, new PetCombineReq { PetId = 10001 });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(bot, PetService.Combine, PetCombineResp.Parser)).Result);
        Assert.Equal(20, Count(bot, 1610001));
        Assert.Empty(Saved(bot).ListPet);
        await Unlock();
        await Gm("!item 1250709 1");
        await bot.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = 10001, SlotId = 1 });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        Assert.Equal(1, Count(bot, 1250709));
        await Gm("!quest done 10105010");
        var functions = _app.Services.GetRequiredService<FunctionService>();
        Assert.All(new[] { 33, 3301, 3302 }, f => Assert.True(functions.IsOpen(Session, f)));
    }

    [ClientDataFact]
    public async Task CombineDebitsExactlyOnceUsesAllWrapperAndSurvivesRelog()
    {
        await using (var bot = await InWorld())
        {
            await Unlock();
            await bot.SendGameAsync(PetService.Combine, new PetCombineReq { PetId = 999999 });
            Assert.Equal(ErrorCodes.PetNoConfig, (await Next(bot, PetService.Combine, PetCombineResp.Parser)).Result);
            await bot.SendGameAsync(PetService.Combine, new PetCombineReq { PetId = 10001 });
            Assert.Equal(ErrorCodes.PetCombineMaterials, (await Next(bot, PetService.Combine, PetCombineResp.Parser)).Result);
            await CombinePet(bot);
            Assert.Equal(0, Count(bot, 1610001));
            var pet = Saved(bot).ListPet[10001];
            Assert.Equal((10001, 1, 1, 0, 0L), (pet.PetId, pet.Level, pet.Star, pet.StarProgress, pet.LevelExp));
            var events = bot.Inbox.Snapshot().ToList();
            int reply = events.FindIndex(e => e.IsGame(PetService.Combine) && PetCombineResp.Parser.ParseFrom(e.Body).Result == 0);
            int state = events.FindIndex(e => e.IsGame(5017) && DBPetInfoIncrement.Parser.ParseFrom(e.Body).ListPet.ContainsKey(10001));
            Assert.True(reply < state);
            Assert.DoesNotContain(events, e => e.IsGame(33309));
            await Gm("!item 1610001 20");
            await bot.SendGameAsync(PetService.Combine, new PetCombineReq { PetId = 10001 });
            Assert.Equal(ErrorCodes.PetAlreadyOwned, (await Next(bot, PetService.Combine, PetCombineResp.Parser)).Result);
            Assert.Equal(20, Count(bot, 1610001));
        }
        await using var again = await InWorld(create: false);
        var sync = await Next(again, 5017, DBPetInfoIncrement.Parser);
        Assert.Single(sync.ListPet);
        Assert.Equal(10001, sync.ListPet[10001].All.PetId);
        Assert.Empty(sync.SetupPlans[0].All.SetUpPets);
    }

    [ClientDataFact]
    public async Task LineupEchoesRequestSwapsAndDrivesOwnerBoundTypeSevenOverWire()
    {
        await using var bot = await InWorld();
        await using var viewer = await InWorld("Bob");
        await Unlock();
        await CombinePet(bot);
        await CombinePet(bot, 11001, 1611001);
        var req = new PetStateUpdateReq { PetId = 10001, Target = (PetStateSource_Type)1, TargetSlotIndex = 1 };
        await bot.SendGameAsync(PetService.StateUpdate, req);
        Assert.Equal(ErrorCodes.PetSetupSlot, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        await Gm("!lvl 33");
        await bot.SendGameAsync(PetService.StateUpdate, req);
        var reply = await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser);
        Assert.Equal(0, reply.Result);
        Assert.Equal(req, reply.Req);
        Assert.Equal(10001, reply.SetUpPets[1]);
        Assert.Equal(379, Saved(bot).SetupPlans[0].Power.NormalPower);
        var ownView = await Next(bot, 31037, AoIViewChangeData.Parser, m => m.Bases.Any(e => e.EntityType == 7));
        var actor = ownView.Bases.Single(e => e.EntityType == 7);
        Assert.Equal((long)bot.Self!.Uuid, actor.Owner);
        Assert.Equal(2300440UL, actor.EntityTemplateID);
        var remote = await Next(viewer, 31037, AoIViewChangeData.Parser, m => m.Bases.Any(e => e.Uuid == actor.Uuid));
        Assert.Equal(actor.Owner, remote.Bases.Single(e => e.Uuid == actor.Uuid).Owner);
        req.TargetSlotIndex = 3;
        await bot.SendGameAsync(PetService.StateUpdate, req);
        Assert.Equal(ErrorCodes.PetAlreadyAssigned, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        req.Source = (PetStateSource_Type)1; req.SourceSlotIndex = 2;
        await bot.SendGameAsync(PetService.StateUpdate, req);
        Assert.Equal(ErrorCodes.PetSourceSlot, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        req.SourceSlotIndex = 1;
        await bot.SendGameAsync(PetService.StateUpdate, req);
        Assert.Equal(ErrorCodes.PetSetupSlot, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        await bot.SendGameAsync(PetService.StateUpdate, new PetStateUpdateReq { PetId = 11001, Target = (PetStateSource_Type)1, TargetSlotIndex = 2 });
        Assert.Equal(0, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        req.TargetSlotIndex = 2;
        await bot.SendGameAsync(PetService.StateUpdate, req);
        var swapped = await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser);
        Assert.Equal(0, swapped.Result);
        Assert.Equal(11001, swapped.SetUpPets[1]);
        Assert.Equal(10001, swapped.SetUpPets[2]);
        await bot.SendGameAsync(PetService.StateUpdate, new PetStateUpdateReq { PetId = 12001, Target = (PetStateSource_Type)1, TargetSlotIndex = 1 });
        Assert.Equal(ErrorCodes.PetNotOwned, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task BoboFollowsOverWireAndSurvivesVisibilityMapAndPlanChanges()
    {
        await using var bot = await InWorld();
        await using var viewer = await InWorld("Bob");
        var born = Session.Position.Clone();
        await Unlock();
        await CombinePet(bot, 13001, 1613001);
        await Gm("!lvl 33");
        await bot.SendGameAsync(PetService.StateUpdate,
            new PetStateUpdateReq { PetId = 13001, Target = (PetStateSource_Type)1, TargetSlotIndex = 1 });
        Assert.Equal(0, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        var own = await Next(bot, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.EntityType == 7));
        var bobo = own.Bases.Single(e => e.EntityType == 7);
        long petUid = (long)bobo.Uuid, ownerUid = (long)bot.Self!.Uuid;
        Assert.Equal(2300130UL, bobo.EntityTemplateID);
        Assert.Equal(ownerUid, bobo.Owner);
        Assert.InRange(Distance(bobo.MovementInfo.Pos, born), 2.99f, 3.01f);
        var peerSpawn = await Next(viewer, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.Uuid == bobo.Uuid));
        Assert.Equal(bobo.MovementInfo.Pos, peerSpawn.Bases.Single(e => e.Uuid == bobo.Uuid).MovementInfo.Pos);

        foreach (var (x, dir, stop) in new[] { (born.X + 2, 90f, false), (born.X + 4, 90f, false),
                     (born.X + 4, 90f, true), (born.X + 2, 270f, false), (born.X + 2, 270f, true) })
        {
            var request = new WalkReq
            {
                Pos = new Vec3 { X = x, Y = born.Y, Z = born.Z },
                Dir = dir, Face = dir, SpeedRate = 1, BStop = stop
            };
            await bot.SendGameAsync(MsgId.Walk, request);
            var ownerMove = await Next(viewer, 31081, MovementChangeNtf.Parser, m => m.Uid == ownerUid);
            var ownerPet = await Next(bot, 31081, MovementChangeNtf.Parser, m => m.Uid == petUid);
            var peerPet = await Next(viewer, 31081, MovementChangeNtf.Parser, m => m.Uid == petUid);
            Assert.Equal(ownerPet.MovementInfo.Pos, peerPet.MovementInfo.Pos);
            Assert.Equal(ownerPet.MovementInfo.MoveType, peerPet.MovementInfo.MoveType);
            Assert.Equal(stop ? MovementType.MoveTypeNone : MovementType.MoveTypeWalk, peerPet.MovementInfo.MoveType);
            Assert.InRange(Distance(ownerMove.MovementInfo.Pos, peerPet.MovementInfo.Pos), 2.99f, 3.01f);
            Assert.InRange(Distance(Project(ownerMove), Project(peerPet)), 2.99f, 3.01f);
        }

        await bot.SendGameAsync(31020, new ChangeFaceReq { Angle = 180 });
        Assert.Equal(180f, (await Next(bot, 31014, EntityFaceToAll.Parser, m => m.EntityID == bobo.Uuid)).Face);
        Assert.Equal(180f, (await Next(viewer, 31014, EntityFaceToAll.Parser, m => m.EntityID == bobo.Uuid)).Face);

        await Gm(FormattableString.Invariant($"!jump {born.X + 6:0.0} {born.Z:0.0}"));
        var jumpedOwner = await Next(viewer, 31081, MovementChangeNtf.Parser, m => m.Uid == ownerUid);
        var jumpedPet = await Next(viewer, 31081, MovementChangeNtf.Parser, m => m.Uid == petUid);
        Assert.Equal(MovementTransitionType.MoveTransForceTeleport, jumpedPet.TransType);
        Assert.Equal(MovementType.MoveTypeNone, jumpedPet.MovementInfo.MoveType);
        Assert.InRange(Distance(jumpedOwner.MovementInfo.Pos, jumpedPet.MovementInfo.Pos), 2.99f, 3.01f);
        Assert.Equal(MovementTransitionType.MoveTransForceTeleport,
            (await Next(bot, 31081, MovementChangeNtf.Parser, m => m.Uid == petUid)).TransType);

        await using var late = await InWorld("Charlie");
        await Next(late, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.Uuid == (ulong)ownerUid));
        var latePet = await Next(late, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.Uuid == bobo.Uuid));
        Assert.InRange(Distance(latePet.Bases.Single(e => e.Uuid == bobo.Uuid).MovementInfo.Pos, Session.Position), 2.99f, 3.01f);

        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = new Vec3 { X = born.X + 100, Y = born.Y, Z = born.Z }, BStop = true });
        await Next(viewer, 31037, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(bobo.Uuid));
        await Next(viewer, 31037, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains((ulong)ownerUid));
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = new Vec3 { X = born.X + 6, Y = born.Y, Z = born.Z }, BStop = true });
        await Next(viewer, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.Uuid == (ulong)ownerUid));
        await Next(viewer, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.Uuid == bobo.Uuid));

        await Gm("!warp 10100");
        await Next(viewer, 31037, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(bobo.Uuid));
        await Next(bot, 31037, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(bobo.Uuid));
        await FollowSceneChange(bot);
        var onNewMap = await Next(bot, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.Uuid == bobo.Uuid));
        Assert.InRange(Distance(onNewMap.Bases.Single(e => e.Uuid == bobo.Uuid).MovementInfo.Pos, Session.Position), 2.99f, 3.01f);
        await Gm(FormattableString.Invariant($"!warp 10111 {born.X:0.0} {born.Z:0.0}"));
        await FollowSceneChange(bot);
        await Next(bot, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.Uuid == bobo.Uuid));
        await Next(viewer, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.Uuid == (ulong)ownerUid));
        await Next(viewer, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.Uuid == bobo.Uuid));

        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(Session);
            Session.Pets!.SetupPlans[2] = new DBPetSetUpPlan();
            Roles.SavePets(ownerUid, Session.Pets.ToByteArray());
        });
        await bot.SendGameAsync(PetService.SwitchPlan, new PetSwitchPlanReq { TargetIndex = 2 });
        Assert.Equal(0, (await Next(bot, PetService.SwitchPlan, PetSwitchPlanResp.Parser)).Result);
        Assert.Null(Session.PetActor);
        await Next(bot, 31037, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(bobo.Uuid));
        await Next(viewer, 31037, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(bobo.Uuid));
        await bot.SendGameAsync(PetService.SwitchPlan, new PetSwitchPlanReq { TargetIndex = 0 });
        Assert.Equal(0, (await Next(bot, PetService.SwitchPlan, PetSwitchPlanResp.Parser)).Result);
        var redeployed = await Next(bot, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.EntityType == 7));
        var newUid = redeployed.Bases.Single(e => e.EntityType == 7).Uuid;
        Assert.NotEqual(bobo.Uuid, newUid);
        await Next(viewer, 31037, AoIViewChangeData.Parser, v => v.Bases.Any(e => e.Uuid == newUid));
        await bot.SendGameAsync(PetService.StateUpdate, new PetStateUpdateReq
        {
            PetId = 13001, Source = (PetStateSource_Type)1, SourceSlotIndex = 1,
            Target = (PetStateSource_Type)0,
        });
        Assert.Equal(0, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        await Next(bot, 31037, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(newUid));
        await Next(viewer, 31037, AoIViewChangeData.Parser, v => v.LeaveAoi.Contains(newUid));
        Assert.Null(Session.PetActor);
    }

    [ClientDataFact]
    public async Task LevelsCarryBookSurplusAndStarTransitionsChargeTargetRows()
    {
        await using var bot = await InWorld();
        await Unlock();
        await CombinePet(bot);
        await bot.SendGameAsync(PetService.LevelUp, new PetLvUpReq { PetId = 10001, EType = (EPetLvUpType)1 });
        Assert.Equal(ErrorCodes.PetOwnerLevel, (await Next(bot, PetService.LevelUp, PetLvUpResp.Parser)).Result);
        await Gm("!lvl 33");
        await bot.SendGameAsync(PetService.LevelUp, new PetLvUpReq { PetId = 10001, EType = (EPetLvUpType)1 });
        Assert.Equal(ErrorCodes.PetLevelMaterials, (await Next(bot, PetService.LevelUp, PetLvUpResp.Parser)).Result);
        await Gm("!item 1250701 2");
        await bot.SendGameAsync(PetService.LevelUp, new PetLvUpReq { PetId = 10001, EType = (EPetLvUpType)1 });
        var lv = await Next(bot, PetService.LevelUp, PetLvUpResp.Parser);
        Assert.Equal((0, 2, 396L), (lv.Result, lv.NewLevel, lv.LevelExp));
        Assert.Equal(0, Count(bot, 1250701));
        await Gm("!item 1250702 1");
        await bot.SendGameAsync(PetService.LevelUp, new PetLvUpReq { PetId = 10001, EType = (EPetLvUpType)2 });
        Assert.Equal(12, (await Next(bot, PetService.LevelUp, PetLvUpResp.Parser)).NewLevel);
        await bot.SendGameAsync(PetService.LevelUp, new PetLvUpReq { PetId = 10001, EType = (EPetLvUpType)10 });
        Assert.Equal(ErrorCodes.PetLevelConditions, (await Next(bot, PetService.LevelUp, PetLvUpResp.Parser)).Result);
        await Gm("!item 1610001 30");
        await bot.SendGameAsync(PetService.StarUp, new PetStarUpReq { PetId = 10001 });
        Assert.Equal(ErrorCodes.PetNeedsProgress, (await Next(bot, PetService.StarUp, PetStarUpResp.Parser)).Result);
        for (int stage = 1; stage <= 3; stage++)
        {
            await bot.SendGameAsync(PetService.ProgressUp, new PetProgressUpReq { PetId = 10001 });
            var progress = await Next(bot, PetService.ProgressUp, PetProgressUpResp.Parser);
            Assert.Equal((0, stage), (progress.Result, progress.StarProgress));
        }
        await bot.SendGameAsync(PetService.ProgressUp, new PetProgressUpReq { PetId = 10001 });
        Assert.Equal(ErrorCodes.PetMaxProgress, (await Next(bot, PetService.ProgressUp, PetProgressUpResp.Parser)).Result);
        await bot.SendGameAsync(PetService.StarUp, new PetStarUpReq { PetId = 10001 });
        var star = await Next(bot, PetService.StarUp, PetStarUpResp.Parser);
        Assert.Equal((0, 2, 0), (star.Result, star.Star, star.StarProgress));
        Assert.Equal(10, Count(bot, 1610001));
        await bot.SendGameAsync(PetService.ProgressUp, new PetProgressUpReq { PetId = 10001 });
        Assert.Equal(0, (await Next(bot, PetService.ProgressUp, PetProgressUpResp.Parser)).Result);
        Assert.Equal(0, Count(bot, 1610001));
        var skills = _app.Services.GetRequiredService<PetService>().ConfiguredSkills(new PetData { PetId = 10001, Star = 1 });
        Assert.Equal(2353301, skills.Protect);
        Assert.Equal(2353201, skills.Passive);
        Assert.Equal(new long[] { 2353001, 2353101 }, skills.Active);
    }

    [ClientDataFact]
    public async Task RebirthRefundsExactSpentBooksAfterRelogAndRollsBackOnLedgerFailure()
    {
        await using (var bot = await InWorld())
        {
            await Unlock();
            await CombinePet(bot);
            await Gm("!lvl 33");
            await Gm("!item 1250701 2");
            await bot.SendGameAsync(PetService.LevelUp, new PetLvUpReq { PetId = 10001, EType = (EPetLvUpType)1 });
            Assert.Equal(0, (await Next(bot, PetService.LevelUp, PetLvUpResp.Parser)).Result);
            Assert.Equal(0, Count(bot, 1250701));
            Assert.NotNull(Roles.LoadPetBooks(RoleId(bot)));
        }
        await using var again = await InWorld(create: false);
        Assert.Equal(2, Saved(again).ListPet[10001].Level);
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER fail_pet_books BEFORE UPDATE OF pet_books ON role BEGIN SELECT RAISE(ABORT, 'test ledger failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(
            () => _app.Services.GetRequiredService<PetService>().RebirthAsync(Session, new PetRebirthReq { PetId = 10001 })));
        Assert.Equal(2, Saved(again).ListPet[10001].Level);
        Assert.Equal(2, Session.Pets!.ListPet[10001].Level);
        Assert.Equal(0, Count(again, 1250701));
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER fail_pet_books");
        await again.SendGameAsync(PetService.Rebirth, new PetRebirthReq { PetId = 10001 });
        Assert.Equal(0, (await Next(again, PetService.Rebirth, PetRebirthResp.Parser)).Result);
        Assert.Equal(1, Saved(again).ListPet[10001].Level);
        Assert.Equal(0, Saved(again).ListPet[10001].LevelExp);
        Assert.Equal(2, Count(again, 1250701));
        await again.SendGameAsync(PetService.Rebirth, new PetRebirthReq { PetId = 10001 });
        Assert.Equal(ErrorCodes.PetOperation, (await Next(again, PetService.Rebirth, PetRebirthResp.Parser)).Result);
        Assert.Equal(2, Count(again, 1250701));
        // Simulate an older leveled pet with no spend ledger: equivalent whole 500-EXP books.
        await again.SendGameAsync(PetService.LevelUp, new PetLvUpReq { PetId = 10001, EType = (EPetLvUpType)1 });
        Assert.Equal(0, (await Next(again, PetService.LevelUp, PetLvUpResp.Parser)).Result);
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "UPDATE role SET pet_books=NULL WHERE id=$role", ("$role", RoleId(again)));
        await again.SendGameAsync(PetService.Rebirth, new PetRebirthReq { PetId = 10001 });
        Assert.Equal(0, (await Next(again, PetService.Rebirth, PetRebirthResp.Parser)).Result);
        Assert.Equal(2, Count(again, 1250701));
    }

    [ClientDataFact]
    public async Task PermanentAndLimitedTimePoolsEachKeepTheirOwnEggInASlot()
    {
        // Server day 29: activities 1022/1023 (TimeLimitConfig 1072/1073, days 28-170) open rounds 10008 and 10009.
        _clock.Advance(29 * 86400L);
        await using var bot = await InWorld();
        await Unlock(hatch: true);
        await bot.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = 99999, SlotId = 1 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        await Gm("!item 1250709 3");
        // The client's Permanent Pool tab then sends round 10009 and its Limited-Time Pool round 10008.
        await bot.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = 10009, SlotId = 1 });
        Assert.Equal(0, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        await bot.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = 10008, SlotId = 1 });
        Assert.Equal(0, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        await bot.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = 10008, SlotId = 1 });
        Assert.Equal(ErrorCodes.PetOperation, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        Assert.Equal(1, Count(bot, 1250709));
        Assert.Equal(new long[] { 10008, 10009 }, Saved(bot).IncubationSlot[1].IncubateInfo.Keys.Order());
        _clock.Advance(600);
        await bot.SendGameAsync(PetService.EggReward, new PetEggRewardReq { RoundId = 10008, SlotIds = { 1 } });
        Assert.Equal(0, (await Next(bot, PetService.EggReward, PetEggRewardRes.Parser)).Result);
        // Collecting the limited egg leaves the permanent egg in the same slot.
        Assert.Equal(new long[] { 10009 }, Saved(bot).IncubationSlot[1].IncubateInfo.Keys);
    }

    [ClientDataFact]
    public async Task OnlyRoundsWhoseActivityIsOpenTakeEggs()
    {
        // Server day 0 (OpenContent off): only activity 1015 (TimeLimitConfig 1065, days 0-7) is open, so the client
        // shows round 10001; the later Permanent rounds and every Limited-Time round are still closed.
        await using var bot = await InWorld();
        await Unlock(hatch: true);
        await Gm("!item 1250709 2");
        foreach (long closed in new long[] { 10002, 10003, 10008, 10009 })
        {
            await bot.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = closed, SlotId = 1 });
            Assert.Equal(ErrorCodes.ActivityClosed, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        }
        Assert.Equal(2, Count(bot, 1250709));
        Assert.Empty(Saved(bot).IncubationSlot);
        await bot.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = 10001, SlotId = 1 });
        Assert.Equal(0, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        Assert.Equal(1, Count(bot, 1250709));
    }

    [ClientDataFact]
    public async Task SixtiethHatchIsGuaranteedFromTheGuaranteedGroupsAndEachPoolCountsApart()
    {
        _clock.Advance(29 * 86400L); // rounds 10008/10009 open (see PermanentAndLimitedTimePoolsEachKeepTheirOwnEggInASlot)
        await using var bot = await InWorld();
        await Unlock(hatch: true);
        var game = _app.Services.GetRequiredService<GameData>();
        HashSet<long> GuaranteedItems(long draw)
        {
            var groups = game.Row("DrawConfig", draw)!.Value.GetProperty("_kGuaranteedBanner").EnumerateArray()
                .Select(v => v.GetInt64()).ToHashSet();
            return game.Table("DrawPoolConfig").Values.Where(r => groups.Contains(GameData.Int(r, "_iGroupID")))
                .SelectMany(r => r.GetProperty("_kItem").EnumerateArray().Select(t => t[1].GetInt64())).ToHashSet();
        }
        // Room 1 uses _kDrawIds[1]; the Normal pool's pity key is that draw's _iType, the limited pool's is its draw id.
        long permanentDraw = Flat(game.Row("PetIncubationSlotConfig", 10009)!.Value, "_kDrawIds")[1];
        long limitedDraw = Flat(game.Row("PetIncubationSlotConfig", 10008)!.Value, "_kDrawIds")[1];
        int permanentKey = (int)GameData.Int(game.Row("DrawConfig", permanentDraw)!.Value, "_iType");
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(Session);
            Session.Lottery!.PityRecords[permanentKey] = new LotteryPityRecord { PityCount = 59, MustCount = 60 };
            await Task.CompletedTask;
        });
        await Gm("!item 1250709 2");
        await bot.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = 10009, SlotId = 1 });
        Assert.Equal(0, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        var egg = Saved(bot).IncubationSlot[1].IncubateInfo[10009];
        Assert.Contains(egg.AwardId, GuaranteedItems(permanentDraw));
        var record = Session.Lottery!.PityRecords[permanentKey];
        Assert.Equal((0, 60, 1), (record.PityCount, record.MustCount, record.HitPityCount));
        await bot.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = 10008, SlotId = 1 });
        Assert.Equal(0, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        var limited = Session.Lottery.PityRecords[(int)limitedDraw];
        Assert.Equal(1, limited.TotalCount);
        Assert.Equal(GuaranteedItems(limitedDraw).Contains(Saved(bot).IncubationSlot[1].IncubateInfo[10008].AwardId) ? 0 : 1,
            limited.PityCount);
        Assert.Equal(0, Session.Lottery.PityRecords[permanentKey].PityCount);
    }

    private static long[] Flat(JsonElement row, string key) =>
        row.GetProperty(key).EnumerateArray().Select(v => v.GetInt64()).ToArray();

    [ClientDataFact]
    public async Task HatchingAcceleratesAtomicallyAndCollectsOnlyOnce()
    {
        await using var bot = await InWorld();
        await Unlock(hatch: true);
        var generate = new PetGenEggReq { RoundId = 10001, SlotId = 1 };
        // The fixture removed the quest's hatch materials to exercise refusal without payment.
        await bot.SendGameAsync(PetService.GenerateEgg, generate);
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        await Gm("!item 1250709 2");
        await bot.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = 10001, SlotId = 2 });
        Assert.Equal(ErrorCodes.PetOperation, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        await bot.SendGameAsync(PetService.GenerateEgg, generate);
        Assert.Equal(0, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        var sync = await Next(bot, 5017, DBPetInfoIncrement.Parser, m => m.IncubationSlot.ContainsKey(1));
        var egg = sync.IncubationSlot[1].All.IncubateInfo[10001];
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), egg.IncubateTime);
        Assert.True(egg.AwardId > 0 && egg.AwardCount > 0);
        // A draw can return the same fragments already granted by the unlock quest.
        long previousAwardCount = egg.AwardType == 2 ? Count(bot, (int)egg.AwardId) : 0;
        Assert.Equal(1, Count(bot, 1250709));
        await bot.SendGameAsync(PetService.GenerateEgg, generate);
        Assert.Equal(ErrorCodes.PetOperation, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
        Assert.Equal(1, Count(bot, 1250709));
        _clock.Advance(120);
        Assert.Equal(480, _app.Services.GetRequiredService<PetService>().Remaining(Saved(bot), egg));
        var accelerate = new PetEggAccReq { RoundId = 10001, SlotId = 1 };
        await bot.SendGameAsync(PetService.AccelerateEgg, accelerate);
        Assert.Equal(ErrorCodes.CurrencyShort, (await Next(bot, PetService.AccelerateEgg, PetEggAccRes.Parser)).Result);
        await Gm("!item 1250724 1");
        await bot.SendGameAsync(PetService.AccelerateEgg, accelerate);
        Assert.Equal(ErrorCodes.CurrencyShort, (await Next(bot, PetService.AccelerateEgg, PetEggAccRes.Parser)).Result);
        Assert.Equal(1, Count(bot, 1250724));
        Assert.Equal(0, Saved(bot).IncubationSlot[1].IncubateInfo[10001].AccTime);
        await Gm("!item 3 100");
        await bot.SendGameAsync(PetService.AccelerateEgg, accelerate);
        Assert.Equal(0, (await Next(bot, PetService.AccelerateEgg, PetEggAccRes.Parser)).Result);
        Assert.Equal(0, Count(bot, 1250724));
        Assert.Equal(0, Count(bot, 3));
        Assert.Equal(600, Saved(bot).IncubationSlot[1].IncubateInfo[10001].AccTime);
        await bot.SendGameAsync(PetService.AccelerateEgg, accelerate);
        Assert.Equal(0, (await Next(bot, PetService.AccelerateEgg, PetEggAccRes.Parser)).Result);
        var collect = new PetEggRewardReq { RoundId = 10001, SlotIds = { 1 } };
        await bot.SendGameAsync(PetService.EggReward, collect);
        var reward = await Next(bot, PetService.EggReward, PetEggRewardRes.Parser);
        Assert.Equal(0, reward.Result);
        Assert.Equal(egg.AwardId, Assert.Single(reward.PetItems.Concat(reward.NormalItems)).ItemId);
        if (egg.AwardType == 1) Assert.Contains((int)egg.AwardId, Saved(bot).ListPet.Keys);
        else Assert.Equal(previousAwardCount + egg.AwardCount, Count(bot, (int)egg.AwardId));
        var saved = Saved(bot);
        Assert.Empty(saved.IncubationSlot);
        Assert.Equal(1, saved.IncubationCount);
        // The first hatch fills room 1's 10-proficiency bar and the room levels itself (no client 33316).
        Assert.Equal((2, 0L), (saved.IncubationLv, saved.IncubationExp));
        Assert.Equal(egg.AwardId, Assert.Single(saved.IncubationHistory).ItemId);
        await bot.SendGameAsync(PetService.EggReward, collect);
        Assert.Equal(ErrorCodes.PetOperation, (await Next(bot, PetService.EggReward, PetEggRewardRes.Parser)).Result);
        Assert.Equal(saved, Saved(bot));
    }

    [ClientDataFact]
    public async Task NaturalTimerAndPersistedOutcomeSurviveRelogAndInvalidBatchIsAllOrNothing()
    {
        DBIncubateInfo egg;
        await using (var bot = await InWorld())
        {
            await Unlock(hatch: true);
            await Gm("!item 1250709 1");
            await bot.SendGameAsync(PetService.GenerateEgg, new PetGenEggReq { RoundId = 10001, SlotId = 1 });
            Assert.Equal(0, (await Next(bot, PetService.GenerateEgg, PetGenEggRes.Parser)).Result);
            egg = Saved(bot).IncubationSlot[1].IncubateInfo[10001].Clone();
        }
        _clock.Advance(599);
        await using var again = await InWorld(create: false);
        Assert.Equal(egg, Saved(again).IncubationSlot[1].IncubateInfo[10001]);
        await again.SendGameAsync(PetService.EggReward, new PetEggRewardReq { RoundId = 10001, SlotIds = { 1 } });
        Assert.Equal(ErrorCodes.PetOperation, (await Next(again, PetService.EggReward, PetEggRewardRes.Parser)).Result);
        _clock.Advance(1);
        await again.SendGameAsync(PetService.EggReward, new PetEggRewardReq { RoundId = 10001, SlotIds = { 1, 1 } });
        Assert.Equal(ErrorCodes.Parameter, (await Next(again, PetService.EggReward, PetEggRewardRes.Parser)).Result);
        await again.SendGameAsync(PetService.EggReward, new PetEggRewardReq { RoundId = 10001, SlotIds = { 1, 2 } });
        Assert.Equal(ErrorCodes.PetOperation, (await Next(again, PetService.EggReward, PetEggRewardRes.Parser)).Result);
        Assert.Equal(0, Saved(again).IncubationCount);
        Assert.Single(Saved(again).IncubationSlot);
        await again.SendGameAsync(PetService.EggReward, new PetEggRewardReq { RoundId = 10001, SlotIds = { 1 } });
        Assert.Equal(0, (await Next(again, PetService.EggReward, PetEggRewardRes.Parser)).Result);
        Assert.Equal(0, Count(again, 1250709));
    }

    [ClientDataFact]
    public async Task DuplicatePetEggConvertsToConfiguredFragmentsAndUnprovenPathsRefuse()
    {
        await using var bot = await InWorld();
        await Unlock(hatch: true);
        await CombinePet(bot);
        // A known, already persisted pet outcome isolates the duplicate branch from random draw selection.
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(Session);
            Session.Pets!.IncubationSlot[1] = new DBSlotInfo
            {
                SlotId = 1, IncubateInfo =
                { [10001] = new DBIncubateInfo { IncubateTime = _clock.GetUtcNow().ToUnixTimeSeconds() - 600, AwardType = 1, AwardId = 10001, AwardCount = 1 } }
            };
            Roles.SavePets(RoleId(bot), Session.Pets.ToByteArray());
        });
        await bot.SendGameAsync(PetService.EggReward, new PetEggRewardReq { RoundId = 10001, SlotIds = { 1 } });
        var reward = await Next(bot, PetService.EggReward, PetEggRewardRes.Parser);
        Assert.Equal(0, reward.Result);
        var broken = Assert.Single(reward.BrokenItems);
        // The reply names the duplicate PET (the client resolves its _kDecompose shard); the bag gains the shards.
        Assert.Equal((10001L, 20L), (broken.ItemId, broken.Count));
        Assert.Equal(20, Count(bot, 1610001));
        Assert.Single(Saved(bot).ListPet);
        // The collect already spent the full bar on room 2, so an explicit 33316 has nothing left to buy.
        await bot.SendGameAsync(PetService.RoomLevelUp, new NullArg());
        Assert.Equal(ErrorCodes.PetOperation, (await Next(bot, PetService.RoomLevelUp, PetIncubationLvUpRes.Parser)).Result);
        await bot.SendGameAsync(PetService.Break, new PetBreakReq { PetId = 10001 });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(bot, PetService.Break, PetBreakRes.Parser)).Result);
        Assert.Equal(2, Saved(bot).IncubationLv);
        Assert.Empty(Saved(bot).ListPet[10001].BreakLv);
    }

    [ClientDataFact]
    public async Task FullBarLevelsTheRoomAfterACollectOnlyWhenTheCurrentRowsCostIsAffordable()
    {
        await using var bot = await InWorld();
        await Unlock(hatch: true);
        await Gm("!delitem 1");
        var room = _app.Services.GetRequiredService<GameData>().Row("PetIncubationRoomConfig", 2)!.Value;
        long bar = GameData.Int(room, "_iExp");
        var cost = room.GetProperty("_kCost")[0];
        int coin = cost[0].GetInt32();
        long price = cost[1].GetInt64();
        Assert.True(bar > 10 && price > 0);
        // Room 2 one egg (10 proficiency) short of its bar, with one ready egg in slot 1.
        Task ReadyEgg() => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(Session);
            Session.Pets!.IncubationLv = 2;
            Session.Pets.IncubationExp = bar - 10;
            Session.Pets.IncubationSlot[1] = new DBSlotInfo
            {
                SlotId = 1, IncubateInfo =
                { [10001] = new DBIncubateInfo { IncubateTime = _clock.GetUtcNow().ToUnixTimeSeconds() - 600, AwardType = 2, AwardId = 1610001, AwardCount = 1 } }
            };
            Roles.SavePets(RoleId(bot), Session.Pets.ToByteArray());
        });
        var collect = new PetEggRewardReq { RoundId = 10001, SlotIds = { 1 } };
        await ReadyEgg();
        await bot.SendGameAsync(PetService.EggReward, collect);
        Assert.Equal(0, (await Next(bot, PetService.EggReward, PetEggRewardRes.Parser)).Result);
        // Short of the cost: the bar stays full and nothing is spent.
        Assert.Equal((2L, bar), (Saved(bot).IncubationLv, Saved(bot).IncubationExp));
        await Gm($"!item {coin} {price}");
        await bot.SendGameAsync(PetService.RoomLevelUp, new NullArg());
        Assert.Equal(0, (await Next(bot, PetService.RoomLevelUp, PetIncubationLvUpRes.Parser)).Result);
        Assert.Equal((3L, 0L, 0L), (Saved(bot).IncubationLv, Saved(bot).IncubationExp, Count(bot, coin)));
        await ReadyEgg();
        await Gm($"!item {coin} {price}");
        await bot.SendGameAsync(PetService.EggReward, collect);
        Assert.Equal(0, (await Next(bot, PetService.EggReward, PetEggRewardRes.Parser)).Result);
        Assert.Equal((3L, 0L, 0L), (Saved(bot).IncubationLv, Saved(bot).IncubationExp, Count(bot, coin)));
    }

    [ClientDataFact]
    public async Task FastHatchRequiresRoomTwoAndAtomicallyConsumesMaterialsAndAccelerators()
    {
        await using var bot = await InWorld();
        await Unlock(hatch: true);
        await bot.SendGameAsync(PetService.FastEgg, new FastIncubateReq { RoundId = 10001 });
        Assert.Equal(ErrorCodes.PetOperation, (await Next(bot, PetService.FastEgg, FastIncubateRes.Parser)).Result);
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(Session);
            Session.Pets!.IncubationExp = 10;
            Roles.SavePets(RoleId(bot), Session.Pets.ToByteArray());
        });
        await bot.SendGameAsync(PetService.RoomLevelUp, new NullArg());
        Assert.Equal(0, (await Next(bot, PetService.RoomLevelUp, PetIncubationLvUpRes.Parser)).Result);
        Assert.Equal(2, Saved(bot).IncubationLv);
        Assert.Equal(0, Saved(bot).IncubationExp);
        await Gm("!item 1250709 5");
        await Gm("!item 1250724 10");
        long material = Count(bot, 1250709), accelerator = Count(bot, 1250724);
        await bot.SendGameAsync(PetService.FastEgg, new FastIncubateReq { RoundId = 10001 });
        var reply = await Next(bot, PetService.FastEgg, FastIncubateRes.Parser);
        Assert.Equal(0, reply.Result);
        Assert.Equal(5, reply.PetItems.Count + reply.NormalItems.Count + reply.BrokenItems.Count);
        Assert.Equal(material - 5, Count(bot, 1250709));
        Assert.Equal(accelerator - 10, Count(bot, 1250724));
        Assert.Equal(5, Saved(bot).IncubationCount);
        Assert.Equal(5, Saved(bot).IncubationHistory.Count);
        Assert.Empty(Saved(bot).IncubationSlot);
        var before = Saved(bot).ToByteArray();
        await bot.SendGameAsync(PetService.FastEgg, new FastIncubateReq { RoundId = 10001 });
        Assert.NotEqual(0, (await Next(bot, PetService.FastEgg, FastIncubateRes.Parser)).Result);
        Assert.Equal(before, Saved(bot).ToByteArray());
    }

    [ClientDataFact]
    public async Task SupportPlansCollectionAndFragmentBreakdownPersistWithoutAnActor()
    {
        await using var bot = await InWorld();
        await Unlock();
        await CombinePet(bot);
        await Gm("!lvl 40");
        await bot.SendGameAsync(PetService.StateUpdate, new PetStateUpdateReq { PetId = 10001, Target = (PetStateSource_Type)2, TargetSlotIndex = 1 });
        Assert.Equal(0, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        Assert.Null(Session.PetActor);
        await bot.SendGameAsync(PetService.ChangePlanName, new PetChangePlanNameReq { TargetIndex = 0, Name = "Support" });
        Assert.Equal(0, (await Next(bot, PetService.ChangePlanName, PetChangePlanNameResp.Parser)).Result);
        Assert.Equal("Support", Saved(bot).SetupPlans[0].PlanName);
        await bot.SendGameAsync(PetService.SwitchPlan, new PetSwitchPlanReq { TargetIndex = 5 });
        Assert.Equal(ErrorCodes.PetPlan, (await Next(bot, PetService.SwitchPlan, PetSwitchPlanResp.Parser)).Result);
        Assert.Equal(10001, Saved(bot).SetupPlans[0].SupportPets[1]);
        long zeny = Count(bot, 1);
        await bot.SendGameAsync(PetService.CollectActive, new PetCollectActiveReq { PetId = 10001 });
        Assert.Equal(0, (await Next(bot, PetService.CollectActive, PetCollectActiveRes.Parser)).Result);
        Assert.True(Saved(bot).ListPet[10001].IsActiveCollect);
        Assert.Equal(zeny + 1000, Count(bot, 1));
        await bot.SendGameAsync(PetService.CollectActive, new PetCollectActiveReq { PetId = 10001 });
        Assert.Equal(ErrorCodes.AlreadyUnlocked, (await Next(bot, PetService.CollectActive, PetCollectActiveRes.Parser)).Result);
        Assert.Equal(zeny + 1000, Count(bot, 1));
        await Gm("!item 1610001 3");
        await bot.SendGameAsync(PetService.FragmentsBroken, new PetFragmentsBrokenReq { Fragments = { new PetFragment { FragmentId = 1610001, Count = 2 } } });
        Assert.Equal(0, (await Next(bot, PetService.FragmentsBroken, PetFragmentsBrokenResp.Parser)).Result);
        Assert.Equal(1, Count(bot, 1610001));
        Assert.Equal(10, Count(bot, 1250710));
        await bot.SendGameAsync(PetService.FragmentsBroken, new PetFragmentsBrokenReq { Fragments = { new PetFragment { FragmentId = 1610001, Count = -1 } } });
        Assert.Equal(ErrorCodes.Parameter, (await Next(bot, PetService.FragmentsBroken, PetFragmentsBrokenResp.Parser)).Result);
        Assert.Equal(1, Count(bot, 1610001));
    }

    [ClientDataFact]
    public async Task PersistedSetupCannotSpawnWithTheQuestGateClosed()
    {
        await using (var bot = await InWorld())
        {
            await Unlock();
            await CombinePet(bot);
            await Gm("!lvl 33");
            await bot.SendGameAsync(PetService.StateUpdate, new PetStateUpdateReq { PetId = 10001, Target = (PetStateSource_Type)1, TargetSlotIndex = 1 });
            Assert.Equal(0, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
            await Gm("!quest reset");
        }
        await using var again = await InWorld(create: false);
        await Next(again, 5017, DBPetInfoIncrement.Parser);
        Assert.Equal(10001, Saved(again).SetupPlans[0].SetUpPets[1]);
        Assert.False(_app.Services.GetRequiredService<FunctionService>().IsOpen(Session, 33));
        Assert.Null(Session.PetActor);
        Assert.DoesNotContain(again.Inbox.Snapshot(), e => e.IsGame(31037) && AoIViewChangeData.Parser.ParseFrom(e.Body).Bases.Any(b => b.EntityType == 7));
    }

    [ClientDataFact]
    public async Task SqliteFailureRollsBackBagCurrencyAndPetStateTogether()
    {
        await using var bot = await InWorld();
        await Unlock();
        await Gm("!item 1610001 20");
        var state = Saved(bot);
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER fail_pet BEFORE UPDATE OF pets ON role BEGIN SELECT RAISE(ABORT, 'test pet failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(Session);
            var next = Session.Pets!.Clone();
            next.ListPet[10001] = new PetData { PetId = 10001, Star = 1, Level = 1 };
            await _app.Services.GetRequiredService<CharacterService>().ExchangeAsync(Session, [(1610001, 20L), (1, 10L)], [(1250701, 1)], next);
        }));
        Assert.Equal(state, Saved(bot));
        Assert.Equal(state, Session.Pets);
        Assert.Equal(20, Count(bot, 1610001));
        Assert.Equal(20, _app.Services.GetRequiredService<CharacterService>().CountOf(Session, 1610001));
        Assert.Equal(0, Count(bot, 1250701));
        Assert.Equal(Roles.LoadProgress(RoleId(bot)).Currency[1], Session.Progress!.Currency[1]);
    }

    /// <summary>
    /// The FirstHeartScoreConfig row 6 box 1250715 [[204,4,30004,31005,32001,33004]] (ActionConfig 204 SelfChoosePet):
    /// UI_Com_RewardSelectWidgetCtrl sends 31323 with selfChoosePet.choosePet {option index: pet}. The pick joins the
    /// roster with 33313 PetChooseNtf; an owned pick turns into its _kDecompose shards; a pet the box does not offer,
    /// or no pick, keeps the box.
    /// </summary>
    [ClientDataFact]
    public async Task SelfChoosePetBoxGrantsThePickedPetOrItsShards()
    {
        await using var bot = await InWorld();
        await Gm("!item 1250715 2");
        long Box() => _app.Services.GetRequiredService<ItemStore>().Load(RoleId(bot)).Single(i => i.Entry == 1250715 && i.Grid == 0).Guid;
        async Task<int> Use(params (int Index, int Pet)[] picks)
        {
            var param = new ItemUseParamInfo { SelfChoosePet = new ItemUseParamInfo.Types.SelfChoosePet() };
            foreach (var (index, pet) in picks) param.SelfChoosePet.ChoosePet[index] = pet;
            await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = Box(), ItemCount = 1, ItemParam = { param } });
            return (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result;
        }

        Assert.Equal(ErrorCodes.Parameter, await Use((0, 31005)));          // option 0 is 30004
        Assert.Equal(ErrorCodes.Parameter, await Use());                     // no pick
        Assert.Equal(ErrorCodes.Parameter, await Use((1, 31005), (2, 32001))); // one box, one pet
        Assert.Equal(2, Count(bot, 1250715));

        Assert.Equal(0, await Use((1, 31005)));
        Assert.Equal(31005, (await Next(bot, PetService.SelfChoose, PetChooseNtf.Parser)).PetId);
        await Next(bot, PetService.PetSync, DBPetInfoIncrement.Parser, m => m.ListPet.ContainsKey(31005));
        Assert.True(Saved(bot).ListPet.ContainsKey(31005));
        Assert.Equal(1, Count(bot, 1250715));
        Assert.Equal(0, Count(bot, 1631005));

        Assert.Equal(0, await Use((1, 31005)));
        Assert.Equal(31005, (await Next(bot, PetService.SelfChoose, PetChooseNtf.Parser)).PetId);
        Assert.Equal(0, Count(bot, 1250715));
        Assert.Equal(50, Count(bot, 1631005)); // PetBaseConfig 31005 _kDecompose [1631005,50]
        Assert.Equal(Saved(bot), Session.Pets);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
