using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>DropGroupConfig._iQuestLimit: a quest drop (the fixture's group 9603, quest 9900001) on the synthetic data.</summary>
public sealed class QuestDropRulesTests
{
    [Fact]
    public async Task AQuestDropRowDropsOnlyWhileItsQuestIsUnderWay()
    {
        await using var host = await SyntheticHost.StartAsync();
        var rewards = host.Service<RewardService>();
        var progress = new RoleProgress { Career = 1000, Level = 10 };
        List<(int, int)> Roll(Func<long, bool>? questActive)
        {
            var drops = new List<(int, int)>();
            rewards.PickFromGroup(9603, progress, drops, questActive: questActive);
            return drops;
        }
        Assert.Empty(Roll(null)); // an award or AFK stage roll knows no quest: never
        Assert.Empty(Roll(quest => quest != 9900001)); // another quest under way
        Assert.Equal([(3990002, 1)], Roll(quest => quest == 9900001));
    }
}
