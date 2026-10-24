#pragma warning disable CS1998 // arms that only send or return keep the uniform async shape
using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>Chat, teams, friends, guilds and their caravan, challenge, campfire and home, emblems.</summary>
public sealed class SocialMessages(ChatService chat, TeamService teams, EmblemService emblems, GuildService guilds, GuildCaravanService caravan, GuildChallengeService guildChallenge, FriendExtraService friendExtra, GuildCampfireService campfire, GuildHomeService guildHome, GuildHuntingService hunting, PersonalService personal, SocialOutfitService socialOutfit) : Messaging.IMessageModule
{
    private const uint SetFriendSystemMetadata = 82208, GetGuildLeagueMgrData = GuildLeagueService.MgrData;

    public void Map(Messaging.MessageRoutes routes)
    {
        // Guild-social owners admit their own guild and member keys; the route owner picks them.
        routes.AdmitEconomic(id => routes.TryGet(id, out var route) &&
            route.Owner is "guilds" or "caravan" or "campfire" or "guildChallenge" or "friendExtra" or "teams");
        routes.AdmitEconomic((_, id, body) => ChatService.IsEventOwnerRequest(id, body));
        routes.AdmitEconomic(id => id is >= 83900 and <= 83908); // guild black market
        // Guild activity chests and dividends pay awards (GuildService.Activity).
        routes.AdmitEconomic(id => id is GuildService.ReceiveWeekActiveReward or GuildService.TakeActivityBonus);
        routes.AdmitRuntime(id => id is GuildCaravanService.HelpOther, "dispatcher/owned continuation");
        // Chat acts on the sender. As Realm it queued every fighter's next cast behind it (31102 p99 1.4 s).
        routes.AdmitRole(id => id is ChatService.TunnelUp or ChatService.JoinChannel);
        routes.AdmitRole(id => id is >= 82422 and <= 82426); // emblems
        // Hunting Challenge history reads the settled week and answers its sender only.
        routes.AdmitRole(id => id is GuildHuntingService.History);
        // Profile card edits change the sender's own presets; readers see them through UserDataService.
        routes.AdmitRole(id => id is PersonalService.UpdateAppearance or PersonalService.UpdateCurPlan or PersonalService.UpdatePlanName);
        // Wearing a chat cosmetic changes the sender's own 5044; readers see it through UserDataService.
        routes.AdmitRole(id => id is SocialOutfitService.Equip or SocialOutfitService.Refresh);

        routes.Map(PersonalService.UpdateAppearance, "personal", async (session, msgId, body) =>
        {
            await personal.UpdateAppearanceAsync(session, UpdatePersonalAppearanceReq.Parser.ParseFrom(body.Span));
        });
        // A like also writes the liked role's received likes: the default Realm reservation covers both roles.
        routes.Map(PersonalService.Upvote, "personal", async (session, msgId, body) =>
        {
            await personal.UpvoteAsync(session, UpvotePersonalReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PersonalService.UpdateCurPlan, "personal", async (session, msgId, body) =>
        {
            await personal.UpdateCurPlanAsync(session, UpdateCurPersonalPlanReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PersonalService.UpdatePlanName, "personal", async (session, msgId, body) =>
        {
            await personal.UpdatePlanNameAsync(session, UpdatePersonalPlanNameReq.Parser.ParseFrom(body.Span));
        });
        // Guild Store: the guild-social owner admits the buyer's own guild keys (RunGuildAsync).
        routes.Map(GuildService.BuyCommodity, "guilds", async (session, msgId, body) =>
        {
            await guilds.BuyCommodityAsync(session, BuyGuildCommodityReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SocialOutfitService.Equip, "socialOutfit", async (session, msgId, body) =>
        {
            await socialOutfit.EquipAsync(session, EquipSocialOutfitReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SocialOutfitService.Refresh, "socialOutfit", async (session, msgId, body) =>
        {
            await socialOutfit.RefreshAsync(session);
        });

        // Not a guild-social owner: the default Realm reservation serializes the first entries of one guild, as 30400.
        routes.Map(GuildHomeService.EnterHome, "guildHome", async (session, msgId, body) =>
        {
            await guildHome.EnterAsync(session);
        });
        // A commander skill buffs other guilds' members and bosses: the default Realm reservation.
        routes.Map(GuildHuntingService.UseSkill, "guildHunt", async (session, msgId, body) =>
        {
            await hunting.UseSkillAsync(session, UseGveHuntingSkillReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildHuntingService.History, "guildHunt", async (session, msgId, body) =>
        {
            await hunting.HistoryAsync(session);
        });
        routes.Map(83242, "campfire", async (session, msgId, body) =>
        {
            await campfire.InfoAsync(session);
        });
        routes.Map(GuildChallengeService.Info, "guildChallenge", async (session, msgId, body) =>
        {
            await guildChallenge.InfoAsync(session);
        });
        routes.Map(GuildChallengeService.Open, "guildChallenge", async (session, msgId, body) =>
        {
            await guildChallenge.OpenAsync(session, GuildDungeonChallengeOpenReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildChallengeService.CallMembers, "guildChallenge", async (session, msgId, body) =>
        {
            await guildChallenge.CallMembersAsync(session, DungeonCallMemberReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(83243, "campfire", async (session, msgId, body) =>
        {
            await campfire.AccelerantAsync(session, GuildCampFireAddAccelerantReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(83244, "campfire", async (session, msgId, body) =>
        {
            await campfire.PlayerInfoAsync(session);
        });
        routes.Map(82200, "friendExtra", async (session, msgId, body) =>
        {
            await friendExtra.CreateAsync(session, CreateFriendChatGroupReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82201, "friendExtra", async (session, msgId, body) =>
        {
            await friendExtra.RenameAsync(session, RenameFriendChatGroupReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82202, "friendExtra", async (session, msgId, body) =>
        {
            await friendExtra.InviteAsync(session, InviteFriendChatGroupReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82203, "friendExtra", async (session, msgId, body) =>
        {
            await friendExtra.KickAsync(session, KickFriendChatGroupReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82204, "friendExtra", async (session, msgId, body) =>
        {
            await friendExtra.DismissAsync(session, DismissFriendChatGroupReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82205, "friendExtra", async (session, msgId, body) =>
        {
            await friendExtra.LeaveAsync(session, LeaveFriendChatGroupReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82206, "friendExtra", async (session, msgId, body) =>
        {
            await friendExtra.DndAsync(session, SetFriendChatGroupDNDReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82207, "friendExtra", async (session, msgId, body) =>
        {
            await friendExtra.FetchAsync(session);
        });
        routes.Map(82209, "friendExtra", async (session, msgId, body) =>
        {
            await friendExtra.GiftAsync(session, GiveFriendGiftReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82210, "friendExtra", async (session, msgId, body) =>
        {
            await friendExtra.RecommendAsync(session);
        });
        routes.Map(42002, "teams", async (session, msgId, body) =>
        {
            await teams.ChangeTypeAsync(session, ChangeTeamTypeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(42003, "teams", async (session, msgId, body) =>
        {
            await teams.ChangeInfoAsync(session, ChangeTeamInfoReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(42009, "teams", async (session, msgId, body) =>
        {
            await teams.RallyAsync(session, RallyTeamReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(42011, "teams", async (session, msgId, body) =>
        {
            await teams.RallyResponseAsync(session, RallyTeamMemberResponse.Parser.ParseFrom(body.Span));
        });
        routes.Map(42012, "teams", async (session, msgId, body) =>
        {
            await teams.EditorAsync(session, EditorTeamReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(42013, "teams", async (session, msgId, body) =>
        {
            await teams.GetListAsync(session, GetTeamListReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(42014, "teams", async (session, msgId, body) =>
        {
            await teams.EnterAsync(session, EnterTeamReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(42015, "teams", async (session, msgId, body) =>
        {
            await teams.SetFlagAsync(session, TeamSetFlagReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(42018, "teams", async (session, msgId, body) =>
        {
            await teams.ApplyAsync(session, ApplyListOperateReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(42031, "teams", async (session, msgId, body) =>
        {
            await teams.FollowAsync(session, TeamFollowOperReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(42034, "teams", async (session, msgId, body) =>
        {
            await teams.RecruitAsync(session, TeamRecruitReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.ReceiveWeekActiveReward, "guilds", async (session, msgId, body) =>
        {
            await guilds.ReceiveWeekRewardAsync(session, ReceiveGuildWeekActiveRewardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.TakeActivityBonus, "guilds", async (session, msgId, body) =>
        {
            await guilds.TakeActivityBonusAsync(session);
        });
        routes.Map(GuildService.RecommendPlayers, "guilds", async (session, msgId, body) =>
        {
            await guilds.RecommendPlayersAsync(session, PullRecommendPlayersReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.Events, "guilds", async (session, msgId, body) =>
        {
            await guilds.EventsAsync(session, GuildEventReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.MoneyRank, "guilds", async (session, msgId, body) =>
        {
            await guilds.MoneyRankAsync(session);
        });
        routes.Map(GuildService.Create, "guilds", async (session, msgId, body) =>
        {
            await guilds.CreateAsync(session, GuildCreateReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.CheckSensitive, "guilds", async (session, msgId, body) =>
        {
            await guilds.CheckSensitiveAsync(session, CheckSensitiveWordReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.Recommend, "guilds", async (session, msgId, body) =>
        {
            await guilds.RecommendAsync(session, GuildRecommendListReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.HelperList, "guilds", async (session, msgId, body) =>
        {
            await guilds.HelperListAsync(session, GuildCreateHelperListReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(32215, "guilds", async (session, msgId, body) =>
        {
            await guilds.CancelCreateAsync(session);
        });
        routes.Map(32220, "guilds", async (session, msgId, body) =>
        {
            await guilds.HelperOpeAsync(session, GuildCreateHelperOpeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.Convene, "guilds", async (session, msgId, body) =>
        {
            await guilds.ConveneAsync(session);
        });
        routes.Map(GuildService.ConfirmConvene, "guilds", async (session, msgId, body) =>
        {
            await guilds.ConfirmConveneAsync(session, ConfirmConveneReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.PullHelperApply, "guilds", async (session, msgId, body) =>
        {
            await guilds.PullHelperApplyAsync(session);
        });
        routes.Map(GuildService.HelperApplyOpe, "guilds", async (session, msgId, body) =>
        {
            await guilds.HelperApplyOpeAsync(session, CreateHelperApplyOpeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.InviteHelperToken, "guilds", async (session, msgId, body) =>
        {
            await guilds.InviteHelperTokenAsync(session, InviteCreateHelperTokenReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.InviteHelperOpe, "guilds", async (session, msgId, body) =>
        {
            await guilds.InviteHelperOpeAsync(session, InviteCreateHelperOpeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(32221, "guilds", async (session, msgId, body) =>
        {
            await guilds.HelperLeaveAsync(session, GuildCreateHelperLeaveReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(32257, "guilds", async (session, msgId, body) =>
        {
            await guilds.RecruitAsync(session, GuildRecruitReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.Detail, "guilds", async (session, msgId, body) =>
        {
            await guilds.DetailAsync(session, GuildDetailInfoReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.MemberIds, "guilds", async (session, msgId, body) =>
        {
            await guilds.MemberIdsAsync(session, GuildMemberIDListReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.SyncOnce, "guilds", async (session, msgId, body) =>
        {
            await guilds.SyncOnceAsync(session, SyncGuildDataOnceReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.EliteQuery, "guilds", async (session, msgId, body) =>
        {
            await guilds.EliteQueryAsync(session);
        });
        routes.Map(GuildService.EliteSave, "guilds", async (session, msgId, body) =>
        {
            await guilds.EliteSaveAsync(session, QuickJoinEliteGroupReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.MergePull, "guilds", async (session, msgId, body) =>
        {
            await guilds.MergePullAsync(session);
        });
        routes.Map(GuildService.MergeRecommend, "guilds", async (session, msgId, body) =>
        {
            await guilds.MergeRecommendAsync(session);
        });
        routes.Map(GuildService.MergeDetail, "guilds", async (session, msgId, body) =>
        {
            await guilds.MergeDetailAsync(session, MergeGuildDetailDataReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.MergeMembers, "guilds", async (session, msgId, body) =>
        {
            await guilds.MergeMembersAsync(session, SaveMergeMemberReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.MergeOpe, "guilds", async (session, msgId, body) =>
        {
            await guilds.MergeOpeAsync(session, MergeGuildOpeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.MergeAsk, "guilds", async (session, msgId, body) =>
        {
            await guilds.MergeAskAsync(session, InviteMergeGuildReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.Peach, "guilds", async (session, msgId, body) =>
        {
            await guilds.PeachAsync(session, GuildPeachReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.PeachApply, "guilds", async (session, msgId, body) =>
        {
            await guilds.PeachApplyAsync(session);
        });
        routes.Map(GuildService.BroadEmail, "guilds", async (session, msgId, body) =>
        {
            await guilds.BroadEmailAsync(session, GuildBroadEmailReq.Parser.ParseFrom(body.Span));
        });
        // Reads the committed roster, then the team family reserves the leader and the invited elites itself.
        routes.Map(GuildService.EliteTeam, "teams", async (session, msgId, body) =>
        {
            await guilds.EliteTeamAsync(session);
        });
        routes.Map(GuildService.BuildUpgrade, "guilds", async (session, msgId, body) =>
        {
            await guilds.BuildUpgradeAsync(session, GuildBuildLevelUpReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.Apply, "guilds", async (session, msgId, body) =>
        {
            await guilds.ApplyAsync(session, GuildEnterApplyReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.ApplyOpe, "guilds", async (session, msgId, body) =>
        {
            await guilds.ApplyOpeAsync(session, GuildEnterApplyOpeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.Invite, "guilds", async (session, msgId, body) =>
        {
            await guilds.InviteAsync(session, GuildInviteReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.InviteAck, "guilds", async (session, msgId, body) =>
        {
            await guilds.InviteAckAsync(session, GuildInviteAckReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.ChangeOfficial, "guilds", async (session, msgId, body) =>
        {
            await guilds.ChangeOfficialAsync(session, GuildChangeOfficialReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.Kick, "guilds", async (session, msgId, body) =>
        {
            await guilds.KickAsync(session, GuildMemberKickReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.Leave, "guilds", async (session, msgId, body) =>
        {
            await guilds.LeaveAsync(session);
        });
        routes.Map(GuildService.Edit, "guilds", async (session, msgId, body) =>
        {
            await guilds.EditAsync(session, GuildEditReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildCaravanService.Show, "caravan", async (session, msgId, body) =>
        {
            await caravan.ShowAsync(session);
        });
        routes.Map(GuildCaravanService.Submit, "caravan", async (session, msgId, body) =>
        {
            await caravan.SubmitAsync(session, GuildCatShopItemSubmitReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildCaravanService.WantHelp, "caravan", async (session, msgId, body) =>
        {
            await caravan.WantHelpAsync(session, GuildCatShopItemWantHelpReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildCaravanService.HelpOther, "caravan", async (session, msgId, body) =>
        {
            await caravan.HelpOtherAsync(session, GuildCatShopItemHelpOtherReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildCaravanService.HelpList, "caravan", async (session, msgId, body) =>
        {
            await caravan.HelpListAsync(session);
        });
        routes.Map(GuildCaravanService.Depart, "caravan", async (session, msgId, body) =>
        {
            await caravan.DepartAsync(session);
        });
        routes.Map(GuildCaravanService.Refresh, "caravan", async (session, msgId, body) =>
        {
            await caravan.RefreshAsync(session, GuildCatShopRefreshReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuildService.MarketBooth, "guilds", async (session, msgId, body) =>
        {
            await guilds.MarketAsync(session, msgId, body);
        });
        routes.Map(GuildService.MarketItems, "guilds", async (session, msgId, body) =>
        {
            await guilds.MarketAsync(session, msgId, body);
        });
        routes.Map(GuildService.MarketList, "guilds", async (session, msgId, body) =>
        {
            await guilds.MarketAsync(session, msgId, body);
        });
        routes.Map(GuildService.MarketUnlist, "guilds", async (session, msgId, body) =>
        {
            await guilds.MarketAsync(session, msgId, body);
        });
        routes.Map(GuildService.MarketBuy, "guilds", async (session, msgId, body) =>
        {
            await guilds.MarketAsync(session, msgId, body);
        });
        routes.Map(GuildService.MarketSellRecords, "guilds", async (session, msgId, body) =>
        {
            await guilds.MarketAsync(session, msgId, body);
        });
        routes.Map(GuildService.MarketBuyRecords, "guilds", async (session, msgId, body) =>
        {
            await guilds.MarketAsync(session, msgId, body);
        });
        routes.Map(GuildService.MarketUpdate, "guilds", async (session, msgId, body) =>
        {
            await guilds.MarketAsync(session, msgId, body);
        });
        routes.Map(TeamService.TeamCreate, "teams", async (session, msgId, body) =>
        {
            await teams.CreateAsync(session, TeamCreateReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TeamService.TeamLeave, "teams", async (session, msgId, body) =>
        {
            await teams.LeaveAsync(session);
        });
        routes.Map(TeamService.TeamInvite, "teams", async (session, msgId, body) =>
        {
            await teams.InviteAsync(session, InviteIntoTeamReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TeamService.TeamInviteeResp, "teams", async (session, msgId, body) =>
        {
            await teams.InviteeRespAsync(session, InviteeResp.Parser.ParseFrom(body.Span));
        });
        routes.Map(TeamService.TeamOperate, "teams", async (session, msgId, body) =>
        {
            await teams.OperateAsync(session, TeamOperateReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TeamService.TeamInviteList, "teams", async (session, msgId, body) =>
        {
            await teams.InviteListAsync(session, TeamInviteListReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TeamService.TeamChangeMemberState, "teams", async (session, msgId, body) =>
        {
            await teams.ChangeMemberStateAsync(session, ChangeTeamMemberStateReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GetGuildLeagueMgrData, "guilds", async (session, msgId, body) =>
        {
            await guilds.LeagueMgrDataAsync(session, GetGuildLeagueMgrData);
        });
        routes.Map(GuildLeagueService.Info, "guilds", async (session, msgId, body) =>
        {
            await guilds.LeagueInfoAsync(session, GuildLeagueService.Info);
        });
        routes.Map(GuildLeagueService.GroupInfo, "guilds", async (session, msgId, body) =>
        {
            await guilds.LeagueGroupInfoAsync(session, GuildLeagueService.GroupInfo);
        });
        routes.Map(SetFriendSystemMetadata, "friendExtra", async (session, msgId, body) =>
        {
            await friendExtra.SetAsync(session, SetFriendSystemMetadataReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EmblemService.SetUp, "emblems", async (session, msgId, body) =>
        {
            await emblems.SetUpAsync(session, SetUpEmblemReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EmblemService.SetDown, "emblems", async (session, msgId, body) =>
        {
            await emblems.SetDownAsync(session, SetDownEmblemReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EmblemService.Upgrade, "emblems", async (session, msgId, body) =>
        {
            await emblems.UpgradeAsync(session, UpgradeEmblemReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EmblemService.SwitchPlan, "emblems", async (session, msgId, body) =>
        {
            await emblems.SwitchPlanAsync(session, EmbleSwitchPlanReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EmblemService.RenamePlan, "emblems", async (session, msgId, body) =>
        {
            await emblems.RenamePlanAsync(session, EmblemPlanChangeNameReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(ChatService.ImUserSig, "chat", async (session, msgId, body) =>
        {
            await chat.UserSigAsync(session);
        });
        routes.Map(ChatService.JoinChannel, "chat", async (session, msgId, body) =>
        {
            await chat.JoinAsync(session, JoinChatChannelReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(ChatService.TunnelUp, "chat", async (session, msgId, body) =>
        {
            await chat.TunnelAsync(session, body);
        });
    }
}
