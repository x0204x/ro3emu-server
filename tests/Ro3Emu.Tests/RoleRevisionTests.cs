using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class RoleRevisionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-revisions-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;

    public RoleRevisionTests()
    {
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));
        _roles = new RoleStore(_accounts);
        _items = new ItemStore(_accounts);
    }

    private long Create(string name) => _roles.Create("account", 1, name, 1, 1000, 1)!.Id;

    [Fact]
    public void MigrationBackfillsExistingRolesAndCreationPopulatesEveryPartImmediately()
    {
        long old = Create("BeforeMigration");
        _accounts.Dispose();
        using (var db = new SqliteConnection($"Data Source={Path.Combine(_dir, "ro3emu.db")}"))
        {
            db.Open();
            using var remove = db.CreateCommand();
            remove.CommandText = "DELETE FROM owner_revision; DELETE FROM role_lifecycle";
            remove.ExecuteNonQuery();
        }
        using var restarted = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));
        var roles = new RoleStore(restarted);
        var backfilled = roles.ReadRevisionVector(old);
        Assert.Equal(1, backfilled.Epoch);
        Assert.False(backfilled.Deleted);
        Assert.Equal(0, backfilled.Revision(RevisionStore.Progress));
        Assert.Equal(0, backfilled.Revision("settings"));
        long created = roles.Create("account", 1, "AfterMigration", 1, 1000, 1)!.Id;
        var fresh = roles.ReadRevisionVector(created);
        Assert.Equal(1, fresh.Epoch);
        Assert.False(fresh.Deleted);
        foreach (string part in RoleColumns.Blobs.Concat(RevisionStore.AdditionalParts))
            Assert.Equal(0, fresh.Revision(part));
    }

    [Fact]
    public void FailedRevisionInsertRollsBackNewRoleAndLifecycleTogether()
    {
        FixtureSql.Execute(_accounts, """
            CREATE TRIGGER fail_new_role_revision BEFORE INSERT ON owner_revision
            WHEN NEW.kind='role' AND NEW.part='settings'
            BEGIN SELECT RAISE(ABORT, 'test revision insert failure'); END
            """);
        Assert.Throws<SqliteException>(() => _roles.Create("account", 1, "RolledBack", 1, 1000, 1));
        Assert.Null(_roles.FindByName("RolledBack"));
        FixtureSql.Execute(_accounts, "DROP TRIGGER fail_new_role_revision");
        long created = Create("RolledBack");
        Assert.Equal(1, _roles.ReadRevisionVector(created).Epoch);
    }

    [Fact]
    public void BuilderBindsFreshProgressWithoutRelabellingEarlierStagedParts()
    {
        long role = Create("FreshCompositePart");
        var tx = _roles.NewTransaction();
        tx.RoleBlob(role, "settings", [1]);
        var fresh = _roles.LoadProgress(role);
        fresh.Exp = 5;
        _roles.SaveProgress(role, fresh);
        fresh.Exp = 9;
        tx.Progress(role, fresh);
        _roles.Commit(tx, "test/disjoint-composite");
        Assert.Equal(9, _roles.LoadProgress(role).Exp);
        Assert.Equal(new byte[] { 1 }, _roles.LoadSettings(role));

        var stale = _roles.LoadProgress(role);
        var staged = _roles.NewTransaction();
        staged.Progress(role, stale);
        var newer = _roles.LoadProgress(role);
        newer.Exp = 11;
        _roles.SaveProgress(role, newer);
        Assert.Throws<OwnerRevisionConflictException>(() => staged.Progress(role, newer));
    }

    [Fact]
    public void StaleProgressAndSettingsPlansFailButDisjointProgressSurvivesSettingsWrite()
    {
        long role = Create("DisjointParts");
        var captured = _roles.ReadRevisionVector(role);
        var first = _roles.LoadProgress(role);
        first.Exp = 17;
        var stale = new RoleProgressWritePlan(role, first);
        stale.Expect([captured]);
        var current = _roles.LoadProgress(role);
        current.Exp = 29;
        _roles.SaveProgress(role, current);
        Assert.ThrowsAny<RolePlanConflictException>(() => _accounts.WriteSync<int>(stale));
        Assert.Equal(29, _roles.LoadProgress(role).Exp);

        var settingsVector = _roles.ReadRevisionVector(role);
        var staleSettings = new RoleBlobWritePlan(role, "settings", [1]);
        staleSettings.Expect([settingsVector]);
        _roles.SaveSettings(role, [2]);
        Assert.ThrowsAny<RolePlanConflictException>(() => _accounts.WriteSync<int>(staleSettings));
        Assert.Equal(new byte[] { 2 }, _roles.LoadSettings(role));

        var disjointVector = _roles.ReadRevisionVector(role);
        var disjoint = _roles.LoadProgress(role);
        disjoint.Exp = 31;
        var progressPlan = new RoleProgressWritePlan(role, disjoint);
        progressPlan.Expect([disjointVector]);
        _roles.SaveSettings(role, [3]);
        Assert.Equal(1, _accounts.WriteSync<int>(progressPlan));
        Assert.Equal(31, _roles.LoadProgress(role).Exp);
        Assert.Equal(new byte[] { 3 }, _roles.LoadSettings(role));
        using var metrics = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(_accounts.AccessMetrics.Snapshot()));
        Assert.True(metrics.RootElement.GetProperty("revisionConflicts").GetInt64() >= 2);
    }

    [Fact]
    public void LoadedProgressKeepsItsSourceRevisionAcrossDirectAndEnlistedSaves()
    {
        long role = Create("LoadedProgress");
        var stale = _roles.LoadProgress(role);
        var current = _roles.LoadProgress(role);
        current.Exp = 7;
        _roles.SaveProgress(role, current);
        stale.Exp = 8;
        Assert.Throws<OwnerRevisionConflictException>(() => _roles.SaveProgress(role, stale));
        current.Exp = 9;
        _roles.SaveProgress(role, current);

        var enlisted = _roles.NewTransaction();
        current.Exp = 10;
        _roles.SaveProgress(role, current, enlisted);
        enlisted.Commit(_accounts, "test/source-progress");
        current.Exp = 11;
        _roles.SaveProgress(role, current);
        Assert.Equal(11, _roles.LoadProgress(role).Exp);

        _roles.SaveSettings(role, [1]);
        var mixed = _roles.NewTransaction();
        current.Exp = 12;
        _roles.SaveProgress(role, current, mixed);
        Assert.Equal(new byte[] { 1 }, mixed.ReadRoleBlob(_accounts, role, "settings"));
        mixed.RoleBlob(role, "settings", [2]);
        mixed.Commit(_accounts, "test/progress-disjoint-settings");
        Assert.Equal(12, _roles.LoadProgress(role).Exp);
        Assert.Equal(new byte[] { 2 }, _roles.LoadSettings(role));
    }

    [Fact]
    public void ItemOnlyAndOfflineMailPlansRejectStaleRevisionsAndDeletedEpoch()
    {
        long role = Create("OfflineRevision");
        var item = new InvItem { Entry = 1004000, Stack = 3, Pack = 1 };
        _items.Insert(role, item);
        var beforeItem = _roles.ReadRevisionVector(role);
        var staleItem = new ItemWritePlan(ItemWriteKind.Update, items: [new InvItem
        { Guid = item.Guid, Entry = item.Entry, Stack = 1, Pack = item.Pack }]);
        staleItem.Expect([beforeItem]);
        item.Stack = 2;
        _items.Update(item);
        Assert.ThrowsAny<RolePlanConflictException>(() => _accounts.WriteSync<long[]>(staleItem));
        Assert.Equal(2, Assert.Single(_items.Load(role)).Stack);

        var beforeMail = _roles.ReadRevisionVector(role);
        var staleMail = new FinancialMailAppendPlan(role, "source:old", new MailService.Letter
        { DeliveryKey = "source:old", TemplateId = 1040 }, 10);
        staleMail.Expect([beforeMail]);
        _roles.SaveMailbox(role, [1, 2]);
        Assert.ThrowsAny<RolePlanConflictException>(() => _accounts.WriteSync<int>(staleMail));
        Assert.Equal(new byte[] { 1, 2 }, _roles.LoadMailbox(role));

        var beforeDelete = _roles.ReadRevisionVector(role);
        var afterDeleteItem = new ItemWritePlan(ItemWriteKind.Update, items: [item]);
        afterDeleteItem.Expect([beforeDelete]);
        Assert.True(_roles.Delete("account", role));
        Assert.ThrowsAny<RolePlanConflictException>(() => _accounts.WriteSync<long[]>(afterDeleteItem));
        Assert.Equal(beforeDelete.Epoch + 1, _roles.ReadRevisionVector(role).Epoch);
        Assert.True(_roles.ReadRevisionVector(role).Deleted);
        Assert.Equal(2, Assert.Single(_items.Load(role)).Stack);
    }

    [Fact]
    public void MailboxReadGuardRejectsAbaBeforeADeferredClaim()
    {
        long role = Create("MailboxAba");
        _roles.SaveMailbox(role, [1]);
        var source = _accounts.ReadSync<RoleBlobSnapshot>(new RoleBlobSnapshotRead(role, "mailbox"));
        var pending = _accounts.NewTransaction();
        pending.Guard(new RoleBlobGuardPlan(role, "mailbox", source));
        pending.RoleBlob(role, "mailbox", [2]);

        _roles.SaveMailbox(role, [3]);
        _roles.SaveMailbox(role, [1]);
        Assert.Throws<OwnerRevisionConflictException>(() => pending.Commit(_accounts, "test/mailbox-aba"));
        Assert.Equal(new byte[] { 1 }, _roles.LoadMailbox(role));
    }

    [Fact]
    public void CompositeConflictRollsBackProgressAndInventoryWithSettings()
    {
        long role = Create("CompositeRevision");
        var staged = _roles.LoadProgress(role);
        staged.Exp = 99;
        var item = new InvItem { Entry = 1004000, Stack = 1, Pack = 1 };
        var builder = _roles.NewTransaction();
        builder.Progress(role, staged);
        builder.RoleBlob(role, "settings", [1]);
        builder.Add(new ItemWritePlan(ItemWriteKind.Insert, role: role, items: [item]));
        _roles.SaveSettings(role, [2]);
        Assert.ThrowsAny<RolePlanConflictException>(() => builder.Commit(_accounts, "test/revision-composite"));
        Assert.Equal(0, _roles.LoadProgress(role).Exp);
        Assert.Equal(new byte[] { 2 }, _roles.LoadSettings(role));
        Assert.Empty(_items.Load(role));
        Assert.Equal(0, item.Guid);
    }

    [Fact]
    public void OpaqueMarketKeysStayDistinctAndCollectionRevisionRejectsAnAbaRead()
    {
        _accounts.WriteSync<int>(new MarketSchemaPlan());
        void Put(string key, string? data)
        {
            var write = _accounts.NewTransaction();
            write.Add(new MarketRecordWritePlan("opaque", key, data));
            write.Commit(_accounts, "test/opaque-record");
        }

        Put("001", "first");
        Put("1", "second");
        var rows = _accounts.ReadSync<List<(string Id, string Data)>>(new MarketRecordReadPlan("opaque"));
        Assert.Equal(["001", "1"], [.. rows.Select(row => row.Id)]);
        var first = _accounts.ReadSync<OwnerRevisionValue>(new OwnerRevisionReadPlan(
            RevisionStore.Entity("market_record:opaque", "001")));
        var second = _accounts.ReadSync<OwnerRevisionValue>(new OwnerRevisionReadPlan(
            RevisionStore.Entity("market_record:opaque", "1")));
        Assert.True(first.Exists);
        Assert.True(second.Exists);
        Assert.Equal(1, first.Revision);
        Assert.Equal(1, second.Revision);

        var snapshot = _accounts.ReadSync<MarketRecordSnapshot>(new MarketRecordSnapshotReadPlan("opaque"));
        var stale = _accounts.NewTransaction();
        stale.Guard(new MarketRecordGuardPlan("opaque", null, snapshot));
        stale.Add(new MarketRecordWritePlan("opaque", "001", "stale"));
        Put("temporary", "value");
        Put("temporary", null);
        Assert.Throws<OwnerRevisionConflictException>(() => stale.Commit(_accounts, "test/market-aba"));
        Assert.Equal("first", Assert.Single(_accounts.ReadSync<List<(string Id, string Data)>>(new MarketRecordReadPlan("opaque", "001"))).Data);
    }

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
