using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Wire and persistence checks for 82976 BattlePass and MonthCard.</summary>
public sealed class BattlePassActivityTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-pass-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication _app = null!;
    private string _http = "";
    private readonly TestClock _clock = new();
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void AdvanceDays(int count) => Interlocked.Add(ref _seconds, count * 86400L);
    }

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.PlayBook = 101; o.SeasonStart = "2026-10-05";
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private Task Reconcile(BotClient bot) => Service<NewActivityService>().ReconcileAsync(Session(bot));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    private static long _serial;
    private static async Task<CommonActivityRes> Common(BotClient bot, CommonActivityReq req)
    {
        req.RequestId = Interlocked.Increment(ref _serial);
        await bot.SendGameAsync(NewActivityService.Common, req);
        return await Next(bot, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == req.RequestId);
    }

    [ClientDataFact]
    public async Task PassRequiresEarnedLevelAndPremiumEntitlementAndPersistsClaims()
    {
        var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 10");
        await Reconcile(alice);
        var premature = await Common(alice, new CommonActivityReq
        {
            ActivityId = 9201, BattlePassClaimReward = new BattlePassClaimRewardReq { RewardType = 0, Levels = { 1 } },
        });
        Assert.Equal(ErrorCodes.BattlePassLevelRequired, premature.Result);

        await Gm(alice, "!item 920132 2000");
        await Reconcile(alice);
        Assert.Equal(1, Session(alice).NewActivities!.ActivityInfo[9201].ActivityParam.BattlePass.Level);
        var free = await Common(alice, new CommonActivityReq
        {
            ActivityId = 9201, BattlePassClaimReward = new BattlePassClaimRewardReq { RewardType = 0, Levels = { 1 } },
        });
        Assert.Equal(0, free.Result);
        Assert.Equal(new[] { 1 }, free.BattlePassClaimReward.ClaimedLevels);
        Assert.Equal(ErrorCodes.BattlePassClaimed, (await Common(alice, new CommonActivityReq
        {
            ActivityId = 9201, BattlePassClaimReward = new BattlePassClaimRewardReq { RewardType = 0, Levels = { 1 } },
        })).Result);
        Assert.Equal(ErrorCodes.BattlePassPremium, (await Common(alice, new CommonActivityReq
        {
            ActivityId = 9201, BattlePassClaimReward = new BattlePassClaimRewardReq { RewardType = 1, Levels = { 1 } },
        })).Result);

        await Gm(alice, "!item 920703 1");
        await Reconcile(alice);
        Assert.True(Session(alice).NewActivities!.ActivityInfo[9201].ActivityParam.BattlePass.Premium);
        Assert.Equal(0, (await Common(alice, new CommonActivityReq
        {
            ActivityId = 9201, BattlePassClaimReward = new BattlePassClaimRewardReq { RewardType = 1, Levels = { 1 } },
        })).Result);
        Assert.Equal(0, (await Common(alice, new CommonActivityReq { ActivityId = 9201, BattlePassMarkUnlockViewed = new() })).Result);
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Common(alice, new CommonActivityReq
        {
            ActivityId = 9201, BattlePassBuyLevel = new BattlePassBuyLevelReq { Count = 1 },
        })).Result);
        await alice.DisposeAsync();
        await using var again = await InWorld("Alice", create: false);
        var saved = Session(again).NewActivities!.ActivityInfo[9201].ActivityParam.BattlePass;
        Assert.Equal(1, saved.Level);
        Assert.True(saved.Premium);
        Assert.True(saved.UnlockPageViewed);
        Assert.Equal(new[] { 1 }, saved.ClaimedFreeLevels);
        Assert.Equal(new[] { 1 }, saved.ClaimedPremiumLevels);
    }

    [ClientDataFact]
    public async Task ConfiguredLevelCostAndGiftTokenSettleOnceWithRecipientMail()
    {
        await using var bob = await InWorld("Bob");
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Reconcile(alice);
        long before = Session(alice).Progress!.Currency.GetValueOrDefault(1);
        var bought = await Common(alice, new CommonActivityReq
        {
            ActivityId = 200, BattlePassBuyLevel = new BattlePassBuyLevelReq { Count = 2 },
        });
        Assert.Equal(0, bought.Result);
        Assert.Equal(2, bought.BattlePassBuyLevel.ActualCount);
        Assert.Equal(2, Session(alice).NewActivities!.ActivityInfo[200].ActivityParam.BattlePass.Level);
        Assert.Equal(before - 20, Session(alice).Progress!.Currency[1]);
        Assert.Equal(ErrorCodes.BattlePassLevel, (await Common(alice, new CommonActivityReq
        {
            ActivityId = 200, BattlePassBuyLevel = new BattlePassBuyLevelReq { Count = 0 },
        })).Result);

        await Gm(alice, "!item 1020001 2");
        long bobId = (long)bob.Self!.Uuid;
        var gift = new CommonActivityReq
        {
            ActivityId = 200, BattlePassGift = new BattlePassGiftReq { TargetPlayerId = bobId, GiftMessage = "Enjoy the pass" },
        };
        Assert.Equal(0, (await Common(alice, gift)).Result);
        Assert.Equal(ErrorCodes.BattlePassGiftPending, (await Common(alice, gift)).Result);
        var mailbox = JsonSerializer.Deserialize<MailService.Mailbox>(Service<RoleStore>().LoadMailbox(bobId)!)!;
        var letter = Assert.Single(mailbox.Letters, m => m.Title == "Battle Pass Gift");
        Assert.Equal(new MailService.Attachment(703, 1), Assert.Single(letter.Attachments));
        await bob.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { letter.Id } });
        Assert.Equal(0, (await Next(bob, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        await Gm(bob, "!lvl 99");
        await Reconcile(bob);
        Assert.True(Session(bob).NewActivities!.ActivityInfo[200].ActivityParam.BattlePass.Premium);
        Assert.Equal(0, Session(bob).Progress!.Currency.GetValueOrDefault(703));
    }

    [ClientDataFact]
    public async Task MonthCardClaimsEachOwnedCardOncePerLocalDayAndExpires()
    {
        var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 20");
        await Reconcile(alice);
        Assert.Equal(ErrorCodes.MonthCardNotOwned, (await Common(alice, new CommonActivityReq
        {
            ActivityId = 199, GainMonthCardDailyAward = new GainMonthCardDailyAwardReq { MonthCardId = { 1001 } },
        })).Result);
        await Gm(alice, "!item 701 1");
        await Gm(alice, "!item 702 1");
        await Reconcile(alice);
        var month = Session(alice).NewActivities!.ActivityInfo[199].ActivityParam.MonthCard;
        Assert.Equal(31, month.Infos[1001].LastDays); // client parser displays lastDays - 1
        Assert.Equal(31, month.Infos[1002].LastDays);
        var claim = new CommonActivityReq
        {
            ActivityId = 199,
            GainMonthCardDailyAward = new GainMonthCardDailyAwardReq { MonthCardId = { 1001, 1002 } }
        };
        Assert.Equal(0, (await Common(alice, claim)).Result);
        Assert.Equal(ErrorCodes.MonthCardClaimed, (await Common(alice, claim)).Result);
        await alice.DisposeAsync();
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(ErrorCodes.MonthCardClaimed, (await Common(again, claim)).Result);
        _clock.AdvanceDays(1);
        await Reconcile(again);
        Assert.False(Session(again).NewActivities!.ActivityInfo[199].ActivityParam.MonthCard.Infos[1001].DayAward);
        Assert.Equal(0, (await Common(again, claim)).Result);
        _clock.AdvanceDays(30);
        await Reconcile(again);
        Assert.Equal(ErrorCodes.MonthCardExpired, (await Common(again, claim)).Result);
    }
}
