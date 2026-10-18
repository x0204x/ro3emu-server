using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Romsg;

namespace Ro3Emu.Tests;

public sealed partial class AuctionTests
{
    private static TaskCompletionSource AuctionSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task DungeonRewardRestagesWhenParticipantProgressChangesDuringAdmission()
    {
        await using var alice = await InWorld("RollAlice");
        await Gm(alice, "!lvl 99");
        long role = (long)alice.Self!.Uuid;
        var roles = Service<RoleStore>();
        Assert.Equal(99, roles.LoadProgress(role).Level);
        var data = Service<GameData>();
        const long group = 900991001, award = 900991002, pool = 900991003, drop = 900991004;
        Assert.IsType<Dictionary<string, JsonElement>>(data.Table("MultiverseGroupConfig"))[group.ToString()] =
            JsonSerializer.SerializeToElement(new { _iValue_101 = award });
        Assert.IsType<Dictionary<string, JsonElement>>(data.Table("AwardConfig"))[award.ToString()] =
            JsonSerializer.SerializeToElement(new[] { new { _iDropPool = pool } });
        Assert.IsType<Dictionary<string, JsonElement>>(data.Table("DropPoolConfig"))[pool.ToString()] =
            JsonSerializer.SerializeToElement(new { _iDropGroup_1 = new long[] { drop, 1_000_000, 1, 1 } });
        Assert.IsType<Dictionary<string, JsonElement>>(data.Table("DropGroupConfig"))[drop.ToString()] =
            JsonSerializer.SerializeToElement(new[]
            {
                new { _iWeights = 1, _iMinLevelLimit = 1, _iMaxLevelLimit = 50, _iMinNumber = 1,
                    _iMaxNumber = 1, _kDropItem = Entry },
                new { _iWeights = 1, _iMinLevelLimit = 51, _iMaxLevelLimit = 100, _iMinNumber = 2,
                    _iMaxNumber = 2, _kDropItem = Entry },
            });
        Assert.Equal(2, Assert.Single(Service<RewardService>().RollAuctionAwardBound(award,
            roles.LoadProgress(role), 1)).Count);

        const long runId = 880991001;
        var clear = Service<AccountStore>().NewTransaction();
        Service<AuctionService>().RecordDungeonClear(clear, runId, role, 0, 5001, 101,
            new Dictionary<int, long> { [1] = group });
        clear.Commit(Service<AccountStore>(), "test/reward-restage-clear");

        var owners = Service<OwnerRegistry>();
        var loop = Service<GameLoop>();
        var entered = AuctionSignal(); var release = AuctionSignal(); var deferred = AuctionSignal();
        owners.DeferredForTest = origin => { if (origin == "auction/finalize-run") deferred.TrySetResult(); };
        var hold = loop.InvokeAsync(() => owners.RunAsync("test/hold-run",
            [ReservationKey.Entity("auction_run", runId.ToString())], async () =>
            {
                // Keep the ready write and its first eligible finalization under this same entity owner.
                Service<AuctionService>().MarkDungeonRunReady(runId);
                entered.TrySetResult();
                await release.Task;
            }));
        Task? finalize = null;
        try
        {
            await entered.Task.WaitAsync(Wait);
            finalize = Service<AuctionService>().FinalizeDungeonRunAsync(runId);
            await deferred.Task.WaitAsync(Wait);
            await loop.InvokeAsync(() => owners.RunAsync("test/level-before-auction-commit",
                [ReservationKey.Role(role)], () =>
                {
                    var progress = roles.LoadProgress(role);
                    progress.Level = 1;
                    roles.SaveProgress(role, progress);
                    return Task.CompletedTask;
                })).WaitAsync(Wait);
            Assert.Equal(1, Assert.Single(Service<RewardService>().RollAuctionAwardBound(award,
                roles.LoadProgress(role), 1)).Count);
            Assert.False(finalize.IsCompleted);
        }
        finally { owners.DeferredForTest = null; release.TrySetResult(); }
        await hold.WaitAsync(Wait);
        await finalize!.WaitAsync(Wait);
        Assert.Single(Service<AuctionService>().DungeonRows(runId));
        Assert.True(Service<AuctionStore>().GetRun(runId)!.Finalized);
    }

    [Fact]
    public async Task HeldBidDefersSettlementWithoutBlockingUnrelatedRole()
    {
        await using var alice = await InWorld("HoldAlice");
        await using var other = await InWorld("HoldOther");
        await Gm(alice, "!zeny 10000");
        var row = Seed(AuctionType.Dungeon, alice);
        long aliceBefore = Currency(alice);
        var accounts = Service<AccountStore>();
        var owners = Service<OwnerRegistry>();
        var loop = Service<GameLoop>();
        var entered = AuctionSignal(); var release = AuctionSignal(); var deferred = AuctionSignal();
        owners.DeferredForTest = origin => { if (origin == "auction/settle") deferred.TrySetResult(); };
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "character/exchange") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        var bid = Bid(alice, row.Id, 1000, BiddingType.Auto);
        Task? settle = null;
        try
        {
            await entered.Task.WaitAsync(Wait);
            Assert.Equal(aliceBefore, Currency(alice));
            Assert.Equal(0, Service<AuctionStore>().Get(row.Id)!.Owner);
            clock.Advance(TimeSpan.FromSeconds(121));
            settle = Service<AuctionService>().SettleAsync(row.Id);
            await deferred.Task.WaitAsync(Wait);
            long otherRole = (long)other.Self!.Uuid;
            await loop.InvokeAsync(() => owners.RunAsync("auction unrelated progress",
                [ReservationKey.Role(otherRole)], () =>
                {
                    var progress = Service<RoleStore>().LoadProgress(otherRole);
                    progress.Exp = 123;
                    Service<RoleStore>().SaveProgress(otherRole, progress);
                    return Task.CompletedTask;
                })).WaitAsync(Wait);
            Assert.Equal(123, Service<RoleStore>().LoadProgress(otherRole).Exp);
            Assert.False(settle.IsCompleted);
        }
        finally
        {
            accounts.BeforeOwnedCommitForTest = null;
            owners.DeferredForTest = null;
            release.TrySetResult();
        }
        Assert.Equal(0, (await bid.WaitAsync(Wait)).Result);
        await settle!.WaitAsync(Wait);
        var finished = Service<AuctionStore>().Get(row.Id)!;
        Assert.Equal(AuctionState.Settled, finished.State);
        Assert.Equal((long)alice.Self!.Uuid, finished.Winner);
        Assert.Equal(aliceBefore - 1000, Currency(alice));
        Assert.Single(Mailbox(alice).Letters, letter => letter.DeliveryKey == $"auction:{row.Id}:award");
    }

    [Fact]
    public async Task SlowNoticeDoesNotRetainBidParticipants()
    {
        await using var alice = await InWorld("NoteAlice");
        await using var bob = await InWorld("NoteBob");
        await Gm(alice, "!zeny 10000");
        await Gm(bob, "!zeny 10000");
        var row = Seed(AuctionType.Dungeon, alice, bob);
        var entered = AuctionSignal(); var release = AuctionSignal();
        int notices = 0;
        var auctions = Service<AuctionService>();
        auctions.BeforeNoticeForTest = () =>
        {
            if (Interlocked.Increment(ref notices) != 1) return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        var first = Bid(alice, row.Id, 1000, BiddingType.Manual);
        try
        {
            await entered.Task.WaitAsync(Wait);
            Assert.Equal((long)alice.Self!.Uuid, Service<AuctionStore>().Get(row.Id)!.Owner);
            Assert.Equal(0, (await Bid(bob, row.Id, 1200, BiddingType.Manual).WaitAsync(Wait)).Result);
            Assert.Equal((long)bob.Self!.Uuid, Service<AuctionStore>().Get(row.Id)!.Owner);
            Assert.False(first.IsCompleted);
        }
        finally { auctions.BeforeNoticeForTest = null; release.TrySetResult(); }
        Assert.Equal(0, (await first.WaitAsync(Wait)).Result);
        Assert.Single(Mailbox(alice).Letters, letter => letter.DeliveryKey.StartsWith($"auction:{row.Id}:outbid:"));
    }

    [Fact]
    public async Task PreStackWaitsForPublicEscrowApplyBeforeReadingPersonalCount()
    {
        await using var alice = await InWorld("StackAlice");
        await Gm(alice, "!zeny 10000");
        var row = Seed(AuctionType.Dungeon, alice);
        row.State = AuctionState.Public;
        row.Start = clock.GetUtcNow().ToUnixTimeSeconds() + 30;
        row.End = row.Start + 120;
        SaveRows(row);
        var accounts = Service<AccountStore>();
        var owners = Service<OwnerRegistry>();
        var entered = AuctionSignal(); var release = AuctionSignal(); var deferred = AuctionSignal();
        owners.DeferredForTest = origin => { if (origin == "auction/prestack-read") deferred.TrySetResult(); };
        accounts.BeforeOwnedApplyForTest = label =>
        {
            if (label != "character/exchange") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        var bid = Bid(alice, row.Id, 900, BiddingType.Auto);
        Task? query = null;
        try
        {
            await entered.Task.WaitAsync(Wait);
            Assert.Equal(0, Session(alice).Auction?.PreBuyNum.GetValueOrDefault("4:5001:1200000") ?? 0);
            // The transport serializes one socket's requests; invoke this independent reader
            // directly on the loop to exercise the owner fence instead of the socket queue.
            query = Service<GameLoop>().InvokeAsync(() => Service<AuctionService>().PreStackAsync(Session(alice),
                new PreAuctionItemStackQueryReq { AuctionType = AuctionType.Dungeon, SrcType = 5001 }));
            await deferred.Task.WaitAsync(Wait);
            Assert.DoesNotContain(alice.Inbox.Snapshot(), item => item.IsGame(AuctionService.PreStack));
        }
        finally { accounts.BeforeOwnedApplyForTest = null; owners.DeferredForTest = null; release.TrySetResult(); }
        Assert.Equal(0, (await bid.WaitAsync(Wait)).Result);
        await query!.WaitAsync(Wait);
        var stack = await Next(alice, AuctionService.PreStack, PreAuctionItemStackQueryRes.Parser);
        Assert.Equal(1, stack.PreItems[Entry].PreBuyNum);
    }

    [Fact]
    public async Task FailedFixedBuyLeavesEscrowAndMailUntouched()
    {
        await using var alice = await InWorld("FailAlice");
        await Gm(alice, "!zeny 10000");
        var row = Seed(AuctionType.Dungeon, alice);
        long before = Currency(alice);
        var accounts = Service<AccountStore>();
        FixtureSql.Execute(accounts, "CREATE TRIGGER fail_fixed_auction BEFORE UPDATE ON auction_item "
            + "BEGIN SELECT RAISE(ABORT, 'injected fixed buy conflict'); END");
        try
        {
            Assert.Equal(ErrorCodes.Unknown, (await Bid(alice, row.Id, 2000, BiddingType.Fixed)).Result);
            Assert.Equal(before, Currency(alice));
            Assert.Equal(0, Service<AuctionStore>().Get(row.Id)!.Owner);
            Assert.DoesNotContain(Mailbox(alice).Letters, letter => letter.DeliveryKey.StartsWith($"auction:{row.Id}:"));
        }
        finally { FixtureSql.Execute(accounts, "DROP TRIGGER fail_fixed_auction"); }
        Assert.Equal(0, (await Bid(alice, row.Id, 2000, BiddingType.Fixed)).Result);
        Assert.Equal(before - 2000, Currency(alice));
        Assert.Equal(AuctionState.Settled, Service<AuctionStore>().Get(row.Id)!.State);
    }

    [Fact]
    public async Task DeletedEscrowRecipientLeavesOneDurableRecoveryObligation()
    {
        var roles = Service<RoleStore>();
        var removed = roles.Create("goneauction", 1, "GoneRole", 1, 1000, 1)!;
        var row = new AuctionStore.Item
        {
            SourceKey = "test:deleted:" + Guid.NewGuid().ToString("N"),
            Type = AuctionType.Dungeon, SrcType = 5001, Entry = Entry,
            State = AuctionState.Bidding, Start = clock.GetUtcNow().ToUnixTimeSeconds() - 100,
            End = clock.GetUtcNow().ToUnixTimeSeconds() - 1,
            Owner = removed.Id, Winner = 0, Price = 500, Reserve = 1000,
            Currency = 1, Eligible = [removed.Id], Joined = [removed.Id],
        };
        SaveRows(row);
        Assert.True(roles.Delete("goneauction", removed.Id));
        await Service<AuctionService>().SettleAsync(row.Id);
        var recovery = Service<AuctionStore>().Get(row.Id)!;
        Assert.Equal(AuctionState.Bidding, recovery.State);
        Assert.Equal(removed.Id, recovery.Owner);
        Assert.Equal($"deleted-recipient:{removed.Id}", recovery.SettlementRecoveryReason);
        await Service<AuctionService>().SettleAsync(row.Id);
        Assert.Equal(recovery.Revision, Service<AuctionStore>().Get(row.Id)!.Revision);
    }
}
