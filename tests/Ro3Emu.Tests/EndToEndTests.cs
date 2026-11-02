using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Whole server in-process on free ports with a throwaway data dir, driven by <see cref="BotClient"/>.</summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-e2e-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.PublicHost = "127.0.0.1";
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

    private BotClient NewBot() => new(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));

    [Fact]
    public async Task NewAccountCreatesRoleAndEntersWorld()
    {
        await using var bot = NewBot();
        await bot.LoginAsync("alice", "pw");
        Assert.Empty(bot.Roles);

        await bot.EnterGateAsync();
        var created = await bot.CreateRoleAsync("Alice", sex: 2, hair: 5);
        Assert.Equal(0, created.Result);
        Assert.Equal(5, created.Appearances[RoleProto.HairSlot]);

        var self = await bot.EnterWorldAsync(created.RoleBaseInfo.Id);
        Assert.Equal((ulong)created.RoleBaseInfo.Id, self.Uuid);
        Assert.Equal("Alice", self.Name);
        Assert.Equal(3, self.EntityType);

        await bot.WalkAsync(160, 0, 190, stop: true);
    }

    [Fact]
    public async Task DuplicateNameIsRefusedAndRolesSurviveRelogin()
    {
        await using (var first = NewBot())
        {
            await first.LoginAsync("bob", "pw");
            await first.EnterGateAsync();
            Assert.Equal(0, (await first.CreateRoleAsync("Bobby")).Result);
            Assert.Equal(2, (await first.CreateRoleAsync("bobby")).Result); // ERR_NameExist, case-insensitive
        }
        await using var second = NewBot();
        await second.LoginAsync("bob", "pw");
        Assert.Single(second.Roles);
        Assert.Equal("Bobby", second.Roles[0].Name);
    }

    [Fact]
    public async Task PositionSurvivesRelogin()
    {
        long roleId;
        await using (var bot = NewBot())
        {
            await bot.LoginAsync("dave", "pw");
            await bot.EnterGateAsync();
            roleId = (await bot.CreateRoleAsync("Dave")).RoleBaseInfo.Id;
            var spawn = await bot.EnterWorldAsync(roleId);
            // The fixture town's born point (sceneeditor/synthetic_town.json).
            Assert.Equal(150f, spawn.MovementInfo.Pos.X);
            await bot.WalkAsync(170f, 1.5f, 200f, stop: true);
            // WalkAsync only sends the packet. Observe its server-side Apply before
            // disconnect, leaving persistence to the real logout barrier below.
            var loop = _app.Services.GetRequiredService<GameLoop>();
            var sessions = _app.Services.GetRequiredService<SessionRegistry>();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            bool arrived = false;
            while (!arrived && elapsed.Elapsed < bot.Timeout)
            {
                arrived = await loop.InvokeAsync(() => Task.FromResult(sessions.All()
                    .Single(session => session.Role?.Id == roleId).Position is { X: 170f, Y: 1.5f, Z: 200f }));
                if (!arrived) await Task.Delay(10);
            }
            Assert.True(arrived, "The server must process the requested stop before disconnect.");
        }
        await using var again = NewBot();
        await again.LoginAsync("dave", "pw");
        await again.EnterGateAsync();
        var self = await again.EnterWorldAsync(roleId);
        Assert.Equal(170f, self.MovementInfo.Pos.X);
        Assert.Equal(1.5f, self.MovementInfo.Pos.Y);
        Assert.Equal(200f, self.MovementInfo.Pos.Z);
    }

    [Fact]
    public async Task ClientSettingsComeBackOnLogin()
    {
        long roleId;
        await using (var bot = NewBot())
        {
            await bot.LoginAsync("erin", "pw");
            await bot.EnterGateAsync();
            roleId = (await bot.CreateRoleAsync("Erin")).RoleBaseInfo.Id;
            await bot.EnterWorldAsync(roleId);
            await bot.SaveClientSettingAsync(10, [0x10, 0x01, 0x08, 0x01]); // SettingType_GameMode as the client sent it
        }
        await using var again = NewBot();
        await again.LoginAsync("erin", "pw");
        await again.EnterGateAsync();
        await again.EnterWorldAsync(roleId);
        var sync = Romsg.DBSettingInfoIncrement.Parser.ParseFrom(
            (await again.Inbox.WaitAsync(e => e.IsGame(MsgId.AutoSyncSettings), TimeSpan.FromSeconds(5))).Body);
        Assert.True(sync.SyncAllMark);
        Assert.Equal(new byte[] { 0x10, 0x01, 0x08, 0x01 }, sync.ClientSetting[10].All.ToByteArray());
    }

    [Fact]
    public async Task SwitchRoleReturnsToRoleScreenAndEntersAnotherRole()
    {
        await using var bot = NewBot();
        await bot.LoginAsync("frank", "pw");
        await bot.EnterGateAsync();
        var first = (await bot.CreateRoleAsync("Frank")).RoleBaseInfo;
        await bot.EnterWorldAsync(first.Id);

        await bot.SwitchRoleAsync();
        var second = await bot.CreateRoleAsync("Franka", sex: 2);
        Assert.Equal(0, second.Result);
        var self = await bot.EnterWorldAsync(second.RoleBaseInfo.Id);
        Assert.Equal("Franka", self.Name);
    }

    [Fact]
    public async Task WrongPasswordIsRejected()
    {
        await using (var bot = NewBot()) await bot.LoginAsync("carol", "right");
        await using var again = NewBot();
        await Assert.ThrowsAsync<InvalidOperationException>(() => again.LoginAsync("carol", "wrong"));
    }

}
