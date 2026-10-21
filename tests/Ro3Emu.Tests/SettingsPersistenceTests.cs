using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Persistence;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>C/D 17: lazy client settings - merged in memory, no loop write, AFK settings durable, flushed on leave.</summary>
public sealed class SettingsPersistenceTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-set-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
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
        var session = Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
        await Eventually(() => !session.SceneLoading);
        return (bot, session);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    private Task<T> OnLoop<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));
    private Romsg.DBSettingInfo Stored(long role)
        => Service<RoleStore>().LoadSettings(role) is { } bytes ? Romsg.DBSettingInfo.Parser.ParseFrom(bytes) : new Romsg.DBSettingInfo();

    [Fact]
    public async Task ClientSettingsAreMergedInMemoryAndWrittenLazilyWithoutALoopWrite()
    {
        var (bot, s) = await InWorld("Settler");
        await using var _ = bot;
        var accounts = Service<AccountStore>();
        Service<SettingsPersistence>().Interval = TimeSpan.FromMilliseconds(300);
        int loopBlobWrites = 0;
        accounts.WriteSyncPlanObserved = (plan, onLoop) =>
        { if (onLoop && plan is Ro3Emu.Server.Accounts.Persistence.RoleBlobWritePlan { Column: "settings" }) Interlocked.Increment(ref loopBlobWrites); };
        try
        {
            // Two changes before the flush: both survive (each request carries only its own key).
            await bot.SaveClientSettingAsync(10, [0x10, 0x01, 0x08, 0x01]);
            await bot.SaveClientSettingAsync(11, [0x08, 0x02]);
            await Eventually(() => Stored(s.Role!.Id).ClientSetting.ContainsKey(10) && Stored(s.Role!.Id).ClientSetting.ContainsKey(11));
        }
        finally { accounts.WriteSyncPlanObserved = null; }
        Assert.Equal(0, loopBlobWrites);
        var stored = Stored(s.Role!.Id);
        Assert.Equal(new byte[] { 0x10, 0x01, 0x08, 0x01 }, stored.ClientSetting[10].ToByteArray());
        Assert.Equal(new byte[] { 0x08, 0x02 }, stored.ClientSetting[11].ToByteArray());
    }

    [Fact]
    public async Task AnAfkSettingIsDurableBeforeItsReply()
    {
        var (bot, s) = await InWorld("Afker");
        await using var _ = bot;
        Service<SettingsPersistence>().Interval = TimeSpan.FromHours(1); // a lazy write cannot explain the result
        // SaveSettingReq { type: [WildAfk], dbSettingInfo { wildAFKInfo { field 2 = 1 } } } as the client encodes it.
        await bot.SendGameBytesAsync(MsgId.SaveSetting, [0x0a, 1, 5, 0x12, 4, 0x2a, 2, 16, 1]);
        await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.SaveSetting), Wait);
        Assert.NotNull(Stored(s.Role!.Id).WildAFKInfo);
    }

    [Fact]
    public async Task LeavingTheWorldFlushesAnUnsavedSetting()
    {
        var (bot, s) = await InWorld("Leaver");
        long role = s.Role!.Id;
        Service<SettingsPersistence>().Interval = TimeSpan.FromHours(1); // only the leave barrier can write it
        await bot.SaveClientSettingAsync(12, [0x08, 0x07]); // returns after the reply
        Assert.False(Stored(role).ClientSetting.ContainsKey(12));
        await bot.SendGameAsync(MsgId.Logout, new Romsg.LogoutReq());
        await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.Logout), Wait);
        await bot.DisposeAsync();
        Assert.Equal(new byte[] { 0x08, 0x07 }, Stored(role).ClientSetting[12].ToByteArray());
    }

    [Fact]
    public async Task TheLeaveBarrierWinsOverALazyWriteStillInFlight()
    {
        // Audit 11.10: the value was X, a lazy write of Y was in flight, the player set X again and left. The
        // barrier compared X with the snapshot (still X, Y not committed), skipped its write, and Y then committed.
        var (bot, entered) = await InWorld("Flipper");
        var s = entered;
        long role = s.Role!.Id;
        var accounts = Service<AccountStore>();
        Service<SettingsPersistence>().Interval = TimeSpan.FromMilliseconds(50);
        byte[] x = [0x08, 0x01], y = [0x08, 0x02];
        await bot.SaveClientSettingAsync(14, x);
        await Eventually(() => Stored(role).ClientSetting.TryGetValue(14, out var v) && v.ToByteArray().SequenceEqual(x));
        await Eventually(() => s.SettingsPart is { InFlight: false } part && part.Applied == part.Persisted);
        // Re-enter the same role: X is now what the login snapshot read.
        await bot.SwitchRoleAsync();
        await bot.EnterWorldAsync(role);
        s = Service<SessionRegistry>().All().Single(x => x.Account == bot.Account && x.Role != null);
        await Eventually(() => !s.SceneLoading);

        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeLazyWriteForTest = async label =>
        {
            if (label != "role/write-blob" || !captured.TrySetResult()) return;
            await release.Task;
        };
        try
        {
            await bot.SaveClientSettingAsync(14, y);
            await captured.Task.WaitAsync(Wait);
            Service<SettingsPersistence>().Interval = TimeSpan.FromHours(1); // only the leave barrier writes now
            await bot.SaveClientSettingAsync(14, x);
            await bot.SendGameAsync(MsgId.Logout, new Romsg.LogoutReq());
            await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.Logout), Wait);
            release.TrySetResult();
            await Eventually(() => s.SettingsPart is { InFlight: false });
        }
        finally { release.TrySetResult(); accounts.BeforeLazyWriteForTest = null; }
        await bot.DisposeAsync();
        Assert.Equal(x, Stored(role).ClientSetting[14].ToByteArray());
    }

    [ClientDataFact]
    public async Task AFirstLoginCommitsItsDefaultBlobsWithoutBlockingTheLoop()
    {
        // Stage G: a new role's first login wrote 10 default blobs synchronously on the loop (15 ms per new role).
        var accounts = Service<AccountStore>();
        var onLoop = new System.Collections.Concurrent.ConcurrentQueue<string>();
        accounts.WriteSyncPlanObserved = (plan, loop) =>
        { if (loop && plan is Ro3Emu.Server.Accounts.Persistence.RoleBlobWritePlan blob) onLoop.Enqueue(blob.Column); };
        var (bot, s) = await InWorld("Firstie");
        await using var _ = bot;
        accounts.WriteSyncPlanObserved = null;
        Assert.DoesNotContain(onLoop, AccountStore.StagedColumns.Contains);
        Assert.NotNull(Service<RoleStore>().LoadTasks(s.Role!.Id));
        Assert.NotNull(accounts.ReadSync<byte[]?>(new Ro3Emu.Server.Accounts.Persistence.RoleBlobReadPlan(s.Role.Id, "tasks")));
        Assert.Equal(-1, accounts.StagedCount(s.Role.Id));
    }
}
