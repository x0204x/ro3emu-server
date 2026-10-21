using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>C/D 16 (first part): legacy writes of an attached role capture its vector from the owner, not loop SQL.</summary>
public sealed class RevisionCacheTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-rev-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = hp; o.MicroGatePort = mp; o.GameGatePort = gp;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{hp}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    private async Task<(BotClient Bot, GameSession Session)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        // SelfInfoEnd precedes the owned visited-map commit. Finish that login write
        // before measuring revision reads of the legacy write below.
        await bot.Inbox.WaitAsync(e => e.IsGame(WorldCoreService.AoiInfo), TimeSpan.FromSeconds(8));
        return (bot, Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null));
    }

    [Fact]
    public async Task AnAttachedRolesLegacyWriteReadsNoRevisionVectorFromSql()
    {
        var (bot, s) = await InWorld("Cached");
        await using var _ = bot;
        var accounts = Service<AccountStore>();
        int vectorReads = 0;
        long hits = accounts.VectorCacheHits;
        accounts.ReadSyncObserved = (label, onLoop) => { if (onLoop && label == "role/read-revisions") Interlocked.Increment(ref vectorReads); };
        try
        {
            await Service<GameLoop>().InvokeAsync(() => Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().RunSessionAsync("cache test",
                [Ro3Emu.Server.Game.Ownership.ReservationKey.Role(s.Role!.Id)], s, () =>
                {
                    Service<RoleStore>().SaveDaily(s.Role.Id, [1, 2, 3]);
                    return Task.CompletedTask;
                }));
        }
        finally { accounts.ReadSyncObserved = null; }
        Assert.Equal(0, vectorReads);
        Assert.True(accounts.VectorCacheHits > hits);
        Assert.Equal(new byte[] { 1, 2, 3 }, Service<RoleStore>().LoadDaily(s.Role!.Id));
    }

    [Fact]
    public async Task AStaleOwnerVectorRetriesOnceFromSqlAndResynchronizes()
    {
        var (bot, s) = await InWorld("Stale");
        await using var _ = bot;
        var accounts = Service<AccountStore>();
        var roles = Service<RoleStore>();
        long role = s.Role!.Id;
        long retries = accounts.VectorCacheRetries;
        await Service<GameLoop>().InvokeAsync(() => Service<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().RunSessionAsync("stale test",
            [Ro3Emu.Server.Game.Ownership.ReservationKey.Role(role)], s, () =>
            {
                // A commit the owner never recorded (as if made off the loop): its cached daily revision is behind.
                var owner = s.Owner!;
                long durable = roles.ReadRevisionVector(role).Revision("daily");
                owner.Revisions = new Dictionary<string, long>(owner.Revisions, StringComparer.Ordinal) { ["daily"] = durable - 1 };
                roles.SaveDaily(role, [9, 9]);
                Assert.Equal(roles.ReadRevisionVector(role).Revision("daily"), owner.Revisions["daily"]);
                return Task.CompletedTask;
            }));
        Assert.Equal(retries + 1, accounts.VectorCacheRetries);
        Assert.Equal(new byte[] { 9, 9 }, roles.LoadDaily(role));
    }
}
