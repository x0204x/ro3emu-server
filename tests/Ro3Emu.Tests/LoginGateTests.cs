using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Tests;

/// <summary>
/// The dispatcher's stage gate: before its role is in the world a connection may send only what the client's own
/// code sends then (HEARTBEAT from the start; on the role screen the role creation, selection and SDK info). Every
/// other routed message, and every id no route claims, is refused without reaching its handler.
/// </summary>
public sealed class LoginGateTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-gate-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "", _pem = "";

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
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
        _pem = File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem"));
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private MessageRoutes Routes => _app.Services.GetRequiredService<GameMessageHandler>().Routes;

    /// <summary>Every routed id (a range by its bounds) and one no route claims.</summary>
    private uint[] Ids() =>
        [.. Routes.Exact.Select(r => r.From), .. Routes.Ranges.SelectMany(r => new[] { r.From, r.To }), 999_999u];

    private Dictionary<MessageRoutes.Route, long> Calls() =>
        Routes.Exact.Concat(Routes.Ranges).ToDictionary(r => r, r => r.Calls);

    /// <summary>
    /// Sends every id refused at <paramref name="stage"/> with an empty body, then a heartbeat: its Pong comes after
    /// everything sent before it has been handled. Returns the routes whose handlers ran meanwhile.
    /// </summary>
    private async Task<string[]> SendRefused(BotClient bot, SessionStage stage)
    {
        var before = Calls();
        foreach (uint id in Ids().Where(id => Routes.RequiredStage(id) > stage))
            await bot.SendGameBytesAsync(id, []);
        await bot.HeartbeatAsync();
        return [.. Calls().Where(c => c.Value != before[c.Key] && c.Key.From != MsgId.Heartbeat)
            .Select(c => $"{c.Key.From} {c.Key.Owner}")];
    }

    [Fact]
    public void OnlyTheClientsOwnLoginMessagesAreAcceptedBeforeTheWorld()
    {
        var early = Ids().Where(id => Routes.RequiredStage(id) < SessionStage.World)
            .ToDictionary(id => id, id => Routes.RequiredStage(id));

        Assert.Equal(new Dictionary<uint, SessionStage>
        {
            [MsgId.Heartbeat] = SessionStage.Connected,
            [MsgId.LoginRoleCreate] = SessionStage.RoleScreen,
            [MsgId.LoginRoleSelect] = SessionStage.RoleScreen,
            [MsgId.UploadClientSdkInfo] = SessionStage.RoleScreen,
        }, early);
    }

    [Fact]
    public async Task OnTheRoleScreenEveryOtherMessageIsRefused()
    {
        await using var bot = new BotClient(_http, _pem);
        await bot.LoginAsync("screen", "pw");
        await bot.EnterGateAsync();

        Assert.Empty(await SendRefused(bot, SessionStage.RoleScreen));
        Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.Kind == BotEventKind.Game && e.MsgId != MsgId.Heartbeat);
        // The role screen still works: a role is created and enters the world.
        await bot.EnterWorldAsync((await bot.CreateRoleAsync("Screen")).RoleBaseInfo.Id);
    }

    [Fact]
    public async Task BeforeTheAccountSignsInOnlyTheHeartbeatIsAnswered()
    {
        await using var bot = new BotClient(_http, _pem);
        await bot.LoginAsync("early", "pw");
        await bot.OpenGateAsync();

        Assert.Empty(await SendRefused(bot, SessionStage.Connected));
        Assert.All(bot.Inbox.Snapshot().Where(e => e.Kind == BotEventKind.Game),
            e => Assert.Equal(MsgId.Heartbeat, e.MsgId));
    }
}
