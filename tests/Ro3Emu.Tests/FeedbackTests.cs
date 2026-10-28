using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// In-game bug reports (FeedbackService, docs/guide/feedback.md): "/bug" in chat and the Feedback button's private message
/// store one JSON line each with who/where/when and recent log context; loopback GET /ro3emu/reports reads them.
/// </summary>
public sealed class FeedbackTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-feedback-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.Feedback.CooldownSeconds = 0;
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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
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

    private static async Task<string> Marquee(BotClient bot, string contains) =>
        BroadCastNotify.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
            && BroadCastNotify.Parser.ParseFrom(e.Body).Content.Contains(contains), Wait)).Body).Content;

    private async Task<List<FeedbackReport>> Reports(string query = "")
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"{_http}/ro3emu/reports{query}");
        return JsonSerializer.Deserialize<List<FeedbackReport>>(json, FeedbackStore.Json)!;
    }

    private GameSession Session(string account) =>
        _app.Services.GetRequiredService<SessionRegistry>().All().First(s => s.Account == account);

    [Fact]
    public async Task SlashBugInChatIsStoredWithWhoWhereAndContextAndShownToNobody()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await alice.JoinChatAsync("4_1");
        await bob.JoinChatAsync("4_1");

        await alice.SendChatAsync("4_1", 2, "/bug the Prontera gate door is stuck", channelIndex: 4);
        Assert.Contains("Report #1", await Marquee(alice, "Report #"));
        // A normal line afterwards reaches Bob; the report line before it did not.
        await alice.SendChatAsync("4_1", 2, "hello", channelIndex: 4);
        Assert.Equal("hello", (await bob.NextChatLineAsync()).Content);

        var report = Assert.Single(await Reports());
        var session = Session("alice");
        Assert.Equal(1, report.Id);
        Assert.Equal("chat", report.Source);
        Assert.Equal("alice", report.Account);
        Assert.Equal("Alice", report.RoleName);
        Assert.Equal(session.Role!.Id, report.RoleId);
        Assert.Equal(session.SceneEntry, report.Scene);
        Assert.NotEqual(0, report.Scene);
        Assert.Equal(Math.Round(session.Position.X, 1), report.X, 1);
        Assert.Equal("the Prontera gate door is stuck", report.Text);
        Assert.False(report.Truncated);
        Assert.True((DateTimeOffset.UtcNow - report.Time).Duration() < TimeSpan.FromMinutes(1));
        // Recent server log lines about this player ("Role Alice (...) of alice enters scene ...").
        Assert.Contains(report.Context, l => l.Contains("enters scene") && l.Contains("alice"));
        Assert.DoesNotContain(report.Context, l => l.Contains("Bob"));
        Assert.True(File.Exists(Path.Combine(_dataDir, "reports", "feedback.jsonl")));
    }

    [Fact]
    public async Task TheButtonMessageStoresAReportAndSinceFiltersByIdAndTime()
    {
        await using var alice = await InWorld("Alice");
        await alice.JoinChatAsync("4_1");
        await alice.SendChatAsync("4_1", 2, "/BUG first", channelIndex: 4);
        await Marquee(alice, "Report #1");
        var between = DateTimeOffset.UtcNow;
        await Task.Delay(50);
        await alice.SendGameBytesAsync(FeedbackService.SubmitMsg, Encoding.UTF8.GetBytes("  Quest log froze\r\nafter the cutscene \u0001 "));
        await Marquee(alice, "Report #2");

        var all = await Reports();
        Assert.Equal(new long[] { 1, 2 }, all.Select(r => r.Id));
        var button = all[1];
        Assert.Equal("button", button.Source);
        Assert.Equal("Quest log froze\nafter the cutscene", button.Text); // control characters out, new line kept
        Assert.Equal(new long[] { 2 }, (await Reports("?since=1")).Select(r => r.Id));
        Assert.Equal(new long[] { 2 }, (await Reports("?since=" + Uri.EscapeDataString(between.ToString("O")))).Select(r => r.Id));
        Assert.Equal(new long[] { 1 }, (await Reports("?limit=1")).Select(r => r.Id));

        using var http = new HttpClient();
        var text = await http.GetStringAsync($"{_http}/ro3emu/reports?format=text");
        Assert.Contains("#2 ", text);
        Assert.Contains("> Quest log froze", text);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"{_http}/ro3emu/reports?since=yesterday-ish")).StatusCode);
    }

    [Fact]
    public async Task EmptyReportsAndRapidRepeatsAreRefused()
    {
        _app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value.Feedback.CooldownSeconds = 60;
        await using var alice = await InWorld("Alice");
        await alice.JoinChatAsync("4_1");
        await alice.SendChatAsync("4_1", 2, "/bug", channelIndex: 4);
        Assert.Contains("Usage: /bug", await Marquee(alice, "Usage"));
        await alice.SendChatAsync("4_1", 2, "/feedback the shop is empty", channelIndex: 4);
        await Marquee(alice, "Report #1");
        await alice.SendChatAsync("4_1", 2, "/bug again", channelIndex: 4);
        await Marquee(alice, "wait a few seconds");
        Assert.Equal(new[] { "the shop is empty" }, (await Reports()).Select(r => r.Text));
    }

    [Theory]
    [InlineData("/bug door stuck", true, "door stuck")]
    [InlineData("  /Bug   two  words ", true, "two  words")]
    [InlineData("/bug", true, "")]
    [InlineData("/feedback x", true, "x")]
    [InlineData("/bugs are fun", false, "")]
    [InlineData("bug /bug", false, "")]
    [InlineData("!bug x", false, "")]
    public void ChatCommandParsing(string line, bool matches, string text)
    {
        Assert.Equal(matches, FeedbackService.TryParseChatCommand(line, out var rest));
        Assert.Equal(text, rest);
    }

    [Fact]
    public void TheStoreSkipsABrokenLineAndKeepsCountingAndCutsNothing()
    {
        var path = Path.Combine(_dataDir, "store-test", "feedback.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"id\":7,\"text\":\"old\",\"source\":\"chat\"}\n{\"id\":8,\"text\":\"half");
        var store = new FeedbackStore(path);
        Assert.Equal(8, store.Append(new FeedbackReport { Text = "ünïcödé \"quoted\"", Source = "chat" }));
        var all = store.Read();
        Assert.Equal(new long[] { 7, 8 }, all.Select(r => r.Id));
        Assert.Equal("ünïcödé \"quoted\"", all[1].Text);
        // A fresh store (a restart) continues after the highest id.
        Assert.Equal(9, new FeedbackStore(path).Append(new FeedbackReport { Text = "next" }));
    }
}
