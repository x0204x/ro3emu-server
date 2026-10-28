using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class TeamExpTests
{
    [Fact]
    public void FivePersonPartyUsesItsWholeRosterForExpBonus()
    {
        long[] members = [11, 12, 13, 14, 15];

        Assert.Equal((0, 5), TeamService.ExpGroupRange(TeamType.Five, members, 13));
    }

    [Fact]
    public void FortyPersonTeamUsesKillersFiveSlotSubgroup()
    {
        long[] members = Enumerable.Range(1, 12).Select(i => (long)i).ToArray();

        Assert.Equal((0, 5), TeamService.ExpGroupRange(TeamType.Forty, members, 1));
        Assert.Equal((0, 5), TeamService.ExpGroupRange(TeamType.Forty, members, 5));
        Assert.Equal((5, 5), TeamService.ExpGroupRange(TeamType.Forty, members, 6));
        Assert.Equal((5, 5), TeamService.ExpGroupRange(TeamType.Forty, members, 10));
        Assert.Equal((10, 2), TeamService.ExpGroupRange(TeamType.Forty, members, 11));
    }

    [Fact]
    public void FortyPersonTeamRejectsMemberOutsideRoster()
    {
        Assert.Equal((0, 0), TeamService.ExpGroupRange(TeamType.Forty, [1, 2, 3], 99));
    }
}
