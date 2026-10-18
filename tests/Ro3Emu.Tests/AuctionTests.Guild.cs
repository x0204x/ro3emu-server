using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class AuctionContentTests
{
    [ClientDataFact]
    public async Task CommittedChallengeRetryRetainsOriginalPlayBookAndOpenDay()
    {
        await using var alice = await InWorld("Alice");
        long guild = await AuctionGuild(alice);
        long role = (long)alice.Self!.Uuid;
        Service<SeasonService>().SetPlayBook(102);
        Assert.Equal(ErrorCodes.NoPermission,
            await Service<AuctionService>().PublishGuildChallengeAsync("old-cycle", guild, 1001, 1, [role]));
        Assert.Equal(0, await Service<AuctionService>().PublishGuildChallengeAsync("old-cycle", guild, 1001, 1,
            [role], committedParticipants: true, rewardPlayBook: 101, rewardOpenDay: 1));
        Assert.InRange(Service<AuctionStore>().Items().Count, 14, 15);
    }

    private async Task<long> AuctionGuild(BotClient leader, params BotClient[] members)
    {
        foreach (var bot in members.Prepend(leader)) await Gm(bot, "!lvl 99");
        await leader.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Sales",
            Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(leader, GuildService.Create, GuildCreateResp.Parser)).Result);
        long guild = (await Next(leader, GuildService.Full, GuildFullInfoNtf.Parser, n => n.Guid > 0)).Guid;
        await leader.SendGameAsync(GuildService.Edit, new GuildEditReq
        { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(leader, GuildService.Edit, GuildEditResp.Parser)).Result);
        foreach (var bot in members)
        {
            await bot.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guild } });
            Assert.Equal(0, Assert.Single((await Next(bot, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
            Assert.Equal(guild, (await Next(bot, GuildService.Full, GuildFullInfoNtf.Parser, n => n.IsJoin)).Guid);
        }
        return guild;
    }

    [ClientDataFact]
    public async Task GuildStagePublishesOnceAndMemberBuysWithParticipantDividendAndMailClaim()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long guild = await AuctionGuild(alice, bob);
        await Gm(bob, "!zeny 100000000");
        long participant = (long)alice.Self!.Uuid;
        var service = Service<AuctionService>();
        Assert.Equal(0, await service.PublishGuildChallengeAsync("clear-one", guild, 1001, 1, [participant]));
        var lots = Service<AuctionStore>().Items().Where(r => r.GuildId == guild).ToArray();
        Assert.NotEmpty(lots);
        Assert.All(lots, r =>
        {
            Assert.Equal(1003, r.SrcType);
            Assert.Equal(AuctionState.Public, r.State);
            Assert.Equal(participant, Assert.Single(r.Eligible));
        });
        Assert.Equal(0, await service.PublishGuildChallengeAsync("clear-one", guild, 1001, 1, [participant]));
        Assert.Equal(lots.Length, Service<AuctionStore>().Items().Count(r => r.GuildId == guild));
        await bob.SendGameAsync(AuctionService.Query, new QueryAuctionReq
        { AuctionType = AuctionType.Guild, GuildId = guild, SrcType = 1003, StartIndex = 1, EndIndex = 100 });
        Assert.Equal(lots.Length, (await Next(bob, AuctionService.Query, QueryAuctionRes.Parser)).Auctions.Count);

        clock.Advance(TimeSpan.FromMinutes(5));
        await service.ProcessPrebuysAsync();
        var bought = lots[0];
        Assert.Equal(0, (await Bid(bob, bought.Id, bought.FixedPrice, BiddingType.Fixed)).Result);
        var award = Assert.Single(Mailbox(bob).Letters, m => m.DeliveryKey == $"auction:{bought.Id}:award");
        Assert.Equal(1042, award.TemplateId);
        Assert.NotNull(award.ContentParams);
        // The client decoder (LC_DAL_Mail._decodeGetAuction) reads price, currency item, auction tab and item as integers.
        var awardParams = LanguageData.Parser.ParseFrom(award.ContentParams!).Param;
        Assert.All(awardParams, p => Assert.Equal(LanguageParamType.Int, p.Tp));
        Assert.Equal([(long)bought.FixedPrice, bought.Currency, 1003L, bought.Entry], awardParams.Select(p => p.IntParam).ToArray());
        var dividend = Assert.Single(Mailbox(alice).Letters, m => m.DeliveryKey == $"auction:{bought.Id}:dividend:{participant}");
        Assert.Equal(1100, dividend.TemplateId);
        Assert.Equal(bought.FixedPrice, Assert.Single(dividend.Attachments).Count);
        Assert.DoesNotContain(Mailbox(bob).Letters, m => m.DeliveryKey.Contains(":dividend:"));

        int before = Session(bob).Items.Where(i => i.Entry == bought.Entry).Sum(i => i.Stack);
        await bob.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { award.Id } });
        Assert.Equal(0, (await Next(bob, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(before + 1, Session(bob).Items.Where(i => i.Entry == bought.Entry).Sum(i => i.Stack));
        await bob.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { award.Id } });
        Assert.NotEqual(0, (await Next(bob, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(before + 1, Session(bob).Items.Where(i => i.Entry == bought.Entry).Sum(i => i.Stack));
    }

    [ClientDataTheory]
    [InlineData(1001, 1)]
    [InlineData(1002, 2)]
    [InlineData(1003, 3)]
    public async Task GuildFullClearUsesFinalStageDropPoolWithoutMintingBasePreview(int challengeId, int finalStage)
    {
        await using var alice = await InWorld("Alice");
        long guild = await AuctionGuild(alice);
        long role = (long)alice.Self!.Uuid;
        var service = Service<AuctionService>();

        Assert.Equal(0, await service.PublishGuildChallengeAsync("full-clear", guild, challengeId, 0, [role]));
        var lots = Service<AuctionStore>().Items().Where(r => r.GuildId == guild).ToArray();
        // The four 100%-chance slots in each final-stage day-one pool produce 14 lots;
        // the optional fifth slot may produce one more.
        Assert.InRange(lots.Length, 14, 15);
        Assert.All(lots, lot =>
        {
            Assert.StartsWith($"guild:challenge:{guild}:{challengeId}:{finalStage}:full-clear:", lot.SourceKey);
            Assert.Equal(1003, lot.SrcType);
            Assert.DoesNotContain(lot.Entry, new[] { 1255500, 1255016, 1255011, 1255010 });
        });

        Assert.Equal(0, await service.PublishGuildChallengeAsync("full-clear", guild, challengeId, finalStage, [role]));
        Assert.Equal(lots.Select(r => r.Id).Order(), Service<AuctionStore>().Items()
            .Where(r => r.GuildId == guild).Select(r => r.Id).Order());
    }

    [ClientDataFact]
    public async Task GuildCommittedClearKeepsCapturedParticipantAfterGuildLeave()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long guild = await AuctionGuild(alice, bob);
        long participant = (long)bob.Self!.Uuid;
        await alice.SendGameAsync(GuildService.Kick, new GuildMemberKickReq
        { TarPlayerIDs = { participant }, IfSingleKick = true });
        Assert.Equal(0, (await Next(alice, GuildService.Kick, GuildMemberKickResp.Parser)).Result);

        var service = Service<AuctionService>();
        Assert.Equal(ErrorCodes.NoPermission,
            await service.PublishGuildChallengeAsync("departed", guild, 1001, 1, [participant]));
        Assert.Empty(Service<AuctionStore>().Items());
        // The trusted outbox has already captured and validated this participant at the clear boundary.
        Assert.Equal(0, await service.PublishGuildChallengeAsync("departed", guild, 1001, 1,
            [participant], committedParticipants: true));
        var lots = Service<AuctionStore>().Items().Where(r => r.GuildId == guild).ToArray();
        Assert.NotEmpty(lots);
        Assert.All(lots, lot => Assert.Equal(participant, Assert.Single(lot.Eligible)));
    }

    [ClientDataFact]
    public async Task GuildThresholdDeduplicatesParticipantsAndChallengeUsesSeasonGate()
    {
        await using var alice = await InWorld("Alice");
        long guild = await AuctionGuild(alice);
        long role = (long)alice.Self!.Uuid;
        var service = Service<AuctionService>();
        Assert.Equal(0, await service.PublishGuildLeagueAsync("league-one", guild, 1, true,
            Enumerable.Repeat(role, 20).ToArray()));
        Assert.Empty(Service<AuctionStore>().Items());
        // Challenge 1002 stage 1 explicitly maps to the empty award 1003.
        Assert.Equal(0, await service.PublishGuildChallengeAsync("empty-stage", guild, 1002, 1, [role]));
        Assert.Empty(Service<AuctionStore>().Items());
        Service<SeasonService>().SetPlayBook(102);
        Assert.Equal(102, Service<SeasonService>().PlayBook);
        Assert.Equal(ErrorCodes.NoPermission,
            await service.PublishGuildChallengeAsync("wrong-season", guild, 1001, 1, [role]));
        Assert.Empty(Service<AuctionStore>().Items());
    }

    [ClientDataFact]
    public void AuctionFixedAwardAndDayVariantsDoNotChangeDailyPoolOnlyContract()
    {
        var rewards = Service<RewardService>();
        var progress = new RoleProgress { Level = 99, Career = 1000 };
        Assert.Equal((2, 1, 0), Assert.Single(rewards.RollAuctionAwardBound(1001, progress, 1)));
        Assert.Empty(rewards.RollAwardBound(1001, progress));
        Assert.Empty(rewards.RollAuctionAwardBound(100160102, progress, 1)); // base challenge award is preview only

        // A private in-memory fixture exercises a day boundary and fixed binding without changing shared data/.
        var table = Assert.IsType<Dictionary<string, JsonElement>>(Service<GameData>().Table("AwardConfig"));
        table["900000001"] = JsonSerializer.SerializeToElement(new[]
        {
            new { _kServerOpenDay = new[] { 1, 3 }, _kAward = new[] { new[] { Entry, 2, 1 } } },
            new { _kServerOpenDay = new[] { 4, 14 }, _kAward = new[] { new[] { Entry, 5, 0 } } },
        });
        Assert.Equal((Entry, 2, 1), Assert.Single(rewards.RollAuctionAwardBound(900000001, progress, 3)));
        Assert.Equal((Entry, 5, 0), Assert.Single(rewards.RollAuctionAwardBound(900000001, progress, 4)));
        Assert.Empty(rewards.RollAuctionAwardBound(900000001, progress, 15));
    }
}
