using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.ClientHooks;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class SkillTraceTelemetryTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-skilltrace-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], options =>
        {
            options.DataDir = _dataDir;
            options.GameDataDir = Path.Combine(RepoRoot(), "data");
            options.HttpPort = httpPort;
            options.MicroGatePort = microPort;
            options.GameGatePort = gamePort;
            options.SkillTraceTelemetry.Enabled = true;
            options.SkillTraceTelemetry.AllowAllAccounts = true;
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

    [Fact]
    public void WireFormatRoundTripsAndBoundsReason()
    {
        byte[] token = Enumerable.Range(1, SkillTraceProtocol.TokenSize).Select(i => (byte)i).ToArray();
        byte[] grantBytes = SkillTraceProtocol.EncodeGrant(token, 600, 20, 40);
        Assert.True(SkillTraceProtocol.TryDecodeGrant(grantBytes, out var grant));
        Assert.Equal(token, grant.Token);
        Assert.Equal(600u, grant.TtlSeconds);
        grantBytes[17] = 1;
        grantBytes[18] = grantBytes[19] = grantBytes[20] = byte.MaxValue;
        Assert.False(SkillTraceProtocol.TryDecodeGrant(grantBytes, out _));

        var input = new SkillTraceProtocol.Report(token, 7, 1234, 31120, 1200411, 100001, 900001, 99, 77,
            1200404, 1200411, 4567, SkillTraceProtocol.Stage.HandlerCompleted, true, new string('x', 100));
        byte[] reportBytes = SkillTraceProtocol.EncodeReport(input);
        Assert.True(SkillTraceProtocol.TryDecodeReport(reportBytes, out var report));
        Assert.Equal(7u, report.Sequence);
        Assert.Equal(31120u, report.MsgId);
        Assert.Equal(1200411, report.SkillId);
        Assert.Equal(SkillTraceProtocol.MaxReasonBytes, report.Reason.Length);
        Assert.True(report.Success);
        Assert.False(SkillTraceProtocol.TryDecodeReport(reportBytes[..^1], out _));
    }

    [Fact]
    public void TelemetryCodeDefaultsStayOpenForTheCurrentAnimationInvestigation()
    {
        var options = new SkillTraceTelemetryOptions();
        Assert.True(options.Enabled);
        Assert.True(options.AllowAllAccounts);
        Assert.Empty(options.Accounts);
    }

    [Fact]
    public async Task EveryConnectionGetsAUniqueTokenAndOnlyBoundedOrderedReportsAreAccepted()
    {
        await using var alice = await InWorld("Alice");
        var grantFrame = await alice.Inbox.WaitAsync(e => e.IsGame(SkillTraceProtocol.GrantMsgId), Wait);
        Assert.True(SkillTraceProtocol.TryDecodeGrant(grantFrame.Body, out var grant));
        var service = _app.Services.GetRequiredService<SkillTraceTelemetryService>();
        var aliceSession = Session("alice");
        Assert.True(service.HasGrant(aliceSession));

        await using var bob = await InWorld("Bob");
        var bobGrantFrame = await bob.Inbox.WaitAsync(e => e.IsGame(SkillTraceProtocol.GrantMsgId), Wait);
        Assert.True(SkillTraceProtocol.TryDecodeGrant(bobGrantFrame.Body, out var bobGrant));
        Assert.True(service.HasGrant(Session("bob")));
        Assert.NotEqual(grant.Token, bobGrant.Token);

        long before = service.ServerEvents;
        await aliceSession.SendGameAsync(CombatService.UseInSkill, new UseInSkillInfo
        {
            SrcID = aliceSession.Role!.Id,
            SkillID = 1200401,
            TargetId = 900001,
            SessionId = 44,
            StartTime = 1000,
            PlayAct = true,
        });
        await aliceSession.SendGameAsync(CombatService.UseInSkill, new UseInSkillInfo
        {
            SrcID = aliceSession.Role.Id,
            SkillID = 1200401,
            TargetId = 900001,
            SessionId = 44,
            StartTime = 1000,
            PlayAct = true,
        });
        Assert.Equal(before + 2, service.ServerEvents);

        SkillTraceProtocol.Report Report(uint sequence, SkillTraceProtocol.Stage stage, int skillId = 1200401) =>
            new(grant.Token, sequence, 499 + sequence, 31100, skillId, aliceSession.Role.Id, 900001, 44, 55,
                0, 0, 1000, stage, true, "");

        byte[] first = SkillTraceProtocol.EncodeReport(Report(1, SkillTraceProtocol.Stage.RouteReceived));
        await alice.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId, first);
        await alice.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId,
            SkillTraceProtocol.EncodeReport(Report(2, SkillTraceProtocol.Stage.HandlerCompleted)));
        await alice.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId,
            SkillTraceProtocol.EncodeReport(Report(3, SkillTraceProtocol.Stage.TimelineBuilt)));
        await alice.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId,
            SkillTraceProtocol.EncodeReport(Report(4, SkillTraceProtocol.Stage.RouteReceived)));
        await alice.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId,
            SkillTraceProtocol.EncodeReport(Report(5, SkillTraceProtocol.Stage.HandlerCompleted)));
        await alice.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId,
            SkillTraceProtocol.EncodeReport(Report(6, SkillTraceProtocol.Stage.TimelineBuilt)));
        await Eventually(() => service.Accepted == 6 && service.Completed == 2);

        await alice.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId, first); // duplicate sequence
        var wrong = grant.Token.ToArray();
        wrong[0] ^= 0xff;
        await alice.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId,
            SkillTraceProtocol.EncodeReport(new SkillTraceProtocol.Report(wrong, 7, 506, 31100, 1200401,
                aliceSession.Role.Id, 900001, 44, 55, 0, 0, 1000,
                SkillTraceProtocol.Stage.HandlerCompleted, true, "")));
        await bob.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId,
            SkillTraceProtocol.EncodeReport(Report(7, SkillTraceProtocol.Stage.RouteReceived)));
        await alice.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId,
            SkillTraceProtocol.EncodeReport(new SkillTraceProtocol.Report(grant.Token, 7, 506, 99999, 1200401,
                aliceSession.Role.Id, 900001, 44, 55, 0, 0, 1000,
                SkillTraceProtocol.Stage.RouteReceived, true, "")));
        await alice.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId,
            SkillTraceProtocol.EncodeReport(Report(7, SkillTraceProtocol.Stage.RouteReceived, 999999)));
        await alice.SendGameBytesAsync(SkillTraceProtocol.ReportMsgId, new byte[300]);
        await Eventually(() => service.Unmatched == 1);
        Assert.Equal(6, service.Accepted);
        Assert.Equal(2, service.Completed);

        string path = Path.Combine(_dataDir, "logs", $"skilltrace-{DateTimeOffset.UtcNow:yyyyMMdd}.jsonl");
        await Eventually(() => File.Exists(path) && ReadShared(path).Contains("\"status\":\"complete\""));
        string log = ReadShared(path);
        Assert.Contains("\"side\":\"client\"", log);
        Assert.Contains("\"status\":\"complete\"", log);
        Assert.Contains("\"status\":\"unmatched\"", log);
        Assert.Contains("\"skillId\":1200401", log);
        Assert.DoesNotContain("\"body\":", log, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alice", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(grant.Token), log, StringComparison.OrdinalIgnoreCase);

        await aliceSession.SendGameAsync(CombatService.UseInSkill, new UseInSkillInfo
        {
            SrcID = aliceSession.Role.Id,
            SkillID = 1200402,
            TargetId = 900002,
            SessionId = 45,
            StartTime = 2000,
            PlayAct = true,
        });
        service.Reset(aliceSession);
        Assert.Equal(1, service.Gaps);
        await Eventually(() => ReadShared(path).Contains("\"status\":\"gap:grant-reset\""));
    }

    [Fact]
    public void RateLimiterAllowsConfiguredBurstThenDrops()
    {
        var limiter = new SkillTraceRateLimiter(10, 5);
        Assert.Equal(15, Enumerable.Range(0, 30).Count(_ => limiter.TryTake()));
        Assert.False(limiter.TryTake());
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

    private GameSession Session(string account) =>
        _app.Services.GetRequiredService<SessionRegistry>().All().First(session => session.Account == account);

    private static async Task Eventually(Func<bool> done)
    {
        var until = DateTime.UtcNow + Wait;
        while (!done() && DateTime.UtcNow < until) await Task.Delay(25);
        Assert.True(done());
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
