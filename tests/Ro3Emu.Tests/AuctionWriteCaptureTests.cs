using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Romsg;

namespace Ro3Emu.Tests;

public sealed class AuctionWriteCaptureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-auction-capture-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly AuctionStore _auctions;
    private readonly RoleStore _roles;

    public AuctionWriteCaptureTests()
    {
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new RoleStore(_accounts);
        _auctions = new AuctionStore(_accounts);
        foreach (long role in new long[] { 11, 12 })
            FixtureSql.Execute(_accounts, """
                INSERT INTO role(id,account,area,name,sex,career,level,hair,create_time,last_login_time)
                VALUES($id,'auction-capture',1,$name,1,1000,1,1,0,0)
                """, ("$id", role), ("$name", $"AuctionCapture{role}"));
        _accounts.WriteSync<int>(new RoleSchemaPlan());
    }

    private long Role(string name) => _roles.Create("capture", 1, name, 1, 1000, 1)!.Id;

    private AuctionStore.Item Seed(string key)
    {
        var item = new AuctionStore.Item
        {
            SourceKey = key, Type = AuctionType.Dungeon, Entry = 1200000,
            Price = 500, Eligible = [11, 12], State = AuctionState.Bidding,
        };
        var builder = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistSave(item, builder);
        builder.Commit(_accounts, "test/auction-seed");
        return item;
    }

    [Fact]
    public void ItemPlanCapturesNestedStateAndReturnsSourceBytesOnlyAfterCommit()
    {
        var item = new AuctionStore.Item
        {
            SourceKey = "captured", Type = AuctionType.Dungeon, Entry = 1200000,
            Eligible = [11], Price = 500,
        };
        var builder = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistSave(item, builder);
        item.Eligible.Add(99);
        item.Price = 900;
        Assert.Equal(0, item.Id);
        Assert.Null(item.OriginalBytes);

        builder.Commit(_accounts, "test/auction-capture");

        Assert.True(item.Id > 0);
        Assert.NotNull(item.OriginalBytes);
        var persisted = _auctions.Get(item.Id)!;
        Assert.Equal(500, persisted.Price);
        Assert.Equal(new long[] { 11 }, persisted.Eligible);
        Assert.Equal(item.OriginalBytes, persisted.OriginalBytes);
    }

    [Fact]
    public void StaleLotRejectsWholePaymentAndMailboxPlan()
    {
        long recipient = Role("AuctionBidder");
        var initial = Seed("lot");
        var stale = _auctions.Get(initial.Id)!;
        stale.Owner = recipient;
        stale.Reserve = 900;
        var builder = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistBonus(recipient, "payment", 900, 0, builder);
        builder.RoleBlob(recipient, "mailbox", [5]);
        _auctions.EnlistSave(stale, builder);

        var winner = _auctions.Get(initial.Id)!;
        winner.Owner = 12;
        winner.Reserve = 1000;
        var concurrent = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistSave(winner, concurrent);
        concurrent.Commit(_accounts, "test/auction-concurrent-bid");

        Assert.ThrowsAny<RolePlanConflictException>(() => builder.Commit(_accounts, "test/auction-stale-bid"));
        Assert.Equal(12, _auctions.Get(initial.Id)!.Owner);
        Assert.Empty(_auctions.Bonuses(recipient));
        Assert.Null(_roles.LoadMailbox(recipient));
    }

    [Fact]
    public void AuctionRowRevisionRejectsAbaDespiteIdenticalSourceBytes()
    {
        var item = Seed("aba-lot");
        var stale = _auctions.Get(item.Id)!;
        stale.Price = 700;
        var pending = _accounts.NewTransaction();
        _auctions.EnlistSave(stale, pending);

        var sameBytes = _auctions.Get(item.Id)!;
        var intervening = _accounts.NewTransaction();
        _auctions.EnlistSave(sameBytes, intervening);
        intervening.Commit(_accounts, "test/auction-aba-intervening");

        Assert.Throws<OwnerRevisionConflictException>(() => pending.Commit(_accounts, "test/auction-aba-stale"));
        Assert.Equal(500, _auctions.Get(item.Id)!.Price);
    }

    [Fact]
    public void StalePrebuyRejectsWholeLotAndMailPlan()
    {
        long recipient = Role("PrebuyBidder");
        var item = Seed("prebuy-lot");
        var insert = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistPrebuy(new AuctionStore.Prebuy(0, recipient, AuctionType.Dungeon, 5001,
            1200000, 1, 900, 1, 1, false, item.Id, false), insert);
        insert.Commit(_accounts, "test/prebuy-seed");
        var stale = Assert.Single(_auctions.Prebuys());
        var builder = new RoleItemPlanBuilder(_accounts);
        var lot = _auctions.Get(item.Id)!;
        lot.Owner = recipient;
        _auctions.EnlistSave(lot, builder);
        builder.RoleBlob(recipient, "mailbox", [7]);
        _auctions.EnlistPrebuy(stale with { Processed = true }, builder);

        var concurrent = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistPrebuy(stale with { Paused = true }, concurrent);
        concurrent.Commit(_accounts, "test/prebuy-concurrent-pause");

        Assert.ThrowsAny<RolePlanConflictException>(() => builder.Commit(_accounts, "test/prebuy-stale-settle"));
        Assert.Equal(0, _auctions.Get(item.Id)!.Owner);
        Assert.True(Assert.Single(_auctions.Prebuys()).Paused);
        Assert.Null(_roles.LoadMailbox(recipient));
    }

    [Fact]
    public void DuplicateSourceAndStaleRunRollbackEarlierAuctionRows()
    {
        var source = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistSource("guild:once", source);
        source.Commit(_accounts, "test/auction-source");
        var candidate = new AuctionStore.Item { SourceKey = "candidate", Type = AuctionType.Guild };
        var duplicate = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistSave(candidate, duplicate);
        _auctions.EnlistSource("guild:once", duplicate);
        Assert.Throws<AuctionSourceDuplicateException>(() => duplicate.Commit(_accounts, "test/auction-duplicate"));
        Assert.Equal(0, candidate.Id);
        Assert.Empty(_auctions.Items());

        var run = new AuctionStore.Run { Id = 201, Eligible = [11] };
        var start = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistRunSave(run, null, start);
        start.Commit(_accounts, "test/run-seed");
        var stale = _auctions.GetRunSnapshot(run.Id);
        stale.Run!.Ready = true;
        var pending = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistSave(candidate, pending);
        _auctions.EnlistRunSave(stale.Run, stale.Raw, pending);
        var newer = _auctions.GetRunSnapshot(run.Id);
        newer.Run!.Eligible.Add(12);
        var concurrent = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistRunSave(newer.Run, newer.Raw, concurrent);
        concurrent.Commit(_accounts, "test/run-concurrent");

        Assert.ThrowsAny<RolePlanConflictException>(() => pending.Commit(_accounts, "test/run-stale"));
        Assert.Equal(0, candidate.Id);
        Assert.Empty(_auctions.Items());
        Assert.Equal(new long[] { 11, 12 }, _auctions.GetRun(run.Id)!.Eligible);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
