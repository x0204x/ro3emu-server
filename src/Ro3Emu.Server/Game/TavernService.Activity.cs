using System.Globalization;
using System.Text;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A role's own Tavern counters (5043 DBBarRoomInfo, role blob "barroom") and the GM panel's Tavern ops. The client
/// reads only barRoomId from 5043 (LC_BarRoomHelper.OnGetBarRoomID: a room joins its chat and voice again); the rest
/// mirrors what the server counts for the daily quest (106010), Star Power (106008) and active days (106009).
/// </summary>
public sealed partial class TavernService
{
    /// <summary>Role load: the counters, rolled to today and this week (the quest's 106010 and achievement 106008 read
    /// them during the login's settlement).</summary>
    public void Load(GameSession s)
    {
        var saved = _roles.LoadBarRoom(s.Role!.Id);
        var state = saved is { Length: > 0 } ? JsonSerializer.Deserialize<RoleState>(saved) ?? new() : ImportLegacy(s);
        s.Tavern = Rolled(state);
    }

    /// <summary>只轉入同一天已提交的秒數；舊房間／租約與額外飲料不移植，舊日待領仍由相容層結算。</summary>
    private RoleState ImportLegacy(GameSession s)
    {
        long day = DayStartMs();
        var legacy = s.LegacyTavern;
        int seconds = legacy?.DayEpoch == day / 1000 ? (int)Math.Clamp(legacy.ActiveMs / 1000, 0, int.MaxValue) : 0;
        return new RoleState
        {
            DayStartMs = day, WeekStartMs = PersonalService.WeekStartMs(_clock),
            TodaySeconds = seconds, TodayComplete = seconds >= ActSeconds(),
        };
    }

    /// <summary>Login: 5043 with the room the role is still in (a relog within <see cref="OfflineGraceMs"/>), so the
    /// client rejoins its chat and voice.</summary>
    public async Task LoginAsync(GameSession s)
    {
        if (s.Tavern == null) Load(s);
        // 新欄位一旦寫入，重連即讀取新版，避免把舊秒數重複匯入。
        if (_roles.LoadBarRoom(s.Role!.Id) is not { Length: > 0 })
            await CommitAsync(s, s.Tavern!, "tavern/legacy-import");
        lock (_sync)
            if (_roomOf.TryGetValue(s.Role!.Id, out long guid) && _rooms[guid].Members.TryGetValue(s.Role.Id, out var member))
                member.OfflineSinceMs = 0;
        await SyncAsync(s);
        await PayOwedDrinkAsync(s);
    }

    /// <summary>Leaving the game: the time spent in a room so far is written; the place in the room is kept for
    /// <see cref="OfflineGraceMs"/> (the sweep then removes it).</summary>
    public async Task LeaveRoleAsync(GameSession s)
    {
        if (s.Role == null || s.Tavern == null) return;
        await FlushNowAsync(s);
    }

    /// <summary>5043 from the role's counters and the room it is in. beginBarRoomTime is (inferred) when it entered that
    /// room, unix seconds; inBarRoomTime includes the seconds not yet written.</summary>
    private async Task SyncAsync(GameSession s)
    {
        if (s.Role == null || s.Tavern == null) return;
        var state = Rolled(s.Tavern);
        var info = new DBBarRoomInfo
        {
            Popularity = state.Popularity, ActDays = state.ActDays, IsToDayActComplete = state.TodayComplete,
            WeekPopularity = state.WeekPopularity,
        };
        lock (_sync)
        {
            info.InBarRoomTime = AddClamped(state.TodaySeconds, (long)_pendingSeconds.GetValueOrDefault(s.Role.Id));
            if (_roomOf.TryGetValue(s.Role.Id, out long guid) && _rooms[guid].Members.TryGetValue(s.Role.Id, out var member))
            {
                info.BarRoomId = guid;
                info.BeginBarRoomTime = member.JoinSec;
            }
        }
        await CharacterService.SyncAsync(s, Sync, info);
    }

    /// <summary>Posts a write of the role's pending time on its owner (once per role at a time).</summary>
    private void QueueFlush(GameSession s)
    {
        long role = s.Role!.Id;
        lock (_sync) if (!_flushing.Add(role)) return;
        _loop.PostOwned(s, async () =>
        {
            try
            {
                if (!await FlushNowAsync(s)) return;
                await PayOwedDrinkAsync(s);
                _tasks.OnTavernStay(s);
            }
            finally { lock (_sync) _flushing.Remove(role); }
        }, "tavern stay", discarded: () => { lock (_sync) _flushing.Remove(role); });
    }

    /// <summary>
    /// Writes the role's pending seconds into today's stay; the day the stay first reaches the daily quest's 1200 s
    /// is (inferred) an active day (actDays, isToDayActComplete). Returns whether anything was written.
    /// </summary>
    private async Task<bool> FlushNowAsync(GameSession s)
    {
        if (s.Role == null || s.Tavern == null) return false;
        int seconds;
        lock (_sync)
        {
            seconds = (int)_pendingSeconds.GetValueOrDefault(s.Role.Id);
            if (seconds <= 0) return false;
            _pendingSeconds[s.Role.Id] -= seconds;
            if (!_roomOf.ContainsKey(s.Role.Id) && _pendingSeconds[s.Role.Id] < 1) _pendingSeconds.Remove(s.Role.Id);
        }
        var next = Rolled(s.Tavern).Clone();
        next.TodaySeconds = AddClamped(next.TodaySeconds, seconds);
        CompleteDay(next);
        try { await CommitAsync(s, next, "tavern/stay"); }
        // A full queue wrote nothing: the seconds go back, or the handover's retry finds none to write.
        catch (Exception e) when (Accounts.Persistence.QueueFullPatience.Refused(e))
        {
            lock (_sync) _pendingSeconds[s.Role.Id] = _pendingSeconds.GetValueOrDefault(s.Role.Id) + seconds;
            throw;
        }
        return true;
    }

    /// <summary>Tests: time in a room not written yet, without waiting for the sweep to count it.</summary>
    internal void AddStayForTest(long role, double seconds)
    {
        lock (_sync) _pendingSeconds[role] = _pendingSeconds.GetValueOrDefault(role) + seconds;
    }

    private void CompleteDay(RoleState state)
    {
        if (state.TodayComplete || state.TodaySeconds < ActSeconds()) return;
        state.TodayComplete = true;
        state.ActDays = AddClamped(state.ActDays, 1);
        if (_options.Value.TavernDayDrinks) state.DrinkOwed = state.ActDays % 7 == 0 ? GrapeWine : state.ActDays % 3 == 0 ? CraftBrew : Gingerbread;
    }

    /// <summary>
    /// (inferred) The drink an active day earned (<see cref="ServerOptions.TavernDayDrinks"/>): the tables give only Hot
    /// Cocoa a source (quest 2000's award), so the higher drinks come one per active day, Grape Wine every 7th, Craft
    /// Brew every 3rd, Gingerbread otherwise. Paid on the owner after the day is written (or at the next login when the
    /// day ended on a logout), under an event/tavern-day receipt whose source is the day.
    /// </summary>
    private async Task PayOwedDrinkAsync(GameSession s)
    {
        if (s.Role == null || s.Tavern is not { DrinkOwed: > 0 } state) return;
        int drink = state.DrinkOwed;
        var next = state.Clone();
        next.DrinkOwed = 0;
        byte[] saved = JsonSerializer.SerializeToUtf8Bytes(next);
        string source = $"tavern/day/{s.Role.Id}/{state.DayStartMs}/{state.ActDays}";
        await _character.ExchangeAsync(s, [], [(drink, 1)], persist: tx =>
        {
            tx.RoleBlob(s.Role.Id, "barroom", saved);
            tx.WithEventEconomy(EventEconomy.ForRole(_data, s, "tavern-day", source, [], [new AwardGrant(drink, 1)],
                new { Drink = drink, state.ActDays }));
        }, committed: () => s.Tavern = next);
    }

    /// <summary>Star Power from a gift, on the receiver's owner: total and this week's (the week as friend-gift
    /// popularity's, PersonalService.WeekStartMs).</summary>
    private async Task CreditStarAsync(GameSession s, int star)
    {
        if (s.Role == null || s.Tavern == null) return;
        var next = Rolled(s.Tavern).Clone();
        next.Popularity = AddClamped(next.Popularity, star);
        next.WeekPopularity = AddClamped(next.WeekPopularity, star);
        await CommitAsync(s, next, "tavern/star");
        foreach (var handler in PopularityChanged) handler(s);
    }

    private async Task CommitAsync(GameSession s, RoleState next, string label)
    {
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(s.Role!.Id, "barroom", JsonSerializer.SerializeToUtf8Bytes(next));
        await _roles.CommitAsync(transaction, label, _ => s.Tavern = next);
        await SyncAsync(s);
    }

    /// <summary>The state with today's and this week's counters started over when their boundary has passed.</summary>
    private RoleState Rolled(RoleState state)
    {
        long day = DayStartMs(), week = PersonalService.WeekStartMs(_clock);
        if (state.DayStartMs == day && state.WeekStartMs == week) return state;
        var next = state.Clone();
        if (next.DayStartMs != day) (next.DayStartMs, next.TodaySeconds, next.TodayComplete) = (day, 0, false);
        if (next.WeekStartMs != week) (next.WeekStartMs, next.WeekPopularity) = (week, 0);
        return next;
    }

    /// <summary>(inferred) The Tavern day starts at 05:00 realm time, as every daily reset.</summary>
    private long DayStartMs()
    {
        var local = _clock.GetLocalNow();
        var day = local.AddHours(-5).Date.AddHours(5);
        return new DateTimeOffset(day, local.Offset).ToUnixTimeMilliseconds();
    }

    /// <summary>Today's seconds in a Tavern room, written ones and pending ones (the quest's 106010 reads it).</summary>
    public long TodaySeconds(GameSession s)
    {
        if (s.Role == null || s.Tavern == null) return 0;
        // 跨日舊版待領先在原日期結算，不能被新版被動目標的零秒覆寫。
        long legacy = s.LegacyTavern is { } meter && meter.DayEpoch == s.Daily?.DailyEpoch ? meter.ActiveMs / 1000 : 0;
        lock (_sync) return Math.Max(legacy, Rolled(s.Tavern).TodaySeconds + (long)_pendingSeconds.GetValueOrDefault(s.Role.Id));
    }

    /// <summary>Star Power received in total (achievement condition 106008 BarroomPopularity); null before login.</summary>
    public long? Popularity(GameSession s) => s.Tavern?.Popularity;

    // ---- GM ----

    /// <summary>GmOpEnum values of the Tavern's GM panel ops (MG_Common).</summary>
    public const int GmPrint = 134, GmSetInfo = 136, GmSetTag = 137, GmAddStar = 138, GmAddQuests = 139, GmAddActDays = 140, GmAddStay = 141;

    /// <summary>GM panel ops 134 GmPrintBarrroom and 136-141 (GMConfig 1024-1029: GmSetBarroomInfo [room, heat, heat
    /// gain, head count], GMSetBarroomRedBag [room, tag 1-4, 0 add / 1 remove], GMAddBarroomPopularity [n],
    /// GMAddBarroomTaskFinishTimes [n], GMAddBarroomActDays [n], GMAddBarroomToDayStayTimes [seconds]).</summary>
    public async Task<string> GmOpAsync(GameSession s, int op, string[] args)
    {
        long Arg(int i) => i < args.Length && long.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
        switch (op)
        {
            case GmPrint: return List(s);
            case GmSetInfo: return SetRoom(Arg(0), args, Arg(1), Arg(2), Arg(3));
            case GmSetTag: return SetTag(Arg(0), (int)Arg(1), Arg(2) == 1);
            case GmAddStar: return await GmAddAsync(s, st => st.Popularity = AddClamped(st.Popularity, Arg(0)), star: (int)Arg(0));
            case GmAddQuests:
                foreach (var handler in TavernQuestsAdded) handler(s, (int)Math.Clamp(Arg(0), 0, int.MaxValue));
                return $"Tavern quest completions +{Arg(0)} (achievement 106007).";
            case GmAddActDays: return await GmAddAsync(s, st => st.ActDays = AddClamped(st.ActDays, Arg(0)));
            case GmAddStay: return await GmAddAsync(s, st => st.TodaySeconds = AddClamped(st.TodaySeconds, Arg(0)));
            default: return $"GM op {op} is no Tavern op.";
        }
    }

    /// <summary>GMAddBarroomTaskFinishTimes: the daily quest's completion count for 106007 (AchievementService).</summary>
    public List<Action<GameSession, int>> TavernQuestsAdded { get; } = [];

    private async Task<string> GmAddAsync(GameSession s, Action<RoleState> change, int star = 0)
    {
        if (s.Tavern == null) return "No Tavern state loaded.";
        var next = Rolled(s.Tavern).Clone();
        change(next);
        CompleteDay(next);
        if (star > 0) next.WeekPopularity = AddClamped(next.WeekPopularity, star);
        await CommitAsync(s, next, "tavern/gm");
        await PayOwedDrinkAsync(s);
        if (star > 0) foreach (var handler in PopularityChanged) handler(s);
        _tasks.OnTavernStay(s);
        return $"Tavern: Star Power {next.Popularity} (week {next.WeekPopularity}), active days {next.ActDays}, today {next.TodaySeconds} s.";
    }

    private string SetRoom(long guid, string[] args, long heat, long gain, long heads)
    {
        lock (_sync)
        {
            if (!_rooms.TryGetValue(guid, out var room)) return $"No Tavern room {guid}.";
            // Each parameter left empty keeps the room's value (GMConfig 1024's description).
            if (args.Length > 1 && args[1].Length > 0) room.Hot = room.LastHot = (int)Math.Clamp(heat, 0, int.MaxValue);
            if (args.Length > 2 && args[2].Length > 0) room.LastHotAdd = (int)Math.Clamp(gain, 0, int.MaxValue);
            if (args.Length > 3 && args[3].Length > 0) room.GmCount = (int)Math.Clamp(heads, 0, int.MaxValue);
            return $"Tavern room {guid}: heat {room.Hot}, gain {room.LastHotAdd}, extra head count {room.GmCount}.";
        }
    }

    private string SetTag(long guid, int tag, bool remove)
    {
        if (tag is < 1 or > 4) return "Tag 1 red envelope, 2 promote, 3 heat, 4 cheers (BarroomTagConfig).";
        var type = (RoomTagType)(tag - 1); // BarroomTagConfig ids are RoomTagType + 1 (the client adds one)
        lock (_sync)
        {
            if (!_rooms.TryGetValue(guid, out var room)) return $"No Tavern room {guid}.";
            if (remove) room.Tags.Remove(type);
            else room.Tags[type] = NowSec() + GmTagSeconds;
            return $"Tavern room {guid}: tag {type} {(remove ? "removed" : "set for a day")}.";
        }
    }

    private string List(GameSession s)
    {
        var text = new StringBuilder();
        lock (_sync)
        {
            var rooms = _roomOf.TryGetValue(s.Role!.Id, out long own) ? [_rooms[own]] : _rooms.Values.ToList();
            if (rooms.Count == 0) return "No Tavern room is open.";
            foreach (var room in rooms)
                text.Append(CultureInfo.InvariantCulture, $"{room.Guid} \"{room.Name}\" owner {room.Owner}, {room.Members.Count} member(s), ")
                    .Append(CultureInfo.InvariantCulture, $"{room.Mode} mode, list [{string.Join(",", room.Speakers)}], heat {room.Hot}")
                    .Append(room.MicClosed ? ", mics closed" : "").Append('\n');
        }
        return text.ToString().TrimEnd();
    }

    private async Task GmTavernAsync(GameSession s, string[] args) => await GmCommands.ReplyAsync(s, List(s));
}
