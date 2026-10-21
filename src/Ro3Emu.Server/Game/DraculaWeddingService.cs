using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Dracula's Bride / Wedding Crashers (DailyActivityConfig 3400, function 1328 "Dracula's Bride", MG_GuildDraculaWedding
/// 83640-83642). Stage 1 runs in the guild home (GuildHomeService's room): when
/// the event opens, a random eligible member present in the home becomes the bride (83640 opens the client's lottery,
/// LC_DunGeon_Guild.lua:2277-2297, which stops on brideEntity.uuid), 83641 names her, and the home plays story 3001
/// whose action 3000 makes every client enter Dracula's Lair (DungeonConfig 3010401) itself with 30400. Stage 2 is one
/// lair room per guild (DungeonService, waves from DungeonCatalog.Dracula): the tasks 9000100-9000110, Dracula, then
/// the key in one of the bushes; its finder unlocks the cage and is the rescuer. 83642 precedes the 30403 result,
/// whose success branch plays story 3004 with both entities (LC_DunGeon_Dracula.lua:167-185, 235-331); the rewards are
/// MailConfig 10208 (award 85501) to every participant and 10209 (85502) to the MVP, and quest target 210010.
/// Event state is per occurrence and in memory (inferred: a restart during the half hour lets the guild play again;
/// the reward letters are keyed by the occurrence, so they are never paid twice).
/// </summary>
public sealed class DraculaWeddingService : IGameTicker
{
    public const uint SelectBride = 83640, WeddingState = 83641, WeddingComplete = 83642;
    /// <summary>PublicParamConfig.DraculaDailyActConfigId.</summary>
    public const int Activity = 3400;
    public const long Lair = 3010401;
    private const uint SystemBroadcast = 1022;
    /// <summary>
    /// LC_DunGeon_VarDefine.lua:663-696: 1301 CLBridePlayerId, 1302 CLRescuerPlayerId (UI_Sys_SceneUIPanel's key-owner
    /// mark), 1305 CDraculaEndTime (ms; DungeonCountDownTimeStamp, the top countdown), 1306 CDraculaEventOpen
    /// (LC_DunGeon_Actions.lua:3536-3556, LC_MainUITemplateRule.lua:235). 1303 CStageShowTimestamp and 1304
    /// CFinalAnimID are never sent: no client data says what their stage show or final animation would be.
    /// </summary>
    private const int VarBride = 1301, VarRescuer = 1302, VarEndTime = 1305, VarOpen = 1306;
    /// <summary>The key a bush search gives (BuffConfig 4121301): the cage's Unlock asks for it (condition 7000039).</summary>
    private const int KeyBuff = 4121301;
    /// <summary>ChatConfig 6200107 "${1} found the key! Hurry and save the bride!".</summary>
    private const int KeyFoundChat = 6200107;
    private const int ParticipantMail = 10208, MvpMail = 10209;
    /// <summary>
    /// (inferred) The client's lottery (UI_Sys_ActivityEvening_DraculaWidget, a C# scroll of unknown length) runs before
    /// the story; ten seconds lets it land on the bride first.
    /// </summary>
    internal TimeSpan Lottery { get; set; } = TimeSpan.FromSeconds(10);

    private readonly GameData _data;
    private readonly GuildHomeService _home;
    private readonly DungeonService _dungeons;
    private readonly DungeonCatalog _catalog;
    private readonly TimedTodoSchedule _schedule;
    private readonly FunctionService _functions;
    private readonly TaskService _tasks;
    private readonly BuffService _buffs;
    private readonly MailService _mail;
    private readonly SessionRegistry _sessions;
    private readonly TimeProvider _clock;
    private readonly GameLoop _loop;
    private readonly ILogger<DraculaWeddingService> _log;
    /// <summary>The guild's current (or last) occurrence.</summary>
    private readonly Dictionary<long, Run> _runs = new();
    private long _lastSecond;
    private bool _sweeping;
    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    public DraculaWeddingService(GameData data, GuildHomeService home, DungeonService dungeons, DungeonCatalog catalog,
        TimedTodoSchedule schedule, FunctionService functions, TaskService tasks, BuffService buffs, MailService mail,
        SessionRegistry sessions, SceneService scene, GmCommands gm, TimeProvider clock, GameLoop loop,
        ILogger<DraculaWeddingService> log)
    {
        this._data = data; this._home = home; this._dungeons = dungeons; this._catalog = catalog; this._schedule = schedule;
        this._functions = functions; this._tasks = tasks; this._buffs = buffs; this._mail = mail; this._sessions = sessions;
        this._clock = clock; this._loop = loop; this._log = log;
        scene.Loaded.Add(LoadedAsync);
        tasks.ExtraTasks.Add(LairTasks);
        gm.Register("dracula", "open|close (test aid: Wedding Crashers opens now for 30 minutes, or closes)", TestWindowAsync);
    }

    /// <summary>One guild's occurrence of the event.</summary>
    internal sealed class Run
    {
        public required DraculaWeddingService Owner { get; init; }
        public required long Guild { get; init; }
        /// <summary>The occurrence (TimedTodoSchedule window), unix seconds.</summary>
        public required long Start { get; init; }
        public required long End { get; init; }
        public required long Bride { get; init; }
        /// <summary>The bride as she was chosen (RoleSessionService.PlayerEntity, uuid = role id).</summary>
        public required EntityBase BrideEntity { get; init; }
        /// <summary>The bush that hides the key this time (inferred: one of the four at random).</summary>
        public int KeyBush { get; set; }
        public long Rescuer { get; set; }
        public EntityBase? RescuerEntity { get; set; }
        public HashSet<int> Searched { get; } = new();
        public bool StoryStarted { get; set; }
        public bool Settled { get; set; }
        public bool Closed { get; set; }
        public DungeonService.Attempt? Lair { get; set; }
        public DungeonService.Attempt? Live => Lair is { Settled: false, Room.Closed: false } a ? a : null;
        public IEnumerable<DungeonVar> Vars() => [Var(VarBride, Bride), Var(VarRescuer, Rescuer), Var(VarEndTime, End * 1000),
            Var(VarOpen, 1)];
    }

    private static DungeonVar Var(int id, long value) => new() { VarId = id, Vi64 = { value } };

    /// <summary>
    /// The window, decided in this one place: TimedTodoSchedule.CurrentOrNext(3400). DailyActivityConfig 3400 is
    /// _iIsCustome 1 with _iTimeCtrlId 1079, and that pair is exactly what the client counts down to in the guild home
    /// (LC_DunGeon_Guild._getGuildActivityNextOpenTime, LC_DunGeon_Guild.lua:2884-2930): during the first four weeks
    /// LC_DailyActivityHelper.GetActivityWeeklyTimeList (DailyActivityCustomConfig: week 1 only, the day after the
    /// opening weekday, 20:30-21:00), afterwards TimeLimitConfig 1079 (row 93: week offsets 331200-333000 s, Thursday
    /// 20:00-20:30). _kDay [6] (Saturday) and the "opens Friday" text 11530200019 are display only: no client code
    /// schedules from them while _iTimeCtrlId is set, and TimedTodoSchedule prefers the control id too.
    /// </summary>
    private TimedTodoSchedule.Occurrence? Window() => _schedule.CurrentOrNext(Activity);

    /// <summary>A guild member who may take part (DailyActivity 3400 _iiLv 30, function 1328: server day 2 or later).</summary>
    private bool Eligible(GameSession s) => s.ActivitiesReady && s.Progress != null && _home.GuildId(s) != null
        && _data.Row("DailyActivityConfig", Activity) is { } row && s.Progress.Level >= GameData.Int(row, "_iiLv")
        && _functions.IsOpen(s, GameData.Int(row, "_iFunction"));

    /// <summary>The eligible members loaded inside the guild's home room.</summary>
    private GameSession[] InHome(InstanceService.Room room) => room.ActiveMembers.Select(m => m.Session)
        .Where(s => s.Role != null && s.InstanceId == room.Id && s.SceneEntry == _home.Scene && !s.SceneLoading && Eligible(s))
        .ToArray();

    private Run? Current(long guild) => _runs.TryGetValue(guild, out var run) && !run.Settled && !run.Closed && Now < run.End
        ? run : null;

    private GuildDraculaWeddingStateNtf State(Run run) => new()
    {
        BridePlayerId = run.Bride, BrideEntity = run.BrideEntity.Clone(),
        // The client never reads it (inferred: the lair's room id, as 32256's guildSceneId is the home's).
        Stage2SceneId = run.Live?.Room.Id ?? 0,
    };

    private static DungeonEventNotify VarsEvent(long scene, IEnumerable<DungeonVar> vars) => new()
    {
        DungeonEntry = scene, EventType = (DungeonEventType)4, StageNum = 1, LevelNum = 1, Vars = { vars },
    };

    /// <summary>
    /// LC_DunGeon_Actions.ClientPlayCutSceneStory(plot, true) through 30404 DungeonEventInvokeClientFunc
    /// (LC_DunGeon_Base.lua:1265-1290, two EventParams as LC_DunGeon_Base.lua:342-430 unpacks them): it calls the level's
    /// own PlayStory, which adds the bride to stories 3001 (home) and 3003 (lair). The plain story var 100020016 would
    /// go straight to LC_CutSceneManager and play them without her.
    /// </summary>
    private static DungeonEventNotify Story(long scene, long plot) => new()
    {
        DungeonEntry = scene, EventType = (DungeonEventType)6, StageNum = 1, LevelNum = 1,
        CliFuncname = "ClientPlayCutSceneStory",
        Params = { new EventParams { IntParams = { plot } }, new EventParams { BoolParams = { true } } },
    };

    /// <summary>PublicParamConfig.DraculaWeddingDynamicStoryIdArr {3001, 3003, 3004}: home, lair start, rescue.</summary>
    private long StoryId(int index) => _data.Table("PublicParamConfig").TryGetValue("DraculaWeddingDynamicStoryIdArr", out var ids)
        && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > index ? (long)(GameData.Number(ids[index]) ?? 0) : 0;

    private async Task SendQuietAsync(GameSession s, uint id, Google.Protobuf.IMessage msg)
    {
        try { await s.SendGameAsync(id, msg); }
        catch (Exception e) { _log.LogWarning(e, "Wedding Crashers message {Message} failed for {Role}", id, s.Role?.Id); }
    }

    // ---- stage 1: the guild home ----

    public void Tick(GameTime _)
    {
        long now = Now;
        if (_sweeping || now == _lastSecond) return;
        _lastSecond = now;
        var window = Window();
        bool open = window?.Contains(now) == true;
        var start = open ? _home.Rooms.Where(r => (!_runs.TryGetValue(r.Guild, out var run) || run.Start != window!.Start)
            && InHome(r.Room).Length > 0).ToArray() : [];
        var ended = _runs.Values.Where(r => !r.Closed && (now >= r.End || !open)).ToArray();
        if (start.Length == 0 && ended.Length == 0) return;
        _sweeping = true;
        _loop.PostLegacy(async () =>
        {
            try
            {
                foreach (var run in ended) await CloseAsync(run);
                foreach (var (guild, room) in start)
                    if (Window() is { } current && current.Contains(Now) && !room.Closed && _runs.GetValueOrDefault(guild)?.Start != current.Start)
                        await StartAsync(guild, room, current);
            }
            finally { _sweeping = false; }
        }, "dracula wedding sweep");
    }

    private async Task StartAsync(long guild, InstanceService.Room room, TimedTodoSchedule.Occurrence window)
    {
        var present = InHome(room);
        if (present.Length == 0) return;
        // (inferred) The bride is drawn among the eligible members present in the home when it opens (or, in a home
        // still empty then, when the first one arrives during the half hour).
        var bride = present[Random.Shared.Next(present.Length)];
        var entity = RoleSessionService.PlayerEntity(bride);
        entity.PendantInfo.Clear(); // LC_DunGeon_Guild.lua:2280 clears it before storing the entity
        var run = new Run
        {
            Owner = this, Guild = guild, Start = window.Start, End = window.End, Bride = bride.Role!.Id, BrideEntity = entity,
            KeyBush = DungeonCatalog.DraculaBushes[Random.Shared.Next(DungeonCatalog.DraculaBushes.Length)],
        };
        _runs[guild] = run;
        _log.LogInformation("Wedding Crashers: guild {Guild} opens with bride {Bride} ({Present} present)", guild, run.Bride, present.Length);
        var select = new GuildDraculaSelectBrideNtf { BridePlayerId = run.Bride, BrideEntity = run.BrideEntity.Clone() };
        foreach (var s in present)
        {
            await SendQuietAsync(s, DungeonService.Event, VarsEvent(_home.Scene, [Var(VarEndTime, run.End * 1000), Var(VarOpen, 1)]));
            await SendQuietAsync(s, SelectBride, select);
            await SendQuietAsync(s, WeddingState, State(run));
        }
        _loop.ScheduleLegacy(Lottery, () => StoryAsync(run), "dracula wedding story");
    }

    /// <summary>Story 3001 in the home; its end sends each client into the lair (PlotInterpretation 3001 action 3000).</summary>
    private async Task StoryAsync(Run run)
    {
        if (Current(run.Guild) != run || _home.RoomOf(run.Guild) is not { } room) return;
        run.StoryStarted = true;
        foreach (var s in InHome(room)) await SendQuietAsync(s, DungeonService.Event, Story(_home.Scene, StoryId(0)));
    }

    /// <summary>
    /// (inferred) A member who arrives in the home while the guild's occurrence runs gets its state, and once the story
    /// has started, the story too, so that a late member (or one who left the lair) can still go in.
    /// </summary>
    private async Task LoadedAsync(GameSession s)
    {
        if (s.SceneEntry == Lair && s.DungeonAttempt is { Dracula: { } lair } a && s.InstanceId == a.Room.Id)
        {
            await SendQuietAsync(s, WeddingState, State(lair));
            await _tasks.SyncTasksAsync(s);
            return;
        }
        if (!_home.InHome(s, out long guild) || Current(guild) is not { } run || !Eligible(s)) return;
        await SendQuietAsync(s, DungeonService.Event, VarsEvent(_home.Scene, [Var(VarEndTime, run.End * 1000), Var(VarOpen, 1)]));
        await SendQuietAsync(s, WeddingState, State(run));
        if (run.StoryStarted) await SendQuietAsync(s, DungeonService.Event, Story(_home.Scene, StoryId(0)));
    }

    /// <summary>The occurrence is over: a running lair fails, and the home's event state is cleared (inferred).</summary>
    private async Task CloseAsync(Run run)
    {
        if (run.Closed) return;
        run.Closed = true;
        if (run.Live is { } lair) await _dungeons.StopDraculaAsync(lair);
        if (_home.RoomOf(run.Guild) is { } room)
            foreach (var s in InHome(room))
                await SendQuietAsync(s, DungeonService.Event, VarsEvent(_home.Scene, [Var(VarEndTime, 0), Var(VarOpen, 0)]));
        // Older occurrences are only kept to refuse a second start of the same one.
        foreach (long guild in _runs.Where(p => p.Value.Closed && p.Value.End <= Now - 86400).Select(p => p.Key).ToArray())
            _runs.Remove(guild);
    }

    // ---- stage 2: Dracula's Lair ----

    /// <summary>30400 for the lair: only from the guild's own home, while its occurrence runs.</summary>
    public async Task<bool> TryEnterAsync(GameSession s, EnterDungeonReq req)
    {
        if (req.SceneEntry != Lair) return false;
        long guild = _home.GuildId(s) ?? 0;
        var run = guild == 0 ? null : Current(guild);
        var live = run?.Live;
        int code = guild == 0 ? ErrorCodes.NotInGuild : !Eligible(s) ? ErrorCodes.FeatureUnavailable
            : run == null || Window()?.Contains(Now) != true ? ErrorCodes.ActivityPeriod
            : req.InitData?.CalculateSize() > 0 || req.Param?.CalculateSize() > 0 ? ErrorCodes.Parameter
            : s.SceneLoading ? ErrorCodes.SwitchingScenes : s.Dead ? ErrorCodes.FeatureUnavailable
            // (inferred) The way in is the home's story; nobody walks in from elsewhere.
            : !_home.InHome(s, out _) ? ErrorCodes.DungeonBusy
            : live != null && live.Room.ActiveMembers.Count >= live.Plan.MaxPlayers ? ErrorCodes.DungeonTeamSize : 0;
        var plan = code == 0 ? _catalog.Dracula(Lair) : null;
        if (code == 0 && plan == null) code = ErrorCodes.DungeonDataMissing;
        await s.SendGameAsync(DungeonService.Enter, new EnterDungeonRsp { Result = code });
        if (code != 0) return true;
        var attempt = await _dungeons.EnterDraculaAsync(s, plan!, run!, live);
        if (attempt != null) run!.Lair = attempt;
        return true;
    }

    /// <summary>The lair's dungeon task for the wave being played (TaskConfig 9000100-9000110, in chain order).</summary>
    private long[] LairTaskIds() => _data.Table("TaskConfig").Values.Where(r => GameData.Int(r, "_iDungeonID") == Lair)
        .Select(r => GameData.Int(r, "_iTaskID")).Order().ToArray();

    /// <summary>
    /// TaskService.ExtraTasks: inside the lair the current task of the chain runs. The client tracker shows RingTask
    /// (24) son type 3 tasks whose _iDungeonID is the current scene (LC_TaskHelper.lua:892-940). Earlier tasks leave the
    /// list as the chain moves on (inferred: the client also hides finished ones).
    /// </summary>
    private IEnumerable<TaskInfo> LairTasks(GameSession s)
    {
        if (s.DungeonAttempt is not { Dracula: not null, Settled: false } a || s.InstanceId != a.Room.Id) return [];
        var ids = LairTaskIds();
        return a.Wave < ids.Length ? [new TaskInfo { TaskId = ids[a.Wave], Status = TaskState.Running, CompleteNum = { 0 } }] : [];
    }

    /// <summary>A wave begins (DungeonService.SpawnWaveAsync): the tracker moves on; the first one plays story 3003.</summary>
    internal async Task WaveAsync(DungeonService.Attempt a)
    {
        var members = DungeonService.LoadedMembers(a);
        if (a.Wave == 0)
            foreach (var s in members)
            {
                // LC_DunGeon_Dracula.PlayStory adds the bride from 83641's entity, so it comes first.
                await SendQuietAsync(s, WeddingState, State(a.Dracula!));
                await SendQuietAsync(s, DungeonService.Event, Story(Lair, StoryId(1)));
            }
        foreach (var s in members) await _tasks.SyncTasksAsync(s);
    }

    /// <summary>The cage opens only for the key's holder (its client button shows only with the key buff, too).</summary>
    internal int InteractionError(DungeonService.Attempt a, GameSession s, int npc) =>
        npc == DungeonCatalog.DraculaCage && (a.Dracula!.Rescuer == 0 || a.Dracula.Rescuer != s.Role?.Id) ? ErrorCodes.FeatureUnavailable : 0;

    /// <summary>
    /// A bush search (6000018-6000021). The bush that hides the key gives it to its searcher: the key buff (kept until
    /// the event ends; its BuffConfig time is 0), var 1302 and chat 6200107 to the room. That member becomes the rescuer.
    /// </summary>
    internal async Task InteractedAsync(DungeonService.Attempt a, GameSession s, int npc)
    {
        var run = a.Dracula!;
        if (!DungeonCatalog.DraculaBushes.Contains(npc) || !run.Searched.Add(npc) || npc != run.KeyBush || run.Rescuer != 0
            || s.Role == null) return;
        run.Rescuer = s.Role.Id;
        run.RescuerEntity = RoleSessionService.PlayerEntity(s);
        run.RescuerEntity.PendantInfo.Clear(); // LC_DunGeon_Dracula.lua:175-184 clears it too
        _log.LogInformation("Wedding Crashers: {Role} found the key in guild {Guild}'s lair", s.Role.Id, run.Guild);
        await _buffs.AddAsync(s, KeyBuff, (int)Math.Clamp((run.End - Now) * 1000, 1000, int.MaxValue), s.Role.Id);
        await _dungeons.SendToRoomAsync(a, DungeonService.Event, VarsEvent(Lair, [Var(VarRescuer, run.Rescuer)]));
        var chat = new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = KeyFoundChat };
        chat.ParamList[1] = new ChatParameter { Params = { s.Role.Name }, Type = ChatParamType.ChatParamString }; // (inferred) encoding as MvpService
        await _dungeons.SendToRoomAsync(a, SystemBroadcast, chat);
    }

    /// <summary>
    /// (inferred) The key holder who leaves the lair drops it: the search starts over (with a new hiding place), so the
    /// others can still finish.
    /// </summary>
    internal async Task LeftAsync(DungeonService.Attempt a, GameSession s)
    {
        if (s.Buffs.FirstOrDefault(b => b.ConfigId == KeyBuff) is { } key) await _buffs.RemoveAsync(s, key);
        var run = a.Dracula!;
        if (!a.Settled && run.Rescuer != 0 && run.Rescuer == s.Role?.Id)
        {
            run.Rescuer = 0; run.RescuerEntity = null; run.Searched.Clear();
            run.KeyBush = DungeonCatalog.DraculaBushes[Random.Shared.Next(DungeonCatalog.DraculaBushes.Length)];
            await _dungeons.SendToRoomAsync(a, DungeonService.Event, VarsEvent(Lair, [Var(VarRescuer, 0)]));
        }
        // The tracker drops the lair's task; a disconnecting member's channel may already be gone.
        if (s.Role != null)
            try { await _tasks.SyncTasksAsync(s); }
            catch (Exception e) { _log.LogWarning(e, "Wedding Crashers task sync failed for {Role}", s.Role?.Id); }
    }

    /// <summary>
    /// DungeonService settled the lair: a win is the cage opened by the key's holder before the event ends. 83642 goes
    /// before 30403, whose success branch plays story 3004 with both entities. The rewards follow as their own Realm
    /// operation (the settlement can be reached from a member's message).
    /// </summary>
    internal async Task SettleAsync(DungeonService.Attempt a, bool won)
    {
        var run = a.Dracula!;
        bool success = won && run.Rescuer != 0 && Now < run.End && !run.Settled;
        // (inferred) The guild's occurrence ends with its first result; a lost lair (time, everyone gone) can be entered
        // again while the event lasts.
        if (success) run.Settled = true;
        var complete = new GuildDraculaWeddingCompleteNtf
        {
            WinCamp = success ? 1 : 0, // (inferred) unread by the client
            BridePlayerId = run.Bride, RescuerPlayerId = success ? run.Rescuer : 0,
            BrideEntity = run.BrideEntity.Clone(), RescuerEntity = success ? run.RescuerEntity?.Clone() : null,
        };
        var members = DungeonService.LoadedMembers(a);
        foreach (var s in members)
        {
            s.Attack = null;
            await SendQuietAsync(s, WeddingComplete, complete);
            await SendQuietAsync(s, DungeonService.Result, new DungeonResNotify { DungeonEntry = Lair, Res = (DungeonRes)(success ? 1 : 2) });
            await _tasks.SyncTasksAsync(s);
        }
        var keyHolders = members.Where(s => s.Buffs.Any(b => b.ConfigId == KeyBuff)).ToArray();
        // Everyone who entered this lair took part (inferred), and the bride, whose rescue it was.
        var participants = a.Stats.Keys.Append(run.Bride).Distinct().ToArray();
        // (inferred) The MVP is the participant who dealt the most damage in the lair; the rescuer wins a tie.
        long mvp = participants.OrderByDescending(r => a.Stats.GetValueOrDefault(r)?.TotalDamage ?? 0)
            .ThenByDescending(r => r == run.Rescuer).ThenBy(r => r).First();
        _loop.PostLegacy(async () =>
        {
            foreach (var s in keyHolders)
                if (s.Buffs.FirstOrDefault(b => b.ConfigId == KeyBuff) is { } key) await _buffs.RemoveAsync(s, key);
            if (success) await RewardAsync(run, participants, mvp);
        }, "dracula wedding rewards");
    }

    private async Task RewardAsync(Run run, long[] participants, long mvp)
    {
        string occurrence = $"dracula-wedding:{run.Guild}:{run.Start}";
        foreach (long role in participants)
            await LetterAsync(role, ParticipantMail, $"{occurrence}:participant");
        await LetterAsync(mvp, MvpMail, $"{occurrence}:mvp");
        foreach (var s in _sessions.All().Where(s => s.Role != null && participants.Contains(s.Role.Id)))
            try { await _tasks.OnDraculaWeddingAsync(s); }
            catch (Exception e) { _log.LogWarning(e, "Wedding Crashers quest target failed for {Role}", s.Role?.Id); }
    }

    /// <summary>A reward letter, once per occurrence and recipient (its delivery key).</summary>
    private async Task LetterAsync(long role, int template, string key)
    {
        try { await _mail.SendTemplateOnceAsync(role, template, _mail.TemplateAward(template), key, _ => { }, _loop.Owners?.Current); }
        catch (Exception e) { _log.LogError(e, "Wedding Crashers letter {Template} to {Role} failed", template, role); }
    }

    /// <summary>Explicit GM test aid. Overrides only activity 3400's window, in memory.</summary>
    private async Task TestWindowAsync(GameSession s, string[] args)
    {
        if (args.Length != 1 || args[0] is not ("open" or "close")) throw new ArgumentException("Use !dracula open|close");
        if (args[0] == "open") _schedule.TestWindows[Activity] = new(Now, Now + 30 * 60);
        else
        {
            _schedule.TestWindows.Remove(Activity);
            foreach (var run in _runs.Values.Where(r => !r.Closed).ToArray()) await CloseAsync(run);
        }
        await GmCommands.ReplyAsync(s, $"Wedding Crashers test window {args[0]}.");
    }
}
