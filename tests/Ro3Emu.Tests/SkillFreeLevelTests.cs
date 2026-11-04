using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// A skill's free level (_iInitialLevel) with a prerequisite, as Sight Sprout has on Sight, over invented data. The client
/// refunds it to 0 along with its prerequisite (LC_SkillGrowthHelper.LocalMinusSkillLevel), and its reset before a
/// recommended build sets it back to the free level with the prerequisite at 0 (ResetAllChanged), the state of a build
/// nothing was spent in.
/// </summary>
public sealed class SkillFreeLevelTests
{
    private const int Sight = 995, Sprout = 996, FireWall = 997, FreeWithoutPrerequisite = 998, Bolt = 999;

    // The report's first symptom: the client's refund of Sight names Sprout at 0, which came back Parameter.
    [Fact]
    public async Task RefundingAPrerequisiteTakesTheClientsZeroForItsFreeDependent() => await InWorld(async (host, alice, session) =>
    {
        var skills = host.Service<SkillService>();
        Assert.Equal(0, await Update(alice, (Sight, 1), (FireWall, 1)));
        int spent = skills.SpentPoints(session.Progress!);

        Assert.Equal(0, await Update(alice, (Sight, 0), (Sprout, 0), (FireWall, 0)));
        Assert.Equal(0, Shown(skills, session, 0, Sight));
        Assert.Equal(0, Shown(skills, session, 0, FireWall));
        Assert.Equal(1, Shown(skills, session, 0, Sprout)); // stays, and the refund neither charged nor paid for it
        Assert.Equal(1, skills.Level(session.Progress!, Sprout));
        Assert.Equal(spent - 2, skills.SpentPoints(session.Progress!));

        // Taken away and given back before Confirm: the client's only difference is then Sprout at 0.
        Assert.Equal(0, await Update(alice, (Sight, 1)));
        Assert.Equal(0, await Update(alice, (Sprout, 0)));
        Assert.Equal(1, Shown(skills, session, 0, Sprout));
    });

    // The report's second symptom: every untouched build has Sprout at its free level and Sight at 0, which came back 333.
    [Fact]
    public async Task ABuildSwitchesToAnUntouchedOne() => await InWorld(async (host, alice, session) =>
    {
        var skills = host.Service<SkillService>();
        Assert.Equal(0, await Update(alice, (Sight, 1)));
        Assert.Equal(1, Shown(skills, session, 1, Sprout));
        Assert.Equal(0, Shown(skills, session, 1, Sight));

        await alice.SendGameAsync(SkillService.SwitchPlan, new SwitchPlanReq { Plan = 1 });
        Assert.Equal(0, (await Next(alice, SkillService.SwitchPlan, SwitchPlanResp.Parser)).Result);
        Assert.Equal(1, session.SkillPlans!.CurrentPlanIndex);
        Assert.Equal(1, skills.Level(session.Progress!, Sprout));
        Assert.Equal(0, skills.Level(session.Progress!, Sight));
        Assert.Equal(0, await Update(alice, 1, (Bolt, 1)));
    });

    [Fact]
    public async Task AFreeLevelHoldsNothingUpAndPaidLevelsKeepTheirPrerequisites() => await InWorld(async (host, alice, session) =>
    {
        var skills = host.Service<SkillService>();
        Assert.Equal(0, await Update(alice, (Bolt, 1)));
        Assert.Equal(ErrorCodes.SkillPrerequisite, await Update(alice, (FireWall, 1)));
        Assert.Equal(0, await Update(alice, (Sight, 1), (FireWall, 1)));
        Assert.Equal(ErrorCodes.SkillPrerequisite, await Update(alice, (Sight, 0)));

        // The client never asks this of a skill without a prerequisite: nothing refunds it along with another one.
        Assert.Equal(ErrorCodes.Parameter, await Update(alice, (FreeWithoutPrerequisite, 0)));
        Assert.Equal(1, Shown(skills, session, 0, FreeWithoutPrerequisite));
    });

    private static int Shown(SkillService skills, GameSession session, int plan, int group) =>
        skills.Build(session).SkillAssignPlans[plan].SkillAssigns[group].SkillLevel;

    private static async Task InWorld(Func<SyntheticHost, BotClient, GameSession, Task> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-freelevel-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            await using var alice = await host.InWorldAsync("Alice");
            await test(host, alice, host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task<int> Update(BotClient bot, params (int Group, int Level)[] levels) => Update(bot, 0, levels);

    private static async Task<int> Update(BotClient bot, int plan, params (int Group, int Level)[] levels)
    {
        var request = new SkillGrowthSkillPlanUpdateReq { Plan = plan };
        foreach (var (group, level) in levels) request.LevelUpList[group] = new SkillData { SkillId = group, SkillLevel = level };
        await bot.SendGameAsync(SkillService.PlanUpdate, request);
        return (await Next(bot, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T>
    {
        var message = await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(5));
        return parser.ParseFrom(message.Body);
    }

    private static void PrepareData(string root)
    {
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.WriteAllText(Path.Combine(root, "config", "JobConfig.json"), """
            {"1000":{"_iID":1000,"_iJobRank":1}}
            """);
        File.WriteAllText(Path.Combine(root, "config", "JobLvConfig.json"), """
            {"101":{"_iID":101,"_iSkillPoint":10}}
            """);
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """
            {
              "99501":{"_iID":99501,"_iSkillID":995,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iBasicType":1},
              "99601":{"_iID":99601,"_iSkillID":996,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iInitialLevel":1,
                "_kPreSkillRequired":[[995,1]]},
              "99701":{"_iID":99701,"_iSkillID":997,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iBasicType":1,
                "_kPreSkillRequired":[[995,1]]},
              "99801":{"_iID":99801,"_iSkillID":998,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iInitialLevel":1},
              "99901":{"_iID":99901,"_iSkillID":999,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iBasicType":1}
            }
            """);
        File.WriteAllText(Path.Combine(root, "config", "SkillTreeConfig.json"), """
            {"1":{"_iID":1,"_iJob":1000,"_iX1":995,"_iX2":996,"_iX3":997,"_iX4":998,"_iX5":999}}
            """);
    }
}
