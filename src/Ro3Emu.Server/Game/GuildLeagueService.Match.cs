using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// One league match: a Main Battlefield (2210101) and a Sub-Battlefield (2220101) room per pair, open for the round's
/// window (GuildLeagueGameTime 1800 s, DungeonLua_2210101.lua). Rules are UITips 90001/90002/90007/90008/90011 and
/// BattleEventPointConfig 1001-1013. Match state is in memory; its outcome is committed by <see cref="CommitMatchAsync"/>.
/// </summary>
public sealed partial class GuildLeagueService
{
    /// <summary>GuildSettingConfig GuildLeagueSceneID [2210101, 2220101] (LC_GuildLeagueHelper.lua:318-349).</summary>
    public long MainScene => SceneOf(0, 2210101);
    public long SubScene => SceneOf(1, 2220101);
    private long SceneOf(int index, long fallback) => _data.Table("GuildSettingConfig").TryGetValue("GuildLeagueSceneID", out var row)
        && row.TryGetProperty("_kValue", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Array && v.GetArrayLength() > index
        && GameData.Number(v[index]) is { } n ? (long)n : fallback;

    // DungeonLua_2210101.lua: MaxGuildPoint / System_BatteData_GuildScore_Limit 3000, GuildLeagueEndTime 60
    // (GuildLeagueAssistantEndTime 60 in DungeonLua_2220101.lua).
    private const long MaxGuildPoint = 3000;
    private const int EndGrace = 60;
    // UITips 90001 / BattleEventPointConfig 1001-1004: guild points for a flag delivery, a capture, a hold tick, a kill.
    private const long GuildFlag = 200, GuildCapture = 60, GuildHold = 10, GuildKill = 2, HoldSeconds = 12;
    // UITips 90002 / BattleEventPointConfig 1005-1010: personal points.
    // Reviving an ally (5) is not tracked: player revival skills report no healer here.
    private const long PersonalKill = 20, PersonalAssist = 2, PersonalFlagTaken = 65,
        PersonalFlagDelivered = 200, PersonalCapture = 65, PersonalHold = 1, PersonalHoldSeconds = 6;
    // UITips 90007: a captured point is fortified 30 s; a courtyard Boss every 3 minutes.
    private const int FortifySeconds = 30, BossMinutes = 3;
    // UITips 90008: guards at once, more guards and 4 commanders after 5 minutes, a Boss after 10.
    private const int SecondWaveMinutes = 5, BossWaveMinutes = 10;
    // UITips 90011: morale per kill / guard / commander / boss, and the 1000/2000/3000 thresholds.
    private const long MoraleKill = 1, MoraleGuard = 30, MoraleCommander = 100, MoraleBoss = 600;
    private static readonly long[] MoraleSteps = [1000, 2000, 3000];
    /// <summary>BuffConfig 4801101/4801102/4801103 "High Morale" / "Unstoppable": the only morale buffs in the data
    /// (inferred mapping to the three thresholds; their rows grant ATK/DEF where UITips 90011 says damage reduction).</summary>
    private static readonly int[] MoraleBuffs = [4801101, 4801102, 4801103];
    private const int RespawnProtection = 4801001; // BuffConfig "Respawn Protection", 5 s (applied on revival, inferred)
    /// <summary>(inferred) The revive countdown: DungeonConfig revive type 5 auto-revives (LC_SceneCom_Revive_GuildLeague
    /// sends no request), its delay is not in the data.</summary>
    private const int ReviveSeconds = 10;
    /// <summary>(inferred) A hit within this many seconds before a defeat counts as an assist.</summary>
    private const int AssistSeconds = 10;

    // Scene editor sc_pm_leaguematches_001_dg01: regions 42 Mountain (middle), 43 Fire (left), 44 Water (right) are the
    // capture points (PointNameList / GuildLeagueOccupyNoticeType); point 30 (named "BOSS refresh point") is the courtyard Boss spawn;
    // revive points 22 (near Fire) and 21 (near Water).
    private static readonly int[] CapturePoints = [42, 43, 44];
    private const int BossPoint = 30;
    // NPCConfig _kNpcTag: 4800000 courtyard Boss (4800020-4800024), 4800001 guard (4800014-4800018),
    // 4800002 commander (4800025-4800026), 4800003 Sub-Battlefield Boss (4800027).
    private static readonly int[] CourtyardBosses = [4800020, 4800021, 4800022, 4800023, 4800024];
    private static readonly int[] Guards = [4800014, 4800015, 4800016, 4800017, 4800018];
    private static readonly int[] Commanders = [4800025, 4800026];
    private const int SubBoss = 4800027;
    // Scene editor sc_pm_leaguematches_002_dg01 groups: 50 "four corner" guards, 22+23 central guards, 51 commanders, 52 the
    // central Boss; revive points 17 (left) and 15 (right). Which group belongs to which wave is (inferred) from the text.
    private static readonly int[][] SubWaves = [[50], [22, 23, 51], [52]];

    internal enum Kind { Guard, Commander, Boss, Courtyard }

    internal sealed class Fighter
    {
        public long Role { get; init; }
        public long Guild { get; init; }
        public string Name { get; init; } = "";
        public int Job { get; init; }
        public long Score { get; set; }
        public int Kills { get; set; }
        public int Assists { get; set; }
        public int Deaths { get; set; }
        public int MonsterKills { get; set; }
        public int BossKills { get; set; }
        public int CommanderKills { get; set; }
        public int Captures { get; set; }
        public int Flags { get; set; }
        public int GetFlags { get; set; }
        public int Revives { get; set; }
        public long Damage { get; set; }
        public long Taken { get; set; }
        public long Heal { get; set; }
        public long DeadUntil { get; set; }
        public long NextHold { get; set; }
        /// <summary>The circle unit the fighter stands in, as last sent in dungeon var 10103.</summary>
        public long InCircle { get; set; }
        public Dictionary<long, long> HitBy { get; } = new();
    }

    internal sealed class Point
    {
        public required MapRegion Area { get; init; }
        /// <summary>The OccupationCircle unit (NPCConfig 4800000) that shows the point; Circle is its CustomData 16.</summary>
        public long Uid { get; init; }
        public int Circle { get; init; }
        public MapPoint At { get; init; }
        public int Blue { get; set; }
        public int Red { get; set; }
        public string Shown { get; set; } = "";
        public long Owner { get; set; }
        public long Challenger { get; set; }
        public long Since { get; set; }
        public long FortifiedUntil { get; set; }
        public long NextHold { get; set; }
    }

    internal sealed class Field(long scene)
    {
        public long Scene { get; } = scene;
        public InstanceService.Room? Room { get; set; }
        public Dictionary<long, Fighter> Fighters { get; } = new();
        public Dictionary<int, Point> Points { get; } = new();
        public Dictionary<MonsterUnit, (Kind Kind, string Slot)> Actors { get; } = new();
        public HashSet<string> Killed { get; } = new();
        public Dictionary<MonsterUnit, HashSet<long>> Hitters { get; } = new();
        public bool Spawning { get; set; }
        public bool Live => Room is { Closed: false };
    }

    internal sealed class Match
    {
        public required string Key { get; init; }
        public required int Index { get; init; }
        public required int Round { get; init; }
        public required int Tier { get; init; }
        public required int Order { get; init; }
        public required int Pair { get; init; }
        public required long Blue { get; init; }
        public required long Red { get; init; }
        public required long Start { get; init; }
        public required long End { get; init; }
        public required Field Main { get; init; }
        public required Field Sub { get; init; }
        public Dictionary<long, long> Points { get; } = new();
        public Dictionary<long, long> Morale { get; } = new();
        public Dictionary<long, long> MainKills { get; } = new();
        public HashSet<long> Carriers { get; } = new();
        public HashSet<long> Leaving { get; } = new();
        public int Bosses { get; set; }
        public MonsterUnit? Boss { get; set; }
        public int Wave { get; set; }
        public bool FirstBlood { get; set; }
        public bool Dirty { get; set; } = true;
        public bool Ended { get; set; }
        public long EndedAt { get; set; }
        public long Winner { get; set; }
        public bool Committed { get; set; }
        public int Camp(long guild) => guild == Blue ? 1 : guild == Red ? 2 : 0;
        public long Enemy(long guild) => guild == Blue ? Red : Blue;
        public Field FieldOf(long scene) => scene == Main.Scene ? Main : Sub;
    }

    private readonly Dictionary<string, Match> _matches = new();
    private readonly HashSet<GameSession> _switching = new();
    internal IReadOnlyCollection<Match> Matches => _matches.Values;
    private static string MatchKey(int index, int round, int tier, int order, int pair) => $"{index}:{round}:{tier}:{order}:{pair}";

    private (Match Match, Field Field)? Locate(GameSession s)
    {
        if (s.InstanceId == 0) return null;
        foreach (var m in _matches.Values)
        {
            if (m.Main.Room?.Id == s.InstanceId) return (m, m.Main);
            if (m.Sub.Room?.Id == s.InstanceId) return (m, m.Sub);
        }
        return null;
    }

    private long GuildOf(GameSession s) => s.Role == null ? 0 : s.Guild is { GuildId: > 0 } g ? g.GuildId : _guilds.ForRole(s.Role.Id)?.Id ?? 0;

    private void HookBattle()
    {
        _scene.Loaded.Add(LoadedAsync);
        _scene.Leaving.Add(LeavingAsync);
        _combat.PlayerDied.Add(DiedAsync);
        _combat.ReviveInfoOwners.Add(s => Locate(s) != null);
        _combat.PvpDealtAsync.Add(PvpAsync);
        _combat.HealedAsync.Add(HealedAsync);
        _combat.EventDamageRecorded.Add(MonsterDamagedAsync);
        _combat.Killed.Add(MonsterKilledAsync);
    }

    // ---- admission -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// 30400 for either battlefield (UI_Sys_GuildLeague_BattleWidgetCtrl.lua:750-800). The guild must have an undecided
    /// match in the round whose window is open. Temporary members are refused (ErrorCodeConfig 517, GuildTemporaryRegularTime);
    /// the Main Battlefield admits the guild's elite roster (GuildService.LeagueElite) in positions GuildSettingConfig
    /// GuildLeagueMainSceneNeedPosition (518) and 40 per guild (PublicParamConfig GuildLeagueMaxPlayerNumber, 523). A roster
    /// short of GuildLeagueEliteNum is filled when the round opens (<see cref="FillElitesAsync"/>).
    /// </summary>
    public async Task<bool> TryEnterAsync(GameSession s, EnterDungeonReq req)
    {
        if (req.SceneEntry != MainScene && req.SceneEntry != SubScene) return false;
        int code = EntryError(s, req.SceneEntry, out var target);
        var current = Locate(s);
        if (code == 0 && s.InstanceId != 0 && (current == null || current.Value.Match != target!.Value.Match))
            code = ErrorCodes.DungeonBusy;
        if (code == 0 && current?.Field.Scene == req.SceneEntry) code = ErrorCodes.DungeonBusy;
        if (code != 0) { await s.SendGameAsync(DungeonService.Enter, new EnterDungeonRsp { Result = code }); return true; }
        var (match, field) = target!.Value;
        long guild = GuildOf(s);
        bool joined;
        if (field.Room is { Closed: false } room) joined = _instances.AddParticipant(room, s, continuation: current != null);
        else
        {
            field.Room = _instances.Create(field.Scene, [s]);
            field.Actors.Clear(); field.Hitters.Clear();
            if (field == match.Main) match.Boss = null; // the old room took its Boss with it
            joined = true;
        }
        if (!joined) { await s.SendGameAsync(DungeonService.Enter, new EnterDungeonRsp { Result = ErrorCodes.Parameter }); return true; }
        await s.SendGameAsync(DungeonService.Enter, new EnterDungeonRsp());
        // The camp travels with the entity (EntityCamp, GetPlayerGuildLeagueCamp): GuildLeagueCamp [1, 2] blue/red.
        int priorFaction = s.EventFaction;
        s.EventFaction = match.Camp(guild);
        bool entered;
        // A move between the two battlefields keeps the camp through the old room's departure (LeavingAsync).
        if (current != null) _switching.Add(s);
        try { entered = await _instances.EnterAsync(field.Room!, s, 0); }
        catch { s.EventFaction = priorFaction; throw; }
        finally { _switching.Remove(s); }
        if (!entered) { s.EventFaction = Locate(s) is { } still ? still.Match.Camp(guild) : 0; return true; }
        Enlist(field, s, guild);
        await SpawnAsync(match, field);
        return true;
    }

    private int EntryError(GameSession s, long sceneEntry, out (Match Match, Field Field)? target)
    {
        target = null;
        if (s.Role == null || s.Progress == null || s.Dead || s.SceneLoading) return ErrorCodes.Parameter;
        if (!_functions.IsOpen(s, Function)) return ErrorCodes.FeatureUnavailable;
        if (_data.Row("DungeonConfig", sceneEntry) is { } dungeon && s.Progress.Level < GameData.Int(dungeon, "_iDLoadLv"))
            return ErrorCodes.FeatureUnavailable;
        var guild = _guilds.ForRole(s.Role.Id);
        if (guild == null || !guild.Members.TryGetValue(s.Role.Id, out var member)) return ErrorCodes.NotInGuild;
        if (Temporary(member)) return ErrorCodes.LeagueTemporary;
        long now = Now;
        var t = _calendar.Current(now);
        var cup = _state.Cup;
        int round = t == null ? 0 : GuildLeagueCalendar.OpenRound(t, now);
        if (round == 0 || cup == null || cup.Index != t!.Index || cup.SettledRound < round - 1) return ErrorCodes.LeagueClosed;
        var group = GroupOf(guild.Id);
        if (group == null) return ErrorCodes.LeagueClosed;
        var pairs = GuildLeagueBracket.Pairs(Preliminary(group), Scores(group, round - 1), round);
        int pair = Array.FindIndex(pairs, p => p.Blue == guild.Id || p.Red == guild.Id);
        if (pair < 0 || pairs[pair].Blue == 0 || pairs[pair].Red == 0) return ErrorCodes.LeagueClosed;
        if (group.Outcomes.Any(o => o.Round == round && o.Pair == pair)) return ErrorCodes.LeagueClosed;
        var match = MatchFor(cup, group, round, pair, pairs[pair], cup.Round(round));
        if (match.Ended) return ErrorCodes.LeagueClosed;
        if (sceneEntry == match.Main.Scene)
        {
            long[] positions = SettingArray("GuildLeagueMainSceneNeedPosition");
            if (!GuildService.IsLeagueElite(guild, s.Role.Id) || positions.Length > 0 && !positions.Contains(member.Official))
                return ErrorCodes.LeagueElite;
            long[] limits = Param("GuildLeagueMaxPlayerNumber");
            long limit = limits.Length > match.Camp(guild.Id) - 1 && match.Camp(guild.Id) > 0 ? limits[match.Camp(guild.Id) - 1] : 40;
            int inside = match.Main.Room?.ActiveMembers.Count(m => m.Session != s && GuildOf(m.Session) == guild.Id) ?? 0;
            if (inside >= limit) return ErrorCodes.LeagueMainFull;
        }
        target = (match, match.FieldOf(sceneEntry));
        return 0;
    }

    /// <summary>A member who joined less than GuildTemporaryRegularTime seconds ago is temporary (LC_GuildLeagueHelper
    /// CheckEnterTimeInGuild). Officers were appointed, which a temporary member cannot be (10960000029).</summary>
    private bool Temporary(GuildStore.Member member) => member.Official == 20
        && Now - member.EnterTime / 1000 < Setting("GuildTemporaryRegularTime", 3600);

    private long[] SettingArray(string name) => _data.Table("GuildSettingConfig").TryGetValue(name, out var row)
        && row.TryGetProperty("_kValue", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Array
        ? v.EnumerateArray().Select(x => (long)(GameData.Number(x) ?? 0)).ToArray() : [];

    private Match MatchFor(Cup cup, Group group, int round, int pair, GuildLeagueBracket.Pair guildsOf, GuildLeagueCalendar.Window window)
    {
        string key = MatchKey(cup.Index, round, group.Tier, group.Order, pair);
        if (_matches.TryGetValue(key, out var match)) return match;
        match = new Match
        {
            Key = key, Index = cup.Index, Round = round, Tier = group.Tier, Order = group.Order, Pair = pair,
            Blue = guildsOf.Blue, Red = guildsOf.Red, Start = window.Start, End = window.End,
            Main = new Field(MainScene), Sub = new Field(SubScene)
        };
        foreach (long g in new[] { match.Blue, match.Red }) { match.Points[g] = 0; match.Morale[g] = 0; match.MainKills[g] = 0; }
        if (_maps.Map(MainScene) is { } map)
            foreach (int id in CapturePoints)
                if (map.Regions.TryGetValue(id, out var region))
                    match.Main.Points[id] = new Point
                    {
                        Area = region, Uid = CircleUidBase | Interlocked.Increment(ref _circleSerial),
                        Circle = CircleId(id), At = map.Grounded(new MapPoint(region.X, region.Y, region.Z, 0))
                    };
        match.Bosses = 0;
        _matches[key] = match;
        return match;
    }

    private Fighter Enlist(Field field, GameSession s, long guild)
    {
        if (field.Fighters.TryGetValue(s.Role!.Id, out var known)) return known;
        return field.Fighters[s.Role.Id] = new Fighter { Role = s.Role.Id, Guild = guild, Name = s.Role.Name, Job = s.Progress?.Career ?? 0 };
    }

    // ---- scene -------------------------------------------------------------------------------------------------------

    private async Task LoadedAsync(GameSession s)
    {
        if (Locate(s) is not { } at || s.Role == null) return;
        var (match, field) = at;
        long guild = GuildOf(s);
        if (match.Camp(guild) == 0 || match.Ended && Now >= match.EndedAt + EndGrace) { await _instances.LeaveAsync(s); return; }
        s.EventFaction = match.Camp(guild);
        Enlist(field, s, guild);
        await s.SendGameAsync(DungeonService.BaseInfo, BaseInfo(match, field));
        await s.SendGameAsync(SyncNotify, new SyncPlayerGuildLeagueDungeonNtf { Info = MatchInfo(match) });
        if (field == match.Main) await ShowCirclesAsync(s, match);
        if (CampPoint(match, field, guild) is { } point && field == match.Sub && match.Camp(guild) == 2)
            await _scene.TeleportInMapAsync(s, point);
        await MoraleBuffAsync(s, match, guild);
    }

    /// <summary>30402 from a league battlefield: back to the saved world position.</summary>
    public async Task<bool> LeaveAsync(GameSession s)
    {
        if (Locate(s) == null) return false;
        await s.SendGameAsync(DungeonService.Leave, new LeaveDungeonRsp());
        await _instances.LeaveAsync(s);
        return true;
    }

    private async Task LeavingAsync(GameSession s)
    {
        if (Locate(s) is not { } at || s.Role == null) return;
        if (!_switching.Contains(s)) s.EventFaction = 0;
        at.Match.Carriers.Remove(s.Role.Id); // (inferred) a carried flag is lost with its carrier
        s.WorldState.CustomData.Remove(FlagStateKey);
        foreach (var buff in s.Buffs.Where(b => MoraleBuffs.Contains(b.ConfigId)).ToArray()) await _buffs.RemoveAsync(s, buff);
    }

    /// <summary>Main: no camp teleport (both guilds enter at the "Spawn" born point 14); revival goes to the camp's side
    /// point 22 / 21 (inferred from the paired left/right respawn areas 31/23). Sub: left 17 blue, right 15 red.</summary>
    private MapPoint? CampPoint(Match match, Field field, long guild)
    {
        int camp = match.Camp(guild);
        int point = field == match.Main ? (camp == 1 ? 22 : 21) : (camp == 1 ? 17 : 15);
        return _maps.Map(field.Scene) is { } map && map.Points.TryGetValue(point, out var p) ? map.Grounded(p) : null;
    }

    private GuildLeagueMatchInfo MatchInfo(Match m) => new()
    {
        BlueGuild = Dungeon(m.Blue, GroupOf(m.Blue)), RedGuild = Dungeon(m.Red, GroupOf(m.Red)),
    };

    private DungeonBaseInfoNotify BaseInfo(Match m, Field field)
    {
        int stage = m.Ended ? 3 : 2;
        var info = new DungeonBaseInfoNotify
        {
            DungeonEntry = field.Scene, DungeonId = field.Room?.Id ?? 0, Stage = stage, Level = 1,
            InitData = new DungeonInitData
            {
                GuildLeagueInitStr = new GuildLeagueInitStr { Info = MatchInfo(m), CurStage = m.Round },
                PlaybookId = _seasons.PlayBook
            },
            StageModuleData = new DungeonStageModuleData
            {
                MapState = stage, Stage = stage, StageStartTime = m.Start * 1000,
                StageEndTime = m.End * 1000, MapStateStartTime = m.Start * 1000, MapStateEndTime = m.End * 1000
            },
            ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2
        };
        info.Vars.Add(Vars(m));
        return info;
    }

    /// <summary>
    /// LC_DunGeon_VarDefine 403 CGuildLeagueStartTimestamp (ms), 404 CGuildLeaguePrepareTimestamp (ms, none: the battle
    /// starts with the window, inferred), 411 CGuildLeagueScoreListNew and 412 CGuildLeagueSpiritListNew as
    /// [blue, x, red, x] (UI_Sys_GuildLeague_BattleVSWidgetCtrl.lua:1252-1330 reads entries 1 and 3; the client's own default
    /// is {0, 1, 0, 1}, so 1 fills entries 2 and 4, inferred).
    /// </summary>
    private static DungeonVar[] Vars(Match m) =>
    [
        Var(403, m.Start * 1000), Var(404, 0),
        Var(411, m.Points[m.Blue], 1, m.Points[m.Red], 1),
        Var(412, m.Morale[m.Blue], 1, m.Morale[m.Red], 1),
    ];

    private static DungeonVar Var(int id, params long[] values) { var v = new DungeonVar { VarId = id }; v.Vi64.Add(values); return v; }

    private IEnumerable<GameSession> Present(Field field) => field.Room is { Closed: false } room
        ? room.ActiveMembers.Select(m => m.Session).Where(s => s.InstanceId == room.Id && !s.SceneLoading && s.Role != null)
        : [];
    private IEnumerable<GameSession> Present(Match m) => Present(m.Main).Concat(Present(m.Sub));

    private async Task SendAllAsync(IEnumerable<GameSession> to, uint id, Google.Protobuf.IMessage msg)
    {
        await RoleFanout.EachAsync(to, async s =>
        {
            try { await s.SendGameAsync(id, msg); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            { _log.LogDebug(e, "Guild League notice {Message} not delivered to {Role}", id, s.Role?.Id); }
        });
    }

    /// <summary>30404 DungeonEventDungeonVarChange (event type 4, as the scripted dungeons) with the score/morale lists.</summary>
    private Task PublishVarsAsync(Match m) => Task.WhenAll(new[] { m.Main, m.Sub }.Select(field =>
    {
        var change = new DungeonEventNotify { DungeonEntry = field.Scene, EventType = (DungeonEventType)4, StageNum = 2, LevelNum = 1 };
        change.Vars.Add(Vars(m));
        return SendAllAsync(Present(field), DungeonService.Event, change);
    }));

    /// <summary>30433: eventID 1 flag delivered, 2 point captured; id is the acting guild (BattleVSWidgetCtrl.lua:1170-1241).</summary>
    private Task EventAsync(Match m, int eventId, long guild) =>
        SendAllAsync(Present(m.Main), EventNotify, new GuildLeagueEventNtf { EventID = eventId, Id = guild });

    // ---- scoring -----------------------------------------------------------------------------------------------------

    private void AddPoints(Match m, long guild, long amount)
    {
        if (m.Ended || amount <= 0 || !m.Points.ContainsKey(guild)) return;
        m.Points[guild] = Math.Min(MaxGuildPoint, m.Points[guild] + amount);
        m.Dirty = true;
    }

    private void AddMorale(Match m, long guild, long amount)
    {
        if (m.Ended || amount <= 0 || !m.Morale.ContainsKey(guild)) return;
        m.Morale[guild] = Math.Min(MoraleSteps[^1], m.Morale[guild] + amount);
        m.Dirty = true;
    }

    private static int MoraleLevel(long morale) => MoraleSteps.Count(step => morale >= step);

    private int MoraleBuff(Match m, long guild) => MoraleLevel(m.Morale.GetValueOrDefault(guild)) is > 0 and var level
        ? MoraleBuffs[level - 1] : 0;

    private bool MoraleBuffStale(GameSession s, Match m, long guild)
    {
        int wanted = MoraleBuff(m, guild);
        return s.Buffs.Any(b => MoraleBuffs.Contains(b.ConfigId) && b.ConfigId != wanted)
            || wanted != 0 && !s.Dead && s.Buffs.All(b => b.ConfigId != wanted);
    }

    private async Task MoraleBuffAsync(GameSession s, Match m, long guild)
    {
        int wanted = MoraleBuff(m, guild);
        foreach (var buff in s.Buffs.Where(b => MoraleBuffs.Contains(b.ConfigId) && b.ConfigId != wanted).ToArray())
            await _buffs.RemoveAsync(s, buff);
        if (wanted != 0 && !s.Dead && s.Role != null && s.Buffs.All(b => b.ConfigId != wanted))
            await _buffs.AddAsync(s, wanted, (int)Math.Clamp((m.End - Now) * 1000, 1000, int.MaxValue), s.Role!.Id);
    }

    /// <summary>
    /// 30432 GuildLeagueBattlePointTitleNtf for an in-battle title (BattleEventPointConfig, _kScore [3, bonus] adds personal
    /// points). Sent to the battlefield (inferred: every rating widget shows the title holder).
    /// </summary>
    private async Task TitleAsync(Field field, Fighter f, int titleId)
    {
        if (_data.Row("BattleEventPointConfig", titleId) is not { } row) return;
        if (row.TryGetProperty("_kScore", out var score) && score.ValueKind == System.Text.Json.JsonValueKind.Array
            && score.GetArrayLength() >= 2 && GameData.Number(score[0]) == 3) f.Score += (long)(GameData.Number(score[1]) ?? 0);
        var msg = new GuildLeagueBattlePointTitleNtf { TitleId = titleId, PlayerGuid = f.Role };
        if (row.TryGetProperty("_iName", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var p in name.EnumerateArray().Skip(1))
                msg.ParamList.Add(new GuildLeagueBattlePointTitleParam
                {
                    StringValue = p.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p.GetString() ?? "" : p.ToString(), IntValue = int.TryParse(p.ToString(), out int n) ? n : 0
                });
        await SendAllAsync(Present(field), TitleNotify, msg);
    }

    private bool Active(Match m) => !m.Ended && Now >= m.Start && Now < m.End;

    private bool Fights(GameSession s, Match m, Field field) => s.Role != null && s.InstanceId == field.Room?.Id && !s.SceneLoading
        && field.Fighters.TryGetValue(s.Role.Id, out var f) && f.Guild == GuildOf(s) && m.Camp(f.Guild) > 0;

    /// <summary>Opposite guilds of one live match field may fight; allies and onlookers may not.</summary>
    public bool Hostile(GameSession a, GameSession b) => Locate(a) is { } at && a.InstanceId == b.InstanceId && Active(at.Match)
        && Fights(a, at.Match, at.Field) && Fights(b, at.Match, at.Field) && GuildOf(a) != GuildOf(b);

    /// <summary>League monsters are fought only while the match is live, by its two guilds.</summary>
    public bool CanHit(GameSession s, MonsterUnit m)
    {
        if (Locate(s) is not { } at || m.InstanceId != at.Field.Room?.Id) return true;
        return Active(at.Match) && Fights(s, at.Match, at.Field);
    }

    private Task HealedAsync(GameSession s, long amount)
    {
        if (amount > 0 && Locate(s) is { } at && at.Field.Fighters.TryGetValue(s.Role!.Id, out var f)) f.Heal += amount;
        return Task.CompletedTask;
    }

    private async Task PvpAsync(GameSession attacker, GameSession victim, long amount, bool lethal)
    {
        if (Locate(attacker) is not { } at || !Hostile(attacker, victim)) return;
        var (m, field) = at;
        var a = field.Fighters[attacker.Role!.Id];
        var v = field.Fighters[victim.Role!.Id];
        a.Damage += Math.Max(0, amount); v.Taken += Math.Max(0, amount);
        v.HitBy[a.Role] = Now;
        if (!lethal) return;
        a.Kills++; a.Score += PersonalKill;
        if (field == m.Main) { AddPoints(m, a.Guild, GuildKill); m.MainKills[a.Guild]++; }
        else AddMorale(m, a.Guild, MoraleKill);
        foreach (long helper in v.HitBy.Where(h => h.Key != a.Role && Now - h.Value <= AssistSeconds).Select(h => h.Key).ToArray())
            if (field.Fighters.TryGetValue(helper, out var h) && h.Guild == a.Guild) { h.Assists++; h.Score += PersonalAssist; }
        v.HitBy.Clear();
        if (!m.FirstBlood) { m.FirstBlood = true; await TitleAsync(field, a, 1037); }
        // BattleEventPointConfig 1033-1036: total defeats 5/10/15/20 (12050000018 "@{1} Total Kills").
        int title = a.Kills switch { 5 => 1033, 10 => 1034, 15 => 1035, 20 => 1036, _ => 0 };
        if (title != 0) await TitleAsync(field, a, title);
    }

    private async Task DiedAsync(GameSession s)
    {
        if (Locate(s) is not { } at || s.Role == null || !at.Field.Fighters.TryGetValue(s.Role.Id, out var f)) return;
        f.Deaths++;
        f.DeadUntil = Now + ReviveSeconds;
        if (at.Match.Carriers.Remove(s.Role.Id)) // (inferred) the flag is lost when its carrier falls
            PostFlag(s);
        await s.SendGameAsync(CombatService.ReviveInfo, new SyncClientPlayerReviveInfoNtf
        {
            BDead = true, ReviveTime = f.DeadUntil,
            SceneEntry = s.SceneEntry
        });
    }

    /// <summary>31013 from a league battlefield: allowed once the countdown is over (the client sends none by itself).</summary>
    public async Task<bool> ReviveAsync(GameSession s)
    {
        if (Locate(s) is not { } at) return false;
        if (!s.Dead || !at.Field.Fighters.TryGetValue(s.Role!.Id, out var f) || f.DeadUntil > Now)
            await s.SendGameAsync(CombatService.Relive, new EntityReliveAck { Result = ErrorCodes.FeatureUnavailable, EntityID = (ulong)s.Role!.Id });
        else await ReviveAtCampAsync(s);
        return true;
    }

    private async Task ReviveAtCampAsync(GameSession s)
    {
        if (Locate(s) is not { } at || !s.Dead || !_combat.TryClaimRevive(s)) return;
        try
        {
            if (!_combat.CommitReviveClaim(s)) return;
            await s.SendGameAsync(CombatService.Relive, new EntityReliveAck { Result = 0, EntityID = (ulong)s.Role!.Id });
            await _combat.PublishReviveAsync(s, CampPoint(at.Match, at.Field, GuildOf(s)));
            if (s.Role == null) return; // detached by a role handover during the publication
            await _buffs.AddAsync(s, RespawnProtection, 5000, s.Role.Id);
            await MoraleBuffAsync(s, at.Match, GuildOf(s));
        }
        finally { _combat.ReleaseReviveClaim(s); }
    }

    private Task MonsterDamagedAsync(GameSession s, MonsterUnit monster, long amount)
    {
        if (amount > 0 && Locate(s) is { } at && at.Field.Actors.ContainsKey(monster) && s.Role != null)
        {
            if (!at.Field.Hitters.TryGetValue(monster, out var set)) at.Field.Hitters[monster] = set = new();
            set.Add(s.Role.Id);
            if (at.Field.Fighters.TryGetValue(s.Role.Id, out var f)) f.Damage += amount;
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Courtyard Boss: its final blow gives a flag (UITips 90007), "Flag Captured" 65 personal points and the "Defeat the
    /// Boss" title (1041). Sub-Battlefield: guard 10/2, commander 50/10, Boss 300/60 personal points for the final blow /
    /// participation (UITips 90002, BattleEventPointConfig 1011-1013) and 30/100/600 morale (UITips 90011); participation
    /// counts the killer's guild's other damage dealers (inferred).
    /// </summary>
    private async Task MonsterKilledAsync(GameSession s, MonsterUnit monster)
    {
        if (Locate(s) is not { } at || !at.Field.Actors.Remove(monster, out var spawned) || s.Role == null) return;
        var (m, field) = at;
        at.Field.Killed.Add(spawned.Slot);
        var hitters = field.Hitters.Remove(monster, out var set) ? set : [];
        if (m.Ended || !field.Fighters.TryGetValue(s.Role.Id, out var killer)) return;
        if (spawned.Kind == Kind.Courtyard)
        {
            m.Boss = null;
            if (m.Carriers.Add(killer.Role)) { killer.GetFlags++; killer.Score += PersonalFlagTaken; PostFlag(s); }
            await TitleAsync(field, killer, 1041);
            return;
        }
        var (final, share, morale, title) = spawned.Kind switch
        {
            Kind.Guard => (10L, 2L, MoraleGuard, 1042),
            Kind.Commander => (50L, 10L, MoraleCommander, 1043),
            _ => (300L, 60L, MoraleBoss, 1044),
        };
        killer.Score += final;
        if (spawned.Kind == Kind.Guard) killer.MonsterKills++;
        else if (spawned.Kind == Kind.Commander) killer.CommanderKills++;
        else killer.BossKills++;
        foreach (long helper in hitters.Where(h => h != killer.Role))
            if (field.Fighters.TryGetValue(helper, out var h) && h.Guild == killer.Guild) h.Score += share;
        AddMorale(m, killer.Guild, morale);
        await TitleAsync(field, killer, title);
    }

    // ---- clock -------------------------------------------------------------------------------------------------------

    /// <summary>Per-second match work: spawns, capture points, flags, revivals, the end and the departure grace.</summary>
    private async Task BattleClockAsync(long now)
    {
        using var scope = _loop.Owners is { } owners ? await owners.EnterAsync("guild-league/battle", LeagueKey) : null;
        foreach (var m in _matches.Values.ToArray())
        {
            if (!m.Ended)
            {
                await SpawnAsync(m, m.Main);
                await SpawnAsync(m, m.Sub);
                await PointsAsync(m, now);
                if (m.Points.Values.Any(p => p >= MaxGuildPoint) || now >= m.End) End(m, now);
            }
            foreach (var field in new[] { m.Main, m.Sub })
                foreach (var s in Present(field).Where(s => s.Dead && field.Fighters.TryGetValue(s.Role!.Id, out var f)
                    && f.DeadUntil > 0 && f.DeadUntil <= now))
                {
                    field.Fighters[s.Role!.Id].DeadUntil = 0;
                    _loop.PostOwned(s, () => ReviveAtCampAsync(s), "guild league revive");
                }
            if (m.Dirty)
            {
                m.Dirty = false;
                await PublishVarsAsync(m);
                // Buffs refresh the role's stats: each change runs in its role's own scope.
                foreach (var s in Present(m).Where(s => MoraleBuffStale(s, m, GuildOf(s))).ToArray())
                    _loop.PostOwned(s, () => Locate(s)?.Match == m ? MoraleBuffAsync(s, m, GuildOf(s)) : Task.CompletedTask, "guild league morale");
            }
            if (m.Ended && now >= m.EndedAt + EndGrace)
            {
                // The return to the world writes the role: each departure runs in its role's own scope.
                foreach (var s in Present(m).Where(s => m.Leaving.Add(s.Role!.Id)).ToArray())
                    _loop.PostOwned(s, async () => { if (Locate(s)?.Match == m) await _instances.LeaveAsync(s); }, "guild league departure");
                if (m.Committed && !m.Main.Live && !m.Sub.Live) _matches.Remove(m.Key);
            }
        }
    }

    /// <summary>
    /// The winner: the first to MaxGuildPoint; when both reach it or the time runs out level, more Main Battlefield player
    /// defeats wins (UITips 90001/90009). Still level: blue, the guild drawn first (inferred).
    /// </summary>
    private void End(Match m, long now)
    {
        long blue = m.Points[m.Blue], red = m.Points[m.Red];
        bool blueFull = blue >= MaxGuildPoint, redFull = red >= MaxGuildPoint;
        m.Winner = blueFull != redFull ? (blueFull ? m.Blue : m.Red)
            : blue != red && !blueFull ? (blue > red ? m.Blue : m.Red)
            : m.MainKills[m.Red] > m.MainKills[m.Blue] ? m.Red : m.Blue;
        m.Ended = true; m.EndedAt = now; m.Dirty = true;
        m.Boss = null;
        foreach (var field in new[] { m.Main, m.Sub })
            if (field.Room is { Closed: false } room) _loop.TrackRuntimeDetached("guild league clear", () => _instances.ClearMonstersAsync(room));
    }

    /// <summary>
    /// Capture points: the guild with more living members inside a point's region (MapRegion.Contains, LC_AreaHelper)
    /// takes it after GuildSettingConfig GuildLeagueTriggerStayAreaTime seconds (6, reading it as the capture time is
    /// inferred), then it is fortified 30 s. The owner earns 10 guild points per 12 s; its members inside 1 personal point
    /// per 6 s. A flag carrier inside a point of its own guild delivers.
    /// </summary>
    private async Task PointsAsync(Match m, long now)
    {
        if (!m.Main.Live || now < m.Start) return;
        long stay = Setting("GuildLeagueTriggerStayAreaTime", 6);
        var alive = Present(m.Main).Where(s => !s.Dead && Fights(s, m, m.Main)).ToArray();
        var where = new Dictionary<long, long>();
        foreach (var (id, p) in m.Main.Points)
        {
            var inside = alive.Where(s => p.Area.Contains(s.Position.X, s.Position.Z)).ToArray();
            p.Blue = inside.Count(s => GuildOf(s) == m.Blue);
            p.Red = inside.Count(s => GuildOf(s) == m.Red);
            foreach (var s in inside) where[s.Role!.Id] = p.Uid;
            var counts = inside.GroupBy(GuildOf).Select(g => (Guild: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToArray();
            long leader = counts.Length == 0 || counts.Length > 1 && counts[0].Count == counts[1].Count ? 0 : counts[0].Guild;
            if (now >= p.FortifiedUntil)
            {
                if (leader == 0 || leader == p.Owner) p.Challenger = 0;
                else if (p.Challenger != leader) { p.Challenger = leader; p.Since = now; }
                else if (now - p.Since >= stay)
                {
                    p.Owner = leader; p.Challenger = 0; p.FortifiedUntil = now + FortifySeconds; p.NextHold = now + HoldSeconds;
                    AddPoints(m, leader, GuildCapture);
                    foreach (var s in inside.Where(s => GuildOf(s) == leader))
                    { var f = m.Main.Fighters[s.Role!.Id]; f.Captures++; f.Score += PersonalCapture; }
                    await EventAsync(m, 2, leader);
                }
            }
            if (p.Owner != 0 && now >= p.NextHold)
            {
                AddPoints(m, p.Owner, GuildHold);
                p.NextHold = now + HoldSeconds;
            }
            foreach (var s in inside.Where(s => GuildOf(s) == p.Owner))
            {
                var f = m.Main.Fighters[s.Role!.Id];
                if (f.NextHold == 0) f.NextHold = now + PersonalHoldSeconds;
                else if (now >= f.NextHold) { f.Score += PersonalHold; f.NextHold = now + PersonalHoldSeconds; }
                if (m.Carriers.Remove(f.Role))
                {
                    PostFlag(s);
                    f.Flags++; f.Score += PersonalFlagDelivered;
                    AddPoints(m, f.Guild, GuildFlag);
                    await EventAsync(m, 1, f.Guild);
                }
            }
        }
        await PublishCirclesAsync(m, now, stay);
        foreach (var s in Present(m.Main).ToArray())
        {
            if (!m.Main.Fighters.TryGetValue(s.Role!.Id, out var f)) continue;
            long circle = where.GetValueOrDefault(s.Role.Id);
            if (f.InCircle == circle) continue;
            f.InCircle = circle;
            await SendAllAsync([s], DungeonService.Event, OccupyVar(m, circle));
        }
    }

    /// <summary>Courtyard Boss every 3 minutes from the start while none is alive (inferred: one at a time, the five
    /// tag-4800000 Bosses in turn); Sub-Battlefield waves at 0, 5 and 10 minutes, each spawn point once.</summary>
    private async Task SpawnAsync(Match m, Field field)
    {
        if (!field.Live || field.Spawning || m.Ended || Now < m.Start || _maps.Map(field.Scene) is not { } map) return;
        field.Spawning = true;
        try
        {
            long elapsed = Now - m.Start;
            if (field == m.Main)
            {
                int due = (int)(elapsed / (BossMinutes * 60));
                if (due > m.Bosses && m.Boss is not { Dead: false } && map.Points.TryGetValue(BossPoint, out var at))
                {
                    m.Bosses = due;
                    int npc = CourtyardBosses[(due - 1) % CourtyardBosses.Length];
                    if (await _instances.SpawnAsync(field.Room!, npc, map.Grounded(at)) is { } boss)
                    {
                        m.Boss = boss; field.Actors[boss] = (Kind.Courtyard, "boss:" + due);
                        boss.CustomData.Add(LongData(IsFlagKey, 1));
                        await SendAllAsync(boss.Viewers.ToArray(), WorldCoreService.CustomChange,
                            new CustomDataChange { EntityId = boss.Uid, CustomDatas = { boss.CustomData } });
                    }
                }
                return;
            }
            int wave = elapsed >= BossWaveMinutes * 60 ? 3 : elapsed >= SecondWaveMinutes * 60 ? 2 : 1;
            for (int w = 0; w < wave; w++)
                foreach (int groupId in SubWaves[w])
                {
                    var ids = SubGroup(field.Scene, groupId);
                    for (int i = 0; i < ids.Length; i++)
                    {
                        string slot = $"{groupId}:{i}";
                        if (field.Killed.Contains(slot) || field.Actors.Values.Any(a => a.Slot == slot)
                            || !map.Points.TryGetValue(ids[i], out var point)) continue;
                        var (kind, npc) = groupId == 52 ? (Kind.Boss, SubBoss) : groupId == 51 ? (Kind.Commander, Commanders[i % Commanders.Length])
                            : (Kind.Guard, Guards[i % Guards.Length]);
                        if (await _instances.SpawnAsync(field.Room!, npc, map.Grounded(point)) is { } actor) field.Actors[actor] = (kind, slot);
                    }
                }
            m.Wave = wave;
        }
        finally { field.Spawning = false; }
    }

    private readonly Dictionary<(long, int), long[]> _subGroups = new();
    /// <summary>The point ids of a scene-editor group (m_kGroups itemIdList; its region centres are in m_kAllPoints).</summary>
    private long[] SubGroup(long sceneEntry, int groupId)
    {
        if (_subGroups.TryGetValue((sceneEntry, groupId), out var ids)) return ids;
        ids = [];
        if (_data.Row("DungeonConfig", sceneEntry) is { } row && row.TryGetProperty("_kResSceneEditorName", out var name)
            && name.GetString() is { Length: > 0 } file)
        {
            if (DataFiles.Find(Path.Combine(_options.Value.GameDataDir ?? _options.Value.DataDir, "sceneeditor"), file + ".json") is { } path)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
                if (doc.RootElement.TryGetProperty("m_kGroups", out var groups)
                    && groups.TryGetProperty(groupId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var group)
                    && group.TryGetProperty("itemIdList", out var list))
                    ids = list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            }
        }
        return _subGroups[(sceneEntry, groupId)] = ids;
    }

    // ---- 30110 ---------------------------------------------------------------------------------------------------------

    /// <summary>30110 inside a league battlefield: both battlefields' boards (LC_DunGeon_GuildLeague.lua:162-530).</summary>
    public async Task<bool> StatisticsAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (id != 30110 || Locate(s) is not { } at) return false;
        var response = new DungeonStatisticsRes();
        foreach (var (key, info) in Boards(at.Match)) response.DungeonRolesInfo[key] = info;
        await s.SendGameAsync(id, response);
        return true;
    }

    /// <summary>
    /// Both boards keyed by scene entry (ConvertToGuildLeagueBattleReportInfo matches the keys against the scene ids).
    /// dicParams use enum_GuildLeagueScoreType; dungeonReportSpecialData params are enum_GuildLeagueMainBattleSpecialData
    /// [kills, captures, score, flags] / enum_GuildLeagueSubBattleSpecialData [kills, monster kills, boss kills, score,
    /// commander kills] (LC_GuildLeagueHelper.lua:81-93); the Sub-Battlefield "score" is the guild's morale (inferred).
    /// dungeonTitleData holds the end-of-match titles 1014-1024 by guild (lists at LC_GuildLeagueHelper.lua:171-208).
    /// </summary>
    private IEnumerable<(long Key, DungeonStatisticsInfo Info)> Boards(Match m)
    {
        foreach (var field in new[] { m.Main, m.Sub })
        {
            var info = new DungeonStatisticsInfo { DungeonMapId = field.Scene };
            foreach (var f in field.Fighters.Values)
            {
                var role = new DungeonRoleInfo
                {
                    RoleBase = new ObjScoreBaseInfo { Id = f.Role, Name = f.Name, Job = f.Job, Area = 1 },
                    GuildId = f.Guild, TotalDamage = f.Damage, TotalBeDamage = f.Taken, TotalCure = f.Heal, Totalscore = f.Score
                };
                role.DicParams[6] = f.Kills; role.DicParams[7] = f.Assists; role.DicParams[8] = f.Deaths;
                role.DicParams[9] = f.MonsterKills; role.DicParams[10] = f.BossKills; role.DicParams[11] = f.Captures;
                role.DicParams[12] = f.Flags; role.DicParams[14] = f.GetFlags; role.DicParams[16] = f.Revives;
                role.DicParams[17] = f.CommanderKills; role.DicParams[18] = f.Score;
                role.DicParams[997] = f.Damage; role.DicParams[998] = f.Taken; role.DicParams[999] = f.Heal;
                info.DungeonRolesInfo.Add(role);
            }
            foreach (long guild in new[] { m.Blue, m.Red })
            {
                var own = field.Fighters.Values.Where(f => f.Guild == guild).ToArray();
                var special = new DungeonReportSpecialData();
                if (field == m.Main) special.Params.Add([own.Sum(f => (long)f.Kills), own.Sum(f => (long)f.Captures), m.Points[guild], own.Sum(f => (long)f.Flags)]);
                else special.Params.Add([own.Sum(f => (long)f.Kills), own.Sum(f => (long)f.MonsterKills), own.Sum(f => (long)f.BossKills),
                    m.Morale[guild], own.Sum(f => (long)f.CommanderKills)]);
                info.DungeonReportSpecialData[guild] = special;
                var titles = new DungeonTitleData();
                foreach (var (title, value) in Titles(field == m.Main))
                    if (own.Where(f => value(f) > 0).OrderByDescending(value).ThenBy(f => f.Role).FirstOrDefault() is { } best)
                        titles.TitleData[title] = best.Role;
                info.DungeonTitleData[guild] = titles;
            }
            yield return (field.Scene, info);
        }
    }

    /// <summary>End-of-match titles (BattleEventPointConfig 1014-1024 names): Top Score, Most Flags Delivered, Most Bosses
    /// Defeated, Top Capture Points, Most Guard Commanders Defeated, Most Kills, Most Assists, Most Damage Dealt/Taken, Top
    /// Heal. 1024 Fastest Runner needs the distance moved, which is not tracked.</summary>
    private static IEnumerable<(int Title, Func<Fighter, long> Value)> Titles(bool main) =>
    [
        (1014, f => f.Score),
        main ? (1015, f => f.Flags) : (1016, f => f.BossKills),
        main ? (1017, f => f.Captures) : (1018, f => f.CommanderKills),
        (1019, f => f.Kills), (1020, f => f.Assists), (1021, f => f.Damage), (1022, f => f.Taken), (1023, f => f.Heal),
    ];
}
