using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Tests;

/// <summary>C/D 19 (D8): a lot whose end passes while the server is down settles once after restart, by the
/// recovery sweep that starts with the server, and a second restart does not settle it again.</summary>
public sealed partial class AuctionTests
{
    [Fact]
    public async Task ALotThatEndsWhileTheServerIsDownSettlesOnceAfterRestart()
    {
        long id, aliceId, bobId, aliceFunds, bobFunds;
        await using (var alice = await InWorld("Alice"))
        await using (var bob = await InWorld("Bob"))
        {
            await Gm(alice, "!zeny 10000");
            await Gm(bob, "!zeny 10000");
            var row = Seed(AuctionType.Dungeon, alice, bob);
            id = row.Id;
            aliceId = (long)alice.Self!.Uuid; bobId = (long)bob.Self!.Uuid;
            Assert.Equal(0, (await Bid(alice, id, 800, BiddingType.Manual)).Result);
            Assert.Equal(0, (await Bid(bob, id, 1000, BiddingType.Manual)).Result);
            aliceFunds = Currency(alice); bobFunds = Currency(bob);
        }
        await RestartHostAsync(TimeSpan.FromMinutes(5)); // the lot ended 3 minutes into the downtime

        for (int i = 0; i < 100 && Service<AuctionStore>().Get(id)!.State != AuctionState.Settled; i++)
            await Task.Delay(50);
        Assert.Equal(AuctionState.Settled, Service<AuctionStore>().Get(id)!.State);
        Assert.Equal(bobId, Service<AuctionStore>().Get(id)!.Owner);
        Assert.Single(MailboxOf(bobId).Letters, m => m.DeliveryKey == $"auction:{id}:award");
        Assert.DoesNotContain(MailboxOf(aliceId).Letters, m => m.DeliveryKey == $"auction:{id}:award");

        await RestartHostAsync(TimeSpan.FromMinutes(1));
        await Task.Delay(1500); // more than one sweep
        Assert.Single(MailboxOf(bobId).Letters, m => m.DeliveryKey == $"auction:{id}:award");
        await using var returning = await InWorld("Bob", create: false);
        Assert.Equal(bobFunds, Currency(returning)); // the settled reserve is not charged or refunded twice
        await using var outbid = await InWorld("Alice", create: false);
        Assert.Equal(aliceFunds, Currency(outbid));
    }
}
