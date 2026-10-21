using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class RoleItemCaptureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-roleitem-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;
    private readonly FriendStore _friends;

    public RoleItemCaptureTests()
    {
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new RoleStore(_accounts);
        _items = new ItemStore(_accounts);
        _friends = new FriendStore(_accounts);
    }

    private long Role(string name) => _roles.Create("test-account", 1, name, 1, 1000, 1)!.Id;

    [Fact]
    public void RoutedBlobWhitelistCoversEveryLiteralRoleStoreAccessor()
    {
        string root = RepoRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "Ro3Emu.Server", "Accounts", "RoleStore.cs"));
        var columns = Regex.Matches(source, "(?:LoadBlob|SaveBlob)\\([^;\\r\\n]*?\"(?<column>[a-z_]+)\"")
            .Select(match => match.Groups["column"].Value).Distinct().ToArray();
        Assert.True(columns.Length >= 30);
        foreach (string column in columns) RoleColumns.Validate(column);
    }

    [Fact]
    public void DailyAwardDeduplicatesRepeatedNewStackByIdentityAndRetainsDistinctNewStacks()
    {
        long role = Role("DailyCapture");
        _roles.SaveDaily(role, [0]);
        var daily = _roles.LoadDaily(role)!;
        var progress = _roles.LoadProgress(role);
        var first = new InvItem { Entry = 1004000, Stack = 5, Pack = 1 };
        var second = new InvItem { Entry = 1004000, Stack = 2, Pack = 1 };

        Assert.True(_roles.CommitDailyAward(role, daily, [1], progress, [first, first, second]));

        Assert.True(first.Guid > 0);
        Assert.True(second.Guid > 0);
        Assert.NotEqual(first.Guid, second.Guid);
        var stored = _items.Load(role);
        Assert.Equal(2, stored.Count);
        Assert.Equal(new[] { 2, 5 }, stored.Select(i => i.Stack).Order().ToArray());
        Assert.Equal(new[] { first.Guid, second.Guid }.Order(), stored.Select(i => i.Guid).Order());
    }

    [Fact]
    public void DailyConflictRollsBackNewItemsAndPreservesCallerGuid()
    {
        long role = Role("DailyConflict");
        var item = new InvItem { Entry = 1004000, Stack = 1, Pack = 1 };
        Assert.False(_roles.CommitDailyAward(role, [99], [1], _roles.LoadProgress(role), [item]));
        Assert.Equal(0, item.Guid);
        Assert.Empty(_items.Load(role));
    }

    [Fact]
    public void FinancialMailboxAppendsReadCurrentStateAndDeduplicatesSourceWithinOneTransaction()
    {
        long role = Role("MailCapture");
        var first = new MailService.Letter
        {
            DeliveryKey = "source:one", CreatedTime = 10,
            TemplateId = 1040, Attachments = [new MailService.Attachment(1, 5)]
        };
        var second = new MailService.Letter
        {
            DeliveryKey = "source:two", CreatedTime = 11,
            TemplateId = 1040, Attachments = [new MailService.Attachment(1, 7)]
        };
        var builder = new RoleItemPlanBuilder(_accounts);
        builder.Add(new FinancialMailAppendPlan(role, "source:one", first, 100));
        builder.Add(new FinancialMailAppendPlan(role, "source:one", first, 101));
        builder.Add(new FinancialMailAppendPlan(role, "source:two", second, 102));
        first.Attachments[0] = new MailService.Attachment(1, 999);
        builder.Commit(_accounts, "test/mail-two-letters");

        var box = JsonSerializer.Deserialize<MailService.Mailbox>(_roles.LoadMailbox(role)!)!;
        Assert.Equal(2, box.Letters.Count);
        Assert.Equal(new[] { "source:one", "source:two" }, box.Letters.Select(x => x.DeliveryKey));
        Assert.Equal(new[] { 5, 7 }, box.Letters.Select(x => x.Attachments[0].Count));
        Assert.NotEqual(box.Letters[0].Id, box.Letters[1].Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinancialMailboxAndItemRollbackTogether(bool failAfterMail)
    {
        long role = Role(failAfterMail ? "MailAfter" : "MailBefore");
        var item = new InvItem { Entry = 1004000, Stack = 3, Pack = 1 };
        _items.Insert(role, item);
        var spent = item.Clone(); spent.Stack = 1;
        var builder = new RoleItemPlanBuilder(_accounts);
        builder.ItemUpdate(spent);
        if (!failAfterMail) builder.Add(new FailurePlan());
        builder.Add(new FinancialMailAppendPlan(role, "source:fail",
            new MailService.Letter { DeliveryKey = "source:fail", TemplateId = 1040 }, 100));
        if (failAfterMail) builder.Add(new FailurePlan());

        Assert.Throws<InvalidOperationException>(() => builder.Commit(_accounts, "test/mail-rollback"));
        Assert.Equal(3, Assert.Single(_items.Load(role)).Stack);
        Assert.True(_roles.LoadMailbox(role) is null or { Length: 0 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GiftPaymentBothMetadataBlobsAndIntimacyRollbackTogether(bool failAfterGift)
    {
        long giver = Role(failAfterGift ? "GiftAfter" : "GiftBefore");
        long receiver = Role(failAfterGift ? "RecipientAfter" : "RecipientBefore");
        _friends.Link(giver, receiver);
        var item = new InvItem { Entry = 1004000, Stack = 3, Pack = 1 };
        _items.Insert(giver, item);
        var spent = item.Clone(); spent.Stack = 2;
        var builder = new RoleItemPlanBuilder(_accounts);
        builder.ItemUpdate(spent);
        if (!failAfterGift) builder.Add(new FailurePlan());
        builder.FriendMetadata(giver, [1, 2, 3]);
        builder.FriendMetadata(receiver, [4, 5, 6]);
        builder.FriendIntimacy(giver, receiver, 12);
        builder.FriendIntimacy(receiver, giver, 12);
        if (failAfterGift) builder.Add(new FailurePlan());

        Assert.Throws<InvalidOperationException>(() => builder.Commit(_accounts, "test/gift-rollback"));
        Assert.Equal(3, Assert.Single(_items.Load(giver)).Stack);
        Assert.Equal(0, _friends.Intimacy(giver, receiver));
        Assert.Equal(0, _friends.Intimacy(receiver, giver));
        using var db = _accounts.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM friend_metadata WHERE role IN ($giver,$receiver)";
        cmd.Parameters.AddWithValue("$giver", giver);
        cmd.Parameters.AddWithValue("$receiver", receiver);
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    private sealed class FailurePlan : DbPlan
    {
        internal override int Bytes => 64;
        internal override string Label => "test/failure";
        internal override object Execute(SqliteConnection db, SqliteTransaction tx) =>
            throw new InvalidOperationException("test failure");
    }

    public void Dispose()
    {
        _accounts.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
