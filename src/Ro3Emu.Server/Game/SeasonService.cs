using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The current season: 5021 DBSeasonInfo.current at login. The client reads its playBookId
/// for every multiverse-dependent lookup (LC_SeasonHelper.GetPlayBookId); without it the shop's left tab crashes in
/// LC_ShopHelper.GetEndTimeByType and the content stays empty.
/// <para>
/// The playbook is a server setting (ServerOptions.PlayBook, default 101), not MultiverseSchedule's dates.
/// This implementation qualifies the complete season path for playbook 101. Some 102/103 stage rows exist, but
/// their cross-playbook references and dependent content are not a supported clock; the schedule is not live state.
/// The first season starts on the server's opening day (ServerOptions.SeasonStart, else the first start, kept in the
/// database). Stage times and the break-relative tail of stage 101005 are resolved by SeasonClock.
/// A later cycle requires an explicit server-owned start: the config does not schedule the next one (inferred policy).
/// Area 1 is inferred. "!season &lt;playbook&gt;" switches the current cycle's playbook on next login.
/// </para>
/// </summary>
public sealed class SeasonService : IGameTicker
{
    public const uint SeasonSync = 5021, TimeInfoNotify = 82707, StateChangeNotify = 82708;

    /// <summary>Activity owners invalidate their deadlines after a committed administrative clock change.</summary>
    public List<Action> ScheduleChanged { get; } = new();

    /// <summary>Legacy identity-only fallback when this playbook has no supported stage clock.</summary>
    private const long SeasonLengthSeconds = 999_999_999;

    private readonly GameData _data;
    private readonly AccountStore _accounts;
    private int _playBook;
    private int _seasonId;
    private readonly Dictionary<int, Season> _history;
    private readonly TimeProvider _clock;
    private readonly GameLoop _loop;
    private readonly SessionRegistry _sessions;
    private readonly ILogger<SeasonService> _log;
    private SeasonTimeInfoNotify? _timing;
    // Stage G: the clock of a playbook is a pure function of the season start and the tables, yet logins and activity
    // windows rebuilt it each time (290 MB per 500 logins). Shared and read-only; cleared with timing.
    private readonly Dictionary<int, SeasonTimeInfoNotify> _clocks = new();

    /// <summary>The playbook's stage clock for the current season start. Shared: callers must not modify it.</summary>
    internal SeasonTimeInfoNotify Clock(int book)
    {
        lock (_clocks)
        {
            if (!_clocks.TryGetValue(book, out var built)) _clocks[book] = built = new SeasonClock(_data, Start, book, Zone).Build();
            return built;
        }
    }
    private SeasonTimeInfoNotify Shared(int book) => book == 101 ? Clock(101) : Empty;
    private static readonly SeasonTimeInfoNotify Empty = new();
    private TimeSpan _lastTick;
    private bool _checking;
    private TimeSpan _checkingSince;
    internal Action? TickBatchCompletedForTest { get; set; }

    public sealed record Season(int Id, int PlayBook, DateTime Start, DateTime End);

    public SeasonService(GameData data, IOptions<ServerOptions> options, AccountStore accounts,
        TimeProvider clock, GameLoop loop, SessionRegistry sessions, ILogger<SeasonService> log)
    {
        this._data = data;
        this._accounts = accounts;
        this._clock = clock;
        this._loop = loop;
        this._sessions = sessions;
        this._log = log;
        OpenContent = options.Value.OpenContent;
        _playBook = int.TryParse(Get("playbook"), CultureInfo.InvariantCulture, out int saved) ? saved : options.Value.PlayBook;
        _seasonId = int.TryParse(Get("season_id"), CultureInfo.InvariantCulture, out int savedId) && savedId > 0 ? savedId : 1;
        _history = JsonSerializer.Deserialize<Dictionary<int, Season>>(Get("season_history") ?? "{}") ?? new();
        Start = _seasonId > 1 && DateTime.TryParseExact(Get("season_start"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var rolled) ? rolled : options.Value.SeasonStart is { Length: > 0 } configured
            ? DateTime.ParseExact(configured, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None)
            : FirstStart();
    }

    /// <summary>
    /// Midnight of the season's first day, a realm civil date (Kind Unspecified) in <see cref="Zone"/>. Never pass it to
    /// <c>new DateTimeOffset(DateTime)</c>: that reads it in the host's zone, not the realm's; use <see cref="StartTime"/>.
    /// </summary>
    public DateTime Start { get; private set; }

    /// <summary>The realm time zone: the injected clock's, as every daily reset and activity window uses.</summary>
    public TimeZoneInfo Zone => _clock.LocalTimeZone;

    /// <summary>Unix seconds of the season start (realm midnight).</summary>
    public long StartTime => Unix(Start);

    /// <summary>Unix seconds of a realm civil time; a Utc or Local DateTime (old persisted history) keeps its instant.</summary>
    public long Unix(DateTime time) => Unix(time, Zone);

    public static long Unix(DateTime time, TimeZoneInfo zone) => time.Kind == DateTimeKind.Unspecified
        ? new DateTimeOffset(time, zone.GetUtcOffset(time)).ToUnixTimeSeconds() : new DateTimeOffset(time).ToUnixTimeSeconds();

    /// <summary>The realm civil time (Kind Unspecified) of Unix seconds.</summary>
    public static DateTime Civil(long seconds, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(seconds), zone).DateTime;

    /// <summary>The "everything open" policy; every server-day and season gate asks it.</summary>
    public OpenContentOptions OpenContent { get; }
    public int SeasonId => _seasonId;
    public IReadOnlyDictionary<int, Season> History => _history;

    public int PlayBook => _playBook;

    public Season Current() => Current(_playBook);

    private Season Current(int book) => new(_seasonId, book, Start, Civil(
        Shared(book).StageTimeInfo.Values.Select(s => s.EndTime).DefaultIfEmpty(StartTime + SeasonLengthSeconds).Max(), Zone));

    /// <summary>Only playbook 101's stage semantics are qualified by this implementation.</summary>
    public SeasonTimeInfoNotify Times() => Times(_playBook);

    private SeasonTimeInfoNotify Times(int book) => book == 101
        ? (_timing ??= new SeasonClock(_data, Start, book, Zone).Build()).Clone() : new SeasonTimeInfoNotify();

    public long StageId => StageAt(_clock.GetUtcNow().ToUnixTimeSeconds(), _playBook);

    /// <summary>Intervals include their start and exclude their end (inferred boundary convention).</summary>
    public long StageAt(long unixSeconds) => StageAt(unixSeconds, _playBook);

    private long StageAt(long unixSeconds, int book) => Shared(book).StageTimeInfo.Values
        .FirstOrDefault(s => s.StartTime <= unixSeconds && unixSeconds < s.EndTime)?.StageId ?? 0;

    /// <summary>The playbooks the client has a MultiverseConfig row for.</summary>
    public IEnumerable<int> PlayBooks => _data.Table("MultiverseConfig").Values.Select(r => (int)GameData.Int(r, "_iID")).Order();

    /// <summary>GM: switch the live playbook (kept across restarts); players see it after their next login.</summary>
    public string SetPlayBook(int value)
    {
        if (!PlayBooks.Contains(value)) return $"No playbook {value}; the client knows {string.Join(", ", PlayBooks)}.";
        _playBook = value;
        _timing = null; lock (_clocks) _clocks.Clear();
        Set("playbook", value.ToString(CultureInfo.InvariantCulture));
        foreach (var changed in ScheduleChanged) changed();
        return $"Playbook {value} from the next login.";
    }

    /// <summary>GM path: persist the clock setting before publishing it to active owners.</summary>
    public async Task<string> SetPlayBookAsync(int value)
    {
        if (!_loop.IsOnLoop) return await _loop.InvokeAsync(() => SetPlayBookAsync(value));
        using var scope = _loop.Owners is { } owners
            ? await owners.EnterAsync("season/set-playbook", [Ownership.ReservationKey.Realm]) : null;
        if (!PlayBooks.Contains(value)) return $"No playbook {value}; the client knows {string.Join(", ", PlayBooks)}.";
        var transaction = _accounts.NewTransaction();
        transaction.Add(new SeasonStateWritePlan("playbook", value.ToString(CultureInfo.InvariantCulture)));
        await transaction.CommitAsync(_accounts, "season/set-playbook", _ =>
        {
            _playBook = value;
            _timing = null; lock (_clocks) _clocks.Clear();
            foreach (var changed in ScheduleChanged) changed();
        });
        return $"Playbook {value} from the next login.";
    }

    /// <summary>
    /// Explicit, persisted rollover. (inferred) No configured next cycle exists: 101005 has a 999999999-second
    /// off-season tail. The caller supplies a real new start after the current break; active sessions finish their
    /// captured cycle and must log in again before receiving the new page/identity.
    /// </summary>
    public string AdvanceCycle(int book, DateTime start)
    {
        if (!PlayBooks.Contains(book)) return $"No playbook {book}.";
        if (_playBook != 101) return "Current playbook has no supported season clock/content.";
        if (book != 101) return $"Playbook {book} has no supported season clock/content.";
        if (start.TimeOfDay != TimeSpan.Zero || start <= Start || start > _clock.GetLocalNow().Date)
            return "New season start must be a past or current local midnight after the current start.";
        long breakTime = Times().SeasonBreakStartTime;
        if (_clock.GetUtcNow().ToUnixTimeSeconds() < breakTime)
            return "Current season has not reached its break.";
        if (Unix(start) < breakTime)
            return "New season start cannot precede the current season break.";
        if (_seasonId == int.MaxValue) return "Season ID exhausted.";
        // The operator-supplied next start closes the otherwise open 101005 tail (inferred historical boundary).
        var previous = Current() with { End = start };
        var nextHistory = new Dictionary<int, Season>(_history) { [previous.Id] = previous };
        var transaction = _accounts.NewTransaction();
        transaction.Add(new SeasonStateWritePlan("season_history", JsonSerializer.Serialize(nextHistory)));
        transaction.Add(new SeasonStateWritePlan("season_id", (_seasonId + 1).ToString(CultureInfo.InvariantCulture)));
        transaction.Add(new SeasonStateWritePlan("season_start", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        transaction.Add(new SeasonStateWritePlan("playbook", book.ToString(CultureInfo.InvariantCulture)));
        transaction.Commit(_accounts, "season/advance-cycle");
        _history.Clear();
        foreach (var (id, value) in nextHistory) _history[id] = value;
        _seasonId++;
        _playBook = book;
        Start = start;
        _timing = null; lock (_clocks) _clocks.Clear();
        foreach (var changed in ScheduleChanged) changed();
        return $"Season {_seasonId}, playbook {book}, from next login.";
    }

    /// <summary>GM path: publish a new epoch only after all four server-state parts commit.</summary>
    public async Task<string> AdvanceCycleAsync(int book, DateTime start)
    {
        if (!_loop.IsOnLoop) return await _loop.InvokeAsync(() => AdvanceCycleAsync(book, start));
        using var scope = _loop.Owners is { } owners
            ? await owners.EnterAsync("season/advance-cycle", [Ownership.ReservationKey.Realm]) : null;
        if (!PlayBooks.Contains(book)) return $"No playbook {book}.";
        if (_playBook != 101) return "Current playbook has no supported season clock/content.";
        if (book != 101) return $"Playbook {book} has no supported season clock/content.";
        if (start.TimeOfDay != TimeSpan.Zero || start <= Start || start > _clock.GetLocalNow().Date)
            return "New season start must be a past or current local midnight after the current start.";
        long breakTime = Times().SeasonBreakStartTime;
        if (_clock.GetUtcNow().ToUnixTimeSeconds() < breakTime)
            return "Current season has not reached its break.";
        if (Unix(start) < breakTime)
            return "New season start cannot precede the current season break.";
        if (_seasonId == int.MaxValue) return "Season ID exhausted.";
        var previous = Current() with { End = start };
        var nextHistory = new Dictionary<int, Season>(_history) { [previous.Id] = previous };
        var transaction = _accounts.NewTransaction();
        transaction.Add(new SeasonStateWritePlan("season_history", JsonSerializer.Serialize(nextHistory)));
        transaction.Add(new SeasonStateWritePlan("season_id", (_seasonId + 1).ToString(CultureInfo.InvariantCulture)));
        transaction.Add(new SeasonStateWritePlan("season_start", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        transaction.Add(new SeasonStateWritePlan("playbook", book.ToString(CultureInfo.InvariantCulture)));
        await transaction.CommitAsync(_accounts, "season/advance-cycle", _ =>
        {
            _history.Clear();
            foreach (var (id, value) in nextHistory) _history[id] = value;
            _seasonId++;
            _playBook = book;
            Start = start;
            _timing = null; lock (_clocks) _clocks.Clear();
            foreach (var changed in ScheduleChanged) changed();
        });
        return $"Season {_seasonId}, playbook {book}, from next login.";
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"Season {_seasonId}, playbook {_playBook}, since {Start:yyyy-MM-dd}.");

    /// <summary>Capture the login's playbook before its first stat refresh; GM changes apply on the next login.</summary>
    public void Load(GameSession session) => session.SeasonState = State(_playBook);

    public async Task LoginAsync(GameSession session)
    {
        if (session.SeasonState == null) Load(session);
        var state = State((int)session.SeasonState!.PlayBookId);
        await SendClockAsync(session, state);
        session.SeasonState = state;
    }

    private SeasonState State(int book) => new() { SeasonId = _seasonId, PlayBookId = book, StageId = StageAt(_clock.GetUtcNow().ToUnixTimeSeconds(), book) };

    private async Task SendClockAsync(GameSession session, SeasonState state)
    {
        var season = Current((int)state.PlayBookId);
        var times = Times(season.PlayBook);
        var info = new DBSeasonInfo
        {
            Current = new SeasonInfo
            {
                SeasonId = season.Id, PlayBookId = season.PlayBook, SeasonAreaId = 1,
                StartTime = Unix(season.Start), EndTime = Unix(season.End),
                BreakTime = times.SeasonBreakStartTime, StageId = state.StageId,
            },
        };
        foreach (var (id, previous) in _history)
            info.History[id] = new SeasonInfo
            {
                SeasonId = id, PlayBookId = previous.PlayBook, SeasonAreaId = 1,
                StartTime = Unix(previous.Start), EndTime = Unix(previous.End)
            };
        await CharacterService.SyncAsync(session, SeasonSync, info);
        if (times.StageTimeInfo.Count > 0) await session.SendGameAsync(TimeInfoNotify, times);
    }

    /// <summary>82707 then 82708 on a real transition, and 5021.current.stageId for protocol consumers.</summary>
    public async Task ReconcileAsync(GameSession session)
    {
        if (session.Role == null || session.SeasonState == null) return;
        using var guard = await CharacterService.Lock(session);
        if (session.Role == null || session.SeasonState == null) return;
        var previous = session.SeasonState;
        if (previous.SeasonId != _seasonId) return; // Next login will migrate 5022 before exposing this cycle.
        // Keep this login's playbook and continue its clock after a GM change to the next-login setting.
        var current = State((int)previous.PlayBookId);
        if (previous.Equals(current)) return;
        await SendClockAsync(session, current);
        await session.SendGameAsync(StateChangeNotify, new SeasonStateChangeNotify { OldSeasonState = previous.Clone(), NewSeasonState = current.Clone() });
        session.SeasonState = current;
    }

    /// <summary>Check wall-clock boundaries once a second on the existing game loop; no client request is required.</summary>
    private long _checkingBatch;
    public void Tick(GameTime time)
    {
        // A batch whose operation was neither run nor discarded (an admission fault) cannot stall the clock for good.
        if (_checking && time.Now - _checkingSince < TimeSpan.FromSeconds(10) || time.Now - _lastTick < TimeSpan.FromSeconds(1)) return;
        _lastTick = time.Now;
        // C/D 16: only roles whose season state moved reconcile, each as its own role-owned operation. One Realm
        // operation for all sessions every second waited behind owned work for up to 16 s (S3).
        var changed = _sessions.All().Where(session => session.Role != null && session.SeasonState is { } state
            && state.SeasonId == _seasonId && !state.Equals(State((int)state.PlayBookId))).ToArray();
        if (changed.Length == 0) { TickBatchCompletedForTest?.Invoke(); return; }
        _checking = true;
        _checkingSince = time.Now;
        int remaining = changed.Length;
        // Only the current batch clears the flag: a batch overtaken by the 10 s fallback must not end its successor.
        long batch = ++_checkingBatch;
        void Done() { if (--remaining == 0 && batch == _checkingBatch) { _checking = false; TickBatchCompletedForTest?.Invoke(); } }
        foreach (var session in changed)
            _loop.PostOwned(session, async () =>
            {
                try { await ReconcileAsync(session); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
                { _log.LogDebug(e, "Season transition skipped for closed session"); }
                finally { Done(); }
            }, "season reconcile", discarded: Done);
    }

    private DateTime FirstStart()
    {
        if (DateTime.TryParseExact(Get("season_start"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var saved)) return saved;
        var today = _clock.GetLocalNow().Date;
        Set("season_start", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return today;
    }

    private string? Get(string key) => _accounts.ReadSync<string?>(new SeasonStateReadPlan(key));

    private void Set(string key, string value) => _accounts.WriteLegacySync<int>(new SeasonStateWritePlan(key, value));
}
