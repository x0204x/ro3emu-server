using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Legacy ActivityConfig transport (MG_Activity.proto). The exported novice event
/// 10000001 has an absolute 2024-06-01 to 2025-08-01 join window. Task completion is owned by TaskService;
/// a client upload cannot set taskIds or award inventory. The meaning of taskIds' map value as the
/// ActivityStageConfig stage number is (inferred), based on the novice widget using its maximum as lock day.
/// The same windows gate the Event Auction tabs: AuctionTabConfig 3001-3003 name an ActivityConfig id in
/// _KTypeParam, and UI_Sys_AuctionWidgetCtrl.CheckSubTab (lines 699-743) lists a tab only while
/// LC_ActManager holds that activity (LC_ActivityHelper.CheckActivityIsOpen, lines 154-173).
/// </summary>
public sealed class TimedEventService(GameData data, TimeProvider time, SessionRegistry sessions, GameLoop loop, SeasonService seasons) : IGameTicker
{
    public const uint StatusNotify = 43501, Common = 43502, Upload = 43503;
    internal bool IsOwnerRequest(uint message) => message is Common or Upload;
    private readonly GameData _data = data;
    private readonly TimeProvider _time = time;
    private readonly SessionRegistry _sessions = sessions;
    private readonly GameLoop _loop = loop;
    private readonly SeasonService _seasons = seasons;
    private long _lastSecond = -1;
    private readonly Dictionary<int, bool> _previous = new();
    // GM test aid (!actauction): an in-memory window per activity, never persisted; the Tick publishes its expiry.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DateTimeOffset Start, DateTimeOffset End)> _testWindows = new();

    /// <summary>The activity's join window holds now (real dates, the OpenContent repeat, or a GM test window).</summary>
    public bool IsActive(int id) => Active(id);

    /// <summary>The end of the window in force, or null when the activity has no configured window.</summary>
    public DateTimeOffset? WindowEnd(int id) => Window(id)?.End;

    /// <summary>GM test aid, on the game loop: opens the activity for <paramref name="minutes"/> (open) or drops the
    /// override (close), and publishes the new status at once (the Tick only looks once per clock second).</summary>
    public void SetTestWindow(int id, bool open, int minutes = 90)
    {
        if (open) _testWindows[id] = (_time.GetUtcNow(), _time.GetUtcNow().AddMinutes(minutes));
        else _testWindows.TryRemove(id, out _);
        bool active = Active(id);
        if (_previous.TryGetValue(id, out bool was) && was == active) return;
        _previous[id] = active;
        foreach (var s in _sessions.All().Where(s => s.Role != null && s.ActivitiesReady))
            _loop.PostOwned(s, () => NotifyAsync(s, id), "timed event test window");
    }

    public async Task LoginAsync(GameSession session)
    {
        foreach (var id in Ids()) await NotifyAsync(session, id);
    }

    public async Task CommonAsync(GameSession session, ActivityReq req)
    {
        using var scope = session.Role != null && _loop.Owners is { } owners
            ? await owners.EnterAsync("timed event common", [ReservationKey.Role(session.Role.Id)]) : null;
        if (session.Owner != null) scope?.Context.Pin(session.Owner);
        var response = new ActivityResp { ActivityId = req.ActivityId };
        int taskId = req.ReqParam?.NoviceReq?.TaskId ?? 0;
        int stage = StageFor(req.ActivityId, taskId);
        response.Result = session.Role == null || !session.ActivitiesReady || !Active(req.ActivityId) ? ErrorCodes.FeatureUnavailable
            : req.ReqParam?.NoviceReq == null || stage == 0 ? ErrorCodes.Parameter
            : session.Tasks == null || !TaskService.IsDelivered(session, taskId) ? ErrorCodes.TaskRequirements : 0;
        response.RespParam = new ActivityRespParam { NoviceResp = new NoviceActivityResp { Result = response.Result, TaskId = taskId } };
        await session.SendGameAsync(Common, response);
        if (response.Result == 0) await NotifyAsync(session, req.ActivityId);
    }

    /// <summary>43503: only the authenticated role's quest state is returned; playerId and taskIds in the request are untrusted.</summary>
    public async Task UploadAsync(GameSession session, ActivityDataUploadReq req)
    {
        using var scope = session.Role != null && _loop.Owners is { } owners
            ? await owners.EnterAsync("timed event upload", [ReservationKey.Role(session.Role.Id)]) : null;
        if (session.Owner != null) scope?.Context.Pin(session.Owner);
        int result = session.Role == null || !session.ActivitiesReady || !Active(req.ActivityId) ? ErrorCodes.FeatureUnavailable
            : req.PlayerId != session.Role.Id || req.UploadParam?.NoviceActivity == null ? ErrorCodes.Parameter : 0;
        var response = new ActivityDataUploadResp
        {
            Result = result, PlayerId = session.Role?.Id ?? 0, ActivityId = req.ActivityId,
            DownloadParam = new DBActivityParam { NoviceActivity = Progress(session, req.ActivityId) },
        };
        await session.SendGameAsync(Upload, response);
        if (result == 0) await NotifyAsync(session, req.ActivityId);
    }

    public void Tick(GameTime _)
    {
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (now == _lastSecond) return;
        _lastSecond = now;
        foreach (var id in Ids())
        {
            bool active = Active(id);
            if (_previous.TryGetValue(id, out bool was) && was == active) continue;
            _previous[id] = active;
            foreach (var s in _sessions.All().Where(s => s.Role != null && s.ActivitiesReady))
                _loop.PostOwned(s, () => NotifyAsync(s, id), "timed event window");
        }
    }

    private async Task NotifyAsync(GameSession session, int id)
    {
        if (Window(id) is not { } w) return;
        await session.SendGameAsync(StatusNotify, new ActivityDataNtf
        {
            ActivityId = id, ActivityStatus = Active(id) ? ActivityStatus.Start : ActivityStatus.Close,
            OpenTime = w.Start.ToUnixTimeMilliseconds(), CloseTime = w.End.ToUnixTimeMilliseconds(),
            ActivityParam = new ActivityParam { NoviceActivity = Progress(session, id) },
        });
    }

    private NoviceActivity Progress(GameSession session, int id)
    {
        var value = new NoviceActivity();
        foreach (var row in _data.Table("ActivityStageConfig").Values.Where(r => GameData.Int(r, "_iActivityID") == id))
        {
            int stage = (int)GameData.Int(row, "_iID");
            if (row.TryGetProperty("_kValue", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
                foreach (var pair in tasks.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;
                    int task = (int)(GameData.Number(pair[1]) ?? 0);
                    if (task > 0 && session.Tasks != null && _data.Row("TaskConfig", task) != null && TaskService.IsDelivered(session, task))
                        value.TaskIds[task] = stage;
                }
        }
        return value;
    }

    private int StageFor(int id, int taskId)
    {
        if (taskId <= 0) return 0;
        foreach (var row in _data.Table("ActivityStageConfig").Values.Where(r => GameData.Int(r, "_iActivityID") == id))
            if (row.TryGetProperty("_kValue", out var tasks) && tasks.ValueKind == JsonValueKind.Array
                && tasks.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2 && GameData.Number(p[1]) == taskId))
                return (int)GameData.Int(row, "_iID");
        return 0;
    }

    private bool Active(int id) => Window(id) is { } w && _time.GetUtcNow() >= w.Start && _time.GetUtcNow() < w.End;

    private (DateTimeOffset Start, DateTimeOffset End)? Window(int id)
    {
        if (_testWindows.TryGetValue(id, out var test) && _time.GetUtcNow() < test.End) return test;
        if (_data.Row("ActivityConfig", id) is not { } row) return null;
        var start = ReadDate(row, "_kJoinStartTime");
        var end = ReadDate(row, "_kJoinEndTime");
        if (start == null || !(end > start)) return null;
        // OpenContent (OpenContentOptions.PerpetualWindows): an absolute window that already passed repeats with its
        // own length, as ActivityClock.ResolveActivity does for NewActivityConfig tuples.
        var now = _time.GetUtcNow();
        if (_seasons.OpenContent.Enabled && _seasons.OpenContent.PerpetualWindows && end <= now)
        {
            var length = end.Value - start.Value;
            long cycle = (now - start.Value).Ticks / length.Ticks;
            return (start.Value + length * cycle, start.Value + length * (cycle + 1));
        }
        return (start.Value, end.Value);
    }

    private DateTimeOffset? ReadDate(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var pair) || pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) return null;
        string text = $"{(long)(GameData.Number(pair[0]) ?? 0):D8}{(long)(GameData.Number(pair[1]) ?? 0):D6}";
        // ActivityConfig dates are interpreted in the injected server civil timezone (inferred policy).
        return DateTime.TryParseExact(text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DateTimeOffset(date, _time.LocalTimeZone.GetUtcOffset(date)) : null;
    }

    private IEnumerable<int> Ids() => _data.Table("ActivityConfig").Values.Select(r => (int)GameData.Int(r, "_iID")).Where(id => id > 0);
}
