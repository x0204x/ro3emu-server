using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

public sealed class LegacyTransactionCaptureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-legacy-capture-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void CallbackCapturesOnCallerAndReturnsGeneratedGuidAfterWholeCommit()
    {
        using var accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));
        var roles = new RoleStore(accounts);
        var items = new ItemStore(accounts);
        var role = roles.Create("capture", 1, "Capture", 1, 1000, 1)!;
        var progress = roles.LoadProgress(role.Id);
        var item = new InvItem { Entry = 1001, Stack = 2, Pack = 1 };
        int caller = Environment.CurrentManagedThreadId;
        roles.WithTransaction(tx =>
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            progress.Currency[1] = 10;
            roles.SaveProgress(role.Id, progress, tx);
            items.Insert(role.Id, item, tx);
            Assert.Equal(0, item.Guid);
            progress.Currency[1] = 999;
            item.Stack = 999;
        });
        Assert.True(item.Guid > 0);
        Assert.Equal(10, roles.LoadProgress(role.Id).Currency[1]);
        var stored = Assert.Single(items.Load(role.Id));
        Assert.Equal(item.Guid, stored.Guid);
        Assert.Equal(2, stored.Stack);
    }

    [Fact]
    public void LaterFailureRollsBackItemAndDoesNotPublishItsGuid()
    {
        using var accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));
        var roles = new RoleStore(accounts);
        var items = new ItemStore(accounts);
        var role = roles.Create("rollback", 1, "Rollback", 1, 1000, 1)!;
        var item = new InvItem { Entry = 1001, Stack = 1 };
        Assert.ThrowsAny<PersistenceException>(() => roles.WithTransaction(tx =>
        {
            items.Insert(role.Id, item, tx);
            roles.SaveMailbox(long.MaxValue, [1], tx);
        }));
        Assert.Equal(0, item.Guid);
        Assert.Empty(items.Load(role.Id));
    }

    [Fact]
    public void CaptureReadsItsOwnEarlierBlobWriteAndCopiesReturnedBytes()
    {
        using var accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = _dir }));
        var roles = new RoleStore(accounts);
        var role = roles.Create("overlay", 1, "Overlay", 1, 1000, 1)!;
        roles.SaveMailbox(role.Id, [1]);
        roles.WithTransaction(tx =>
        {
            roles.SaveMailbox(role.Id, [2, 3], tx);
            var read = roles.LoadMailbox(role.Id, tx)!;
            Assert.Equal(new byte[] { 2, 3 }, read);
            read[0] = 99;
            Assert.Equal(new byte[] { 2, 3 }, roles.LoadMailbox(role.Id, tx));
        });
        Assert.Equal(new byte[] { 2, 3 }, roles.LoadMailbox(role.Id));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
