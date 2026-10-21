using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild League (function 60): the tournament ladder, its matches and settlement.
/// Client: Logic/Asset/LC_DAL_GuildLeague.lua, Logic/GuildLeague/LC_GuildLeagueHelper.lua, Logic/Level/LC_DunGeon_GuildLeague.lua.
/// The calendar is <see cref="GuildLeagueCalendar"/>, the bracket <see cref="GuildLeagueBracket"/>. League standings and the
/// registered tournament are one realm blob (guild_league); a running match lives in memory and is committed with its
/// outcome. Reconstructed server choices are marked (inferred) with the reason.
/// </summary>
public sealed partial class GuildLeagueService : IGameTicker
{
    public const uint MgrData = 30410, Info = 30411, GroupInfo = 30412, ResultNotify = 30414, SyncNotify = 30415,
        TitleNotify = 30432, EventNotify = 30433;
    public const int Function = 60;

    private readonly GameData _data;
    private readonly AccountStore _accounts;
    private readonly RoleStore _roles;
    private readonly GuildStore _guilds;
    private readonly GuildService _guildService;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly GuildLeagueCalendar _calendar;
    private readonly SessionRegistry _sessions;
    private readonly InstanceService _instances;
    private readonly SceneService _scene;
    private readonly WorldData _maps;
    private readonly CombatService _combat;
    private readonly BuffService _buffs;
    private readonly MailService _mail;
    private readonly AuctionService _auctions;
    private readonly World _world;
    private readonly IOptions<ServerOptions> _options;
    private readonly TimeProvider _clock;
    private readonly GameLoop _loop;
    private readonly ILogger<GuildLeagueService> _log;

    /// <summary>A newly committed match outcome: its round (1-3) and each fighter's personal points (quest targets
    /// 106603 and 106019). Runs inside the Realm settlement; listeners post their work.</summary>
    public List<Action<int, IReadOnlyDictionary<long, long>>> MatchCommitted { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LeagueState _state;

    public sealed class LeagueState
    {
        /// <summary>SeasonService.SeasonId the standings belong to; a new season places by strength again (UITips 90006).</summary>
        public int Season { get; set; }
        /// <summary>The last registered tournament (GuildLeagueCalendar index), 0 before the first.</summary>
        public int Index { get; set; }
        public Cup? Cup { get; set; }
        public Dictionary<long, Standing> Guilds { get; set; } = new();
        public List<Report> Reports { get; set; } = new();
    }

    /// <summary>A guild's place in the league ladder.</summary>
    public sealed class Standing
    {
        /// <summary>GuildLeagueRankConfig _iRank: 1 Ragnarok .. 8 Black Iron.</summary>
        public int Tier { get; set; }
        /// <summary>Group order inside the tier in the last tournament and the place 1-8 after its finals (0 before).</summary>
        public int Group { get; set; }
        public int Place { get; set; }
        /// <summary>MG_Guild GuildLeagueRankLift of the last settlement.</summary>
        public int Lift { get; set; }
        /// <summary>Consecutive registrations the guild failed (mails 1036/1037).</summary>
        public int Misses { get; set; }
        public long Strength { get; set; }
        public string Name { get; set; } = "";
        public long Background { get; set; }
        public long Icon { get; set; }
    }

    /// <summary>One registered tournament.</summary>
    public sealed class Cup
    {
        public int Index { get; set; }
        public long Init { get; set; }
        /// <summary>Preliminaries, semifinals and finals as start/end pairs (unix seconds).</summary>
        public long[] Windows { get; set; } = [];
        public List<Group> Groups { get; set; } = new();
        /// <summary>The last round whose every pair has an outcome.</summary>
        public int SettledRound { get; set; }
        /// <summary>Places, promotion/relegation and tier mails are done.</summary>
        public bool Settled { get; set; }
        public GuildLeagueCalendar.Window Round(int round) => new(Windows[2 * round - 2], Windows[2 * round - 1]);
    }

    public sealed class Group
    {
        public int Tier { get; set; }
        public int Order { get; set; }
        /// <summary>The four preliminary pairs as [blue, red]; 0 is an empty slot.</summary>
        public List<long[]> Pairs { get; set; } = new();
        public List<Outcome> Outcomes { get; set; } = new();
    }

    /// <summary>A decided pair of one round, with what its rewards need.</summary>
    public sealed class Outcome
    {
        public int Round { get; set; }
        public int Pair { get; set; }
        public long Blue { get; set; }
        public long Red { get; set; }
        public long Winner { get; set; }
        public long BluePoints { get; set; }
        public long RedPoints { get; set; }
        public long Time { get; set; }
        public string Report { get; set; } = "";
        /// <summary>Participant role -> (guild, personal points over both battlefields).</summary>
        public Dictionary<long, long[]> Players { get; set; } = new();
        public bool Rewarded { get; set; }
        /// <summary>Both guilds' auctions were published or refused by the rule.</summary>
        public bool AuctionDone { get; set; }
    }

    /// <summary>A match report for 30411 (GuildLeagueHistory bytes).</summary>
    public sealed class Report
    {
        public string Id { get; set; } = "";
        public long Time { get; set; }
        public long[] Guilds { get; set; } = [];
        public byte[] History { get; set; } = [];
    }

    public GuildLeagueService(GameData data, AccountStore accounts, RoleStore roles, GuildStore guilds, GuildService guildService,
        FunctionService functions, SeasonService seasons, GuildLeagueCalendar calendar, SessionRegistry sessions,
        InstanceService instances, SceneService scene, WorldData maps, CombatService combat, BuffService buffs, MailService mail,
        AuctionService auctions, World world, GmCommands gm, IOptions<ServerOptions> options, TimeProvider clock, GameLoop loop, ILogger<GuildLeagueService> log)
    {
        this._data = data; this._accounts = accounts; this._roles = roles; this._guilds = guilds; this._guildService = guildService;
        this._functions = functions; this._seasons = seasons; this._calendar = calendar; this._sessions = sessions;
        this._instances = instances; this._scene = scene; this._maps = maps; this._combat = combat; this._buffs = buffs;
        this._mail = mail; this._auctions = auctions; this._world = world; this._options = options; this._clock = clock; this._loop = loop; this._log = log;
        _state = accounts.ReadSync<byte[]?>(new TerritoryRealmReadPlan(RealmStateKind.GuildLeague)) is { } saved
            ? JsonSerializer.Deserialize<LeagueState>(saved)! : new();
        guildService.League = new GuildService.LeagueHooks(MgrDataCoreAsync, InfoCoreAsync, GroupInfoCoreAsync);
        HookBattle();
        gm.Register("league", "register | fill | status (Guild League test aid: register the current or next tournament now; fill your guild's elite roster now; show it)", GmAsync);
    }

    private async Task GmAsync(GameSession s, string[] args)
    {
        string reply;
        if (args.Length > 0 && args[0] == "register") reply = await RegisterNowAsync();
        else if (args.Length > 0 && args[0] == "fill") reply = await FillNowAsync(s);
        else
        {
            var info = _calendar.Info(Now);
            var own = s.Role == null ? null : _guilds.ForRole(s.Role.Id);
            var group = own == null ? null : GroupOf(own.Id);
            reply = $"Tournament {info.Index} {info.EStatus} open={info.Open} start={info.StartTime} end={info.EndTime}; "
                + $"registered {_state.Index}, groups {_state.Cup?.Groups.Count ?? 0}, round settled {_state.Cup?.SettledRound ?? 0}"
                + (group == null ? "; your guild is not placed" : $"; your guild: tier {group.Tier} group {group.Order}");
        }
        await GmCommands.ReplyAsync(s, reply);
    }

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();
    internal LeagueState State => _state;

    private long Setting(string name, long fallback) => _data.Table("GuildSettingConfig").TryGetValue(name, out var row)
        && row.TryGetProperty("_kValue", out var value) && GameData.Number(value) is { } n ? (long)n : fallback;
    private long[] Param(string name) => _data.Table("PublicParamConfig").TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Array
        ? v.EnumerateArray().Select(x => (long)(GameData.Number(x) ?? 0)).ToArray() : [];
    private JsonElement? Rank(int tier) => _data.Table("GuildLeagueRankConfig").Values
        .Where(r => GameData.Int(r, "_iRank") == tier).Select(r => (JsonElement?)r).FirstOrDefault();
    private int[] Tiers => _data.Table("GuildLeagueRankConfig").Values.Select(r => (int)GameData.Int(r, "_iRank")).Order().ToArray();
    /// <summary>Groups of a tier hold 8 guilds (GetAllRankGroupNum, Lua_CFG_GuildLeagueRankConfig.lua:214-232: _iGroupNum * 8).</summary>
    private int Groups(int tier) => Rank(tier) is { } row ? (int)Math.Max(1, GameData.Int(row, "_iGroupNum")) : 1;

    private static ReservationKey LeagueKey => ReservationKey.Entity("realm", nameof(RealmStateKind.GuildLeague));

    private static LeagueState Copy(LeagueState value) => JsonSerializer.Deserialize<LeagueState>(JsonSerializer.SerializeToUtf8Bytes(value))!;
    private static void Save(LeagueState value, RoleItemPlanBuilder tx) =>
        tx.Add(new RealmStatePlan(RealmStateKind.GuildLeague, JsonSerializer.SerializeToUtf8Bytes(value)));

    private async Task CommitAsync(LeagueState next, string label, Action<RoleItemPlanBuilder>? more = null)
    {
        var tx = _roles.NewTransaction();
        Save(next, tx);
        more?.Invoke(tx);
        await _roles.CommitAsync(tx, "guild-league/" + label);
        _state = next;
    }

    /// <summary>Realm work of this service: the league gate under the operation's own scope (a borrowed Realm keeps it).</summary>
    private async Task<IDisposable?> EnterAsync(string origin)
    {
        if (_loop.Owners is not { } owners) { await _gate.WaitAsync(); return null; }
        return await owners.EnterWithGateAsync("guild-league/" + origin, _gate, [ReservationKey.Realm]);
    }

    // ---- registration ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Monday 05:03 (UITips 90006, GuildLeagueRankSureTime [[1,1,50300]]): every guild that meets the requirements is placed
    /// and drawn. Requirements: membership GuildLeaguePeopleNum and last week's activity GuildLeagueActive (50058/50059 texts;
    /// reading these two settings as the thresholds is inferred, ServerOptions.GuildLeagueMinMembers can lower the first). A
    /// placed guild that fails is warned (mail 1036) and dropped after the mail's count of consecutive misses (1037).
    /// </summary>
    private async Task RegisterAsync(GuildLeagueCalendar.Tournament t)
    {
        var next = Copy(_state);
        bool newSeason = next.Season != _seasons.SeasonId;
        if (newSeason) { next.Season = _seasons.SeasonId; next.Guilds.Clear(); }
        int minMembers = _options.Value.GuildLeagueMinMembers ?? (int)Setting("GuildLeaguePeopleNum", 40);
        long minActivity = Setting("GuildLeagueActive", 0);
        int dropAfter = MailCount(1037, 2);
        var warned = new List<(long Leader, int Template)>();
        var eligible = new List<GuildStore.State>();
        foreach (var g in _guilds.List())
        {
            bool ok = g.Status == (int)GuildStatus.Normal && g.Members.Count >= minMembers
                && _guildService.LastWeekActivityOf(g) >= minActivity;
            if (ok) { eligible.Add(g); continue; }
            if (!next.Guilds.TryGetValue(g.Id, out var standing) || standing.Tier == 0) continue;
            standing.Misses++;
            long leader = g.Members.Values.FirstOrDefault(m => m.Official == 1)?.RoleId ?? 0;
            if (standing.Misses >= dropAfter) { next.Guilds.Remove(g.Id); warned.Add((leader, (int)Setting("GuildLeagueLevelNoticeMailID", 1037))); }
            else warned.Add((leader, (int)Setting("GuildLeagueLevelWarnMailID", 1036)));
        }
        foreach (long gone in next.Guilds.Keys.Where(id => _guilds.Get(id) == null).ToArray()) next.Guilds.Remove(gone);
        foreach (var g in eligible)
        {
            var standing = next.Guilds.TryGetValue(g.Id, out var known) ? known : next.Guilds[g.Id] = new Standing();
            standing.Misses = 0;
            // (inferred) The server has no recovered combat power formula; member levels are the
            // strength proxy for the first placement.
            standing.Strength = g.Members.Values.Sum(m => (long)m.Level);
            standing.Name = g.Name; standing.Background = g.Background; standing.Icon = g.Icon;
        }
        Place(next, eligible.Select(g => g.Id).ToHashSet(), newSeason || next.Guilds.Values.All(s => s.Tier == 0));
        var cup = new Cup
        {
            Index = t.Index, Init = t.Init, Windows = [t.Preliminary.Start, t.Preliminary.End,
            t.Semifinal.Start, t.Semifinal.End, t.Final.Start, t.Final.End]
        };
        foreach (int tier in Tiers)
        {
            var members = next.Guilds.Where(p => p.Value.Tier == tier && eligible.Any(g => g.Id == p.Key))
                .OrderBy(p => p.Value.Group == 0 ? int.MaxValue : p.Value.Group).ThenBy(p => p.Value.Place == 0 ? int.MaxValue : p.Value.Place)
                .ThenByDescending(p => p.Value.Strength).ThenBy(p => p.Key).Select(p => p.Key).ToList();
            if (members.Count == 0) continue;
            int count = Math.Min(Groups(tier), (members.Count + 7) / 8);
            for (int order = 1; order <= count; order++)
            {
                // (inferred) Guilds are dealt round-robin in ladder order so the groups of a tier are even.
                var dealt = members.Where((_, i) => i % count == order - 1).ToList();
                var random = new Random(HashCode.Combine(t.Index, tier, order, _seasons.SeasonId));
                var group = new Group { Tier = tier, Order = order };
                group.Pairs.AddRange(GuildLeagueBracket.Draw(dealt, random).Select(p => new[] { p.Blue, p.Red }));
                cup.Groups.Add(group);
                foreach (long id in dealt) { next.Guilds[id].Group = order; next.Guilds[id].Place = 0; }
            }
        }
        foreach (var standing in next.Guilds.Values.Where(s => s.Tier > 0)) standing.Lift = 0;
        next.Index = t.Index;
        next.Cup = cup;
        var letters = warned.Where(w => w.Leader > 0 && _roles.Get(w.Leader) != null).ToArray();
        using (await _mail.LockRecipientsAsync(letters.Select(w => w.Leader)))
            await CommitAsync(next, "register", tx =>
            {
                foreach (var (leader, template) in letters)
                    _mail.QueueTemplateLocked(leader, $"guild-league:{t.Index}:placement:{leader}", template, [], tx);
            });
        foreach (var (leader, _) in letters)
            try { await _mail.NotifyRecipientAsync(leader); }
            catch (Exception e) { _log.LogWarning(e, "Guild League placement mail notice deferred for {Role}", leader); }
        _log.LogInformation("Guild League tournament {Index} registered: {Guilds} guilds in {Groups} groups",
            t.Index, eligible.Count, cup.Groups.Count);
    }

    /// <summary>The count a MailConfig template carries as its first content parameter (1036/1037: "2").</summary>
    private int MailCount(int template, int fallback) => _data.Row("MailConfig", template) is { } row
        && row.TryGetProperty("_iContent", out var content) && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 1
        && int.TryParse(content[1].ValueKind == JsonValueKind.String ? content[1].GetString() : content[1].ToString(), out int n) ? n : fallback;

    /// <summary>
    /// Tiers for the eligible guilds. The first tournament of a season fills the tiers from the top by strength (10960000256);
    /// later ones keep each guild's tier from the last settlement (promotion/relegation applied there) and new guilds start in
    /// the lowest tier (inferred: the client names no other entry rule). A tier above capacity pushes its last guilds down
    /// (inferred), and the lowest tier's overflow is not placed this time.
    /// </summary>
    private void Place(LeagueState next, HashSet<long> eligible, bool byStrength)
    {
        var tiers = Tiers;
        if (tiers.Length == 0) return;
        if (byStrength)
        {
            var ordered = eligible.OrderByDescending(id => next.Guilds[id].Strength).ThenBy(id => id).ToList();
            int index = 0;
            foreach (int tier in tiers)
                for (int n = 0; n < Groups(tier) * 8 && index < ordered.Count; n++)
                {
                    var s = next.Guilds[ordered[index++]];
                    s.Tier = tier; s.Group = 0; s.Place = 0;
                }
            for (; index < ordered.Count; index++) next.Guilds[ordered[index]].Tier = 0;
            return;
        }
        foreach (long id in eligible)
            if (next.Guilds[id].Tier == 0) { next.Guilds[id].Tier = tiers[^1]; next.Guilds[id].Group = 0; next.Guilds[id].Place = 0; }
        for (int i = 0; i < tiers.Length; i++)
        {
            var members = next.Guilds.Where(p => eligible.Contains(p.Key) && p.Value.Tier == tiers[i])
                .OrderBy(p => p.Value.Place == 0 ? int.MaxValue : p.Value.Place).ThenByDescending(p => p.Value.Strength)
                .ThenBy(p => p.Key).Select(p => p.Value).ToList();
            foreach (var extra in members.Skip(Groups(tiers[i]) * 8))
                extra.Tier = i + 1 < tiers.Length ? tiers[i + 1] : 0;
        }
    }

    // ---- 30410 / 30411 / 30412 ---------------------------------------------------------------------------------------

    private Group? GroupOf(long guild) => guild == 0 ? null
        : _state.Cup?.Groups.FirstOrDefault(g => g.Pairs.Any(p => p[0] == guild || p[1] == guild));

    /// <summary>A guild's cumulative score in its group after <paramref name="throughRound"/> rounds.</summary>
    private static Dictionary<long, int> Scores(Group group, int throughRound = 3)
    {
        var scores = new Dictionary<long, int>();
        foreach (var pair in group.Pairs) foreach (long id in pair) if (id > 0) scores[id] = 0;
        foreach (var o in group.Outcomes.Where(o => o.Round <= throughRound && o.Winner > 0))
            scores[o.Winner] = scores.GetValueOrDefault(o.Winner) + GuildLeagueBracket.Weight(o.Round);
        return scores;
    }

    private static GuildLeagueBracket.Pair[] Preliminary(Group group) =>
        group.Pairs.Select(p => new GuildLeagueBracket.Pair(p[0], p[1])).ToArray();

    private GuildLeagueGuildDungeonData Dungeon(long guild, Group? group, int pairIndex = -1, bool blue = true)
    {
        var data = new GuildLeagueGuildDungeonData { GuildID = guild };
        if (pairIndex >= 0) data.Matcharea = GuildLeagueBracket.Area(pairIndex);
        if (guild == 0) return data;
        var live = _guilds.Get(guild);
        var standing = _state.Guilds.GetValueOrDefault(guild);
        data.Name = live?.Name ?? standing?.Name ?? "";
        data.Icon = live != null ? new GuildHeadIcon { BackgroundType = live.Background, IconType = live.Icon }
            : new GuildHeadIcon { BackgroundType = standing?.Background ?? 0, IconType = standing?.Icon ?? 0 };
        if (group == null) return data;
        var scores = Scores(group);
        data.Rank = group.Tier;
        data.RankOrder = group.Order;
        data.Score = scores.GetValueOrDefault(guild);
        // rankNum is the guild's number inside the group: its seed until the finals are scored, then its place
        // (8 - score); GenerateRankData numbers empty slots 1-8 per group the same way (inferred reading).
        int seed = group.Pairs.FindIndex(p => p[0] == guild || p[1] == guild);
        data.RankNum = _state.Cup?.SettledRound >= 3 ? GuildLeagueBracket.Place(data.Score)
            : seed < 0 ? 0 : group.Pairs[seed][0] == guild ? seed + 1 : seed + 5;
        if (_state.Cup?.Settled == true && standing != null) data.RankLift = (GuildLeagueRankLift)standing.Lift;
        if (pairIndex < 0 && seed >= 0) data.Matcharea = GuildLeagueBracket.Area(seed);
        return data;
    }

    /// <summary>30410: the timeline, every placed guild by tier and the caller's own guild (LC_DAL_GuildLeague.lua:114-293).</summary>
    private Task MgrDataCoreAsync(GameSession s, uint msgId)
    {
        var own = s.Role == null ? null : _guilds.ForRole(s.Role.Id);
        var rsp = new GetGuildLeagueMgrDataRsp { ActivityInfo = _calendar.Info(Now) };
        var cup = _state.Cup;
        if (cup != null && rsp.ActivityInfo.EStatus != GuildLeagueStatus.NotOpen)
            foreach (var group in cup.Groups)
                foreach (long id in group.Pairs.SelectMany(p => p).Where(id => id > 0))
                    rsp.RankGuildInfo.Add(Dungeon(id, group));
        var mine = own == null ? null : GroupOf(own.Id);
        rsp.MyGuildInfo = own == null ? new GuildLeagueGuildDungeonData() : Dungeon(own.Id, cup != null ? mine : null);
        return s.SendGameAsync(msgId, rsp);
    }

    /// <summary>30411: the caller's guild's recent match reports, at most GuildLeagueHistoryReportNum.</summary>
    private Task InfoCoreAsync(GameSession s, uint msgId)
    {
        var own = s.Role == null ? null : _guilds.ForRole(s.Role.Id);
        var info = new GuildLeagueInfo();
        if (own != null)
            info.History.AddRange(_state.Reports.Where(r => r.Guilds.Contains(own.Id)).OrderByDescending(r => r.Time)
                .Take((int)Setting("GuildLeagueHistoryReportNum", 10)).Select(r => GuildLeagueHistory.Parser.ParseFrom(r.History)));
        return s.SendGameAsync(msgId, new GetGuildLeagueInfoRsp { Info = info });
    }

    /// <summary>
    /// 30412: the four preliminary pairs of the caller's group with cumulative scores; the client derives the semifinals and
    /// finals from them (UI_Sys_GuildLeague_BattleWidgetCtrl.lua:310-372). Pairs 1/3 carry matcharea 0, 2/4 carry 1.
    /// </summary>
    private Task GroupInfoCoreAsync(GameSession s, uint msgId)
    {
        var own = s.Role == null ? null : _guilds.ForRole(s.Role.Id);
        var rsp = new GetGuildLeagueGroupInfoRsp { EStatus = _calendar.Info(Now).EStatus };
        if (own != null && GroupOf(own.Id) is { } group)
            for (int i = 0; i < group.Pairs.Count; i++)
                rsp.Info.Add(new GuildLeagueMatchInfo
                {
                    BlueGuild = Dungeon(group.Pairs[i][0], group, i),
                    RedGuild = Dungeon(group.Pairs[i][1], group, i)
                });
        return s.SendGameAsync(msgId, rsp);
    }

    // ---- clock -------------------------------------------------------------------------------------------------------

    private long _lastSweep;
    private bool _sweeping, _settling;

    /// <summary>Tests: the per-second work of <paramref name="at"/> and any reconcile it started are done.</summary>
    internal bool Idle(long at) => _lastSweep == at && !_sweeping && !_settling;

    public void Tick(GameTime _)
    {
        long now = Now;
        if (_sweeping || now == _lastSweep) return;
        _lastSweep = now;
        bool live = _matches.Count > 0;
        bool due = !_settling && (RegistrationDue(now) != null || SettlementDue(now) || ElitesDue(now));
        if (!live && !due) return;
        _sweeping = true;
        _loop.PostRuntime(async () =>
        {
            try
            {
                if (live) await BattleClockAsync(now);
                if (!_settling && (RegistrationDue(now) != null || SettlementDue(now) || ElitesDue(now)))
                {
                    _settling = true;
                    _loop.PostLegacy(async () => { try { await ReconcileAsync(); } finally { _settling = false; } }, "guild league reconcile");
                }
            }
            finally { _sweeping = false; }
        }, "guild league sweep");
    }

    private GuildLeagueCalendar.Tournament? RegistrationDue(long now) =>
        _calendar.Current(now) is { } t && (_state.Index < t.Index || _state.Season != _seasons.SeasonId && _state.Index > 0)
            && now < t.Final.End ? t : null;

    private bool SettlementDue(long now)
    {
        if (_matches.Values.Any(m => m.Ended && !m.Committed)) return true;
        if (_state.Cup is not { } cup) return false;
        if (cup.Groups.Any(g => g.Outcomes.Any(o => !o.Rewarded || !o.AuctionDone))) return true;
        return cup.SettledRound < 3 ? now >= cup.Round(cup.SettledRound + 1).End : !cup.Settled;
    }

    /// <summary>Registration, ended matches, round closes and the tournament settlement, in that order, under Realm.</summary>
    internal async Task ReconcileAsync()
    {
        using var scope = await EnterAsync("reconcile");
        try
        {
            long now = Now;
            if (RegistrationDue(now) is { } t) await RegisterAsync(t);
            foreach (var match in _matches.Values.Where(m => m.Ended && !m.Committed).ToArray())
                await CommitMatchAsync(match);
            await CloseRoundsAsync(now);
            await ElitesAsync(now);
            await RewardPendingAsync();
            if (_state.Cup is { Settled: false, SettledRound: >= 3 }) await SettleCupAsync();
        }
        finally { _gate.Release(); }
    }

    /// <summary>GM aid: register the current or next tournament now, as if its 05:03 had come.</summary>
    internal async Task<string> RegisterNowAsync()
    {
        var t = _calendar.Current(Now) is { } current && _state.Index < current.Index ? current : _calendar.Next(Now);
        using var scope = await EnterAsync("gm register");
        try
        {
            if (_state.Index >= t.Index && _state.Season == _seasons.SeasonId) return $"Tournament {t.Index} is already registered.";
            await RegisterAsync(t);
            return $"Tournament {t.Index} registered: {_state.Cup!.Groups.Count} groups.";
        }
        finally { _gate.Release(); }
    }
}
