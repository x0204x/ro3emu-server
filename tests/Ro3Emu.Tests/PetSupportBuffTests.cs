using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Timed support buffs on WorldUnit and player targets, including finite shield damage.</summary>
public sealed class PetSupportBuffTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-pet-support-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
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

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        // Buff deltas require a joined viewer, after the scene's initial AOI snapshot is complete.
        await bot.Inbox.WaitAsync(e => e.IsGame(GuildService.SceneGuildId), Wait);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private GameSession Session(string account) => _app.Services.GetRequiredService<SessionRegistry>().All()
        .Single(s => s.Account == account);
    private BuffService Buffs => _app.Services.GetRequiredService<BuffService>();
    private World World => _app.Services.GetRequiredService<World>();
    private Task Run(Func<Task> work) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(work);
    private Task<T> Run<T>(Func<Task<T>> work) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(work);

    private sealed class SupportTarget : WorldUnit
    {
        public override EntityBase Entity() => new() { Uuid = (ulong)Uid, EntityType = 1, State = EntityState.Alive };
    }

    private async Task<SupportTarget> Unit(GameSession owner, long uid)
    {
        var unit = new SupportTarget
        {
            Uid = uid, SceneEntry = owner.SceneEntry, InstanceId = owner.InstanceId,
            X = owner.Position.X, Y = owner.Position.Y, Z = owner.Position.Z,
        };
        await Run(() => World.AddUnitAsync(unit));
        return unit;
    }

    [ClientDataFact]
    public async Task UnitBuffRefreshesByGroupAndSourceAndKeepsOtherUnitsIndependent()
    {
        await using var alice = await InWorld("Alice");
        var owner = Session("alice");
        var first = await Unit(owner, 1L << 50);
        var other = await Unit(owner, (1L << 50) + 1);
        await Run(() => Buffs.AddSupportAsync(first, 1100501, 15000, 11));
        var granted = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, x => x.UnitId == first.Uid);
        long guid = Assert.Single(granted.Buffs).BuffGuid;
        var aoi = await Next(alice, CharacterService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.EntityUuid == (ulong)first.Uid && x.BuffInfo?.AddBuffs.Any(b => b.BuffGuid == guid) == true);
        Assert.Single(aoi.BuffInfo.AddBuffs);
        Assert.Contains(Buffs.UnitAttributes(first), c => c.Id == 1053 && c.Value == 55);
        Assert.Empty(Buffs.UnitAttributes(other));

        await Run(() => Buffs.AddSupportAsync(first, 1100501, 16000, 22)); // _iMultiSource=0: group refreshes.
        var refreshed = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            x => x.UnitId == first.Uid && x.Buffs.Any(b => b.SourceId == 22));
        Assert.Equal(guid, Assert.Single(refreshed.Buffs).BuffGuid);
        Assert.Single(Buffs.UnitAttributes(first), c => c.Id == 1053);

        await Run(() => Buffs.AddSupportAsync(first, 902701, 15000, 11)); // _iMultiSource=1.
        await Run(() => Buffs.AddSupportAsync(first, 902701, 15000, 22));
        var snapshot = new EntityBase();
        Buffs.PopulateEntity(first, snapshot);
        Assert.Equal(3, snapshot.BuffData.AddBuffs.Count);
        Assert.Equal(2, snapshot.BuffData.AddBuffs.Count(b => b.ConfigId == 902701));
        await Run(() => World.RemoveUnitAsync(first));
        Assert.Empty(Buffs.UnitAttributes(first));
        Assert.Equal(0, Buffs.ShieldAmount(first));
    }

    [ClientDataFact]
    public async Task UnitShieldAbsorbsBeforeHpAndRemovesSpentAndExpiredBuffs()
    {
        await using var alice = await InWorld("Alice");
        var unit = await Unit(Session("alice"), 1L << 50);
        var other = await Unit(Session("alice"), (1L << 50) + 1);
        await Run(() => Buffs.AddSupportAsync(unit, 2354401, 3000, 42, 65));
        await Run(() => Buffs.AddSupportAsync(other, 2354401, 3000, 42, 20));
        var granted = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, x => x.UnitId == unit.Uid);
        long guid = Assert.Single(granted.Buffs).BuffGuid;
        Assert.Equal(65, Buffs.ShieldAmount(unit));
        var snapshot = new EntityBase();
        Buffs.PopulateEntity(unit, snapshot);
        Assert.Equal(65, Assert.Single(snapshot.Property, p => p.Idx == 2003).Data);
        Assert.Equal(0, await Run(() => Buffs.AbsorbAsync(unit, -5)));
        Assert.Equal(65, Buffs.ShieldAmount(unit));
        Assert.Equal(0, await Run(() => Buffs.AbsorbAsync(unit, 20)));
        Assert.Equal(45, Buffs.ShieldAmount(unit));
        Assert.Equal(20, Buffs.ShieldAmount(other));
        var changed = await Next(alice, CharacterService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.EntityUuid == (ulong)unit.Uid && x.Property.Any(p => p.Idx == 2003 && p.Data == 45));
        // 配對客戶端同時同步總盾量與物理／魔法／通用護盾；此案例是通用護盾。
        Assert.Equal(45, Assert.Single(changed.Property, p => p.Idx == 2003).Data);
        Assert.Equal(0, Assert.Single(changed.Property, p => p.Idx == 2006).Data);
        Assert.Equal(0, Assert.Single(changed.Property, p => p.Idx == 2007).Data);
        Assert.Equal(45, Assert.Single(changed.Property, p => p.Idx == 2008).Data);
        Assert.Equal(35, await Run(() => Buffs.AbsorbAsync(unit, 80)));
        Assert.Equal(0, Buffs.ShieldAmount(unit));
        Assert.Contains(guid, (await Next(alice, BuffService.BuffRemove, BuffRemoveNotify.Parser,
            x => x.UnitId == unit.Uid)).BuffGuids);
        await Run(() => Buffs.AddSupportAsync(unit, 2354401, 100, 42, 20));
        Assert.Equal(20, Buffs.ShieldAmount(unit));
        await Task.Delay(180);
        Assert.Equal(30, await Run(() => Buffs.AbsorbAsync(unit, 30)));
        Assert.Equal(0, Buffs.ShieldAmount(unit));
    }

    [ClientDataFact]
    public async Task PlayerShieldAddsToExistingDynamicShieldAndClearsOnConsumption()
    {
        await using var alice = await InWorld("Alice");
        var player = Session("alice");
        var character = _app.Services.GetRequiredService<CharacterService>();
        character.DynamicProperties.Add(_ => [(2003, 17L)]); // A second shield owner, as TotemEffects is.
        await Run(() => Buffs.AddSupportAsync(player, 2354401, 3000, 42, 65));
        Assert.Equal(82, character.Stats(player)[2003]);
        Assert.Equal(0, await Run(() => Buffs.AbsorbAsync(player, 20)));
        Assert.Equal(62, character.Stats(player)[2003]);
        Assert.Equal(35, await Run(() => Buffs.AbsorbAsync(player, 80)));
        Assert.Equal(17, character.Stats(player)[2003]);
        Assert.DoesNotContain(player.Buffs, b => b.ConfigId == 2354401);
        await Run(() => Buffs.AddSupportAsync(player, 2354401, 3000, 42, 30));
        await Run(() => Buffs.ClearOnDeathAsync(player)); // BuffConfig._iDeadClear=1.
        Assert.Equal(17, character.Stats(player)[2003]);
        await Run(() => Buffs.AddSupportAsync(player, 2354401, 3000, 42, 30));
        await Run(() => Buffs.ClearOnSceneAsync(player)); // BuffConfig._iSceneClear=1.
        Assert.Equal(17, character.Stats(player)[2003]);
    }

    private static long Shield(GameSession player, int buff) =>
        player.Buffs.Where(b => b.ConfigId == buff).Select(b => b.ShieldRemaining).SingleOrDefault();

    [ClientDataFact]
    public async Task PlayerSkillShieldsTakeTheirAmountFromTheCaster()
    {
        await using var alice = await InWorld("Alice");
        var player = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var caster = new StatSheet { [101] = 12345, [104] = 1000 };
        // King's Grace 11163401: "Shield equal to Max HP * 20%" of the caster.
        await Run(() => combat.ApplySkillBuffAsync(caster, player, 11163401, 5000, 7));
        Assert.Equal(2469, Shield(player, 11163401));
        // Kyrie Eleison 1411701: Magic ATK * 40%; Meditate's 9302501 restores Life and carries no shield.
        await Run(() => combat.ApplySkillBuffAsync(caster, player, 1411701, 5000, 7));
        Assert.Equal(400, Shield(player, 1411701));
        await Run(() => combat.ApplySkillBuffAsync(caster, player, 9302501, 5000, 7));
        Assert.Contains(player.Buffs, b => b.ConfigId == 9302501 && b.ShieldRemaining == 0);
        Assert.Equal(2869, _app.Services.GetRequiredService<CharacterService>().Stats(player)[2003]);
    }

    [ClientDataFact]
    public async Task KyrieAbsorbsOnlyPhysicalHitsAndStackedShieldsAddOnePerPile()
    {
        await using var alice = await InWorld("Alice");
        var player = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Run(() => combat.ApplySkillBuffAsync(new StatSheet { [104] = 1000 }, player, 1411701, 5000, 7));
        Assert.Equal(100, await Run(() => Buffs.AbsorbAsync(player, 100, magic: true)));
        Assert.Equal(400, Shield(player, 1411701));
        Assert.Equal(0, await Run(() => Buffs.AbsorbAsync(player, 150)));
        Assert.Equal(250, Shield(player, 1411701));
        Assert.Equal(300, await Run(() => Buffs.AbsorbAsync(player, 550)));
        Assert.DoesNotContain(player.Buffs, b => b.ConfigId == 1411701);

        // Twilight's 1160701 (_iStackType 7, _iMaxStack 5): "Each stack grants a shield equal to 3% of Max Life".
        var caster = new StatSheet { [101] = 10000 };
        await Run(() => combat.ApplySkillBuffAsync(caster, player, 1160701, 5000, 7));
        await Run(() => combat.ApplySkillBuffAsync(caster, player, 1160701, 5000, 7));
        Assert.Equal(600, Shield(player, 1160701));
        Assert.Equal(0, await Run(() => Buffs.AbsorbAsync(player, 100, magic: true)));
        await Run(() => combat.ApplySkillBuffAsync(caster, player, 1160701, 5000, 7));
        Assert.Equal(800, Shield(player, 1160701));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository data/config");
    }
}
