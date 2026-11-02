namespace Ro3Emu.Server;

public sealed class ServerOptions
{
    /// <summary>玩家套件契約；既有獨立伺服器預設維持未啟用。</summary>
    public ClientReleaseOptions ClientRelease { get; set; } = new();
    /// <summary>伺服器送出的可見名稱；客戶端選單仍採玩家原生選語。zh_TW、zh_CN 或 en。</summary>
    public string ClientTextLanguage { get; set; } = "zh_TW";

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

    /// <summary>The web panel's listen address: its own bind address, else the server's.</summary>
    public System.Net.IPAddress PanelListenAddress() =>
        Panel.BindAddress is { Length: > 0 } bind ? System.Net.IPAddress.Parse(bind) : ListenAddress();

    /// <summary>
    /// Only this machine reaches the server: the client is told a loopback address, the game ports and the panel listen
    /// on loopback and no proxy serves the panel (<see cref="Ro3Emu.Panel.PanelOptions.PublicFacing"/>). Local play and
    /// the tests. A public <see cref="PublicHost"/> with a loopback bind is a tunnel (frp, ssh -R, nginx stream): it
    /// delivers strangers to loopback. An address that does not parse counts as public, so a typo never opens GM.
    /// </summary>
    public bool LocalOnly()
    {
        if (!System.Net.IPAddress.TryParse(PublicHost, out var told) || !System.Net.IPAddress.IsLoopback(told)) return false;
        if (BindAddress is { Length: > 0 } bind && !System.Net.IPAddress.TryParse(bind, out _)) return false;
        if (Panel.BindAddress is { Length: > 0 } panel && !System.Net.IPAddress.TryParse(panel, out _)) return false;
        return System.Net.IPAddress.IsLoopback(ListenAddress()) && !Panel.PublicFacing(PanelListenAddress());
    }

    /// <summary>The voice relay serves whenever a port is set (the stream is sealed, so off loopback too).</summary>
    public bool VoiceServed() => Voice.Port > 0;

    public int HttpPort { get; set; } = 5380;

    public int MicroGatePort { get; set; } = 20000;

    public int GameGatePort { get; set; } = 33100;

    public string DataDir { get; set; } = "data";

    /// <summary>Story dungeons whose encounter scripts are not in the client use the reconstructed waves in
    /// Reconstructed/dungeon-monsters.json (inferred, marked "odtworzone"); off refuses them as before.</summary>
    public bool ReconstructedDungeons { get; set; } = true;

    /// <summary>Field monsters whose Ragnarok Online 1 counterpart is aggressive (rAthena pre-renewal mob_db, listed in
    /// Reconstructed/ro1-aggressive-monsters.json) attack on sight like the client's AIConfig 20000004 monsters. An RO1
    /// reference, not client evidence (inferred); on by default as a trial (owner's decision, 28.10), off changes nothing.</summary>
    public bool Ro1AggressiveMonsters { get; set; } = true;

    /// <summary>The RO1 aggression stays off for a monster on an open field (not a dungeon, town or sandbox) whose own field
    /// spawns hold a common monster (MonsterService.Ro1GateMinSharePercent of the population) more than this many levels
    /// below it (Payon Forest: Greatest General 55 and Elder Willow 34 among Horns 16), so new characters can follow the
    /// story there; bosses and MVPs are never gated; 0 or negative: no gate. (inferred, owner's decision 31.10)</summary>
    public int Ro1AggressiveMaxLevelGap { get; set; } = 15;

    /// <summary>Monsters whose Ragnarok Online 1 counterpart is much tougher than the client makes them get the RO1 HP
    /// (Reconstructed/ro1-monster-hp.json: the Pupa, 4 HP in the client, 427 here). An RO1 reference, not
    /// client evidence (inferred). Off by default: the client cuts their HP on purpose (_kAttriScale), so the 1:1 server
    /// keeps it; an operator who wants the RO1 feel turns it on.</summary>
    public bool Ro1MonsterHp { get; set; }

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

    /// <summary>
    /// Walk check (SceneService.AcceptWalkAsync): the distance budget of the speed attribute and the client's nav grid.
    /// "enforce" (default) snaps a refused walk back to the last good position, "log" only reports it (CheatMonitor), "off"
    /// skips the check. A walk to a position that is not a number is refused in every mode but "off".
    /// </summary>
    public string WalkCheck { get; set; } = "enforce";

    /// <summary>
    /// Travel check (SceneService.Travel): which raw 30012 and 30131 requests a client may send. "enforce" (default) refuses
    /// the others, "log" counts them and lets them through (CheatMonitor), "off" skips the check.
    /// </summary>
    public string TravelCheck { get; set; } = "enforce";

    /// <summary>Anti-cheat limits and the web panel's suspicious-activity thresholds (CheatMonitor, docs/systems/anticheat.md).</summary>
    public AntiCheatOptions AntiCheat { get; set; } = new();

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

    /// <summary>Paid products and top-ups: free for now (owner decision 2026-10-31).</summary>
    public ShopOptions Shop { get; set; } = new();

    /// <summary>
    /// Server-wide reward multipliers. These are orthogonal: monster Zeny is affected by <see cref="RewardRateOptions.Zeny"/>
    /// but not by <see cref="RewardRateOptions.MonsterDrops"/>, while quest EXP uses the Base/Job rates only.
    /// </summary>
    public RewardRateOptions RewardRates { get; set; } = new();

    /// <summary>Opt-in, account-scoped client evidence for diagnosing missing skill presentation.</summary>
    public SkillTraceTelemetryOptions SkillTraceTelemetry { get; set; } = new();

    /// <summary>
    /// Accounts allowed to use GM commands in game ("!" lines in chat, the client's GM panel, 1031/1034). Empty:
    /// <see cref="GmOpenWhenEmpty"/> decides. docs/guide/gm-access.md.
    /// </summary>
    public string[] GmAccounts { get; set; } = [];

    /// <summary>
    /// What an empty <see cref="GmAccounts"/> means in game: true = every account has GM (research server, tests,
    /// local play), false = nobody has GM. Left unset, ServerHost decides: true only on a server that only this machine
    /// reaches (<see cref="LocalOnly"/>) and that was not set to Production explicitly (ASPNETCORE_ENVIRONMENT,
    /// DOTNET_ENVIRONMENT or --environment); false on a public bind, behind a proxy, or in Production. The loopback
    /// HTTP POST /ro3emu/gm (the operator's tools) keeps the old rule either way: empty = every account.
    /// </summary>
    public bool? GmOpenWhenEmpty { get; set; }

    /// <summary>The web control panel on its own port (docs/guide/panel.md).</summary>
    public Ro3Emu.Panel.PanelOptions Panel { get; set; } = new();

    /// <summary>In-game bug reports (FeedbackService, docs/guide/feedback.md).</summary>
    public FeedbackOptions Feedback { get; set; } = new();

    /// <summary>Database copies in DataDir/backups (DatabaseBackup).</summary>
    public BackupOptions Backup { get; set; } = new();

    /// <summary>The web panel's update manager (Updates.UpdateManager, docs/guide/panel.md).</summary>
    public UpdateOptions Updates { get; set; } = new();

    /// <summary>How long economic receipts keep their payload and outcome bytes (ReceiptRetention).</summary>
    public ReceiptRetentionOptions ReceiptRetention { get; set; } = new();

    /// <summary>
    /// Credential checks (SDK login, character deletion) one remote address may make per minute; past it the server
    /// answers 429 without hashing. Each check costs a 100 000-round PBKDF2 hash, and logins register accounts.
    /// Loopback is exempt (local play, the load tests, RO3Emu.exe); behind a reverse proxy every player shares the
    /// proxy's address, so raise this there.
    /// </summary>
    public int LoginAttemptsPerMinute { get; set; } = 20;

    /// <summary>Tests: the credential limit and the gift code address limit apply to loopback too.</summary>
    public bool LoginLimitIncludesLoopback { get; set; }

    /// <summary>
    /// The operator token the loopback tools (/ro3emu/gm, /shutdown, /online, /reports, /metrics) must send in
    /// X-Ro3Emu-Operator (Http.OperatorToken). Unset: a random one, kept in DataDir/operator.token. Set (at least 32
    /// characters): this one, also written to that file, for scripts that already hold a secret.
    /// </summary>
    public string? OperatorToken { get; set; }

    /// <summary>
    /// A game login (SDK login) with an account name that has no account is refused instead of creating the account,
    /// so accounts come only from the web panel's sign-up (a closed test). The client shows its wrong-credentials
    /// popup, as for a wrong password. Off: the first login creates the account, as before.
    /// </summary>
    public bool RefuseUnknownAccounts { get; set; }

    /// <summary>Chat flood protection (ChatService.Flood.cs). Off by default.</summary>
    public ChatFloodOptions ChatFlood { get; set; } = new();

    /// <summary>
    /// Gift code attempts (POST /gift-card/use/v2, docs/systems/gift-codes.md) one remote address, and separately one
    /// account, may make per minute; past it the server answers the refusal code GiftCardRateLimited without looking
    /// the code up. Loopback is exempt from the address budget, as for <see cref="LoginAttemptsPerMinute"/>, but not
    /// from the account budget. Behind a reverse proxy every player shares the proxy's address, so raise this there.
    /// </summary>
    public int GiftCodeAttemptsPerMinute { get; set; } = 10;

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

    /// <summary>
    /// (inferred) Seconds of a Realm of the Gods raid's preparation phase: the boss waits until the team leader presses the
    /// scene's "Start Battle" button (DungeonLua_30201xx DungeonBase.FunctionButtons, sent as 30125) or this time runs out.
    /// The client has the button, not the phase length.
    /// </summary>
    public int RaidPreparationSeconds { get; set; } = 120;

    /// <summary>
    /// Pace of the scripted scenes' own timers (the hide-and-seek rounds), in percent of their reconstructed length:
    /// 100 plays them as designed, a test makes them shorter.
    /// </summary>
    public int ScriptedScenePacePercent { get; set; } = 100;

    /// <summary>
    /// Fewer of one monster on one map (Ro3Emu:SpawnLimits). Empty by default: every spawn pool group creates as many as the
    /// client's own scene data says (docs/systems/monster-ai.md). A rule only lowers the count a pool group rolls for its NPC
    /// at the first spawn of a map line; respawns replace each defeated monster one for one.
    /// </summary>
    public List<SpawnLimitRule> SpawnLimits { get; set; } = [];

    /// <summary>
    /// More (or less) EXP from the monsters of the open field, by the killer's base level (Ro3Emu:FieldExpRates). Empty by
    /// default: a kill pays what the client's drop tables say (docs/systems/monster-ai.md). The first rule whose level range
    /// holds the killer's level scales that kill's Base EXP, and the Job EXP that equals it, by its percent. (inferred: the
    /// original had no such knob.)
    /// </summary>
    public List<FieldExpRule> FieldExpRates { get; set; } = [];

    /// <summary>
    /// (inferred) Star Cores drop for roles with the Astral Level open (ParagonService.StarCoreDrops). The client data gives
    /// them no source; false turns the reconstructed drop off.
    /// </summary>
    public bool StarCoreDrops { get; set; } = true;

    /// <summary>
    /// (inferred) Chance in percent that an MVP kill drops a Lord's Mane, the Starshatter Dragon's unseal material, to one
    /// participant (MvpService). The client data gives it no source; 0 turns it off.
    /// </summary>
    public int LordsManePercent { get; set; } = 10;

    /// <summary>
    /// (inferred) Chance in percent that a boss kill drops a title Crest of the boss level's tier (TitleService). The
    /// client data gives the Crests no source; 0 turns it off.
    /// </summary>
    public int TitleCrestPercent { get; set; } = 25;

    /// <summary>
    /// (inferred) Every Tavern active day (20 minutes in a room) also brings one higher drink: Grape Wine every 7th
    /// active day, Craft Brew every 3rd, Gingerbread otherwise (TavernService). The client data gives only Hot Cocoa a
    /// source (the daily quest's award); false leaves it at that.
    /// </summary>
    public bool TavernDayDrinks { get; set; } = true;

    /// <summary>
    /// (inferred) Seconds from a raid boss fight's start to its enrage, against which the Final Trial "more than N minutes
    /// remaining until enrage" honors (TenGameplayFinalTrialConfig, condition 7004) are judged. The enrage timer lives in
    /// the boss's server script, which the client does not carry.
    /// </summary>
    public int RaidEnrageSeconds { get; set; } = 600;

    /// <summary>Boss special skills (BossSkillSets, MonsterService.BossSkills): the scheduler's inferred numbers.</summary>
    public BossSkillOptions BossSkills { get; set; } = new();

    /// <summary>
    /// TEST AIDS, not original behaviour (Ro3Emu:Testing, docs/guide/hosting.md): To-Do activities open at any hour and
    /// rewards that never run out, so testers need not wait for a weekday or a weekly reset. All off by default; GM
    /// "!testing" changes them in memory.
    /// </summary>
    public TestingOptions Testing { get; set; } = new();
}

/// <summary>
/// Switches for test realms (Game/TestingSwitches.cs). None of them is the original game's behaviour; the server logs a
/// warning at startup while one is on.
/// </summary>
public sealed class TestingOptions
{
    /// <summary>Every To-Do activity (DailyActivityConfig) counts as open now, any weekday and hour: its occurrence repeats
    /// back to back with its own length, phases included. The Guild League and Territory Wars rows keep their calendars.</summary>
    public bool AlwaysOpen { get; set; }

    /// <summary>Reward allowances and attempt counters never run out: dungeon reward counts (Realm of the Gods, Phantom
    /// Realm), MVP daily reward counts, the Commendation Vault extra awards and Dodo's Request.</summary>
    public bool UnlimitedRewards { get; set; }
}

/// <summary>Server-wide reward rates. 1 is unchanged, 0.5 is half, and 0 disables that reward family.</summary>
public sealed class RewardRateOptions
{
    private const decimal Maximum = 100m;

    /// <summary>Base EXP from kills, quests and resolved server awards.</summary>
    public decimal BaseExperience { get; set; } = 1m;

    /// <summary>JOB EXP from kills and resolved server awards.</summary>
    public decimal JobExperience { get; set; } = 1m;

    /// <summary>Zeny (item 1) produced by reward transactions. Costs, shops, refunds and transfers are unchanged.</summary>
    public decimal Zeny { get; set; } = 1m;

    /// <summary>Non-currency item quantities from monster kills. Fractional units use one random remainder roll.</summary>
    public decimal MonsterDrops { get; set; } = 1m;

    internal void Validate()
    {
        Validate(nameof(BaseExperience), BaseExperience);
        Validate(nameof(JobExperience), JobExperience);
        Validate(nameof(Zeny), Zeny);
        Validate(nameof(MonsterDrops), MonsterDrops);
    }

    private static void Validate(string name, decimal value)
    {
        if (value is < 0m or > Maximum)
            throw new InvalidOperationException($"Ro3Emu:RewardRates:{name} must be between 0 and {Maximum}.");
    }

    internal long ScaleBaseExperience(long amount) => ScaleWhole(amount, BaseExperience);
    internal long ScaleJobExperience(long amount) => ScaleWhole(amount, JobExperience);
    internal long ScaleZeny(long amount) => ScaleWhole(amount, Zeny);
    internal int ScaleBaseExperienceToInt(int amount) => ScaleWholeToInt(amount, BaseExperience);
    internal int ScaleJobExperienceToInt(int amount) => ScaleWholeToInt(amount, JobExperience);
    internal int ScaleZenyToInt(int amount) => ScaleWholeToInt(amount, Zeny);

    /// <summary>Scales the three reward-only ItemConfig entries used inside AwardConfig.</summary>
    internal int ScaleConfiguredAward(int entry, int amount) => entry switch
    {
        1 => ScaleZenyToInt(amount),
        100 => ScaleBaseExperienceToInt(amount),
        101 => ScaleJobExperienceToInt(amount),
        _ => amount,
    };

    internal int ScaleMonsterDrop(int amount, Random random)
    {
        if (amount <= 0 || MonsterDrops == 1m) return amount;
        if (MonsterDrops == 0m) return 0;
        decimal exact = amount * MonsterDrops;
        if (exact >= int.MaxValue) return int.MaxValue;
        int whole = (int)decimal.Floor(exact);
        decimal remainder = exact - whole;
        return whole + (remainder > 0m && (decimal)random.NextDouble() < remainder ? 1 : 0);
    }

    private static long ScaleWhole(long amount, decimal rate)
    {
        if (amount <= 0 || rate == 1m) return amount;
        if (rate == 0m) return 0;
        decimal exact = decimal.Floor(amount * rate);
        long scaled = exact >= long.MaxValue ? long.MaxValue : (long)exact;
        // Positive EXP/Zeny rewards keep one indivisible unit; large values still converge on the requested rate.
        return Math.Max(1, scaled);
    }

    private static int ScaleWholeToInt(int amount, decimal rate)
    {
        long scaled = ScaleWhole(amount, rate);
        return scaled >= int.MaxValue ? int.MaxValue : (int)scaled;
    }
}

/// <summary>
/// Diagnostic client telemetry. Enabled for every signed-in account during the animation investigation;
/// installing ClientHooks alone still sends nothing without the local marker.
/// </summary>
public sealed class SkillTraceTelemetryOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Allow every signed-in player to receive a trace grant. Kept explicit so an empty list is never ambiguous.</summary>
    public bool AllowAllAccounts { get; set; } = true;
    public string[] Accounts { get; set; } = [];
    public int GrantSeconds { get; set; } = 3600;
    public int EventsPerSecond { get; set; } = 20;
    public int Burst { get; set; } = 40;
    public int MaxPayloadBytes { get; set; } = 256;

    internal void Validate()
    {
        if (GrantSeconds is < 60 or > 86400)
            throw new InvalidOperationException("Ro3Emu:SkillTraceTelemetry:GrantSeconds must be between 60 and 86400.");
        if (EventsPerSecond is < 1 or > ushort.MaxValue)
            throw new InvalidOperationException("Ro3Emu:SkillTraceTelemetry:EventsPerSecond must be between 1 and 65535.");
        if (Burst is < 0 or > ushort.MaxValue)
            throw new InvalidOperationException("Ro3Emu:SkillTraceTelemetry:Burst must be between 0 and 65535.");
        if (MaxPayloadBytes is < Ro3Emu.ClientHooks.SkillTraceProtocol.MaxReportSize or > 4096)
            throw new InvalidOperationException($"Ro3Emu:SkillTraceTelemetry:MaxPayloadBytes must be between " +
                $"{Ro3Emu.ClientHooks.SkillTraceProtocol.MaxReportSize} and 4096.");
    }
}

/// <summary>
/// The boss skill scheduler. The skill rows, their cooldowns, cast and action times, ranges, damage and buffs come from
/// SkillConfig; the original server's AI scripts that decided when to cast are not in the client, so every number here
/// is (inferred) and can be tuned without a code change.
/// </summary>
public sealed class BossSkillOptions
{
    /// <summary>Off: bosses only swing their normal attack, as before the scheduler.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after a boss engages before its first special skill.</summary>
    public int FirstCastDelayMs { get; set; } = 3000;

    /// <summary>Pause after a special's action ends before the next one (the boss swings meanwhile); a row's longer
    /// _iPubCD wins.</summary>
    public int GlobalCooldownMs { get; set; } = 2000;

    /// <summary>Cooldown of a skill whose family carries only placeholder cooldowns (every row 0-2 s: Realm of the
    /// Gods, story Baphomet and Pharaoh), where the encounter script set the pace.</summary>
    public int PlaceholderCooldownMs { get; set; } = 15000;

    /// <summary>HP percent at which a boss with phase variants enters each next phase (phase 1, phase 2...).</summary>
    public int[] PhaseHpPercents { get; set; } = [50, 25];

    /// <summary>Area radius (m) when neither the row nor its show template gives one.</summary>
    public float DefaultRadius { get; set; } = 5f;

    /// <summary>Longest distance (m) to its target at which a boss starts a targeted skill; rows with "anywhere"
    /// ranges (_iDistanceMax 9999) are capped to it.</summary>
    public float MaxReach { get; set; } = 15f;

    /// <summary>Most hits one cast lands (a show's attack points; Ring of Light has three).</summary>
    public int MaxHitsPerCast { get; set; } = 6;

    /// <summary>Boss skill damage as a percent of the row's formula result.</summary>
    public int DamagePercent { get; set; } = 100;

    /// <summary>Minions of a summon skill whose description gives no count.</summary>
    public int SummonCount { get; set; } = 3;

    /// <summary>Life of a summoned minion without a self-destruct born buff.</summary>
    public int SummonLifeMs { get; set; } = 30000;
}

/// <summary>
/// Compaction of old economic receipts (Accounts/ReceiptRetention.cs, docs/guide/hosting.md). Every row, hash, child
/// receipt, revision, ledger leg and source claim stays; only the payload and outcome bytes of receipts older than
/// <see cref="Days"/> are dropped.
/// </summary>
public sealed class ReceiptRetentionOptions
{
    /// <summary>Age in days after which a receipt is compacted; 0 keeps every receipt whole. A compacted receipt still
    /// replays (by its intent hash), so the window only bounds how far back the offline audit re-checks intents.</summary>
    public int Days { get; set; } = 2;

    /// <summary>Receipts compacted per writer transaction.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Minutes between compaction runs (the first runs a few minutes after startup).</summary>
    public int IntervalMinutes { get; set; } = 60;
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

    /// <summary>
    /// A Discord channel webhook (https://discord.com/api/webhooks/...): each stored report is also posted there. Unset
    /// keeps reports in the file only. The URL lets anyone post to that channel, so keep it out of committed config.
    /// </summary>
    public string? DiscordWebhookUrl { get; set; }
}

/// <summary>
/// Ro3Emu:ChatFlood (Game.ChatFloodGuard): per-account chat limits for a public server, off by default. The numbers
/// stay above the client's own channel cooldowns (ChatChannelConfig _iCD: world 5 s), so an unmodified client typing
/// at its fastest is never refused. GM accounts (Ro3Emu:GmAccounts) are exempt.
/// </summary>
public sealed class ChatFloodOptions
{
    public bool Enabled { get; set; }

    /// <summary>The window the two line budgets count in.</summary>
    public int WindowSeconds { get; set; } = 10;

    /// <summary>Lines per window on the open channels: world, map, recruitment and every other channel anyone can join.</summary>
    public int OpenLines { get; set; } = 5;

    /// <summary>Lines per window on whispers, party, guild and friend-group chat, counted apart from the open channels.</summary>
    public int PrivateLines { get; set; } = 10;

    /// <summary>A line whose text (letter case and spaces aside) went to the same channel or player this many seconds
    /// before is dropped; 0 turns the check off.</summary>
    public int DuplicateSeconds { get; set; } = 30;

    /// <summary>How long an account that went past a budget cannot chat.</summary>
    public int MuteSeconds { get; set; } = 60;
}

/// <summary>
/// The update manager of the web panel's Server control (Updates.UpdateManager): the releases on GitHub, a checked
/// download and an install or rollback planned like a restart (docs/guide/panel.md, Updates).
/// </summary>
public sealed class UpdateOptions
{
    /// <summary>Ask GitHub for new releases: when the page asks and every <see cref="CheckHours"/>. Off: no request leaves
    /// the server and the page shows the installed version only.</summary>
    public bool Check { get; set; } = true;

    /// <summary>The GitHub repository whose releases are offered (owner/name).</summary>
    public string Repository { get; set; } = "aethai/ro3emu-server";

    /// <summary>Hours between two checks in the background; at least 1.</summary>
    public double CheckHours { get; set; } = 6;

    /// <summary>The GitHub API's address. Another one (a local copy of the releases list) is for testing; its own
    /// address may then serve the downloads too.</summary>
    public string ApiUrl { get; set; } = "https://api.github.com";

    /// <summary>
    /// How the server is run, which decides whether the page can install: auto (found from the environment), systemd,
    /// service (a Windows service wrapper that restarts it), docker, app (RO3Emu.exe or the launcher) or manual.
    /// </summary>
    public string Hosting { get; set; } = "auto";

    /// <summary>The release folder the server runs from (the one with server/ and RO3Emu). Empty: the folder above
    /// the server's own executable when it is a release's.</summary>
    public string? InstallDir { get; set; }
}

/// <summary>
/// Database copies (DatabaseBackup): one at start and one every <see cref="IntervalHours"/>. The receipt tables make the
/// database grow with play, and every copy is a whole database, so all but the newest <see cref="Uncompressed"/> are
/// gzip-compressed (about a quarter of the size).
/// </summary>
public sealed class BackupOptions
{
    /// <summary>Copies kept, compressed or not; older ones are deleted.</summary>
    public int Keep { get; set; } = 20;

    /// <summary>Hours between two copies.</summary>
    public double IntervalHours { get; set; } = 6;

    /// <summary>Newest copies left as plain .db files, ready to restore or open; at least 1. Set it to
    /// <see cref="Keep"/> to compress nothing.</summary>
    public int Uncompressed { get; set; } = 2;
}

/// <summary>
/// Team voice relay (Talk/Listen/Mute in the party panel). The listener only opens when <see cref="Port"/> is set;
/// an off relay still answers the private ticket request with port 0 so the client's menu can disable itself cleanly.
/// </summary>
public sealed class VoiceOptions
{
    /// <summary>TCP port the voice gate listens on; 0 keeps the relay off.</summary>
    public int Port { get; set; }

    /// <summary>Members a single voice room (one party/team) may hold.</summary>
    public int MaxRoomSize { get; set; } = 40;

    /// <summary>Listeners a Tavern room's voice may hold. The room itself takes PublicParamConfig.BarroomPersonNum (1000)
    /// members; (inferred) the relay bounds its own fan-out, everyone past this keeps the room's text chat.</summary>
    public int MaxTavernRoomSize { get; set; } = 200;

    /// <summary>Audio frames a connection may send per second before the token bucket drops the extra ones.</summary>
    public int FramesPerSecond { get; set; } = 50;

    /// <summary>Extra frames the token bucket allows in a short burst on top of <see cref="FramesPerSecond"/>.</summary>
    public int Burst { get; set; } = 10;

    /// <summary>Control frames (Join, Leave, Mic, Speaker, Ping, anything else after Auth) a connection may send per
    /// second; the extra ones are dropped, and a sustained flood (3x this rate over 5 s) disconnects it. A real
    /// client sends a few per minute: a ping every 10 s plus the player's own clicks.</summary>
    public int ControlPerSecond { get; set; } = 10;

    /// <summary>Extra control frames allowed in a short burst on top of <see cref="ControlPerSecond"/>.</summary>
    public int ControlBurst { get; set; } = 20;

    /// <summary>Longest opus payload accepted in one Audio frame, in bytes.</summary>
    public int MaxPayload { get; set; } = 512;

    /// <summary>How long a voice ticket stays valid before a connection must present it.</summary>
    public int TicketSeconds { get; set; } = 60;

    /// <summary>Voice connections the listener accepts at once; past this new connections are refused.</summary>
    public int MaxConnections { get; set; } = 1024;

    /// <summary>Voice connections one IP address may hold at once (a LAN party behind one router needs a few).</summary>
    public int MaxConnectionsPerIp { get; set; } = 8;

    /// <summary>Connections from one IP that end without a valid Auth before that IP is refused for a minute.</summary>
    public int MaxFailedAuthPerIp { get; set; } = 10;

    /// <summary>Seconds a fresh connection has to send its Auth frame before it is closed. A test host lowers this.</summary>
    public int FirstFrameTimeoutSeconds { get; set; } = 5;

    /// <summary>Seconds without any frame before an authed connection is closed as idle. A test host lowers this.</summary>
    public int IdleTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// The client's cash shop (2001 CREATE_ORDER from the top-up panel, the pass and the month card): on this server a purchase
/// costs nothing (owner decision 2026-10-31, docs/systems/shop-currency.md). Off: every order is refused as unavailable.
/// </summary>
public sealed class ShopOptions
{
    /// <summary>Products, packs and top-ups are granted without payment.</summary>
    public bool FreeProducts { get; set; } = true;

    /// <summary>Free top-ups of one package a role may take per game day (inferred cap, at least 1).</summary>
    public int FreeTopUpsPerDay { get; set; } = 1;
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
    /// 70122 Totem Shop, 41 Feedback, 1000000 Adv. Guild (AdventureGroupService), 96 Valhalla Trials (ValhallaService),
    /// 69 Season page (SeasonPageService; level 50 until the 20 Oct 2026 client closed it with condition 2000062, [1002,9999])
    /// (Feedback's panel comes from Ro3Emu.ClientHooks.FeedbackButton, its backend is FeedbackService), 14 Title (TitleService; its
    /// widget is hosted by Ro3Emu.ClientHooks.TitleButton). Shells without a working panel (Academy 34...) stay closed so the menu shows no dead
    /// buttons; a function joins this list together with its backend. Novice Challenge (90, activity 112) is left out
    /// (owner, 2026-10-11): its 15 random event texts are "Event Text..."/"Option A" filler and the first node's
    /// dialogues 9901-9904 do not exist, so the original never finished it.
    /// </summary>
    public long[] Functions { get; set; } = [7201, 70122, 41, 1000000, 96, 69, 14];

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

/// <summary>One <see cref="ServerOptions.SpawnLimits"/> rule: on the map <see cref="Scene"/>, at most <see cref="Max"/> of the
/// NPC <see cref="Npc"/> spawn from its pool group (a group that rolls fewer keeps its count).</summary>
public sealed class SpawnLimitRule
{
    public long Scene { get; set; }
    public int Npc { get; set; }
    public int Max { get; set; }
}

/// <summary>One <see cref="ServerOptions.FieldExpRates"/> rule: killers of base level <see cref="MinLevel"/> to
/// <see cref="MaxLevel"/> get <see cref="Percent"/> percent of the EXP a field monster pays (100 leaves it as it is).</summary>
public sealed class FieldExpRule
{
    public int MinLevel { get; set; }
    public int MaxLevel { get; set; }
    public int Percent { get; set; } = 100;
}
