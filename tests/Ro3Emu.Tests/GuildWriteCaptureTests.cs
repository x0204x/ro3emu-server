using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

public sealed class GuildWriteCaptureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-guild-capture-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly GuildStore _guilds;
    private readonly ItemStore _items;

    public GuildWriteCaptureTests()
    {
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _ = new RoleStore(_accounts);
        _guilds = new GuildStore(_accounts);
        _items = new ItemStore(_accounts);
        foreach (long role in new long[] { 1, 2, 4, 7, 11 })
            FixtureSql.Execute(_accounts, """
                INSERT INTO role(id,account,area,name,sex,career,level,hair,create_time,last_login_time)
                VALUES($id,'guild-capture',1,$name,1,1000,1,1,0,0)
                """, ("$id", role), ("$name", $"GuildCapture{role}"));
        _accounts.WriteSync<int>(new RoleSchemaPlan());
    }

    [Fact]
    public void GuildIdentityAndNestedStateAreCapturedAndAppliedOnCallerAfterCommit()
    {
        var state = new GuildStore.State { Name = "captured", Rights = { [1] = [2, 3] } };
        state.Members[11] = new GuildStore.Member { RoleId = 11, Name = "member" };
        var builder = new RoleItemPlanBuilder(_accounts);
        _guilds.EnlistSave(state, builder);
        int caller = Environment.CurrentManagedThreadId, appliedOn = 0;
        int threadReceipt = builder.Add(new ThreadPlan(), _ => appliedOn = Environment.CurrentManagedThreadId);
        state.Name = "changed after capture";
        state.Members[11].Name = "changed";
        state.Members.Clear();
        state.Rights[1].Clear();
        Assert.Equal(0, state.Id);

        var receipts = builder.Commit(_accounts, "test/guild-capture");

        Assert.NotEqual(caller, (int)receipts[threadReceipt]!);
        Assert.Equal(caller, appliedOn);
        Assert.True(state.Id > 0);
        var persisted = Assert.IsType<GuildStore.State>(_guilds.Get(state.Id));
        Assert.Equal(state.Id, persisted.Id);
        Assert.Equal("captured", persisted.Name);
        Assert.Equal("member", persisted.Members[11].Name);
        Assert.Equal(new[] { 2, 3 }, persisted.Rights[1]);
        Assert.Equal(state.Id, _guilds.ForRole(11)!.Id);
    }

    [Fact]
    public void CachedGuildsAreIndependentCopiesAndFollowEveryCommittedMembershipChange()
    {
        var guild = new GuildStore.State { Name = "cached-guild" };
        guild.Members[1] = new GuildStore.Member { RoleId = 1 };
        Assert.True(_guilds.Save(guild));
        Assert.Null(_guilds.ForRole(2)); // cached as "no guild"
        var copy = _guilds.ForRole(1)!;
        copy.Notice = "uncommitted"; copy.Members.Clear();
        Assert.Equal("", _guilds.Get(guild.Id)!.Notice);
        Assert.Single(_guilds.ForRole(1)!.Members);

        var join = _guilds.Get(guild.Id)!;
        join.Members[2] = new GuildStore.Member { RoleId = 2 };
        Assert.True(_guilds.Save(join));
        Assert.Equal(guild.Id, _guilds.ForRole(2)!.Id); // the save invalidated the cached "no guild"
        Assert.Equal([1L, 2L], _guilds.Get(guild.Id)!.Members.Keys.Order());

        _guilds.Delete(guild.Id);
        Assert.Null(_guilds.Get(guild.Id));
        Assert.Null(_guilds.ForRole(1));
    }

    [Fact]
    public void StaleLoadedGuildAndMarketSnapshotsCannotOverwriteNewerState()
    {
        var guild = new GuildStore.State { Name = "revision-guild" };
        Assert.True(_guilds.Save(guild));
        var stale = _guilds.Get(guild.Id)!;
        var newer = _guilds.Get(guild.Id)!;
        newer.Notice = "newer";
        Assert.True(_guilds.Save(newer));
        stale.Notice = "stale";
        Assert.Throws<OwnerRevisionConflictException>(() => _guilds.Save(stale));
        Assert.Equal("newer", _guilds.Get(guild.Id)!.Notice);

        var staleMarket = _guilds.LoadMarket();
        var currentMarket = _guilds.LoadMarket();
        currentMarket.NextId = 4;
        Assert.True(_guilds.SaveMarket(currentMarket, []));
        staleMarket.NextId = 3;
        Assert.Throws<OwnerRevisionConflictException>(() => _guilds.SaveMarket(staleMarket, []));
        Assert.Equal(4, _guilds.LoadMarket().NextId);
    }

    [Fact]
    public void WriterEvaluatedGuildDeltaInvalidatesAnEarlierReplacementSnapshot()
    {
        var guild = new GuildStore.State { Name = "delta-guild", Materials = 10 };
        guild.Members[7] = new GuildStore.Member { RoleId = 7 };
        Assert.True(_guilds.Save(guild));
        var stale = _guilds.Get(guild.Id)!;
        var delta = _accounts.NewTransaction();
        _guilds.EnlistMaterialContribution(7, guild.Id, 5, delta);
        delta.Commit(_accounts, "test/guild-delta");
        stale.Notice = "overwrite";
        Assert.Throws<OwnerRevisionConflictException>(() => _guilds.Save(stale));
        Assert.Equal(15, _guilds.Get(guild.Id)!.Materials);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPaymentTransactionDoesNotPublishGuildIdentityOrAnyPartialWrites(bool failAfterGuild)
    {
        var occupied = new GuildStore.State { Name = "occupied" };
        occupied.Members[1] = new GuildStore.Member { RoleId = 1 };
        Assert.True(_guilds.Save(occupied));
        var item = new InvItem { Entry = 1004000, Stack = 3, Pack = 1 };
        _items.Insert(2, item);
        var payment = item.Clone();
        payment.Stack = 1;
        var candidate = new GuildStore.State { Name = "new guild" };
        candidate.Members[2] = new GuildStore.Member { RoleId = 2 };
        var builder = new RoleItemPlanBuilder(_accounts);
        builder.ItemUpdate(payment);
        if (!failAfterGuild) builder.Add(new DuplicateNamePlan());
        _guilds.EnlistSave(candidate, builder);
        if (failAfterGuild) builder.Add(new DuplicateNamePlan());

        var failure = Assert.Throws<SqliteException>(() => builder.Commit(_accounts, "test/guild-rollback"));

        Assert.Equal(19, failure.SqliteErrorCode);
        Assert.Equal(0, candidate.Id);
        Assert.Equal(3, Assert.Single(_items.Load(2)).Stack);
        Assert.Null(_guilds.ForRole(2));
        Assert.Equal(occupied.Id, Assert.Single(_guilds.List()).Id);
    }

    [Fact]
    public void MembershipConflictAndMissingRenameRetainTheirFailureSemantics()
    {
        var existing = new GuildStore.State { Name = "existing" };
        existing.Members[7] = new GuildStore.Member { RoleId = 7 };
        Assert.True(_guilds.Save(existing));
        var candidate = new GuildStore.State { Name = "candidate" };
        candidate.Members[7] = new GuildStore.Member { RoleId = 7 };
        var conflicting = new RoleItemPlanBuilder(_accounts);
        _guilds.EnlistSave(candidate, conflicting);
        Assert.Equal(19, Assert.Throws<SqliteException>(() => conflicting.Commit(_accounts, "test/guild-conflict")).SqliteErrorCode);
        Assert.Equal(0, candidate.Id);
        Assert.Equal(existing.Id, _guilds.ForRole(7)!.Id);

        var missing = new RoleItemPlanBuilder(_accounts);
        _guilds.EnlistSave(new GuildStore.State { Id = existing.Id + 10, Name = "missing" }, missing,
            "guild disappeared during rename");
        var gone = Assert.Throws<RowMissingException>(() => missing.Commit(_accounts, "test/guild-missing"));
        Assert.True(gone.Is(GuildStore.Table, existing.Id + 10));
        Assert.Equal("guild disappeared during rename", gone.Message);
    }

    [Fact]
    public void EscortContributionRechecksMembershipAndPreservesInterveningTreasuryChanges()
    {
        var state = new GuildStore.State { Name = "escort", Materials = 100 };
        state.Members[7] = new GuildStore.Member { RoleId = 7 };
        Assert.True(_guilds.Save(state));
        var contribution = new RoleItemPlanBuilder(_accounts);
        _guilds.EnlistMaterialContribution(7, state.Id, 1000, contribution);
        state.Materials = 200;
        state.Money = 123;
        Assert.True(_guilds.Save(state));
        contribution.Commit(_accounts, "test/guild-escort");
        var after = _guilds.Get(state.Id)!;
        Assert.Equal(1200, after.Materials);
        Assert.Equal(123, after.Money);

        var pack = new InvItem { Entry = 1258002, Stack = 2, Pack = 1 };
        _items.Insert(7, pack);
        var payment = pack.Clone();
        payment.Stack = 1;
        var left = new RoleItemPlanBuilder(_accounts);
        left.ItemUpdate(payment);
        _guilds.EnlistMaterialContribution(7, state.Id, 1000, left);
        after.Members.Clear();
        Assert.True(_guilds.Save(after));
        Assert.Equal("Escort submitter left guild",
            Assert.Throws<InvalidOperationException>(() => left.Commit(_accounts, "test/guild-left")).Message);
        Assert.Equal(2, Assert.Single(_items.Load(7)).Stack);
        Assert.Equal(1200, _guilds.Get(state.Id)!.Materials);
    }

    [Fact]
    public void CaravanRewardUsesLatestTreasuryAndOverflowRollsBackEarlierPayment()
    {
        var state = new GuildStore.State { Name = "caravan", Money = 10 };
        state.Members[4] = new GuildStore.Member { RoleId = 4, CurContribute = 2, HistoryContribute = 5 };
        Assert.True(_guilds.Save(state));
        var reward = new RoleItemPlanBuilder(_accounts);
        _guilds.EnlistCaravanReward(state.Id, 4, 3, 4, reward);
        state.Money = 20;
        state.Members[4].CurContribute = 6;
        state.Members[4].HistoryContribute = 9;
        Assert.True(_guilds.Save(state));
        reward.Commit(_accounts, "test/caravan-reward");
        var after = _guilds.Get(state.Id)!;
        Assert.Equal(23, after.Money);
        Assert.Equal(10, after.Members[4].CurContribute);
        Assert.Equal(13, after.Members[4].HistoryContribute);

        after.Money = long.MaxValue;
        Assert.True(_guilds.Save(after));
        var item = new InvItem { Entry = 1004000, Stack = 2, Pack = 1 };
        _items.Insert(4, item);
        var payment = item.Clone();
        payment.Stack = 1;
        var overflow = new RoleItemPlanBuilder(_accounts);
        overflow.ItemUpdate(payment);
        _guilds.EnlistCaravanReward(state.Id, 4, 1, 1, overflow);
        Assert.Throws<OverflowException>(() => overflow.Commit(_accounts, "test/caravan-overflow"));
        Assert.Equal(2, Assert.Single(_items.Load(4)).Stack);
        Assert.Equal(13, _guilds.Get(state.Id)!.Members[4].HistoryContribute);
    }

    private sealed class ThreadPlan : DbPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/guild-thread";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx) => Environment.CurrentManagedThreadId;
    }

    private sealed class DuplicateNamePlan : DbPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/guild-failure";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx)
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO guild(name,state) VALUES('occupied',X'00')";
            return command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
