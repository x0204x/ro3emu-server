using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Http;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Micro;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Server;

/// <summary>Builds the whole emulator (HTTP + both gates); used by Program and by end-to-end tests.</summary>
public static partial class ServerHost
{
    public static WebApplication Create(string[] args, Action<ServerOptions>? configure = null, TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        var options = Resolve(builder.Configuration, configure);
        var clientRequirement = ClientReleaseGate.Read(options);
        // Fail fast, before any service is built, if an offline restore of this data dir did not finish (C/D 19).
        DatabaseRestore.ThrowIfUnfinished(options.DataDir);
        // Stage G: the default host on Windows also writes Warning and above to the Windows Event Log, synchronously on
        // the logging thread - the game loop included (a quarter of a walk's cost at 500 players). The console logger
        // queues to a background writer; the metrics provider registered below stays.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(console =>
        {
            console.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
        });
        // One server per data dir (C/D 19): taken before anything opens the database, released when the app is disposed.
        builder.Services.AddSingleton(_ => RealmLock.Acquire(options.DataDir));
        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton(clock ?? TimeProvider.System);
        // GM and Admin accounts need two-factor authentication on a public panel unless the operator says otherwise.
        options.Panel.RequireAdmin2FA ??= !options.LocalOnly();
        // Loopback for local play and tests; the web panel (docs/guide/panel.md) on its own port, routed by PanelSite.Map.
        builder.WebHost.UseUrls(options.Panel.Enabled
            ? [$"http://{options.ListenAddress()}:{options.HttpPort}", PanelUrl(options)]
            : [$"http://{options.ListenAddress()}:{options.HttpPort}"]);
        // Kestrel stops first and waits for every HTTP connection (a half-sent request holds it for the 30 s header timeout),
        // and the gates, loop and database stop only after it. The reaper aborts what is left a moment after stop begins.
        var httpConnections = new HttpConnectionReaper();
        builder.WebHost.ConfigureKestrel(k => k.ConfigureEndpointDefaults(l => Microsoft.AspNetCore.Connections.ConnectionBuilderExtensions.Use(l, httpConnections.Track)));

        // First hosted service: everything after it (loop, gates, HTTP) starts with the game data already parsed.
        if (options.PreloadGameData) builder.Services.AddHostedService<Ro3Emu.Server.Data.GameDataPreload>();
        builder.Services.AddSingleton<Diagnostics.ServerMetrics>();
        builder.Services.AddSingleton<Diagnostics.SessionCensus>();
        builder.Services.AddSingleton<ILoggerProvider, Diagnostics.MetricsLoggerProvider>();
        // Recent log lines for bug reports (FeedbackService); kept out of the pipeline when reports keep no context.
        builder.Services.AddSingleton<Diagnostics.RecentLogBuffer>();
        if (options.Feedback.ContextLines > 0)
            builder.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<Diagnostics.RecentLogBuffer>());
        builder.Services.AddSingleton<GameLoop>();
        builder.Services.AddSingleton<Deadlines>();
        // Starts before the loop: by then the host has built every hosted service and, through them, every ticker.
        builder.Services.AddHostedService<GameLoopTickers>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<GameLoop>());
        builder.Services.AddSingleton<AccountStore>();
        builder.Services.AddSingleton<RoleStore>();
        builder.Services.AddSingleton<Game.Ownership.OwnerRegistry>();
        builder.Services.AddSingleton<Game.Ownership.RoleHandover>();
        builder.Services.AddSingleton<AuctionStore>();
        builder.Services.AddSingleton<GuildStore>();
        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddSingleton<CharacterService>();
        builder.Services.AddSingleton<SkillService>();
        builder.Services.AddSingleton<FunctionService>();
        builder.Services.AddSingleton<ReconstructedDungeons>();
        builder.Services.AddSingleton<MonsterService>();
        builder.Services.AddSingleton<PublicLineManager>();
        builder.Services.AddSingleton<BuffService>();
        builder.Services.AddSingleton<CombatService>();
        builder.Services.AddSingleton<RewardService>();
        builder.Services.AddSingleton<TaskService>();
        builder.Services.AddSingleton<AwardService>();
        builder.Services.AddSingleton<DailyActivityService>();
        builder.Services.AddSingleton<TestingSwitches>();
        builder.Services.AddSingleton<ActivityClock>();
        builder.Services.AddSingleton<NewActivityService>();
        builder.Services.AddSingleton<TimedEventService>();
        builder.Services.AddSingleton<TimedTodoSchedule>();
        builder.Services.AddSingleton<PronteraDefenseService>();
        builder.Services.AddSingleton<TimedBattleService>();
        builder.Services.AddSingleton<DungeonMatchService>();
        builder.Services.AddSingleton<FestivalService>();
        builder.Services.AddSingleton<RankRewardService>();
        builder.Services.AddSingleton<TrainingService>();
        builder.Services.AddSingleton<LotteryService>();
        builder.Services.AddSingleton<GuildHomeService>();
        builder.Services.AddSingleton<GuildCampfireService>();
        builder.Services.AddSingleton<GuildHuntingService>();
        builder.Services.AddSingleton<GuildChallengeStore>();
        builder.Services.AddSingleton<GuildChallengeService>();
        builder.Services.AddSingleton<DraculaWeddingService>();
        builder.Services.AddSingleton<WildAfkService>();
        builder.Services.AddSingleton<LegacyTavernService>();
        builder.Services.AddSingleton<LifeJobService>();
        builder.Services.AddSingleton<GatheringService>();
        builder.Services.AddSingleton<GuideService>();
        builder.Services.AddSingleton<EmojiService>();
        builder.Services.AddSingleton<PersonalService>();
        builder.Services.AddSingleton<SocialOutfitService>();
        builder.Services.AddSingleton<AchievementService>();
        builder.Services.AddSingleton<TeamService>();
        builder.Services.AddSingleton<MvpService>();
        builder.Services.AddSingleton<CardService>();
        builder.Services.AddSingleton<CardEffects>();
        builder.Services.AddSingleton<TalentProcs>();
        builder.Services.AddSingleton<PassiveSkills>();
        builder.Services.AddSingleton<RidingEffects>();
        builder.Services.AddSingleton<StealthEffects>();
        builder.Services.AddSingleton<SeasonService>();
        builder.Services.AddSingleton<SeasonPageService>();
        builder.Services.AddSingleton<SeasonRefineService>();
        builder.Services.AddSingleton<SeasonTalentService>();
        builder.Services.AddSingleton<ShopService>();
        builder.Services.AddSingleton<ItemBoxService>();
        builder.Services.AddSingleton<MountService>();
        builder.Services.AddSingleton<CareerSpecialService>();
        builder.Services.AddSingleton<WardrobeService>();
        builder.Services.AddSingleton<TotemEffects>();
        builder.Services.AddSingleton<TotemService>();
        builder.Services.AddSingleton<GenreService>();
        builder.Services.AddSingleton<PowerService>();
        builder.Services.AddSingleton<Game.MailService>();
        builder.Services.AddSingleton<AuctionService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AuctionService>());
        builder.Services.AddSingleton<MarketStore>();
        builder.Services.AddSingleton<TradeService>();
        builder.Services.AddSingleton<StallsService>();
        builder.Services.AddSingleton<GuildService>();
        builder.Services.AddSingleton<GuildVoiceService>();
        builder.Services.AddSingleton<TavernService>();
        builder.Services.AddSingleton<AdventureGroupStore>();
        builder.Services.AddSingleton<AdventureGroupService>();
        builder.Services.AddSingleton<GuildCaravanService>();
        builder.Services.AddSingleton<EmblemService>();
        builder.Services.AddSingleton<EquipmentService>();
        builder.Services.AddSingleton<InstanceService>();
        builder.Services.AddSingleton<DungeonCatalog>();
        builder.Services.AddSingleton<DungeonObligationStore>();
        builder.Services.AddSingleton<PraiseService>();
        builder.Services.AddSingleton<CasualBattleService>();
        builder.Services.AddSingleton<DungeonService>();
        builder.Services.AddSingleton<TerritoryWarsClock>();
        builder.Services.AddSingleton<TerritoryWarsService>();
        builder.Services.AddSingleton<TerritoryEscortService>();
        builder.Services.AddSingleton<GuildLeagueCalendar>();
        builder.Services.AddSingleton<GuildLeagueService>();
        builder.Services.AddSingleton<TowerService>();
        builder.Services.AddSingleton<ValhallaService>();
        builder.Services.AddSingleton<MonsterHuntService>();
        builder.Services.AddSingleton<GrowthClassService>();
        builder.Services.AddSingleton<ParagonService>();
        builder.Services.AddSingleton<FlyingDragonService>();
        builder.Services.AddSingleton<TitleService>();
        builder.Services.AddSingleton<FirstHeartService>();
        builder.Services.AddSingleton<AppraisalService>();
        builder.Services.AddSingleton<EnchantService>();
        builder.Services.AddSingleton<PetFollowerService>();
        builder.Services.AddSingleton<PetService>();
        builder.Services.AddSingleton<TrafficLog>();
        builder.Services.AddSingleton<MsgCatalog>();
        builder.Services.AddSingleton<IAoiSink, GameAoiSink>();
        builder.Services.AddSingleton<World>();
        builder.Services.AddSingleton<WorldData>();
        builder.Services.AddSingleton<SceneService>();
        builder.Services.AddSingleton<WorldEffectsService>();
        builder.Services.AddSingleton<WorldCoreService>();
        builder.Services.AddSingleton<WorldCommonService>();
        builder.Services.AddSingleton<WorldInteractionService>();
        builder.Services.AddSingleton<FriendExtraService>();
        builder.Services.AddSingleton<ChatService>();
        builder.Services.AddSingleton<ChatFloodGuard>();
        builder.Services.AddSingleton<FeedbackService>();
        builder.Services.AddSingleton<SkillTraceTelemetryService>();
        builder.Services.AddSingleton<GmCommands>();
        builder.Services.AddSingleton<Ro3Emu.Server.Data.GameData>();
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<GateAuthRegistry>();
        builder.Services.AddSingleton<ReconnectService>();
        builder.Services.AddSingleton<Voice.VoiceTicketRegistry>();
        builder.Services.AddSingleton<Voice.VoiceRooms>();
        // The voice relay PoC only opens a second listener when a port is configured for it.
        if (options.Voice.Port > 0) builder.Services.AddHostedService<Voice.VoiceGateServer>();
        // A singleton too: the panel's server control lists backups and makes one on demand.
        builder.Services.AddSingleton<DatabaseBackup>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DatabaseBackup>());
        builder.Services.AddSingleton<CheatMonitor>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<CheatMonitor>());
        builder.Services.AddSingleton<ReceiptRetention>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ReceiptRetention>());
        builder.Services.AddSingleton<Game.Persistence.PositionPersistence>();
        builder.Services.AddSingleton<Game.Persistence.SettingsPersistence>();
        builder.Services.AddSingleton<Game.Persistence.WildAfkPersistence>();
        builder.Services.AddSingleton<RoleSystems>();
        builder.Services.AddSingleton<RoleSessionService>();
        // Message modules in this order: it is the order of their interceptors (World before Economy before Character).
        builder.Services.AddSingleton<Game.Messaging.IMessageModule, CoreMessages>();
        builder.Services.AddSingleton<Game.Messaging.IMessageModule, WorldMessages>();
        builder.Services.AddSingleton<Game.Messaging.IMessageModule, EconomyMessages>();
        builder.Services.AddSingleton<Game.Messaging.IMessageModule, CharacterMessages>();
        builder.Services.AddSingleton<Game.Messaging.IMessageModule, SocialMessages>();
        builder.Services.AddSingleton<Game.Messaging.IMessageModule, ActivitiesMessages>();
        builder.Services.AddSingleton<Game.Messaging.IMessageModule, AdventureGroupMessages>();
        builder.Services.AddSingleton<Game.Messaging.IMessageModule, Voice.VoiceTicketService>();
        builder.Services.AddSingleton<Game.Messaging.IMessageModule>(sp => sp.GetRequiredService<SkillTraceTelemetryService>());
        builder.Services.AddSingleton<Game.Messaging.IMessageModule>(sp => sp.GetRequiredService<GuildVoiceService>());
        builder.Services.AddSingleton<Game.Messaging.IMessageModule>(sp => sp.GetRequiredService<TavernService>());
        builder.Services.AddSingleton<GameMessageHandler>();
        builder.Services.AddSingleton(sp => GateCrypto.LoadOrCreate(options.DataDir, sp.GetRequiredService<ILogger<GateCrypto>>()));
        builder.Services.AddSingleton(_ => OperatorToken.LoadOrCreate(options.DataDir, options.OperatorToken));
        builder.Services.AddSingleton<IMicroService, LoginService>();
        builder.Services.AddSingleton<IMicroService, ClusterRecommendService>();
        builder.Services.AddSingleton<IMicroService, PhonestGateService>();
        builder.Services.AddSingleton<IMicroService, PlatformProxyService>();
        builder.Services.AddSingleton<SocialOwnerCache>();
        builder.Services.AddSingleton<IMicroService, UserDataService>();
        builder.Services.AddSingleton<MicroSessions>();
        builder.Services.AddSingleton<FriendStore>();
        builder.Services.AddSingleton<FriendService>();
        builder.Services.AddSingleton<IMicroService>(sp => sp.GetRequiredService<FriendService>());
        builder.Services.AddSingleton<IMicroService, NameService>();
        builder.Services.AddSingleton<IMicroService, PartyService>();
        builder.Services.AddSingleton<IMicroService>(sp => sp.GetRequiredService<Game.MailService>());
        builder.Services.AddSingleton<RankService>();
        builder.Services.AddSingleton<IMicroService>(sp => sp.GetRequiredService<RankService>());
        builder.Services.AddSingleton<MicroServiceRouter>();
        builder.Services.AddHostedService<MicroGateServer>();
        builder.Services.AddHostedService<GameGateServer>();
        // Hosted services stop in reverse order: close ordinary admission before either gate begins teardown.
        builder.Services.AddHostedService<GameLoopQuiesceService>();
        // Stops before the quiesce: roles waiting for a reconnect are logged out while cleanup is still admitted.
        builder.Services.AddHostedService<ReconnectShutdownService>();

        builder.Services.AddSingleton<CredentialLimiter>();
        builder.Services.AddSingleton(options.Panel);
        builder.Services.AddSingleton<PanelBackend>();
        builder.Services.AddSingleton<Ro3Emu.Panel.IPanelBackend>(sp => sp.GetRequiredService<PanelBackend>());
        builder.Services.AddSingleton<Ro3Emu.Panel.IPanelAdminTools, PanelAdminTools>();
        builder.Services.AddSingleton<StaffRoles>();
        builder.Services.AddSingleton<MaintenanceMode>();
        builder.Services.AddSingleton<PanelOperations>();
        builder.Services.AddSingleton<Ro3Emu.Panel.IPanelOperations>(sp => sp.GetRequiredService<PanelOperations>());
        builder.Services.AddSingleton<Updates.UpdateManager>();
        builder.Services.AddSingleton<Ro3Emu.Panel.IPanelUpdates>(sp => sp.GetRequiredService<Updates.UpdateManager>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Updates.UpdateManager>());
        builder.Services.AddSingleton<Ro3Emu.Panel.PanelSite>();
        builder.Services.AddSingleton<GiftCodeLimiter>();
        builder.Services.AddSingleton<GiftCodeService>();

        var app = builder.Build();
        app.Lifetime.ApplicationStopping.Register(() => httpConnections.AbortAfter(HttpConnectionReaper.Grace));
        app.Services.GetRequiredService<RealmLock>();
        // After the realm lock, so a second server on the same data dir never rewrites the running one's token; before
        // the ports open, so a tool started with the server finds the file.
        app.Services.GetRequiredService<OperatorToken>();
        app.Services.GetRequiredService<Game.Ownership.RoleHandover>()
            .Configure(session => app.Services.GetRequiredService<RoleSessionService>().CleanupRoleAsync(session),
                session => app.Services.GetRequiredService<RoleSessionService>().CleanupReservation(session));
        app.Services.GetRequiredService<Diagnostics.ServerMetrics>().Owners = app.Services.GetRequiredService<Game.Ownership.OwnerRegistry>();
        // First: the panel's port serves only the panel, without the server's request log and endpoints.
        if (options.Panel.Enabled) Ro3Emu.Panel.PanelSite.Map(app, options.Panel.Port);
        // Mount energy meters (EnergyConfig._iBelongMount): BuffService cannot take MountService (MountService -> SceneService -> BuffService).
        app.Services.GetRequiredService<MountService>().Changed.Add(app.Services.GetRequiredService<BuffService>().MountsChangedAsync);
        app.Services.GetRequiredService<CharacterService>().ExpCredited.Add(app.Services.GetRequiredService<CheatMonitor>().ExpGained);
        // Team chat channels (ChatService cannot take TeamService: TeamService -> ChatService).
        app.Services.GetRequiredService<ChatService>().TeamChannelAllowed = app.Services.GetRequiredService<TeamService>().ChannelAllowed;
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) && app.Services.GetRequiredService<GameLoop>().Phase != GameLoopPhase.Accepting)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            if (app.Logger.IsEnabled(LogLevel.Debug)) app.Logger.LogDebug("HTTP {Method} {Path}{Query}", context.Request.Method, context.Request.Path, context.Request.QueryString);
            await next();
        });
        SdkEndpoints.Map(app);
        app.MapGet(Ro3Emu.ClientRelease.ClientReleaseRequirement.Endpoint, () => clientRequirement is null
            ? Results.NotFound() : Results.Json(clientRequirement));
        GiftCodeEndpoints.Map(app);
        Diagnostics.MetricsEndpoints.Map(app);
        OpsEndpoints.Map(app);
        AccountEndpoints.Map(app);
        // The web panel's public address for launchers that joined (Ro3Emu:Panel:PublicUrl); 404 without one.
        app.MapGet("/ro3emu/panel", (Microsoft.Extensions.Options.IOptions<ServerOptions> options) =>
            options.Value.Panel is { Enabled: true, PublicUrl: { Length: > 0 } url } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                ? Results.Json(new { url = uri.ToString().TrimEnd('/') })
                : Results.NotFound());
        // Public status for the launcher's online pill: only a count, never account or character names. Read from the
        // loop's census, so anyone polling it adds no work to the loop.
        app.MapGet("/ro3emu/status", (Diagnostics.SessionCensus census) =>
            Results.Json(new { online = true, players = census.Current.Roles.Count }));
        app.MapFallback((HttpContext context) =>
        {
            if (app.Logger.IsEnabled(LogLevel.Debug)) app.Logger.LogDebug("HTTP unhandled {Method} {Path}", context.Request.Method, context.Request.Path);
            return Results.NotFound();
        });
        // Services that only hook themselves into others.
        app.Services.GetRequiredService<RewardService>();
        app.Services.GetRequiredService<TaskService>(); // hooks kills / level / job changes
        app.Services.GetRequiredService<DailyActivityService>(); // quest settlement and online rollover
        app.Services.GetRequiredService<NewActivityService>(); // activity clocks and reward state
        app.Services.GetRequiredService<TimedEventService>();
        app.Services.GetRequiredService<PronteraDefenseService>();
        var timedBattles = app.Services.GetRequiredService<TimedBattleService>();
        var casual = app.Services.GetRequiredService<CasualBattleService>();
        app.Services.GetRequiredService<CombatService>().SetEventPvPHandlers(
            (a, b) => timedBattles.CanFight(a, b) || casual.CanFight(a, b),
            async (killer, victim) => { await timedBattles.PlayerKilledAsync(killer, victim); await casual.PlayerKilledAsync(killer, victim); });
        app.Services.GetRequiredService<FestivalService>();
        app.Services.GetRequiredService<TrainingService>();
        app.Services.GetRequiredService<GuildHomeService>();
        app.Services.GetRequiredService<GuildCampfireService>();
        app.Services.GetRequiredService<GuildHuntingService>(); // boss kills, damage, home entry and the 5840 guard
        app.Services.GetRequiredService<GuildChallengeService>();
        app.Services.GetRequiredService<DraculaWeddingService>(); // home scene hook, lair tasks and the !dracula aid
        app.Services.GetRequiredService<AdventureGroupService>(); // creation-instance NPCs, group quest gate, scene hook
        app.Services.GetRequiredService<GuildCaravanService>(); // 05:00 settlement also covers offline orders
        app.Services.GetRequiredService<WildAfkService>(); // field drop accounting and Bonus Time ticker
        app.Services.GetRequiredService<TeamService>(); // party card ticker
        app.Services.GetRequiredService<MvpService>(); // field MVP spawns and timers
        var territory = app.Services.GetRequiredService<TerritoryWarsService>();
        var escort = app.Services.GetRequiredService<TerritoryEscortService>();
        escort.OwnerOf = territory.OwnerOf;
        escort.GuildPoint = territory.GuildPoint;
        territory.GuildEscortScore = (guild, round) => escort.GetGuildEscortScore(guild, round);
        var ranks = app.Services.GetRequiredService<RankService>();
        territory.GloryBoardRank = role => ranks.RankOfAsync(RankService.BattleGlory, role);
        app.Services.GetRequiredService<GuildService>().ItemDataOf = escort.GuildItems;
        var territoryCombat = app.Services.GetRequiredService<CombatService>();
        var league = app.Services.GetRequiredService<GuildLeagueService>(); // 30410-30412 hooks, battlefield scene/combat hooks
        territoryCombat.PlayerHostile = (a, b) => territory.Hostile(a, b) || escort.Hostile(a, b) || territory.WorldHostile(a, b)
            || league.Hostile(a, b);
        territoryCombat.MonsterTargetAllowed = (s, m) => territory.CanHitObjective(s, m) && escort.CanHitCart(s, m) && league.CanHit(s, m);
        app.Services.GetRequiredService<WorldEffectsService>(); // scene/monster effect hooks before clients connect
        app.Services.GetRequiredService<WorldCoreService>();
        app.Services.GetRequiredService<WorldInteractionService>();
        app.Services.GetRequiredService<SkillService>().Learned.Add(app.Services.GetRequiredService<CharacterService>().NotifyCareerCapacityAsync);
        // 副本通關以耐久任務旁表計數；完成回呼仍留給其他系統，不能在重登時重加一次。
        var questTasks = app.Services.GetRequiredService<TaskService>();
        var questDungeons = app.Services.GetRequiredService<DungeonService>();
        questDungeons.CaptureTaskClear = questTasks.CaptureDungeonClear;
        questDungeons.ApplyTaskClear = questTasks.ApplyDungeonClearAsync;
        questDungeons.ResumeTaskClearFollowup = questTasks.ResumeDungeonClearFollowupAsync;
        questDungeons.StoryEntryAllowed = questTasks.StoryDungeonEntryAllowed;
        questDungeons.StoryContinuationAllowed = questTasks.StoryDungeonContinuationAllowed;
        app.Services.GetRequiredService<TotemService>().Combined.Add(questTasks.OnTotemCombined); // target 315004
        // Participation and state targets (TaskService.ParticipationTargets.cs).
        app.Services.GetRequiredService<DungeonService>().MultiBossCleared.Add(questTasks.OnTenBossClearedAsync); // 106602
        app.Services.GetRequiredService<GuildChallengeService>().Participated.Add(questTasks.OnGveJoined); // 103014
        league.MatchCommitted.Add(questTasks.OnGuildLeagueMatch); // 106603, 106019
        territory.SiegeSettled.Add(questTasks.OnSiegeSettled); // 106611, 106605, 106606
        questTasks.BattleMeritPoints = territory.GloryPoints; // 106610
        questTasks.FactionCamp = territory.WorldCampOf; // the Faction Quest (TaskService.FactionQuest.cs)
        questTasks.FactionRivals = territory.WorldRivals; // 101023
        territory.CampChanged.Add(questTasks.OnFactionCampChanged);
        territory.CrystalCollected.Add(questTasks.OnFactionCrystalCollected); // 107102
        questTasks.FactionGuildAward = app.Services.GetRequiredService<GuildCaravanService>().PayFactionAward;
        territory.SiegeSettled.Add((role, _, _) => questTasks.OnGuildEventJoined(role, TaskService.GuildEvent.RoyalCity)); // 106010
        territory.BattleSettled.Add(questTasks.OnTerritoryBattleSettled); // 106010 Stronghold / Regional Capital War
        territory.BattleSettled.Add((role, _) => questTasks.OnGuildWarScored(role)); // 106609
        questTasks.GuildWarPoints = territory.GuildWarPoints; // 106609
        // Dungeon task trackers (TaskService.DungeonTasks.cs): 210005, 200003, 210004, 200000; the siege's 200001, 210003.
        app.Services.GetRequiredService<DungeonService>().RunTracked.Add(questTasks.OnDungeonRun);
        territory.SiegeTracked.Add(questTasks.OnDungeonRun);
        questTasks.DungeonRunValue = territory.SiegeTrackerValue;
        escort.CartStarted.Add(s => questTasks.OnGuildEventJoined(s, TaskService.GuildEvent.Escort)); // 106010 Escort
        app.Services.GetRequiredService<EmblemService>().Upgraded.Add(questTasks.OnEchoUpgraded); // 317002
        app.Services.GetRequiredService<TowerService>().Settled.Add(questTasks.OnTowerSettled); // 101007, 101010
        app.Services.GetRequiredService<PersonalService>().Upvoted.Add(questTasks.OnProfileLiked); // 106902
        app.Services.GetRequiredService<GuildCampfireService>().Joined.Add(questTasks.OnCampfireJoined); // 106018
        app.Services.GetRequiredService<GuildService>().MembershipChanged.Add(questTasks.OnStateChanged); // 104007
        // The remaining targets (TaskService.MiscTargets.cs).
        app.Services.GetRequiredService<TradeService>().Bought.Add(questTasks.OnExchangeBought); // 103002
        app.Services.GetRequiredService<AuctionService>().GuildBid.Add(questTasks.OnGuildAuctionBid); // 103003
        app.Services.GetRequiredService<GuildCampfireService>().Joined.Add(questTasks.OnBonfireJoined); // 103013
        league.MatchCommitted.Add(questTasks.OnGuildLeagueFought); // 103015
        app.Services.GetRequiredService<ChatService>().TextSent.Add(questTasks.OnChatSent); // 103020
        app.Services.GetRequiredService<MountService>().RideChanged.Add(s => { questTasks.OnStateChanged(s); return Task.CompletedTask; }); // 101021
        app.Services.GetRequiredService<GuildService>().MembershipChanged.Add(app.Services.GetRequiredService<GuildVoiceService>().OnMembershipChanged);
        app.Services.GetRequiredService<GuildService>().RightsChanged.Add(app.Services.GetRequiredService<GuildVoiceService>().OnRightsChanged);
        // The Tavern's achievements: 106007 counts the daily quest's deliveries, 106008 reads the Star Power.
        var achievements = app.Services.GetRequiredService<AchievementService>();
        var tavern = app.Services.GetRequiredService<TavernService>();
        questTasks.QuestDelivered.Add((s, task, _) => achievements.OnTaskCompletions(s, task, 1));
        tavern.TavernQuestsAdded.Add((s, count) => achievements.OnTaskCompletions(s, tavern.TaskId(), count));
        tavern.PopularityChanged.Add(achievements.OnStateChanged);
        var social = app.Services.GetRequiredService<Micro.SocialOwnerCache>();
        questTasks.LoadFriendCount = async s => s.Role is { } role
            ? (await social.LoadSocialAsync([role.Id])).GetValueOrDefault(role.Id)?.Friends.LongLength : null; // 103019
        app.Services.GetRequiredService<Micro.FriendService>().RelationChanged.Add(questTasks.OnRelationChanged);
        // Connect quests (TaskService.Connect.cs): party-mates, the raid boss clears and 116's intimacy.
        questTasks.PartyMembers = app.Services.GetRequiredService<TeamService>().MembersOf;
        app.Services.GetRequiredService<DungeonService>().MultiBossCleared.Add(questTasks.OnConnectBossClearedAsync); // 107005
        questTasks.GainIntimacy = app.Services.GetRequiredService<FriendExtraService>().GainIntimacy;
        questTasks.QuestDelivered.Add(questTasks.OnConnectDelivered);
        app.Services.GetRequiredService<ReconnectService>(); // hooks the handover and registers !netdrop
        app.Services.GetRequiredService<CardEffects>(); // socketed-card passives and procs (sockets, scene loads, hits)
        app.Services.GetRequiredService<TalentProcs>(); // profession talent procs (cast resolutions)
        app.Services.GetRequiredService<PassiveSkills>(); // learned passives' procs, shields and reactions
        app.Services.GetRequiredService<RidingEffects>(); // a ridden mount's buffs and the riding passives
        app.Services.GetRequiredService<StealthEffects>(); // hiding and detection attributes, reveals, Ruwach
        // Create the key before the first client connects, so ClientPatch can read the public half.
        app.Services.GetRequiredService<GateCrypto>();
        // Staff roles and maintenance from the database before the gates take a login (the game gate asks both).
        app.Services.GetRequiredService<StaffRoles>().Load();
        app.Services.GetRequiredService<MaintenanceMode>().Load();
        if (options.Panel.Enabled)
        {
            app.Services.GetRequiredService<Ro3Emu.Panel.PanelSite>(); // built now, after every hook above
            string panelUrl = PanelUrl(options);
            LogPanel(app.Logger, panelUrl);
            LogPanelTwoFactor(app.Logger, options.Panel.RequireAdmin2FA == true ? "required" : "not required");
            LogPanelAdmins(app.Logger, options);
        }
        app.Logger.LogInformation("Data dir {DataDir}", options.DataDir);
        LogGmPolicy(app.Logger, options, builder.Environment.EnvironmentName);
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess) LogRunningAsRoot(app.Logger);
        LogTesting(app.Logger, options.Testing);
        if (options.RefuseUnknownAccounts && !(options.Panel.Enabled && options.Panel.Registration)) LogNoSignUp(app.Logger);
        // D1 inventory across many short-lived servers (a whole test suite): each appends its loop waits on stop.
        if (Environment.GetEnvironmentVariable("RO3EMU_LOOPWAIT_DUMP") is { Length: > 0 } dump)
            app.Lifetime.ApplicationStopped.Register(() => Diagnostics.LoopWaitDump.Append(dump, app.Services.GetRequiredService<AccountStore>().AccessMetrics));
        return app;
    }

    /// <summary>The options a server started with these arguments would use (offline tools such as --restore-backup).</summary>
    public static ServerOptions ResolveOptions(string[] args) =>
        Resolve(WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory }).Configuration, null);

    private static ServerOptions Resolve(IConfiguration configuration, Action<ServerOptions>? configure)
    {
        var options = configuration.GetSection("Ro3Emu").Get<ServerOptions>() ?? new ServerOptions();
        configure?.Invoke(options);
        options.RewardRates.Validate();
        options.SkillTraceTelemetry.Validate();
        // 空白 GM 名單只在明確非 Production 且僅能從本機存取時開放。
        options.GmOpenWhenEmpty ??= !ExplicitProduction(configuration) && options.LocalOnly();
        // A relative data dir is relative to the repository root (the folder holding Ro3Emu.slnx), else to the binary.
        if (!Path.IsPathRooted(options.DataDir)) options.DataDir = Path.GetFullPath(Path.Combine(RepositoryRoot(), options.DataDir));
        return options;
    }

    /// <summary>The panel's listen URL: its own bind address, else the server's; a port the server already uses is refused.</summary>
    private static string PanelUrl(ServerOptions options)
    {
        var panel = options.Panel;
        if (panel.Port is <= 0 or > 65535 || panel.Port == options.HttpPort || panel.Port == options.MicroGatePort
            || panel.Port == options.GameGatePort || panel.Port == options.Voice.Port)
            throw new InvalidOperationException($"Ro3Emu:Panel:Port {panel.Port} must be a free port of its own (docs/guide/panel.md).");
        var address = PanelAddress(options);
        string host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return $"http://{host}:{panel.Port}";
    }

    private static System.Net.IPAddress PanelAddress(ServerOptions options) => options.PanelListenAddress();

    [LoggerMessage(Level = LogLevel.Information, Message = "Web panel on {Url}")]
    private static partial void LogPanel(ILogger logger, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Web panel: two-factor authentication for GM and Admin accounts {State} (Ro3Emu:Panel:RequireAdmin2FA)")]
    private static partial void LogPanelTwoFactor(ILogger logger, string state);

    /// <summary>ASPNETCORE_ENVIRONMENT, DOTNET_ENVIRONMENT or --environment set to Production (not the host default).</summary>
    internal static bool ExplicitProduction(IConfiguration configuration) =>
        string.Equals(configuration[HostDefaults.EnvironmentKey], Environments.Production, StringComparison.OrdinalIgnoreCase);

    /// <summary>Says at startup who has GM in game, loudly when it is everyone on a public address or nobody.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "!!! The server is running as root: a hole in it would own the whole machine. Run it as an ordinary user (docs/guide/hosting.md).")]
    private static partial void LogRunningAsRoot(ILogger logger);

    private static void LogGmPolicy(ILogger logger, ServerOptions options, string environment)
    {
        if (options.GmAccounts.Length > 0)
        {
            logger.LogInformation("GM accounts: {Accounts}", string.Join(", ", options.GmAccounts));
            return;
        }
        bool local = options.LocalOnly();
        string where = local ? "local only" : $"reachable from other machines: listens on {options.ListenAddress()}, public host {options.PublicHost}";
        if (options.GmOpenWhenEmpty == false)
        {
            logger.LogWarning("!!! Ro3Emu:GmAccounts is empty, so NO account has GM commands in game " +
                "(environment {Environment}, {Where}): an empty list means everyone only on a local server. " +
                "Set Ro3Emu__GmAccounts__0=<account> (docs/guide/gm-access.md). The loopback /ro3emu/gm tool " +
                "still works.", environment, where);
            return;
        }
        // Open on a server others reach only by an explicit Ro3Emu:GmOpenWhenEmpty. A plain local start has no
        // environment set, and the host then calls itself Production, so the name alone says nothing.
        if (!local)
            logger.LogWarning("!!! Ro3Emu:GmAccounts is empty and GM is open: EVERY account can use GM commands in game " +
                "(environment {Environment}, {Where}, Ro3Emu:GmOpenWhenEmpty set). For a public server list the GM " +
                "accounts: Ro3Emu__GmAccounts__0=<account> (docs/guide/gm-access.md).", environment, where);
        else
            logger.LogInformation("GM: every account (Ro3Emu:GmAccounts is empty; local or test server)");
    }

    /// <summary>Says at startup who is a configured Admin of the web panel, loudly when nobody is.</summary>
    private static void LogPanelAdmins(ILogger logger, ServerOptions options)
    {
        var admins = options.GmAccounts.Concat(options.Panel.Roles
            .Where(r => Ro3Emu.Panel.PanelRoles.Parse(r.Value) == Ro3Emu.Panel.PanelRole.Admin).Select(r => r.Key))
            .Distinct(StringComparer.Ordinal).ToList();
        if (admins.Count > 0) LogPanelAdminList(logger, admins);
        else if (StaffRoles.OpenGmAdmin(options)) LogPanelAdminsOpen(logger);
        else LogPanelNoAdmin(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Web panel Admins: {Accounts}")]
    private static partial void LogPanelAdminList(ILogger logger, IReadOnlyList<string> accounts);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Web panel Admins: every account (GmAccounts is empty and GM is open on a local panel)")]
    private static partial void LogPanelAdminsOpen(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "!!! Web panel: no Admin in the configuration. Set " +
        "Ro3Emu__GmAccounts__0=<account> (docs/guide/panel.md); Admins given on the panel's Staff roles page keep " +
        "their role.")]
    private static partial void LogPanelNoAdmin(ILogger logger);

    /// <summary>Ro3Emu:Testing switches are test aids, never the original game: say so loudly while one is on.</summary>
    private static void LogTesting(ILogger logger, TestingOptions testing)
    {
        if (testing.AlwaysOpen) LogAlwaysOpen(logger);
        if (testing.UnlimitedRewards) LogUnlimitedRewards(logger);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "!!! Ro3Emu:Testing:AlwaysOpen is ON: every To-Do activity is open at " +
        "any hour, its occurrence repeating all day. TEST AID, not original behaviour; turn it off for a real realm " +
        "(docs/guide/hosting.md).")]
    private static partial void LogAlwaysOpen(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "!!! Ro3Emu:Testing:UnlimitedRewards is ON: dungeon, MVP, Commendation " +
        "Vault and Dodo's Request reward counts never run out. TEST AID, not original behaviour; turn it off for a real realm " +
        "(docs/guide/hosting.md).")]
    private static partial void LogUnlimitedRewards(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "!!! Ro3Emu:RefuseUnknownAccounts is on but the web panel's sign-up is " +
        "off: no new account can be created. Turn on Ro3Emu:Panel:Enabled and Ro3Emu:Panel:Registration (docs/guide/hosting.md).")]
    private static partial void LogNoSignUp(ILogger logger);

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        }
        return AppContext.BaseDirectory;
    }
}

/// <summary>
/// Adds the loop's fixed tickers, in the order it ticks them, just before the loop starts. Each used to add itself
/// from its constructor; resolving them any earlier would change the order the services subscribe to each other's
/// events. Map instances and the visibility rebalancer join later as the world opens maps.
/// </summary>
internal sealed class GameLoopTickers(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var loop = services.GetRequiredService<GameLoop>();
        void Add<T>() where T : IGameTicker => loop.Add(services.GetRequiredService<T>());
        Add<Deadlines>();
        Add<MonsterService>();
        Add<BuffService>();
        Add<CombatService>();
        Add<RewardService>(); // retries durable field-party EXP shares
        Add<SeasonService>();
        Add<MountService>();
        Add<RidingEffects>();
        Add<StealthEffects>();
        Add<PassiveSkills>();
        Add<TeamService>();
        Add<DailyActivityService>();
        Add<TimedEventService>();
        Add<PronteraDefenseService>();
        Add<TimedBattleService>();
        Add<CasualBattleService>();
        Add<FestivalService>();
        Add<RankRewardService>();
        Add<TrainingService>();
        Add<GuildHomeService>();
        Add<GuildCampfireService>();
        Add<GuildHuntingService>();
        Add<GuildChallengeService>();
        Add<DraculaWeddingService>();
        Add<AdventureGroupService>();
        Add<GuildCaravanService>();
        Add<WildAfkService>();
        Add<MvpService>();
        Add<TerritoryWarsService>();
        Add<TerritoryEscortService>();
        Add<GuildLeagueService>();
        Add<WorldCoreService>();
        Add<EmojiService>();
        Add<AchievementService>();
        Add<FirstHeartService>();
        Add<DungeonMatchService>();
        Add<GatheringService>();
        Add<TotemEffects>();
        Add<Game.Persistence.PositionPersistence>();
        Add<Game.Persistence.SettingsPersistence>();
        Add<Game.Persistence.WildAfkPersistence>();
        Add<Diagnostics.SessionCensus>();
        Add<ShopService>(); // 05:00 purchase-limit rollover
        Add<ItemBoxService>(); // award boxes; 05:00 item use-time rollover
        Add<PowerService>(); // throttled 31502/31503 republish of changed power
        Add<Voice.VoiceRooms>(); // VAD silence timeout every tick, team-membership sweep every second
        Add<GuildVoiceService>(); // guild voice channels: drops the offline and those no longer in the guild, every second
        Add<TavernService>(); // Tavern rooms: time in a room, offline members, mutes and tags, every second
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class GameLoopQuiesceService(GameLoop loop) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) { loop.Quiesce(); return Task.CompletedTask; }
}

/// <summary>Tracks the open HTTP connections so a stop that Kestrel is still draining can abort them after a short grace.</summary>
internal sealed class HttpConnectionReaper
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Microsoft.AspNetCore.Connections.ConnectionContext, byte> _open = new();

    public async Task Track(Microsoft.AspNetCore.Connections.ConnectionContext connection, Func<Task> next)
    {
        _open[connection] = 0;
        try { await next(); }
        finally { _open.TryRemove(connection, out _); }
    }

    public void AbortAfter(TimeSpan grace) => _ = Task.Delay(grace).ContinueWith(_ =>
    {
        foreach (var connection in _open.Keys) connection.Abort();
    }, TaskScheduler.Default);
}
