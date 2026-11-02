using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>LeaderBoardsGiftConfig rank rewards: the settlement times of _kPrizesTimeNew and the mailed rank bands.</summary>
public sealed class RankRewardTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-rankreward-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private string _http = "";
    private T Service<T>() where T : notnull => _app!.Services.GetRequiredService<T>();

    private readonly TestClock _clock = new();

    /// <summary>From Thursday 8 Oct 2026, 12:00 UTC, in a UTC realm.</summary>
    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void AdvanceDays(int count) => Interlocked.Add(ref _seconds, count * 86400L);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task StartAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        if (_app != null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<long> Role(string name)
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        return (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
    }

    private List<MailService.Letter> Letters(long role) => Service<RoleStore>().LoadMailbox(role) is { } box
        ? JsonSerializer.Deserialize<MailService.Mailbox>(box)!.Letters : [];

    [Theory]
    [InlineData(2026, 10, 8, 4, 59, "2026-10-07 05:00")] // before 05:00: yesterday's settlement
    [InlineData(2026, 10, 8, 5, 0, "2026-10-08 05:00")]
    [InlineData(2026, 10, 8, 23, 0, "2026-10-08 05:00")]
    public void DailySettlementIsTheLastFiveOClock(int y, int m, int d, int h, int min, string expected)
    {
        var at = RankRewardService.LastSettlement([[1, 5]], new DateTime(y, m, d, h, min, 0));
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), at);
    }

    [Fact]
    public void WeeklySettlementIsTheLastWeekdayAtItsHour()
    {
        // Thursday 8 Oct 2026: [2, 1, 5] is Monday 5 Oct 05:00; [2, 4, 5] before 05:00 is the Thursday a week ago.
        Assert.Equal(new DateTime(2026, 10, 5, 5, 0, 0), RankRewardService.LastSettlement([[2, 1, 5]], new DateTime(2026, 10, 8, 12, 0, 0)));
        Assert.Equal(new DateTime(2026, 10, 1, 5, 0, 0), RankRewardService.LastSettlement([[2, 4, 5]], new DateTime(2026, 10, 8, 4, 0, 0)));
        Assert.Equal(new DateTime(2026, 10, 4, 5, 0, 0), RankRewardService.LastSettlement([[2, 7, 5]], new DateTime(2026, 10, 8, 12, 0, 0)));
        Assert.Null(RankRewardService.LastSettlement([[3, 118]], new DateTime(2026, 10, 8, 12, 0, 0)));
    }

    private Task<bool> Settle(LeaderBoardsConfig board, string period) =>
        Service<GameLoop>().InvokeAsync(() => Service<RankRewardService>().SettleAsync(board, period));

    [ClientDataFact]
    public async Task EveryRewardedBoardSettlesDailyAndMailsItsBandOncePerSettlement()
    {
        await StartAsync();
        var data = Service<GameData>();
        var service = Service<RankRewardService>();
        var due = service.Due().ToList();
        Assert.Equal(LeaderBoardsConfig.All(data).Where(b => b.RankRewards.Length > 0).Select(b => b.Id).Order(),
            due.Select(d => d.Board.Id).Order());
        Assert.All(due, d => Assert.Equal("2026100805", d.Period));

        long first = await Role("Champion"), second = await Role("Runnerup");
        var board = LeaderBoardsConfig.Get(data, 1301)!.Value;
        service.Standings = (id, count) => Task.FromResult<IReadOnlyList<(long, long)>>(
            id == board.Id ? [(first, 900), (second, 800)] : []);
        Assert.True(await Settle(board, "2026100805"));
        Assert.True(await Settle(board, "2026100805")); // the same settlement again pays nothing more
        var band = LeaderBoardsGiftConfig.Get(data, board.RankRewards[0])!.Value;
        var goods = Service<AwardService>().ResolveForProgress(Service<RoleStore>().LoadProgress(first), band.Reward, 101).Grants;
        foreach (long role in new[] { first, second })
        {
            var letter = Assert.Single(Letters(role), l => l.TemplateId == board.MailId);
            Assert.Equal(goods.Select(g => (g.Entry, g.Count)), letter.Attachments.Select(a => (a.Entry, a.Count)));
        }

        // The next day's 05:00 settlement comes from the server's own tick.
        _clock.AdvanceDays(1);
        for (int i = 0; i < 100 && Letters(second).Count(l => l.TemplateId == board.MailId) < 2; i++) await Task.Delay(100);
        Assert.Equal(2, Letters(first).Count(l => l.TemplateId == board.MailId));
        Assert.Equal(2, Letters(second).Count(l => l.TemplateId == board.MailId));
    }
}
