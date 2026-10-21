using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class EmblemTests
{
    [ClientDataFact]
    public async Task SavedPlansSwitchItemsStatsAndNamesOverWireAndSurviveRelog()
    {
        long guid;
        await using (var bot = await InWorld("Alice"))
        {
            var initial = await Next(bot, EmblemService.PlanSync, DBEmblemInfoIncrement.Parser);
            Assert.Equal(5, initial.EmblePlans.Count);
            await bot.SendGameAsync(EmblemService.SwitchPlan, new EmbleSwitchPlanReq { TargetIndex = 1 });
            Assert.Equal(ErrorCodes.FunctionLocked, (await Next(bot, EmblemService.SwitchPlan, EmbleSwitchPlanResp.Parser)).Result);
            await Gm("alice", "!quest done 10103040");
            await Gm("alice", "!item 1830101");
            guid = Granted(await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.ContainsKey(5)), 1830101).ItemGuid;
            await bot.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = guid });
            Assert.Equal(0, (await Next(bot, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
            var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
            long wornHp = _app.Services.GetRequiredService<CharacterService>().Stats(session)[101];
            await bot.SendGameAsync(EmblemService.RenamePlan, new EmblemPlanChangeNameReq { TargetIndex = 0, Name = "Echo" });
            Assert.Equal(0, (await Next(bot, EmblemService.RenamePlan, EmblemPlanChangeNameResp.Parser)).Result);
            var renamed = await Next(bot, EmblemService.PlanSync, DBEmblemInfoIncrement.Parser,
                m => m.EmblePlans.TryGetValue(0, out var p) && p.Inc?.PlanName == "Echo");
            Assert.Equal("Echo", renamed.EmblePlans[0].Inc.PlanName);

            await bot.SendGameAsync(EmblemService.SwitchPlan, new EmbleSwitchPlanReq { TargetIndex = 1 });
            var switched = await Next(bot, EmblemService.SwitchPlan, EmbleSwitchPlanResp.Parser);
            Assert.Equal(0, switched.Result);
            Assert.Equal(1, switched.CurrentPlan);
            Assert.Equal(0, session.Items.Single(i => i.Guid == guid).Attrs![10001]);
            Assert.True(_app.Services.GetRequiredService<CharacterService>().Stats(session)[101] < wornHp);
            // Same-plan retry is idempotent; switching elsewhere during the shared SchemeCD is refused.
            await bot.SendGameAsync(EmblemService.SwitchPlan, new EmbleSwitchPlanReq { TargetIndex = 1 });
            Assert.Equal(0, (await Next(bot, EmblemService.SwitchPlan, EmbleSwitchPlanResp.Parser)).Result);
            await bot.SendGameAsync(EmblemService.SwitchPlan, new EmbleSwitchPlanReq { TargetIndex = 0 });
            Assert.Equal(ErrorCodes.Cooldown, (await Next(bot, EmblemService.SwitchPlan, EmbleSwitchPlanResp.Parser)).Result);
            session.EmblemPlanSwitchUtc = DateTime.UtcNow.AddSeconds(-4);
            await bot.SendGameAsync(EmblemService.SwitchPlan, new EmbleSwitchPlanReq { TargetIndex = 0 });
            Assert.Equal(0, (await Next(bot, EmblemService.SwitchPlan, EmbleSwitchPlanResp.Parser)).Result);
            Assert.Equal(wornHp, _app.Services.GetRequiredService<CharacterService>().Stats(session)[101]);
            Assert.Equal(2, session.Items.Single(i => i.Guid == guid).Attrs![10001]);
            await bot.SendGameAsync(EmblemService.RenamePlan, new EmblemPlanChangeNameReq { TargetIndex = 5, Name = "Invalid" });
            Assert.NotEqual(0, (await Next(bot, EmblemService.RenamePlan, EmblemPlanChangeNameResp.Parser)).Result);
            await bot.SendGameAsync(EmblemService.RenamePlan, new EmblemPlanChangeNameReq { TargetIndex = 0, Name = "TooLongName" });
            Assert.NotEqual(0, (await Next(bot, EmblemService.RenamePlan, EmblemPlanChangeNameResp.Parser)).Result);
        }
        await using var again = await InWorld("Alice", create: false);
        var saved = await Next(again, EmblemService.PlanSync, DBEmblemInfoIncrement.Parser);
        Assert.Equal(0, saved.CurrentPlan);
        Assert.Equal("Echo", saved.EmblePlans[0].All.PlanName);
        Assert.Equal(guid, saved.EmblePlans[0].All.SetUpEmblem[1]);
        var bag = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(2, bag.PackInfo[5].All.Items[guid].ItemInfos[10001].LongVal);
    }

    [ClientDataFact]
    public async Task PlanSwitchDatabaseFailureLeavesSelectionAndItemProjectionUntouched()
    {
        await using var bot = await InWorld("Alice");
        await Gm("alice", "!quest done 10103040");
        await Gm("alice", "!item 1830101");
        long guid = Granted(await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.ContainsKey(5)), 1830101).ItemGuid;
        await bot.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = guid });
        Assert.Equal(0, (await Next(bot, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
        var roles = _app.Services.GetRequiredService<RoleStore>();
        byte[] before = roles.LoadEmblems(session.Role!.Id)!;
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER fail_emblem_switch BEFORE UPDATE OF emblems ON role BEGIN SELECT RAISE(ABORT, 'claim audit fault'); END");
        await bot.SendGameAsync(EmblemService.SwitchPlan, new EmbleSwitchPlanReq { TargetIndex = 1 });
        Assert.NotEqual(0, (await Next(bot, EmblemService.SwitchPlan, EmbleSwitchPlanResp.Parser)).Result);
        Assert.Equal(0, session.Emblems.CurrentPlan);
        Assert.Equal(2, session.Items.Single(i => i.Guid == guid).Attrs![10001]);
        Assert.Equal(before, roles.LoadEmblems(session.Role.Id));
        Assert.Equal(2, _app.Services.GetRequiredService<ItemStore>().Load(session.Role.Id).Single(i => i.Guid == guid).Attrs![10001]);
    }
}
