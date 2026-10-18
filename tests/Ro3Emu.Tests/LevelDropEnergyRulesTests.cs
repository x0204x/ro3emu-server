using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Client-shipped rules: the level cap 99 (BaseLvLimitConfig), the server-level EXP catch-up (BaseExpAddConfig), drop
/// attenuation by level gap (DropAttenuationConfig) and the Luxury Dragon's energy meter (EnergyConfig._iBelongMount).
/// </summary>
public sealed class LevelDropEnergyRulesTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-rules-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

    private sealed class Clock(long start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(start);
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    // Season start 2026-10-05 (realm midnight); noon of 2026-10-17 is open day 13, server level 69.
    private static readonly long Noon17th = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.FromHours(2)).ToUnixTimeSeconds();
    private const int DragonMount = 60060010, DragonEnergy = 3112301, DragonWhistle = 1501004;

    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.SeasonStart = "2026-10-05";
            o.PlayBook = 101;
        }, new Clock(Noon17th));
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

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameData Data => Service<GameData>();
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);

    private async Task<BotClient> InWorld(string name, Action<long>? beforeEnter = null)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        beforeEnter?.Invoke(id);
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task Gm(BotClient bot, string line)
    {
        using var http = new HttpClient();
        var resp = await http.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(line));
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    // ---- 1. level cap ----

    [ClientDataFact]
    public void TheCapIsTheLimitTablesHighestLevelNotTheLastExpRow()
    {
        // BaseLvConfig still lists the EXP of 99 -> 100 (the old cap was its first 0-EXP row, 100).
        Assert.True(GameData.Int(Data.Row("BaseLvConfig", 99)!.Value, "_iBaseExp") > 0);
        Assert.Equal(0, GameData.Int(Data.Row("BaseLvConfig", 100)!.Value, "_iBaseExp"));
        Assert.Equal(99, Data.Table("BaseLvLimitConfig").Values.Max(r => GameData.Int(r, "_iBaseLimit")));
        Assert.Equal(99, Service<CharacterService>().MaxLevel);
    }

    [ClientDataFact]
    public async Task ExpStopsAt99AndARoleAboveTheCapKeepsItsLevel()
    {
        await using var bot = await InWorld("Capped");
        var session = Session(bot);
        await Gm(bot, "!lvl 120");
        Assert.Equal(99, session.Progress!.Level);
        await Gm(bot, "!exp 50000000");
        Assert.Equal((99, 0L), (session.Progress.Level, session.Progress.Exp));
    }

    [ClientDataFact]
    public async Task ARoleSavedAboveTheCapKeepsItsLevel()
    {
        // A role saved at 100 under the old cap (BaseLvConfig's last row) is never lowered by EXP.
        await using var bot = await InWorld("Veteran", id =>
        {
            var roles = Service<RoleStore>();
            var progress = roles.LoadProgress(id);
            progress.Level = 100;
            roles.SaveProgress(id, progress);
        });
        var session = Session(bot);
        Assert.Equal(100, session.Progress!.Level);
        await Gm(bot, "!exp 1000");
        Assert.Equal((100, 0L), (session.Progress.Level, session.Progress.Exp));
    }

    // ---- 2. server level and the EXP catch-up ----

    [ClientDataFact]
    public void TheServerLevelFollowsTheOpenDayAsTheClientReadsIt()
    {
        var character = Service<CharacterService>();
        Assert.Equal(13, Service<FunctionService>().ServerDay());
        Assert.Equal(69, character.ServerLevel());
        Assert.Equal(1, character.ServerLevelOn(1));
        Assert.Equal(42, character.ServerLevelOn(2));
        Assert.Equal(95, character.ServerLevelOn(41));
        Assert.Equal(99, character.ServerLevelOn(45));
        Assert.Equal(99, character.ServerLevelOn(200));
        // Lua_CFG_BaseLvLimitConfig.GetWorldLevel: no row is level 0.
        Assert.Equal(0, character.ServerLevelOn(201));
        Assert.Equal(0, character.ServerLevelOn(0));
    }

    [ClientDataFact]
    public void TheCatchUpPercentIsTheClientsLowerBound()
    {
        var character = Service<CharacterService>();
        Assert.Equal(0, character.PursueExpPercent(70, 69));
        Assert.Equal(0, character.PursueExpPercent(69, 69));
        Assert.Equal(0, character.PursueExpPercent(65, 69));       // 4 below
        Assert.Equal(2000, character.PursueExpPercent(64, 69));    // 5 below: +20%
        Assert.Equal(14500, character.PursueExpPercent(59, 69));   // 10 below
        Assert.Equal(40000, character.PursueExpPercent(49, 69));   // 20 below: +400%
        Assert.Equal(40000, character.PursueExpPercent(1, 69));    // past the table: the last row
        Assert.Equal(500, character.WithPursueBonus(1, 100));
        Assert.Equal(100, character.WithPursueBonus(69, 100));
    }

    [ClientDataFact]
    public async Task ExpBelowTheServerLevelGetsTheBonus()
    {
        await using var bot = await InWorld("Chaser");
        var session = Session(bot);
        await Gm(bot, "!lvl 60");
        Assert.True(GameData.Int(Data.Row("BaseLvConfig", 60)!.Value, "_iBaseExp") > 2200);
        await Gm(bot, "!exp 1000");
        // 9 below server level 69: BaseExpAddConfig +120%.
        Assert.Equal((60, 2200L), (session.Progress!.Level, session.Progress.Exp));
        await Gm(bot, "!lvl 69");
        await Gm(bot, "!exp 1000");
        Assert.Equal((69, 1000L), (session.Progress.Level, session.Progress.Exp));
    }

    // ---- 3. drop attenuation ----

    [ClientDataFact]
    public void AttenuationPlansScaleByThePlayerMonsterLevelGap()
    {
        var plans = new DropAttenuation(Data);
        Assert.Equal(10000, plans.Ratio(1, 0));
        Assert.Equal(10000, plans.Ratio(1, -9));
        Assert.Equal(10000, plans.Ratio(1, 10));
        Assert.Equal(7000, plans.Ratio(1, -15));
        Assert.Equal(7000, plans.Ratio(1, 15));
        Assert.Equal(3000, plans.Ratio(1, -29));
        Assert.Equal(3000, plans.Ratio(1, 30));
        Assert.Equal(0, plans.Ratio(1, -30));
        Assert.Equal(0, plans.Ratio(1, 31));
        Assert.Equal(5000, plans.Ratio(2, -19));
        Assert.Equal(5000, plans.Ratio(2, 30));
        Assert.Equal(0, plans.Ratio(2, -20));
        Assert.Equal(0, plans.Ratio(2, 31));
        Assert.Equal(10000, plans.Ratio(0, 500));
        // The groups using them: NPC 5099017's drop 22 -> NPCDropConfig 10022 -> pools 24/25/26 -> groups 107/106/105.
        Assert.Equal(1, GameData.Int(Data.Row("DropGroupConfig", 105)!.Value[0], "_iAttenuationPlanID"));
        Assert.Equal(2, GameData.Int(Data.Row("DropGroupConfig", 106)!.Value[0], "_iAttenuationPlanID"));
        Assert.Equal(2, GameData.Int(Data.Row("DropGroupConfig", 107)!.Value[0], "_iAttenuationPlanID"));
        Assert.Equal(22, GameData.Int(Data.Row("NPCConfig", 5099017)!.Value, "_iDrop"));
    }

    [ClientDataFact]
    public void AKillDropFromAnAttenuatedGroupShrinksWithTheGap()
    {
        var rewards = Service<RewardService>();
        List<(int, int)> Roll(int playerLevel, int monsterLevel)
        {
            var drops = new List<(int, int)>();
            rewards.PickFromGroup(105, new RoleProgress { Career = 1000, Level = playerLevel }, drops, null, monsterLevel);
            return drops;
        }
        // Group 105: 100 x item 2, plan 1.
        Assert.Equal([(2, 100)], Roll(50, 50));
        Assert.Equal([(2, 30)], Roll(80, 50));
        Assert.Equal([(2, 70)], Roll(35, 50));
        Assert.Empty(Roll(85, 50));
        // No slain monster (an award): in full.
        Assert.Equal([(2, 100)], Roll(85, DropAttenuation.NoMonster));
    }

    [ClientDataFact]
    public void ExpCurveDropGroupsAreImplicitGuaranteedBaseExp()
    {
        var drops = new List<(int, int)>();
        Service<RewardService>().PickFromGroup(3005, new RoleProgress { Career = 1000, Level = 5 }, drops);
        Assert.Equal([(100, 5)], drops);
    }

    [ClientDataFact]
    public void DAsiaSerialKeyedDropGroupUsesOmittedWeightAndQuantityDefaults()
    {
        var groups = Assert.IsType<Dictionary<string, JsonElement>>(Data.Table("DropGroupConfig"));
        const string groupId = "121121";
        JsonElement original = groups[groupId];
        groups[groupId] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["6"] = new
            {
                _iID = 121121, _iJobLimit = new[] { 2100 }, _iMinLevelLimit = 1,
                _iSerial = 6, _kDropItem = 2090120,
            },
        });
        try
        {
            var drops = new List<(int, int)>();
            Service<RewardService>().PickFromGroup(121121, new RoleProgress { Career = 2100, Level = 10 }, drops);
            Assert.Equal([(2090120, 1)], drops);
        }
        finally { groups[groupId] = original; }
    }

    // ---- 8. mount energy ----

    [ClientDataFact]
    public async Task OwningTheLuxuryDragonGivesItsEnergyMeter()
    {
        var row = Data.Row("EnergyConfig", DragonEnergy)!.Value;
        Assert.Equal(DragonMount, GameData.Int(row, "_iBelongMount"));
        await using var bot = await InWorld("Rider");
        var session = Session(bot);
        Assert.False(session.Energy.ContainsKey(DragonEnergy));
        var skill = Data.Row("SkillConfig", DragonEnergy)!.Value;
        Assert.False(Service<BuffService>().CanSpendEnergy(session, skill));
        await Gm(bot, "!lvl 10");
        await Gm(bot, $"!item {DragonWhistle} 1");
        long guid = session.Items.Single(i => i.Entry == DragonWhistle).Guid;
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        var meter = await Next(bot, BuffService.EnergyNotify, PlayerEnergyNotify.Parser, n => n.EnergyConfigId == DragonEnergy);
        Assert.Equal(12, meter.EnergyValue);
        Assert.True(session.Mounts!.Mounts.ContainsKey(DragonMount));
        // Skill 3112301 costs [8,3112301,0,12]: the full starting meter pays it.
        Assert.True(Service<BuffService>().CanSpendEnergy(session, skill));
    }
}
