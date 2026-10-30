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
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
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
        // The bar: 1100110 1100210 1100405 1109901 1100505, then the normal attack 1001001 as the Swordman's own (11000).
        Assert.Contains("Skill bar: 6 skills.", reply);
        var bar = s.SkillPlans!.SkillAssignPlans[s.SkillPlans.CurrentPlanIndex].ManualLoadSkill;
        Assert.Equal([11001, 11002, 11004, 11099, 11005, 11000], bar.Select(b => b.SkillId));
        Assert.Contains(s.Items, i => i.Entry == 90000722 && i.Grid > 0);
        var plan = s.SeasonTalent!.Plan[s.SeasonTalent.CurPlanId];
        Assert.Equal(3, plan.TalentNodes[10101001].CurrentLevel); // SeasonTalentConfig _iMaxLevel 3
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
