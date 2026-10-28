using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Two bots in one scene: AOI enter, movement relay, turning, leave, head names.</summary>
public sealed class MultiplayerTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-mp-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }

    private static AoIViewChangeData View(BotEvent e) => AoIViewChangeData.Parser.ParseFrom(e.Body);

    [Fact]
    public async Task PlayersSeeEachOtherMoveTurnAndLeave()
    {
        await using var alice = await InWorld("Alice");
        var bob = await InWorld("Bob");
        ulong aliceId = alice.Self!.Uuid, bobId = bob.Self!.Uuid;

        // Both spawn at the same point: each gets the other's entity.
        var bobSeen = View(await alice.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.SyncAoiView), Wait));
        Assert.Equal(bobId, Assert.Single(bobSeen.Bases).Uuid);
        Assert.Equal("Bob", bobSeen.Bases[0].Name);
        Assert.Equal(3, bobSeen.Bases[0].EntityType);
        var aliceSeen = View(await bob.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.SyncAoiView), Wait));
        Assert.Equal(aliceId, Assert.Single(aliceSeen.Bases).Uuid);

        // Bob walks: Alice gets a Walk movement extrapolating along dir at Speed/100.
        await bob.SendGameAsync(31082, new WalkReq { Pos = new Vec3 { X = 158, Y = 0.5f, Z = 192 }, Dir = 90, Face = 90, SpeedRate = 1 });
        var walk = MovementChangeNtf.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.MovementChange), Wait)).Body);
        Assert.Equal((long)bobId, walk.Uid);
        Assert.Equal(MovementType.MoveTypeWalk, walk.MovementInfo.MoveType);
        Assert.Equal(7f, walk.MovementInfo.MovementWalk.Speed); // Novice Speed 700 (JobConfig default)
        Assert.Equal(0.5f, walk.MovementInfo.MovementWalk.EndHeight);
        Assert.True(walk.MovementInfo.MoveDuration > 0);

        await bob.WalkAsync(160, 0.5f, 192, stop: true);
        var stop = MovementChangeNtf.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.MovementChange), Wait)).Body);
        Assert.Equal(MovementType.MoveTypeNone, stop.MovementInfo.MoveType);
        Assert.Equal(160f, stop.MovementInfo.Pos.X);

        await bob.SendGameAsync(31020, new ChangeFaceReq { Angle = 270 });
        var turn = EntityFaceToAll.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.EntityFaceToAllId), Wait)).Body);
        Assert.Equal(270f, turn.Face);

        // Bob disconnects: Alice is told to drop him, never herself.
        await bob.DisposeAsync();
        var gone = View(await alice.Inbox.WaitAsync(e => e.IsGame(GameAoiSink.SyncAoiView), Wait));
        Assert.Equal(new[] { bobId }, gone.LeaveAoi);
    }

    [Fact]
    public async Task ChatReachesGroupMembersAndWhisperTarget()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        foreach (var bot in new[] { alice, bob }) await bot.JoinChatAsync("4_1");

        // Map channel: everyone in the group gets the push, the sender too; Carol (not joined) gets nothing.
        await alice.SendChatAsync("4_1", chatType: 2, "hello Prontera");
        foreach (var bot in new[] { alice, bob })
        {
            var line = await bot.NextChatLineAsync();
            Assert.Equal("hello Prontera", line.Content);
            Assert.Equal(alice.Self!.Uuid.ToString(), line.SenderId);
            Assert.Equal("4_1", line.ReceiveId);
        }

        // Whisper by role id: target and sender only.
        await bob.SendChatAsync(carol.Self!.Uuid.ToString(), chatType: 1, "psst");
        Assert.Equal("psst", (await carol.NextChatLineAsync()).Content);
        Assert.Equal("psst", (await bob.NextChatLineAsync()).Content);
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            alice.Timeout = TimeSpan.FromMilliseconds(300);
            await alice.NextChatLineAsync();
        });
    }

    [Fact]
    public async Task LateJoinerGetsRecentChannelLines()
    {
        await using var alice = await InWorld("Alice");
        await alice.JoinChatAsync("4_1");
        await alice.SendChatAsync("4_1", 2, "first", channelIndex: 4);
        await alice.NextChatLineAsync();

        await using var bob = await InWorld("Bob");
        await bob.JoinChatAsync("4_1");
        Assert.Equal("first", (await bob.NextChatLineAsync()).Content);
    }

    [Fact]
    public async Task SecondLoginKicksTheFirstSession()
    {
        await using var first = await InWorld("Dup");
        await using var second = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await second.LoginAsync("dup", "pw");
        await second.EnterGateAsync();
        await first.Inbox.WaitAsync(e => e.Kind == BotEventKind.Disconnect, Wait);
    }

    [Fact]
    public async Task GmCommandRepliesPrivatelyAndIsNotBroadcast()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        foreach (var bot in new[] { alice, bob }) await bot.JoinChatAsync("4_1");

        await alice.SendChatAsync("4_1", 2, "!where", channelIndex: 4);
        var reply = BroadCastNotify.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(Ro3Emu.Server.Game.GmCommands.BroadcastNotify), Wait)).Body);
        Assert.StartsWith("Scene 10111", reply.Content);
        bob.Timeout = TimeSpan.FromMilliseconds(300);
        await Assert.ThrowsAsync<TimeoutException>(() => bob.NextChatLineAsync());
    }

    [Fact]
    public async Task GmSkillTraceCommandReachesTheClientHookUnchanged()
    {
        await using var alice = await InWorld("Alice");
        await alice.JoinChatAsync("4_1");

        await alice.SendChatAsync("4_1", 2, "!client skilltrace on", channelIndex: 4);

        var command = await alice.Inbox.WaitAsync(e => e.IsGame(Ro3Emu.ClientHooks.PrivateMessageIds.Diagnostics), Wait);
        Assert.Equal("skilltrace on", System.Text.Encoding.UTF8.GetString(command.Body));
        var reply = BroadCastNotify.Parser.ParseFrom((await alice.Inbox.WaitAsync(
            e => e.IsGame(Ro3Emu.Server.Game.GmCommands.BroadcastNotify), Wait)).Body);
        Assert.Contains("Player.log", reply.Content);
        Assert.Contains("Ro3Emu.SkillTrace", reply.Content);
    }

    [Fact]
    public async Task GateKeyIsServedOverHttp()
    {
        using var http = new HttpClient();
        var pem = await http.GetStringAsync(_http + Ro3Emu.Server.Http.SdkEndpoints.GateKeyPath);
        Assert.Equal(File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")), pem);
    }

    [Fact]
    public void DatabaseBackupWritesACopy()
    {
        var backup = (Ro3Emu.Server.Accounts.DatabaseBackup)Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetServices<Microsoft.Extensions.Hosting.IHostedService>(_app.Services)
            .Single(s => s is Ro3Emu.Server.Accounts.DatabaseBackup);
        Assert.True(File.Exists(backup.Backup()));
    }

    [Fact]
    public async Task ConcurrentBackupsProduceReadableCopiesWithAccountData()
    {
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        Assert.Equal(AccountStore.LoginResult.Registered, accounts.Login("backup-reader", "pw"));
        var backup = _app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<DatabaseBackup>().Single();

        using var ready = new CountdownEvent(4);
        using var release = new ManualResetEventSlim();
        var calls = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            release.Wait();
            return backup.Backup();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try { Assert.True(ready.Wait(TimeSpan.FromSeconds(10))); }
        finally { release.Set(); }
        var paths = await Task.WhenAll(calls);

        foreach (var path in paths)
        {
            Assert.True(File.Exists(path));
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM account WHERE name = 'backup-reader'";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }
    }

    [Fact]
    public async Task HeadNamesComeFromUserDataService()
    {
        await using var alice = await InWorld("Alice");
        var reply = await alice.GetRoleAttributesAsync((long)alice.Self!.Uuid);
        var entry = (Dictionary<object, object?>)Assert.Single((object[])reply["Results"]!)!;
        var data = (Dictionary<object, object?>)entry["Data"]!;
        Assert.Equal("Alice", data["Name"]);
        Assert.IsType<byte[]>(data["Base"]);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
