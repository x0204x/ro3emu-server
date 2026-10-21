using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>Expiry callbacks contend with an admitted command for the same role.</summary>
public sealed class FamilyExpiryOwnerTests : IAsyncLifetime
{
    private const int Hair = 70000000, TimedHead = 10030417;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-family-expiry-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        }, _clock);
        await _app.StartAsync(); _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id); return bot;
    }

    private async Task Gm(string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync(_http + "/ro3emu/gm?account=alice", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))),
            TimeSpan.FromSeconds(10))).Body);

    [ClientDataFact]
    public async Task MountExpiryRunsAfterSuspendedSelectionAndDoesNotRestoreAnExpiredRider()
    {
        await using var bot = await Enter();
        await Gm("!lvl 14"); await Gm("!item 1501023 1");
        long voucher = Session.Items.Single(i => i.Entry == 1501023).Guid;
        await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = voucher, ItemCount = 1 });
        Assert.Equal(0, (await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        long due = Session.Mounts!.Mounts[60040010].OverdueTimeMs;
        Assert.True(due > _clock.GetUtcNow().ToUnixTimeMilliseconds());

        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ArmStageBarrier("economy/cd13b/mount-state", "deadline/mount-expiry", staged, release, deferred);
        var selection = Service<GameLoop>().InvokeAsync(() => Service<MountService>().SetAsync(Session,
            new PlayerMountsSetCfgReq { MountId = 60040010 }));
        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Service<GameLoop>().InvokeAsync(() =>
            {
                Assert.False(Session.Mounts!.MountIds.ContainsKey(1));
                _clock.Set(DateTimeOffset.FromUnixTimeMilliseconds(due));
                Service<Deadlines>().Tick(default);
                return Task.CompletedTask;
            });
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(selection.IsCompleted);
        }
        finally { release.TrySetResult(); await ClearStageBarrier(); }
        await selection.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, (await Next(bot, MountService.SetConfig, PlayerMountsSetCfgResp.Parser)).Result);
        await Service<GameLoop>().InvokeAsync(() => { Service<Deadlines>().Tick(default); return Task.CompletedTask; });
        await Next(bot, MountService.Sync, DBMountsInfoIncrement.Parser,
            p => p.Mounts.TryGetValue(60040010, out var mount) && mount.Del);
        Assert.False(Session.Mounts!.Mounts.ContainsKey(60040010));
        Assert.Equal(0, MountService.Rider(Session));
        var saved = JsonSerializer.Deserialize<MountService.SavedState>(Service<RoleStore>().LoadMounts(Session.Role!.Id)!)!;
        Assert.False(DBMountsInfo.Parser.ParseFrom(saved.Info).Mounts.ContainsKey(60040010));
    }

    [ClientDataFact]
    public async Task WardrobeExpiryRemovesAHeadWornBySuspendedCommand()
    {
        await using var bot = await Enter();
        var due = DateTimeOffset.FromUnixTimeMilliseconds(_clock.GetUtcNow().ToUnixTimeMilliseconds() + 1250);
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            var info = Session.Wardrobe!.Clone();
            info.AllLightUpAppearances[TimedHead] = new DBAppearanceInfo
            { AppearanceId = TimedHead, OverdueTimeMs = due.ToUnixTimeMilliseconds() };
            Service<RoleStore>().SaveWardrobe(Session.Role!.Id, info.ToByteArray());
            await Service<WardrobeService>().LoginAsync(Session);
        });
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ArmStageBarrier("economy/cd13b/wardrobe-state", "deadline/wardrobe-expiry", staged, release, deferred);
        var wear = new WardRobeUseAppearanceReq(); wear.AppearanceIds.Add([Hair, TimedHead]);
        var command = Service<GameLoop>().InvokeAsync(() => Service<WardrobeService>().UseAsync(Session, wear));
        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Service<GameLoop>().InvokeAsync(() =>
            {
                Assert.False(Session.Wardrobe!.CurUseAppearances.ContainsKey(1));
                _clock.Set(due);
                Service<Deadlines>().Tick(default);
                return Task.CompletedTask;
            });
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(command.IsCompleted);
        }
        finally { release.TrySetResult(); await ClearStageBarrier(); }
        await command.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, (await Next(bot, WardrobeService.Use, WardRobeUseAppearanceRes.Parser)).Result);
        await Service<GameLoop>().InvokeAsync(() => { Service<Deadlines>().Tick(default); return Task.CompletedTask; });
        await Next(bot, WardrobeService.WardrobeSync, DBWardRobeInfoIncrement.Parser,
            p => p.AllLightUpAppearances.TryGetValue(TimedHead, out var head) && head.Del);
        Assert.False(Session.Wardrobe!.AllLightUpAppearances.ContainsKey(TimedHead));
        Assert.False(Session.Wardrobe.CurUseAppearances.ContainsKey(1));
        Assert.Equal(Session.Wardrobe.ToByteArray(), Service<RoleStore>().LoadWardrobe(Session.Role!.Id));
    }

    private Task ArmStageBarrier(string familyLabel, string deadlineOrigin, TaskCompletionSource staged,
        TaskCompletionSource release, TaskCompletionSource deferred) => Service<GameLoop>().InvokeAsync(() =>
    {
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != familyLabel) return Task.CompletedTask;
            staged.TrySetResult();
            return release.Task;
        };
        Service<OwnerRegistry>().DeferredForTest = origin =>
        { if (origin == deadlineOrigin) deferred.TrySetResult(); };
        return Task.CompletedTask;
    });

    private Task ClearStageBarrier() => Service<GameLoop>().InvokeAsync(() =>
    {
        Service<AccountStore>().BeforeOwnedCommitForTest = null;
        Service<OwnerRegistry>().DeferredForTest = null;
        return Task.CompletedTask;
    });

    private sealed class ManualClock(DateTimeOffset initial) : TimeProvider
    {
        private long _ticks = initial.UtcTicks;
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
