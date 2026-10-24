#pragma warning disable CS1998 // arms that only send or return keep the uniform async shape
using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>The role itself: stats, skills, equipment, cards, pets, mounts, wardrobe, life jobs, emotes and achievements.</summary>
public sealed class CharacterMessages(CharacterService character, SkillService skills, CombatService combat, SceneService scene, TaskService tasks, GuideService guides, EmojiService emoji, AchievementService achievements, CardService cards, EquipmentService equipment, AppraisalService appraisal, EnchantService enchants, SeasonPageService seasonPage, SeasonRefineService seasonRefine, SeasonTalentService talents, PetService pets, DungeonService dungeons, LifeJobService life, GatheringService gathering, TimedBattleService timedBattles, GuildCampfireService campfire, MountService mounts, CareerSpecialService careerSpecial, WardrobeService wardrobe, TotemService totems, GrowthClassService growthClass, ParagonService paragon, TitleService titles) : Messaging.IMessageModule
{
    public void Map(Messaging.MessageRoutes routes)
    {
        routes.AdmitEconomic(id => id is GatheringService.MiniGame);
        // The Codex monster unlock pays its Collection Points, a Growth Classroom rank its reward.
        routes.AdmitEconomic(id => id is CardService.LibraryMonsterUnlock or GrowthClassService.GetRankReward);
        // Messages that change only the sender's role.
        routes.AdmitRole(id => id is CharacterService.PlayerTransformJob or CharacterService.ChangeStatPlan
            or CharacterService.RenameStatPlan
            or >= 31301 and <= 31501); // character, equipment, card and stat plans
        routes.AdmitRole(id => id is CardService.LibraryCardUnlock or CardService.LibrarySwitchTheme);
        // An item whose use action changes a guild or Adventure Group record reserves it in its own item-use scope.
        routes.AdmitEconomic((session, id, body) => id == CharacterService.ItemUse && character.ItemUseWidens(session, body));
        // As Realm a cast waited behind every other player's work (31102 p99 1.4 s); NPC talk behind the market up to 3.4 s.
        routes.AdmitRole(id => id is CombatService.UseSkill or CombatService.SkillInterrupt
            or >= 31151 and <= 31160); // skill plans
        routes.AdmitRole(id => id is SceneService.NpcInteraction);
        routes.AdmitRole(id => id is >= 33501 and <= 33515); // life jobs
        routes.AdmitRole(id => id is >= 82901 and <= 82903); // season page
        routes.AdmitRole(id => id is >= 83220 and <= 83222 or >= 83550 and <= 83553); // refinement and talent
        routes.AdmitRole(id => id is >= 31183 and <= 31189); // wardrobe
        routes.AdmitRole(id => id is >= ParagonService.Allocate and <= ParagonService.ResetSingle); // Astral Level
        routes.AdmitRole(id => id is TitleService.Level);
        routes.AdmitRole(id => id is >= 33302 and <= 33325); // pets and incubation
        routes.AdmitRole(id => id is >= 32743 and <= 32750); // mounts
        routes.AdmitRole(id => id is CareerSpecialService.Lease or CareerSpecialService.CancelLease); // own battle slot only
        routes.AdmitRole(id => id is >= 82362 and <= 82370); // totems
        routes.AdmitRole(id => id is EmojiService.SetIdle or EmojiService.AddCollection or EmojiService.DelCollection);
        routes.AdmitRole(id => id is AchievementService.GetReward or AchievementService.GetPointReward
            or AchievementService.UpdateHintLevel or AchievementService.GetAllReward);
        routes.Intercept("life", id => id is >= LifeJobService.SwitchLife and <= GatheringService.MiniGame, async (session, msgId, _) =>
        {
            // Life job requests before the role's life state is loaded are refused, not routed.
            if (session.Role != null && session.Progress != null && session.LifeJob != null && session.Restrictions != null) return false;
            await life.RejectUnloadedAsync(session, msgId);
            return true;
        });

        routes.Map(31183, "wardrobe", async (session, msgId, body) =>
        {
            await wardrobe.UseAsync(session, WardRobeUseAppearanceReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31184, "wardrobe", async (session, msgId, body) =>
        {
            await wardrobe.UseAttrAsync(session, WardRobeUseAppearanceAttrReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31185, "wardrobe", async (session, msgId, body) =>
        {
            await wardrobe.SavePlanAsync(session, WardRobeSaveAppearancePlanReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31186, "wardrobe", async (session, msgId, body) =>
        {
            await wardrobe.ApplyPlanAsync(session, WardRobeApplyAppearancePlanReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31187, "wardrobe", async (session, msgId, body) =>
        {
            await wardrobe.SetLikeAsync(session, WardRobeSetLikeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31188, "wardrobe", async (session, msgId, body) =>
        {
            await wardrobe.RedeemAsync(session, WardRobeRedeemUnlockAppearanceReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MountService.SetConfig, "mounts", async (session, msgId, body) =>
        {
            await mounts.SetAsync(session, PlayerMountsSetCfgReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MountService.Ride, "mounts", async (session, msgId, body) =>
        {
            await mounts.RideAsync(session, PlayerRiderMountReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MountService.ScoreAward, "mounts", async (session, msgId, body) =>
        {
            await mounts.ClaimScoreAsync(session, PlayerGetMountScoreAwardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MountService.EtchantRefresh, "mounts", async (session, msgId, body) =>
        {
            await mounts.RefreshEtchantAsync(session, MountEtchantRefreshReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MountService.EtchantSave, "mounts", async (session, msgId, body) =>
        {
            await mounts.SaveEtchantAsync(session, MountEtchantSaveReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MountService.EtchantPlan, "mounts", async (session, msgId, body) =>
        {
            await mounts.SetEtchantPlanAsync(session, MountSetEtchantPlanReq.Parser.ParseFrom(body.Span));
        });
        // Special Class Items (CareerSpecialService): lease / cancel from the Profession panel, function 16.
        routes.Map(CareerSpecialService.Lease, "mounts", async (session, msgId, body) =>
        {
            await careerSpecial.LeaseAsync(session, LeaseCareerSpecialItemReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CareerSpecialService.CancelLease, "mounts", async (session, msgId, body) =>
        {
            await careerSpecial.CancelAsync(session, CancelLeaseCareerSpecialItemReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(82362, "totems", async (session, msgId, body) =>
        {
            var request = SetUpTotemReq.Parser.ParseFrom(body.Span);
            await totems.RunOwnedAsync(session, () => totems.SetupAsync(session, request));
        });
        routes.Map(82363, "totems", async (session, msgId, body) =>
        {
            var request = RefineTotemAttrReq.Parser.ParseFrom(body.Span);
            await totems.RunOwnedAsync(session, () => totems.RefineAsync(session, request));
        });
        routes.Map(82364, "totems", async (session, msgId, body) =>
        {
            var request = RefineTotemSkillReq.Parser.ParseFrom(body.Span);
            await totems.RunOwnedAsync(session, () => totems.LearnAsync(session, request));
        });
        routes.Map(82365, "totems", async (session, msgId, body) =>
        {
            var request = UpgradeTotemSystemReq.Parser.ParseFrom(body.Span);
            await totems.RunOwnedAsync(session, () => totems.UpgradeAsync(session, request));
        });
        routes.Map(82366, "totems", async (session, msgId, body) =>
        {
            var request = CombineTotemReq.Parser.ParseFrom(body.Span);
            await totems.RunOwnedAsync(session, () => totems.CombineAsync(session, request));
        });
        routes.Map(82367, "totems", async (session, msgId, body) =>
        {
            var request = TotemSwitchPlanReq.Parser.ParseFrom(body.Span);
            await totems.RunOwnedAsync(session, () => totems.SwitchAsync(session, request));
        });
        routes.Map(82368, "totems", async (session, msgId, body) =>
        {
            var request = TotemChangeTotemNameReq.Parser.ParseFrom(body.Span);
            await totems.RunOwnedAsync(session, () => totems.RenameAsync(session, request));
        });
        routes.Map(82369, "totems", async (session, msgId, body) =>
        {
            var request = TotemSwapReq.Parser.ParseFrom(body.Span);
            await totems.RunOwnedAsync(session, () => totems.SwapAsync(session, request));
        });
        routes.Map(82370, "totems", async (session, msgId, body) =>
        {
            var request = TotemAppraiseReq.Parser.ParseFrom(body.Span);
            await totems.RunOwnedAsync(session, () => totems.AppraiseAsync(session, request));
        });
        routes.Map(31314, "character", async (session, msgId, body) =>
        {
            await character.QuickUseSaveAsync(session, QuickUseSaveReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31320, "character", async (session, msgId, body) =>
        {
            await character.MedicinesAutoUseSetAsync(session, MedicinesAutoUseSetReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31324, "character", async (session, msgId, body) =>
        {
            await character.ArrowSwitchAsync(session, ItemArrowSwitchReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31335, "character", async (session, msgId, body) =>
        {
            await character.CareerSpecialTransferAsync(session, CareerSpecialTransferItemsReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31336, "character", async (session, msgId, body) =>
        {
            await character.ShortcutBarSaveAsync(session, ShortcutBarSaveReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31337, "character", async (session, msgId, body) =>
        {
            await character.ShortcutBarUseAsync(session, ShortcutBarUseReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31504, "character", async (session, msgId, body) =>
        {
            await character.ChangeStatPlanAsync(session, PlanAttrPointChangeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31505, "character", async (session, msgId, body) =>
        {
            await character.RenameStatPlanAsync(session, PlanAttrPointNameChangeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(LifeJobService.SwitchLife, "life", async (session, msgId, body) =>
        {
            await life.SwitchAsync(session, SwitchLifeJobReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(LifeJobService.ChooseTitle, "life", async (session, msgId, body) =>
        {
            await life.ChooseTitleAsync(session, ChooseTitlePointReq.Parser.ParseFrom(body.Span), false);
        });
        routes.Map(LifeJobService.ChooseTitleEx, "life", async (session, msgId, body) =>
        {
            await life.ChooseTitleAsync(session, ChooseTitlePointReq.Parser.ParseFrom(body.Span), true);
        });
        routes.Map(LifeJobService.RefreshTitle, "life", async (session, msgId, body) =>
        {
            var title = RefreshTitlePointReq.Parser.ParseFrom(body.Span);
            await life.RefreshTitleAsync(session, title.LifejobId, title.Level, false);
        });
        routes.Map(LifeJobService.RefreshTitleEx, "life", async (session, msgId, body) =>
        {
            var extraTitle = RefreshTitlePointExReq.Parser.ParseFrom(body.Span);
            await life.RefreshTitleAsync(session, extraTitle.LifejobId, extraTitle.Level, true);
        });
        routes.Map(LifeJobService.StartForge, "life", async (session, msgId, body) =>
        {
            await life.ForgeAsync(session, StartForgeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(LifeJobService.UpgradeTitle, "life", async (session, msgId, body) =>
        {
            await life.UpgradeTitleAsync(session, UpGradeJobTileReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(LifeJobService.StudyRecipe, "life", async (session, msgId, body) =>
        {
            await life.LearnRecipeAsync(session, StudyRecipeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(LifeJobService.StudyCollection, "life", async (session, msgId, body) =>
        {
            await life.LearnCollectionAsync(session, StudyCollectionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(LifeJobService.StudyDrop, "life", async (session, msgId, body) =>
        {
            await life.LearnDropAsync(session, StudyCollectionDropReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(LifeJobService.ForgeTime, "life", async (session, msgId, body) =>
        {
            await life.ForgeTimeAsync(session, GetForgeCostTimeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(LifeJobService.HealthAll, "life", async (session, msgId, body) =>
        {
            await life.HealthAsync(session);
        });
        routes.Map(LifeJobService.HealthOne, "life", async (session, msgId, body) =>
        {
            await life.HealthAsync(session, GetHealthCostReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GatheringService.MiniGame, "gathering", async (session, msgId, body) =>
        {
            await gathering.FinishMiniGameAsync(session, FinishLifeJobGameReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SeasonPageService.GetData, "seasonPage", async (session, msgId, body) =>
        {
            await seasonPage.GetDataAsync(session);
        });
        routes.Map(SeasonPageService.ReceiveRankReward, "seasonPage", async (session, msgId, body) =>
        {
            await seasonPage.ClaimAsync(session, SeasonPageReceiveRankRewardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SeasonPageService.PutOnHonorBadge, "seasonPage", async (session, msgId, body) =>
        {
            await seasonPage.WearAsync(session, SeasonPagePutOnHonorBadgeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SeasonRefineService.Recast, "seasonRefine", async (session, msgId, body) =>
        {
            await seasonRefine.RecastAsync(session, SeasonRefineRecastReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SeasonRefineService.SelectAttr, "seasonRefine", async (session, msgId, body) =>
        {
            await seasonRefine.SelectAttrAsync(session, SeasonRefineSelectAttrReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SeasonRefineService.GiveUp, "seasonRefine", async (session, msgId, body) =>
        {
            await seasonRefine.GiveUpAsync(session, SeasonRefineGiveUpReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CharacterService.EquipSwitchPlan, "character", async (session, msgId, body) =>
        {
            await character.SwitchEquipmentPlanAsync(session, EquipSwitchPlanReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CharacterService.EquipChangePlanName, "character", async (session, msgId, body) =>
        {
            await character.RenameEquipmentPlanAsync(session, EquipChangePlanNameReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EquipmentService.ItemLockDecompose, "equipment", async (session, msgId, body) =>
        {
            await equipment.LockDecomposeAsync(session, ItemLockDecomposeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EquipmentService.ItemAutoDecomposeSetting, "equipment", async (session, msgId, body) =>
        {
            await equipment.AutoDecomposeSettingAsync(session, ItemAutoDecomposeSettingReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AppraisalService.Appraise, "appraisal", async (session, msgId, body) =>
        {
            await appraisal.AppraiseAsync(session, EquipAppraiseReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EnchantService.Enchant, "enchants", async (session, msgId, body) =>
        {
            await enchants.EnchantAsync(session, EnchantReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EquipmentService.Strengthen, "equipment", async (session, msgId, body) =>
        {
            await equipment.StrengthenAsync(session, StrengthenEquipmentReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EquipmentService.Refine, "equipment", async (session, msgId, body) =>
        {
            await equipment.RefineAsync(session, RefineEquipmentReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EquipmentService.Craft, "equipment", async (session, msgId, body) =>
        {
            await equipment.CraftAsync(session, EquipCraftReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EquipmentService.ItemCombine, "equipment", async (session, msgId, body) =>
        {
            await equipment.CombineAsync(session, ItemCombineReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(EquipmentService.ItemDecompose, "equipment", async (session, msgId, body) =>
        {
            await equipment.DecomposeAsync(session, ItemDecomposeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SeasonTalentService.Upgrade, "talents", async (session, msgId, body) =>
        {
            await talents.UpgradeAsync(session, SeasonTalentUpgradeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SeasonTalentService.SetPlan, "talents", async (session, msgId, body) =>
        {
            await talents.SetPlanAsync(session, SeasonTalentSetCurPlanReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SeasonTalentService.RenamePlan, "talents", async (session, msgId, body) =>
        {
            await talents.RenameAsync(session, SeasonTalentSetPlanNameReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.Combine, "pets", async (session, msgId, body) =>
        {
            await pets.CombineAsync(session, PetCombineReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.LevelUp, "pets", async (session, msgId, body) =>
        {
            await pets.LevelAsync(session, PetLvUpReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.ProgressUp, "pets", async (session, msgId, body) =>
        {
            await pets.AdvanceAsync(session, PetProgressUpReq.Parser.ParseFrom(body.Span).PetId, star: false);
        });
        routes.Map(PetService.StarUp, "pets", async (session, msgId, body) =>
        {
            await pets.AdvanceAsync(session, PetStarUpReq.Parser.ParseFrom(body.Span).PetId, star: true);
        });
        routes.Map(PetService.StateUpdate, "pets", async (session, msgId, body) =>
        {
            await pets.LineupAsync(session, PetStateUpdateReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.SwitchPlan, "pets", async (session, msgId, body) =>
        {
            await pets.SwitchAsync(session, PetSwitchPlanReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.ChangePlanName, "pets", async (session, msgId, body) =>
        {
            await pets.RenameAsync(session, PetChangePlanNameReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.CollectActive, "pets", async (session, msgId, body) =>
        {
            await pets.ActivateAsync(session, PetCollectActiveReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.FragmentsBroken, "pets", async (session, msgId, body) =>
        {
            await pets.DecomposeAsync(session, PetFragmentsBrokenReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.GenerateEgg, "pets", async (session, msgId, body) =>
        {
            await pets.GenerateAsync(session, PetGenEggReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.AccelerateEgg, "pets", async (session, msgId, body) =>
        {
            await pets.AccelerateAsync(session, PetEggAccReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.EggReward, "pets", async (session, msgId, body) =>
        {
            await pets.CollectAsync(session, PetEggRewardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.SetSkipAnim, "pets", async (session, msgId, body) =>
        {
            await pets.PreferenceAsync(session, PetSetSkipAnimReq.Parser.ParseFrom(body.Span).Status);
        });
        routes.Map(PetService.OpenRoom, "pets", async (session, msgId, body) =>
        {
            await pets.PreferenceAsync(session, null);
        });
        routes.Map(PetService.RoomLevelUp, "pets", async (session, msgId, body) =>
        {
            await pets.RoomLevelAsync(session);
        });
        routes.Map(PetService.FastEgg, "pets", async (session, msgId, body) =>
        {
            await pets.FastAsync(session, FastIncubateReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(PetService.Break, "pets", async (session, msgId, body) =>
        {
            await pets.UnsupportedAsync(session, msgId);
        });
        routes.Map(33305, "pets", async (session, msgId, body) =>
        {
            await pets.UnsupportedAsync(session, msgId);
        });
        routes.Map(33307, "pets", async (session, msgId, body) =>
        {
            await pets.UnsupportedAsync(session, msgId);
        });
        routes.Map(PetService.Rebirth, "pets", async (session, msgId, body) =>
        {
            await pets.RebirthAsync(session, PetRebirthReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.SlotInsert, "cards", async (session, msgId, body) =>
        {
            await cards.InsertAsync(session, EquipmentCardInsertReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.SlotRemove, "cards", async (session, msgId, body) =>
        {
            await cards.RemoveAsync(session, EquipmentCardRemoveReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.SlotTransfer, "cards", async (session, msgId, body) =>
        {
            await cards.TransferAsync(session, EquipmentCardTransferReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.SwitchPlan, "cards", async (session, msgId, body) =>
        {
            await cards.SwitchPlanAsync(session, CardSwitchPlanReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.ChangePlanName, "cards", async (session, msgId, body) =>
        {
            await cards.ChangeNameAsync(session, StorageholeChangeNameReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.LevelUp, "cards", async (session, msgId, body) =>
        {
            await cards.LevelUpAsync(session, CardLevelUpReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.StarUp, "cards", async (session, msgId, body) =>
        {
            await cards.StarUpAsync(session, CardStarUpReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.Revert, "cards", async (session, msgId, body) =>
        {
            await cards.RevertAsync(session, CardRevertReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.Treasured, "cards", async (session, msgId, body) =>
        {
            await cards.TreasuredAsync(session, CardTreasuredReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.LibraryCardUnlock, "cards", async (session, msgId, body) =>
        {
            await cards.UnlockAsync(session, LibraryCardUnlockReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.LibrarySwitchTheme, "cards", async (session, msgId, body) =>
        {
            await cards.SwitchThemeAsync(session, LibrarySwitchThemeReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CardService.LibraryMonsterUnlock, "cards", async (session, msgId, body) =>
        {
            await cards.UnlockMonsterAsync(session, LibraryMonsterUnlockReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TitleService.Level, "titles", async (session, msgId, body) => await titles.LevelAsync(session));
        routes.Map(ParagonService.Allocate, "paragon", async (session, msgId, body) =>
            await paragon.AllocateAsync(session, ParagonLevelAllocatePointReq.Parser.ParseFrom(body.Span)));
        routes.Map(ParagonService.Install, "paragon", async (session, msgId, body) =>
            await paragon.InstallAsync(session, ParagonLevelRuneInstallReq.Parser.ParseFrom(body.Span)));
        routes.Map(ParagonService.Remove, "paragon", async (session, msgId, body) =>
            await paragon.RemoveAsync(session, ParagonLevelRuneRemoveReq.Parser.ParseFrom(body.Span)));
        routes.Map(ParagonService.ResetAll, "paragon", async (session, msgId, body) => await paragon.ResetAllAsync(session));
        routes.Map(ParagonService.ResetSingle, "paragon", async (session, msgId, body) =>
            await paragon.ResetSingleAsync(session, ParagonLevelResetSingleReq.Parser.ParseFrom(body.Span)));
        routes.Map(GrowthClassService.GetRankReward, "growthClass", async (session, msgId, body) =>
        {
            await growthClass.ClaimAsync(session, GrowthClassGetRankRewardReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CharacterService.AllocateAttrPoints, "character", async (session, msgId, body) =>
        {
            await character.AllocateAsync(session, AllocateAttrPointReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CharacterService.ResetAttrPoints, "character", async (session, msgId, body) =>
        {
            await character.ResetStatsAsync(session, ResetAttrPointReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31152, "skills", async (session, msgId, body) =>
        {
            await skills.SwitchPlanAsync(session, SwitchPlanReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31157, "skills", async (session, msgId, body) =>
        {
            await skills.RenamePlanAsync(session, SkillPlanChangeNameReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CharacterService.EquipItemOpr, "character", async (session, msgId, body) =>
        {
            await character.EquipAsync(session, EquipItemOprReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CharacterService.ItemUse, "character", async (session, msgId, body) =>
        {
            await character.UseItemAsync(session, ItemUseReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CharacterService.PackItems, "character", async (session, msgId, body) =>
        {
            await character.SortAsync(session, ArrangeItemReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CharacterService.PlayerTransformJob, "character", async (session, msgId, body) =>
        {
            await character.TransformJobAsync(session, PlayerTransformJobReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(CombatService.UseSkill, "gathering", async (session, msgId, body) =>
        {
            // Starting another action cancels collection even if it never lands a combat hit (inferred).
            await gathering.InterruptAsync(session);
            var cast = CastSkillReq.Parser.ParseFrom(body.Span);
            // Dragon Coffin Island's crystals and soul are taken by their interaction skills (TimedBattleService.DragonIsland).
            if (!await timedBattles.TryDragonPickupAsync(session, cast)) await combat.CastAsync(session, cast);
        });
        routes.Map(SkillService.PlanUpdate, "skills", async (session, msgId, body) =>
        {
            await skills.PlanUpdateAsync(session, SkillGrowthSkillPlanUpdateReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SkillService.SkillLoad, "skills", async (session, msgId, body) =>
        {
            await skills.LoadAsync(session, SkillLoadReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SkillService.SkillReset, "skills", async (session, msgId, body) =>
        {
            await skills.ResetAsync(session, SkillResetReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SkillService.QueueUpdate, "skills", async (session, msgId, body) =>
        {
            await skills.QueueUpdateAsync(session, SkillQueueUpdateReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SkillService.SwitchGenre, "skills", async (session, msgId, body) =>
        {
            await skills.SwitchGenreAsync(session, SkillSwitchGenreReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SceneService.NpcInteraction, "gathering", async (session, msgId, body) =>
        {
            var npcInteraction = NpcInteractionReq.Parser.ParseFrom(body.Span);
            if (!await dungeons.TryInteractAsync(session, npcInteraction) // scripted scene actors (own UID namespace)
                && !await gathering.TryInteractAsync(session, npcInteraction)
                && !await timedBattles.TryInteractAsync(session, npcInteraction)
                && !await campfire.TryInteractAsync(session, npcInteraction)) await scene.InteractAsync(session, npcInteraction);
        });
        // Emotes and sitting (EmojiService).
        routes.Map(EmojiService.StartPlay, "emoji", async (session, msgId, body) =>
            await emoji.StartAsync(session, EmojiStartPlayReq.Parser.ParseFrom(body.Span)));
        routes.Map(EmojiService.StopPlay, "emoji", async (session, msgId, body) => await emoji.StopAsync(session));
        routes.Map(EmojiService.SetIdle, "emoji", async (session, msgId, body) =>
            await emoji.SetIdleAsync(session, SetIdleEmojiReq.Parser.ParseFrom(body.Span)));
        routes.Map(EmojiService.AddCollection, "emoji", async (session, msgId, body) =>
            await emoji.CollectionAsync(session, AddEmojiCollectionReq.Parser.ParseFrom(body.Span).EmojiId, add: true));
        routes.Map(EmojiService.DelCollection, "emoji", async (session, msgId, body) =>
            await emoji.CollectionAsync(session, DelEmojiCollectionReq.Parser.ParseFrom(body.Span).EmojiId, add: false));
        routes.Map(EmojiService.Invite, "emoji", async (session, msgId, body) =>
            await emoji.InviteAsync(session, EmojiInviteReq.Parser.ParseFrom(body.Span)));
        routes.Map(EmojiService.Accept, "emoji", async (session, msgId, body) =>
            await emoji.AcceptAsync(session, EmojiInviteAcceptReq.Parser.ParseFrom(body.Span)));
        routes.Map(EmojiService.Refuse, "emoji", async (session, msgId, body) =>
            await emoji.RefuseAsync(session, EmojiInviteRefuseReq.Parser.ParseFrom(body.Span)));
        // Achievements / Trophy (AchievementService). 76004 share has no client sender: unrouted.
        routes.Map(AchievementService.GetReward, "achievements", async (session, msgId, body) =>
            await achievements.ClaimAsync(session, GetAchievementRewardReq.Parser.ParseFrom(body.Span)));
        routes.Map(AchievementService.GetPointReward, "achievements", async (session, msgId, body) =>
            await achievements.ClaimTierAsync(session, GetAchievementPointRewardReq.Parser.ParseFrom(body.Span)));
        routes.Map(AchievementService.UpdateHintLevel, "achievements", async (session, msgId, body) =>
            await achievements.HintSeenAsync(session)); // no reply: the client names no response type
        routes.Map(AchievementService.GetAllReward, "achievements", async (session, msgId, body) =>
            await achievements.ClaimAllAsync(session, GetAchievementAllRewardReq.Parser.ParseFrom(body.Span)));
        routes.Map(GuideService.UpdateGuide, "guides", async (session, msgId, body) =>
        {
            await guides.UpdateAsync(session, UpdateGuideReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GuideService.CompleteGuide, "guides", async (session, msgId, body) =>
        {
            var guideReq = ClientCompleteGuideReq.Parser.ParseFrom(body.Span);
            await guides.CompleteAsync(session, guideReq);
            await tasks.OnGuideCompleteAsync(session, guideReq.GuildId);
        });
    }
}
