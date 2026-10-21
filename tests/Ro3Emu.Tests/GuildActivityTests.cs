using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// GuildService.Activity: guild currencies from awards move into the guild, the weekly activity chests (32311), the
/// Fund Rankings (32291) and last week's activity dividend (32300) after the Monday 05:00 turn.
/// </summary>
public sealed class GuildActivityTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-guildact-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    private sealed class Clock : TimeProvider
    {
        // A Thursday afternoon.
        private long _seconds = new DateTimeOffset(2026, 10, 15, 14, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void AdvanceDays(int days) => Interlocked.Add(ref _seconds, days * 86400L);
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    public async Task InitializeAsync()
    {
        var (web, micro, gate) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = gate;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private async Task Until(Func<Task<bool>> condition, string what)
    {
        for (int i = 0; i < 500; i++) { if (await condition()) return; await Task.Delay(20); }
        Assert.Fail("timed out: " + what);
    }

    private async Task<long> CreateGuild(BotClient bot, string name)
    {
        await bot.SendGameAsync(GuildService.Create, new GuildCreateReq
        { Name = name, Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        Assert.Equal(0, (await Next(bot, GuildService.Create, GuildCreateResp.Parser)).Result);
        return (await Next(bot, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid > 0)).Guid;
    }

    private async Task<int> Chest(BotClient bot, long id)
    {
        await bot.SendGameAsync(GuildService.ReceiveWeekActiveReward, new ReceiveGuildWeekActiveRewardReq { RewardId = id });
        return (await Next(bot, GuildService.ReceiveWeekActiveReward, ReceiveGuildWeekActiveRewardRes.Parser)).Result;
    }

    private async Task<TakeActivityBonusRes> Dividend(BotClient bot)
    {
        await bot.SendGameAsync(GuildService.TakeActivityBonus, new TakeActivityBonusReq());
        return await Next(bot, GuildService.TakeActivityBonus, TakeActivityBonusRes.Parser);
    }

    [ClientDataFact]
    public async Task GuildCurrenciesMoveIntoTheGuildAndPayChestsRankingsAndDividends()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 35");
        long guildId = await CreateGuild(alice, "Astra");
        var s = Session(alice);
        GuildStore.State Guild() => Service<GuildStore>().Get(guildId)!;
        long contributeBefore = await Read(() => Guild().Contribute);

        // A guild quest's award: activity, contribution and funds land with the guild, not in the player's purse.
        await Gm(alice, "!item 106 12000");
        await Gm(alice, "!item 107 500");
        await Gm(alice, "!item 104 20");
        await Until(async () => await Read(() => Guild().CurActivity) == 12000 && await Read(() => Guild().Money) >= 20,
            "the deposit into the guild");
        Assert.Equal(0, await Read(() => s.Progress!.Currency.GetValueOrDefault(106)));
        Assert.Equal(0, await Read(() => s.Progress!.Currency.GetValueOrDefault(107)));
        Assert.Equal(contributeBefore + 500, await Read(() => Guild().Contribute));
        Assert.Equal(12000, await Read(() => Guild().Members[s.Role!.Id].WeekActivity));
        Assert.Equal(20, await Read(() => Guild().Members[s.Role!.Id].ContributeMoney));
        await Next(alice, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            n => n.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangeGuildActivity && n.Activity.CurActivity == 12000);

        // Chest 9001 needs 10000 activity, 9002 needs 20000; each once a week.
        long items = await Read(() => s.Items.Sum(i => (long)i.Stack));
        Assert.Equal(0, await Chest(alice, 9001));
        Assert.True(await Read(() => s.Items.Sum(i => (long)i.Stack)) > items, "the chest's Elunium arrived");
        Assert.Equal(GuildService.ErrActivityClaimed, await Chest(alice, 9001));
        Assert.Equal(GuildService.ErrActivityShort, await Chest(alice, 9002));
        Assert.Equal(GuildService.ErrActivityRewardId, await Chest(alice, 1));

        // Fund Rankings list every member with the funds they brought in.
        await alice.SendGameAsync(GuildService.MoneyRank, new GuildMoneyRankReq());
        var rank = await Next(alice, GuildService.MoneyRank, GuildMoneyRankResp.Parser);
        Assert.Equal(20, Assert.Single(rank.PlayerHistoryData).ContributeMoney);

        // No dividend before a week has turned with the member inside.
        Assert.NotEqual(0, (await Dividend(alice)).Result);

        // Next week: activity starts over, last week's chests expired, last week's dividend is claimable once.
        _clock.AdvanceDays(7);
        Assert.Equal(GuildService.ErrActivityShort, await Chest(alice, 9001));
        long zeny = await Read(() => Service<CharacterService>().CountOf(s, 1));
        var paid = await Dividend(alice);
        Assert.Equal(0, paid.Result);
        // Guild 12000 (grade B, raised to the new guild's protected grade A: 10000) + member 12000 (S: 8000)
        // + daily activity (not tracked, lowest grade: 1000).
        Assert.Equal(19000, Assert.Single(paid.BaseInfo).Stack);
        Assert.Equal(zeny + 19000, await Read(() => Service<CharacterService>().CountOf(s, 1)));
        Assert.Equal(GuildService.ErrDividendClaimed, (await Dividend(alice)).Result);
    }
}
