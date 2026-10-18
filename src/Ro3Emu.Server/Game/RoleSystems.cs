using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What every game system does for a role, in order: when it is loaded, when it logs in (each step followed by the
/// selection check), when it moves and when it leaves. RoleSessionService runs the selection around these; the order
/// here is the client's, see the comments on each step.
/// </summary>
public sealed class RoleSystems(Maps.World world, ChatService chat, CharacterService character, SkillService skills, FunctionService functions, CombatService combat, BuffService buffs, SceneService scene, TaskService tasks, DailyActivityService daily, GuideService guides, EmojiService emoji, PersonalService personal, SocialOutfitService socialOutfit, AchievementService achievements, Micro.FriendService friends, MvpService mvps, CardService cards, ShopService shops, EmblemService emblems, SeasonService seasons, SeasonPageService seasonPage, SeasonRefineService seasonRefine, SeasonTalentService talents, PetService pets, TowerService tower, ValhallaService valhalla, MonsterHuntService monsterHunt, GrowthClassService growthClass, FirstHeartService firstHeart, MailService mail, AuctionService auctions, GuildService guilds, AdventureGroupService adventureGroups, GuildCaravanService caravan, GuildChallengeService guildChallenge, DungeonService dungeons, DungeonMatchService dungeonMatches, InstanceService instances, TradeService trade, StallsService stalls, LifeJobService life, GatheringService gathering, WildAfkService afk, WorldCoreService worldCore, WorldInteractionService interaction, NewActivityService activities, TimedEventService timedEvents, PronteraDefenseService defense, TimedBattleService timedBattles, CasualBattleService casual, FestivalService festival, TrainingService training, LotteryService lottery, MountService mounts, CareerSpecialService careerSpecial, WardrobeService wardrobe, TotemService totems, GenreService genre, PowerService power, TerritoryWarsService territory, TerritoryEscortService escort, Persistence.SettingsPersistence settings, ILogger<RoleSystems> log)
{
    internal async Task LoadAsync(GameSession session)
    {
        power.Load(session); // before the first plan dataset: 5007/5025/5026/5067 carry plan power
        character.Load(session);
        await character.FinishLoadAsync(session);
        careerSpecial.Load(session); // before mounts: a leased mount entitles the battle slot
        valhalla.Load(session);
        monsterHunt.Load(session);
        growthClass.Load(session);
        firstHeart.Load(session);
        mounts.Load(session);
        totems.Load(session);
        await life.LoadAsync(session);
        seasons.Load(session);
        session.ActivitiesReady = false;
        session.NewActivities = null;
        session.DailyReady = false;
        afk.Load(session);
        daily.Load(session);
        await emblems.LoadAsync(session);
        talents.Load(session);
        seasonRefine.Load(session);
        power.Prime(session); // the login 5066 carries the computed power (pets join at the login publish)
        character.Refresh(session); // first entity/stats use this role's saved active talent plan
    }

    internal async Task LoginAsync(GameSession session, Action checkpoint)
    {
        await chat.OnRoleEnteredAsync(session);
        checkpoint();
        await seasons.LoginAsync(session); // 5021 early: multiverse lookups read its playBookId
        checkpoint();
        await character.SendLoginSyncAsync(session);
        checkpoint();
        await skills.LoadAsync(session); // after character/cart sync, before login rewards can call skill sync under StateLock
        checkpoint();
        await life.LoginAsync(session); // 5018 vigor then 5010 before To-Do/tasks/functions consume life state.
        checkpoint();
        await seasonPage.LoginAsync(session); // 5022 before the Season/honor panels can open
        checkpoint();
        await seasonRefine.LoginAsync(session); // 5023 after 5007, before function unlocks
        checkpoint();
        await wardrobe.LoginAsync(session);
        checkpoint();
        await mounts.LoginAsync(session);
        checkpoint();
        await careerSpecial.LoginAsync(session); // 5002: the equipment window reads SpecialPropData unguarded
        checkpoint();
        await totems.SyncAsync(session);
        checkpoint();
        // 5035 must precede To-Do/scene loading: LC_OnHookHelper calls Mathf.Max on its remaining seconds.
        await afk.LoginAsync(session);
        checkpoint();
        await tasks.LoginAsync(session); // 5027 before 5056: quest-gated functions (condition 4004)
        checkpoint();
        await daily.LoginAsync(session); // 5004 and 5059 before 5056 opens To-Do / Dodo
        checkpoint();
        await afk.ReconcileAsync(session); // finish any held old-day award before exposing today's allowance
        checkpoint();
        await talents.SyncAsync(session); // TaskService may return without quest config; 5050 still has safe nonempty maps
        checkpoint();
        await guides.LoginAsync(session);
        checkpoint();
        await emoji.LoginAsync(session); // 5006: the emote panel enables only unlocked emotes
        checkpoint();
        await personal.LoginAsync(session); // 5016 before 5056 opens Profile (function 63)
        await socialOutfit.LoginAsync(session); // 5044: the own profile card reads the worn title and honor
        checkpoint();
        await cards.LoginAsync(session); // after 5009: inserted cards are its storagePack
        checkpoint();
        await tower.LoginAsync(session); // 5065 before tower-dependent functions and shop conditions
        checkpoint();
        await dungeons.LoginAsync(session); // 5062 before opening the Dungeon menu
        await valhalla.LoginAsync(session); // 5046 after 5062: cleared layers are dungeon records
        await monsterHunt.LoginAsync(session); // 5053
        checkpoint();
        await emblems.LoginAsync(session); // after 5009: plan GUIDs refer to item instances
        checkpoint();
        await shops.LoginAsync(session); // after 5066: purchase replies read PlayerBasicInfo.id
        checkpoint();
        await trade.LoginAsync(session); // 5033 before 5056 makes the trade UI reachable.
        checkpoint();
        await stalls.LoginAsync(session); // 5058 is metadata; stock remains in 33408/33411.
        checkpoint();
        await guilds.LoginAsync(session); // 5047 and, for members only, 32213 before 5056 opens guild UI
        checkpoint();
        await adventureGroups.LoginAsync(session); // 5068 and, for members, 34011 before 5056 opens the Adv. Guild
        checkpoint();
        await guildChallenge.LoginAsync(session);
        checkpoint();
        await caravan.LoginAsync(session); // role caravan counters in 5047 before the activity card opens
        checkpoint();
        await territory.LoginAsync(session);
        checkpoint();
        await escort.LoginAsync(session);
        checkpoint();
        await defense.LoginAsync(session);
        checkpoint();
        await activities.LoginAsync(session); // 5013/5042/5054 before gated activity panels
        checkpoint();
        await dungeons.DeliverJourneyPendingAsync(session);
        checkpoint();
        await timedEvents.LoginAsync(session);
        checkpoint();
        await timedBattles.LoginAsync(session);
        checkpoint();
        await casual.LoginAsync(session); // Mock Battle payouts left pending, on the timed-battle state just loaded
        checkpoint();
        await festival.LoginAsync(session);
        checkpoint();
        await training.LoginAsync(session);
        checkpoint();
        await auctions.LoginAsync(session); // 5063 personal state; 82000/82005 supply actual lists
        checkpoint();
        await pets.LoginAsync(session); // 5017 before 5056: opening a function must not expose an uninitialized pet DAL.
        checkpoint();
        await genre.LoginAsync(session);
        await power.LoginAsync(session); // 31502/31503/5066 once every power owner (pets, Build) is loaded
        session.DeferTaskSync = false;
        checkpoint();
        await tasks.RefreshAsync(session); // all owner snapshots are now available for passive objectives; settles with the login's one full 5027
        await growthClass.LoginAsync(session); // 5045 after the login's quest settlement: it reads the delivered growth quests
        checkpoint();
        await achievements.LoginAsync(session); // one full 5019 after every owner snapshot its conditions read; function 21 opens at level 1
        checkpoint();
        await firstHeart.LoginAsync(session); // 5049 after 5019: its login-day tasks read the achievement count
        checkpoint();
        session.DeferFunctionSync = false;
        await functions.SyncAsync(session);
        checkpoint();
        await lottery.LoginAsync(session);
        checkpoint();
        await mail.LoginAsync(session); // offline deliveries invalidate the mail categories after role selection
        await mvps.RecoverAsync(session); // frozen MVP outcomes survive commit-before-delivery and restart
        checkpoint();
        await skills.SyncAsync(session); // after 5066: skill points need PlayerBasicInfo.career
        checkpoint();
        await buffs.InitializeEnergyAsync(session);
        checkpoint();
        await worldCore.LoginAsync(session);
        checkpoint();
    }

    /// <summary>The keys the login steps write besides the role, its account and lifecycle.</summary>
    internal async Task<Ownership.ReservationKey[]> LoginKeysAsync(long roleId) =>
        [.. defense.LoginKeys(roleId), .. activities.LoginKeys(roleId), .. territory.LoginKeys(),
         .. caravan.LoginKeys(roleId), .. guildChallenge.LoginKeys(roleId),
         .. await mvps.LoginKeysAsync(roleId), .. await dungeons.LoginKeysAsync(roleId),
         .. await casual.LoginKeysAsync(roleId), .. await timedBattles.LoginKeysAsync(roleId)];

    /// <summary>The shared keys the leave steps write: the own guild (offline publication) and the timed battle.</summary>
    internal Ownership.ReservationKey[] LeaveKeys(GameSession session) =>
        [.. guilds.OwnGuildKeys(session), .. timedBattles.CleanupKeys(session)];

    internal async Task MovedAsync(GameSession session, WalkReq walk)
    {
        await world.MoveAsync(session, walk);
        await gathering.PlayerMovedAsync(session);
        combat.PlayerMoved(session);
        await emoji.PlayerMovedAsync(session);
        await dungeons.PlayerMovedAsync(session); // scripted scene regions and points
    }

    /// <summary>Each essential step runs through <paramref name="step"/>, which collects its failure.</summary>
    internal async Task LeaveAsync(GameSession session, Func<string, Func<Task>, Task> step, Action savePosition)
    {
        await step("scene", () => { scene.DisconnectLine(session); return Task.CompletedTask; });
        await step("combat", () => combat.ClearSkillsAsync(session));
        await step("interaction", () => interaction.DisconnectAsync(session));
        await step("gathering", () => gathering.InterruptAsync(session, notify: false));
        await step("life", () => life.CancelAsync(session));
        if (session.Role is { } leaving)
        {
            try { await friends.OnlineChangedAsync(leaving.Id, online: false); }
            catch (Exception e) { log.LogWarning(e, "Optional offline friend notice failed for role {Role}", leaving.Id); }
        }
        if (session.RolePositionReady)
            await step("position", () => { savePosition(); return Task.CompletedTask; });
        await step("settings", () => { settings.Flush(session); return Task.CompletedTask; });
        await step("territory", () => territory.DisconnectAsync(session));
        var afkState = session.WildAfk;
        await step("afk", () => afk.LeaveAsync(session));
        // AFK.Leave detaches its session state. A held old-day award still needs the old
        // daily/task rows on the next login, so the ordinary daily reset must wait for it.
        if (!WildAfkService.PendingDailyAward(afkState, session.Daily, session.Tasks, session.DailyRows))
            await step("daily boundary", () => daily.ReconcileAsync(session));
        await step("dungeon", () => dungeons.AbandonAsync(session));
        await step("tower", () => tower.AbandonAsync(session));
        if (session.GuildOnlinePublished)
            await step("guild", () => guilds.OnlineChangedAsync(session, false));
        session.ActivitiesReady = false;
        await step("training", () => training.LeaveRoleAsync(session));
        await step("festival", () => festival.LeaveRoleAsync(session));
        await step("dungeon match", () => dungeonMatches.LeaveRoleAsync(session));
        session.DungeonSoloMatch = false;
        await step("timed battle", () => timedBattles.LeaveRoleAsync(session));
        session.EventFaction = 0;
        await step("instance", () => instances.AbandonAsync(session));
        await step("world", () => world.LeaveAsync(session));
        await step("chat", () => { chat.OnRoleLeft(session); return Task.CompletedTask; });
        await step("world core", () => { worldCore.Disconnected(session); return Task.CompletedTask; });
    }
}
