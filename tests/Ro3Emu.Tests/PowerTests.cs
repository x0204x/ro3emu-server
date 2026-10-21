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
/// Role power (PowerService): the 31502/31503 package against a hand computation from the client's Lua formulas, plan
/// power on the plan datasets, change publishing with the persisted maxHistoryPower, the Become Stronger (1) and Season
/// Treasure (8) stage rewards, and the Echo Resonance stats, stage power and SkillInUse rows.
/// </summary>
public sealed class PowerTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-power-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private WebApplication _app = null!;
    private string _httpUrl = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http;
            o.MicroGatePort = micro;
            o.GameGatePort = game;
        });
        await _app.StartAsync();
        _httpUrl = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameData Data => Service<GameData>();

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string command)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync($"{_httpUrl}/ro3emu/gm?account={account}", new StringContent(command));
        response.EnsureSuccessStatusCode();
    }

    private GameSession Session(string name) => Service<SessionRegistry>().All().Single(s => s.Role?.Name == name);
    private Task<T> OnLoop<T>(string name, Func<GameSession, Task<T>> work) => Service<GameLoop>().InvokeAsync(() => work(Session(name)));

    private static long Sum(PowerUnit u) => u.NormalPower + u.SeasonPower;
    private static long Sum(PlayerPowerPackage p) => new[]
    {
        p.LevelUpAddPointScore, p.SkillAddPointScore, p.TitleScore, p.EquipRankScore, p.EquipRefineScore,
        p.EquipStrengthenScore, p.CardSuitScore, p.CardFillInEquipScore, p.TotemScore, p.EmblemScore,
    }.Sum(Sum);

    private double Weight(long attr) => Data.Row("AttributeConfig", attr) is { } row && row.TryGetProperty("_iPower", out var w)
        ? GameData.Number(w) ?? 0 : 0;
    private long BaseLv(int level) => GameData.Int(Data.Row("BaseLvConfig", level)!.Value, "_iPower");

    private JsonElement Row(string table, Func<JsonElement, bool> match) => Data.Table(table).Values.First(match);

    [ClientDataFact]
    public async Task PackageMatchesTheClientFormulasForAKnownSetup()
    {
        await using var alice = await InWorld("Alice");
        var login = await Next(alice, PowerService.ChangeNtf, PlayerChangePowerNtf.Parser);
        // curPower = BaseLvConfig[level]._iPower + the package (+ pet plan power, none here).
        Assert.Equal(BaseLv(1) + Sum(login.PowerPackage), Sum(login.CurPower));
        Assert.Equal(login.CurPower, login.MaxHistoryPower);
        var package = await Next(alice, PowerService.PackageNtf, PlayerPowerPackageNtf.Parser);
        Assert.Equal(login.PowerPackage, package.PowerPackage);
        Assert.Equal(0, login.PowerPackage.LevelUpAddPointScore.NormalPower);

        const int gearEntry = 2010120, cardEntry = 1420100, enchant = 1011201;
        await OnLoop("Alice", async s =>
        {
            using var held = await CharacterService.Lock(s);
            s.Progress!.Stats[0] = 5; // STR: 4 points above the base 1
            s.Progress.Stats[1] = 3;  // AGI: 2 points
            var gear = new InvItem { Guid = 9_100_001, Entry = gearEntry, Stack = 1, Pack = 1, Grid = 1, Attrs = new() { [3] = 1, [30] = 103, [60] = 10 } };
            gear.Enchantments[enchant] = new ItemEnchantment { Attr = new() { [103] = 1 } };
            s.Items.Add(gear);
            s.Items.Add(new InvItem { Guid = 9_100_002, Entry = cardEntry, Stack = 1, Grid = CharacterService.CardGrid(1, 1), Attrs = new() { [502] = 1 } });
            s.EquipLevels.Strength[1] = 1;
            s.EquipLevels.Refine[1] = 1;
            await Service<PowerService>().PublishAsync(s);
            return true;
        });
        var changed = await Next(alice, PowerService.ChangeNtf, PlayerChangePowerNtf.Parser, m => m.PowerPackage.EquipRankScore.NormalPower > 0);
        var p = changed.PowerPackage;

        // LC_ScoreHelper.CalcAttrScore (value x AttributeConfig._iPower) over the allocated points.
        Assert.Equal((long)Math.Ceiling(4 * Weight(1) + 2 * Weight(2)), p.LevelUpAddPointScore.NormalPower);
        // Rank: the instance score (PATK 10 x weight) plus the Enchantment row's power.
        Assert.Equal((long)Math.Round(10 * Weight(103)) + GameData.Int(Data.Row("Enchantment", enchant)!.Value, "_iPower"),
            p.EquipRankScore.NormalPower);
        Assert.Equal(GameData.Int(Data.Row("Enchantment", enchant)!.Value, "_iSeasonPower"), p.EquipRankScore.SeasonPower);
        // Grid 1 at strengthen / refine level 1: the rows' own _iPower (no resonance at one grid).
        Assert.Equal(GameData.Int(Row("StrengthenTable", r => GameData.Int(r, "_iEquipType") == 1 && GameData.Int(r, "_iStrengthenInterval") == 1), "_iPower"),
            p.EquipStrengthenScore.NormalPower);
        Assert.Equal(GameData.Int(Row("RefiningTable", r => GameData.Int(r, "_iEquipType") == 1 && GameData.Int(r, "_iRefiningInterval") == 1), "_iPower"),
            p.EquipRefineScore.NormalPower);
        // LC_CardHelper.GetPower: CardConfig by (card, star) plus CardUpgradeConfig of the card's level 0 row.
        var item = Data.Row("ItemConfig", cardEntry)!.Value;
        long upgrade = Data.Table("CardUpgradeConfig").Values.Where(r => GameData.Int(r, "_iLevel") == 0
            && GameData.Int(r, "_iParts") == GameData.Int(item, "_iCardPart") && GameData.Int(r, "_iQuality") == GameData.Int(item, "_iQuality"))
            .Select(r => GameData.Int(r, "_iPower")).FirstOrDefault();
        Assert.Equal(GameData.Int(Row("CardConfig", r => GameData.Int(r, "_iCardID") == cardEntry && GameData.Int(r, "_iTier") == 1), "_iPower") + upgrade,
            p.CardFillInEquipScore.NormalPower);
        Assert.Equal(0, Sum(p.CardSuitScore));
        Assert.Equal(0, Sum(p.TitleScore));
        Assert.Equal(BaseLv(1) + Sum(p), Sum(changed.CurPower));
        Assert.Equal(Sum(changed.CurPower) - Sum(login.CurPower), Sum(changed.ChangePower));
        Assert.Equal(changed.CurPower, changed.MaxHistoryPower);
        await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser,
            m => m.CurPower?.Inc?.NormalPower == changed.CurPower.NormalPower); // 5066 follows

        // Plan power on the plan datasets: a 5067 plan of the same points, and a 5025 plan with one passive row.
        var (statPlan, skillPlan, passive) = await OnLoop("Alice", s =>
        {
            var attrs = new BaseAttrInfo();
            attrs.AllPlanBaseProperty[0] = new PlanBaseProperty { BaseProperty = { [1] = 5, [2] = 3, [3] = 1, [4] = 1, [5] = 1, [6] = 1 } };
            s.PlanPowers!(CharacterService.BaseAttrSync, attrs);
            var skillsData = new DBSkillData();
            var plan = new SkillPlan();
            plan.SkillAssigns[11006] = new SkillData { SkillId = 11006, SkillLevel = 1 };
            skillsData.SkillAssignPlans.Add(plan);
            s.PlanPowers!(SkillService.SkillDataSync, skillsData);
            var row = Data.Row("SkillConfig", 1100601)!.Value;
            return Task.FromResult((attrs.AllPlanBaseProperty[0].Power.NormalPower, plan.Power.NormalPower, row));
        });
        Assert.Equal(p.LevelUpAddPointScore.NormalPower, statPlan);
        Assert.Equal((long)Math.Ceiling(GameData.Pairs(passive, "_kAttri").Sum(a => a.Value * Weight(a.Id))), skillPlan);
        Assert.True(skillPlan > 0);
    }

    [ClientDataFact]
    public async Task ChangesPublishAndTheHistoryIsKeptAcrossRelog()
    {
        long high, low;
        long roleId;
        await using (var alice = await InWorld("Alice"))
        {
            var first = await Next(alice, PowerService.ChangeNtf, PlayerChangePowerNtf.Parser);
            await Gm("alice", "!lvl 30");
            var raised = await Next(alice, PowerService.ChangeNtf, PlayerChangePowerNtf.Parser,
                m => Sum(m.CurPower) - Sum(m.PowerPackage) == BaseLv(30));
            high = Sum(raised.CurPower);
            Assert.True(high > Sum(first.CurPower));
            Assert.Equal(raised.CurPower, raised.MaxHistoryPower);
            await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser,
                m => m.CurPower?.Inc?.NormalPower == raised.CurPower.NormalPower
                    && m.HistoryMaxPower?.Inc?.NormalPower == raised.MaxHistoryPower.NormalPower);

            await Gm("alice", "!lvl 5");
            var lowered = await Next(alice, PowerService.ChangeNtf, PlayerChangePowerNtf.Parser,
                m => Sum(m.CurPower) - Sum(m.PowerPackage) == BaseLv(5));
            low = Sum(lowered.CurPower);
            Assert.True(low < high);
            Assert.Equal(high, Sum(lowered.MaxHistoryPower));
            Assert.Equal(low - high, Sum(lowered.ChangePower));
            roleId = (await OnLoop("Alice", s => Task.FromResult(s.Role!.Id)));
        }
        var saved = JsonSerializer.Deserialize<EmblemService.EmblemState>(Service<RoleStore>().LoadEmblems(roleId)!)!.Power!;
        Assert.Equal(high, saved.HistoryNormal + saved.HistorySeason);
        Assert.Equal(low, saved.CurrentNormal + saved.CurrentSeason);

        await using var again = await InWorld("Alice", create: false);
        var relog = await Next(again, PowerService.ChangeNtf, PlayerChangePowerNtf.Parser);
        Assert.Equal(low, Sum(relog.CurPower));
        Assert.Equal(high, Sum(relog.MaxHistoryPower));
    }

    [ClientDataFact]
    public async Task BecomeStrongerStagesNeedTheBestPowerAndSeasonTreasureStagesTheItemCount()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, PowerService.ChangeNtf, PlayerChangePowerNtf.Parser);
        var stage1 = Data.Row("GenericStageRewardsConfig", 1)!.Value;
        Assert.Equal(1, GameData.Int(stage1, "_iType"));
        await alice.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 1 });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);

        await Gm("alice", "!lvl 30"); // opens function 48 (level 10) and raises the base power to 3600
        var raised = await Next(alice, PowerService.ChangeNtf, PlayerChangePowerNtf.Parser, m => Sum(m.MaxHistoryPower) >= GameData.Int(stage1, "_iScore"));
        await Gm("alice", "!lvl 10"); // current power drops below _iScore; the claim reads the best power
        await Next(alice, PowerService.ChangeNtf, PlayerChangePowerNtf.Parser, m => Sum(m.CurPower) < GameData.Int(stage1, "_iScore"));
        await alice.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 1 });
        Assert.Equal(0, (await Next(alice, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        await alice.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 1 });
        Assert.Equal(ErrorCodes.RewardClaimed, (await Next(alice, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        long unreached = Data.Table("GenericStageRewardsConfig").Values.Where(r => GameData.Int(r, "_iType") == 1
            && GameData.Int(r, "_iScore") > Sum(raised.MaxHistoryPower)).Select(r => GameData.Int(r, "_iID")).Min();
        await alice.SendGameAsync(DailyActivityService.StageBatchGain, new StageRewardBatchGainReq { StageIds = { (int)unreached } });
        Assert.Equal(ErrorCodes.CurrencyShort, (await Next(alice, DailyActivityService.StageBatchGain, StageRewardBatchGainResp.Parser)).Result);

        // Season Treasure: item 130401 count against _iScore 1 / 3 / 5.
        await Gm("alice", "!item 130401 3");
        await alice.SendGameAsync(DailyActivityService.StageBatchGain, new StageRewardBatchGainReq { StageIds = { 8001, 8002 } });
        Assert.Equal(0, (await Next(alice, DailyActivityService.StageBatchGain, StageRewardBatchGainResp.Parser)).Result);
        await alice.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 8003 });
        Assert.Equal(ErrorCodes.CurrencyShort, (await Next(alice, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        var claims = await OnLoop("Alice", s => Task.FromResult(s.Daily!.Claims.ToArray()));
        Assert.Contains(1, claims);
        Assert.Contains(8001, claims);
        Assert.Contains(8002, claims);
        Assert.DoesNotContain(8003, claims);
    }

    [ClientDataFact]
    public async Task EchoResonanceAddsTheJobConversionStagePowerAndSkillRows()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, PowerService.ChangeNtf, PlayerChangePowerNtf.Parser);
        await Gm("alice", "!job 1400");
        var character = Service<CharacterService>();
        var emblems = Service<EmblemService>();
        const int echo = 1850101; // four mark slots

        // Four echoes, each with mark 1001 in all four lit slots: 16 marks reach stages 1-3 of mark 1001.
        long[] Deploy(GameSession s, long mark)
        {
            var guids = new List<long>();
            s.Emblems.Plans[s.Emblems.CurrentPlan].Slots.Clear();
            s.Items.RemoveAll(i => i.Entry == echo);
            for (int slot = 1; slot <= 4; slot++)
            {
                var item = new InvItem
                {
                    Entry = echo, Stack = 1, Pack = 5, Sort = 100 + slot, Guid = 9_000_000 + slot,
                    Attrs = new() { [1001] = 0, [10001] = 2, [10002] = slot }
                };
                for (int i = 0; i < 4; i++) item.Attrs[1021 + i] = mark;
                s.Items.Add(item);
                s.Emblems.Plans[s.Emblems.CurrentPlan].Slots[slot] = item.Guid;
                guids.Add(item.Guid);
            }
            return guids.ToArray();
        }

        var result = await OnLoop("Alice", s =>
        {
            Assert.Equal(1400, s.Progress!.Career);
            s.Progress.Stats[0] = 99; // a visible floor(STR / 4) bonus, below the cap of 50
            Deploy(s, 0); // no lit mark: the plan scores items only
            var bare = character.Stats(s);
            long barePower = emblems.GenrePower(s, s.Emblems).SeasonPower;
            Deploy(s, 1001);
            var lit = character.Stats(s);
            long litPower = emblems.GenrePower(s, s.Emblems).SeasonPower;
            var skills = emblems.ResonanceSkills(s).Order().ToArray();
            long stage = EmblemService.Resonances(s, Data)[1001];
            s.Progress.Career = 1100; // another job: the conversion and job-bound rows do not apply
            var otherLit = character.Stats(s);
            var otherSkills = emblems.ResonanceSkills(s).ToArray();
            Deploy(s, 0);
            var otherBare = character.Stats(s);
            s.Progress.Career = 1400;
            return Task.FromResult((bare, lit, otherBare, otherLit, barePower, litPower, skills, otherSkills, stage));
        });

        // 13400100 [15, 1400, STR, 4, 1033 (flat PATK), 1, 50]: min(floor(STR / 4) * 1, 50) on top of the same sheet.
        long str = result.bare[1];
        Assert.True(str >= 4);
        Assert.Equal(result.bare[103] + Math.Min(str / 4, 50), result.lit[103]);
        Assert.Equal(result.bare[1], result.lit[1]);
        Assert.Equal(result.otherBare[103], result.otherLit[103]);
        Assert.Equal(3, result.stage);
        // Stages 1-3 of mark 1001 add their EmblemMarkConfig._iSeasonPower to the plan (100 + 200 + 300 here).
        long stages = Data.Table("EmblemMarkConfig").Values.Where(r => GameData.Int(r, "_iEmblemMarkID") == 1001 && GameData.Int(r, "_iMarkStage") > 0)
            .Sum(r => GameData.Int(r, "_iSeasonPower"));
        Assert.Equal(result.barePower + stages, result.litPower);
        // SkillInUse rows of stages 2-3 whose SkillConfig._iJob is in the job line: 11130101 and 11130201 for 1400.
        Assert.Equal(new[] { 11130101, 11130201 }, result.skills);
        Assert.Empty(result.otherSkills);
    }
}
