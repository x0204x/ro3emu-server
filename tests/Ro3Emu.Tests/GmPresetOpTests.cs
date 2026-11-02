using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The GM panel ops that only GMCombinationConfig presets use (GMConfig buttons 1030-1032, 3008-3011): a whole class
/// preset runs with every step implemented, and its build (stats, skills, gear, echoes, lease, talents) is on the role.
/// </summary>
public sealed class GmPresetOpTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-gmpreset-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private WebApplication _app = null!;
    private string _http = "";

    public Task InitializeAsync() => StartAsync();

    private async Task StartAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    private async Task StopAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public async Task DisposeAsync()
    {
        await StopAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync(create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id);
        return bot;
    }

    private static async Task<string> PanelAsync(BotClient bot, GeneralGMProcReq req)
    {
        await bot.SendGameAsync(GmCommands.GeneralProcGm, req);
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.GeneralProcGm), Wait);
        return GeneralGMProcRsp.Parser.ParseFrom(e.Body).ErrInfo;
    }

    private GameSession Session(BotClient bot) =>
        _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)bot.Self!.Uuid);

    [ClientDataFact]
    public async Task SwordmanPresetRunsEveryStepAndBuildsTheRole()
    {
        await using var bot = await InWorld("Preset");
        // 1100 Swordman: levels, job, openallfunc, profession 101's skills and stats, the bar, the weapon, talent tree 10101.
        string reply = await PanelAsync(bot, new GeneralGMProcReq { GmListId = 1100 });
        Assert.DoesNotContain("Not implemented", reply);
        Assert.Contains("Skill build 101240", reply); // profession 101, job rank 2, job level 40
        Assert.Contains("Equipped", reply);

        var s = Session(bot);
        Assert.Equal(1100, s.Progress!.Career);
        Assert.True(s.GmAllFunctionsOpen);
        Assert.Equal([29, 29, 1, 1, 1, 1], s.Progress.Stats); // JobProfessPointConfig 101029
        Assert.Equal(10, s.Progress.Skills.GetValueOrDefault(11001)); // 1100110 in JobProfessSkillConfig 101240
        // G的JobAttackConfig以目前武器選1001001（群組10010）；預設列不能改用舊的職業普攻11000。
        Assert.Contains("Skill bar: 6 skills.", reply);
        var bar = s.SkillPlans!.SkillAssignPlans[s.SkillPlans.CurrentPlanIndex].ManualLoadSkill;
        Assert.Equal(1001001, _app.Services.GetRequiredService<SkillService>().NormalAttack(s));
        Assert.Equal([11001, 11002, 11004, 11099, 11005, 10010], bar.Select(b => b.SkillId));
        Assert.Contains(s.Items, i => i.Entry == 90000722 && i.Grid > 0);
        var plan = s.SeasonTalent!.Plan[s.SeasonTalent.CurPlanId];
        Assert.Equal(3, plan.TalentNodes[10101001].CurrentLevel); // SeasonTalentConfig _iMaxLevel 3
    }

    /// <summary>
    /// A preset-built role never delivered quest 10101170, which opens Skills (function 10): openallfunc held only for
    /// the session closed the skill bar (its lock, no drags) at the next login and after a server restart.
    /// </summary>
    [ClientDataFact]
    public async Task AssassinPresetKeepsItsFunctionsAndSkillBarAfterRelogAndRestart()
    {
        await using (var bot = await InWorld("Sin"))
        {
            // 5200 Assassin: levels, job, openallfunc, profession 503's skills, the bar.
            Assert.DoesNotContain("Not implemented", await PanelAsync(bot, new GeneralGMProcReq { GmListId = 5200 }));
            Assert.True(Session(bot).GmAllFunctionsOpen);
        }
        await AssertBuildAfterLogin();
        await StopAsync();
        await StartAsync();
        await AssertBuildAfterLogin();

        async Task AssertBuildAfterLogin()
        {
            await using var bot = await InWorld("Sin", create: false);
            var open = FunctionOpenInfoIncrement.Parser.ParseFrom((await bot.Inbox.WaitAsync(
                e => e.IsGame(FunctionService.FunctionOpenSync), Wait)).Body);
            Assert.True(open.FunctionOpenInfos[10].All); // SkillGrowth: the skill window and the bar
            Assert.True(open.FunctionOpenInfos[1001].All); // SkillSchool
            var skills = DBSkillDataIncrement.Parser.ParseFrom((await bot.Inbox.WaitAsync(
                e => e.IsGame(SkillService.SkillDataSync), Wait)).Body);
            Assert.Equal(5200, Session(bot).Progress!.Career);
            Assert.Equal(10, skills.SkillAssignPlans.All.Value[(int)skills.CurrentPlanIndex].SkillAssigns[15111].SkillLevel);
            Assert.Equal(6, skills.SkillWheel.All.WhellSkills.Count(w => w.Key is >= 1 and <= 6 && w.Value.SkillConfigId > 0));
            // A learned skill still goes on the bar: Envenom (15005) over slot 6.
            var plan = Session(bot).SkillPlans!.SkillAssignPlans[Session(bot).SkillPlans!.CurrentPlanIndex];
            await bot.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
            {
                OpType = SkillLoadOperation.SkillLoadOpReplace, IsManual = true, SocketNum = 5,
                Info = new SkillData { SkillId = 15005, SkillLevel = 10 }, ReplaceInfo = plan.ManualLoadSkill[5].Clone()
            });
            Assert.Equal(0, SkillLoadResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(SkillService.SkillLoad), Wait)).Body).Result);
        }
    }

    [ClientDataFact]
    public async Task DragonKnightPresetDeploysEchoesLeasesTheMountAndQueuesSkills()
    {
        await using var bot = await InWorld("Dragon");
        // 1401 Rune Knight (dragon): five level-20 Soul Echoes, mount 1500205, queue 11314 11201, four talent trees.
        string reply = await PanelAsync(bot, new GeneralGMProcReq { GmListId = 1401 });
        Assert.DoesNotContain("Not implemented", reply);

        var s = Session(bot);
        var echoes = s.Emblems.Plans[s.Emblems.CurrentPlan].Slots;
        Assert.Equal(5, echoes.Count);
        Assert.All(echoes.Values, guid => Assert.Equal(20, s.Items.Single(i => i.Guid == guid).Attrs![1001]));
        Assert.Equal(1500205, s.CareerSpecial!.MountsSlot.CartRemouldCfg);
        var queue = s.SkillPlans!.SkillAssignPlans[s.SkillPlans.CurrentPlanIndex].SkillQueue;
        Assert.Equal([11314, 11201], queue.OrderBy(q => q.Key).Select(q => q.Value.SkillId));
    }

    [ClientDataFact]
    public async Task PendantRideAndAppearanceSteps()
    {
        await using var bot = await InWorld("Ranger");
        // 3401 Ranger (shooting): the wolf, ridden ("1"); then 86: every appearance.
        string reply = await PanelAsync(bot, new GeneralGMProcReq { GmListId = 3401 });
        Assert.DoesNotContain("Not implemented", reply);
        var s = Session(bot);
        Assert.Equal(1500400, s.CareerSpecial!.MountsSlot.CartRemouldCfg);
        Assert.True(s.Mounts!.MountIds[2].IsRiding);

        reply = await PanelAsync(bot, new GeneralGMProcReq { GmListId = 86 });
        Assert.DoesNotContain("Not implemented", reply);
        Assert.Contains("appearances unlocked", reply);
        Assert.True(s.Wardrobe!.AllLightUpAppearances.Count > 100);

        // 3200 Hunter: the falcon in the pendant slot.
        reply = await PanelAsync(bot, new GeneralGMProcReq { GmListId = 3200 });
        Assert.DoesNotContain("Not implemented", reply);
        Assert.Equal(1500100, s.CareerSpecial!.PendantSlot.CartRemouldCfg);
    }

    [ClientDataFact]
    public async Task UnknownSubcommandsAreRefusedWithTheReason()
    {
        await using var bot = await InWorld("Refuse");
        Assert.Contains("only openallfunc", await PanelAsync(bot, new GeneralGMProcReq { GmType = (GmOpEnum)999, ParamString = "openall" }));
        Assert.Contains("only upgradetree", await PanelAsync(bot, new GeneralGMProcReq { GmType = (GmOpEnum)261, ParamString = "reset,10101" }));
        Assert.Contains("not a Special Class Item mount", await PanelAsync(bot, new GeneralGMProcReq { GmType = (GmOpEnum)175, ParamString = "1500100" }));
    }
}
