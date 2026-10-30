using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The remaining quest targets of LC_ConditionDefine (Logic/ConditionSystem/LC_ConditionDefine.lua:100-460) with a server
/// owner, and the quest groups the server offers outside the pre-task graph: World Exploration node quests (type 21) and
/// the season settlement quest (type 12). Texts are ProgressConfig _iConditionDesc / the quests' _iTaskDescribe; R is the
/// client's formatter (LC_InteractCondition GetDefaultDes: R = p2) unless said otherwise.
/// <list type="bullet">
/// <item>103001 CheckTitle [rank] "Reach Title ${1}" (100004 "Reach the title Wind's Voyage I", [103001,2]): the
/// TitleConfig rank p2 or a later one (_kIndex) is held now (TitleService). Read as R or 0 (inferred).</item>
/// <item>103002 CheckTradeJoinCnt [n] "Trade on the Exchange ${1} times": one per committed Trading House purchase
/// (TradeService.Bought); a sale is not counted (inferred).</item>
/// <item>103003 CheckAuctionJoinCnt [n] "Participate in the Guild Auction ${1} times": one per committed bid on a guild
/// lot (AuctionService.GuildBid, inferred).</item>
/// <item>103013 CheckGuildKaoHuoJoinCnt [n] "Participate in the Guild Bonfire ${1} times": GuildCampfireService.Joined,
/// as 106018. 103015 CheckGuildTournamentJoinCnt [n] "Participate in the Guild League ${1} times": one per committed
/// Guild League match the role fought in, as 106603.</item>
/// <item>103016 CheckOnHookTime [minutes] "Spend a total of ${1} time in field combat" (100019 "@{1} minutes", 306060
/// "Use @{1} minutes of boosted auto-battle time"): R = tonumber(p2) (LC_InteractCondition.CheckOnHookTime); the whole
/// minutes of DBWildAFKInfo.sumHighMultiplierTime, the Bonus Time ever consumed (WildAfkService).</item>
/// <item>103020 CheckChatNum [channel, n] "Send ${1} chat messages in the specified channel": 100023 [103020,2,3] reads
/// "Send 3 messages in the Guild Channel" (_iTaskDescribe [..., "3"]), so p2 is the ChatChannelType (2 Guild) and R = p3,
/// the ${1} of the text (inferred, the default formatter would show p2). One per line ChatService delivered.</item>
/// <item>101021 NowMount [R, mount] "Ride the Lion mount" (Guide 302091-302141, 382091-382141): the mount p3 is ridden
/// now (MountService.Rider). p2 has no other recovered meaning (inferred).</item>
/// <item>318001 [n] "Claim ${1} Map Rewards" / 318002 [n] "Claim ${1} Area Rewards" / 318003 CheckMapReceiveAward
/// [AreaConcurMapConfig id] "Claim ${1}'s Map Reward" (R 1, GetAreaMapDes): the claimed map awards
/// (DBAreaConcurInfo.mapInfo isClaimedAward) / the claimed area stages / that map's award.
/// 本地 MonsterHuntService.ClaimMapAsync（83621）已提交地圖領獎旗標，這裡讀取已提交的狀態。</item>
/// <item>309001 [s] "Please wait ${1}s for the quest to complete automatically" (994000): the seconds since the quest
/// was accepted (TaskInfo.startTime, inferred).</item>
/// <item>103052 [1] "Please select a main story branch" (102035 "The Road Ahead", _kTrack UI_Sys_Guide_ExperiencePanel):
/// accepting it offers PublicParamConfig.GuideMainQuestBranchChoice [1,2] as DBTaskInfo.waitSelectBranch {1: 1, 2: 2}
/// (the panel sends waitSelectBranch[1] or [2], UI_Sys_Guide_ExperiencePanelCtrl._getWaitSelectBranch); 31215 sets
/// selectBranch and the target is met.</item>
/// <item>106907 [n] "Participate in the Commendation Vault ${1} times" (500220): one per paid Rift Crisis clear
/// (TimedBattleService OnePiece settlement), as 103049.</item>
/// <item>101022 CheckTerritoryWarzoneTaskPoint [points] (1101000 "Faction Quest", R = p2,
/// CheckTerritoryWarzoneTaskPointDes): the faction sub-tasks (type 25) name their points, "Collect Magic Crystals +@{1}
/// Points" (_iName [text, "1"]); each delivered sub-task adds them (inferred: no other point table is exported).</item>
/// </list>
/// 仍無可證的已提交事件來源：103012／103017／103018 對應的三筆成長任務文字標為預留。
/// actual G 的 CheckMINIJoinCnt／CheckGuildPrayJoinCnt 只找到條件名稱註冊；MINI 小地圖分類不等於參加事件，
/// Valhalla 的祈禱也不是公會祈禱。43405 有離線領獎請求，但客戶端沒有證明發獎公式或結算來源，故仍拒絕。
/// 101008 不能以普通通關或首通領獎代替：掃蕩入口為 32804，PetFloorConfig 的每日獎勵目前全為零。
/// 103048 則不需要一般任務 owner：1400010 由 LC_TaskHelper._getOtherClientTask 依 HiddenTreasureMapInfo
/// 在客戶端建立，TimedBattleService 已提供該同步；不可為降低缺口數而另建可領獎的伺服器任務。
/// 詳見 docs/research/remaining-quest-targets-evidence.md；未找到的來源是有界靜態調查結果，不宣稱原服不存在。
/// </summary>
public sealed partial class TaskService
{
    private const int TTitle = 103001, TTradeJoin = 103002, TGuildAuctionJoin = 103003, TBonfireJoin = 103013,
        TLeagueJoinCount = 103015, TOnHookTime = 103016, TChatNum = 103020, TNowMount = 101021, TMapAwards = 318001,
        TAreaAwards = 318002, TMapAward = 318003, TWaitSeconds = 309001, TBranchChosen = 103052, TVaultJoin = 106907,
        TFactionPoints = 101022;

    private const int SeasonTaskType = 12, FactionSubTaskType = 25;

    private Dictionary<long[], long>? _waitTargets;
    private readonly HashSet<GameSession> _waitQueued = [];
    private Dictionary<long, long>? _factionPoints;
    private Dictionary<int, long[]>? _eventTasks;
    private ExploreNodeConfig[]? _exploreNodes;
    private Dictionary<long, MultiverseSettlementTask>? _settlementTasks;

    private static long MiscRequired(long[] t) => t switch
    {
        [TMapAward, _] => 1,
        [TTitle or TTradeJoin or TGuildAuctionJoin or TBonfireJoin or TLeagueJoinCount or TOnHookTime or TMapAwards
            or TAreaAwards or TWaitSeconds or TBranchChosen or TVaultJoin or TFactionPoints, var n] => n,
        [TNowMount, var n, _] => n,
        [TChatNum, _, var n] => n,
        _ => -1,
    };

    private bool MiscSupported(long[] t) => t[0] switch
    {
        TTitle => TitleConfig.Get(_data, t[1]) != null,
        TNowMount => MountsConfig.Get(_data, t[2]) != null || CartRemouldTable.Get(_data, t[2]) != null,
        TMapAward => AreaConcurMapConfig.Get(_data, t[1]) != null,
        TChatNum => Enum.IsDefined((ChatChannelType)t[1]),
        _ => true,
    };

    private long MiscPassive(GameSession s, long[] t) => t[0] switch
    {
        TTitle => s.TitleInfo == null ? -1 : TitleReached(s.TitleInfo.TitleId, t[1]) ? t[1] : 0,
        TOnHookTime => s.WildAfk is { } afk ? afk.Info.SumHighMultiplierTime / 60 : -1,
        TNowMount => s.Mounts == null ? -1 : MountService.Rider(s) == t[2] ? t[1] : 0,
        TMapAwards => s.AreaConcur?.MapInfo.Values.LongCount(m => m.IsClaimedAward) ?? -1,
        TAreaAwards => s.AreaConcur?.AreaInfo.Values.Sum(a => a.ClaimedAward.Values.LongCount(c => c)) ?? -1,
        TMapAward => s.AreaConcur == null ? -1 : s.AreaConcur.MapInfo.GetValueOrDefault(t[1])?.IsClaimedAward == true ? 1 : 0,
        TBranchChosen => s.Tasks == null ? -1 : s.Tasks.SelectBranch.Values.Any(chosen => chosen) ? 1 : 0,
        TWaitSeconds => WaitedSeconds(s, t),
        _ => -1,
    };

    /// <summary>The rank held is <paramref name="rank"/> or a later one in _kIndex order.</summary>
    private bool TitleReached(long held, long rank) => TitleConfig.Get(_data, held) is { } current
        && TitleConfig.Get(_data, rank) is { } wanted && current.Index >= wanted.Index;

    // ---- events ----

    /// <summary>103002: one committed Trading House purchase.</summary>
    public void OnExchangeBought(GameSession s)
    {
        if (Holds(s, TTradeJoin))
            _loop.PostOwned(s, () => ProgressAsync(s, TTradeJoin, t => t.Length == 2 ? 1 : 0), "quest exchange");
    }

    /// <summary>103003: one committed bid on a guild auction lot.</summary>
    public void OnGuildAuctionBid(GameSession s)
    {
        if (Holds(s, TGuildAuctionJoin))
            _loop.PostOwned(s, () => ProgressAsync(s, TGuildAuctionJoin, t => t.Length == 2 ? 1 : 0), "quest guild auction");
    }

    /// <summary>103013: the member is at the Bonfire Party (GuildCampfireService.Joined, once per event day).</summary>
    public void OnBonfireJoined(GameSession s)
    {
        if (Holds(s, TBonfireJoin))
            _loop.PostOwned(s, () => ProgressAsync(s, TBonfireJoin, t => t.Length == 2 ? 1 : 0), "quest bonfire");
    }

    /// <summary>103015: one committed Guild League match for every fighter in <paramref name="scores"/>.</summary>
    public void OnGuildLeagueFought(int round, IReadOnlyDictionary<long, long> scores)
    {
        foreach (var s in _sessions.All().Where(s => s.Role != null && scores.ContainsKey(s.Role.Id)).ToArray())
            if (Holds(s, TLeagueJoinCount))
                _loop.PostOwned(s, () => ProgressAsync(s, TLeagueJoinCount, t => t.Length == 2 ? 1 : 0), "quest guild league");
    }

    /// <summary>103020: one chat line delivered on a channel of type <paramref name="channel"/> (ChatChannelType).</summary>
    public void OnChatSent(GameSession s, int channel)
    {
        if (Holds(s, TChatNum))
            _loop.PostOwned(s, () => ProgressAsync(s, TChatNum, t => t is [TChatNum, var c, _] && c == channel ? 1 : 0),
                "quest chat");
    }

    /// <summary>The role runs a quest with a <paramref name="type"/> target: a chat line or a purchase does not post
    /// quest work for everyone.</summary>
    private bool Holds(GameSession s, int type)
    {
        _eventTasks ??= Defs().Values.SelectMany(d => d.Targets.Select(t => (Type: (int)t[0], d.Id))).Distinct()
            .GroupBy(p => p.Type).ToDictionary(g => g.Key, g => g.Select(p => p.Id).ToArray());
        return s.Tasks is { } tasks && _eventTasks.TryGetValue(type, out var ids)
            && ids.Any(id => tasks.Infos.TryGetValue(id, out var info) && info.Status == Running);
    }

    /// <summary>106907, staged with the Rift Crisis reward (TaskService.EventTransactions.cs).</summary>
    private static bool VaultTarget(long[] t) => t is [TVaultJoin, _];

    /// <summary>101022: a delivered faction sub-task adds its points to the running faction quest (QuestDelivered, inside
    /// the quest lock; the settlement that delivered it saves and sends the change).</summary>
    private void AddFactionPoints(GameSession s, long taskId, AwardPlan _)
    {
        if (s.Tasks == null || !FactionPoints().TryGetValue(taskId, out long points)) return;
        foreach (var info in s.Tasks.Infos.Values.Where(i => i.Status == Running))
        {
            var def = Defs()[info.TaskId];
            for (int i = 0; i < def.Targets.Length && i < info.CompleteNum.Count; i++)
                if (def.Targets[i] is [TFactionPoints, var required] && required > 0)
                    info.CompleteNum[i] = Math.Min(required, Math.Max(0, info.CompleteNum[i]) + points);
        }
    }

    /// <summary>Type 25 task → the points its _iName names ([text, "points"]).</summary>
    private Dictionary<long, long> FactionPoints() => _factionPoints ??= TaskConfig.All(_data)
        .Where(r => r.TaskType == FactionSubTaskType)
        .Select(r => (r.TaskId, Points: r.NameIntegerArgument))
        .Where(r => r.Points > 0).ToDictionary(r => r.TaskId, r => r.Points);

    // ---- acceptance ----

    /// <summary>An accepted 103052 quest offers the branch choice (unless one was made).</summary>
    private void StartMiscTargets(GameSession session, long id)
    {
        var def = Defs()[id];
        if (def.Targets.Any(t => t[0] == TBranchChosen) && !session.Tasks!.SelectBranch.Values.Any(chosen => chosen))
        {
            int index = 1;
            foreach (long branch in BranchChoices()) session.Tasks.WaitSelectBranch[index++] = branch;
        }
    }

    /// <summary>PublicParamConfig.GuideMainQuestBranchChoice, the branch ids LC_TaskHelper.GetQuestBranchId checks.</summary>
    private long[] BranchChoices() => PublicParamConfig.GuideMainQuestBranchChoice(_data);

    /// <summary>
    /// 309001: the seconds since its quest was accepted; while short of R a refresh is queued for when it is reached.
    /// </summary>
    private long WaitedSeconds(GameSession s, long[] t)
    {
        if (!WaitTargets().TryGetValue(t, out long task) || s.Tasks?.Infos.GetValueOrDefault(task) is not { } info) return -1;
        long waited = Math.Max(0, (_clock.GetUtcNow().ToUnixTimeMilliseconds() - info.StartTime) / 1000);
        if (info.Status == Running && waited < t[1] && _waitQueued.Add(s))
            _loop.Schedule(TimeSpan.FromSeconds(t[1] - waited), () =>
            {
                _waitQueued.Remove(s);
                QueueRefresh(s);
                return Task.CompletedTask;
            });
        return waited;
    }

    /// <summary>Each 309001 target (by reference, as TaskDef holds it) → its quest.</summary>
    private Dictionary<long[], long> WaitTargets()
    {
        if (_waitTargets != null) return _waitTargets;
        var targets = new Dictionary<long[], long>(ReferenceEqualityComparer.Instance);
        foreach (var def in Defs().Values)
            foreach (var t in def.Targets.Where(t => t[0] == TWaitSeconds)) targets[t] = def.Id;
        return _waitTargets = targets;
    }

    /// <summary>The quests the server offers outside the pre-task graph; returns how many were accepted.</summary>
    private int AcceptMiscTasks(GameSession session) => AcceptExploreTasks(session) + AcceptSettlementTask(session);

    /// <summary>
    /// World Exploration (activity 111, type 21 quests): when a node's notes are all filled, the client shows the node's
    /// _iQuestID in its unlock tip (UI_Sys_Season_MovementPlayPanelCtrl._createUnlockTipWidget, which opens it in the task
    /// panel), and the node reads as done once _iFinishQuestID is delivered (UI_Sys_Season_MovementPlay_CellWidgetCtrl
    /// ._updatePanel). The server accepts the node quest when its node is finished, and a different finish quest
    /// (node 1033: 301006 then 301007, which has no accept mode) after it (inferred).
    /// </summary>
    private int AcceptExploreTasks(GameSession session)
    {
        if (session.NewActivities?.ActivityInfo.Values.Select(a => a.ActivityParam?.WorldExploration)
            .FirstOrDefault(w => w != null) is not { } exploration) return 0;
        _exploreNodes ??= [.. ExploreNodeConfig.All(_data).Where(n => Defs().ContainsKey(n.QuestId))];
        int accepted = 0;
        foreach (var node in _exploreNodes)
        {
            if (!exploration.PhaseInfos.Values.Any(p => p.NodeInfos.GetValueOrDefault(node.NodeId)?.IsFinished == true)) continue;
            foreach (long id in new[] { node.QuestId, node.FinishQuestId }.Distinct())
            {
                if (!Defs().TryGetValue(id, out var def) || Active(session, id) || IsDelivered(session, id)
                    || !Ready(session, def)) continue;
                Accept(session, id);
                accepted++;
            }
        }
        return accepted;
    }

    /// <summary>
    /// The season settlement quest (type 12, "Reach Lv. @{1} by Season Settlement", claimed in the Season book,
    /// LC_SeasonHelper.GetSeasonTaskList / UI_Sys_Season_BookAchievementWidgetCtrl): MultiverseSettlementTask names one
    /// per season (_iID) and playbook. The server offers the current season's (5021 current.seasonId); a quest the rows
    /// repeat (_iIfRepeat 1) starts over in a later season (inferred: the season number is the row id).
    /// </summary>
    private int AcceptSettlementTask(GameSession session)
    {
        if (session.Synced.GetValueOrDefault(SeasonService.SeasonSync) is not DBSeasonInfo { Current: { } season }) return 0;
        _settlementTasks ??= MultiverseSettlementTask.All(_data)
            .Where(r => Defs().TryGetValue(r.TaskId, out var d) && d.Type == SeasonTaskType)
            .GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        if (!_settlementTasks.TryGetValue(season.SeasonId, out var row)
            || (row.MultiverseArray.Length > 0 && !row.MultiverseArray.Contains(season.PlayBookId))) return 0;
        long id = row.TaskId, start = season.StartTime * 1000;
        if (session.Tasks!.Infos.TryGetValue(id, out var old) && old.StartTime < start
            && _clock.GetUtcNow().ToUnixTimeMilliseconds() >= start)
        {
            session.Tasks.Infos.Remove(id);
            session.Tasks.CompleteTaskNum.Remove(id);
        }
        if (Active(session, id) || IsDelivered(session, id) || !Ready(session, Defs()[id])) return 0;
        Accept(session, id);
        return 1;
    }
}
