using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

[Collection("Market server")]
public sealed class MarketCaptureTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-market-capture-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private AccountStore _accounts = null!;
    private RoleStore _roles = null!;
    private ItemStore _items = null!;
    private MailService _mail = null!;

    public Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        _accounts = _app.Services.GetRequiredService<AccountStore>();
        _roles = _app.Services.GetRequiredService<RoleStore>();
        _items = _app.Services.GetRequiredService<ItemStore>();
        _mail = _app.Services.GetRequiredService<MailService>();
        _app.Services.GetRequiredService<MarketStore>();
        return Task.CompletedTask;
    }

    private long Role(string name)
    {
        long id = _roles.Create("capture", 1, name, 1, 1000, 1)!.Id;
        var progress = _roles.LoadProgress(id);
        progress.Currency[1] = 100;
        _roles.SaveProgress(id, progress);
        return id;
    }
    private MarketTransaction Transaction() => new(_accounts, _mail, [], DateTimeOffset.UtcNow);

    [Fact]
    public void ParticipantHandoverRebindsApplyWithoutOverwritingAnotherSelectedRole()
    {
        long first = Role("OriginalOwner"), second = Role("OtherSelection");
        var oldSession = new GameSession(null!, null!) { Role = _roles.Get(first), Progress = _roles.LoadProgress(first) };
        var tx = new MarketTransaction(_accounts, _mail, [oldSession], DateTimeOffset.UtcNow);
        tx.Role(first).Currency[1] -= 23;
        // Selection/handover completed while the buyer was waiting for its all-or-none reservation.
        oldSession.Role = _roles.Get(second); oldSession.Progress = _roles.LoadProgress(second);
        var replacement = new GameSession(null!, null!) { Role = _roles.Get(first), Progress = _roles.LoadProgress(first) };
        tx.RebindLive([oldSession, replacement]);
        tx.Persist(); tx.Publish();
        Assert.Equal(100, oldSession.Progress.Currency[1]);
        Assert.Equal(100, _roles.LoadProgress(second).Currency[1]);
        Assert.Equal(77, replacement.Progress.Currency[1]);
        Assert.Equal(77, _roles.LoadProgress(first).Currency[1]);
    }

    [Fact]
    public void OfflineParticipantCommitsEscrowPaymentAndDistinctGuidsWithDetachedBlobs()
    {
        long role = Role("OfflineMarket");
        var owned = new InvItem { Entry = 1004000, Stack = 5, Pack = 1 };
        _items.Insert(role, owned);
        var tx = Transaction();
        var participant = tx.Role(role);
        Assert.Null(participant.Session);
        participant.Currency[1] -= 23;
        participant.Items[0].Stack = 2;
        participant.Items.Add(new() { Entry = 2010121, Stack = 1, Pack = 2, Attrs = new() { [30] = 17 } });
        participant.Items.Add(new() { Entry = 2010121, Stack = 1, Pack = 2, Attrs = new() { [30] = 19 } });
        byte[] blob = [2, 3];
        tx.SaveBlob(role, "trade_info", blob);
        blob[0] = 99;
        var stagedRead = tx.LoadBlob(role, "trade_info")!;
        stagedRead[0] = 88;
        tx.Put("capture", "sale", new { Role = role, Paid = 23 });
        tx.Persist();
        tx.Publish();

        Assert.Equal(77, _roles.LoadProgress(role).Currency[1]);
        var saved = _items.Load(role);
        Assert.Equal(3, saved.Count);
        Assert.Equal(2, saved.Single(x => x.Guid == owned.Guid).Stack);
        Assert.All(participant.Items, x => Assert.True(x.Guid > 0));
        Assert.Equal(3, participant.Items.Select(x => x.Guid).Distinct().Count());
        Assert.Equal(participant.Items.Select(x => x.Guid).Order(), saved.Select(x => x.Guid).Order());
        Assert.Equal(new long[] { 17, 19 }, saved.Where(x => x.Entry == 2010121).Select(x => x.Attrs![30]).Order());
        Assert.Equal(new byte[] { 2, 3 }, _roles.LoadTrade(role));
        Assert.Single(_accounts.ReadSync<List<(string Id, string Data)>>(new MarketRecordReadPlan("capture")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedRecordOrPhantomRejectsEntirePaymentItemAndMailCommit(bool phantom)
    {
        long role = Role("MarketConflict");
        _accounts.WriteLegacySync<int>(new MarketRecordWritePlan("capture", "existing", "1"));
        var tx = Transaction();
        if (phantom) Assert.Equal(new[] { 1 }, tx.All<int>("capture"));
        else Assert.Equal(1, tx.Get<int>("capture", "existing"));
        tx.Put("capture", "candidate", 2);
        var participant = tx.Role(role);
        participant.Currency[1] = 50;
        var candidate = new InvItem { Entry = 2010121, Stack = 1, Pack = 2 };
        participant.Items.Add(candidate);
        tx.DeliverMail(role, 1063, currency: new Dictionary<int, long> { [1] = 50 });
        byte[]? originalMail = _roles.LoadMailbox(role);
        _accounts.WriteLegacySync<int>(new MarketRecordWritePlan("capture", phantom ? "phantom" : "existing", "3"));

        Assert.ThrowsAny<InvalidOperationException>(tx.Persist);

        Assert.Equal(100, _roles.LoadProgress(role).Currency[1]);
        Assert.Empty(_items.Load(role));
        Assert.Equal(0, candidate.Guid);
        Assert.Equal(originalMail, _roles.LoadMailbox(role));
        var rows = _accounts.ReadSync<List<(string Id, string Data)>>(new MarketRecordReadPlan("capture"));
        Assert.DoesNotContain(rows, x => x.Id == "candidate");
        Assert.Contains(rows, x => x.Data == "3");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfflineParticipantSourceChangeCannotBeOverwritten(bool changeItem)
    {
        long role = Role("ParticipantConflict");
        var item = new InvItem { Entry = 1004000, Stack = 5, Pack = 1 };
        _items.Insert(role, item);
        var tx = Transaction();
        var participant = tx.Role(role);
        participant.Currency[1] = 40;
        participant.Items[0].Stack = 2;
        tx.Put("capture", "must-rollback", 1);
        if (changeItem) { item.Stack = 4; _items.Update(item); }
        else
        {
            var progress = _roles.LoadProgress(role);
            progress.Currency[1] = 90;
            _roles.SaveProgress(role, progress);
        }

        Assert.ThrowsAny<InvalidOperationException>(tx.Persist);

        Assert.Equal(changeItem ? 100 : 90, _roles.LoadProgress(role).Currency[1]);
        Assert.Equal(changeItem ? 4 : 5, Assert.Single(_items.Load(role)).Stack);
        Assert.Empty(_accounts.ReadSync<List<(string Id, string Data)>>(new MarketRecordReadPlan("capture")));
    }

    [Fact]
    public void ChangedMailboxRejectsAllEarlierStagedWritesAndKeepsConcurrentDelivery()
    {
        long role = Role("MailboxConflict");
        _roles.SaveMailbox(role, [1]);
        var builder = new RoleItemPlanBuilder(_accounts);
        builder.Add(new MarketRecordWritePlan("capture", "must-rollback", "1"));
        Assert.Equal(new byte[] { 1 }, builder.ReadRoleBlob(_accounts, role, "mailbox"));
        builder.RoleBlob(role, "mailbox", [2]);
        _roles.SaveMailbox(role, [3]);

        Assert.ThrowsAny<InvalidOperationException>(() => builder.Commit(_accounts, "test/mailbox-conflict"));

        Assert.Equal(new byte[] { 3 }, _roles.LoadMailbox(role));
        Assert.Empty(_accounts.ReadSync<List<(string Id, string Data)>>(new MarketRecordReadPlan("capture")));
    }

    [Fact]
    public void RecordOverlayPreservesOrderedWritesAndPrefixGuardsKeepReceiptIndices()
    {
        var tx = Transaction();
        tx.Put("capture", "b", 1);
        tx.Put("capture", "a", 2);
        Assert.Equal(new[] { 2, 1 }, tx.All<int>("capture"));
        tx.Delete("capture", "b");
        tx.Put("capture", "a", 3);
        Assert.Equal(new[] { 3 }, tx.All<int>("capture"));
        tx.Persist();
        var row = Assert.Single(_accounts.ReadSync<List<(string Id, string Data)>>(new MarketRecordReadPlan("capture")));
        Assert.Equal(("a", "3"), row);

        long role = Role("BlobOverlay");
        _roles.SaveMailbox(role, [1]);
        var builder = new RoleItemPlanBuilder(_accounts);
        int slot = builder.Add(new MarketRecordWritePlan("capture", "receipt", "4"));
        Assert.Equal(new byte[] { 1 }, builder.ReadRoleBlob(_accounts, role, "mailbox"));
        byte[] value = [2];
        builder.RoleBlob(role, "mailbox", value);
        value[0] = 99;
        Assert.Equal(new byte[] { 2 }, builder.ReadRoleBlob(_accounts, role, "mailbox"));
        builder.RoleBlob(role, "mailbox", [3]);
        Assert.Equal(1, (int)builder.Commit(_accounts, "test/overlay")[slot]!);
        Assert.Equal(new byte[] { 3 }, _roles.LoadMailbox(role));
    }

    [Fact]
    public void RepeatedNewItemReferenceKeepsOneRowAndDistinctNewInstancesKeepDistinctGuids()
    {
        long role = Role("RepeatedMarketItem");
        var tx = Transaction();
        var participant = tx.Role(role);
        var repeated = new InvItem { Entry = 2010121, Stack = 1, Pack = 2 };
        var distinct = new InvItem { Entry = 2010121, Stack = 1, Pack = 2 };
        participant.Items.AddRange([repeated, distinct, repeated]);
        tx.Persist();

        var saved = _items.Load(role);
        Assert.Equal(2, saved.Count);
        Assert.True(repeated.Guid > 0);
        Assert.True(distinct.Guid > 0);
        Assert.NotEqual(repeated.Guid, distinct.Guid);
        Assert.Equal(new[] { repeated.Guid, distinct.Guid }.Order(), saved.Select(i => i.Guid).Order());
        Assert.Equal(participant.Items[0].Guid, participant.Items[2].Guid);
    }

    [Fact]
    public void InterleavedReferencesToOneExistingGuidKeepTheOriginalWriteOrder()
    {
        long role = Role("OrderedMarketItem");
        var original = new InvItem { Entry = 1004000, Stack = 5, Pack = 1 };
        _items.Insert(role, original);
        var tx = Transaction();
        var participant = tx.Role(role);
        var first = participant.Items[0];
        first.Stack = 1;
        var second = first.Clone();
        second.Stack = 2;
        participant.Items.AddRange([second, first]);
        tx.Persist();

        var saved = Assert.Single(_items.Load(role));
        Assert.Equal(original.Guid, saved.Guid);
        Assert.Equal(1, saved.Stack);
        Assert.All(participant.Items, item => Assert.Equal(original.Guid, item.Guid));
    }

    [Fact]
    public void CapturedItemExecutorCopiesNestedFieldsAndAppliesIdsOnlyAfterCommit()
    {
        long role = Role("ItemSnapshot");
        var candidate = new InvItem
        {
            Entry = 2010121, Stack = 1, Pack = 2,
            Attrs = new() { [30] = 17 }, Enchantments = new() { [7] = new() { Attr = new() { [8] = 9 } } }
        };
        var builder = new RoleItemPlanBuilder(_accounts);
        int slot = builder.Add(new MarketItemsWritePlan(role, [], [candidate]));
        candidate.Attrs[30] = 99;
        candidate.Enchantments[7].Attr[8] = 99;
        long guid = Assert.Single((long[])builder.Commit(_accounts, "test/market-copy")[slot]!);
        var saved = Assert.Single(_items.Load(role));
        Assert.Equal(guid, saved.Guid);
        Assert.Equal(0, candidate.Guid); // Executor has no alias; the market caller applies its receipt.
        Assert.Equal(17, saved.Attrs![30]);
        Assert.Equal(9, saved.Enchantments[7].Attr[8]);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
