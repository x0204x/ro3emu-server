using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Tests;

/// <summary>Two hosts in one process, each with its own database: a write to one never makes the other's guild or
/// adventure-group cache stale (each database owns its cache generations).</summary>
public sealed class HostCacheIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3-host-cache-" + Guid.NewGuid().ToString("N"));
    private readonly AccountStore _first, _second;

    public HostCacheIsolationTests()
    {
        _first = Open("first");
        _second = Open("second");
    }

    private AccountStore Open(string name)
    {
        var accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = Path.Combine(_root, name) }));
        _ = new RoleStore(accounts);
        accounts.WriteSync<int>(new RoleSchemaPlan());
        return accounts;
    }

    [Fact]
    public void AGuildWriteBumpsOnlyItsOwnDatabasesGeneration()
    {
        var guilds = new GuildStore(_first);
        long before = _second.Caches.Current(WrittenCache.Guild), own = _first.Caches.Current(WrittenCache.Guild);
        var builder = new RoleItemPlanBuilder(_first);
        guilds.EnlistSave(new GuildStore.State { Name = "first" }, builder);
        builder.Commit(_first, "test/host-cache");
        Assert.True(_first.Caches.Current(WrittenCache.Guild) > own);
        Assert.Equal(before, _second.Caches.Current(WrittenCache.Guild));
    }

    [Fact]
    public void AnAdventureGroupWriteBumpsOnlyItsOwnDatabasesGeneration()
    {
        var groups = new AdventureGroupStore(_first);
        long before = _second.Caches.Current(WrittenCache.AdventureGroup);
        long own = _first.Caches.Current(WrittenCache.AdventureGroup);
        var builder = new RoleItemPlanBuilder(_first);
        groups.EnlistSave(new AdventureGroupStore.State { Name = "first" }, builder);
        builder.Commit(_first, "test/host-cache");
        Assert.True(_first.Caches.Current(WrittenCache.AdventureGroup) > own);
        Assert.Equal(before, _second.Caches.Current(WrittenCache.AdventureGroup));
    }

    public void Dispose()
    {
        _first.Dispose();
        _second.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
