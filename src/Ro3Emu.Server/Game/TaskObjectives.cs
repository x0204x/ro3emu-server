using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    // the client, ordered by usage where an authoritative owner exists in this server.
    private const int TMapKill = 103043, TUse = 103042, TEquipment = 302002, TGuide = 106003,
        TCardStars = 103044, TCardQuality = 103007, TCraft = 103024, TMvpOnce = 103022,
        TResonance = 103023, TTotalRefine = 103033, TSubmit = 103051, TRefineSlots = 302001,
        TTreasure = 314001, TSlotRefine = 103021, TCraftQuality = 103006, TCollect = 103050,
        TEquipmentQuality = 106002, TStrength = 103004, TRefineCount = 103005, TMvp = 103011,
        TSkillRank = 311001, TCargo = 103053, TPydDownload = 106701, TAfkSeconds = 106901, TRestrictUse = 106906,
        TOnePieceCount = 103049, TTimedBattle = 106509, TCaravan = 104011, TDungeonPass = 101018, TAreaAction = 106903;

    // Growth-manual child targets (TaskService.GrowthManual.cs). Displays from ProgressConfig/Localization: 326002
    // "Activate ${1} talents" (zh_CN 激活的天赋达到${1}, its unmet tip 天赋等级不足 "talent level too low": counted as
    // talent levels, inferred), 103034 CheckSetUpPetCount "Deploy ${1} pets" and 101014 CheckClimbTowerLayer "Clear Floor
    // ${1} of the Pet Tower" (Logic/ConditionSystem/LC_ConditionDefine.lua:125,215).
    private const int TTalentLevels = 326002, TPetsDeployed = 103034, TTowerFloor = 101014;

    // 309003 RentCareerSpecialSubType [309003, CartRemouldTable._iSubType] ("Rent a Cart Merchant / Falcon / Peco Peco",
    // TaskConfig 10200150-10200200): R is 1 (LC_InteractCondition.GetRentCareerSpecialSubTypeDes). Read as the lease held
    // now, like the other Check* state targets (inferred).
    private const int TRentCareerSpecial = 309003;

    // 334001 CheckHeroicSpiritChallengedId [334001, HeroicSpiritLayerConfig id] ("Clear Valhalla ${1}", the Tower Trial
    // side quests 302040/304050/305050/384050/385050): R is 1 (LC_InteractCondition.lua:4851-4875); the client condition
    // reads the layer as completed (:4830-4848), so it is the cleared state, like the other Check* targets.
    private const int TValhallaLayer = 334001;

    // 318004 [318004, AreaConcurConfig id, n] "Reach @{1} progress toward the <Area> Monster Hunt full collection reward"
    // (growth manual 703040/704050/705030/706030): R is p2 (LC_InteractCondition.lua:5246-5266). The area's collection
    // progress, its claimed monster stages (MonsterHuntService; inferred: the original counter is not recovered).
    private const int TMonsterHunt = 318004;

    // 210011 [210011, n]: TaskConfig 1602120 "Guild Activities - Hunting Challenge" (type 16, _kEXECAction 115 3015) is
    // its only user. The client has no formatter for it; R is the trailing count, as the sibling 104011 caravan departure
    // (inferred). Credited once per event when a member takes part (GuildHuntingService).
    private const int TGuildHunt = 210011;

    // 210010 [210010, n] "Guild Activities - Wedding Crashers" (guild quest 1602110, accepted by the hidden DailyActivity
    // 3014's _iTaskReceiveAction ["31","1","1602110"]): one per Wedding Crashers clear (DraculaWeddingService). Its
    // TaskTargetConfig row is not exported; counting a successful rescue, not an entry, is (inferred).
    private const int TDraculaWedding = 210010;

    // Growth Classroom modules Pet 1004, Divine Totem 1005, Soul Echo 1006 and Equip 1002 (also side quests, the growth
    // manual and type 13 echo quests). R per LC_ConditionDefine.m_kGetDesFunctionGroup (LC_InteractCondition.lua):
    // 103009 GetPetQualityCntDes, 103035/103036 GetSetUpQuality*CountDes, 103010 GetPetLvCntDes, 302003/315002/315003/
    // 315004/316001/316002 their *CountDes: R = p3; 306001 PetSetUpQualityStarCountDes: R = p4. 306002 and 306003 are
    // not registered, so GetDefaultDes: R = p2.
    // - 103009 CheckPetQualityCnt [quality, n] "Have ${1} Pets of the specified Rarity": owned pets of PetBaseConfig
    //   quality >= p2, as AchievementService (quest 100012 [103009,4,2]: "Own @{1} pets of Grade A or higher").
    // - 103010 CheckPetLvCnt [level, n]: owned pets at level >= p2, the client's own evaluator over PetInfo.m_kPetList
    //   (LC_InteractCondition.lua:1255-1291) and quest 100013's "Own @{1} pets at Lv. @{2} or higher", although the
    //   Growth Classroom text says "Deploy".
    // - 306001 PetSetUpQualityStarCount [quality, star, n] "Deploy ${1} pets of ${2} quality or higher": fight-deployed
    //   pets (current plan setUpPets) of quality >= p2 and PetData.star >= p3; the pet panel lights star - 1 stars
    //   (UI_Sys_Pet_DetailsWidgetCtrl.lua:589-600), so 1700317's p3 = 3 reads "2 stars" as its text says.
    // - 306002 [0] "Activate ${1} Faction effects with deployed pets": the current plan's fight pets activate a faction
    //   when three share PetBaseConfig._iCamp (Lua_CFG_PetBaseConfig.GetActivateCampByData, LC_PetHelper.GetActivateCamp).
    //   R = p2 = 0 would never complete; the Growth panel shows such a row as 0/1 (LC_BecomeStrongerHelper.lua:765-790)
    //   and its text passes "1", so R = 1 (inferred).
    // - 306003 [n] "Deploy ${1} Support Pets": occupied supportPets slots of the current plan.
    // - 103035 CheckSetUpQualityTotemCount [quality, n]: deployed totems (active plan) of ItemConfig quality >= p2.
    // - 315002 SetUpQualityTotemSkillCount [quality, n]: skills (itemInfos 240+) of the deployed totems whose
    //   TotemSkillConfig quality >= p2, every entry counted (inferred).
    // - 315003 SetUpQualityTotemFullPropertyCount [quality, n] "Deploy a Totem with ${1} maxed attributes": maxed attribute
    //   lines over the deployed totems of quality >= p2 (three lines a totem, three slots, n up to 6).
    // - 315004 TotemComposeCount [quality, n] "Combine ${1} Purple Totems" (合成过): one per committed combine whose made
    //   totem has quality >= p2, counted since acceptance (inferred, like the other event counters).
    // - 103036 CheckSetUpQualityEmblemCount [quality, n] / 316001 CheckSetUpLevelEmblemCount [level, n]: worn Soul Echoes
    //   of quality >= p2 / at level (itemInfos 1001) >= p2.
    // - 316002 CheckSoulEmblemResonanceCount [stage, n] "Activate ${1} Lv. ${2} Echo Resonances": mark ids whose
    //   resonance stage is >= p2 (EmblemService.Resonances).
    // - 302003 EquipEnchantQualityCount [quality, n]: enchantments on worn gear of quality >= p2. The texts disagree
    //   between enchanted pieces (condition, 件) and enchants (the quests, 个附魔); enchants are counted (inferred).
    private const int TPetQuality = 103009, TPetLevel = 103010, TPetQualityStar = 306001, TPetCamp = 306002,
        TPetSupport = 306003, TTotemQuality = 103035, TTotemSkills = 315002, TTotemMaxed = 315003, TTotemCombine = 315004,
        TEchoQuality = 103036, TEchoLevel = 316001, TEchoResonance = 316002, TEnchants = 302003;

    // Growth emblem titles 3-6 (NewbieTitlesConfig 703990-706990) and the targets of the same owners elsewhere. R per
    // LC_ConditionDefine.m_kGetDesFunctionGroup (LC_InteractCondition.lua); unregistered ids use GetDefaultDes, R = p2.
    // - 302005 EquipSpecialSkillCount [p2, p3, p4, n] (705060 [302005,1,0,0,3]): R = p5 (GetEquipSpecialSkillCountDes,
    //   :5203-5221). ProgressConfig "Equip ${1} pieces of gear with Stunts" (zh 穿戴装备特技数量达到${1}件): worn gear pieces
    //   carrying at least p2 Stunts (EquipmentAffixes.Specials). p3/p4 have no recovered meaning; only 0 is supported (inferred).
    // - 306004 [lv] "Reach Pet Hatching Room Lv. ${1}" (zh 炼金房等级达到${1}级): DBPetInfo.incubationLv.
    // - 106602 CheckTenBOSSWin [n, boss...] "Challenge and win a five-player dungeon ${1} times": R = p2 (:1359-1379); the
    //   trailing ids are TenGameplayBossConfig._iBoss (the TenGameplay boss of a Phantom Realm / Realm of the Gods run). One
    //   per committed successful clear of a listed boss (DungeonService.MultiBossCleared). Guild co-op (type 16) and
    //   Adventure Party (type 23) quests say "with at least @{1} other members": they count only when another member of
    //   the role's guild / adventure group cleared the same run (inferred).
    // - 106802 [n] "Participate in Treasure Hunting ${1} times" (zh 参与${1}次挖宝): one per committed dig at the Treasure
    //   Map's point (NewActivityService.CompleteTreasureHuntAsync); using the map alone is not a dig (inferred).
    // - 210012 [n] "Participate in Battle of Dragon Coffin Island or Survivor Battle" and 210009 [n] "Participate in Battle
    //   of Dragon Coffin Island": one per settled match of MatchConfig 2 / 6, like 106509 (TimedBattleService settlement).
    // - 106603 CheckGuildLeagueJoin [stage, n]: R = p3 (:1315-1335). One per committed Guild League match the role fought
    //   in; stage 1/2/3 are the preliminaries/semifinals/finals (500040-500042, GuildLeagueCalendar rounds), 0 any (706050).
    // - 103014 CheckGVEJoinCnt [n] "Participate in the Guild Event: Ilmatar, God of Nature" (706070, 1602090, 1603000): the
    //   guild GVE this client runs is Ilmata (GuildChallengeService; the client's GuildGVE/GVEWhaleRally, its result panel
    //   is UI_Sys_GVE_VictoryNewPanelCtrl). The classic GVE (GVEconfig, sc_GVE_MonsterDragon) has no server here. One per
    //   settled Ilmata stage attempt the member was present for, won or lost (participation, inferred).
    private const int TStunts = 302005, TPetRoom = 306004, TTenBossWin = 106602, TTreasureDig = 106802,
        TCoffinOrSurvivor = 210012, TDragonCoffin = 210009, TLeagueJoin = 106603, TGveJoin = 103014;

    // Everyday state and activity targets (default formatter, R = p2, unless said otherwise).
    // - 104007 CheckJoinGuild [1] (side quest 10200080): in a guild now.
    // - 103019 CheckFriend [n] "Have ${1} friends" (side quest 10200090): the friend list's size (FriendService relation,
    //   read through SocialOwnerCache after login and every relation change).
    // - 317001 CheckEmblemSetUpLv [n] "Raise all equipped Echoes to Lv. @{1}" (zh 全身残响等级达到${1}): the summed level of the
    //   worn Soul Echoes; five slots of at most Lv. 20 (EmblemLvConfig) make the quests' 100 the full set.
    // - 317002 CheckEmblemUpgrade [n] "Enhance Echoes by a total of @{1} levels": levels gained by committed 82424 upgrades.
    // - 101007 CheckFinishPetFloor [n] "Participate in the Tower Climb ${1} times" / 101010 CheckWinPetFloor [n]: one per
    //   settled Spirit Tower floor attempt / per won one (TowerService.Settled); an abandoned attempt is neither.
    // - 104008 CheckPetFloor [n] "Clear Floor ${1} of the Tower Challenge": the highest cleared floor, as 101014.
    // - 106902 CheckPersonalHomepageLike [n]: one per committed 82554 like.
    // - 101009 CheckActivation [n] "Reach ${1} Activity": today's activity points (currency 5).
    // - 106018 [n] "Participate in the Guild Bonfire at least ${1} times": one per event day the member is in the guild
    //   home while the Bonfire Party runs (GuildCampfireService.Joined, inferred).
    // - 106019 [stage, points, n] "Earn more than ${1} points in the Guild League ${2} times" (1602010-1602030,
    //   1603040-1603042): stage 0/1/2 = round 1/2/3; one per match whose personal score is above p3. The default formatter
    //   would make R = p2, the stage (0 for the preliminaries); R = p4, the ${2} of the text (inferred).
    // - 106611 [points, n] "Reach ${1} personal points in Siege War ${2} times" (1602040): one per settled siege whose
    //   personal score reaches p2; R = p3, the ${2} of the text (inferred, the default formatter would show p2).
    private const int TJoinGuild = 104007, TFriends = 103019, TEchoLevelSum = 317001, TEchoUpgrade = 317002,
        TTowerJoin = 101007, TTowerWin = 101010, TTowerFloorReached = 104008, TLike = 106902, TLiveness = 101009,
        TCampfire = 106018, TLeagueScore = 106019, TSiegeScore = 106611;

    // Season activity tasks (TaskConfig type 13, TaskService.ActivityTasks.cs), R = p2:
    // - 106605 CheckGVGJoin [n] "Participate in ${1} War of Emperium matches" / 106606 CheckGVGWin [n] "Win ${1} Siege
    //   Battles" (LC_InteractCondition.lua:1403-1445): one per settled siege the role was loaded into / its guild won.
    // - 106801 [n] "Log in ${1} times" (110007, Lost Treasure): one per login while the quest runs; the login that
    //   accepts it counts as the first (inferred).
    private const int TGvgJoin = 106605, TGvgWin = 106606, TLogin = 106801;

    private readonly ConditionalWeakTable<MonsterUnit, HashSet<long>> _participants = new();
    private readonly HashSet<GameSession> _refreshQueued = new();

    private void QueueRefresh(GameSession session)
    {
        if (session.Tasks == null || !_refreshQueued.Add(session)) return;
        _loop.PostOwned(session, async () => { _refreshQueued.Remove(session); await RefreshAsync(session); },
            discarded: () => _refreshQueued.Remove(session));
    }

    /// <summary>
    /// Exact formatter R, independent of the domain predicate.
    /// Unsupported/malformed targets return -1. In particular, a trailing filter 0 is never a completion threshold.
    /// </summary>
    private long Required(GameSession? session, long[] t) => t switch
    {
        [TDialog, _, _, _] or [TArea, _, _] or [TJobChange, _] or [TTaskDone, _] or [TJobRank, _] or [TAreaAction, _, _, _]
            or [TGuide, _] or [TPydDownload, 1] or [TRentCareerSpecial, _] or [TValhallaLayer, _] or [TPetCamp, 0]
            or [TJoinGuild, 1] => 1,
        [TPlot or TBaseLevel or TSkillPoints or TStatPoints or TResonance or TTotalRefine or TRefineSlots
            or TRefineCount or TMvp or TMvpOnce or TCargo or TAfkSeconds or TTimedBattle or TCaravan
            or TTalentLevels or TPetsDeployed or TTowerFloor or TGuildHunt or TDraculaWedding or TPetSupport
            or TPetRoom or TTreasureDig or TCoffinOrSurvivor or TDragonCoffin or TGveJoin or TFriends or TEchoLevelSum
            or TEchoUpgrade or TTowerJoin or TTowerWin or TTowerFloorReached or TLike or TLiveness or TCampfire
            or TGvgJoin or TGvgWin or TLogin, var n] => n,
        [TTenBossWin, var n, _, ..] => n,
        [TLeagueJoin, _, var n] => n,
        [TLeagueScore, _, _, var n] => n,
        [TSiegeScore, _, var n] => n,
        [TStunts, _, _, _, var n] => n,
        [TPetQualityStar, _, _, var n] => n,
        [TPetQuality or TPetLevel or TTotemQuality or TTotemSkills or TTotemMaxed or TTotemCombine or TEchoQuality
            or TEchoLevel or TEchoResonance or TEnchants, _, var n] => n,
        [TOnePieceCount, _, _, var limit] => _data.Row("RestrictConfig", limit) is { } restrict
            ? GameData.Int(restrict, "_iMaxRestrictNum") : -1,
        [TKillOnMap, var n, _] => n,
        [TMonsterHunt, _, var n] => n,
        [TDungeonPass, _, var n] => n,
        [TSaplingChosen or TDeclaration, var n] => n, // Adventure Group (TaskService.AdventureGroup.cs)
        [TWaterSapling, var n, _] => n,
        [TKill or TKillInvolved, _, _, ..] => t[^1],
        [TClientInteract or TMapKill or TUse or TCardStars or TCardQuality or TTreasure or TSlotRefine
            or TCraftQuality or TCollect or TEquipmentQuality or TStrength or TSkillRank or TRestrictUse, _, var n] => n,
        [TEquipment, _, _, var n] => n,
        [TCraft, _, _, var n, _] => n,
        [TSubmit, _, var n, _] => n,
        _ => -1,
    };

    private bool Supported(long[] t)
    {
        if (Required(null, t) <= 0) return false;
        return t[0] switch
        {
            // Mode 1 is the connected chapter's inventory turn-in (inferred). Mode 0's resource rules are
            // unresolved; keep it pending, including the formerly false-completing two-slot task 201120.
            TSubmit => t[3] == 1 && ItemExists(t[1]),
            TOnePieceCount => _data.Row("DailyActivityConfig", t[1]) != null && ItemExists(t[2])
                && _data.Row("RestrictConfig", t[3]) != null,
            TUse or TCollect => ItemExists(t[1]),
            TRestrictUse => t[1] == LifeJobService.VigorId,
            TDialog => t[1] > 0 && _data.Row("MapConfig", t[2]) != null && _data.Row("DialogueConfig", t[3]) != null,
            TArea => _data.Row("MapConfig", t[1]) != null && t[2] > 0,
            TMapKill => _data.Row("MapConfig", t[1]) != null,
            TKillOnMap => _data.Row("MapConfig", t[2]) != null,
            TDungeonPass => _data.Row("DungeonConfig", t[1]) != null,
            TAreaAction => _data.Row("MapConfig", t[1]) != null && t[2] > 0 && _data.Row("PerformanceConfig", t[3]) != null,
            TKill or TKillInvolved => t[1..^1].All(npc => _data.Row("NPCConfig", npc) != null),
            TJobRank => t[1] > 0,
            TJobChange => _data.Row("JobConfig", t[1]) != null,
            TTaskDone => Defs().ContainsKey(t[1]),
            TGuide => GuideExists(t[1]),
            TStrength => t[1] is >= 1 and <= 7,
            TSlotRefine => t[1] is >= 0 and <= 7,
            TEquipment => t[1] >= 0 && t[2] >= 0,
            TCraft => t[1] >= 0 && t[2] > 0 && t[4] is >= 0 and <= 10,
            TValhallaLayer => _data.Row("HeroicSpiritLayerConfig", t[1]) != null,
            TMonsterHunt => _data.Row("AreaConcurConfig", t[1]) != null,
            TStunts => t[1] >= 1 && t[2] == 0 && t[3] == 0,
            TTenBossWin => t[2..].All(boss => _data.Row("TenGameplayBossConfig", boss) != null),
            TLeagueJoin => t[1] is >= 0 and <= 3,
            TLeagueScore => t[1] is >= 0 and <= 2 && t[2] >= 0,
            TSiegeScore => t[1] >= 0,
            _ => true, // Only the explicit formatter cases above can reach here.
        };
    }

    /// <summary>Every target of the quest has a supported reader and a positive R (a quest without one never ends).</summary>
    internal bool TargetsSupported(long taskId) => Defs().TryGetValue(taskId, out var def) && def.Targets.Length > 0
        && def.Targets.All(t => Supported(t) && Required(null, t) > 0);

    private bool ItemExists(long id) => id > 0 && id <= int.MaxValue && _data.Row("ItemConfig", id) != null;

    private bool GuideExists(long group) => group > 0 && _data.Row("GuideConfig", group) != null;

    /// <summary>A state target read for another progress owner (FirstHeartService): its current value and R, or null
    /// when this server has no snapshot owner for it.</summary>
    internal (long Value, long Required)? SnapshotTarget(GameSession s, long[] t)
    {
        if (s.Progress == null || !Supported(t) || PassiveValue(s, t) is not (var value and >= 0)) return null;
        return (value, Required(s, t));
    }

    /// <summary>
    /// Snapshot objectives read committed owner state, at acceptance/login/change and again before delivery.
    /// Minimum quality/star comparisons, currently-owned collection, positive refinement slots, and totals across
    /// grids 1..7 are inferred server predicates; the client proves their R and parameter positions, not these rules.
    /// </summary>
    private long PassiveValue(GameSession s, long[] t)
    {
        var p = s.Progress!;
        return t[0] switch
        {
            TBaseLevel => p.Level,
            // 500130 [106901,3600] displays whole consumed minutes. Reading the committed
            // daily counter also repairs a disconnect between 5035 persistence and the quest sync (inferred).
            TAfkSeconds => s.WildAfk is { } afk && s.Daily?.DailyEpoch == afk.DailyEpoch ? afk.ConsumedTodaySeconds : 0,
            TJobRank => _data.Row("JobConfig", p.Career) is { } job && GameData.Int(job, "_iJobRank") >= t[1] ? 1 : 0,
            TSkillPoints => _skills.SpentPoints(p),
            TSkillRank => _skills.SpentPoints(p, (int)t[1]),
            TStatPoints => _character.SpentStatPoints(p),
            TTaskDone => IsDelivered(s, t[1]) ? 1 : 0,
            TJobChange => IsDescendant(p.Career, t[1]) ? 1 : 0,
            TGuide => s.TaskRuntime.CompletedGuides.Contains(t[1]) ? 1 : 0,
            TCollect or TSubmit => _character.CountOf(s, (int)t[1]),
            // TaskConfig[500070] [106906,1004,100]: committed consumption, not current balance or item 5.
            TRestrictUse => LifeJobService.DailyEnergyUsed(s, t[1]),
            TEquipment => s.Items.LongCount(i => i.Grid > 0 && ItemQuality(i.Entry) >= t[1]
                && _data.Row("ItemConfig", i.Entry) is { } row && GameData.Int(row, "_iType") == 5 && GameData.Int(row, "_iLevelNeed") >= t[2]),
            TEquipmentQuality => s.Items.LongCount(i => i.Grid > 0 && ItemQuality(i.Entry) >= t[1]
                && _data.Row("ItemConfig", i.Entry) is { } row && GameData.Int(row, "_iType") == 5),
            TCardQuality => s.Items.LongCount(i => i.Grid < 0 && _character.IsCard(i.Entry) && ItemQuality(i.Entry) >= t[1]),
            TCardStars => s.Items.LongCount(i => i.Grid < 0 && _character.IsCard(i.Entry) && (i.Attrs?.GetValueOrDefault(502) ?? 0) >= t[1]),
            TTreasure => ReferenceEquals(s.CardLibraryReadyAttachment, s.Attachment)
                ? s.Library?.Cards.Values.LongCount(c => c.TreasureUnlock && ItemQuality((int)c.CardId) >= t[1]) ?? 0 : 0,
            TResonance => _character.Resonance(s, "RefiningEffectTable"),
            TTotalRefine => Enumerable.Range(1, 7).Sum(g => (long)s.EquipLevels.Refine.GetValueOrDefault(g)),
            TRefineSlots => Enumerable.Range(1, 7).LongCount(g => s.EquipLevels.Refine.GetValueOrDefault(g) > 0),
            // Slot 0 is "all equipment slots" (growth titles 4/5, 704060 [103021,0,2]): the lowest of grids 1..7 (inferred).
            TSlotRefine => t[1] == 0 ? Enumerable.Range(1, 7).Min(g => (long)s.EquipLevels.Refine.GetValueOrDefault(g))
                : s.EquipLevels.Refine.GetValueOrDefault((int)t[1]),
            // The active plan's purchased talent levels (the Battle Oath is its own track).
            TTalentLevels => s.SeasonTalent is { } talent && talent.Plan.TryGetValue(talent.CurPlanId, out var plan)
                ? plan.TalentNodes.Values.Sum(n => n.CurrentLevel) : 0,
            TPetsDeployed => s.Pets?.SetupPlans.GetValueOrDefault(s.Pets.CurrentPlan)?.SetUpPets.Values.LongCount(id => id != 0) ?? 0,
            TTowerFloor or TTowerFloorReached => s.Tower is { LayerConfID: > 0 } tower && _data.Table("PetFloorConfig").Values
                .FirstOrDefault(r => GameData.Int(r, "_iID") == tower.LayerConfID) is { ValueKind: JsonValueKind.Object } floor
                ? GameData.Int(floor, "_iFloor") : 0,
            TStrength => s.EquipLevels.Strength.GetValueOrDefault((int)t[1]),
            TRentCareerSpecial => CareerSpecialService.HoldsSubType(s, _data, t[1]) ? 1 : 0,
            TValhallaLayer => DungeonService.ValhallaCleared(s.Dungeons).Contains(t[1]) ? 1 : 0,
            TMonsterHunt => MonsterHuntService.AreaProgress(s, t[1]),
            // Pets load after the quests at login (RoleSystems): keep the stored value until the post-login refresh.
            TPetQuality => s.Pets?.ListPet.Values.LongCount(pet => PetQuality(pet.PetId) >= t[1]) ?? -1,
            TPetLevel => s.Pets?.ListPet.Values.LongCount(pet => pet.Level >= t[1]) ?? -1,
            TPetQualityStar => s.Pets == null ? -1
                : FightPets(s).LongCount(pet => PetQuality(pet.PetId) >= t[1] && pet.Star >= t[2]),
            TPetCamp => s.Pets == null ? -1 : FightPets(s).Select(pet => _data.Row("PetBaseConfig", pet.PetId) is { } row
                ? GameData.Int(row, "_iCamp") : 0).Where(camp => camp > 0).GroupBy(camp => camp).Any(g => g.Count() >= 3) ? 1 : 0,
            TPetSupport => s.Pets == null ? -1
                : s.Pets.SetupPlans.GetValueOrDefault(s.Pets.CurrentPlan)?.SupportPets.Values.LongCount(id => id != 0) ?? 0,
            TTotemQuality => TotemService.Deployed(s, _data).LongCount(i => ItemQuality(i.Entry) >= t[1]),
            TTotemSkills => TotemService.Deployed(s, _data).SelectMany(TotemService.Skills).LongCount(skill =>
                _data.Row("TotemSkillConfig", skill) is { } row && GameData.Int(row, "_iQuality") >= t[1]),
            TTotemMaxed => TotemService.Deployed(s, _data).Where(i => ItemQuality(i.Entry) >= t[1])
                .Sum(i => (long)TotemService.MaxedAttributes(_data, i)),
            TEchoQuality => EmblemService.Worn(s).LongCount(i => ItemQuality(i.Entry) >= t[1]),
            TEchoLevel => EmblemService.Worn(s).LongCount(i => (i.Attrs?.GetValueOrDefault(1001) ?? 0) >= t[1]),
            TEchoResonance => EmblemService.Resonances(s, _data).Values.LongCount(stage => stage >= t[1]),
            TEnchants => s.Items.Where(i => i.Grid > 0 && ItemQuality(i.Entry) >= t[1]
                && _data.Row("ItemConfig", i.Entry) is { } row && GameData.Int(row, "_iType") == 5).Sum(i => (long)i.Enchantments.Count),
            TStunts => s.Items.LongCount(i => i.Grid > 0 && _data.Row("ItemConfig", i.Entry) is { } row
                && GameData.Int(row, "_iType") == 5 && EquipmentAffixes.Specials(i).LongCount() >= t[1]),
            TPetRoom => s.Pets?.IncubationLv ?? -1,
            // Guild membership and the friend list load after the quests at login: keep the stored value until then.
            TJoinGuild => s.Guild == null ? -1 : s.Guild.GuildId != 0 ? 1 : 0,
            TFriends => FriendCount(s),
            TEchoLevelSum => EmblemService.Worn(s).Sum(i => i.Attrs?.GetValueOrDefault(1001) ?? 0),
            TLiveness => _character.CountOf(s, 5),
            _ => -1,
        };
    }

    private long PetQuality(int pet) => _data.Row("PetBaseConfig", pet) is { } row ? GameData.Int(row, "_iQuality") : -1;

    /// <summary>The current plan's fight (出战) pets that are owned.</summary>
    private static IEnumerable<PetData> FightPets(GameSession s) =>
        s.Pets?.SetupPlans.GetValueOrDefault(s.Pets.CurrentPlan)?.SetUpPets.Values
            .Select(id => s.Pets.ListPet.GetValueOrDefault(id)).OfType<PetData>() ?? [];

    private long ItemQuality(int entry) => _data.Row("ItemConfig", entry) is { } row ? GameData.Int(row, "_iQuality") : -1;

    /// <summary>
    /// 103024's last parameter is the crafted item's ItemConfig _iEquipPart (TaskConfig: 2 weapons, 4 tops, 7 accessories;
    /// EquipCraftConfig 2070 makes only part 7), not the recipe's _iEquipType (70) the craft request carries. Part 2 is
    /// read as any weapon, two-handed part 1 included (inferred: the weapon recipes make both).
    /// </summary>
    private bool CraftPartMatches(int entry, long part)
    {
        long made = _data.Row("ItemConfig", entry) is { } row ? GameData.Int(row, "_iEquipPart") : -1;
        return part == 0 || made == part || (part == 2 && made == 1);
    }

    private bool IsDescendant(int career, long ancestor)
    {
        var seen = new HashSet<long>();
        long current = career;
        while (seen.Add(current) && _data.Row("JobConfig", current) is { } job)
        {
            current = GameData.Int(job, "_iParentId");
            if (current == ancestor) return true;
            if (current <= 0) break;
        }
        return false;
    }

    /// <summary>
    /// Since-accept counters (inferred). Killer credit is separate from participation: each positive-damage
    /// contributor still on the death's map receives one 104014/103043 event, irrespective of the last hit.
    /// Weak monster keys disappear on despawn; no claim can be made by sending 31214.
    /// </summary>
    private ReservationKey[] GetKillTaskParticipantKeys(GameSession killer, MonsterUnit monster)
    {
        var settlement = monster.RewardSettlement ?? throw new InvalidOperationException("Kill source was not staged.");
        if (settlement.TaskObligationsStaged || settlement.AfkRecordStaged || settlement.HuntStaged)
        {
            // A failed lethal admission restores HP. Rebuild the callbacks from the next admitted state rather
            // than replaying stale task/AFK/Monster Hunt snapshots over intervening owner work (latest stage first).
            settlement.ResetHuntStage?.Invoke();
            settlement.ResetAfkStage?.Invoke();
            settlement.ResetTaskStage?.Invoke();
            settlement.TaskParticipantIds = null;
        }
        if (settlement.TaskParticipantIds == null)
        {
            var credited = _participants.TryGetValue(monster, out var ids) ? ids : [];
            settlement.TaskParticipantIds = _sessions.All()
                .Where(s => s.Role != null && s.Tasks != null && s.WorldScene == monster.WorldScene &&
                    (ReferenceEquals(s, killer) || credited.Contains(s.Role.Id)))
                .Select(s => s.Role!.Id).Distinct().Order().ToArray();
        }
        return settlement.TaskParticipantIds.Select(id => ReservationKey.Role(id)).ToArray();
    }

    /// <summary>Freeze the exact running targets under all participant role reservations before the kill writer
    /// starts. A later task acceptance cannot consume this already committed occurrence.</summary>
    private void StageKillTaskObligations(GameSession killer, MonsterUnit monster, RewardSettlement settlement)
    {
        settlement.ResetTaskStage?.Invoke();
        if (killer.Role == null) throw new InvalidOperationException("Kill task source has no killer role.");
        var context = killer.Owners?.Current ?? throw new InvalidOperationException("Kill task progress requires its owner.");
        var candidates = settlement.TaskParticipantIds ?? throw new InvalidOperationException("Kill task roles were not reserved.");
        var frozen = new List<(long Role, byte[]? BeforeTasks, byte[] AfterTasks)>();
        var applied = new List<(GameSession Session, object? Attachment, DBTaskInfo Tasks)>();
        foreach (long role in candidates)
        {
            context.AssertRole(role);
            var session = _sessions.All().SingleOrDefault(s => s.Role?.Id == role && s.Tasks != null &&
                s.WorldScene == monster.WorldScene && s.Attachment?.Cancelled != true);
            if (session?.Owner is not { } owner || !context.CanPin(owner)) continue;
            context.Pin(owner);
            var next = session.Tasks!.Clone();
            bool changed = false;
            foreach (var info in next.Infos.Values.Where(x => x.Status == Running))
            {
                if (!Defs().TryGetValue(info.TaskId, out var def)) continue;
                for (int index = 0; index < def.Targets.Length && index < info.CompleteNum.Count; index++)
                {
                    var target = def.Targets[index];
                    bool matches = target[0] switch
                    {
                        TKill => ReferenceEquals(session, killer) && target.Length >= 3 && target[1..^1].Contains(monster.Template.NpcId),
                        TKillInvolved => target.Length >= 3 && target[1..^1].Contains(monster.Template.NpcId),
                        TMapKill => target.Length == 3 && target[1] == monster.SceneEntry,
                        TKillOnMap => ReferenceEquals(session, killer) && target.Length == 3 && target[2] == monster.SceneEntry,
                        _ => false
                    };
                    if (!matches) continue;
                    long required = Required(session, target);
                    if (required <= 0 || info.CompleteNum[index] >= required) continue;
                    info.CompleteNum[index] = Math.Min(required, checked(info.CompleteNum[index] + 1));
                    changed = true;
                }
            }
            if (!changed) continue;
            byte[]? beforeTasks = _roles.LoadTasks(role);
            frozen.Add((role, beforeTasks, next.ToByteArray()));
            applied.Add((session, session.Attachment, next));
        }
        var priorAfterCards = settlement.EnlistAfterCards;
        var priorApplied = settlement.Applied;
        settlement.ResetTaskStage = () =>
        {
            settlement.EnlistAfterCards = priorAfterCards;
            settlement.Applied = priorApplied;
            settlement.TaskObligationsStaged = false;
            settlement.ResetTaskStage = null;
        };
        if (frozen.Count > 0)
        {
            settlement.EnlistAfterCards = builder =>
            {
                priorAfterCards?.Invoke(builder);
                var values = frozen.Select(x =>
                {
                    byte[]? beforeFollowup = _roles.ReadTaskFollowup(builder, x.Role);
                    var pending = beforeFollowup is { Length: > 0 }
                        ? JsonSerializer.Deserialize<HashSet<string>>(beforeFollowup) ?? [] : new HashSet<string>();
                    pending.Add("kill/" + settlement.SourceKey);
                    return new KillTaskProgress(x.Role, x.BeforeTasks, x.AfterTasks, beforeFollowup,
                        JsonSerializer.SerializeToUtf8Bytes(pending));
                }).ToArray();
                builder.Add(new KillTaskProgressPlan(settlement.SourceKey, values));
            };
            settlement.Applied = () =>
            {
                priorApplied?.Invoke();
                foreach (var (session, attachment, tasks) in applied)
                    if (ReferenceEquals(session.Attachment, attachment) && session.Role != null)
                        session.Tasks = tasks;
            };
        }
        settlement.TaskObligationsStaged = true;
    }

    private async Task OnKillAsync(GameSession killer, MonsterUnit monster)
    {
        var participants = _participants.TryGetValue(monster, out var credited) ? credited.ToHashSet() : new HashSet<long>();
        if (killer.Role != null) participants.Add(killer.Role.Id);
        _participants.Remove(monster);
        if (monster.RewardSettlement?.TaskObligationsStaged == true)
        {
            await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null &&
                monster.RewardSettlement.TaskParticipantIds?.Contains(s.Role.Id) == true),
                async s => { using var gate = await LockTasksAsync(s); await SettleAsync(s); });
            return;
        }
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && s.WorldScene == monster.WorldScene && participants.Contains(s.Role.Id)),
            s => ProgressAsync(s, null, t => t[0] switch
            {
                TKill when s == killer && t.Length >= 3 && t[1..^1].Contains(monster.Template.NpcId) => 1,
                TKillInvolved when t.Length >= 3 && t[1..^1].Contains(monster.Template.NpcId) => 1,
                TMapKill when t.Length == 3 && t[1] == monster.SceneEntry => 1,
                TKillOnMap when s == killer && t.Length == 3 && t[2] == monster.SceneEntry => 1,
                _ => 0,
            }));
    }

    private Task OnItemUsedAsync(GameSession session, int entry, int count) =>
        ProgressAsync(session, TUse, t => t.Length == 3 && t[1] == entry ? count : 0);

    /// <summary>Only EquipmentService's successful paid craft is a source, never possession or a GM item grant.</summary>
    private Task OnCraftedAsync(GameSession s, int entry, int rank, int subtype) => ProgressAsync(s, null, t => t[0] switch
    {
        TCraft when t.Length == 5 && ItemQuality(entry) >= t[1] && rank == t[2] && CraftPartMatches(entry, t[4]) => 1,
        TCraftQuality when t.Length == 3 && ItemQuality(entry) >= t[1] => 1,
        _ => 0,
    });

    /// <summary>TotemService.Combined fires inside the character lock: the progress runs on the owner loop after it.</summary>
    public void OnTotemCombined(GameSession session, int entry) => _loop.PostOwned(session, () =>
        ProgressAsync(session, TTotemCombine, t => t.Length == 3 && ItemQuality(entry) >= t[1] ? 1 : 0));

    /// <summary>31210 is completion. 84000 arbitrary guide records do not satisfy this objective (§4 C5).</summary>
    public async Task OnGuideCompleteAsync(GameSession session, long guide)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        if (session.Tasks == null || !GuideExists(guide)) return;
        session.TaskRuntime.CompletedGuides.Add(guide);
        await SettleAsync(session);
    }

    /// <summary>
    /// 83651 carries no task, attempt id, or proof. Accept a local clear only for running 10103150 on its tracked
    /// map, one of three persisted slots (inferred). Network replay within those three cannot be distinguished
    /// from another local clear; completion/reward beyond three is rejected, including after reconnect.
    /// </summary>
    public async Task GoodsStackAsync(GameSession session)
    {
        using var gate = await LockTasksAsync(session);
        if (BeforeProgress is { } before) await before(session);
        bool ok = session.Tasks?.Infos.TryGetValue(10103150, out var info) == true && info.Status == Running
            && ContextMapMatches(session, Defs()[10103150]) && Defs()[10103150].Targets.Any(t => t is [TCargo, > 0]);
        if (ok) await ProgressCoreAsync(session, TCargo, _ => 1, d => d.Id == 10103150);
        await session.SendGameAsync(GoodsStack, new TaskGoodsStackResp { Result = ok ? 0 : ErrorCodes.TaskRequirements });
    }

    private static bool ContextMapMatches(GameSession s, TaskDef def)
    {
        var maps = def.Track.Where(a => a.Length >= 3 && a[0] is "142" or "143")
            .Select(a => long.TryParse(a[1], out long map) ? map : 0).Where(m => m > 0).ToList();
        return maps.Count == 0 || maps.Contains(s.SceneEntry);
    }

    internal static async Task<IDisposable> LockTasksAsync(GameSession s)
    {
        var role = s.Role;
        var attachment = s.Attachment;
        var owner = s.Owner;
        long? epoch = owner?.LifecycleEpoch;
        Ownership.LegacyOwnerScope? scope = null;
        if (role != null && s.Owners is { } owners)
            scope = await owners.EnterAsync("Task.TaskLock", Ownership.ReservationKey.Role(role.Id));
        bool acquired = false;
        try
        {
            await s.TaskLock.WaitAsync();
            acquired = true;
            if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment)
                || !ReferenceEquals(s.Owner, owner) || owner?.LifecycleEpoch != epoch)
                throw new OperationCanceledException("Role changed before the task gate was acquired.");
            if (owner != null) scope?.Context.Pin(owner);
            return new TaskLockReleaser(s.TaskLock, scope);
        }
        catch
        {
            if (acquired) s.TaskLock.Release();
            scope?.Dispose();
            throw;
        }
    }

    private sealed class TaskLockReleaser(SemaphoreSlim gate, IDisposable? scope) : IDisposable
    {
        public void Dispose() { gate.Release(); scope?.Dispose(); }
    }
}

/// <summary>Extra authoritative quest source state; stored alongside DBTaskInfo. No new client dataset.</summary>
public sealed class TaskRuntimeState
{
    public HashSet<long> CompletedGuides { get; set; } = new();
    public bool InCutScene { get; set; }
}
