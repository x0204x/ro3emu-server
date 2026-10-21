using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

public sealed class ActivityFragmentCaptureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-fragment-capture-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;

    public ActivityFragmentCaptureTests()
    {
        _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _directory }));
        _roles = new RoleStore(_accounts);
        _items = new ItemStore(_accounts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FragmentTransferGuardsBothInventoriesAndAppliesRecipientGuidOnlyAfterWholeCommit(bool conflict)
    {
        long sender = _roles.Create("fragment", 1, "Sender", 1, 1000, 1)!.Id;
        long recipient = _roles.Create("fragment", 1, "Recipient", 1, 1000, 1)!.Id;
        var original = new InvItem { Entry = 1111001, Stack = 2, Pack = 1 };
        _items.Insert(sender, original);
        var senderBefore = _accounts.ReadSync<List<InvItem>>(new ActivityInventoryReadPlan(sender));
        var recipientBefore = _accounts.ReadSync<List<InvItem>>(new ActivityInventoryReadPlan(recipient));
        var senderAfter = senderBefore.Select(i => i.Clone()).ToList();
        senderAfter[0].Stack = 1;
        var gift = new InvItem { Entry = 1111001, Stack = 1, Pack = 1 };
        var recipientAfter = new List<InvItem> { gift };
        var transaction = new RoleItemPlanBuilder(_accounts);
        transaction.Guard(new ActivityInventoryGuardPlan(sender, senderBefore));
        transaction.Guard(new ActivityInventoryGuardPlan(recipient, recipientBefore));
        transaction.Add(new ItemSnapshotWritePlan(sender, senderAfter), receipt =>
            ItemSnapshotWritePlan.ApplyGuids(senderAfter, (long[])receipt!));
        transaction.Add(new ItemSnapshotWritePlan(recipient, recipientAfter), receipt =>
            ItemSnapshotWritePlan.ApplyGuids(recipientAfter, (long[])receipt!));
        transaction.RoleBlob(recipient, "activity_runtime", [1, 2, 3]);
        Assert.Equal(0, gift.Guid);
        if (conflict)
        {
            original.Stack = 3;
            _items.Update(original);
            Assert.ThrowsAny<InvalidOperationException>(() => transaction.Commit(_accounts, "test/fragment-conflict"));
            Assert.Equal(0, gift.Guid);
            Assert.Equal(3, Assert.Single(_items.Load(sender)).Stack);
            Assert.Empty(_items.Load(recipient));
            Assert.Null(_roles.LoadActivityRuntime(recipient));
        }
        else
        {
            transaction.Commit(_accounts, "test/fragment-success");
            Assert.True(gift.Guid > 0);
            Assert.Equal(gift.Guid, Assert.Single(_items.Load(recipient)).Guid);
            Assert.Equal(1, Assert.Single(_items.Load(sender)).Stack);
            Assert.Equal(new byte[] { 1, 2, 3 }, _roles.LoadActivityRuntime(recipient));
        }
    }

    [Fact]
    public void InventoryGuardComparesRowsIndependentOfLiveBagOrder()
    {
        long role = _roles.Create("fragment", 1, "Ordered", 1, 1000, 1)!.Id;
        var first = new InvItem { Entry = 1111001, Stack = 1, Pack = 1 };
        var second = new InvItem { Entry = 1111002, Stack = 1, Pack = 1 };
        _items.Insert(role, first);
        _items.Insert(role, second);
        var reversed = _items.Load(role);
        reversed.Reverse();
        var builder = new RoleItemPlanBuilder(_accounts);
        builder.Guard(new ActivityInventoryGuardPlan(role, reversed));
        builder.RoleBlob(role, "activity_runtime", [3]);

        builder.Commit(_accounts, "test/fragment-order-independent");

        Assert.Equal(new byte[] { 3 }, _roles.LoadActivityRuntime(role));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
