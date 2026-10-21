using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Realm of the Gods extras (TenGameplayConfig 100101-100103, playbook 101):
/// <list type="bullet">
/// <item>82583 hall of fame: the server's first team clear of a boss (the group widget asks on every boss it shows,
/// LC_DAL_MultiPlayerDungeon.GetHallOfFameInfo; UI_Sys_Dungeon_GroupWidgetCtrl._openAchievement says "No Team First Clear
/// Yet", UIWords 56287, when passTimestamp is 0). Kept as DBMultiDungeonServerInfo.hallOfFame in server_state.</item>
/// <item>Sacred Blessings (TenGameplayBossConfig _kBlessingID/_iBlessingBuffUnlockType/_kBlessingBuffUnlock): unlocked by
/// the server's first clear (condition 3034 CheckBOSSFirstDay [boss, days], "Unlocks on day N after the Current Server's
/// First Clear", LC_InteractCondition.CheckBOSSFirstDayDes); its TenGameplayBuffConfig level rises with _iUnlockDay counted
/// from firstUnlockTime (UI_Com_Tips_Dungeon_ListWidgetCtrl.SetData), and its buffs and extra revives apply in the run.
/// 82588 acknowledges the unlock tip (UI_Sys_Dungeon_GroupWidgetCtrl._openSettlementPanel).</item>
/// <item>Final Trial honors (_kFinalTrialID, TenGameplayFinalTrialConfig): earned on a clear from their LC_ConditionList
/// goals, shown in 5062 and 82584, claimed with 82589 (UI_Sys_Dungeon_RightInfoWidgetCtrl._getDungeonUltimateReward).</item>
/// <item>30125 Start Battle: the raids' DungeonBase.FunctionButtons["1"] (dungeon var 1001 PFunctionBtnList,
/// LC_SceneCom_CustomInteractBtn) ends the preparation phase.</item>
/// </list>
/// </summary>
public sealed partial class DungeonService
{
    public const uint HallOfFame = 82583, MarkBlessings = 82588, ClaimHonor = 82589, ModuleButton = 30125;
    internal const string ServerInfoKey = "multi_dungeon_server";

    /// <summary>
    /// DungeonLua_3020101/3020102/3020103/3020104/3020108 DungeonBase.FunctionButtons["1"]: "Start Battle" (UIWords 44136),
    /// receivecondition [1] IsTeamLeader, popup 44131/44067 "Skip the preparation phase and start the challenge?". The
    /// DungeonLua modules are not in the exported tables, so their one button is listed here.
    /// </summary>
    internal static readonly long[] StartBattleScenes = [3020101, 3020102, 3020103, 3020104, 3020108];
    private const int StartBattleButton = 1, FunctionButtonVar = 1001;
    // DBMultiDungeonHonorState (MG_DB.proto): NotFinish 0, Finish 1, Received 2.
    private const int HonorFinished = 1, HonorReceived = 2;
    private const long Day = 86400;

    private DBMultiDungeonServerInfo? _serverInfo;

    /// <summary>The realm's Realm of the Gods records (server_state "multi_dungeon_server", base64 protobuf).</summary>
    internal DBMultiDungeonServerInfo ServerInfo => _serverInfo ??= _accounts.ReadSync<string?>(new SeasonStateReadPlan(ServerInfoKey))
        is { Length: > 0 } saved ? DBMultiDungeonServerInfo.Parser.ParseFrom(Convert.FromBase64String(saved)) : new DBMultiDungeonServerInfo();

    private async Task SaveServerInfoAsync(DBMultiDungeonServerInfo next)
    {
        await _accounts.WriteLegacyAsync<int>(new SeasonStateWritePlan(ServerInfoKey, Convert.ToBase64String(next.ToByteArray())));
        _serverInfo = next;
    }

    private static Ownership.ReservationKey ServerInfoReservation =>
        Ownership.ReservationKey.Entity("server_state", ServerInfoKey);

    // ---- hall of fame ----

    public Task HallOfFameAsync(GameSession s, QueryHallOfFameInfoReq req) => s.SendGameAsync(HallOfFame,
        new QueryHallOfFameInfoResp
        {
            MultiDungeonBossId = req.MultiDungeonBossId,
            HallOfFameInfo = ServerInfo.HallOfFame.TryGetValue(req.MultiDungeonBossId, out var record) ? record.Clone() : null
        });

    /// <summary>
    /// The first won run of a boss on this realm becomes its hall of fame entry: the team leader, the time and every
    /// participant's card, equipment and properties as they were at the clear (DBMultiDungeonHallOfFame). Later clears
    /// never replace it. Written by its own operation after the settlement.
    /// </summary>
    private void RecordFirstClear(Attempt a, JsonElement boss, GameSession[] party)
    {
        int bossId = (int)GameData.Int(boss, "_iBoss");
        if (ServerInfo.HallOfFame.ContainsKey(bossId) || party.Length == 0) return;
        var leader = party.FirstOrDefault(s => s.Role!.Id == a.Leader) ?? party[0];
        var record = new DBMultiDungeonHallOfFame
        {
            MultiDungeonBossId = bossId,
            PassTime = (int)Math.Min(int.MaxValue, (_loop.Now - a.StartedAt).TotalSeconds),
            PassTimestamp = _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            TeamLeaderId = leader.Role!.Id, TeamLeaderName = leader.Role.Name
        };
        // The MVP panel shows up to ten roles (UI_Sys_Dungeon_Group_MvpPanelCtrl m_kWT_Role01..10), leader first.
        foreach (var member in party.OrderBy(s => s == leader ? 0 : 1).ThenBy(s => Slot(a, s.Role!.Id)).Take(10))
            record.RoleInfo.Add(_character.DetailInfo(member));
        _loop.TrackRuntimeDetached("dungeon hall of fame", () => StoreFirstClearAsync(record));
    }

    internal async Task StoreFirstClearAsync(DBMultiDungeonHallOfFame record, bool replace = false)
    {
        using (_loop.Owners is { } owners ? await owners.EnterAsync("dungeon/hall-of-fame", ServerInfoReservation) : null)
        {
            if (!replace && ServerInfo.HallOfFame.ContainsKey(record.MultiDungeonBossId)) return;
            var next = ServerInfo.Clone();
            next.HallOfFame[record.MultiDungeonBossId] = record;
            await SaveServerInfoAsync(next);
        }
        // A first clear can unlock a Sacred Blessing for everyone: refresh the online 5062s.
        foreach (var session in _sessions.All().Where(s => s.Dungeons != null && s.Role != null))
            _loop.PostOwned(session, () => SyncAsync(session), "dungeon blessing sync");
    }

    // ---- Sacred Blessings ----

    /// <summary>The boss whose first clear unlocks a blessing and the days after it (LC_ConditionList [3034, boss, days]).</summary>
    private (int Boss, long Days)? FirstClearCondition(long conditionId)
    {
        if (_data.Row("LC_ConditionList", conditionId) is not { } condition
            || !condition.TryGetProperty("m_kSubConditionList", out var subs) || subs.ValueKind != JsonValueKind.Array) return null;
        foreach (var sub in subs.EnumerateArray())
            if (sub.TryGetProperty("m_kData", out var args) && args.ValueKind == JsonValueKind.Array && args.GetArrayLength() >= 2
                && GameData.Number(args[0]) == 3034)
                return ((int)(GameData.Number(args[1]) ?? 0), args.GetArrayLength() > 2 ? (long)(GameData.Number(args[2]) ?? 0) : 0);
        return null;
    }

    /// <summary>
    /// A boss's Sacred Blessing now: null before the server's first clear of its unlock boss. firstUnlockTime is that clear
    /// plus the condition's days (inferred: the level list counts each level's _iUnlockDay from it, and 14720010's day-2
    /// unlock would otherwise come after its own day-1 level); the level is the highest whose day has passed.
    /// </summary>
    internal (DBMultiDungeonSacredBlessings Info, JsonElement? Row)? Blessing(JsonElement boss)
    {
        long group = GameData.Int(boss, "_kBlessingID");
        // enum_BlessingUnlockType: Open 0, FirstPass 1. Every blessing of this client is FirstPass.
        if (group == 0 || GameData.Int(boss, "_iBlessingBuffUnlockType") != 1
            || FirstClearCondition(GameData.Int(boss, "_kBlessingBuffUnlock")) is not var (unlockBoss, days)
            || !ServerInfo.HallOfFame.TryGetValue(unlockBoss, out var first)) return null;
        long unlock = first.PassTimestamp / 1000 + days * Day;
        var info = new DBMultiDungeonSacredBlessings
        {
            FirstPassTeamLeaderName = first.TeamLeaderName,
            TeamLeaderId = first.TeamLeaderId, FirstUnlockTime = unlock
        };
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        JsonElement? level = null;
        if (now >= unlock)
            foreach (var row in _data.Table("TenGameplayBuffConfig").Values
                .Where(r => GameData.Int(r, "_iGroupType") == group).OrderBy(r => GameData.Int(r, "_iLevel")))
                if (now >= unlock + GameData.Int(row, "_iUnlockDay") * Day) level = row;
        if (level is { } current) info.UnlockSacredBlessingID = GameData.Int(current, "_iID");
        return (info, level);
    }

    private static IEnumerable<int> BlessingBuffs(Attempt a) => a.Blessing is { } row
        && row.TryGetProperty("_kBuffID", out var ids) && ids.ValueKind == JsonValueKind.Array
        ? ids.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).Where(id => id > 0) : [];

    private static int BlessingRevives(Attempt a) => a.Blessing is { } row ? (int)Math.Max(0, GameData.Int(row, "_iAddReviveNum")) : 0;

    private JsonElement? BossOf(int multiId, int mark) => _catalog.MultiRows
        .Where(r => GameData.Int(r, "_iID") == multiId).Select(r => _catalog.Boss(r, mark)).FirstOrDefault();

    /// <summary>82588: the role has seen its blessing's current level; 5062 drops isNeedTip until the next level.</summary>
    public async Task MarkBlessingsAsync(GameSession s, MultiDungeonMarkSacredBlessingsReq req)
    {
        var boss = BossOf(req.MultiDungeonId, req.MarkId);
        long level = boss is { } row && Blessing(row) is { } blessing ? blessing.Info.UnlockSacredBlessingID : 0;
        int error = s.Dungeons == null || level == 0 ? ErrorCodes.Parameter : 0;
        if (error == 0)
        {
            using (await CharacterService.Lock(s))
            {
                var state = CurrentState(s.Dungeons!);
                state.BlessingsSeen[GameData.Int(boss!.Value, "_iBoss")] = level;
                await SaveAsync(s, state);
            }
            await SyncAsync(s);
        }
        await s.SendGameAsync(MarkBlessings, new MultiDungeonMarkSacredBlessingsResp { Result = error });
    }

    // ---- Final Trial honors ----

    private static long[] TrialIds(JsonElement boss) => boss.TryGetProperty("_kFinalTrialID", out var ids)
        && ids.ValueKind == JsonValueKind.Array ? ids.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).Where(id => id > 0).ToArray() : [];

    private static DBMultiDungeonHonor? HonorView(State state, string key, JsonElement boss)
    {
        var ids = TrialIds(boss);
        if (ids.Length == 0) return null;
        var honor = new DBMultiDungeonHonor();
        var mine = state.Honors.GetValueOrDefault(key);
        foreach (long id in ids) honor.HonorInfo[id] = (DBMultiDungeonHonorState)(mine?.GetValueOrDefault(id) ?? 0);
        return honor;
    }

    /// <summary>
    /// A won Final Trial run finishes every goal whose conditions hold (LC_ConditionList, all sub-conditions):
    /// 7005 [n, _] "Have at least n Revive attempts remaining" (the team's pool: the boss's _iOriginalReviveNum plus the
    /// blessing's, less every revive used in the run, inferred), 7004 [s, _] "more than s seconds remaining until enrage"
    /// (ServerOptions.RaidEnrageSeconds after the fight began, inferred), 7006 [id] that goal finished. The trailing 4 of
    /// 7004/7005 is not read (inferred: the Ultimate difficulty the trials belong to).
    /// </summary>
    private void EarnHonors(State next, Attempt a, JsonElement boss, long durationMs)
    {
        var ids = TrialIds(boss);
        if (ids.Length == 0) return;
        string key = RecordKey(a.Plan);
        if (!next.Honors.TryGetValue(key, out var honors)) next.Honors[key] = honors = new();
        int revivesLeft = Math.Max(0, (int)GameData.Int(boss, "_iOriginalReviveNum")) + BlessingRevives(a) - a.RevivesUsed.Values.Sum();
        long enrageLeft = _options.RaidEnrageSeconds - durationMs / 1000;
        bool Holds(JsonElement sub)
        {
            if (!sub.TryGetProperty("m_kData", out var args) || args.ValueKind != JsonValueKind.Array || args.GetArrayLength() < 2) return false;
            long n = (long)(GameData.Number(args[1]) ?? 0);
            bool met = GameData.Number(args[0]) switch
            {
                7004 => enrageLeft > n,
                7005 => revivesLeft >= n,
                7006 => honors.GetValueOrDefault(n) >= HonorFinished,
                _ => false,
            };
            return sub.TryGetProperty("m_bInverse", out var inverse) && inverse.ValueKind == JsonValueKind.True ? !met : met;
        }
        // Two passes: a "complete all objectives" goal reads the goals finished before it.
        for (int pass = 0; pass < 2; pass++)
            foreach (long id in ids)
            {
                if (honors.GetValueOrDefault(id) >= HonorFinished || _data.Row("TenGameplayFinalTrialConfig", id) is not { } goal
                    || _data.Row("LC_ConditionList", GameData.Int(goal, "_kAchieveConditions")) is not { } condition
                    || !condition.TryGetProperty("m_kSubConditionList", out var subs) || subs.ValueKind != JsonValueKind.Array
                    || subs.GetArrayLength() == 0) continue;
                if (subs.EnumerateArray().All(Holds)) honors[id] = HonorFinished;
            }
    }

    /// <summary>82584's honor: the goals this run finished (the client lists the Complete ones, LC_DAL_MultiPlayerDungeon:543-564).</summary>
    private static DBMultiDungeonHonor? EarnedHonors(Accounts.DungeonRewardObligation row)
    {
        var before = JsonSerializer.Deserialize<State>(row.BaseState)!;
        var after = JsonSerializer.Deserialize<State>(row.NextState)!;
        DBMultiDungeonHonor? honor = null;
        foreach (var (key, states) in after.Honors)
            foreach (var (id, state) in states)
                if (state == HonorFinished && before.Honors.GetValueOrDefault(key)?.GetValueOrDefault(id) is null or 0)
                    (honor ??= new DBMultiDungeonHonor()).HonorInfo[id] = DBMultiDungeonHonorState.Finish;
        return honor;
    }

    /// <summary>
    /// 82589: each requested finished goal pays its _iPersonAward (MultiverseGroupConfig -> AwardConfig) once and becomes
    /// Received, in one receipt per goal. A request with any goal not finished or already received is refused whole.
    /// </summary>
    public async Task ClaimHonorAsync(GameSession s, MultiDungeonClaimHonorAwardReq req)
    {
        string key = $"{req.MultiDungeonId}:{req.MarkId}";
        long roleId = s.Role?.Id ?? 0;
        using var scope = roleId > 0 && _loop.Owners is { } owners
            ? await owners.EnterAsync("dungeon honor claim", [Ownership.ReservationKey.Role(roleId),
                Ownership.ReservationKey.Lifecycle(roleId), Ownership.ReservationKey.Entity("dungeon_honor_claim", $"{roleId}/{key}")]) : null;
        if (s.Role?.Id != roleId) throw new OperationCanceledException("Role changed before honor claim admission.");
        if (s.Owner != null) scope?.Context.Pin(s.Owner);
        var ids = BossOf(req.MultiDungeonId, req.MarkId) is { } boss ? TrialIds(boss) : [];
        var wanted = req.HonorIds.Distinct().ToArray();
        int error = s.Role == null || s.Dungeons == null || wanted.Length == 0 || wanted.Any(id => !ids.Contains(id))
            ? ErrorCodes.Parameter
            : wanted.Any(id => CurrentState(s.Dungeons).Honors.GetValueOrDefault(key)?.GetValueOrDefault(id) != HonorFinished)
                ? ErrorCodes.RewardClaimed : 0;
        foreach (long id in error == 0 ? wanted : [])
        {
            var goal = _data.Row("TenGameplayFinalTrialConfig", id)!.Value;
            long group = GameData.Int(goal, "_iPersonAward");
            long awardId = 0;
            if (_data.Row("MultiverseGroupConfig", group) is { } mg)
                awardId = GameData.Int(mg, $"_iValue_{_seasons.PlayBook}") is var v and > 0 ? v : GameData.Int(mg, "_iValue_default");
            AwardPlan? plan = null;
            try { if (awardId > 0) plan = _awards.ResolveDirect(s, awardId); }
            catch (AwardResolutionException e) { _log.LogWarning(e, "Final Trial award {Award} rejected", awardId); }
            if (plan == null || plan.Grants.Count == 0) { error = ErrorCodes.RewardMissing; break; }
            var next = CurrentState(s.Dungeons!);
            if (!next.Honors.TryGetValue(key, out var honors)) next.Honors[key] = honors = new();
            honors[id] = HonorReceived;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next);
            if (!await _character.ApplyAwardAsync(s, plan, persist: tx =>
                {
                    tx.RoleBlob(s.Role!.Id, "dungeons", bytes);
                    if (plan.Grants.Any(grant => grant.Entry is not (100 or 101)))
                        tx.WithEventEconomy(EventEconomy.ForRole(_data, s, "dungeon/honor-award",
                            $"dungeon/honor-award/{s.Role.Id}/{key}/{id}/{next.SeasonStart}", [], plan.Grants,
                            new { Multi = req.MultiDungeonId, Mark = req.MarkId, Honor = id, Award = awardId }));
                }, committed: () => s.Dungeons = next))
            { error = ErrorCodes.Unknown; break; }
        }
        if (wanted.Length > 0 && s.Dungeons != null) await SyncAsync(s);
        await s.SendGameAsync(ClaimHonor, new MultiDungeonClaimHonorAwardResp { Result = error });
    }

    // ---- 30125 Start Battle ----

    private static DungeonVar? FunctionButtons(Attempt a)
    {
        if (!StartBattleScenes.Contains(a.Plan.SceneEntry) || a.Plan.Multi == null || !a.Started) return null;
        var v = new DungeonVar { VarId = FunctionButtonVar };
        if (a.Preparing) v.Vi64.Add(StartBattleButton); // an empty list removes the button (UpdateFunctionBtn)
        return v;
    }

    private async Task EndPreparationAsync(Attempt a)
    {
        if (!a.Preparing || a.Settled || a.Room.Closed) return;
        a.Preparing = false;
        await BeginFightAsync(a);
    }

    /// <summary>30125: only the run's leader may press Start Battle (receivecondition 1 IsTeamLeader), only while preparing.</summary>
    public async Task ModuleButtonAsync(GameSession s, DungeonModuleButtonReq req)
    {
        var a = s.DungeonAttempt;
        int error = a == null || !ReferenceEquals(Active(s), a) || FunctionButtons(a) is not { } buttons
            || !buttons.Vi64.Contains(req.ButtonId) ? ErrorCodes.Parameter
            : s.Role!.Id != a.Leader ? ErrorCodes.NoPermission : 0;
        await s.SendGameAsync(ModuleButton, new DungeonModuleButtonResp { Result = error });
        if (error == 0 && req.ButtonId == StartBattleButton) await EndPreparationAsync(a!);
    }

    // ---- GM ----

    /// <summary>"!firstclear &lt;boss&gt; [daysAgo]": the realm's first clear of a boss by the GM's role, dated back
    /// (test aid, not original behaviour); "clear" removes it.</summary>
    private async Task FirstClearGmAsync(GameSession s, string[] args)
    {
        if (args.Length < 1 || !int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int boss)
            || _data.Row("TenGameplayBossConfig", boss) == null) throw new ArgumentException("<TenGameplayBossConfig boss id> [daysAgo] | <boss> clear");
        if (args.Length > 1 && args[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            using (_loop.Owners is { } owners ? await owners.EnterAsync("dungeon/hall-of-fame gm", ServerInfoReservation) : null)
            {
                var next = ServerInfo.Clone();
                next.HallOfFame.Remove(boss);
                await SaveServerInfoAsync(next);
            }
            await SyncAsync(s);
            await GmCommands.ReplyAsync(s, $"First clear of {boss} removed (test aid).");
            return;
        }
        double days = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 0;
        var record = new DBMultiDungeonHallOfFame
        {
            MultiDungeonBossId = boss, PassTime = 0,
            PassTimestamp = _clock.GetUtcNow().AddDays(-days).ToUnixTimeMilliseconds(),
            TeamLeaderId = s.Role!.Id, TeamLeaderName = s.Role.Name, RoleInfo = { _character.DetailInfo(s) }
        };
        await StoreFirstClearAsync(record, replace: true);
        await SyncAsync(s);
        await GmCommands.ReplyAsync(s, $"First clear of {boss} set {days} day(s) ago (test aid).");
    }
}
