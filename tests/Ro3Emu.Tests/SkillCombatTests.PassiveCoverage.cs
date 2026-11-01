using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, 1010)]
    [InlineData(10, 1100)]
    public async Task MerchantPassiveAddsOnlyDismantleCoinsAndReplayCannotPayTwice(int level, int expected)
    {
        await using var bot = await InWorld("MerchantPassive");
        await Gm("merchantpassive", "!lvl 99"); await Gm("merchantpassive", "!job 6100");
        await Teach("merchantpassive", 16002, level); await Gm("merchantpassive", "!item 2010250");
        var s = Session("merchantpassive");
        var (guid, before) = await Loop.InvokeAsync(() => Task.FromResult((
            s.Items.Single(i => i.Entry == 2010250).Guid, s.Progress!.Currency.GetValueOrDefault(2))));
        var request = new ItemDecomposeReq { DecomposeItem = { [guid] = 1 } };
        await bot.SendGameAsync(EquipmentService.ItemDecompose, request);
        Assert.Equal(0, (await Next(bot, EquipmentService.ItemDecompose, ItemDecomposeResp.Parser)).Result);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(before + expected, s.Progress!.Currency.GetValueOrDefault(2));
            Assert.DoesNotContain(s.Items, i => i.Guid == guid);
            var persisted = _app.Services.GetRequiredService<RoleStore>().LoadProgress(s.Role!.Id);
            Assert.Equal(before + expected, persisted.Currency.GetValueOrDefault(2));
            var service = _app.Services.GetRequiredService<EquipmentService>();
            Assert.Equal(new[] { (2, (long)expected), (1, 1000L), (1200000, 5L) },
                service.MerchantReturns(s, [(2, 1000), (1, 1000), (1200000, 5)]));
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(EquipmentService.ItemDecompose, request);
        Assert.NotEqual(0, (await Next(bot, EquipmentService.ItemDecompose, ItemDecomposeResp.Parser)).Result);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(before + expected, s.Progress!.Currency.GetValueOrDefault(2));
            s.Progress.Skills.Remove(16002);
            Assert.Equal(new[] { (2, 1000L) }, _app.Services.GetRequiredService<EquipmentService>().MerchantReturns(s, [(2, 1000)]));
            return Task.CompletedTask;
        });
    }

    [ClientDataTheory]
    [InlineData(11102, 1200, 1)]
    [InlineData(11102, 1200, 5)]
    [InlineData(11402, 1210, 1)]
    [InlineData(11402, 1210, 10)]
    [InlineData(15212, 5300, 1)]
    [InlineData(15212, 5300, 5)]
    [InlineData(15213, 5300, 1)]
    [InlineData(15213, 5300, 5)]
    [InlineData(15219, 5300, 1)]
    [InlineData(15219, 5300, 10)]
    public async Task LegacyPassiveRowsOutsideThePairedCareerTreeNeverGrantEffects(int group, int career, int level)
    {
        await using var bot = await InWorld("LegacyPassive");
        await Gm("legacypassive", "!lvl 99"); await Gm("legacypassive", $"!job {career}");
        var s = Session("legacypassive");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var skills = _app.Services.GetRequiredService<SkillService>();
        var before = await Loop.InvokeAsync(() => Task.FromResult(combat.PassiveAttributes(s).ToArray()));
        await Teach("legacypassive", group, level); // 模擬舊存檔殘留，不改現行職業樹。
        await Loop.InvokeAsync(() =>
        {
            Assert.False(skills.InTree(career, group));
            Assert.Null(combat.LearnedPassive(s, group));
            Assert.Equal(before, combat.PassiveAttributes(s).ToArray());
            return Task.CompletedTask;
        });
    }
}
