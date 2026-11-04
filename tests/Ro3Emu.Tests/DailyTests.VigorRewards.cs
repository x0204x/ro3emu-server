using Google.Protobuf;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class DailyTests
{
    private async Task SeedVigorRewardAsync(long balance, long legacyCurrency = 0)
    {
        long role = await State(s => s.Role!.Id);
        await OwnedFixtureAsync(role, "活力獎勵測試資料", async s =>
        {
            var roles = _app.Services.GetRequiredService<RoleStore>();
            var next = s.Restrictions!.Clone();
            next.RestrictCount[1004].Count = balance;
            s.Progress!.Currency[407] = legacyCurrency;
            var tx = roles.NewTransaction();
            tx.Progress(role, s.Progress);
            tx.RoleBlob(role, "life_restrict", next.ToByteArray());
            await roles.CommitAsync(tx, "fixture/vigor", _ => s.Restrictions = next);
        });
    }

    private async Task PrepareVigorClaimAsync()
    {
        await Gm("alice", "!lvl 34");
        await Gm("alice", "!quest done 10105090");
        await Gm("alice", "!item 5 60");
    }

    [ClientDataFact]
    public async Task VigorStageClaimKeepsCurrencyOnceAndPersistsAcrossRelog()
    {
        var bot = await InWorld("Alice");
        await PrepareVigorClaimAsync();
        await SeedVigorRewardAsync(500, 321);
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(0, (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        Assert.Equal(500, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(521, await State(s => s.Progress!.Currency[407]));
        Assert.Equal(1021, await State(s => ((DBRestrictInfo)s.Synced[LifeJobService.RestrictSync]).RestrictCount[1004].Count));
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(ErrorCodes.RewardClaimed, (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        Assert.Equal(500, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(521, await State(s => s.Progress!.Currency[407]));
        await bot.DisposeAsync();
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(500, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(5000, await State(s => s.Restrictions!.RestrictCount[1004].LimitCount));
        Assert.Equal(521, await State(s => s.Progress!.Currency[407]));
        await again.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(ErrorCodes.RewardClaimed, (await Next(again, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task VigorBatchStageClaimKeepsTheConfiguredThousandOnlyOnce()
    {
        await using var bot = await InWorld("Alice");
        await PrepareVigorClaimAsync();
        await SeedVigorRewardAsync(100);
        var request = new StageRewardBatchGainReq { StageIds = { 7001, 7002, 7003, 7001 } };
        await bot.SendGameAsync(DailyActivityService.StageBatchGain, request);
        Assert.Equal(0, (await Next(bot, DailyActivityService.StageBatchGain, StageRewardBatchGainResp.Parser)).Result);
        Assert.Equal(100, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(1000, await State(s => s.Progress!.Currency[407]));
        await bot.SendGameAsync(DailyActivityService.StageBatchGain, request);
        Assert.Equal(ErrorCodes.RewardClaimed, (await Next(bot, DailyActivityService.StageBatchGain, StageRewardBatchGainResp.Parser)).Result);
        Assert.Equal(100, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(1000, await State(s => s.Progress!.Currency[407]));
    }

    [ClientDataTheory]
    [InlineData(4900, 5000)]
    [InlineData(5000, 5000)]
    [InlineData(5200, 5200)]
    public async Task VigorStageClaimPreservesCurrencyAboveDisplayLimit(long before, long expected)
    {
        await using var bot = await InWorld("Alice");
        await PrepareVigorClaimAsync();
        await SeedVigorRewardAsync(before);
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(0, (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        Assert.Equal(before, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Contains(7001, await State(s => s.Daily!.Claims));
        Assert.Equal(200, await State(s => s.Progress!.Currency[407]));
        Assert.Equal(expected, await State(s => ((DBRestrictInfo)s.Synced[LifeJobService.RestrictSync]).RestrictCount[1004].Count));
    }

    [ClientDataFact]
    public async Task VigorCurrencySaveFailureRollsBackTheWholeStageAndRetryPaysOnce()
    {
        await using var bot = await InWorld("Alice");
        await PrepareVigorClaimAsync();
        await SeedVigorRewardAsync(500, 321);
        long role = await State(s => s.Role!.Id);
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var previousDaily = roles.LoadDaily(role);
        var previousVigor = roles.LoadLifeRestrict(role);
        var previous = await State(s => (s.Progress!.Exp, Currency: s.Progress.Currency.OrderBy(x => x.Key).ToArray(),
            Items: s.Items.Select(i => (i.Entry, i.Stack, i.BindType)).ToArray()));
        Sql("CREATE TRIGGER fail_vigor_reward BEFORE UPDATE OF currency ON role BEGIN SELECT RAISE(ABORT, 'vigor fixture'); END");
        try
        {
            await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
            Assert.Equal(ErrorCodes.Unknown, (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
            Assert.Equal(previousDaily, roles.LoadDaily(role));
            Assert.Equal(previousVigor, roles.LoadLifeRestrict(role));
            Assert.Equal(500, await State(s => s.Restrictions!.RestrictCount[1004].Count));
            Assert.Equal(previous.Exp, await State(s => s.Progress!.Exp));
            Assert.Equal(previous.Currency, await State(s => s.Progress!.Currency.OrderBy(x => x.Key).ToArray()));
            Assert.Equal(previous.Items, await State(s => s.Items.Select(i => (i.Entry, i.Stack, i.BindType)).ToArray()));
            Assert.DoesNotContain(7001, await State(s => s.Daily!.Claims));
        }
        finally { Sql("DROP TRIGGER fail_vigor_reward"); }
        await bot.SendGameAsync(DailyActivityService.StageGain, new StageRewardGainReq { StageId = 7001 });
        Assert.Equal(0, (await Next(bot, DailyActivityService.StageGain, StageRewardGainResp.Parser)).Result);
        Assert.Equal(500, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(521, await State(s => s.Progress!.Currency[407]));
    }

    [ClientDataFact]
    public async Task VigorExchangeKeepsOrdinaryCurrencyAtomic()
    {
        await using var bot = await InWorld("Alice");
        await SeedVigorRewardAsync(500, 321);
        long role = await State(s => s.Role!.Id);
        Sql("CREATE TRIGGER fail_vigor_exchange BEFORE UPDATE OF currency ON role BEGIN SELECT RAISE(ABORT, 'vigor exchange fixture'); END");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => OwnedFixtureAsync(role, "活力交易失敗", async s =>
                await _app.Services.GetRequiredService<CharacterService>().ExchangeAsync(s, [], [(407, 200)])));
            Assert.Equal(500, await State(s => s.Restrictions!.RestrictCount[1004].Count));
            Assert.Equal(321, await State(s => s.Progress!.Currency[407]));
        }
        finally { Sql("DROP TRIGGER fail_vigor_exchange"); }
        await OwnedFixtureAsync(role, "活力交易重試", async s =>
            Assert.NotNull(await _app.Services.GetRequiredService<CharacterService>().ExchangeAsync(s, [], [(407, 200)])));
        Assert.Equal(500, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(521, await State(s => s.Progress!.Currency[407]));
    }

    [ClientDataFact]
    public async Task VigorCurrencyCanPayExchangeAndAwardCosts()
    {
        await using var bot = await InWorld("Alice");
        await SeedVigorRewardAsync(500, 321);
        long role = await State(s => s.Role!.Id);
        await OwnedFixtureAsync(role, "活力交易支付", async s =>
            Assert.NotNull(await _app.Services.GetRequiredService<CharacterService>()
                .ExchangeAsync(s, [(407, 100)], [(407, 200)])));
        Assert.Equal(421, await State(s => s.Progress!.Currency[407]));
        await OwnedFixtureAsync(role, "活力獎勵成本", async s =>
            Assert.True(await _app.Services.GetRequiredService<CharacterService>().ApplyAwardAsync(s,
                new AwardPlan(0, 0, [new AwardGrant(407, 200, 0)]), costs: [(407, 100)])));
        Assert.Equal(500, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(521, await State(s => s.Progress!.Currency[407]));
    }

    [ClientDataFact]
    public async Task VigorLifeCommitKeepsCurrencyRewardAndRestrictionCostAtomic()
    {
        await using var bot = await InWorld("Alice");
        await SeedVigorRewardAsync(500, 321);
        long role = await State(s => s.Role!.Id);
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var original = roles.LoadLifeRestrict(role);
        async Task Execute() => await OwnedFixtureAsync(role, "生活消耗與活力獎勵同筆提交", async s =>
        {
            using var gate = await CharacterService.Lock(s);
            var next = s.Restrictions!.Clone();
            next.RestrictCount[1004].Count -= 20;
            next.RestrictCount[1004].HistoryUseCount += 20;
            next.RestrictCount[1005].Count -= 1;
            var runtime = JsonSerializer.Deserialize<LifeJobService.RuntimeState>(JsonSerializer.SerializeToUtf8Bytes(s.LifeRuntime))!;
            runtime.EnergySpent += 20;
            // 直接覆蓋生活製造的交易邊界；輸出 407 為合成案例，未改動原始配方資料。
            var method = typeof(LifeJobService).GetMethod("CommitAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task<List<InvItem>?>)method.Invoke(_app.Services.GetRequiredService<LifeJobService>(),
                [s, "cd13c/life-forge", s.LifeJob!.Clone(), next, runtime,
                    Array.Empty<(int Entry, long Count)>(), new AwardGrant[] { new(407, 200, 0) },
                    null, null, null, new { Fixture = "活力消耗與獎勵合成" }, 0L])!;
            Assert.NotNull(await task);
        });
        Sql("CREATE TRIGGER fail_composed_vigor BEFORE UPDATE OF life_restrict ON role BEGIN SELECT RAISE(ABORT, 'composed fixture'); END");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(Execute);
            Assert.Equal(original, roles.LoadLifeRestrict(role));
            Assert.Equal(500, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        }
        finally { Sql("DROP TRIGGER fail_composed_vigor"); }
        await Execute();
        var saved = DBRestrictInfo.Parser.ParseFrom(roles.LoadLifeRestrict(role)!);
        Assert.Equal(480, saved.RestrictCount[1004].Count);
        Assert.Equal(20, saved.RestrictCount[1004].HistoryUseCount);
        Assert.Equal(4, saved.RestrictCount[1005].Count);
        Assert.Equal(saved.ToByteArray(), await State(s => s.Restrictions!.ToByteArray()));
        Assert.Equal(521, await State(s => s.Progress!.Currency[407]));
    }

    [ClientDataFact]
    public async Task VigorCompensationMailClaimCreditsRestrictionAndCannotReplay()
    {
        var bot = await InWorld("Alice");
        await SeedVigorRewardAsync(500, 321);
        long role = await State(s => s.Role!.Id);
        var mail = _app.Services.GetRequiredService<MailService>();
        string id = await mail.SendTemplateAsync(role, 1058, [new MailService.Attachment(407, 200)]);
        var request = new ClaimMailReq { MailUid = { id } };
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var previousMailbox = roles.LoadMailbox(role);
        var previousVigor = roles.LoadLifeRestrict(role);
        Sql("CREATE TRIGGER fail_vigor_mail BEFORE UPDATE OF life_restrict ON role BEGIN SELECT RAISE(ABORT, 'mail vigor fixture'); END");
        try
        {
            await bot.SendGameAsync(MailService.ClaimMail, request);
            Assert.Equal(ErrorCodes.Unknown, (await Next(bot, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
            Assert.Equal(previousMailbox, roles.LoadMailbox(role));
            Assert.Equal(previousVigor, roles.LoadLifeRestrict(role));
            Assert.Equal(500, await State(s => s.Restrictions!.RestrictCount[1004].Count));
            Assert.Equal(321, await State(s => s.Progress!.Currency[407]));
        }
        finally { Sql("DROP TRIGGER fail_vigor_mail"); }
        await bot.SendGameAsync(MailService.ClaimMail, request);
        Assert.Equal(0, (await Next(bot, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(700, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(321, await State(s => s.Progress!.Currency[407]));
        await bot.SendGameAsync(MailService.ClaimMail, request);
        Assert.Equal(ErrorCodes.MailAlreadyClaimed, (await Next(bot, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        await bot.DisposeAsync();
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(700, await State(s => s.Restrictions!.RestrictCount[1004].Count));
        Assert.Equal(321, await State(s => s.Progress!.Currency[407]));
    }
}
