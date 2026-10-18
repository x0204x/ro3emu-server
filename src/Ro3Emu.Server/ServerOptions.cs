namespace Ro3Emu.Server;

public sealed class ServerOptions
{
    /// <summary>Address the client is told to connect to (server list gate addresses). Must be an IP literal.</summary>
    public string PublicHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// Interface the HTTP and gate ports listen on. Empty: loopback when <see cref="PublicHost"/> is a loopback
    /// address (local play and the tests: no Windows Firewall prompt per test host), otherwise every interface.
    /// </summary>
    public string? BindAddress { get; set; }

    public System.Net.IPAddress ListenAddress() =>
        BindAddress is { Length: > 0 } bind ? System.Net.IPAddress.Parse(bind)
        : System.Net.IPAddress.TryParse(PublicHost, out var host) && System.Net.IPAddress.IsLoopback(host) ? System.Net.IPAddress.Loopback
        : System.Net.IPAddress.Any;

    /// <summary>The voice relay serves only with a port set, and off loopback only when plaintext was allowed on purpose.</summary>
    public bool VoiceServed() =>
        Voice.Port > 0 && (System.Net.IPAddress.IsLoopback(ListenAddress()) || Voice.AllowPlaintextOffLoopback);

    public int HttpPort { get; set; } = 5380;

    public int MicroGatePort { get; set; } = 20000;

    public int GameGatePort { get; set; } = 33100;

    public string DataDir { get; set; } = "data";

    /// <summary>Story dungeons whose encounter scripts are not in the client use the reconstructed waves in
    /// Reconstructed/dungeon-monsters.json (inferred, marked "odtworzone"); off refuses them as before.</summary>
    public bool ReconstructedDungeons { get; set; } = true;

    /// <summary>Game-message JSON log (data/logs/traffic-*.jsonl): "off", "on" or a comma-separated account list.
    /// Formatting and writes run off the game loop; keep it off for load tests and production.</summary>
    public string TrafficLog { get; set; } = "off";

    /// <summary>Parse all client config tables before the gates open (tools/run-server.ps1 turns it on). Off by
    /// default so the hundreds of short-lived test hosts load only the tables they touch.</summary>
    public bool PreloadGameData { get; set; }

    /// <summary>
    /// Folder with the data exported from the client: config/ (tools/ConfigExport), sceneeditor/ and nav/
    /// (tools/WorldData); default DataDir.
    /// </summary>
    public string? GameDataDir { get; set; }

    /// <summary>Walk check against the client's nav grid: "off", "log" (report only) or "enforce" (snap back).</summary>
    public string WalkCheck { get; set; } = "log";

    public int ClusterId { get; set; } = 1;

    public string ClusterName { get; set; } = "RO3Emu";

    /// <summary>The server (area) inside the cluster: the login screen shows the cluster name above it.</summary>
    public string AreaName { get; set; } = "Prontera";

    /// <summary>
    /// Stall title limit in characters. The original's PublicParamConfig.StallsNameCharacterLimit (10) was sized for
    /// Chinese; English titles need more. ClientHooks.LuaPatches sets the client's input limit to the same value.
    /// </summary>
    public int StallNameMaxLength { get; set; } = 24;

    /// <summary>
    /// The live season playbook (SeasonService). 101 is the only one this client has season content for; a value
    /// set in game with "!season" overrides it.
    /// </summary>
    public int PlayBook { get; set; } = 101;

    /// <summary>The season's first day, yyyy-MM-dd; empty means the server's first start (kept in the database).</summary>
    public string? SeasonStart { get; set; }

    /// <summary>
    /// Owner policy (2026-10-11): every piece of content the client can show is playable. Off by default so the tests
    /// keep pinning the original's rules; appsettings.json turns it on for the real server.
    /// </summary>
    public OpenContentOptions OpenContent { get; set; } = new();

    /// <summary>Battle pass policy: the premium track is free for now.</summary>
    public PassOptions Pass { get; set; } = new();

    /// <summary>
    /// Accounts allowed to use GM commands in game ("!" lines in chat, the client's GM panel, 1031/1034). Empty:
    /// <see cref="GmOpenWhenEmpty"/> decides. docs/guide/gm-access.md.
    /// </summary>
    public string[] GmAccounts { get; set; } = [];

    /// <summary>
    /// What an empty <see cref="GmAccounts"/> means in game: true = every account has GM (research server, tests,
    /// local play), false = nobody has GM. Left unset, ServerHost decides from the host environment: false when it was
    /// set to Production explicitly (ASPNETCORE_ENVIRONMENT, DOTNET_ENVIRONMENT or --environment), true otherwise. The
    /// loopback HTTP POST /ro3emu/gm (the operator's tools) keeps the old rule either way: empty = every account.
    /// </summary>
    public bool? GmOpenWhenEmpty { get; set; }

    /// <summary>In-game bug reports (FeedbackService, docs/guide/feedback.md).</summary>
    public FeedbackOptions Feedback { get; set; } = new();

    /// <summary>Team voice relay proof of concept (Voice/, docs TBD). Off until a port is set.</summary>
    public VoiceOptions Voice { get; set; } = new();

    /// <summary>(inferred) Locally configured text blocklist; the original service's sensitive-word dictionary is unavailable.</summary>
    public string[] GuildBlockedWords { get; set; } = [];

    /// <summary>
    /// "instant": a guild is Normal at once unless the founder ticks the create widget's responder toggle (this server's
    /// shortcut). "responders": every guild is founded through GuildResponsePeopleLimit responders, as the original;
    /// the toggle then only waives the founder's approval of each response.
    /// </summary>
    public string GuildFounding { get; set; } = "instant";

    /// <summary>
    /// Guild League registration's membership threshold. Unset: GuildSettingConfig GuildLeaguePeopleNum (40, read as the
    /// "Guild membership reaches ${1}" requirement, inferred). A small realm or a test can lower it.
    /// </summary>
    public int? GuildLeagueMinMembers { get; set; }

    /// <summary>
    /// Seconds an in-game role stays in the world after its game socket drops, waiting for the client's silent
    /// reconnect (Phonest ConnectServerReq ReConnection). 60 is the reference
    /// AVATAR_LOSE_CONNECTION_TIMEOUT_SECONDS; it must exceed the client's 20 s + 10 s budgets. 0 turns grace off.
    /// </summary>
    public int ReconnectGraceSeconds { get; set; } = 60;

    /// <summary>
    /// TEST AID, not original behaviour: Survivor Battle (match entry 2) starts with this many entrants in teams of
    /// <see cref="ChickenTestTeamSize"/> instead of EatChickenMatchConfig's 100 in teams of 5 (the client gives no sign of
    /// server-side filler bots). 0 keeps the config. GM "!chicken shape" sets the same in memory.
    /// </summary>
    public int ChickenTestPlayers { get; set; }
    public int ChickenTestTeamSize { get; set; }
}

/// <summary>In-game bug reports: the menu's Feedback button and "/bug" in chat (FeedbackService).</summary>
public sealed class FeedbackOptions
{
    /// <summary>Longest report text kept (characters); longer text is cut and marked truncated.</summary>
    public int MaxLength { get; set; } = 1000;

    /// <summary>Server log lines about the player stored with a report (RecentLogBuffer); 0 keeps none and leaves the
    /// buffer out of the logging pipeline.</summary>
    public int ContextLines { get; set; } = 40;

    /// <summary>How far back those log lines may reach.</summary>
    public int ContextMinutes { get; set; } = 15;

    /// <summary>Minimum seconds between two reports of one account.</summary>
    public int CooldownSeconds { get; set; } = 5;
}

/// <summary>
/// Team voice relay (Talk/Listen/Mute in the party panel). The listener only opens when <see cref="Port"/> is set;
/// an off relay still answers the 77015 ticket request with port 0 so the client's menu can disable itself cleanly.
/// </summary>
public sealed class VoiceOptions
{
    /// <summary>TCP port the voice gate listens on; 0 keeps the relay off.</summary>
    public int Port { get; set; }

    /// <summary>Members a single voice room (one party/team) may hold.</summary>
    public int MaxRoomSize { get; set; } = 40;

    /// <summary>Audio frames a connection may send per second before the token bucket drops the extra ones.</summary>
    public int FramesPerSecond { get; set; } = 50;

    /// <summary>Extra frames the token bucket allows in a short burst on top of <see cref="FramesPerSecond"/>.</summary>
    public int Burst { get; set; } = 10;

    /// <summary>Longest opus payload accepted in one Audio frame, in bytes.</summary>
    public int MaxPayload { get; set; } = 512;

    /// <summary>How long a 77015 ticket stays valid before a connection must present it.</summary>
    public int TicketSeconds { get; set; } = 60;

    /// <summary>Voice connections the listener accepts at once; past this new connections are refused.</summary>
    public int MaxConnections { get; set; } = 1024;

    /// <summary>Voice connections one IP address may hold at once (a LAN party behind one router needs a few).</summary>
    public int MaxConnectionsPerIp { get; set; } = 8;

    /// <summary>Connections from one IP that end without a valid Auth before that IP is refused for a minute.</summary>
    public int MaxFailedAuthPerIp { get; set; } = 10;

    /// <summary>
    /// The voice stream is not encrypted yet (only the ticket travels inside the encrypted game gate), so the relay
    /// refuses to listen on anything but loopback unless this is set on purpose.
    /// </summary>
    public bool AllowPlaintextOffLoopback { get; set; }

    /// <summary>Seconds a fresh connection has to send its Auth frame before it is closed. A test host lowers this.</summary>
    public int FirstFrameTimeoutSeconds { get; set; } = 5;

    /// <summary>Seconds without any frame before an authed connection is closed as idle. A test host lowers this.</summary>
    public int IdleTimeoutSeconds { get; set; } = 30;
}

/// <summary>Both off by default so the tests keep the original's pass; a realm turns them on (docs/systems/activities-battlepass-monthcard.md).</summary>
public sealed class PassOptions
{
    /// <summary>Every role gets the premium track and its privileges without the premium item 703.</summary>
    public bool FreePremium { get; set; }

    /// <summary>Pass EXP from play: challenges, daily activity, monsters, dungeons, MVPs and story (NewActivityService.PassPlay).</summary>
    public bool Play { get; set; }
}

/// <summary>
/// "Everything open" ("Policy: all content open"). The playbook stays 101: it is the only one
/// with its own content in this client, so seasons need no switch.
/// </summary>
public sealed class OpenContentOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Functions the original never opened (base level 999/9999) whose client UI and data work: 7201 Realm War map,
    /// 70122 Totem Shop, 41 Feedback, 1000000 Adv. Guild (AdventureGroupService), 96 Valhalla Trials (ValhallaService)
    /// (Feedback's panel comes from Ro3Emu.ClientHooks.FeedbackButton, its backend is FeedbackService). Shells without a working panel (Title 14, Academy 34...) stay closed so the menu shows no dead
    /// buttons; a function joins this list together with its backend. Novice Challenge (90, activity 112) is left out
    /// (owner, 2026-10-11): its 15 random event texts are "Event Text..."/"Option A" filler and the first node's
    /// dialogues 9901-9904 do not exist, so the original never finished it.
    /// </summary>
    public long[] Functions { get; set; } = [7201, 70122, 41, 1000000, 96];

    /// <summary>
    /// NewActivityConfig rows whose own open condition is a level the cap never reaches (e.g. 112 Novice Challenge).
    /// 200 Pass I asks for base level 99 (condition 21000019). Pass II (201, the same condition and EXP currency) opens
    /// after Pass I, when the owner adds it here.
    /// </summary>
    public long[] Activities { get; set; } = [200];

    /// <summary>
    /// The season page's stage activities (MultiverseStage._kStageActivity of the playbook: for 101 that is 105 Fragment
    /// Gather, 106 Treasure, 107 Middle Season, 109/110 Racing, 111 World Exploration, 115 Emblem) are open whatever
    /// their own condition: every one is [1002,99], the final level cap (BaseLvLimitConfig day 198), so on a fresh realm
    /// the season page would list them all as locked for months. Their time windows still apply (107 keeps its
    /// settlement bounds, KeepTimeLimits), and World Exploration's chapters open with them (NewActivityService.WorldPhaseOpen).
    /// </summary>
    public bool SeasonStageActivities { get; set; } = true;

    /// <summary>Server-day conditions (3031 open days, 3030 playbook seconds) count as met from the first day.</summary>
    public bool IgnoreServerDay { get; set; } = true;

    /// <summary>
    /// TimeLimitConfig windows bounded by the server opening or by season stages open at the server opening and never
    /// close (every such id has a single state row: card pools, passes, Novice Challenge, Battle for Prontera, tower
    /// races...). Windows of activities given by absolute dates that already passed repeat with their own length.
    /// </summary>
    public bool PerpetualWindows { get; set; } = true;

    /// <summary>Windows that keep their original bounds: 1021 Midterm Settlement, 1023 Top 10 Guilds, 1036 Feedback (settlements, not content).</summary>
    public long[] KeepTimeLimits { get; set; } = [1021, 1023, 1036];

    /// <summary>NewActivityConfig rows that stay closed: 1011-1014, the original's "Test ... Pool" rows.</summary>
    public long[] ClosedActivities { get; set; } = [1011, 1012, 1013, 1014];

    /// <summary>Absolute windows that also become perpetual: 1060, the Territory War activity around the May block.</summary>
    public long[] PerpetualAbsolute { get; set; } = [1060];

    /// <summary>
    /// Territory Wars: the exported block of battles (2026-05-19..27, absolute dates) repeats every N days; 0 keeps the
    /// absolute dates. Each repeat is its own territory season (TerritoryWarsClock.Epoch), so rewards settle every cycle.
    /// </summary>
    public int TerritoryWarCycleDays { get; set; } = 14;

    public bool Perpetual(long timeLimitId) => Enabled && PerpetualWindows && !KeepTimeLimits.Contains(timeLimitId);
    public bool PerpetualAbsoluteWindow(long timeLimitId) => Enabled && PerpetualWindows && PerpetualAbsolute.Contains(timeLimitId);
    public long TerritoryWarPeriod => Enabled && TerritoryWarCycleDays > 0 ? TerritoryWarCycleDays * 86400L : 0;
    public bool Closes(long activityId) => Enabled && ClosedActivities.Contains(activityId);
    public bool Forces(long functionId) => Enabled && Functions.Contains(functionId);
    public bool ForcesActivity(long activityId) => Enabled && Activities.Contains(activityId);
    public bool OpensStageActivities => Enabled && SeasonStageActivities;
    public bool ServerDayFree => Enabled && IgnoreServerDay;
}
