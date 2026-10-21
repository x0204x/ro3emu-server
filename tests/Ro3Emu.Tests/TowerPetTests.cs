using System.Net;
using System.Net.Sockets;
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

/// <summary>Wire formation, persisted 5065, type-7 allies, combat and death substitution.</summary>
public sealed class TowerPetTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-tower-pets-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        Service<SeasonService>().SetPlayBook(101);
        SureHitRolls.Install(_app); // lethal hits are prepared; a FLEE miss would only be noise
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("towerpet", "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync("Towerpet")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        if (create)
        {
            var roster = new DBPetInfo { CurrentPlan = 0, Inited = true, IncubationLv = 1, OnOpenUIRoomLv = 1 };
            foreach (int petId in new[] { 10001, 11001, 12001, 13001 })
                roster.ListPet[petId] = new PetData { PetId = petId, Level = 1, Star = 1, StarProgress = 0 };
            Service<RoleStore>().SavePets(id, roster.ToByteArray());
        }
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
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account=towerpet", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private async Task Eligible(int level = 99)
    {
        await Gm($"!lvl {level}");
        await Gm("!quest done 10105020");
        await Gm("!quest done 10104330");
    }

    private async Task<SetClimTowerPetsRes> Set(BotClient bot, params int[] ids)
    {
        var req = new SetClimTowerPetsReq(); req.Pets.Add(ids);
        await bot.SendGameAsync(TowerService.SetPets, req);
        return await Next(bot, TowerService.SetPets, SetClimTowerPetsRes.Parser);
    }

    [ClientDataFact]
    public async Task FormationRejectsInvalidRequestsAndPersistsFourSlots()
    {
        await using (var bot = await InWorld())
        {
            Assert.Equal(ErrorCodes.FeatureUnavailable, (await Set(bot, 10001, 0, 0, 0)).Result);
            await Eligible(33);
            Assert.Equal(ErrorCodes.Parameter, (await Set(bot, 10001)).Result);
            Assert.Equal(ErrorCodes.Parameter, (await Set(bot, 10001, 0, 0, 0, 0)).Result);
            Assert.Equal(ErrorCodes.PetNoConfig, (await Set(bot, -7, 0, 0, 0)).Result);
            Assert.Equal(ErrorCodes.PetAlreadyAssigned, (await Set(bot, 10001, 10001, 0, 0)).Result);
            Assert.Equal(ErrorCodes.PetNotOwned, (await Set(bot, 20001, 0, 0, 0)).Result);
            Assert.Equal(ErrorCodes.PetSetupSlot, (await Set(bot, 10001, 11001, 13001, 0)).Result);
            var ok = await Set(bot, 10001, 11001, 0, 0);
            Assert.Equal(0, ok.Result);
            Assert.Equal(new[] { 10001, 11001, 0, 0 }, ok.Pets);
            var sync = await Next(bot, TowerService.TowerSync, DBClimTowerIncrement.Parser,
                t => t.PetIds?.All?.Value.SequenceEqual(new[] { 10001, 11001, 0, 0 }) == true);
            Assert.False(sync.SyncAllMark);
            await Gm("!lvl 44");
            Assert.Equal(ErrorCodes.PetSetupSlot, (await Set(bot, 10001, 11001, 13001, 0)).Result);
            await Gm("!lvl 45");
            Assert.Equal(0, (await Set(bot, 10001, 11001, 13001, 0)).Result);
            await Gm("!lvl 49");
            var locked = await Set(bot, 10001, 11001, 13001, 12001);
            Assert.Equal(ErrorCodes.PetSetupSlot, locked.Result);
            Assert.Equal(new[] { 10001, 11001, 13001, 0 }, locked.Pets);
            await Gm("!lvl 50");
            // The battle panel's reorder and remove actions use the same full four-position request.
            Assert.Equal(0, (await Set(bot, 11001, 10001, 13001, 12001)).Result);
            Assert.Equal(0, (await Set(bot, 11001, 0, 13001, 12001)).Result);
            Assert.Equal(0, (await Set(bot, 10001, 11001, 13001, 12001)).Result);
        }
        await using var relog = await InWorld(create: false);
        var full = await Next(relog, TowerService.TowerSync, DBClimTowerIncrement.Parser);
        Assert.Equal(new[] { 10001, 11001, 13001, 12001 }, full.PetIds.All.Value);
        await relog.SendGameAsync(TowerService.Info, new ClimTowerInfoReq());
        var info = await Next(relog, TowerService.Info, ClimTowerInfoRsp.Parser);
        Assert.Equal(new[] { 10001, 11001, 13001, 12001 }, info.PetIds);
    }

    [ClientDataFact]
    public async Task TwoAlliesCastConfiguredSkillsAndKillTowerMonsters()
    {
        await using var bot = await InWorld();
        await Eligible();
        Assert.Equal(0, (await Set(bot, 10001, 11001, 13001, 0)).Result);
        await Gm("!lvl 33");
        await bot.SendGameAsync(TowerService.Start, new ClimTowerStartReq { Layer = 1 });
        Assert.Equal(ErrorCodes.PetSetupSlot, (await Next(bot, TowerService.Start, ClimTowerStartRes.Parser)).Result);
        await Gm("!lvl 99");
        var a = await Enter(bot);
        var refusedSwap = await Set(bot, 11001, 10001, 13001, 0);
        Assert.Equal(ErrorCodes.PetOperation, refusedSwap.Result);
        Assert.Equal(new[] { 10001, 11001, 13001, 0 }, refusedSwap.Pets);
        Assert.Equal(2, a.Pets.Count);
        var petUids = a.Pets.Select(p => p.Uid).ToHashSet();
        await Service<GameLoop>().InvokeAsync(() =>
        {
            Assert.Equal(2, a.Monsters.Count);
            // Keep both sides alive until each actor demonstrates its own configured attack.
            foreach (var monster in a.Monsters) monster.Hp = 1_000_000;
            foreach (var pet in a.Pets) pet.Hp = 1_000_000;
            return Task.CompletedTask;
        });
        foreach (var pet in a.Pets.ToArray())
        {
            var cast = await Next(bot, CombatService.UseInSkill, UseInSkillInfo.Parser,
                use => use.SrcID == pet.Uid && use.PetConfigId == pet.PetId
                    && pet.AttackSkills.Any(skill => skill.Id == use.SkillID));
            Assert.Equal(pet.PetId == 10001 ? 2353001 : 2356501, cast.SkillID);
            await Next(bot, 31081, MovementChangeNtf.Parser,
                move => move.Uid == pet.Uid && move.MovementInfo.MoveType == MovementType.MoveTypeNone);
            await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
                hit => hit.SrcID == pet.Uid && hit.Dsts.Any(d => d.Damage > 0 && a.Monsters.Any(m => m.Uid == d.DstID)));
        }
        await Service<GameLoop>().InvokeAsync(() =>
        {
            foreach (var monster in a.Monsters) monster.Hp = 1;
            return Task.CompletedTask;
        });
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => petUids.Contains(h.SrcID) && h.Dsts.Any(d => d.IsDead));
        Assert.Contains(hit.Dsts, d => d.IsDead && a.Monsters.Any(m => m.Uid == d.DstID));
        var end = await Next(bot, TowerService.End, ClimTowerEndNotify.Parser);
        Assert.True(end.ClimTowerResult);
        Assert.Equal(1, end.PassedMaxLayerConfID);
    }

    [ClientDataFact]
    public async Task OrdinaryFollowerIsReplacedByTowerLineupAndReturnsOnExit()
    {
        await using var bot = await InWorld();
        await Eligible();
        var setup = new PetStateUpdateReq { PetId = 10001, Target = (PetStateSource_Type)1, TargetSlotIndex = 1 };
        await bot.SendGameAsync(PetService.StateUpdate, setup);
        Assert.Equal(0, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        var before = await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(e => e.EntityType == 7 && e.EntityTemplateID == 2300440));
        ulong followerUid = before.Bases.Single(e => e.EntityType == 7).Uuid;
        Assert.Equal(0, (await Set(bot, 10001, 11001, 0, 0)).Result);
        var a = await Enter(bot);
        var session = Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
        Assert.Null(session.PetActor);
        Assert.Equal(10001, session.Pets!.SetupPlans[0].SetUpPets[1]);
        Assert.DoesNotContain(a.Pets, p => (ulong)p.Uid == followerUid);

        // Even a normal setup edit during the attempt cannot create a third passive actor.
        setup.TargetSlotIndex = 2;
        setup.PetId = 13001;
        await bot.SendGameAsync(PetService.StateUpdate, setup);
        Assert.Equal(0, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser)).Result);
        Assert.Null(session.PetActor);
        Assert.Equal(2, a.Pets.Count);

        var towerPetUids = a.Pets.Select(p => (ulong)p.Uid).ToArray();
        int exitEventStart = bot.Inbox.Snapshot().Count;
        await bot.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(bot, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        AssertPetDespawnBeforeSwitch(bot, exitEventStart, towerPetUids);
        Assert.True(a.Room.Closed);
        Assert.Null(Service<InstanceService>().Find(a.InstanceId));
        Assert.Empty(a.Room.ActiveMembers);
        Assert.Empty(a.Room.Monsters);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        var after = await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(e => e.EntityType == 7 && e.EntityTemplateID == 2300440));
        Assert.Single(after.Bases, e => e.EntityType == 7);
        Assert.Null(session.TowerAttempt);
        Assert.Empty(a.Pets);
        Assert.Equal(10001, session.PetActor!.PetId);
        Assert.Equal(10001, session.Pets.SetupPlans[0].SetUpPets[1]);
    }

    [ClientDataFact]
    public async Task EnemyDeathsDeployBothSubstitutesWithoutChangingSavedFormation()
    {
        await using var bot = await InWorld();
        await Eligible();
        Assert.Equal(0, (await Set(bot, 10001, 11001, 13001, 12001)).Result);
        var a = await Enter(bot);
        TowerPetUnit first = a.Pets.Single(p => p.PetId == 10001);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            var monster = a.Monsters[0];
            foreach (var enemy in a.Monsters) enemy.Hp = 1_000_000; // keep the floor alive through both replacements
            first.Hp = 1;
            first.X = monster.X + 1f; first.Z = monster.Z;
            Service<World>().Map(first.WorldScene).Moved(first);
            Service<MonsterService>().Engage(monster, first);
            return Task.CompletedTask;
        });
        var cast = await Next(bot, CombatService.UseInSkill, UseInSkillInfo.Parser,
            use => use.TargetId == first.Uid && a.Monsters.Any(m => m.Uid == use.SrcID));
        Assert.Equal(first.Uid, cast.TargetId);
        var death = await Next(bot, TowerService.PetDeadState, ClimbTowerSyncPetDeadStateRes.Parser,
            state => state.PetDeadState.GetValueOrDefault(10001) == 1);
        Assert.Equal(1, death.PetDeadState[10001]);
        var substitute = await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            view => view.Bases.Any(e => e.EntityType == 7 && e.EntityTemplateID == 2300130));
        Assert.Contains(substitute.Bases, e => e.EntityType == 7 && e.Owner == (long)bot.Self!.Uuid);
        Assert.All(substitute.Bases.Where(e => e.EntityType == 7), e => Assert.Equal(1, e.EntityCamp));
        Assert.Equal(new[] { 11001, 13001 }, a.Pets.Select(p => p.PetId).Order());
        await Service<GameLoop>().InvokeAsync(() =>
        {
            var second = a.Pets.Single(p => p.PetId == 11001);
            var monster = a.Monsters[0];
            second.Hp = 1;
            second.X = monster.X + 1f; second.Z = monster.Z;
            Service<World>().Map(second.WorldScene).Moved(second);
            Service<MonsterService>().Engage(monster, second);
            return Task.CompletedTask;
        });
        await Next(bot, TowerService.PetDeadState, ClimbTowerSyncPetDeadStateRes.Parser,
            state => state.PetDeadState.GetValueOrDefault(11001) == 1);
        await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            view => view.Bases.Any(e => e.EntityType == 7 && e.EntityTemplateID == 2300150 && e.EntityCamp == 1));
        Assert.Equal(new[] { 12001, 13001 }, a.Pets.Select(p => p.PetId).Order());
        Assert.Equal(new[] { 10001, 11001, 13001, 12001 }, Service<RoleStore>().LoadTower((long)bot.Self!.Uuid) is { } saved
            ? DBClimTower.Parser.ParseFrom(saved).PetIds.ToArray() : []);
        long oldInstance = a.InstanceId;
        var towerPetUids = a.Pets.Select(p => (ulong)p.Uid).ToArray();
        int exitEventStart = bot.Inbox.Snapshot().Count;
        await bot.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
        Assert.Equal(0, (await Next(bot, TowerService.LeaveDungeon, LeaveDungeonRsp.Parser)).Result);
        var exit = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        AssertPetDespawnBeforeSwitch(bot, exitEventStart, towerPetUids);
        Assert.True(a.Room.Closed);
        Assert.Null(Service<InstanceService>().Find(oldInstance));
        Assert.Empty(a.Room.ActiveMembers);
        Assert.Empty(a.Room.Monsters);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = exit.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        Assert.Empty(a.Pets);
        var next = await Enter(bot); // also requires an empty 32809 to clear both previous deaths
        Assert.NotEqual(oldInstance, next.InstanceId);
        Assert.Empty(next.DeadPetIds);
        Assert.Equal(new[] { 10001, 11001 }, next.Pets.Select(p => p.PetId).Order());
        Assert.All(next.Pets, p => Assert.True(p.Hp > 0));
    }

    [ClientDataTheory]
    [InlineData(10000, 200)]
    [InlineData(9000, 180)]
    public async Task TowerDifficultyScalesEnemyDamageAgainstPets(int ratio, long expectedDamage)
    {
        await using var bot = await InWorld();
        await Eligible();
        Assert.Equal(0, (await Set(bot, 10001, 11001, 0, 0)).Result);
        var a = await Enter(bot);
        var pet = a.Pets.Single(p => p.PetId == 10001);
        long attackerId = (1L << 49) + ratio;
        MonsterUnit? attacker = null;
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            var source = a.Monsters[0];
            // Controlled combat fixture: stop ordinary AI and use a fixed 200-damage fallback attack,
            // leaving the production enemy-to-pet callback and its wire publication unchanged.
            foreach (var enemy in a.Monsters) enemy.Dead = true;
            pet.Hp = 1_000_000;
            attacker = new MonsterUnit
            {
                Uid = attackerId, SceneEntry = pet.SceneEntry, InstanceId = a.InstanceId,
                X = pet.X, Y = pet.Y, Z = pet.Z, Hp = 1_000_000, DamageRatio = ratio,
                Template = source.Template with { NormalSkill = 0, Atk = 200, HitPointSeconds = 0 },
                Group = source.Group, Home = source.Home,
            };
            await Service<World>().AddUnitAsync(attacker);
            await Service<MonsterService>().AttackPet!(attacker, pet);
        });
        try
        {
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.SrcID == attackerId && h.Dsts.Any(d => d.DstID == pet.Uid));
            Assert.Equal(expectedDamage, Assert.Single(hit.Dsts).Damage);
            Assert.Equal(1_000_000 - expectedDamage, pet.Hp);
            Assert.False(pet.Dead);
        }
        finally { await Service<GameLoop>().InvokeAsync(() => Service<World>().RemoveUnitAsync(attacker!)); }
    }

    private async Task<TowerService.Attempt> Enter(BotClient bot)
    {
        await bot.SendGameAsync(TowerService.Start, new ClimTowerStartReq { Layer = 1 });
        Assert.Equal(0, (await Next(bot, TowerService.Start, ClimTowerStartRes.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.PlayerAsset, new PlayerAssetReq());
        await Next(bot, MsgId.PlayerAsset, PlayerAssetResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        await Next(bot, TowerService.DungeonInfo, DungeonBaseInfoNotify.Parser);
        var reset = await Next(bot, TowerService.PetDeadState, ClimbTowerSyncPetDeadStateRes.Parser);
        Assert.Empty(reset.PetDeadState);
        var session = Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
        Assert.NotNull(session.TowerAttempt);
        // World.Join sends all already spawned units in one AOI batch.
        var pets = await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Count(e => e.EntityType == 7) == 2);
        Assert.Equal(new[] { 2300160UL, 2300440UL }, pets.Bases.Where(e => e.EntityType == 7)
            .Select(e => e.EntityTemplateID).Order());
        Assert.All(pets.Bases.Where(e => e.EntityType == 7), e =>
        {
            Assert.Equal((long)bot.Self!.Uuid, e.Owner);
            Assert.Equal(1, e.EntityCamp);
            Assert.True(e.Uuid < (1UL << 53));
            Assert.Equal(e.Uuid, (ulong)(double)e.Uuid); // identity must survive Lua number conversion
        });
        Assert.Contains(pets.Bases, e => e.EntityType == 1 && e.BornTimeMs > 0);
        return session.TowerAttempt!;
    }

    private static void AssertPetDespawnBeforeSwitch(BotClient bot, int start, IReadOnlyCollection<ulong> petUids)
    {
        var exitEvents = bot.Inbox.Snapshot().Skip(start).ToArray();
        int switchIndex = Array.FindIndex(exitEvents, e => e.IsGame(MsgId.SwitchSceneLine));
        Assert.True(switchIndex >= 0);
        var removed = exitEvents.Take(switchIndex)
            .Where(e => e.IsGame(GameAoiSink.SyncAoiView))
            .SelectMany(e => AoIViewChangeData.Parser.ParseFrom(e.Body).LeaveAoi)
            .ToHashSet();
        Assert.All(petUids, uid => Assert.Contains(uid, removed));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
