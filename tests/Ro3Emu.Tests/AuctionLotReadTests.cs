using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Romsg;

namespace Ro3Emu.Tests;

/// <summary>#254: auction reads filter on indexed copies of the lot state instead of decoding every lot ever made.</summary>
public sealed class AuctionLotReadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-auction-reads-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly AuctionStore _auctions;

    public AuctionLotReadTests()
    {
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory, GameDataDir = SyntheticData.Dir }));
        _auctions = new AuctionStore(_accounts);
    }

    private AuctionStore.Item Save(AuctionStore.Item item)
    {
        var builder = new RoleItemPlanBuilder(_accounts);
        _auctions.EnlistSave(item, builder);
        builder.Commit(_accounts, "test/auction-read");
        return item;
    }

    private (AuctionStore.Item Open, AuctionStore.Item Settled, AuctionStore.Item Run) Seed()
    {
        var open = Save(new() { SourceKey = "open", Type = AuctionType.World, Entry = 1, End = 100 });
        var settled = Save(new() { SourceKey = "settled", Type = AuctionType.World, Entry = 2, End = 50 });
        settled.State = AuctionState.Settled;
        Save(settled); // settles a lot that was written open
        var run = Save(new() { SourceKey = "run", Type = AuctionType.Dungeon, Entry = 3, RunId = 77, End = 200 });
        return (open, settled, run);
    }

    private static long[] Ids(IEnumerable<AuctionStore.Item> items) => [.. items.Select(item => item.Id).Order()];

    private void AssertReads(AuctionStore.Item open, AuctionStore.Item settled, AuctionStore.Item run)
    {
        Assert.Equal(Ids([open, run]), Ids(_auctions.OpenItems()));
        Assert.Equal([settled.Id], Ids(_auctions.SettledItems(AuctionType.World)));
        Assert.Empty(_auctions.SettledItems(AuctionType.Dungeon));
        Assert.Equal(Ids([open, settled]), Ids(_auctions.ItemsOfType(AuctionType.World)));
        Assert.Equal([run.Id], Ids(_auctions.RunItems(77)));
        Assert.Equal(Ids([open, run]), Ids(_auctions.ItemsById([run.Id, open.Id, 999_999])));
        Assert.Empty(_auctions.ItemsById([]));
    }

    [Fact]
    public void ReadsSelectByStateTypeRunAndIdWithTheLotRevision()
    {
        var (open, settled, run) = Seed();
        AssertReads(open, settled, run);

        // The joined revision is the one a lot's next write expects: an update read back from OpenItems commits.
        var reread = _auctions.OpenItems().Single(item => item.Id == open.Id);
        Assert.True(reread.SourceRevision is { Exists: true, Revision: > 0 });
        reread.Price = 700;
        Save(reread);
        Assert.Equal(700, Assert.IsType<AuctionStore.Item>(_auctions.Get(open.Id)).Price);
    }

    [Fact]
    public void ADatabaseFromBeforeTheColumnsIsBackfilledFromTheLotState()
    {
        var (open, settled, run) = Seed();
        RemoveIndexColumns();

        _accounts.WriteSync<int>(new AuctionSchemaPlan());

        AssertReads(open, settled, run);
    }

    [Fact]
    public void InvalidLegacyStateRollsBackTheMigrationAndCanBeRepaired()
    {
        var (open, settled, run) = Seed();
        RemoveIndexColumns();
        FixtureSql.Execute(_accounts, "UPDATE auction_item SET state=$state WHERE id=$id",
            ("$state", System.Text.Encoding.UTF8.GetBytes("null")), ("$id", open.Id));

        Assert.Throws<InvalidDataException>(() => _accounts.WriteSync<int>(new AuctionSchemaPlan()));

        FixtureSql.Execute(_accounts, "UPDATE auction_item SET state=$state WHERE id=$id",
            ("$state", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(open)), ("$id", open.Id));
        _accounts.WriteSync<int>(new AuctionSchemaPlan());
        AssertReads(open, settled, run);
    }

    private void RemoveIndexColumns() =>
        FixtureSql.Execute(_accounts, """
            DROP INDEX auction_item_open; DROP INDEX auction_item_run;
            ALTER TABLE auction_item DROP COLUMN settled; ALTER TABLE auction_item DROP COLUMN auction_type;
            ALTER TABLE auction_item DROP COLUMN run_id; ALTER TABLE auction_item DROP COLUMN end_time;
            """);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
