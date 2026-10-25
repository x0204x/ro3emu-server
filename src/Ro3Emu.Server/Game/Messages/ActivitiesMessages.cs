#pragma warning disable CS1998 // arms that only send or return keep the uniform async shape
using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>Dungeons, the tower, daily and timed activities, festivals, training, lottery, AFK and build plans.</summary>
public sealed class ActivitiesMessages(PraiseService praise, CasualBattleService casual, DailyActivityService daily, TowerService tower, ValhallaService valhalla, FlyingDragonService dragon, MonsterHuntService monsterHunt, FirstHeartService firstHeart, DungeonService dungeons, DungeonMatchService dungeonMatches, WildAfkService afk, NewActivityService activities, TimedEventService timedEvents, TimedBattleService timedBattles, FestivalService festival, TrainingService training, LotteryService lottery, GenreService genre, GuildHuntingService hunting) : Messaging.IMessageModule
{
    private const uint SetAutoBattle = 31054;

    public void Map(Messaging.MessageRoutes routes)
    {
        routes.AdmitEconomic(id => id is >= 82800 and <= 82828); // genre build plans
        routes.AdmitRole(id => id is NewActivityService.PassPanelMsg); // marks the session and syncs its own 5027
        routes.Map(NewActivityService.PassPanelMsg, "pass", (session, _, _) => activities.PassPanelAsync(session));
        routes.AdmitEconomic(id => id is >= 84061 and <= 84067); // training
        routes.AdmitEconomic(id => id is LotteryService.DrawRequest);
        routes.AdmitEconomic((_, id, body) => activities.IsOfflineOwnerRequest(id, body) || activities.IsEventOwnerRequest(id, body));
        routes.AdmitEconomic((_, id, body) => festival.IsOwnerRequest(id, body));
        routes.AdmitEconomic(timedEvents.IsOwnerRequest);
        routes.AdmitEconomic(IsTimedBattleOwnerRequest);
        routes.AdmitEconomic(training.IsOwnerRequest);
        // A solo private floor (its return position, room and handoff); as Realm p99 1.2 s at 500 players.
        // Valhalla Trials: a layer starts a solo private dungeon like a tower floor; a Blessing spends coins.
        routes.AdmitRole(id => id is ValhallaService.Challenge);
        routes.AdmitEconomic(id => id is ValhallaService.Pray);
        // Monster Hunt stage claims pay their awards.
        routes.AdmitEconomic(id => id is MonsterHuntService.GainAreaAward or MonsterHuntService.GainMonsterAward);
        // Seven-Day Goal task and score claims pay their awards.
        routes.AdmitEconomic(id => id is FirstHeartService.GetTaskReward or FirstHeartService.GetScoreReward);
        routes.AdmitRole(id => id is TowerService.Info or TowerService.SetPets or TowerService.FirstPassRange
            or TowerService.ClaimFirstPass or TowerService.Sweep or TowerService.End or TowerService.Start);
        routes.AdmitRole(id => id is DungeonService.SetJobPosition or DungeonService.SetGuide or DungeonService.ClaimFirstPass
            or DungeonService.Statistics or DungeonService.StatisticsHistory or DungeonService.StatisticsCurrent
            or DungeonService.StatisticsModule or DungeonService.QueryResult
            or DungeonService.HallOfFame or DungeonService.MarkBlessings or PraiseService.Praise);
        // Final Trial honor claims pay their awards (DungeonService.RealmOfGods reserves the role and the claim).
        routes.AdmitEconomic(id => id is DungeonService.ClaimHonor);

        routes.Map(82800, "genre", async (session, msgId, body) =>
        {
            await genre.RunOwnedAsync(session, () => genre.SeekAsync(session), 0, codes: true);
        });
        routes.Map(82801, "genre", async (session, msgId, body) =>
        {
            var request = GenreResponserReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.RespondAsync(session, request), request.SeekUserId, codes: false);
        });
        routes.Map(82802, "genre", async (session, msgId, body) =>
        {
            var request = GenreResponserSendPlanReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.SendRecommendationAsync(session, request), request.SeekUserId, codes: false);
        });
        routes.Map(82803, "genre", async (session, msgId, body) =>
        {
            var request = GenreResponserSaveReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.SaveRecommendationAsync(session, request), request.SeekUserId, codes: false);
        });
        routes.Map(82804, "genre", async (session, msgId, body) =>
        {
            var request = GenreResponserDeleteReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.DeleteRecommendationAsync(session, request), request.SeekerUserId, codes: false);
        });
        routes.Map(82805, "genre", async (session, msgId, body) =>
        {
            var request = GenreSeekerDeleteReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.DeleteSeekerAsync(session, request), request.ResponseUserId, codes: false);
        });
        routes.Map(82806, "genre", async (session, msgId, body) =>
        {
            var request = SwitchGenrePlanReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.SwitchAsync(session, request), 0, codes: false);
        });
        routes.Map(82807, "genre", async (session, msgId, body) =>
        {
            await genre.RunOwnedAsync(session, () => genre.CodeAsync(session), 0, codes: true);
        });
        routes.Map(82808, "genre", async (session, msgId, body) =>
        {
            await genre.RunOwnedAsync(session, () => genre.MyRecommendationsAsync(session), 0, codes: false);
        });
        routes.Map(82809, "genre", async (session, msgId, body) =>
        {
            var request = GenreGetAllOtherRecommendReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.OthersAsync(session, request), request.ResponseUserID, codes: false);
        });
        routes.Map(82810, "genre", async (session, msgId, body) =>
        {
            var request = GenreGetMyRecConcretePlanReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.ConcreteAsync(session, request), request.SeekerUserId, codes: false);
        });
        routes.Map(82813, "genre", async (session, msgId, body) =>
        {
            var request = GenreSeekerApplyPlanReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.ApplyRecommendationAsync(session, request), request.ResponseUserId, codes: false);
        });
        routes.Map(82816, "genre", async (session, msgId, body) =>
        {
            var request = GenreChangePlanNameReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.RenameAsync(session, request), 0, codes: false);
        });
        routes.Map(82823, "genre", async (session, msgId, body) =>
        {
            var request = GenreGetPlanPropertyReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.PropertyAsync(session, request), 0, codes: false);
        });
        routes.Map(82824, "genre", async (session, msgId, body) =>
        {
            var request = GenreResponserNameSaveReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.RenameRecommendationAsync(session, request), request.SeekUserId, codes: false);
        });
        routes.Map(82825, "genre", async (session, msgId, body) =>
        {
            var request = GenrePlanPowerReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.PowerAsync(session, request), 0, codes: false);
        });
        routes.Map(82826, "genre", async (session, msgId, body) =>
        {
            await genre.RunOwnedAsync(session, () => genre.SeasonAsync(session), 0, codes: false);
        });
        routes.Map(82827, "genre", async (session, msgId, body) =>
        {
            var request = GenreAttrSettingSchoolReq.Parser.ParseFrom(body.Span);
            await genre.RunOwnedAsync(session, () => genre.SchoolAsync(session, request), 0, codes: false);
        });
        routes.Map(82828, "genre", async (session, msgId, body) =>
        {
            await genre.RunOwnedAsync(session, () => genre.QueryRecommendationsAsync(session), 0, codes: false);
        });
        routes.Map(82812, "genre", async (session, msgId, body) =>
            await genre.RespondCodeRequestAsync(session, GenreResponseCodeReq.Parser.ParseFrom(body.Span)));
        routes.Map(NewActivityService.GetList, "activities", async (session, msgId, body) =>
        {
            await activities.ListAsync(session);
        });
        routes.Map(NewActivityService.GetReward, "activities", async (session, msgId, body) =>
        {
            await activities.ClaimAsync(session, NewActivityGetRewardCommonReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(NewActivityService.Common, "activities", async (session, msgId, body) =>
        {
            await activities.CommonAsync(session, CommonActivityReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(NewActivityService.LevelAward, "activities", async (session, msgId, body) =>
        {
            await activities.LevelAwardAsync(session, GetThreeDayLevelAwardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(NewActivityService.FunctionPreviewAward, "activities", async (session, msgId, body) =>
        {
            await activities.FunctionPreviewAsync(session, GetThreeDayFunctionPreviewAwardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82960, "activities", async (session, msgId, body) =>
        {
            await activities.PreSubmitAsync(session, NewActivityPreSubmitItemReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82961, "activities", async (session, msgId, body) =>
        {
            await activities.PreGetDataAsync(session, NewActivityPreGetDataReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82962, "activities", async (session, msgId, body) =>
        {
            await activities.PreReceiveAsync(session, NewActivityPreReceiveRewardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82963, "activities", async (session, msgId, body) =>
        {
            await activities.FragmentAskAsync(session, NewActivityFragmentGatherAskReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82964, "activities", async (session, msgId, body) =>
        {
            await activities.FragmentSendAsync(session, NewActivityFragmentGatherSendReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82965, "activities", async (session, msgId, body) =>
        {
            await activities.FragmentCraftAsync(session, NewActivityFragmentGatherCraftReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82966, "activities", async (session, msgId, body) =>
        {
            await activities.FragmentProgressAsync(session, NewActivityFragmentGatherProgressReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82967, "activities", async (session, msgId, body) =>
        {
            await activities.FragmentAskFinishedAsync(session, NewActivityFragmentGatherAskFinishedReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(83450, "activities", async (session, msgId, body) =>
        {
            await activities.WorldFillAsync(session, WorldExplorationFillPhaseNodeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82681, "dungeonMatches", async (session, msgId, body) =>
        {
            var startMatch = StartMatchReq.Parser.ParseFrom(body.Span);
            if (dungeonMatches.WantsEntry(startMatch.MatchEntryId) || startMatch.MatchData?.MultiDungeon != null)
                await dungeonMatches.StartMatchAsync(session, startMatch);
            else await timedBattles.StartMatchAsync(session, startMatch);
        });
        routes.Map(82682, "dungeonMatches", async (session, msgId, body) =>
        {
            var cancelMatch = CancelMatchReq.Parser.ParseFrom(body.Span);
            if (dungeonMatches.Owns(session) || dungeonMatches.WantsEntry(cancelMatch.MatchEntryId) || cancelMatch.MatchData?.MultiDungeon != null)
                await dungeonMatches.CancelMatchAsync(session, cancelMatch);
            else if (await timedBattles.CancelMatchAsync(session, cancelMatch)
                && (dungeonMatches.WantsEntry(cancelMatch.NextMatchEntry) || cancelMatch.NextMatchData?.MultiDungeon != null))
                await dungeonMatches.StartMatchAsync(session, new StartMatchReq
                { MatchEntryId = cancelMatch.NextMatchEntry, MatchData = cancelMatch.NextMatchData?.Clone() });
        });
        routes.Map(82637, "dungeonMatches", async (session, msgId, body) =>
        {
            var matchReady = MatchReadyReq.Parser.ParseFrom(body.Span);
            if (dungeonMatches.Owns(session) || dungeonMatches.WantsEntry(matchReady.MatchEntryId))
                await dungeonMatches.ReadyAsync(session, matchReady);
            else await timedBattles.ReadyAsync(session, matchReady);
        });
        routes.Map(82634, "dungeonMatches", async (session, msgId, body) =>
        {
            await dungeonMatches.RemindAsync(session, MatchReadyReminderReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(84071, "timedBattles", async (session, msgId, body) =>
        {
            await timedBattles.ClaimDragonAsync(session, DragonIslandClaimRankRewardReq.Parser.ParseFrom(body.Span));
        });
        // Survivor Battle inside the match (TimedBattleService.Chicken*): careers, landing, items, revival, giving up.
        routes.Map(TimedBattleService.SelectCareer, "timedBattles", async (session, msgId, body) =>
            await timedBattles.SelectCareerAsync(session, SelectRoleCareerReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.SelectPosition, "timedBattles", async (session, msgId, body) =>
            await timedBattles.SelectPositionAsync(session, SelectRolePositionReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.InsertItem, "timedBattles", async (session, msgId, body) =>
            await timedBattles.InsertAsync(session, InsertChickenItemReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.UseItem, "timedBattles", async (session, msgId, body) =>
            await timedBattles.UseAsync(session, UseChickenItemReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.DiscardItem, "timedBattles", async (session, msgId, body) =>
            await timedBattles.DiscardAsync(session, DiscardChickenItemReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.PickupItem, "timedBattles", async (session, msgId, body) =>
            await timedBattles.PickupAsync(session, PickUpChickenItemReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.PickupAll, "timedBattles", async (session, msgId, body) =>
            await timedBattles.PickupAllAsync(session, PickUpAllChickenItemReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.ForceDead, "timedBattles", async (session, msgId, body) =>
            await timedBattles.ForceDeadAsync(session));
        routes.Map(TimedBattleService.StartRevive, "timedBattles", async (session, msgId, body) =>
            await timedBattles.StartReviveAsync(session));
        routes.Map(TimedBattleService.Relife, "timedBattles", async (session, msgId, body) =>
            await timedBattles.RelifeAsync(session, ChickenRelifeReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.RelifeOther, "timedBattles", async (session, msgId, body) =>
            await timedBattles.RelifeOtherAsync(session, ChickenRelifeOtherReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.ItemUpgrade, "timedBattles", async (session, msgId, body) =>
            await timedBattles.UpgradeAsync(session, ChickenItemUpgradeReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.ItemUpgradeAll, "timedBattles", async (session, msgId, body) =>
            await timedBattles.UpgradeAllAsync(session));
        routes.Map(TimedBattleService.AdventureSelect, "timedBattles", async (session, msgId, body) =>
            await timedBattles.SelectAdventureAsync(session, AdventureEventSelectReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.AdventureRefresh, "timedBattles", async (session, msgId, body) =>
            await timedBattles.RefreshAdventureAsync(session, AdventureRefreshReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.SelectFollow, "timedBattles", async (session, msgId, body) =>
            await timedBattles.SelectFollowAsync(session, SelectFollowReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.ChangeWatch, "timedBattles", async (session, msgId, body) =>
            await timedBattles.ChangeWatchAsync(session, ChickenChangeWatchPlayerReq.Parser.ParseFrom(body.Span)));
        routes.Map(TimedBattleService.OpenPanel, "timedBattles", async (session, msgId, body) =>
            await timedBattles.OpenPanelAsync(session, ChickenOpenPanelReq.Parser.ParseFrom(body.Span)));
        routes.Map(83351, "timedBattles", async (session, msgId, body) =>
        {
            await timedBattles.OnePieceInfoAsync(session, HiddenTreasureRoundInfoReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(83410, "lottery", async (session, msgId, body) =>
        {
            await lottery.DrawAsync(session, LotteryReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TrainingService.ChallengeMode, "training", async (session, msgId, body) =>
        {
            await training.ModeAsync(session, true);
        });
        routes.Map(TrainingService.AimAward, "training", async (session, msgId, body) =>
        {
            await training.ClaimAsync(session, TrainingAimAwardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TrainingService.ExitChallengeMode, "training", async (session, msgId, body) =>
        {
            await training.ModeAsync(session, false);
        });
        routes.Map(TrainingService.UpdateWoodNpc, "training", async (session, msgId, body) =>
        {
            await training.UpdateAsync(session, TrainingUpdateWoodNpcAttrReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TrainingService.Reconnect, "training", async (session, msgId, body) =>
        {
            await training.ReconnectAsync(session, false);
        });
        routes.Map(TrainingService.ChallengeReconnect, "training", async (session, msgId, body) =>
        {
            await training.ReconnectAsync(session, true);
        });
        routes.Map(TrainingService.ChallengeReset, "training", async (session, msgId, body) =>
        {
            await training.ResetAsync(session);
        });
        routes.Map(TimedEventService.Common, "timedEvents", async (session, msgId, body) =>
        {
            await timedEvents.CommonAsync(session, ActivityReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TimedEventService.Upload, "timedEvents", async (session, msgId, body) =>
        {
            await timedEvents.UploadAsync(session, ActivityDataUploadReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(42047, "dungeons", async (session, msgId, body) =>
        {
            var remind = TeamConfirmRemindersReq.Parser.ParseFrom(body.Span);
            if (!await timedBattles.TryRemindRiftAsync(session, remind)) await dungeons.RemindAsync(session, remind);
        });
        routes.Map(DailyActivityService.RingAccept, "festival", async (session, msgId, body) =>
        {
            var dailyRequest = CommonDailyActivityReq.Parser.ParseFrom(body.Span);
            if (dailyRequest.RainbowKoiBet != null) await festival.BetAsync(session, dailyRequest);
            // Branch 200 gveHuntingQueryResult (MG_Activity.proto:310) is the Hunting Challenge's, not the Ring's.
            else if (dailyRequest.GveHuntingQueryResult != null) await hunting.QueryResultAsync(session, dailyRequest);
            // Branch 120 casualBattleSignUp is Mock Battle's (CasualBattleService).
            else if (dailyRequest.CasualBattleSignUp != null) await casual.SignUpAsync(session, dailyRequest);
            else await daily.AcceptRingAsync(session, dailyRequest);
        });
        routes.Map(DailyActivityService.StageGain, "daily", async (session, msgId, body) =>
        {
            await daily.ClaimAsync(session, StageRewardGainReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DailyActivityService.StageBatchGain, "daily", async (session, msgId, body) =>
        {
            await daily.BatchClaimAsync(session, StageRewardBatchGainReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(FirstHeartService.GetTaskReward, "firstHeart", async (session, msgId, body) =>
        {
            await firstHeart.ClaimTaskAsync(session, FirstHeartGetTaskRewardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(FirstHeartService.GetScoreReward, "firstHeart", async (session, msgId, body) =>
        {
            await firstHeart.ClaimScoreAsync(session, FirstHeartGetScoreRewardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MonsterHuntService.GainMonsterAward, "monsterHunt", async (session, msgId, body) =>
        {
            await monsterHunt.ClaimMonsterAsync(session, AreaConcurGainMonsterAwardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MonsterHuntService.GainAreaAward, "monsterHunt", async (session, msgId, body) =>
        {
            await monsterHunt.ClaimAreaAsync(session, AreaConcurGainAreaAwardReq.Parser.ParseFrom(body.Span));
        });
        // The Starshatter ritual is one per server: its requests keep the default realm-wide admission.
        routes.Map(FlyingDragonService.Start, "flyingDragon", async (session, msgId, body) =>
            await dragon.StartAsync(session, FlyingDragonStartProduceReq.Parser.ParseFrom(body.Span)));
        routes.Map(FlyingDragonService.Assist, "flyingDragon", async (session, msgId, body) => await dragon.AssistAsync(session));
        routes.Map(FlyingDragonService.Query, "flyingDragon", async (session, msgId, body) => await dragon.QueryAsync(session));
        routes.Map(FlyingDragonService.Accelerate, "flyingDragon", async (session, msgId, body) => await dragon.AccelerateAsync(session));
        routes.Map(FlyingDragonService.Complete, "flyingDragon", async (session, msgId, body) => await dragon.CompleteAsync(session));
        routes.Map(FlyingDragonService.ThumbsUp, "flyingDragon", async (session, msgId, body) => await dragon.ThumbsUpAsync(session));
        routes.Map(ValhallaService.Challenge, "valhalla", async (session, msgId, body) =>
        {
            await valhalla.ChallengeAsync(session, HeroicSpiritChallengeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(ValhallaService.Pray, "valhalla", async (session, msgId, body) =>
        {
            await valhalla.PrayAsync(session); // NullArg: the client sends no body
        });
        routes.Map(DungeonService.Confirm, "dungeons", async (session, msgId, body) =>
        {
            var confirm = ConfirmTeamDungeonReq.Parser.ParseFrom(body.Span);
            if (!await timedBattles.TryConfirmRiftAsync(session, confirm)) await dungeons.ConfirmAsync(session, confirm);
        });
        routes.Map(DungeonService.ClientEvent, "training", async (session, msgId, body) =>
        {
            var dungeonEvent = ClientDungeonEvent.Parser.ParseFrom(body.Span);
            if (!await training.TryEventAsync(session, dungeonEvent)) await dungeons.ClientEventAsync(session, dungeonEvent);
        });
        routes.Map(DungeonService.Statistics, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.StatisticsAsync(session);
        });
        routes.Map(DungeonService.StatisticsHistory, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.StatisticsHistoryAsync(session, DungeonStatisticsInfoWithDungeonReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.StatisticsCurrent, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.StatisticsCurrentAsync(session);
        });
        routes.Map(DungeonService.StatisticsModule, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.StatisticsModuleAsync(session);
        });
        routes.Map(DungeonService.QueryResult, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.QueryResultAsync(session, QueryDungeonResultReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.SetJobPosition, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.SetPositionAsync(session, MultiDungeonSetJobPositionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.SetGuide, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.SetGuideAsync(session, MultiDungeonSetJobPositionGuideFlagReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.ClaimFirstPass, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.ClaimFirstPassAsync(session, ClaimFirstPassAwardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.HallOfFame, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.HallOfFameAsync(session, QueryHallOfFameInfoReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.MarkBlessings, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.MarkBlessingsAsync(session, MultiDungeonMarkSacredBlessingsReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.ClaimHonor, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.ClaimHonorAsync(session, MultiDungeonClaimHonorAwardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PraiseService.Praise, "praise", async (session, msgId, body) =>
        {
            await praise.PraiseAsync(session, MultiDungeonPraiseReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.ModuleButton, "dungeons", async (session, msgId, body) =>
        {
            await dungeons.ModuleButtonAsync(session, DungeonModuleButtonReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TowerService.Start, "tower", async (session, msgId, body) =>
        {
            await tower.StartAsync(session, ClimTowerStartReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TowerService.Info, "tower", async (session, msgId, body) =>
        {
            await tower.InfoAsync(session);
        });
        routes.Map(TowerService.SetPets, "tower", async (session, msgId, body) =>
        {
            await tower.SetPetsAsync(session, SetClimTowerPetsReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TowerService.FirstPassRange, "tower", async (session, msgId, body) =>
        {
            await tower.FirstPassRangeAsync(session, QueryClimbTowerFirstPassRangeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TowerService.ClaimFirstPass, "tower", async (session, msgId, body) =>
        {
            await tower.ClaimFirstPassAsync(session);
        });
        routes.Map(SetAutoBattle, "afk", async (session, msgId, body) =>
        {
            await afk.SetAutoBattleAsync(session, SetAutoBattleReq.Parser.ParseFrom(body.Span).IsAutoBattle);
        });
        routes.Map(WildAfkService.Info, "afk", async (session, msgId, body) =>
        {
            await afk.InfoAsync(session);
        });
        routes.Map(WildAfkService.Collection, "afk", async (session, msgId, body) =>
        {
            await afk.CollectAsync(session, SetMonsterCollectionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(WildAfkService.Start, "afk", async (session, msgId, body) =>
        {
            await afk.ToggleAsync(session, true);
        });
        routes.Map(WildAfkService.Pause, "afk", async (session, msgId, body) =>
        {
            await afk.ToggleAsync(session, false);
        });
        routes.Map(WildAfkService.Magnification, "afk", async (session, msgId, body) =>
        {
            await afk.ToggleAsync(session, SetHighMagnificationReq.Parser.ParseFrom(body.Span).Status, legacy: true);
        });
        routes.Map(WildAfkService.Snapshot, "afk", async (session, msgId, body) =>
        {
            await afk.SnapshotAsync(session);
        });
    }

    // Shared match packets retain legacy admission if either end belongs to an unconverted dungeon family.
    private bool IsTimedBattleOwnerRequest(GameSession session, uint message, ReadOnlyMemory<byte> body)
    {
        if (!timedBattles.IsOwnerRequest(message, body)) return false;
        if (message == TimedBattleService.StartMatch)
            return !dungeonMatches.WantsEntry(StartMatchReq.Parser.ParseFrom(body.Span).MatchEntryId);
        if (message == TimedBattleService.CancelMatch)
        {
            var request = CancelMatchReq.Parser.ParseFrom(body.Span);
            return !dungeonMatches.Owns(session) && !dungeonMatches.WantsEntry(request.MatchEntryId)
                && !dungeonMatches.WantsEntry(request.NextMatchEntry) && request.NextMatchData?.MultiDungeon == null;
        }
        if (message == TimedBattleService.Ready)
            return !dungeonMatches.Owns(session) && !dungeonMatches.WantsEntry(MatchReadyReq.Parser.ParseFrom(body.Span).MatchEntryId);
        return true;
    }
}
