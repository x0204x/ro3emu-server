using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Http;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Micro;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Server;

/// <summary>Builds the whole emulator (HTTP + both gates); used by Program and by end-to-end tests.</summary>
public static class ServerHost
{
    public static WebApplication Create(string[] args, Action<ServerOptions>? configure = null, TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        var options = Resolve(builder.Configuration, configure);
        // Fail fast, before any service is built, if an offline restore of this data dir did not finish (C/D 19).
        DatabaseRestore.ThrowIfUnfinished(options.DataDir);
        // Stage G: the default host on Windows also writes Warning and above to the Windows Event Log, synchronously on
        // the logging thread - the game loop included (a quarter of a walk's cost at 500 players). The console logger
        // queues to a background writer; the metrics provider registered below stays.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        // One server per data dir (C/D 19): taken before anything opens the database, released when the app is disposed.
        builder.Services.AddSingleton(_ => RealmLock.Acquire(options.DataDir));
        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton(clock ?? TimeProvider.System);
        builder.WebHost.UseUrls($"http://{options.ListenAddress()}:{options.HttpPort}"); // loopback for local play and tests

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
        builder.Services.AddSingleton<ActivityClock>();
        builder.Services.AddSingleton<NewActivityService>();
        builder.Services.AddSingleton<TimedEventService>();
        builder.Services.AddSingleton<TimedTodoSchedule>();
        builder.Services.AddSingleton<PronteraDefenseService>();
        builder.Services.AddSingleton<TimedBattleService>();
        builder.Services.AddSingleton<DungeonMatchService>();
        builder.Services.AddSingleton<FestivalService>();
        builder.Services.AddSingleton<TrainingService>();
        builder.Services.AddSingleton<LotteryService>();
        builder.Services.AddSingleton<GuildHomeService>();
        builder.Services.AddSingleton<GuildCampfireService>();
        builder.Services.AddSingleton<GuildHuntingService>();
        builder.Services.AddSingleton<GuildChallengeStore>();
        builder.Services.AddSingleton<GuildChallengeService>();
        builder.Services.AddSingleton<DraculaWeddingService>();
        builder.Services.AddSingleton<WildAfkService>();
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
        builder.Services.AddSingleton<FeedbackService>();
        builder.Services.AddSingleton<GmCommands>();
        builder.Services.AddSingleton<Ro3Emu.Server.Data.GameData>();
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<GateAuthRegistry>();
        builder.Services.AddSingleton<ReconnectService>();
        builder.Services.AddSingleton<Voice.VoiceTicketRegistry>();
        builder.Services.AddSingleton<Voice.VoiceRooms>();
        // The voice relay PoC only opens a second listener when a port is configured for it.
        if (options.Voice.Port > 0) builder.Services.AddHostedService<Voice.VoiceGateServer>();
        builder.Services.AddHostedService<DatabaseBackup>();
        builder.Services.AddSingleton<Game.Persistence.PositionPersistence>();
        builder.Services.AddSingleton<Game.Persistence.SettingsPersistence>();
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
        builder.Services.AddSingleton<Game.Messaging.IMessageModule>(sp => sp.GetRequiredService<GuildVoiceService>());
        builder.Services.AddSingleton<GameMessageHandler>();
        builder.Services.AddSingleton(sp => GateCrypto.LoadOrCreate(options.DataDir, sp.GetRequiredService<ILogger<GateCrypto>>()));
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

        var app = builder.Build();
        app.Services.GetRequiredService<RealmLock>();
        app.Services.GetRequiredService<Game.Ownership.RoleHandover>()
            .Configure(session => app.Services.GetRequiredService<RoleSessionService>().CleanupRoleAsync(session),
                session => app.Services.GetRequiredService<RoleSessionService>().CleanupReservation(session));
        app.Services.GetRequiredService<Diagnostics.ServerMetrics>().Owners = app.Services.GetRequiredService<Game.Ownership.OwnerRegistry>();
        // Mount energy meters (EnergyConfig._iBelongMount): BuffService cannot take MountService (MountService -> SceneService -> BuffService).
        app.Services.GetRequiredService<MountService>().Changed.Add(app.Services.GetRequiredService<BuffService>().MountsChangedAsync);
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) && app.Services.GetRequiredService<GameLoop>().Phase != GameLoopPhase.Accepting)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            app.Logger.LogInformation("HTTP {Method} {Path}{Query}", context.Request.Method, context.Request.Path, context.Request.QueryString);
            await next();
        });
        SdkEndpoints.Map(app);
        Diagnostics.MetricsEndpoints.Map(app);
        // Run a GM command as an online player (loopback only): POST /ro3emu/gm?account=tester body "!warp 10111 150 190".
        app.MapPost("/ro3emu/gm", async (HttpContext context, SessionRegistry sessions, GmCommands gm, GameLoop loop) =>
        {
            if (context.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip)) return Results.NotFound();
            var account = context.Request.Query["account"].ToString();
            using var reader = new StreamReader(context.Request.Body);
            var line = (await reader.ReadToEndAsync()).Trim();
            if (!line.StartsWith('!')) line = "!" + line;
            return await loop.InvokeAsync(async () =>
            {
                var session = sessions.All().FirstOrDefault(s => string.Equals(s.Account, account, StringComparison.OrdinalIgnoreCase));
                if (session?.Role == null) return Results.BadRequest("not in game");
                await gm.RunAsync(session, line, loopbackTool: true);
                return Results.Ok("done");
            });
        });
        // In-game bug reports (loopback only, docs/guide/feedback.md): GET /ro3emu/reports?since=<id or ISO time>&limit=&format=text
        app.MapGet("/ro3emu/reports", (HttpContext context, FeedbackService feedback) =>
        {
            if (context.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip)) return Results.NotFound();
            var query = context.Request.Query;
            long afterId = 0;
            DateTimeOffset? since = null;
            string sinceText = query["since"].ToString().Trim();
            if (sinceText.Length > 0)
            {
                if (long.TryParse(sinceText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id)) afterId = id;
                else if (DateTimeOffset.TryParse(sinceText, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var time)) since = time;
                else return Results.BadRequest("since: a report id or an ISO-8601 time");
            }
            int limit = int.TryParse(query["limit"], out int l) ? Math.Clamp(l, 1, 1000) : 200;
            var reports = feedback.Store.Read(afterId, since, limit);
            return string.Equals(query["format"], "text", StringComparison.OrdinalIgnoreCase)
                ? Results.Text(FeedbackStore.Format(reports), "text/plain; charset=utf-8")
                : Results.Json(reports, FeedbackStore.Json);
        });
        // Character deletion for the launcher / website (the client itself has none): POST form identity, credential,
        // role (id or name). A role that is in the world cannot be deleted.
        app.MapPost("/ro3emu/account/delete-role", async (HttpRequest request, CredentialLimiter limiter, AccountStore accounts, RoleStore roles, SessionRegistry sessions, GameLoop loop, Game.Ownership.OwnerRegistry owners, ILogger<RoleStore> log) =>
        {
            if (!limiter.TryAcquire(request.HttpContext.Connection.RemoteIpAddress)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var form = await request.ReadFormAsync();
            string typed = form["identity"].ToString().Trim(), credential = form["credential"].ToString(), which = form["role"].ToString().Trim();
            // Roles are keyed by the account's stored name, whatever letter case the form used.
            if (await accounts.VerifiedAccountAsync(typed, credential) is not { } identity)
                return Results.Json(new { code = 401, message = "bad account or password" });
            var role = roles.ForAccount(identity).FirstOrDefault(r => r.Id.ToString() == which || string.Equals(r.Name, which, StringComparison.OrdinalIgnoreCase));
            if (role == null) return Results.Json(new { code = 404, message = "no such character on this account" });
            return await loop.InvokeAsync(async () =>
            {
                using var lifecycle = await owners.EnterAsync("HTTP delete", Game.Ownership.ReservationKey.Lifecycle(role.Id));
                var current = roles.Get(role.Id);
                if (current == null || current.Account != identity)
                    return Results.Json(new { code = 404, message = "no such character on this account" });
                var owner = owners.Cached(role.Id);
                bool attached = owner is { State: not Game.Ownership.RoleOwnerState.Detached } ||
                    owner?.Attachment != null || owner?.Detaching != null ||
                    sessions.All().Any(s => s.Role?.Id == role.Id || s.Owner?.RoleId == role.Id || s.Attachment?.RoleId == role.Id);
                if (attached)
                    return Results.Json(new { code = 409, message = "the character is in the game; log out first" });
                bool ok = roles.Delete(identity, role.Id);
                if (ok) owners.RoleDeleted(role.Id, roles.ReadRevisionVector(role.Id).Epoch);
                log.LogInformation("Account {Account} deletes role {Name} ({Id}): {Result}", identity, current.Name, role.Id, ok);
                return Results.Json(new { code = ok ? 0 : 500, message = ok ? $"{current.Name} deleted" : "not deleted" });
            });
        });
        // Public status for the launcher's online pill: only a count, never account or character names. Read from the
        // loop's census, so anyone polling it adds no work to the loop.
        app.MapGet("/ro3emu/status", (Diagnostics.SessionCensus census) =>
            Results.Json(new { online = true, players = census.Current.Roles.Count }));
        // A clean stop for RO3Emu.exe (loopback only): the same shutdown as Ctrl+C, the loop drains and the database closes.
        app.MapPost("/ro3emu/shutdown", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            if (context.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip)) return Results.NotFound();
            lifetime.StopApplication();
            return Results.Ok("stopping");
        });
        // Who is online, for the admin (loopback only): read on the loop, where the sessions change.
        app.MapGet("/ro3emu/online", async (HttpContext context, SessionRegistry sessions, GameLoop loop) =>
        {
            if (context.Connection.RemoteIpAddress is not { } ip || !System.Net.IPAddress.IsLoopback(ip)) return Results.NotFound();
            var online = await loop.InvokeAsync(() => Task.FromResult(sessions.All().Select(s => new
            {
                account = s.Account,
                role = s.Role?.Name,
                scene = s.Role == null ? (long?)null : s.SceneEntry,
                x = s.Position.X,
                z = s.Position.Z,
                remote = s.Channel.Remote,
            }).ToArray()));
            return Results.Json(online);
        });
        app.MapFallback((HttpContext context) =>
        {
            app.Logger.LogWarning("HTTP unhandled {Method} {Path}", context.Request.Method, context.Request.Path);
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
        // A committed dungeon clear counts toward quest target 101018 (story steps such as Payon Forest, the airship).
        var questTasks = app.Services.GetRequiredService<TaskService>();
        app.Services.GetRequiredService<DungeonService>().Completed.Add((s, scene, _) => questTasks.OnDungeonClearedAsync(s, scene));
        app.Services.GetRequiredService<TotemService>().Combined.Add(questTasks.OnTotemCombined); // target 315004
        // Participation and state targets (TaskService.ParticipationTargets.cs).
        app.Services.GetRequiredService<DungeonService>().MultiBossCleared.Add(questTasks.OnTenBossClearedAsync); // 106602
        app.Services.GetRequiredService<GuildChallengeService>().Participated.Add(questTasks.OnGveJoined); // 103014
        league.MatchCommitted.Add(questTasks.OnGuildLeagueMatch); // 106603, 106019
        territory.SiegeSettled.Add(questTasks.OnSiegeSettled); // 106611, 106605, 106606
        app.Services.GetRequiredService<EmblemService>().Upgraded.Add(questTasks.OnEchoUpgraded); // 317002
        app.Services.GetRequiredService<TowerService>().Settled.Add(questTasks.OnTowerSettled); // 101007, 101010
        app.Services.GetRequiredService<PersonalService>().Upvoted.Add(questTasks.OnProfileLiked); // 106902
        app.Services.GetRequiredService<GuildCampfireService>().Joined.Add(questTasks.OnCampfireJoined); // 106018
        app.Services.GetRequiredService<GuildService>().MembershipChanged.Add(questTasks.OnStateChanged); // 104007
        app.Services.GetRequiredService<GuildService>().MembershipChanged.Add(app.Services.GetRequiredService<GuildVoiceService>().OnMembershipChanged);
        app.Services.GetRequiredService<GuildService>().RightsChanged.Add(app.Services.GetRequiredService<GuildVoiceService>().OnRightsChanged);
        var social = app.Services.GetRequiredService<Micro.SocialOwnerCache>();
        questTasks.LoadFriendCount = async s => s.Role is { } role
            ? (await social.LoadSocialAsync([role.Id])).GetValueOrDefault(role.Id)?.Friends.LongLength : null; // 103019
        app.Services.GetRequiredService<Micro.FriendService>().RelationChanged.Add(questTasks.OnRelationChanged);
        app.Services.GetRequiredService<ReconnectService>(); // hooks the handover and registers !netdrop
        app.Services.GetRequiredService<CardEffects>(); // socketed-card passives and procs (sockets, scene loads, hits)
        // Create the key before the first client connects, so ClientPatch can read the public half.
        app.Services.GetRequiredService<GateCrypto>();
        app.Logger.LogInformation("Data dir {DataDir}", options.DataDir);
        LogGmPolicy(app.Logger, options, builder.Environment.EnvironmentName);
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
        // An empty GM list means nobody on a host started as Production on purpose (docs/guide/gm-access.md). Only an explicit
        // environment counts: the host's own default is also "Production", and tests and local play set none.
        options.GmOpenWhenEmpty ??= !ExplicitProduction(configuration);
        // A relative data dir is relative to the repository root (the folder holding Ro3Emu.slnx), else to the binary.
        if (!Path.IsPathRooted(options.DataDir)) options.DataDir = Path.GetFullPath(Path.Combine(RepositoryRoot(), options.DataDir));
        return options;
    }

    /// <summary>ASPNETCORE_ENVIRONMENT, DOTNET_ENVIRONMENT or --environment set to Production (not the host default).</summary>
    internal static bool ExplicitProduction(IConfiguration configuration) =>
        string.Equals(configuration[HostDefaults.EnvironmentKey], Environments.Production, StringComparison.OrdinalIgnoreCase);

    /// <summary>Says at startup who has GM in game, loudly when it is everyone on a public address or nobody.</summary>
    private static void LogGmPolicy(ILogger logger, ServerOptions options, string environment)
    {
        if (options.GmAccounts.Length > 0)
        {
            logger.LogInformation("GM accounts: {Accounts}", string.Join(", ", options.GmAccounts));
            return;
        }
        if (options.GmOpenWhenEmpty == false)
        {
            logger.LogWarning("!!! {Environment}: Ro3Emu:GmAccounts is empty, so NO account has GM commands in game. " +
                "Set Ro3Emu__GmAccounts__0=<account> (docs/guide/gm-access.md). The loopback /ro3emu/gm tool still works.", environment);
            return;
        }
        bool publicHost = !System.Net.IPAddress.TryParse(options.PublicHost, out var host) || !System.Net.IPAddress.IsLoopback(host);
        if (publicHost || string.Equals(environment, Environments.Production, StringComparison.OrdinalIgnoreCase) && options.GmOpenWhenEmpty == true)
            logger.LogWarning("!!! Ro3Emu:GmAccounts is empty and GM is open: EVERY account can use GM commands in game " +
                "(environment {Environment}, public host {PublicHost}). For a public server set ASPNETCORE_ENVIRONMENT=Production " +
                "and Ro3Emu__GmAccounts__0=<account> (docs/guide/gm-access.md).", environment, options.PublicHost);
        else
            logger.LogInformation("GM: every account (Ro3Emu:GmAccounts is empty; local or test server)");
    }

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
        Add<SeasonService>();
        Add<MountService>();
        Add<TeamService>();
        Add<DailyActivityService>();
        Add<TimedEventService>();
        Add<PronteraDefenseService>();
        Add<TimedBattleService>();
        Add<CasualBattleService>();
        Add<FestivalService>();
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
        Add<Diagnostics.SessionCensus>();
        Add<ShopService>(); // 05:00 purchase-limit rollover
        Add<ItemBoxService>(); // award boxes; 05:00 item use-time rollover
        Add<PowerService>(); // throttled 31502/31503 republish of changed power
        Add<Voice.VoiceRooms>(); // VAD silence timeout every tick, team-membership sweep every second
        Add<GuildVoiceService>(); // guild voice channels: drops the offline and those no longer in the guild, every second
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class GameLoopQuiesceService(GameLoop loop) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) { loop.Quiesce(); return Task.CompletedTask; }
}
