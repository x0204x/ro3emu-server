using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Invented jobs and skills exercise the client's dedicated Lineup slot without client data.</summary>
public sealed class LineupWheelTests
{
    private const int QueueGroup = 991, QueueRow = 99101, BuffGroup = 992, OtherBuffGroup = 993, AdvancedQueueGroup = 994;
    private const int QueueSlot = (int)SkillWheelSlotType.SkillWheelSkillQueue;

    [Fact]
    public async Task QueueSlotAppearsOverWireSurvivesRestartAndClearsWhenUnloaded()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-lineup-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            await using (var alice = await host.InWorldAsync("Alice"))
            {
                var fresh = await Next<DBSkillDataIncrement>(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
                Assert.Contains(QueueGroup, fresh.SkillList.All.Value);
                Assert.False(fresh.SkillWheel.All.WhellSkills.ContainsKey(QueueSlot));
                await UpdateQueue(alice, SkillLoadOperation.SkillLoadOpLoad);
                var loaded = await Next<DBSkillDataIncrement>(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
                Assert.Equal(QueueRow, loaded.SkillWheel.Inc.WhellSkills[QueueSlot].All.SkillConfigId);
                Assert.True(loaded.SkillWheel.Inc.WhellSkills[QueueSlot].All.Enable);
                Assert.Equal(QueueRow, loaded.OriginSkillWheel.Inc.WhellSkills[QueueSlot].All.SkillConfigId);
            }
            await host.RestartAsync();
            await using var again = await host.InWorldAsync("Alice", create: false);
            var restored = await Next<DBSkillDataIncrement>(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
            Assert.Equal(BuffGroup, restored.SkillAssignPlans.All.Value[0].SkillQueue[0].SkillId);
            Assert.Equal(QueueRow, restored.SkillWheel.All.WhellSkills[QueueSlot].SkillConfigId);
            Assert.Equal(QueueRow, restored.OriginSkillWheel.All.WhellSkills[QueueSlot].SkillConfigId);
            // A regular skill remains in its own slot; Lineup consumes none of the six manual positions.
            Assert.Equal(99201, restored.SkillWheel.All.WhellSkills[1].SkillConfigId);
            await UpdateQueue(again, SkillLoadOperation.SkillLoadOpUnload);
            var cleared = await Next<DBSkillDataIncrement>(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
            Assert.True(cleared.SkillWheel.Inc.WhellSkills[QueueSlot].Del);
            Assert.True(cleared.OriginSkillWheel.Inc.WhellSkills[QueueSlot].Del);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The Lineup tab sends only Load and Unload (UI_Sys_Skill_QueueWidgetBaseCtrl._onProgressDragItem): a drop on a taken
    /// slot loads over it, a swap is two Loads, and the queue keeps no emptied entry, which the Skill Bar button draws as
    /// a dot (_refreshQueuePointNum counts every entry).
    /// </summary>
    [Fact]
    public async Task TabDropsLoadOverMoveSkillsAndLeaveNoEmptySlots()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-lineup-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            await using var alice = await host.InWorldAsync("Alice");
            async Task<string> Queue() => await host.Service<GameLoop>().InvokeAsync(() =>
            {
                var plans = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid).SkillPlans!;
                return Task.FromResult(string.Join(",", plans.SkillAssignPlans[plans.CurrentPlanIndex].SkillQueue
                    .OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value.SkillId}")));
            });

            await UpdateQueue(alice, SkillLoadOperation.SkillLoadOpLoad, BuffGroup, 0);
            await UpdateQueue(alice, SkillLoadOperation.SkillLoadOpLoad, OtherBuffGroup, 2);
            Assert.Equal("0:992,2:993", await Queue());
            await UpdateQueue(alice, SkillLoadOperation.SkillLoadOpLoad, OtherBuffGroup, 0); // over 992, out of slot 2
            Assert.Equal("0:993", await Queue());
            await UpdateQueue(alice, SkillLoadOperation.SkillLoadOpLoad, BuffGroup, 1);
            // A swap of slots 1 and 0: the dragged 992 onto slot 0, then the 993 it covered back to slot 1.
            await UpdateQueue(alice, SkillLoadOperation.SkillLoadOpLoad, BuffGroup, 0);
            await UpdateQueue(alice, SkillLoadOperation.SkillLoadOpLoad, OtherBuffGroup, 1);
            Assert.Equal("0:992,1:993", await Queue());
            await UpdateQueue(alice, SkillLoadOperation.SkillLoadOpUnload, BuffGroup, 0);
            Assert.Equal("1:993", await Queue());
            await UpdateQueue(alice, SkillLoadOperation.SkillLoadOpUnload, OtherBuffGroup, 1);
            Assert.Equal("0:0", await Queue()); // the empty slot 0 the client pairs()
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// A job advance changes the Lineup skill (Swordman 11399, Knight 11430): the earlier one stays at its free level 1 in
    /// every saved plan, and a plan switch refused it as a skill outside the tree.
    /// </summary>
    [Fact]
    public async Task AJobAdvanceKeepsEveryPlanValid()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-lineup-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            await using var alice = await host.InWorldAsync("Alice");
            await host.Service<GameLoop>().InvokeAsync(() =>
            {
                var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
                var skills = host.Service<SkillService>();
                Assert.All(session.SkillPlans!.SkillAssignPlans, plan => Assert.Equal(1, plan.SkillAssigns[QueueGroup].SkillLevel));
                session.Progress!.Career = 1100;
                var state = skills.Build(session);
                Assert.Equal(AdvancedQueueGroup, skills.QueueGroup(1100));
                Assert.Contains(AdvancedQueueGroup, state.SkillList);
                Assert.DoesNotContain(QueueGroup, state.SkillList);
                for (int plan = 0; plan < state.SkillAssignPlans.Count; plan++)
                    Assert.Equal(0, skills.StageSkillPlan(session, state.Clone(), CharacterService.GenreCloneProgress(session.Progress), plan));
                session.Progress.Career = 1000;
                return Task.CompletedTask;
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task UpdateQueue(BotClient bot, SkillLoadOperation operation) => UpdateQueue(bot, operation, BuffGroup, 0);

    private static async Task UpdateQueue(BotClient bot, SkillLoadOperation operation, int group, int index)
    {
        await bot.SendGameAsync(SkillService.QueueUpdate, new SkillQueueUpdateReq
        {
            Plan = 0, QueueIndex = index, OpType = operation,
            Info = new SkillData { SkillId = group, SkillLevel = 1 }
        });
        Assert.Equal(0, (await Next<SkillQueueUpdateResp>(bot, SkillService.QueueUpdate, SkillQueueUpdateResp.Parser)).Result);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T>
    {
        var message = await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(8));
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
            {"1000":{"_iID":1000,"_iJobRank":1,"_iQueueSkillId":99101},
             "1100":{"_iID":1100,"_iJobRank":2,"_iParentId":1000,"_iQueueSkillId":99401}}
            """);
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), """
            {
              "99101":{"_iID":99101,"_iSkillID":991,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iBasicType":1,
                "_iInitialLevel":1,"_iSystemType":6},
              "99201":{"_iID":99201,"_iSkillID":992,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,
                "_iBasicType":1,"_iInitialLevel":1,"_iQueue":1},
              "99301":{"_iID":99301,"_iSkillID":993,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,
                "_iBasicType":1,"_iInitialLevel":1,"_iQueue":1},
              "99401":{"_iID":99401,"_iSkillID":994,"_iLevel":1,"_iMaxLevel":1,"_iMode":0,"_iBasicType":1,
                "_iInitialLevel":1,"_iSystemType":6}
            }
            """);
        File.WriteAllText(Path.Combine(root, "config", "SkillTreeConfig.json"), """
            {"1":{"_iID":1,"_iJob":1000,"_iX1":992,"_iX2":993}}
            """);
    }
}
