using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Http;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Tests;

/// <summary>
/// What one connection may cost the server before and after it signs in: the micro gate's request budget, NameService
/// outside the realm-wide reservation, and the SDK login's form and new-name limits. Account names are invented.
/// </summary>
public sealed class MicroGateLimitTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-microlimit-" + Guid.NewGuid().ToString("N"));
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
            o.AntiCheat.MicroRequests = "0.5/6";
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
        _pem = File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem"));
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task ASignedInConnectionOverItsBudgetIsAnsweredWithoutRunning()
    {
        await using var bot = new BotClient(_http, _pem);
        await bot.LoginAsync("budget-player", "pw");
        var answers = new List<string>();
        for (int i = 0; i < 12; i++) answers.Add(Message(await bot.MicroAsync("NameService", "getUidByName", Lookup("nobody"))));
        Assert.Contains("TID_NO_DOCUMENT", answers);
        Assert.Contains("too many requests", answers);
    }

    /// <summary>The budget counts frames, not their cost: one call asking for thousands of roles must not cost thousands.</summary>
    [Fact]
    public async Task OneProfileRequestAnswersNoMoreRolesThanTheClientAsksFor()
    {
        await using var bot = new BotClient(_http, _pem);
        await bot.LoginAsync("batch-player", "pw");
        await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("Batcher")).RoleBaseInfo.Id;
        // Different keys naming one real role, so that an answer shows the work that was done for each.
        var asks = Enumerable.Range(0, UserDataService.MaxRolesPerBatch + 30)
            .ToDictionary(i => new string('0', i) + role, _ => (object)new Dictionary<string, object> { ["Base"] = 0 });
        var answer = await bot.MicroAsync("UserDataService", "getAttributeBatchByVersion", new() { ["UidFieldVersions"] = asks });
        Assert.Equal(UserDataService.MaxRolesPerBatch, Assert.IsType<object[]>(answer["Results"]).Length);
    }

    [Fact]
    public async Task AConnectionThatKeepsGoingFarOverItsBudgetIsClosed()
    {
        await using var bot = new BotClient(_http, _pem);
        await bot.LoginAsync("flood-player", "pw");
        var sessions = _app.Services.GetRequiredService<MicroSessions>();
        Assert.NotNull(sessions.For("flood-player"));
        // The last call gets no answer: the gate closes the connection instead.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            for (int i = 0; i < MicroSession.MaxRefusedFrames + 50; i++)
                await bot.MicroAsync("NameService", "getUidByName", Lookup("nobody")).WaitAsync(TimeSpan.FromSeconds(3));
        });
        for (int i = 0; i < 100 && sessions.For("flood-player") != null; i++) await Task.Delay(50);
        Assert.Null(sessions.For("flood-player"));
    }

    [Fact]
    public async Task NameLookupsDoNotWaitBehindTheRealmReservation()
    {
        await using var bot = new BotClient(_http, _pem);
        await bot.LoginAsync("lookup-player", "pw");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var owners = _app.Services.GetRequiredService<OwnerRegistry>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = loop.InvokeAsync(async () =>
        {
            using var scope = await owners.EnterAsync("test/hold-realm", ReservationKey.Realm);
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var lookup = bot.MicroAsync("NameService", "getUidByName", Lookup("nobody"));
            Assert.Equal("TID_NO_DOCUMENT", Message(await lookup.WaitAsync(TimeSpan.FromSeconds(10))));
        }
        finally { release.SetResult(); }
        await held.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TheSdkLoginRefusesHugeFormsAndOnlyRegistersPrintableNames()
    {
        using var client = new HttpClient();
        using var huge = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = new string('a', 10_000), ["credential"] = "pw" });
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(_http + SdkEndpoints.LoginPath, huge)).StatusCode);

        Assert.Equal(401, await CodeAsync(client, "sdk name", "pw"));
        Assert.Equal(401, await CodeAsync(client, new string('n', 65), "pw"));
        Assert.Equal(401, await CodeAsync(client, "sdk-long-password", new string('p', 1025)));
        Assert.Equal(0, await CodeAsync(client, "sdk-new", "pw"));
        Assert.Equal(0, await CodeAsync(client, "sdk-new", "pw"));
    }

    [Theory]
    [InlineData("admin\u202Egnp")]
    [InlineData("admin\u200B")]
    [InlineData("line\nbreak")]
    public async Task InvisibleCharactersCannotNameANewAccount(string name)
    {
        using var client = new HttpClient();
        Assert.Equal(401, await CodeAsync(client, name, "pw"));
    }

    [Theory]
    [InlineData("evil\nfail: FORGED LINE")]
    [InlineData("evil\rFORGED")]
    [InlineData("evil\u2028FORGED")]
    [InlineData("evil\u202EFORGED")]
    public void ClientTextCannotWriteALineOfItsOwn(string text)
    {
        string clipped = MicroServiceRouter.Clip(text);
        Assert.DoesNotContain(clipped, c => c < ' ' || c is '\u2028' or '\u202E');
        Assert.StartsWith("evil\\u", clipped, StringComparison.Ordinal);
        Assert.Contains("FORGED", clipped, StringComparison.Ordinal);
    }

    private async Task<int> CodeAsync(HttpClient client, string identity, string credential)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = identity, ["credential"] = credential });
        var body = await (await client.PostAsync(_http + SdkEndpoints.LoginPath, form)).Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.GetProperty("code").GetInt32();
    }

    private static Dictionary<string, object?> Lookup(string name) => new() { ["Type"] = "Area0", ["Name"] = name };

    private static string Message(Dictionary<object, object?> response) =>
        (string)((object[])response["Ret"]!)[1]!;
}
