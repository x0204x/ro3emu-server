using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Actual tower start/AOI/cast/hit/buff wire regression for the supplied floor-2 Bobo case.</summary>
public sealed class TowerPetCombatTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-pet-combat-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    /// <summary>Uids for pets this test spawns itself, far above the ones TowerService numbers.</summary>
    private long _petUid = TowerPetUnit.UidBase + (1L << 40);
    private long NextPetUid() => ++_petUid;
    private Task Run(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        Service<SeasonService>().SetPlayBook(101);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InTower()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("petcombat", "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("Petcombat")).RoleBaseInfo.Id;
        var roster = new DBPetInfo { Inited = true, IncubationLv = 1, OnOpenUIRoomLv = 1 };
        foreach (int petId in new[] { 13001, 10001, 11001, 21002 })
            roster.ListPet[petId] = new PetData { PetId = petId, Level = 12, Star = 1 };
        Service<RoleStore>().SavePets(id, roster.ToByteArray());
        Service<RoleStore>().SaveTower(id, new DBClimTower
        {
            LayerConfID = 1, IsNewbeeInit = true,
            ChallengeTime = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds()
        }.ToByteArray());
        await bot.EnterWorldAsync(id);
        using var client = new HttpClient();
        foreach (string command in new[] { "!lvl 99", "!quest done 10105020", "!quest done 10104330" })
            (await client.PostAsync($"{_http}/ro3emu/gm?account=petcombat", new StringContent(command))).EnsureSuccessStatusCode();
        await bot.SendGameAsync(TowerService.SetPets, new SetClimTowerPetsReq { Pets = { 13001, 0, 0, 0 } });
        Assert.Equal(0, (await Next(bot, TowerService.SetPets, SetClimTowerPetsRes.Parser)).Result);
        await bot.SendGameAsync(TowerService.Start, new ClimTowerStartReq { Layer = 2 });
        Assert.Equal(0, (await Next(bot, TowerService.Start, ClimTowerStartRes.Parser)).Result);
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await bot.SendGameAsync(MsgId.PlayerAsset, new PlayerAssetReq());
        await Next(bot, MsgId.PlayerAsset, PlayerAssetResp.Parser);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = sw.SceneEntry });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        await Next(bot, TowerService.DungeonInfo, DungeonBaseInfoNotify.Parser);
        return bot;
    }

    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "petcombat" && s.Role != null);

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    [ClientDataFact]
    public async Task BoboFloorTwoSpawnsBattleStatsCastsShieldAndBasicAttack()
    {
        await using var bot = await InTower();
        var aoi = await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(e => e.EntityTemplateID == 2300130));
        var entity = Assert.Single(aoi.Bases, e => e.EntityTemplateID == 2300130);
        foreach (var (id, value) in new (long, long)[] { (101, 3363), (103, 218), (104, 218), (105, 240), (106, 240), (2002, 12) })
            Assert.Equal(value, entity.Property.Single(p => p.Idx == id).Data);
        var pet = Assert.Single(Session.TowerAttempt!.Pets);
        await Next(bot, CombatService.UseInSkill, UseInSkillInfo.Parser, c => c.SrcID == pet.Uid && c.SkillID == 2354301);
        var shield = await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            b => b.UnitId == pet.Uid && b.Buffs.Any(x => x.ConfigId == 2354401));
        Assert.Equal(3000, Assert.Single(shield.Buffs).Duration);
        Assert.Equal(pet.Uid, shield.Buffs[0].SourceId);
        await Next(bot, CharacterService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.EntityUuid == (ulong)pet.Uid && x.Property.Any(p => p.Idx == 2003 && p.Data == 218));
        var attack = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == pet.Uid && h.SkillId == 2354401 && h.Dsts.Any(d => d.Damage > 0));
        Assert.All(attack.Dsts, d => Assert.InRange(d.Damage, 100, 300));
        Assert.DoesNotContain(bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SrcID == pet.Uid && h.SkillId == 2354301);
    }

    [ClientDataFact]
    public async Task ShieldHasFiniteBudgetAllyLimitRangeCooldownAndNoEnemyDamage()
    {
        await using var bot = await InTower();
        var s = Session; var a = s.TowerAttempt!;
        var added = new List<TowerPetUnit>();
        MonsterUnit attacker = null!;
        await Run(async () =>
        {
            foreach (var old in a.Pets.ToArray()) await Service<World>().RemoveUnitAsync(old);
            a.Pets.Clear();
            foreach (var monster in a.Monsters) monster.Dead = true;
            for (int i = 0; i < 7; i++)
            {
                var pet = TowerPetUnit.Create(Service<GameData>(), s.Pets!, s, Service<World>(), Service<MonsterService>(),
                    Service<CombatService>(), NextPetUid(), 13001, a.InstanceId, s.Position.X + (i == 6 ? 9 : i),
                    s.Position.Y, s.Position.Z)!;
                added.Add(pet); a.Pets.Add(pet); await Service<World>().AddUnitAsync(pet);
                // Only the caster may auto-cast; reserve other actors' cooldown before the controlled cast.
                if (i > 0) Assert.True(pet.TryBeginCast(pet.SupportSkills.Single(), MonsterService.NowMs));
            }
            var source = a.Monsters[0];
            attacker = new MonsterUnit
            {
                Uid = 1L << 49, SceneEntry = s.SceneEntry, InstanceId = a.InstanceId,
                X = added[0].X, Y = added[0].Y, Z = added[0].Z, Hp = 1000000,
                Template = source.Template with { NormalSkill = 0, Atk = 200, HitPointSeconds = 0 },
                Group = source.Group, Home = source.Home
            };
            await Service<World>().AddUnitAsync(attacker);
            await Service<CombatService>().TowerPetSupportAsync(s, added[0], 2354301);
            await Service<CombatService>().TowerPetSupportAsync(s, added[0], 2354301); // rejected by own cooldown/action
        });
        var caster = added[0];
        await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            b => b.UnitId == added[3].Uid && b.Buffs.Any(x => x.SourceId == caster.Uid));
        await Run(() =>
        {
            Assert.Equal(218, Service<BuffService>().ShieldAmount(s));
            Assert.All(added.Take(4), p => Assert.Equal(218, Service<BuffService>().ShieldAmount(p)));
            Assert.All(added.Skip(4), p => Assert.Equal(0, Service<BuffService>().ShieldAmount(p)));
            return Task.CompletedTask;
        });
        long hp = caster.Hp;
        await Run(() => Service<MonsterService>().AttackPet!(attacker, caster));
        var first = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == attacker.Uid);
        Assert.Equal(0, first.Dsts[0].Damage); Assert.Equal(200, first.Dsts[0].SheildDamage);
        Assert.Equal(hp, caster.Hp);
        await Run(() => Service<MonsterService>().AttackPet!(attacker, caster));
        var second = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == attacker.Uid && h.Dsts.Any(d => d.SheildDamage == 18));
        Assert.Equal(182, second.Dsts[0].Damage); Assert.Equal(hp - 182, caster.Hp);
        Assert.Equal(0, Service<BuffService>().ShieldAmount(caster));
        // Review regression: drive the same shield through the actual monster -> player commit path,
        // not just BuffService.AbsorbAsync. Both the wire absorption field and HP must agree.
        long ownerHp = Service<CharacterService>().Stats(s)[2000];
        await Run(() => Service<MonsterService>().Attack!(attacker, s));
        var ownerFirst = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == attacker.Uid && h.Dsts.Any(d => d.DstID == s.Role!.Id));
        Assert.Equal(0, ownerFirst.Dsts[0].Damage); Assert.Equal(200, ownerFirst.Dsts[0].SheildDamage);
        Assert.Equal(ownerHp, Service<CharacterService>().Stats(s)[2000]);
        await Next(bot, CharacterService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.EntityUuid == (ulong)s.Role!.Id && x.Property.Any(p => p.Idx == 2003 && p.Data == 18));
        await Run(() => Service<MonsterService>().Attack!(attacker, s));
        var ownerSecond = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == attacker.Uid && h.Dsts.Any(d => d.DstID == s.Role!.Id && d.SheildDamage == 18));
        Assert.Equal(182, ownerSecond.Dsts[0].Damage);
        Assert.Equal(ownerHp - 182, Service<CharacterService>().Stats(s)[2000]);
        Assert.Equal(0, Service<CharacterService>().Stats(s)[2003]);
        Assert.Single(bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.UseInSkill))
            .Select(e => UseInSkillInfo.Parser.ParseFrom(e.Body)), c => c.SrcID == caster.Uid && c.SkillID == 2354301);
    }

    [ClientDataFact]
    public async Task ChocoSupportChangesAllyAttackOnTheWireAndExpiresWithItsBuff()
    {
        await using var bot = await InTower();
        var s = Session; var a = s.TowerAttempt!;
        TowerPetUnit choco = null!, bobo = null!;
        await Run(async () =>
        {
            foreach (var old in a.Pets.ToArray()) await Service<World>().RemoveUnitAsync(old);
            a.Pets.Clear(); foreach (var monster in a.Monsters) monster.Dead = true;
            foreach (int id in new[] { 10001, 13001 })
            {
                var unit = TowerPetUnit.Create(Service<GameData>(), s.Pets!, s, Service<World>(), Service<MonsterService>(),
                    Service<CombatService>(), NextPetUid(), id, a.InstanceId, s.Position.X, s.Position.Y, s.Position.Z)!;
                a.Pets.Add(unit); await Service<World>().AddUnitAsync(unit);
                if (id == 10001) choco = unit; else bobo = unit;
            }
            await Service<CombatService>().TowerPetSupportAsync(s, choco, 2353101);
        });
        await Next(bot, CharacterService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.EntityUuid == (ulong)bobo.Uid && x.Property.Any(p => p.Idx == 103 && p.Data == 226));
        Assert.Equal(226, bobo.Atk); // ceil(218 * 1.035), BuffConfig2353101 [1032,350].
        Assert.Equal(226, bobo.Matk);
        Assert.Equal(350, bobo.Entity().Property.Single(p => p.Idx == 1032).Data); // late AOI contains the member.
        await Run(() => Service<BuffService>().ClearUnitAsync(bobo));
        await Next(bot, CharacterService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.EntityUuid == (ulong)bobo.Uid && x.Property.Any(p => p.Idx == 103 && p.Data == 218)
                && x.Property.Any(p => p.Idx == 1032 && p.Data == 0));
        Assert.Equal(218, bobo.Atk);
        await Run(async () =>
        {
            // Concrete HarvestSickle attribute114=5 changes influenced normal-attack cadence.
            await Service<BuffService>().AddSupportAsync(bobo, 2340301, 5000, choco.Uid);
            Assert.Equal(953, bobo.ActionMsFor(bobo.AttackSkills.Single()));
            Assert.Equal(1233, bobo.ActionMsFor(bobo.SupportSkills.Single()));
        });
    }

    [ClientDataFact]
    public async Task HealingUsesRecipientHpAndPendingSupportCannotLandAfterSettlement()
    {
        await using var bot = await InTower();
        var s = Session; var a = s.TowerAttempt!;
        TowerPetUnit healer = null!;
        await Run(async () =>
        {
            foreach (var old in a.Pets.ToArray()) await Service<World>().RemoveUnitAsync(old);
            a.Pets.Clear(); foreach (var monster in a.Monsters) monster.Dead = true;
            healer = TowerPetUnit.Create(Service<GameData>(), s.Pets!, s, Service<World>(), Service<MonsterService>(),
                Service<CombatService>(), NextPetUid(), 11001, a.InstanceId, s.Position.X, s.Position.Y, s.Position.Z)!;
            healer.Hp = 1; a.Pets.Add(healer); await Service<World>().AddUnitAsync(healer);
            await Service<CombatService>().TowerPetSupportAsync(s, healer, 2356401);
        });
        var heal = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == healer.Uid && h.SkillId == 2356401);
        Assert.All(heal.Dsts, d => Assert.Equal(DamageType.Heal, d.DamageType));
        long expected = healer.MaxHp * 2 / 100;
        Assert.Equal(expected, heal.Dsts.Single(d => d.DstID == healer.Uid).Damage);
        Assert.Equal(1 + expected, healer.Hp);
        TowerPetUnit cancelled = null!;
        await Run(async () =>
        {
            cancelled = TowerPetUnit.Create(Service<GameData>(), s.Pets!, s, Service<World>(), Service<MonsterService>(),
                Service<CombatService>(), NextPetUid(), 13001, a.InstanceId, s.Position.X, s.Position.Y, s.Position.Z)!;
            a.Pets.Add(cancelled); await Service<World>().AddUnitAsync(cancelled);
            await Service<CombatService>().TowerPetSupportAsync(s, cancelled, 2354301);
            a.Settled = true;
        });
        await Task.Delay(800);
        Assert.Equal(0, Service<BuffService>().ShieldAmount(cancelled));
        Assert.DoesNotContain(bot.Inbox.Snapshot().Where(e => e.IsGame(BuffService.BuffUpdate))
            .Select(e => BuffUpdateNotify.Parser.ParseFrom(e.Body)), b => b.Buffs.Any(x => x.SourceId == cancelled.Uid));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
