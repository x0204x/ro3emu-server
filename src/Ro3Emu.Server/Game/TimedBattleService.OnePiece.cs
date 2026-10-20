using Romsg;
using Google.Protobuf;
using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// OnePiece / Commendation Vault: 83350/83351 world window and the Rift stones' private 3200x01 fights.
/// A stone is a party activity: LC_InteractActions.OnePieceEnter refuses without a party (ERR_EnterOnepieceNoTeam) or
/// as a member (ERR_EnterDungeonNotLeader); the leader's 30400 opens the 42033 ready check, whose OnePiece mode
/// (UI_Sys_Dungeon_PreparePanelCtrl._refreshOnePieceInfo) shows the stone, its boss and stars from
/// hiddenTreasureInitData; when everyone confirmed with 30406 the whole party enters one room.
/// </summary>
public sealed partial class TimedBattleService
{
    /// <summary>
    /// PublicParamConfig.OnePieceExtraRewardRestric: RestrictConfig 1020, 10 with _iRecoverType 1 (PBRestrictRecoverType
    /// Daily). Its count is what is left: the dialog, vault and prepare panel show it as "Reward Count"; at 0 the clear
    /// pays no extra award (UIWords 68002 "You will not receive Additional rewards from this challenge").
    /// </summary>
    private const int RiftExtraRestrict = 1020;
    /// <summary>DungeonConfig 3200x01 _iLimitTime is 10 h; the boss fight's own clock (inferred, the contributed value).</summary>
    private const long RiftFightMilliseconds = 180000;

    private sealed class Treasure
    {
        public required GameSession Leader;
        public required GameSession[] Members;
        public required InstanceService.Room Room;
        public required int Monster;
        public required long Started;
        public required RiftStone Stone;
        public HashSet<GameSession> Loaded { get; } = new();
        public MonsterUnit? Boss;
        public bool Spawning;
        public bool Settled;
    }

    /// <summary>A party's 42033 ready check for one stone; the stone is held for it.</summary>
    private sealed class RiftVote
    {
        public required GameSession Leader;
        public required GameSession[] Members;
        public required RiftStone Stone;
        public required long TeamId;
        public HashSet<GameSession> Ready { get; } = new();
        public bool Closed;
    }

    private sealed record TreasurePay(GameSession Player, State Next, TimedBattleObligationRow Obligation,
        AwardGrant[] Grants, CharacterService.FirstCardSightPlan? TaskPlan);

    private readonly Dictionary<GameSession, Treasure> treasure = new();
    private readonly Dictionary<GameSession, RiftVote> riftVotes = new();

    /// <summary>TEST AID set by GM "!rift party [min]" (not original behaviour): the smallest party a stone admits.</summary>
    internal int? RiftTestMinParty { get; set; }

    private long[] StoneInteractions(RiftStone stone)
    {
        if (data.Row("NPCConfig", stone.StoneNpc) is not { } npc || !npc.TryGetProperty("_iInteractID", out var ids)
            || ids.ValueKind != JsonValueKind.Array) return [];
        return ids.EnumerateArray().Select(GameData.Number).Where(x => x != null).Select(x => (long)x!.Value).ToArray();
    }

    /// <summary>32700 for a Rift stone and one of its NPCConfig _iInteractID entries.</summary>
    public async Task<bool> TryInteractAsync(GameSession session, NpcInteractionReq req)
    {
        if (await TryChestInteractAsync(session, req)) return true;
        if (!IsRiftUid(req.NpcUid)) return false;
        using var scope = session.Role != null && loop.Owners is { } owners
            ? await owners.EnterAsync("timed battle onepiece interaction", [ReservationKey.Role(session.Role.Id)]) : null;
        if (session.Owner != null) scope?.Context.Pin(session.Owner);
        var stone = riftStones.GetValueOrDefault(req.NpcUid);
        int result = session.Role == null || stone == null || session.SceneEntry != stone.Map
                || !StoneInteractions(stone).Contains(req.InteractionId)
            ? ErrorCodes.NpcMissing : session.SceneLoading ? ErrorCodes.SwitchingScenes
            : !session.ActivitiesReady ? ErrorCodes.FeatureUnavailable
            : MonsterService.Distance(session.Position.X, session.Position.Z, stone.Point.X, stone.Point.Z) > 10f
                ? ErrorCodes.NpcTooFar
            : session.Progress?.Level < 44 || !schedule.IsOpen(200) || !functions.IsOpen(session, 95)
                ? ErrorCodes.FeatureUnavailable
            : session.InstanceId != 0 || treasure.ContainsKey(session) || riftVotes.ContainsKey(session) ? ErrorCodes.DungeonBusy
            : stone.Challenger != null ? ErrorCodes.RiftStoneTaken : 0;
        // The dialog's Challenge option shows RestrictInfo1020 as "x/10" (_buildOnePieceExtraParams' caller).
        if (result == 0) await SyncRiftRestrictAsync(session);
        await session.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = result, Req = req });
        return true;
    }

    public async Task OnePieceInfoAsync(GameSession session, HiddenTreasureRoundInfoReq _)
    {
        using var scope = session.Role != null && loop.Owners is { } owners
            ? await owners.EnterAsync("timed battle onepiece info", [ReservationKey.Role(session.Role.Id)]) : null;
        if (session.Owner != null) scope?.Context.Pin(session.Owner);
        var (start, end) = TreasureWindow();
        bool open = session.Progress?.Level >= 44 && schedule.IsOpen(200) && functions.IsOpen(session, 95) && riftRound >= 0;
        var response = new HiddenTreasureRoundInfoResp
        {
            Result = session.Role == null ? ErrorCodes.Parameter : 0,
            IsOpen = open,
            CurIndex = open ? (int)((riftRound - start) / RiftRoundSeconds) + 1 : 0,
            // Seconds, like 83350's endTime: UI_Sys_OnepieceWidgetCtrl compares iNextOpenTime with
            // TimerManager.GetSystemTime (seconds) and hands it to Lua_TimeHelper.GetWeekDayNum (os.date).
            NextOpenTime = start > time.GetUtcNow().ToUnixTimeSeconds() ? start : 0,
        };
        for (long round = start, index = 1; round < end; round += RiftRoundSeconds, index++)
            response.TimeMap[(int)index] = new HiddenTreasureRoundInfo
            { Index = (int)index, StartTime = round * 1000, EndTime = Math.Min(end, round + RiftRoundSeconds) * 1000 };
        if (session.Role != null && session.TimedBattle is { } state)
        {
            long week = RiftWeek(RiftDay());
            foreach (var (item, restrict) in RiftVaultItems())
                response.ItemInfo.Add(new HiddenTreasureGainItemInfo
                {
                    ItemId = item, MaxGainCount = RiftRestrictMax(restrict),
                    CurGainCount = state.OnePieceWeek == week ? state.OnePieceWeekItems.GetValueOrDefault(item) : 0
                });
        }
        if (open)
            foreach (var (map, total) in riftMapTotals)
                response.SceneInfo[map] = new HiddenTreasureSceneLineInfo
                { SceneEntry = map, SceneUid = map, LineNum = 1, NpcRemainCount = RiftRemaining(map), NpcRoundCount = total };
        await SyncRiftRestrictAsync(session);
        await session.SendGameAsync(OnePieceInfo, response);
    }

    /// <summary>
    /// UI_Sys_OnepieceWidgetCtrl._updateGargoyles shows itemInfo [1] and [2] as gained/limit (the Special Commendation
    /// and the Commendation Gift Box, PublicParamConfig OnePieceRewardPreview1/2 = [item, RestrictConfig]; UIWords
    /// 68034/68035 call them "This Week's Limit") and [3] as the Standard Compensation Chests left
    /// (OnePieceMapBoxId / OnePieceMapBoxReId). The three RestrictConfig rows are _iRecoverType 2, weekly.
    /// </summary>
    private IEnumerable<(int Item, long Restrict)> RiftVaultItems()
    {
        var table = data.Table("PublicParamConfig");
        foreach (var key in new[] { "OnePieceRewardPreview1", "OnePieceRewardPreview2" })
            if (table.TryGetValue(key, out var pair) && pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2
                && GameData.Number(pair[0]) is { } item && GameData.Number(pair[1]) is { } restrict)
                yield return ((int)item, (long)restrict);
        if (PublicValue("OnePieceMapBoxId") is > 0 and var box) yield return ((int)box, PublicValue("OnePieceMapBoxReId"));
    }

    private long RiftRestrictMax(long id) => data.Row("RestrictConfig", id) is { } row ? GameData.Int(row, "_iMaxRestrictNum") : 0;

    /// <summary>The restriction day turns at 05:00 like the To-Do and life restrictions (inferred), as a DateOnly day number.</summary>
    private long RiftDay() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).DateTime.AddHours(-5)).DayNumber;

    /// <summary>The day number of the week's Monday (inferred week start).</summary>
    private static long RiftWeek(long day) => day - ((int)DateOnly.FromDayNumber((int)day).DayOfWeek + 6) % 7;

    private long RiftDayStart(long day)
    {
        var local = DateOnly.FromDayNumber((int)day).ToDateTime(new TimeOnly(5, 0));
        return new DateTimeOffset(local, time.LocalTimeZone.GetUtcOffset(local)).ToUnixTimeSeconds();
    }

    /// <summary>
    /// RestrictInfo1020 in the 5018 map (LC_DAL_Restrict, LC_RestrictHelper.GetRestrictCount): the extra awards left
    /// today. The TimedBattle state is authoritative; LifeJobService only shows this projection beside its own rows.
    /// </summary>
    private RestrictInfo? RiftRestriction(GameSession session)
    {
        long max = RiftRestrictMax(RiftExtraRestrict);
        if (session.TimedBattle is not { } state || max <= 0) return null;
        long day = RiftDay();
        long used = state.OnePieceExtraDay == day ? state.OnePieceExtraUsed : 0;
        return new RestrictInfo
        {
            Id = RiftExtraRestrict, Count = Math.Max(0, max - used), LimitCount = max,
            HistoryUseCount = used, LastRecoverTime = RiftDayStart(day), NextRecoverTime = RiftDayStart(day + 1)
        };
    }

    private Task SyncRiftRestrictAsync(GameSession session) => life.SyncRestrictionsAsync(session);

    /// <summary>83350 for the player's current map: the tracker's "Remaining on the current channel" and its countdown.</summary>
    private async Task SendOnePieceMapAsync(GameSession session)
    {
        bool open = session.Progress?.Level >= 44 && schedule.IsOpen(200) && functions.IsOpen(session, 95) && riftRound >= 0;
        await session.SendGameAsync(OnePieceMap, new HiddenTreasureMapInfoNotify
        {
            RoundState = open ? EHiddenTreasureRoundState.Open : EHiddenTreasureRoundState.NotOpen,
            NpcCount = open ? RiftRemaining(session.SceneEntry) : 0,
            MaxNpcCount = open ? riftMapTotals.GetValueOrDefault(session.SceneEntry) : 0,
            // Seconds: LC_DAL_OnePiece keeps it as iEndTime, which the task tracker and the vault widget compare with
            // TimerManager.GetSystemTime (seconds); milliseconds showed "Time Limit: 20722840 Days".
            EndTime = RiftRoundEnd()
        });
    }

    private (long Start, long End) TreasureWindow()
    {
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone);
        var row = data.Row("DailyActivityConfig", 200);
        if (row is not { } r || !r.TryGetProperty("_kTime", out var limits)
            || limits.ValueKind != System.Text.Json.JsonValueKind.Array || limits.GetArrayLength() < 2) return (0, 0);
        int from = (int)(GameData.Number(limits[0]) ?? 0), until = (int)(GameData.Number(limits[1]) ?? 0);
        DateTime localStart = now.Date.AddHours(from / 100).AddMinutes(from % 100);
        DateTime localEnd = now.Date.AddHours(until / 100).AddMinutes(until % 100);
        if (localEnd <= localStart) localEnd = localEnd.AddDays(1);
        if (now.DateTime >= localEnd) { localStart = localStart.AddDays(1); localEnd = localEnd.AddDays(1); }
        return (new DateTimeOffset(localStart, time.LocalTimeZone.GetUtcOffset(localStart)).ToUnixTimeSeconds(),
            new DateTimeOffset(localEnd, time.LocalTimeZone.GetUtcOffset(localEnd)).ToUnixTimeSeconds());
    }

    /// <summary>PublicParamConfig.OnePieceMapMembersNum [3,5]: ErrorCodeConfig 6755/6754 refuse a smaller or larger party.</summary>
    private (int Min, int Max) RiftPartySize()
    {
        int min = 3, max = 5;
        if (data.Table("PublicParamConfig").TryGetValue("OnePieceMapMembersNum", out var range)
            && range.ValueKind == JsonValueKind.Array && range.GetArrayLength() == 2)
        { min = (int)(GameData.Number(range[0]) ?? 3); max = Math.Max(min, (int)(GameData.Number(range[1]) ?? 5)); }
        if (RiftTestMinParty is { } test) min = Math.Min(test, max);
        return (min, max);
    }

    /// <summary>
    /// Called ahead of ordinary DungeonService for configured OnePiece dungeon entries only. The leader's request
    /// reaches the whole party (its ready check, then one room), so it is admitted for the realm as DungeonService's
    /// party entry is.
    /// </summary>
    public async Task<bool> TryEnterOnePieceAsync(GameSession session, EnterDungeonReq req)
    {
        if (data.Row("OnePieceRewardConfig", req.SceneEntry) == null) return false;
        using var scope = loop.Owners is { } owners
            ? await owners.EnterAsync("timed battle onepiece entry", [ReservationKey.Realm]) : null;
        if (session.Owner != null) scope?.Context.Pin(session.Owner);
        // LC_SceneManagerNew.SendEnterDungeonWithOnePieceData names the stone by its unit uid; without one (or with a
        // stone NPC / interaction id) the nearest stone of that scene on the player's map is meant.
        var named = req.InitData?.HiddenTreasureInitData?.NpcId ?? 0;
        var stone = riftStones.GetValueOrDefault(named) ?? NearestRiftStone(session, req.SceneEntry);
        int error = session.Role == null || session.Progress == null || session.TimedBattle == null ? ErrorCodes.Parameter
            : !session.ActivitiesReady ? ErrorCodes.FeatureUnavailable
            : session.Progress.Level < 44 || !schedule.IsOpen(200) || !functions.IsOpen(session, 95)
                ? ErrorCodes.FeatureUnavailable
            : stone == null || session.SceneEntry != stone.Map ? ErrorCodes.NpcMissing
            : MonsterService.Distance(session.Position.X, session.Position.Z, stone.Point.X, stone.Point.Z) > 10f
                ? ErrorCodes.NpcTooFar
            : session.SceneLoading || session.InstanceId != 0 || session.Dead || treasure.ContainsKey(session)
                || riftVotes.ContainsKey(session) ? ErrorCodes.DungeonBusy
            : stone.Challenger != null ? ErrorCodes.RiftStoneTaken
            : named != 0 && !riftStones.ContainsKey(named) && !IsStoneReference(named) || stone.Scene != req.SceneEntry
                ? ErrorCodes.Parameter : 0;
        GameSession[] members = [];
        long teamId = 0;
        if (error == 0) (error, members, teamId) = RiftParty(session, stone!);
        if (error == 0 && req.InitData?.HiddenTreasureInitData is { PlayerIds.Count: > 0 } init
            && !init.PlayerIds.Order().SequenceEqual(members.Select(m => m.Role!.Id).Order())) error = ErrorCodes.Parameter;
        await session.SendGameAsync(DungeonService.Enter, new EnterDungeonRsp { Result = error });
        if (error != 0) return true;
        stone!.Challenger = session;
        PostRiftState(stone);
        if (members.Length == 1) { await BeginTreasureAsync(session, members, stone); return true; }
        var vote = new RiftVote { Leader = session, Members = members, Stone = stone, TeamId = teamId };
        vote.Ready.Add(session); // Asking is the leader's own answer, as DungeonService's ready check (inferred).
        foreach (var member in members) riftVotes[member] = vote;
        await BroadcastRiftVoteAsync(vote);
        // The prepare panel counts down GetInPositionMaxTime from the first 42033 (LC_DAL_Team).
        long seconds = PublicValue("GetInPositionMaxTime") is > 0 and var configured ? configured : 30;
        loop.ScheduleRuntime(TimeSpan.FromSeconds(seconds),
            () => vote.Closed ? Task.CompletedTask : CancelRiftVoteAsync(vote, ErrorCodes.Parameter, 0));
        return true;
    }

    private (int Error, GameSession[] Members, long TeamId) RiftParty(GameSession leader, RiftStone stone)
    {
        long roleId = leader.Role!.Id;
        var (teamId, head) = teams.TeamOf(roleId);
        if (teamId == 0) return (ErrorCodes.RiftNoParty, [], 0);
        if (head != roleId) return (ErrorCodes.NotPartyLeader, [], 0);
        var ids = teams.MembersOf(roleId);
        var (min, max) = RiftPartySize();
        if (ids.Count < min) return (ErrorCodes.RiftPartyTooSmall, [], 0);
        if (ids.Count > max) return (ErrorCodes.RiftPartyTooLarge, [], 0);
        var online = sessions.All().Where(p => p.Role != null).GroupBy(p => p.Role!.Id).ToDictionary(g => g.Key, g => g.First());
        var members = new List<GameSession>();
        foreach (long id in ids)
        {
            if (!online.TryGetValue(id, out var member)) return (ErrorCodes.PlayerOffline, [], 0);
            int error = RiftMemberError(member, stone, null);
            if (error != 0) return (error, [], 0);
            members.Add(member);
        }
        return (0, members.ToArray(), teamId);
    }

    /// <summary>
    /// Each member (the leader too) must be free, unlocked (level 44 and function 95, ErrorCodeConfig 6758) and on the
    /// stone's map alive: the party enters from where the stone is (inferred; 6751 "not eligible").
    /// </summary>
    private int RiftMemberError(GameSession member, RiftStone stone, RiftVote? own) =>
        treasure.ContainsKey(member) || riftVotes.GetValueOrDefault(member) is { } vote && vote != own
            ? ErrorCodes.RiftMemberInVault
        : member.SceneLoading || member.InstanceId != 0 ? ErrorCodes.DungeonBusy
        : !member.ActivitiesReady || member.Progress == null || member.TimedBattle == null || member.Progress.Level < 44
            || !functions.IsOpen(member, 95) ? ErrorCodes.RiftMemberLocked
        : member.Dead || member.SceneEntry != stone.Map ? ErrorCodes.RiftMemberIneligible : 0;

    private static HiddenTreasureInitData RiftInit(RiftStone stone, IEnumerable<GameSession> members, long waveEnd)
    {
        // npcEntryId is the stone's NPCConfig id: the prepare and settlement panels look its stars up in
        // OnePieceMonsterConfig (GetStarLevel) and name the boss from monsterEntryId.
        var init = new HiddenTreasureInitData
        {
            NpcSceneId = stone.Map, NpcId = stone.Uid, NpcEntryId = stone.StoneNpc,
            MonsterEntryId = stone.Boss, WaveEndTime = waveEnd
        };
        init.PlayerIds.Add(members.Where(m => m.Role != null).Select(m => m.Role!.Id));
        return init;
    }

    private Task BroadcastRiftVoteAsync(RiftVote vote, bool cancel = false, int error = 0, long role = 0)
    {
        var msg = new TeamEnterReadyQueueNotify
        {
            DungeonId = vote.Stone.Scene, Cancel = cancel, CancelErrorCode = error,
            CancelRoleId = role, DungeonInitData = new DungeonInitData { HiddenTreasureInitData = RiftInit(vote.Stone, vote.Members, 0) }
        };
        foreach (var member in vote.Members) if (member.Role != null) msg.ReadyQueue[member.Role.Id] = vote.Ready.Contains(member);
        return Task.WhenAll(vote.Members.Where(m => m.Role != null).Select(m => SendRiftQuietAsync(m, DungeonService.ReadyQueue, msg)));
    }

    private async Task SendRiftQuietAsync(GameSession session, uint id, IMessage msg)
    {
        try { await session.SendGameAsync(id, msg); }
        catch (Exception e) when (e is not OperationCanceledException)
        { log.LogWarning(e, "Rift notification {Message} failed for {Role}", id, session.Role?.Id); }
    }

    private async Task CancelRiftVoteAsync(RiftVote vote, int error, long role)
    {
        if (vote.Closed) return;
        vote.Closed = true;
        foreach (var member in vote.Members) if (riftVotes.GetValueOrDefault(member) == vote) riftVotes.Remove(member);
        if (riftStones.GetValueOrDefault(vote.Stone.Uid) == vote.Stone && vote.Stone.Challenger == vote.Leader
            && !treasure.ContainsKey(vote.Leader))
        { vote.Stone.Challenger = null; PostRiftState(vote.Stone); }
        await BroadcastRiftVoteAsync(vote, cancel: true, error, role);
    }

    private bool RiftVoteChanged(RiftVote vote) => vote.Leader.Role is not { } leader
        || teams.TeamOf(leader.Id) != (vote.TeamId, leader.Id)
        || !teams.MembersOf(leader.Id).Order().SequenceEqual(vote.Members.Select(m => m.Role?.Id ?? 0).Order());

    /// <summary>30406 from a member of a Rift ready check; false leaves the message to DungeonService.</summary>
    public async Task<bool> TryConfirmRiftAsync(GameSession session, ConfirmTeamDungeonReq req)
    {
        if (riftVotes.GetValueOrDefault(session) is not { } vote) return false;
        int error = vote.Closed ? ErrorCodes.Parameter : RiftVoteChanged(vote) ? ErrorCodes.MemberLeft : 0;
        await session.SendGameAsync(DungeonService.Confirm, new ConfirmTeamDungeonRsp { Result = error });
        if (vote.Closed) return true;
        if (error != 0 || !req.IsAgree)
        {
            await CancelRiftVoteAsync(vote, error != 0 ? error : ErrorCodes.DungeonDeclined, session.Role?.Id ?? 0);
            return true;
        }
        vote.Ready.Add(session);
        await BroadcastRiftVoteAsync(vote);
        if (vote.Ready.Count != vote.Members.Length) return true;
        // Everyone is checked again after the last answer; the stone must still be this party's.
        foreach (var member in vote.Members)
        {
            error = !sessions.All().Contains(member) ? ErrorCodes.PlayerOffline
                : riftRound < 0 || riftStones.GetValueOrDefault(vote.Stone.Uid) != vote.Stone
                    || vote.Stone.Challenger != vote.Leader ? ErrorCodes.NpcMissing
                : RiftMemberError(member, vote.Stone, vote);
            if (error != 0) { await CancelRiftVoteAsync(vote, error, member.Role?.Id ?? 0); return true; }
        }
        vote.Closed = true;
        foreach (var member in vote.Members) riftVotes.Remove(member);
        await BeginTreasureAsync(vote.Leader, vote.Members, vote.Stone);
        return true;
    }

    /// <summary>42047 from the leader of a Rift ready check (PreparePanel's remind button), as DungeonService.RemindAsync.</summary>
    public async Task<bool> TryRemindRiftAsync(GameSession session, TeamConfirmRemindersReq req)
    {
        if (riftVotes.GetValueOrDefault(session) is not { } vote) return false;
        var target = vote.Members.FirstOrDefault(m => m.Role?.Id == req.RemindersGuid);
        int code = vote.Closed || vote.Leader != session || target == null || vote.Ready.Contains(target)
            || req.PlayerId != session.Role?.Id || req.RemindersGuid == session.Role?.Id ? ErrorCodes.NoPermission : 0;
        await session.SendGameAsync(DungeonService.ConfirmReminder, new TeamConfirmRemindersResp { Result = code });
        if (code == 0) await SendRiftQuietAsync(target!, DungeonService.ConfirmReminderNotify,
            new TeamConfirmRemindersNotify { MemberGuid = session.Role!.Id });
        return true;
    }

    private RiftStone? NearestRiftStone(GameSession session, long scene) => riftStones.Values
        .Where(s => s.Map == session.SceneEntry && s.Scene == scene)
        .MinBy(s => MonsterService.Distance(session.Position.X, session.Position.Z, s.Point.X, s.Point.Z));

    /// <summary>A stone NPC (16100001-16100006) or one of its interaction ids.</summary>
    private bool IsStoneReference(long id) => data.Table("OnePieceMonsterConfig").Values.Any(r =>
        GameData.Int(r, "_iId") == id || data.Row("NPCConfig", GameData.Int(r, "_iId")) is { } npc
            && npc.TryGetProperty("_iInteractID", out var ids) && ids.ValueKind == JsonValueKind.Array
            && ids.EnumerateArray().Any(x => GameData.Number(x) == id));

    /// <summary>The party's room; the stone stays claimed by the leader until the fight ends.</summary>
    private async Task BeginTreasureAsync(GameSession leader, GameSession[] members, RiftStone stone)
    {
        InstanceService.Room room;
        try { room = instances.Create(stone.Scene, members); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "OnePiece room create failed for {Role}", leader.Role?.Id);
            if (riftStones.GetValueOrDefault(stone.Uid) == stone && stone.Challenger == leader)
            { stone.Challenger = null; PostRiftState(stone); }
            return;
        }
        var a = new Treasure
        {
            Leader = leader, Members = members, Room = room, Monster = stone.Boss, Stone = stone,
            Started = time.GetUtcNow().ToUnixTimeMilliseconds()
        };
        foreach (var member in members) treasure[member] = a;
        foreach (var member in members)
        {
            try { if (await instances.EnterAsync(room, member, members.Length > 1 ? 5 : 3)) continue; }
            catch (Exception e) when (e is not OperationCanceledException)
            { log.LogWarning(e, "OnePiece transfer failed for {Role}, instance {Instance}", member.Role?.Id, room.Id); }
            if (treasure.GetValueOrDefault(member) == a) treasure.Remove(member);
        }
        if (!members.Any(m => treasure.GetValueOrDefault(m) == a)) { a.Settled = true; ReleaseRiftStone(a, cleared: false); return; }
        // A client that never acknowledges the scene cannot hold the shared stone (60 s as DungeonService, inferred):
        // without a loaded member the run fails, a member still loading is sent back.
        loop.ScheduleRuntime(TimeSpan.FromSeconds(60), async () =>
        {
            if (a.Settled) return;
            if (a.Boss == null) { await SettleTreasureAsync(a, false); return; }
            foreach (var member in Present(a).Where(m => !a.Loaded.Contains(m))) await instances.LeaveAsync(member);
        });
    }

    /// <summary>Members of the run still in its room (dead or alive).</summary>
    private GameSession[] Present(Treasure a) => a.Members
        .Where(m => treasure.GetValueOrDefault(m) == a && m.InstanceId == a.Room.Id && m.Role != null).ToArray();

    private async Task OnePieceLoadedAsync(GameSession session)
    {
        if (session.InstanceId == 0)
        {
            await RiftViewAsync(session, [], riftStones.Values.Where(s => s.Map == session.SceneEntry).ToArray());
            await SendOnePieceMapAsync(session);
        }
        if (!treasure.TryGetValue(session, out var a) || a.Room.Id != session.InstanceId || a.Settled) return;
        a.Loaded.Add(session);
        var init = new DungeonInitData { HiddenTreasureInitData = RiftInit(a.Stone, a.Members, a.Started + RiftFightMilliseconds) };
        await session.SendGameAsync(DungeonService.BaseInfo, new DungeonBaseInfoNotify
        {
            DungeonEntry = a.Room.SceneEntry, DungeonId = a.Room.Id, Stage = 1, InitData = init,
            ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2,
            StageModuleData = new DungeonStageModuleData
            {
                MapState = 1, Stage = 1,
                StageStartTime = a.Started, StageEndTime = a.Started + RiftFightMilliseconds
            }
        });
        // The first member in raises the boss; later ones join the fight.
        if (a.Boss != null || a.Spawning) return;
        var map = worldData.Map(a.Room.SceneEntry);
        MapPoint? point = map?.DungeonGroups.Values.SelectMany(x => x).FirstOrDefault() is { } spawn && spawn.Id != 0
            ? new MapPoint(spawn.X, spawn.Y, spawn.Z, spawn.Face) : map?.Born;
        if (point == null || a.Monster <= 0) return;
        a.Spawning = true;
        try { a.Boss = await instances.SpawnAsync(a.Room, a.Monster, point.Value); }
        finally { a.Spawning = false; }
        loop.ScheduleRuntime(TimeSpan.FromMilliseconds(RiftFightMilliseconds), async () =>
        { if (!a.Settled) await SettleTreasureAsync(a, false); });
    }

    /// <summary>A member who leaves gives up its share; the run ends when nobody is left or everyone left is dead.</summary>
    private Task OnePieceLeavingAsync(GameSession session)
    {
        if (!treasure.TryGetValue(session, out var a) || session.InstanceId != a.Room.Id) return Task.CompletedTask;
        treasure.Remove(session);
        a.Loaded.Remove(session);
        if (a.Settled) return Task.CompletedTask;
        var rest = Present(a).Where(m => m != session).ToArray();
        if (rest.Length == 0) { a.Settled = true; ReleaseRiftStone(a, cleared: false); }
        else if (rest.All(m => m.Dead)) loop.PostRuntime(() => a.Settled ? Task.CompletedTask : SettleTreasureAsync(a, false));
        return Task.CompletedTask;
    }

    /// <summary>
    /// DungeonConfig 3200x01 _iReviveTrigger 2 (enum_ReviveTriggerType.NoRevive): the client offers no self-revive,
    /// only "Help!" (LC_SceneCom_Revive.IsEnableSOS), so a fallen member waits for a party member's resurrection. The
    /// run fails 3 s after everyone in the room is down (inferred, as an ordinary run fails when every present member is
    /// dead and out of revivals) and the stone becomes free again.
    /// </summary>
    private Task OnePieceDiedAsync(GameSession player)
    {
        if (treasure.GetValueOrDefault(player) is { } a && !a.Settled && a.Room.Id == player.InstanceId)
            loop.ScheduleRuntime(TimeSpan.FromSeconds(3), async () =>
            {
                if (!a.Settled && Present(a) is { Length: > 0 } present && present.All(m => m.Dead))
                    await SettleTreasureAsync(a, false);
            });
        return Task.CompletedTask;
    }

    /// <summary>NoRevive: a dead member's own 31013 is refused; skill and item resurrection by others stays open.</summary>
    private bool InRiftFight(GameSession session) =>
        treasure.GetValueOrDefault(session) is { Settled: false } a && a.Room.Id == session.InstanceId;

    private async Task OnePieceKilledAsync(GameSession killer, MonsterUnit monster)
    {
        if (treasure.GetValueOrDefault(killer) is { } a && !a.Settled && a.Room.Id == monster.InstanceId
            && a.Boss?.Uid == monster.Uid) await SettleTreasureAsync(a, true);
    }

    private static string TreasureSource(Treasure a, long roleId) =>
        $"timed_battle/onepiece/{a.Room.Id.ToString(CultureInfo.InvariantCulture)}/{roleId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A win pays every member still in the room (a fallen one too), each against its own restrictions.</summary>
    private async Task SettleTreasureAsync(Treasure a, bool win)
    {
        if (a.Settled) return;
        var present = Present(a);
        var roleIds = present.ToDictionary(m => m, m => m.Role!.Id);
        var keys = new[] { ReservationKey.Entity("timed_onepiece", a.Room.Id.ToString(CultureInfo.InvariantCulture)) }
            .Concat(roleIds.Values.Select(id => ReservationKey.Role(id)))
            .Concat(roleIds.Values.Select(id => Obligations.Key(TreasureSource(a, id))))
            .ToArray();
        TreasurePay[] paid = [];
        using (var scope = loop.Owners is { } owners
            ? await owners.EnterAsync("timed battle onepiece settlement", keys) : null)
        {
            if (a.Settled) return;
            present = present.Where(m => m.Role?.Id == roleIds[m] && treasure.GetValueOrDefault(m) == a
                && m.InstanceId == a.Room.Id).ToArray();
            if (win)
            {
                try
                {
                    paid = present.Where(m => m.ActivitiesReady && m.TimedBattle != null && m.Progress != null)
                        .Select(m => StageTreasureReward(a, m)).ToArray();
                    if (paid.Length == 0) win = false;
                    else await CommitTreasureAsync(a, paid);
                }
                catch (AwardResolutionException e)
                { log.LogWarning(e, "OnePiece award rejected for room {Room}", a.Room.Id); win = false; paid = []; }
            }
            if (!win)
            {
                a.Settled = true;
                ReleaseRiftStone(a, cleared: false);
                var lost = TreasureResult(a, false, [], [], false);
                foreach (var member in present) await SendRiftQuietAsync(member, DungeonService.Result, lost);
            }
        }
        await RoleFanout.EachAsync(paid.Select(p => p.Player), async recipient =>
        {
            var pay = paid.First(p => p.Player == recipient);
            try
            {
                var deliveryKeys = new[] { ReservationKey.Role(pay.Obligation.RoleId), Obligations.Key(pay.Obligation.SourceKey) };
                if (loop.Owners is { } owners)
                    await owners.RunSessionAsync("timed battle onepiece delivery", deliveryKeys, recipient,
                        () => DeliverOneAsync(recipient, pay.Obligation));
                else await DeliverOneAsync(recipient, pay.Obligation);
            }
            catch (OperationCanceledException) { throw; /* Login will deliver this frozen obligation. */ }
            catch (Exception error) { log.LogError(error, "OnePiece delivery deferred for role {Role}", pay.Obligation.RoleId); }
        });
        foreach (var member in a.Members) if (treasure.GetValueOrDefault(member) == a) treasure.Remove(member);
        // A fallen member comes back alive outside, as TowerService returns its runs (the fight has no revive).
        foreach (var member in present)
            if (member.InstanceId == a.Room.Id && !(member.Dead && await instances.LeaveAsync(member, recoverDeadOnReturn: true)))
                await instances.LeaveAsync(member);
    }

    /// <summary>
    /// OnePieceMonsterConfig pays by star: _iAward (9101-9106) always; _iExtraAward (9201-9206) while RestrictConfig 1020
    /// has a count left today, each item within its AwardRestricConfig weekly cap. An extra award that grants at least
    /// one item spends one count (inferred). OnePieceRewardConfig names 9107-9110, which MultiverseGroupConfig does not have.
    /// </summary>
    private TreasurePay StageTreasureReward(Treasure a, GameSession member)
    {
        long roleId = member.Role!.Id;
        var next = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(member.TimedBattle!))!;
        long day = RiftDay(), week = RiftWeek(day);
        next.OnePieceGains = null; next.OnePieceItemGains = null;
        if (next.OnePieceExtraDay != day) { next.OnePieceExtraDay = day; next.OnePieceExtraUsed = 0; }
        if (next.OnePieceWeek != week) { next.OnePieceWeek = week; next.OnePieceWeekItems.Clear(); }
        long elapsed = Math.Max(1, time.GetUtcNow().ToUnixTimeMilliseconds() - a.Started);
        long previous = next.OnePieceBestMilliseconds.GetValueOrDefault(a.Room.SceneEntry);
        bool record = previous == 0 || elapsed < previous;
        if (record) next.OnePieceBestMilliseconds[a.Room.SceneEntry] = elapsed;
        var reward = StarRow(a.Stone.Star) ?? throw new AwardResolutionException($"OnePiece star {a.Stone.Star} has no row.");
        var baseGrants = awards.Resolve(member, GameData.Int(reward, "_iAward"), 101).Grants.ToArray();
        var extraGrants = new List<AwardGrant>();
        if (next.OnePieceExtraUsed < RiftRestrictMax(RiftExtraRestrict))
        {
            foreach (var item in RestrictedExtra(GameData.Int(reward, "_iExtraAward")))
            {
                int prior = next.OnePieceWeekItems.GetValueOrDefault(item.Grant.Entry);
                int count = item.Cap is { } cap ? Math.Min(item.Grant.Count, Math.Max(0, cap - prior)) : item.Grant.Count;
                if (count <= 0) continue;
                extraGrants.Add(item.Grant with { Count = count });
                next.OnePieceWeekItems[item.Grant.Entry] = prior + count;
            }
            if (extraGrants.Count > 0) next.OnePieceExtraUsed++;
        }
        var grants = baseGrants.Concat(extraGrants).ToArray();
        if (grants.Length == 0) throw new AwardResolutionException("OnePiece has no resolved reward.");
        string source = TreasureSource(a, roleId);
        var obligation = new TimedBattleObligationRow(source, a.Room.Id, checked((int)a.Room.SceneEntry), roleId,
            JsonSerializer.SerializeToUtf8Bytes(grants), JsonSerializer.SerializeToUtf8Bytes(Array.Empty<object>()),
            JsonSerializer.SerializeToUtf8Bytes(next), TreasureResult(a, true, baseGrants, extraGrants, record).ToByteArray());
        return new TreasurePay(member, next, obligation, grants, tasks.StageOnePieceReward(member, source));
    }

    private async Task CommitTreasureAsync(Treasure a, TreasurePay[] paid)
    {
        var tx = roles.NewTransaction();
        foreach (var pay in paid)
        {
            tx.RoleBlob(pay.Obligation.RoleId, "timed_battle", pay.Obligation.NextState);
            pay.TaskPlan?.Persist(tx);
        }
        tx.Add(Obligations.CreatePlan(paid.Select(p => p.Obligation)));
        var economic = paid.SelectMany(p => p.Grants.Where(g => g.Entry is not (100 or 101)).Select(g => (Pay: p, Grant: g))).ToArray();
        if (economic.Length > 0)
        {
            var catalog = EventEconomy.Catalog(data);
            EconomyAsset Asset(int entry) => catalog.TryAsset(entry, out var asset) ? asset
                : throw new InvalidDataException($"OnePiece grant {entry} is not catalogued.");
            tx.WithEventEconomy(new EventEconomyMutation(catalog, Guid.NewGuid().ToString("N"),
                "realm", 0, "event/timed-battle-onepiece", $"timed_battle/onepiece/{a.Room.Id.ToString(CultureInfo.InvariantCulture)}",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [],
                economic.Select(x => new EconomyQuantity(Asset(x.Grant.Entry), x.Grant.Count)).ToArray(),
                economic.Select(x => new EventEconomyOwnerDelta(new EconomyOwner(EconomyOwnerKind.Role,
                    x.Pay.Obligation.RoleId.ToString(CultureInfo.InvariantCulture)), Asset(x.Grant.Entry), x.Grant.Count)).ToArray(),
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    a.Started, Scene = a.Room.SceneEntry,
                    Recipients = paid.Select(p => p.Obligation.RoleId).ToArray()
                })));
        }
        await roles.CommitAsync(tx, "timed-battle/onepiece-obligation", _ =>
        {
            foreach (var pay in paid)
            {
                pay.Player.TimedBattle = pay.Next;
                pay.TaskPlan?.Apply();
                pendingDeliveryRoles.Add(pay.Obligation.RoleId);
            }
            a.Settled = true;
            ReleaseRiftStone(a, cleared: true);
        });
    }

    private DungeonResNotify TreasureResult(Treasure a, bool win, IEnumerable<AwardGrant> baseGrants,
        IEnumerable<AwardGrant> extraGrants, bool record)
    {
        var res = new OnePieceDungeonResData
        { PassTime = time.GetUtcNow().ToUnixTimeMilliseconds() - a.Started, BossLevel = a.Stone.Star, IsNewRecord = win && record };
        // LC_DunGeon_OnePiece.CELL_DUNGEON_RES_NOTIFY lists these as the settlement's normal and extra rewards.
        res.BaseReward.Add(baseGrants.Select(g => new ItemBaseInfo { ItemEntry = g.Entry, Stack = g.Count }));
        res.ExtraReward.Add(extraGrants.Select(g => new ItemBaseInfo { ItemEntry = g.Entry, Stack = g.Count }));
        return new DungeonResNotify
        {
            DungeonEntry = a.Room.SceneEntry, Res = (DungeonRes)(win ? 1 : 2),
            Data = new DungeonResData { OnePieceData = res }
        };
    }

    private long TreasureDay()
    {
        var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone);
        return now.Year * 10000L + now.Month * 100 + now.Day;
    }

    private sealed record RestrictedItem(AwardGrant Grant, int? Cap);

    /// <summary>OnePiece's fixed extra AwardConfig row has explicit per-item weekly restrictions and no replacement.</summary>
    private List<RestrictedItem> RestrictedExtra(long group)
    {
        if (data.Row("MultiverseGroupConfig", group) is not { } route || GameData.Int(route, "_itype") != 0)
            throw new AwardResolutionException($"OnePiece extra group {group} is invalid");
        long id = GameData.Int(route, "_iValue_101");
        if (id <= 0) id = GameData.Int(route, "_iValue_default");
        if (data.Row("AwardConfig", id) is not { ValueKind: JsonValueKind.Array } rows || rows.GetArrayLength() != 1
            || !rows[0].TryGetProperty("_kAward", out var fixedItems) || fixedItems.ValueKind != JsonValueKind.Array
            || GameData.Int(rows[0], "_iDropPool") != 0)
            throw new AwardResolutionException($"OnePiece extra award {id} is not one fixed row");
        var result = new List<RestrictedItem>();
        foreach (var item in fixedItems.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3
                || GameData.Number(item[0]) is not { } entryNumber || GameData.Number(item[1]) is not { } countNumber
                || GameData.Number(item[2]) != 0)
                throw new AwardResolutionException($"OnePiece extra award {id} has unsupported item tuple");
            int entry = checked((int)entryNumber), count = checked((int)countNumber);
            if (entry <= 0 || count <= 0 || data.Row("ItemConfig", entry) == null)
                throw new AwardResolutionException($"OnePiece extra award {id} has invalid item {entry}");
            var restrictions = data.Table("AwardRestricConfig").Values.Where(r =>
                GameData.Int(r, "_iRewardID") == id && GameData.Int(r, "_iItemID") == entry).ToArray();
            if (restrictions.Length > 1) throw new AwardResolutionException($"OnePiece item {entry} has multiple restrictions");
            int? cap = null;
            if (restrictions.Length == 1)
            {
                var restriction = restrictions[0];
                long restrictId = GameData.Int(restriction, "_iRestricid");
                var rule = data.Row("RestrictConfig", restrictId);
                long max = rule is { } configured ? GameData.Int(configured, "_iMaxRestrictNum") : 0;
                // _iRecoverType 2 is PBRestrictRecoverType_Weekly.
                if (GameData.Int(restriction, "_iReplaceAward") != 0 || rule == null
                    || GameData.Int(rule.Value, "_iRecoverType") != 2 || max is <= 0 or > int.MaxValue)
                    throw new AwardResolutionException($"OnePiece item {entry} restriction is unsupported");
                cap = (int)max;
            }
            result.Add(new RestrictedItem(new AwardGrant(entry, count), cap));
        }
        return result;
    }

    private void TickOnePiece(long now) => TickRift();
}
