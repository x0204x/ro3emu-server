namespace Ro3Emu.Server.Game;

/// <summary>
/// Result codes the client knows (ErrorCodeConfig rows, text from Localization_en). Result 1 is
/// "Account already exists", not a generic failure. Which code the original server picks for a given refusal is mostly
/// inferred from the code's meaning; <see cref="Parameter"/> / <see cref="Unknown"/> stand in
/// where the map leaves the choice unresolved.
/// </summary>
public static class ErrorCodes
{
    // ErrorCodeConfig -> Localization_en, territory wars and the 84031 auction contract.
    public const int TerritoryGuild = 6001; // Guild doesn't exist.
    public const int DataNotFound = 23; // ErrorCodeConfig: Data not found in database.
    public const int TerritoryCampGuild = 6008;
    public const int TerritoryCampCooldown = 6009;
    public const int TerritoryCampFixed = 6011;
    public const int TerritoryCampCombat = 6012;
    public const int TerritoryWarClosed = 6031;
    public const int TerritoryInvalidPoint = 6035;
    public const int TerritoryAlreadyQualified = 6051;
    public const int TerritoryLegacyBidLow = 6052;
    public const int TerritoryNotQualified = 6071;
    public const int TerritoryAuctionClosed = 6121;
    public const int TerritoryBidLow = 6122;
    public const int TerritoryOtherRegion = 6123;
    public const int TerritoryBidChanged = 6124;
    public const int TerritoryInitialBidLow = 6125;
    public const int TerritoryOccupier = 6126;
    public const int TerritoryAuctionSettled = 6127;
    public const int TerritoryAuctionFunds = 6128;
    public const int TerritoryGuildMembers = 6129;
    public const int TerritoryMaxBidLow = 6130;
    public const int Unknown = 24;              // "Unknown error"
    public const int FeatureUnavailable = 28;   // unavailable feature
    public const int ActionInBattle = 47;      // NW_ProtobufDefine.ERR_PlayerActionNotAllowedInBattle
    public const int Parameter = 82;            // "Client Parameter error"

    // Activities: ErrorCodeConfig + Localization_en. Selection for each RPC is inferred from these texts.
    public const int ActivityClosed = 3902;      // "Event Not Available"
    public const int ActivityPeriod = 5801;      // "Not in the event period"
    public const int ActivityReward = 1511;      // "Invalid Activity Reward ID"
    // Starshatter ritual (FlyingDragonService): ErrorCodeConfig 1517-1526.
    public const int DragonBusy = 1517, DragonPlace = 1518, DragonAssisted = 1519, DragonItems = 1520, DragonSelf = 1521;
    public const int DragonNoLike = 1522, DragonLiked = 1523, DragonDone = 1524, DragonConfig = 1525, DragonCooldown = 1526;
    public const int BattlePassClosed = 8260, BattlePassLevel = 8261, BattlePassClaimed = 8262;
    public const int BattlePassLevelRequired = 8263, BattlePassRewardType = 8264, BattlePassPremium = 8265;
    public const int BattlePassNoRewards = 8266, BattlePassMaxLevel = 8267, BattlePassExpFull = 8268, BattlePassCurrency = 8269;
    public const int BattlePassGiftSelf = 8270, BattlePassGiftPending = 8271, BattlePassGiftItem = 8272;
    public const int BattlePassGiftMessage = 8273, BattlePassGiftCharacters = 8274, BattlePassGiftDay = 8275, BattlePassGiftFriend = 8276;
    public const int MonthCardNotOwned = 8300, MonthCardExpired = 8301, MonthCardClaimed = 8302;

    // Auction (ErrorCodeConfig 2100-2119; exact operation mapping is inferred).
    public const int AuctionGuild = 2100;
    public const int AuctionSold = 2101;
    public const int AuctionParameter = 2102;
    public const int AuctionPriceChanged = 2103;
    public const int AuctionCurrencyShort = 2104;
    public const int AuctionBonusPending = 2105;
    public const int AuctionNoBonus = 2106;
    public const int AuctionBonusClaimed = 2107;
    public const int AuctionAlreadyBought = 2109;
    public const int AuctionBatchFailed = 2110;
    public const int AuctionNotForSale = 2117;
    public const int AuctionMaxPrice = 2118;
    public const int AuctionAutoExists = 2119;
    // Field Bonus Time / monster favorites (ErrorCodeConfig 2651-2661).
    public const int AfkCollectionInvalid = 2651;
    public const int AfkToggleCooldown = 2653;
    public const int AfkNotActive = 2654;
    public const int AfkTimeEmpty = 2655;
    public const int AfkAlreadyActive = 2656;
    public const int AfkCollectionFull = 2657;
    public const int AfkCollectionExists = 2658;
    public const int AfkCollectionMissing = 2659;
    public const int AfkNoSummary = 2660;
    public const int AfkDungeon = 2661;

    // Mail (ErrorCodeConfig / Localization_en; exact request-to-code choice is inferred).
    public const int MailMissing = 351;         // mail does not exist
    public const int MailExpired = 352;         // mail deleted or expired
    public const int MailUnclaimed = 353;       // attachments must be claimed before deletion
    public const int MailNoAttachments = 358;   // no attachments to receive
    public const int MailAlreadyClaimed = 359;  // already received
    // Ordinary dungeons; the code texts are recovered, exact refusal mapping is (inferred).
    public const int DungeonDeclined = 1250;    // "${1} declined to Enter Dungeon"
    public const int DungeonPreparing = 1253;   // Cannot enter another dungeon during confirmation.
    public const int DungeonTeamSize = 3507;    // "Not enough party Members."
    public const int DungeonDataMissing = 3720; // "Dungeon data not found."
    public const int DungeonBusy = 7107;        // "Already in an instance"
    public const int NotPartyLeader = 274;      // "The Party Leader must open the party instance." (ERR_EnterDungeonNotLeader)
    // Rift Crisis / Hidden Vault: ErrorCodeConfig 6751-6759 (Localization_en 10630001101-10630001109).
    public const int RiftMemberIneligible = 6751; // "Player is not eligible to Enter Dungeon: Hidden Vault"
    public const int RiftStoneTaken = 6753;       // "The Hidden Vault Dungeon is under attack by another player..."
    public const int RiftPartyTooLarge = 6754;    // "Too many party Members"
    public const int RiftPartyTooSmall = 6755;    // "The party does not meet the required number of players."
    public const int RiftNoParty = 6757;          // "You must be in a party to enter this mode." (ERR_EnterOnepieceNoTeam)
    public const int RiftMemberLocked = 6758;     // "A party Member does not meet the Hidden Vault Unlock Requirement"
    public const int RiftMemberInVault = 6759;    // "A party Member is already in the Hidden Treasure Dungeon..."

    // Quests (texts carry the quest name as ${1})
    public const int TaskMissing = 100;         // "Quest [${1}] does not exist"
    public const int TaskExists = 101;          // "Quest [${1}] already exists"
    public const int TaskNoConfig = 102;        // "Configuration for quest [${1}] does not exist"
    public const int TaskStatus = 103;          // "Quest [${1}] status mismatch"
    public const int TaskPrerequisite = 104;    // "Prerequisite quest not completed"
    public const int TaskRequirements = 105;    // "Quest [${1}] requirements not met"
    public const int TaskNoGiveUp = 107;        // "Quest [${1}] cannot be abandoned or reset"

    // Items
    public const int ItemMissing = 150;         // "Item does not exist"
    public const int ItemNoConfig = 151;        // "Item configuration does not exist"
    public const int ItemsShort = 154;          // "Not enough required items"
    public const int BagFull = 159;             // "Backpack is full. Clear some space to receive the item."
    public const int DecomposeTabMissing = 183; // "Item dismantling tab configuration does not exist"
    public const int ItemLocked = 3357;         // "Contains locked items."
    public const int Cooldown = 161;            // "On cooldown. Please try again later."
    public const int UseWhileDead = 181;        // "Items cannot be used while the player is dead"
    public const int ItemUseForbidden = 180;    // "Item use is prohibited"
    public const int ItemUseAreaForbidden = 178; // "Items cannot be used in this area"
    public const int CurrencyShort = 192;       // "Insufficient Currency. Unable to purchase."
    public const int NothingToDismantle = 193;  // "No items available to dismantle"
    public const int StatPointsShort = 200;     // "Insufficient Stat Points"

    // Scenes
    public const int SceneMissing = 266;        // "The scene does not exist"
    public const int SwitchingScenes = 284;     // "Switching scenes"

    // Skills
    public const int SkillPointsShort = 307;    // "Not enough skill points"
    public const int SkillMaxLevel = 319;       // "Skill Level exceeds the maximum level"
    public const int LevelTooLow = 31016;       // UIWords 31016: the title rank's level requirement (inferred code)
    public const int SkillPrerequisite = 333;   // "The level of prerequisite skill is too low"
    public const int SkillNotInTree = 341;      // "This skill is not in the skill tree ..."

    // Season talent (ErrorCodeConfig / Localization_en)
    public const int TalentInvalid = 6258;      // "Invalid talent"
    public const int TalentLocked = 6265;       // "This talent has not been unlocked"
    public const int SeasonRankReward = 3900;   // "No Rank Reward available to Claim."
    public const int SeasonRefineMaterials = 6251; // "Season Refinement Material not configured"
    public const int SeasonRefineLevel = 6252;   // "Season Refine Level too low"
    public const int SeasonRefinePending = 6253; // "Season refinement slot data is not empty"
    public const int SeasonRefineChoice = 6254;  // "The attribute selected by the client does not exist on the server"
    public const int SeasonRefineConfig = 6255;  // "Season refinement is not configured"

    public const int TargetAlive = 551;         // "The target is currently alive"
    public const int ReviveInvitationExpired = 563; // "The Revive invitation you sent has expired."
    public const int SkillReviveNotAllowed = 566; // "Skill-based Revive is not allowed in this area."
    public const int ItemReviveNotAllowed = 567; // "Item-based Revive is not allowed in this area."
    public const int ReviveHelpCooldown = 571;  // "Revive requests are on cooldown."
    public const int ReviveNotAwaited = 572;    // "This player is not awaiting Revive"

    // Equipment
    public const int AppraiseParameter = 651;   // "Equipment Appraisal"
    public const int EquipNotAppraised = 653;   // "Equipment has not been appraised"
    public const int EquipSlot = 654;           // "Incorrect equipment slot"
    public const int NotEquipment = 659;        // "This item is not equipment"
    public const int EquipNotInBag = 660;       // "The equipment is not in your Backpack"
    public const int CraftParameter = 661;
    public const int CannotDismantleEquipped = 663; // "Cannot dismantle an equipped item"      // "Item crafting Parameter error"
    public const int CraftMaterialsShort = 662; // crafting materials short
    public const int StrengthenParameter = 668; // "Equipment Enhance Parameter error"
    public const int StrengthenMaterials = 669; // "Not enough Enhancement Materials"
    public const int RefineParameter = 670;     // "Equipment Refining Parameter error"
    public const int RefineMaterials = 671;     // "Not enough Refinement Material"
    public const int EnchantParameter = 672;    // "Equipment Enchantment Parameter error"
    public const int EnchantMaterials = 673;    // "Not enough Equipment Enchantment materials"

    // NPC interaction
    // Trade/stalls: ErrorCodeConfig; mapping a refusal to its named client code is (inferred).
    public const int TradePrice = 703;
    public const int TradeSpace = 704;
    public const int TradeSaleQuantity = 705;
    public const int TradeItemMissing = 706;
    public const int TradeDelisted = 710;
    public const int TradeRefreshCooldown = 711;
    public const int TradeCardDeveloped = 718;
    public const int TradeEmblemDeveloped = 719;
    public const int TradeCurrencyShort = 722;
    public const int TradeAppraised = 735;
    public const int TradePublicity = 738;
    public const int TradeOwnListing = 739;
    public const int TradeBuyFailed = 740;
    public const int TradeRequest = 716;
    public const int TradeStallFull = 717;
    public const int TradeBound = 734;
    public const int TradeQuantity = 6026;
    public const int TradeRepriceGone = 35030150;
    public const int StallExists = 2201;
    public const int StallArea = 2203;
    public const int StallTooClose = 2204;
    public const int StallCurrency = 2205;
    public const int StallMissing = 2207;
    public const int StallNotOpen = 2208;
    public const int StallFull = 2209;
    public const int StallItemMissing = 2210;
    public const int StallName = 2211;
    public const int StallClosed = 2212;
    public const int StallDistance = 2213;
    public const int StallPriceChanged = 2214;
    public const int StallQuantity = 2215;
    public const int StallShareCooldown = 2220;
    public const int TradeLowCredit = 35030149;
    public const int MailError = 355;
    public const int MailNoConfig = 362;

    // NPC interaction
    public const int NpcMissing = 801;          // "NPC not found"
    public const int NpcTooFar = 804;           // "Too far away for NPC Interaction"
    public const int NpcParameter = 806;        // invalid interaction parameters

    // Party
    public const int AlreadyInParty = 1201;     // "You are already in a Party."
    public const int PartyMissing = 1202;       // "No Party exists with this ID."
    public const int NoPermission = 1204;       // "Do not have Permission"

    // Guild (ErrorCodeConfig / Localization_en; operation choice inferred where the original response is uncaptured).
    public const int AlreadyInGuild = 463, GuildMissing = 464, GuildPermission = 466, GuildNameTaken = 470;
    public const int GuildPositionFull = 471, GuildApplicationMissing = 482, GuildInviteInvalid = 481;
    public const int GuildApplicationLimit = 468, GuildApplicationListFull = 469;
    // Guild League (ErrorCodeConfig 516-524).
    public const int LeagueClosed = 516, LeagueTemporary = 517, LeagueElite = 518, LeagueMainFull = 523;
    // Elite roster (ErrorCodeConfig 10046-10048, 10059): not a member, the roster changed meanwhile, position too low,
    // too few elites for the quick team.
    public const int EliteNotMember = 10046, EliteChanged = 10047, ElitePosition = 10048, EliteTeamShort = 10059;
    public const int GuildOfficerKick = 10068, GuildInviteRefused = 10069, GuildTextNumbers = 10066;
    public const int GuildMemberMissing = 454, NotInGuild = 462, GuildFull = 453;
    public const int AlreadyInGuildHome = 550;  // NW_ProtobufDefine.ERR_AlreadyInGuildHome "You are already at the guild base."
    public const int GuildHunting = 5840;       // "This action cannot be performed during the Hunting Challenge."
    public const int GuildNameInvalid = 10000, GuildNameNumbers = 10001, GuildLevelCreate = 10002;
    public const int GuildIconLocked = 10003, GuildFrameLocked = 10004, GuildJoinLevel = 10008;
    public const int GuildTextLength = 10027;
    public const int GuildMailDaily = 10025, GuildMailContent = 10026; // group mail: today's letters used, empty text
    // Impeaching the master: on cooldown, no grounds, requirements unmet, the master itself, cancel state, responded twice,
    // in progress, not in progress.
    public const int ImpeachCooldown = 472, ImpeachNoGrounds = 473, ImpeachRequirements = 474, ImpeachSelf = 475;
    public const int ImpeachCancelState = 476, ImpeachRepeat = 477, ImpeachInProgress = 478, ImpeachNotInProgress = 479;
    // Merging guilds (ErrorCodeConfig 10050-10078): expired, the other's received invitations full, ours sent full, too
    // many members, invited already, bad announcement, no invitation, impeachment here / there, no merge possible,
    // cooldown here / there.
    public const int MergeExpired = 10050, MergeTheirInvitesFull = 10051, MergeOurInvitesFull = 10052, MergeTooMany = 10053;
    public const int MergeRepeat = 10055, MergeAnnounce = 10056, MergeNoInvite = 10057, MergeOurImpeach = 10060;
    public const int MergeImpossible = 10061, MergeOurCooldown = 10063, MergeTheirImpeach = 10075, MergeTheirCooldown = 10078;
    public const int GuildFundsShort = 484, GuildContributeShort = 10040, GuildBuildBusy = 485, GuildHallLow = 486, GuildBuildMax = 487;
    // Guild voice channels (ErrorCodeConfig 97001-97032, Localization_en): not in a guild, invalid channel, on the
    // channel's block list, already / not in the channel, not leader nor GuildVoice right, the leader cannot be kicked,
    // not blocked; Order mode: no mic in Free mode, managers need not apply, already may speak,
    // already / not applied, not Order mode, reason too long; already in another channel, the leader's mic stays,
    // cannot kick yourself.
    public const int GuildVoiceNotInGuild = 97001, GuildVoiceBadChannel = 97002, GuildVoiceBlocked = 97003;
    public const int GuildVoiceAlreadyInChannel = 97004, GuildVoiceNotInChannel = 97005, GuildVoiceNoPermission = 97006;
    public const int GuildVoiceCannotKickLeader = 97007, GuildVoiceNotBlocked = 97008;
    public const int GuildVoiceFreeMode = 97016, GuildVoiceManagerNeedsNoApply = 97017, GuildVoiceAlreadyCanSpeak = 97018;
    public const int GuildVoiceAlreadyApplied = 97019, GuildVoiceNotApplied = 97020, GuildVoiceNotOrderMode = 97021;
    public const int GuildVoiceReasonTooLong = 97024, GuildVoiceInAnotherChannel = 97029, GuildVoiceLeaderCannotMute = 97030;
    public const int GuildVoiceCannotKickSelf = 97032;
    // Guild Caravan: ErrorCodeConfig 489-504; exact RPC-to-code selection is (inferred).
    public const int CaravanLoaded = 489, CaravanExpired = 490, CaravanOrderExpired = 491;
    public const int CaravanNotAsking = 492, CaravanCannotAsk = 493, CaravanRequestMissing = 494;
    public const int CaravanHelpUnavailable = 495, CaravanHelpLimit = 496, CaravanCargoExpired = 497;
    public const int CaravanLoadShort = 498, CaravanDeparted = 499, CaravanSelfHelp = 501;
    public const int CaravanRefreshLimit = 502, CaravanRefreshUnavailable = 503, CaravanRefreshExpired = 504;
    public const int NameLengthInvalid = 32;
    public const int TextSensitive = 33;
    public const int NotInParty = 1206;         // "You're not in a party."
    public const int PartyFull = 1209;          // "Party is full"
    public const int MemberLeft = 1213;         // "A Member has left the party."
    public const int PlayerOffline = 1224;      // "Player is offline"
    public const int InviteeInParty = 1225;     // "The invitee is already in a party"

    // Stage rewards / daily attempts (ErrorCodeConfig; mapping to these requests is inferred).
    public const int RewardMissing = 2061;     // "Reward not found"
    public const int RewardClaimed = 2062;     // "Reward claimed"
    public const int AttemptsExhausted = 2065; // "Insufficient attempts"

    // Card sockets / collection
    public const int CardMaxLevel = 1801;       // "Card level has reached maximum"
    public const int CardMaxStar = 1802;        // "Card star has reached maximum"
    public const int CardRankFirst = 1803;      // "Rank up before leveling"
    public const int CardLevelReturn = 1804;    // card level cannot be returned
    public const int CardStarReturn = 1822;     // card star cannot be returned
    public const int SlotIndex = 1806;          // "Slot index out of range"
    public const int SlotEmpty = 1810;          // "There is no card in this slot"
    public const int SlotSealed = 1811;         // "Card slot has not been unsealed"
    public const int AlreadyUnlocked = 1813;    // "Already unlocked"
    public const int CardSameName = 1816;       // "Only one card with the same name can be socketed"
    public const int CardSameElement = 1818;    // "Only one card that changes armor elemental properties can be socketed"
    // Codex and Growth Classroom (ErrorCodeConfig; the request mapping is inferred).
    public const int CodexUnavailable = 3802;   // "Unable to view the Codex."
    public const int UnlockRequirement = 3355;  // "Unlock Requirement not met." (a locked Codex theme, inferred)
    public const int NoRankReward = 3900;       // "No Rank Reward available to Claim."

    // Pets (ErrorCodeConfig 1848..1880, Localization_en 10630000526..555).
    public const int PetFastRoomLevel = 1848;   // "Hatchery level too low for Quick Hatch"
    public const int PetAlreadyOwned = 1851;    // "Pet unlocked"
    public const int PetCombineMaterials = 1852;
    public const int PetNotOwned = 1853;        // "Pet not yet unlocked"
    public const int PetLevelConditions = 1854;
    public const int PetOwnerLevel = 1855;
    public const int PetLevelMaterials = 1856;
    public const int PetMaxProgress = 1857;
    public const int PetProgressMaterials = 1858;
    public const int PetMaxStar = 1859;
    public const int PetNeedsProgress = 1860;
    public const int PetStarMaterials = 1861;
    public const int PetSetupSlot = 1862;
    public const int PetNoConfig = 1867;
    public const int PetAlreadyAssigned = 1873;
    public const int PetSourceSlot = 1874;
    public const int PetOperation = 1875;
    public const int PetSupportSlot = 1876;
    public const int PetPlan = 1878;

    // Shops
    public const int ShopNoConfig = 2301;       // "Configuration not found"
    public const int ShopClosed = 2302;         // "Shop is not open"
    public const int NotForSale = 2303;         // "Item is not currently available for purchase"
    public const int PurchaseLimit = 2304;      // "Item purchase limit reached"
    public const int PurchaseRequirements = 2311; // "Purchase requirements not met"
    public const int ShopAppearanceMissing = 2315; // "Appearance does not exist"
    public const int ShopAppearanceOwned = 2316;   // "Appearance already unlocked"
    public const int ShopGiftNotAllowed = 2317;    // "This shop does not allow gifts"
    public const int ShopMissing = 2323;        // "Shop Configuration not found."
    public const int ShopRewardMissing = 2324;  // "Reward Configuration not found."

    // Soul Echo (the client's name for emblems; exact operation mapping inferred).
    public const int FunctionLocked = 2057;       // "Function locked"
    // Mount, totem and Build results from ErrorCodeConfig / Localization_en (request mapping inferred).
    public const int MountNotOwned = 2033, MountStateInvalid = 2035, MountAlreadyOwned = 2036;
    public const int MountExpired = 2037, MountArea = 2038, MountJob = 2039, MountConfig = 2040, MountCombat = 2042;
    public const int TotemInvalid = 2401, TotemSlotEmpty = 2402, TotemMismatch = 2404, TotemAttribute = 2405;
    public const int TotemSkillExists = 2406, TotemCombine = 2409, TotemJob = 2410, TotemPlan = 2412;
    public const int GenreInvalid = 5004, GenreChildInvalid = 5009, GenreCooldown = 5012, GenreExpired = 5023;
    public const int EmblemInvalid = 3301;        // "Invalid Soul Echo"
    public const int EmblemSlot = 3302;           // "Invalid deployment slot"
    public const int EmblemDeploy = 3303;         // "Failed to deploy Soul Echo"
    public const int EmblemRemove = 3304;         // "Failed to remove Soul Echo."
    public const int EmblemSlotEmpty = 3305;      // "There is no Soul Echo in the deployment slot."
    public const int EmblemAlreadyDeployed = 3306; // "Soul Echo is already deployed."
    public const int EmblemCannotLevel = 3307;    // "Soul Echo cannot Level-up."
    public const int EmblemOtherSeason = 3315;    // "Only Soul Echoes from the current scenario can be deployed."

    // Battle (named by the client, NW_ProtobufDefine.lua)
    public const int SkillNotLearned = 3002;    // "Skill has not been learned."
    public const int SkillUnknown = 3001;
    public const int SkillBadState = 3004;
    public const int SkillBusy = 3005;
    public const int SkillCooldown = 3012;
    public const int SkillCostShort = 3013;
    public const int SkillBuffCostShort = 3015;
    public const int SkillRequirements = 3018;
    public const int SkillTargetInvalid = 3022;
    public const int SkillDistance = 3024;
    public const int SkillHeight = 3025;
    public const int SkillBlocked = 3026;
    public const int SkillPassive = 3032;
    public const int SkillEnergyShort = 3033;

    // ErrorCodeConfig 6601..6627, Localization_en 10630001066 onward.
    public const int LifeInactive = 6601, LifeLevel = 6602, LifeTitleLevel = 6603, LifeSkillMissing = 6605;
    public const int LifeConfig = 6606, LifeTitleRequirements = 6608, LifeTitleMissing = 6609;
    public const int LifeRecipe = 6610, LifeVigor = 6611, LifeWrongJob = 6613, LifeCollectionLocked = 6614;
    public const int LifeRequirements = 6618, LifeAlreadyLearned = 6619, LifeCollectionLearned = 6623;
    public const int LifeInvalidJob = 6624, LifeVigorOverflow = 6627;
}
