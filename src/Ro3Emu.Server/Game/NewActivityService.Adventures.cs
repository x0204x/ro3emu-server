using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Adventure activity variants sent by LC_ActivityServer, LC_SeasonServer, LC_JourneyService and
/// LC_DAL_ExplorationBigWorld through 82976. Every mutation is committed with 5013 and its inventory cost.
/// Configured IDs and all oneof response cases are authoritative; transition ordering without original
/// server code is (inferred). See docs/systems/activities-adventures.md.
/// </summary>
public sealed partial class NewActivityService
{
    private static readonly int[] FragmentItems = [1111001, 1111002, 1111003, 1111004, 1111005];

    private void InitializeAdventure(GameSession session, JsonElement row, PlayerActivityInfo info)
    {
        info.ActivityParam ??= new PlayerActivityParam();
        switch (GameData.Int(row, "_iActivityType"))
        {
            case 5:
                info.ActivityParam.FragmentGather ??= new PlayerActivityFragmentGather
                {
                    DailyData = new FragmentGatherDaily(), Playbook = session.SeasonState?.PlayBookId ?? 101,
                };
                break;
            case 13:
                info.ActivityParam.Racing ??= new PlayerActivityRacing();
                break;
            case 14:
                info.ActivityParam.WorldExploration ??= CreateWorldExploration();
                break;
            case 15:
                info.ActivityParam.Journey ??= CreateJourney();
                break;
            case 19:
                info.ActivityParam.SeasonEve ??= CreateSeasonEve();
                break;
        }
    }

    private DBWorldExploration CreateWorldExploration()
    {
        var state = new DBWorldExploration { DropData = new DBWorldExplorationDropData() };
        foreach (var row in _data.Table("ExplorePhaseConfig").Values.Where(r => GameData.Int(r, "_iActivityid") == 111))
        {
            long phaseId = GameData.Int(row, "_iPhaseID");
            if (phaseId == 0 || !row.TryGetProperty("_kNodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array) continue;
            var phase = new DBWorldExplorationPhase { PhaseCfgId = phaseId };
            foreach (var id in nodes.EnumerateArray())
            {
                long nodeId = (long)(GameData.Number(id) ?? 0);
                if (nodeId > 0 && _data.Row("ExploreNodeConfig", nodeId) != null)
                    phase.NodeInfos[nodeId] = new DBWorldExplorationPhaseNode { NodeCfgId = nodeId };
            }
            state.PhaseInfos[phaseId] = phase;
        }
        return state;
    }

    private DBJourneyInfo CreateJourney()
    {
        var first = _data.Table("JourneyNodeConfig").Values.OrderBy(r => GameData.Int(r, "_iChapter"))
            .ThenBy(r => GameData.Int(r, "_iID")).FirstOrDefault();
        var state = new DBJourneyInfo
        {
            CurNodeId = first.ValueKind == JsonValueKind.Object ? GameData.Int(first, "_iID") : 0,
            // (inferred) The authored seven-day travel starts with the first five-hour day.
            TravelInfo = new JourneyTravelInfo { CurDay = 1, RemainHour = 5 }, EventInfo = new JourneyEventInfo(),
        };
        // PublicParamConfig.JourneyAttrInitial supplies the attributes used by the
        // Journey condition and event selectors.
        foreach (var initial in JourneyPublicArray("JourneyAttrInitial"))
            if (initial.ValueKind == JsonValueKind.Array && initial.GetArrayLength() >= 2)
                state.Attributes[(long)(GameData.Number(initial[0]) ?? 0)] = (long)(GameData.Number(initial[1]) ?? 0);
        if (first.ValueKind == JsonValueKind.Object && GameData.Int(first, "_iFirstAct") is var act and > 0)
            state.CurActIds[act] = true;
        return state;
    }

    private DBJourneyInfo Journey(GameSession session, long activityId) =>
        session.NewActivities!.ActivityInfo[activityId].ActivityParam.Journey;

    private PlayerActivitySeasonEve CreateSeasonEve()
    {
        var state = new PlayerActivitySeasonEve();
        foreach (var row in _data.Table("SeasonEveChapterConfig").Values)
        {
            long id = GameData.Int(row, "_iId");
            if (id > 0) state.ChapterInfo[id] = new PlayerSeasonEveChapterInfo();
        }
        return state;
    }

    /// <summary>CommonActivityReq's adventure oneofs. The core holds ActivityGate and validates activity state.</summary>
    private async Task<bool> HandleAdventureCommonAsync(GameSession session, CommonActivityReq req, CommonActivityRes res)
    {
        if (req.SeasonEveSelectWish != null || req.SeasonEveFindClue != null || req.SeasonEvePlayChapter != null
            || req.SeasonEveClaimAward != null || req.SeasonEveGetInfo != null)
        {
            int eveError = req.ActivityId != 117 ? ErrorCodes.Parameter : ActivityError(session, req.ActivityId, 19);
            if (eveError != 0)
            {
                res.Result = eveError;
                return true;
            }
            await ReconcileEveGlobalAsync(session, req.ActivityId);
        }
        if (req.FragmentGatherAsk != null) { res.FragmentGatherAsk = new(); res.Result = await FragmentAskCoreAsync(session, req.ActivityId, req.FragmentGatherAsk, res.FragmentGatherAsk); return true; }
        if (req.FragmentGatherAskFinished != null) { res.FragmentGatherAskFinished = new(); res.Result = await FragmentAskFinishedCoreAsync(session, req.ActivityId, req.FragmentGatherAskFinished, res.FragmentGatherAskFinished); return true; }
        if (req.FragmentGatherSend != null) { res.FragmentGatherSend = new(); res.Result = await FragmentSendCoreAsync(session, req.ActivityId, req.FragmentGatherSend); return true; }
        if (req.FragmentGatherCraft != null) { res.FragmentGatherCraft = new(); res.Result = await FragmentCraftCoreAsync(session, req.ActivityId, res.FragmentGatherCraft); return true; }
        if (req.FragmentGatherProgress != null) { res.FragmentGatherProgress = new(); res.Result = await FragmentProgressCoreAsync(session, req.ActivityId, req.FragmentGatherProgress); return true; }
        if (req.FillPhaseNodeReq != null) { res.FillPhaseNodeRes = new(); res.Result = await WorldFillCoreAsync(session, req.ActivityId, req.FillPhaseNodeReq); res.FillPhaseNodeRes.Err = res.Result; return true; }
        if (req.SeasonEveSelectWish != null) { res.SeasonEveSelectWish = new() { Req = req.SeasonEveSelectWish.Clone() }; res.Result = await SelectWishAsync(session, req.ActivityId, req.SeasonEveSelectWish); return true; }
        if (req.SeasonEveFindClue != null) { res.SeasonEveFindClue = new() { Req = req.SeasonEveFindClue.Clone() }; res.Result = await FindClueAsync(session, req.ActivityId, req.SeasonEveFindClue, res.SeasonEveFindClue); return true; }
        if (req.SeasonEvePlayChapter != null) { res.SeasonEvePlayChapter = new() { Req = req.SeasonEvePlayChapter.Clone() }; res.Result = await PlayChapterAsync(session, req.ActivityId, req.SeasonEvePlayChapter); res.SeasonEvePlayChapter.Reason = res.Result; return true; }
        if (req.SeasonEveClaimAward != null) { res.SeasonEveClaimAward = new() { Req = req.SeasonEveClaimAward.Clone() }; res.Result = await ClaimEveAsync(session, req.ActivityId, req.SeasonEveClaimAward); return true; }
        if (req.SeasonEveGetInfo != null) { res.SeasonEveGetInfo = EveGlobalInfo(session); return true; }
        if (req.JourneyComplete != null) { res.JourneyComplete = new() { Req = req.JourneyComplete.Clone() }; res.Result = await CompleteJourneyAsync(session, req.ActivityId, req.JourneyComplete); return true; }
        if (req.JourneyReportEnterTime != null) { res.JourneyReportEnterTime = new() { Req = req.JourneyReportEnterTime.Clone() }; res.Result = await JourneyEnterAsync(session, req.ActivityId); return true; }
        if (req.JourneyReportOpenCompletePanel != null) { res.JourneyReportOpenCompletePanel = new() { Req = req.JourneyReportOpenCompletePanel.Clone() }; res.Result = await JourneyPanelAsync(session, req.ActivityId); return true; }
        if (req.JourneyGetPlayerComplete != null) { res.JourneyGetPlayerComplete = JourneyPlayerComplete(session, req.JourneyGetPlayerComplete.PlayerId); return true; }
        if (req.JourneyDungeonRestart != null) { res.JourneyDungeonRestart = new(); res.Result = await JourneyRestartAsync(session, req.ActivityId); return true; }
        return false;
    }

    private async Task<int> WorldFillCoreAsync(GameSession session, long activityId, WorldExplorationFillPhaseNodeReq req)
    {
        if (ActivityError(session, activityId, 14) is var error and not 0) return error;
        if (_data.Row("ExplorePhaseConfig", req.PhaseCfgId) is not { } phaseRow
            || GameData.Int(phaseRow, "_iActivityid") != activityId
            || !WorldPhaseOpen(session, phaseRow)
            || _data.Row("ExploreNodeConfig", req.NodeCfgId) is not { } nodeRow
            || !Contains(phaseRow, "_kNodes", req.NodeCfgId)
            || !nodeRow.TryGetProperty("_kPiecesID", out var pieces) || pieces.ValueKind != JsonValueKind.Array
            || req.Position < 1 || req.Position > pieces.GetArrayLength()
            || (long)(GameData.Number(pieces[req.Position - 1]) ?? 0) != req.ItemId) return ErrorCodes.Parameter;
        var next = session.NewActivities!.Clone();
        var state = next.ActivityInfo[activityId].ActivityParam.WorldExploration;
        if (!state.PhaseInfos.TryGetValue(req.PhaseCfgId, out var phase)
            || !phase.NodeInfos.TryGetValue(req.NodeCfgId, out var node)) return ErrorCodes.Parameter;
        if (node.Items.ContainsKey(req.Position) || node.IsFinished) return ErrorCodes.MailAlreadyClaimed;
        node.Items[req.Position] = req.ItemId;
        node.IsFinished = node.Items.Count == pieces.GetArrayLength();
        int slots = phase.NodeInfos.Keys.Sum(nodeId =>
            _data.Row("ExploreNodeConfig", nodeId) is { } configured
            && configured.TryGetProperty("_kPiecesID", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.GetArrayLength() : 0);
        // (inferred) GetChapterProgress divides totalRate by 100 for the displayed
        // percentage, so normalize the filled note slots to 10,000 basis points.
        phase.TotalRate = slots > 0 ? (int)(10000L * phase.NodeInfos.Values.Sum(n => n.Items.Count) / slots) : 0;
        return await CommitAdventureAsync(session, next, costs: [((int)req.ItemId, 1)]);
    }

    private bool WorldPhaseOpen(GameSession session, JsonElement row)
    {
        var stages = _seasons.Clock(Playbook(session)).StageTimeInfo;
        static bool Bound(JsonElement phase, string field, Google.Protobuf.Collections.MapField<long, SeasonStageTimeInfo> stages, out long seconds)
        {
            seconds = 0;
            if (!phase.TryGetProperty(field, out var tuple) || tuple.ValueKind != JsonValueKind.Array
                || tuple.GetArrayLength() != 3 || GameData.Number(tuple[0]) != 4) return false;
            long stageId = (long)(GameData.Number(tuple[1]) ?? 0);
            if (!stages.TryGetValue(stageId, out var stage)) return false;
            seconds = stage.StartTime + (long)(GameData.Number(tuple[2]) ?? 0);
            return true;
        }
        // OpenContent (PerpetualWindows): the chapters (days 8/10/12 to 14 of the season, ExplorePhaseConfig type-4
        // tuples on stages 101002/101003) open with the activity and never close; the stage clock itself is not moved.
        // ClientHooks.ServerDayGates opens LC_SeasonExplorationHelper.IsChapterInActivityTime to match.
        return Bound(row, "_kStartTime", stages, out long start)
            && Bound(row, "_kEndTime", stages, out long end)
            && (_seasons.OpenContent.Enabled && _seasons.OpenContent.PerpetualWindows || start <= Now && Now <= end);
    }

    private static bool Contains(JsonElement row, string field, long id) =>
        row.TryGetProperty(field, out var values) && values.ValueKind == JsonValueKind.Array
        && values.EnumerateArray().Any(v => GameData.Number(v) == id);

    private async Task<int> SelectWishAsync(GameSession session, long activityId, SeasonEveSelectWishReq req)
    {
        if (ActivityError(session, activityId, 19) is var error and not 0) return error;
        if (_data.Row("SeasonEveWishConfig", req.WishId) is not { } wish || GameData.Int(wish, "_iId") != req.WishId) return ErrorCodes.Parameter;
        if (ReadEveGlobal(session, activityId).Progress < EveConstant("SeasonEveTotalProgress")) return ErrorCodes.TaskRequirements;
        var next = session.NewActivities!.Clone();
        var state = next.ActivityInfo[activityId].ActivityParam.SeasonEve;
        if (state.SelectWish != 0) return ErrorCodes.MailAlreadyClaimed;
        state.SelectWish = req.WishId;
        return await CommitAdventureAsync(session, next);
    }

    private async Task<int> FindClueAsync(GameSession session, long activityId, SeasonEveFindClueReq req, SeasonEveFindClueResp res)
    {
        if (ActivityError(session, activityId, 19) is var error and not 0) return error;
        var clue = _data.Row("SeasonEveClueConfig", req.ClueId);
        if (clue == null || GameData.Int(clue.Value, "_iMapId") != session.SceneEntry
            || _scene.ConfiguredPoint(session.SceneEntry, GameData.Int(clue.Value, "_iPointId")) is not { } point
            || MonsterService.Distance(session.Position.X, session.Position.Z, point.X, point.Z) > 12f
            || _data.Table("SeasonEveChapterConfig").Values.FirstOrDefault(c => Contains(c, "_kClueList", req.ClueId)) is not { ValueKind: JsonValueKind.Object } chapter)
            return ErrorCodes.Parameter;
        long chapterId = GameData.Int(chapter, "_iId");
        if (Now < EveChapterOpenTime(session, activityId, chapter)) return ErrorCodes.TaskRequirements;
        var clueIds = chapter.GetProperty("_kClueList").EnumerateArray()
            .Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
        int index = Array.IndexOf(clueIds, req.ClueId) + 1;
        if (index < 1) return ErrorCodes.Parameter;
        long noteId = EveNoteType(index, checked((int)GameData.Int(chapter, "_iNoteTypeCount")));
        var next = session.NewActivities!.Clone();
        var state = next.ActivityInfo[activityId].ActivityParam.SeasonEve;
        if (!state.ChapterInfo.TryGetValue(chapterId, out var progress)) return ErrorCodes.Parameter;
        res.IsFirst = !progress.Clues.ContainsKey(req.ClueId);
        res.Index = index;
        res.NoteId = noteId;
        if (!res.IsFirst) return 0;
        progress.Clues[req.ClueId] = true;
        progress.Notes[index] = noteId;
        await _adventureSocialGate.WaitAsync();
        try
        {
            var global = ReadEveGlobal(session, activityId);
            var globalChapter = EveGlobalChapter(global, chapterId);
            globalChapter.Clues.TryGetValue(req.ClueId, out long previous);
            globalChapter.Clues[req.ClueId] = previous + 1;
            return await CommitAdventureAsync(session, next, persist: tx => SaveEveGlobal(session, activityId, global, tx));
        }
        finally { _adventureSocialGate.Release(); }
    }

    private async Task<int> PlayChapterAsync(GameSession session, long activityId, SeasonEvePlayChapterReq req)
    {
        if (ActivityError(session, activityId, 19) is var error and not 0) return error;
        if (_data.Row("SeasonEveChapterConfig", req.ChapterId) is not { } chapter
            || GameData.Int(chapter, "_iId") != req.ChapterId) return ErrorCodes.Parameter;
        if (Now < EveChapterOpenTime(session, activityId, chapter)) return ErrorCodes.TaskRequirements;
        int count = EveConstant("SeasonEveChapterNoteCount");
        int noteTypes = checked((int)GameData.Int(chapter, "_iNoteTypeCount"));
        if (req.NoteIds.Count != count || noteTypes < 1
            || Enumerable.Range(1, count).Any(index => !req.NoteIds.TryGetValue(index, out long selected)
                || selected < 1 || selected > noteTypes)) return ErrorCodes.Parameter;
        var next = session.NewActivities!.Clone();
        if (!next.ActivityInfo[activityId].ActivityParam.SeasonEve.ChapterInfo.TryGetValue(req.ChapterId, out var progress))
            return ErrorCodes.Parameter;
        if (progress.IsFinish || progress.Clues.Count < GameData.Int(chapter, "_iClueCount")
            || Enumerable.Range(1, count).Any(index => req.NoteIds[index] != EveNoteType(index, noteTypes)))
            return ErrorCodes.TaskRequirements;
        foreach (var (key, value) in req.NoteIds) progress.Notes[key] = value;
        progress.IsFinish = true;
        await _adventureSocialGate.WaitAsync();
        try
        {
            var global = ReadEveGlobal(session, activityId);
            global.Progress = Math.Min(EveConstant("SeasonEveTotalProgress"),
                global.Progress + EveConstant("SeasonEveCompleteChapterProgress"));
            global.PlayerBullet.Add(new SeasonEveBulletInfo
            { PlayerId = session.Role!.Id, ChapterId = req.ChapterId, TimeMs = Now * 1000 });
            int limit = EveConstant("SeasonEveBarrageCacheCount");
            while (global.PlayerBullet.Count > limit) global.PlayerBullet.RemoveAt(0);
            return await CommitAdventureAsync(session, next, [GameData.Int(chapter, "_iAward")],
                persist: tx => SaveEveGlobal(session, activityId, global, tx));
        }
        finally { _adventureSocialGate.Release(); }
    }

    private async Task<int> ClaimEveAsync(GameSession session, long activityId, SeasonEveClaimAwardReq req)
    {
        if (ActivityError(session, activityId, 19) is var error and not 0) return error;
        if (req.ClaimId.Count != 1 || !req.ClaimId.Single().Value) return ErrorCodes.Parameter;
        long id = req.ClaimId.Single().Key;
        var award = EveProgressAwards().FirstOrDefault(x => x.Id == id);
        if (award.Id == 0) return ErrorCodes.Parameter;
        if (ReadEveGlobal(session, activityId).Progress < award.Threshold) return ErrorCodes.TaskRequirements;
        var next = session.NewActivities!.Clone();
        var state = next.ActivityInfo[activityId].ActivityParam.SeasonEve;
        if (state.ClaimedReward.ContainsKey(id)) return ErrorCodes.MailAlreadyClaimed;
        state.ClaimedReward[id] = true;
        return await CommitAdventureAsync(session, next, [award.Award]);
    }

    private SeasonEveGetGlobalInfoResp EveGlobalInfo(GameSession session)
    {
        const long activityId = 117;
        var global = ReadEveGlobal(session, activityId);
        var result = new SeasonEveGetGlobalInfoResp { Progress = global.Progress };
        result.BulletInfo.AddRange(global.PlayerBullet);
        foreach (var row in _data.Table("SeasonEveChapterConfig").Values)
        {
            long id = GameData.Int(row, "_iId");
            if (id < 1) continue;
            var chapter = new SeasonEveGlobalChapterInfo
            { ChapterId = id, OpenTime = EveChapterOpenTime(session, activityId, row) };
            if (global.ChapterInfo.TryGetValue(id, out var saved))
                foreach (var (clue, discoveries) in saved.Clues) chapter.Clues[clue] = discoveries;
            result.ChapterInfo[id] = chapter;
        }
        return result;
    }

    private int EveConstant(string key) => _data.Table("ActivityConstant").TryGetValue(key, out var value)
        ? (int)(GameData.Number(value) ?? 0) : 0;

    private IEnumerable<(long Id, long Threshold, long Award)> EveProgressAwards()
    {
        if (!_data.Table("ActivityConstant").TryGetValue("SeasonEveProgressAward", out var awards)) yield break;
        foreach (var tuple in awards.EnumerateArray())
            if (tuple.ValueKind == JsonValueKind.Array && tuple.GetArrayLength() == 3)
                yield return ((long)(GameData.Number(tuple[0]) ?? 0),
                    (long)(GameData.Number(tuple[1]) ?? 0), (long)(GameData.Number(tuple[2]) ?? 0));
    }

    // (inferred) The clue's authored position has one stable icon among the four chapter note types.
    private static long EveNoteType(int index, int noteTypes) => 1 + (index - 1) % noteTypes;

    private long EveChapterOpenTime(GameSession session, long activityId, JsonElement chapter)
    {
        long start = session.NewActivities!.ActivityInfo[activityId].CurStartTime;
        if (_data.Row("LC_ConditionList", GameData.Int(chapter, "_iCondition")) is not { } condition
            || !condition.TryGetProperty("m_kSubConditionList", out var clauses)) return start;
        foreach (var clause in clauses.EnumerateArray())
        {
            if (!clause.TryGetProperty("m_kData", out var values) || values.GetArrayLength() < 3
                || GameData.Number(values[0]) != 16500 || GameData.Number(values[1]) != activityId) continue;
            return start + (long)(GameData.Number(values[2]) ?? 0);
        }
        return start;
    }

    private DBSeasonEveInfo ReadEveGlobal(GameSession session, long activityId)
    {
        return _accounts.ReadSync<byte[]?>(new AdventureEveGlobalReadPlan(activityId,
            session.NewActivities!.ActivityInfo[activityId].CycleStartTime)) is { } bytes
            ? DBSeasonEveInfo.Parser.ParseFrom(bytes) : new DBSeasonEveInfo();
    }

    private static DBSeasonEveChapterInfo EveGlobalChapter(DBSeasonEveInfo global, long id)
    {
        if (!global.ChapterInfo.TryGetValue(id, out var chapter))
            global.ChapterInfo[id] = chapter = new DBSeasonEveChapterInfo { ChapterId = id };
        return chapter;
    }

    private static void SaveEveGlobal(GameSession session, long activityId, DBSeasonEveInfo global,
        Ro3Emu.Server.Accounts.Persistence.RoleItemPlanBuilder builder) =>
        builder.Add(new Ro3Emu.Server.Accounts.Persistence.ActivityEveGlobalPlan(activityId,
            session.NewActivities!.ActivityInfo[activityId].CycleStartTime, global.ToByteArray()));

    private async Task ReconcileEveGlobalAsync(GameSession session, long activityId)
    {
        await _adventureSocialGate.WaitAsync();
        try
        {
            var global = ReadEveGlobal(session, activityId);
            if (!AdvanceEveGlobal(session, activityId, global)) return;
            var transaction = _accounts.NewTransaction();
            SaveEveGlobal(session, activityId, global, transaction);
            transaction.WithEscrowEconomy(new EscrowEconomyIntent("activity/eve-reconcile",
                session.Account ?? "", session.Role!.Id).Evidence(new
                { Role = session.Role.Id, Activity = activityId, global.LastCalcTime, global.Progress }));
            await _roles.CommitAsync(transaction, "adventure/eve-reconcile");
        }
        finally { _adventureSocialGate.Release(); }
    }

    private bool AdvanceEveGlobal(GameSession session, long activityId, DBSeasonEveInfo global)
    {
        var activity = session.NewActivities!.ActivityInfo[activityId];
        long start = activity.CycleStartTime, end = activity.CycleEndTime;
        long beforeTime = global.LastCalcTime, beforeProgress = global.Progress;
        int interval = EveConstant("SeasonEveProgressCheckInterval");
        int cap = EveConstant("SeasonEveProgressCheckAddMax");
        long total = EveConstant("SeasonEveTotalProgress");
        if (interval <= 0 || cap <= 0 || total <= 0) return false;
        if (global.LastCalcTime < start) global.LastCalcTime = start;
        // (inferred) The named full-time window makes the wish and final milestone
        // available during the last hour of the active activity, once per cycle.
        if (Now >= end - EveConstant("SeasonEveProgressFullTime"))
        {
            global.Progress = total;
            global.LastCalcTime = Math.Max(global.LastCalcTime, Now);
            return global.LastCalcTime != beforeTime || global.Progress != beforeProgress;
        }

        long until = Math.Min(Now, end);
        int population = _sessions.All().Count(s => ActivityError(s, activityId, 19) == 0);
        long coefficient = EveCoefficient(population);
        for (long tick = global.LastCalcTime + interval; tick <= until; tick += interval)
        {
            long target = EveCheckpointTarget(tick - start);
            long deficit = Math.Max(0, target - global.Progress);
            long weighted = deficit > 0 ? Math.Max(1, deficit * coefficient / 10000) : 0;
            global.Progress = Math.Min(total, global.Progress + Math.Min(cap, Math.Min(deficit, weighted)));
            global.LastCalcTime = tick;
        }
        return global.LastCalcTime != beforeTime || global.Progress != beforeProgress;
    }

    private long EveCheckpointTarget(long elapsed)
    {
        if (!_data.Table("ActivityConstant").TryGetValue("SeasonEveProgressCheckPoint", out var configured)) return 0;
        long previousTime = 0, previousTarget = 0;
        foreach (var point in configured.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 2) continue;
            long time = (long)(GameData.Number(point[0]) ?? 0);
            long target = (long)(GameData.Number(point[1]) ?? 0);
            if (time <= previousTime) continue;
            if (elapsed <= time)
                return previousTarget + (target - previousTarget) * Math.Max(0, elapsed - previousTime) / (time - previousTime);
            previousTime = time;
            previousTarget = target;
        }
        return previousTarget;
    }

    private long EveCoefficient(int population)
    {
        if (!_data.Table("ActivityConstant").TryGetValue("SeasonEveProgressCheckCoefficient", out var configured)) return 10000;
        foreach (var band in configured.EnumerateArray())
            if (band.ValueKind == JsonValueKind.Array && band.GetArrayLength() >= 2
                && GameData.Number(band[0]) is { } ceiling && population <= ceiling)
                return (long)(GameData.Number(band[1]) ?? 10000);
        return 10000;
    }

    private JourneyGetPlayerCompleteResp JourneyPlayerComplete(GameSession session, long playerId)
    {
        var res = new JourneyGetPlayerCompleteResp { PlayerId = playerId };
        if (playerId != session.Role!.Id) return res;
        var state = Journey(session, 112);
        foreach (var (id, value) in state.CompleteNode) res.CompleteNode[id] = value.Clone();
        foreach (var (id, value) in state.CompleteAct) res.CompleteAct[id] = value.Clone();
        foreach (var (id, value) in state.CompleteEvent) res.CompleteEvent[id] = value.Clone();
        foreach (var (id, value) in state.CompleteTravel) res.CompleteTravel[id] = value.Clone();
        foreach (var (id, value) in state.Attributes) res.Attributes[id] = value;
        res.GainCoinCount = state.GainCoinCount;
        res.TitleId = state.TitleId;
        return res;
    }

    private async Task<int> JourneyEnterAsync(GameSession session, long activityId)
    {
        if (ActivityError(session, activityId, 15) is var error and not 0) return error;
        var next = session.NewActivities!.Clone();
        var journey = next.ActivityInfo[activityId].ActivityParam.Journey;
        if (journey.ReportEnterTime > 0) return 0;
        journey.ReportEnterTime = Now;
        return await CommitAdventureAsync(session, next);
    }

    private async Task<int> JourneyPanelAsync(GameSession session, long activityId)
    {
        if (ActivityError(session, activityId, 15) is var error and not 0) return error;
        var next = session.NewActivities!.Clone();
        next.ActivityInfo[activityId].ActivityParam.Journey.IsOpenCompletePanel = true;
        return await CommitAdventureAsync(session, next);
    }

    private async Task<int> JourneyRestartAsync(GameSession session, long activityId)
    {
        if (ActivityError(session, activityId, 15) is var error and not 0) return error;
        return await _dungeons.RestartJourneyAsync(session);
    }

    private async Task<int> CompleteJourneyAsync(GameSession session, long activityId, JourneyCompleteReq req, bool serverVictory = false,
        Action<Ro3Emu.Server.Accounts.Persistence.RoleItemPlanBuilder>? persist = null,
        Action? committed = null, string? journeySource = null)
    {
        // A durable dungeon source already earned this exact act while its activity was open.
        bool earnedVictory = serverVictory && journeySource != null;
        if (!earnedVictory && ActivityError(session, activityId, 15) is var error and not 0) return error;
        var next = session.NewActivities!.Clone();
        var journey = next.ActivityInfo[activityId].ActivityParam.Journey;
        long id = req.CompleteId;
        long nodeId = journey.CurNodeId;
        var node = _data.Row("JourneyNodeConfig", nodeId);
        if (node == null && (req.Type is EJourneyCompleteType.Act or EJourneyCompleteType.Node))
            return ErrorCodes.Parameter;
        long chapter = node == null ? 0 : GameData.Int(node.Value, "_iChapter");
        var done = new JourneyCompleteInfo { ChapterId = chapter, Count = 1 };
        long reward = 0;
        var extra = new List<AwardGrant>();
        switch (req.Type)
        {
            case EJourneyCompleteType.Act:
            case EJourneyCompleteType.Game:
                if (req.Type == EJourneyCompleteType.Game)
                {
                    id = journey.CurActIds.Keys.FirstOrDefault(candidate =>
                        _data.Row("JourneyActConfig", candidate) is { } gameAct
                        && JourneyAction(gameAct) == 406 && JourneyGameLevel(gameAct) == req.CompleteId);
                    if (id == 0) return ErrorCodes.TaskRequirements;
                }
                if (journey.CompleteAct.ContainsKey(id) && _data.Row("JourneyActConfig", id) is { } completedAct
                    && JourneyBossScene(completedAct) > 0) return 0;
                if (!journey.CurActIds.ContainsKey(id) || _data.Row("JourneyActConfig", id) is not { } act
                    || GameData.Int(act, "_iNode") != nodeId || journey.CompleteAct.ContainsKey(id)
                    || JourneyBossScene(act) > 0 && !serverVictory
                    || (JourneyAction(act) == 406) != (req.Type == EJourneyCompleteType.Game)
                    || !earnedVictory && !_functions.ConditionMet(session, GameData.Int(act, "_iCondition"))) return ErrorCodes.TaskRequirements;
                journey.CompleteAct[id] = done;
                journey.CurActIds.Remove(id);
                // LC_JourneyHelperPartialAction.FindActIdByCondition selects one act from a
                // predecessor's candidate set. Its siblings are alternative dialogue branches.
                foreach (var predecessor in _data.Table("JourneyActConfig").Values)
                {
                    long previousId = GameData.Int(predecessor, "_iID");
                    if (!journey.CompleteAct.ContainsKey(previousId) || !Contains(predecessor, "_kNext", id)
                        || !predecessor.TryGetProperty("_kNext", out var siblings) || siblings.ValueKind != JsonValueKind.Array) continue;
                    foreach (var sibling in siblings.EnumerateArray())
                        journey.CurActIds.Remove((long)(GameData.Number(sibling) ?? 0));
                }
                if (JourneyBossScene(act) == 0 && JourneyAction(act) == 408
                    && (journey.TravelInfo.CurDay <= 7 || journey.TravelInfo.RemainHour > 0)) return ErrorCodes.TaskRequirements;
                if (!ApplyJourneyActions(journey, act, "_iResultAction", extra, out _)) return ErrorCodes.Parameter;
                if (act.TryGetProperty("_kNext", out var successors) && successors.ValueKind == JsonValueKind.Array)
                    foreach (var successor in successors.EnumerateArray())
                    {
                        long nextId = (long)(GameData.Number(successor) ?? 0);
                        if (nextId > 0 && _data.Row("JourneyActConfig", nextId) != null) journey.CurActIds[nextId] = true;
                    }
                break;
            case EJourneyCompleteType.Node:
                if (id != nodeId || journey.CurActIds.Count > 0 || journey.CompleteNode.ContainsKey(id)) return ErrorCodes.TaskRequirements;
                journey.CompleteNode[id] = done;
                journey.CurNodeId = GameData.Int(node!.Value, "_iNext");
                if (_data.Row("JourneyNodeConfig", journey.CurNodeId) is { } nextNode && GameData.Int(nextNode, "_iFirstAct") is var first and > 0)
                    journey.CurActIds[first] = true;
                break;
            case EJourneyCompleteType.AwardNode:
                if (!journey.CompleteNode.ContainsKey(id) || journey.CompleteAwardNode.ContainsKey(id)
                    || _data.Row("JourneyNodeConfig", id) is not { } awardedNode) return ErrorCodes.TaskRequirements;
                journey.CompleteAwardNode[id] = true;
                reward = GameData.Int(awardedNode, "_iAward");
                break;
            case EJourneyCompleteType.Travel:
                if (_data.Row("JourneyTravelConfig", id) is not { } travel || !JourneyTravelOpen(journey)
                    || journey.EventInfo.EventId != 0 || journey.TravelInfo.CurDay is < 1 or > 7
                    || GameData.Int(travel, "_iTime") <= 0
                    || GameData.Int(travel, "_iTime") > journey.TravelInfo.RemainHour) return ErrorCodes.TaskRequirements;
                long hours = GameData.Int(travel, "_iTime");
                journey.CompleteTravel[id] = new JourneyCompleteInfo
                {
                    ChapterId = chapter, Count = journey.CompleteTravel.GetValueOrDefault(id)?.Count + 1 ?? 1,
                };
                journey.TravelInfo.RemainHour -= hours;
                if (!travel.TryGetProperty("_kEventGroup", out var eventGroups)
                    || eventGroups.ValueKind != JsonValueKind.Array
                    || !StartJourneyEvent(journey, eventGroups.EnumerateArray().Select(x => (long)(GameData.Number(x) ?? 0)), extra))
                    return ErrorCodes.Parameter;
                break;
            case EJourneyCompleteType.TravelDay:
                if (!JourneyTravelOpen(journey) || journey.EventInfo.EventId != 0
                    || journey.TravelInfo.CurDay is < 1 or > 7) return ErrorCodes.TaskRequirements;
                // (inferred) Ending a day may discard unused hours, as the client confirms.
                journey.TravelInfo.CurDay++;
                journey.TravelInfo.RemainHour = journey.TravelInfo.CurDay <= 7 ? 5 : 0;
                if (journey.TravelInfo.CurDay == 8)
                {
                    // (inferred) Finishing the authored seventh day settles action 408
                    // and exposes its dialogue successor without an extra client request.
                    long travelActId = journey.CurActIds.Keys.First(x =>
                        _data.Row("JourneyActConfig", x) is { } travelAct && JourneyAction(travelAct) == 408);
                    journey.CurActIds.Remove(travelActId);
                    journey.CompleteAct[travelActId] = done;
                    if (_data.Row("JourneyActConfig", travelActId) is { } finished
                        && finished.TryGetProperty("_kNext", out var daySuccessors)
                        && daySuccessors.ValueKind == JsonValueKind.Array)
                        foreach (var successor in daySuccessors.EnumerateArray())
                            journey.CurActIds[(long)(GameData.Number(successor) ?? 0)] = true;
                }
                break;
            case EJourneyCompleteType.Event:
                long pendingId = journey.EventInfo.EventId;
                if (pendingId == 0 || _data.Row("JourneyEventConfig", pendingId) is not { } pending
                    || _data.Row("JourneyEventConfig", id) is not { } eventRow) return ErrorCodes.TaskRequirements;
                bool branch = pending.TryGetProperty("_kBtn", out var buttons)
                    && buttons.ValueKind == JsonValueKind.Array && buttons.GetArrayLength() > 0;
                if (branch ? !Contains(pending, "_kBtn", id) : id != pendingId) return ErrorCodes.TaskRequirements;
                CountJourneyEvent(journey, pendingId, chapter);
                if (branch) CountJourneyEvent(journey, id, chapter);
                journey.CurEvents.Clear();
                journey.EventInfo = new JourneyEventInfo();
                if (!ApplyJourneyActions(journey, eventRow, "_iResultAction", extra, out var followGroups)
                    || followGroups.Count > 0 && !StartJourneyEvent(journey, followGroups, extra)) return ErrorCodes.Parameter;
                break;
            default:
                return ErrorCodes.Parameter;
        }
        var change = new JourneyAttrChangeNotify { Type = req.Type, CompleteId = req.CompleteId };
        var previousAttributes = Journey(session, activityId).Attributes;
        foreach (var (attribute, value) in journey.Attributes)
            if (value != previousAttributes.GetValueOrDefault(attribute))
                change.AttrChange[attribute] = value - previousAttributes.GetValueOrDefault(attribute);
        // The client panel computes old/new values from its existing asset plus this delta. Send after
        // the source transaction succeeds but before 5013 replaces that asset (inferred wire ordering).
        return await CommitAdventureAsync(session, next, reward > 0 ? [reward] : null, extra: extra,
            beforeSync: change.AttrChange.Count == 0 ? null : () => session.SendGameAsync(Notify,
                new CommonActivityNotify { ActivityId = activityId, JourneyAttrChange = change }),
            persist: persist, committed: committed, journeySource: journeySource);
    }

    private IEnumerable<JsonElement> JourneyPublicArray(string key)
    {
        if (_data.Table("PublicParamConfig").TryGetValue(key, out var value)
            && value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray();
        return [];
    }

    private bool JourneyTravelOpen(DBJourneyInfo journey) => journey.CurActIds.Keys.Any(id =>
        _data.Row("JourneyActConfig", id) is { } row && JourneyAction(row) == 408
        && GameData.Int(row, "_iNode") == journey.CurNodeId);

    private static long JourneyAction(JsonElement row) => row.TryGetProperty("_iAction", out var actions)
        && actions.ValueKind == JsonValueKind.Array && actions.GetArrayLength() > 0
        && actions[0].ValueKind == JsonValueKind.Array && actions[0].GetArrayLength() > 0
            ? (long)(GameData.Number(actions[0][0]) ?? 0) : 0;

    private static long JourneyGameLevel(JsonElement row) => row.TryGetProperty("_iAction", out var actions)
        && actions.ValueKind == JsonValueKind.Array && actions.GetArrayLength() > 0
        && actions[0].ValueKind == JsonValueKind.Array && actions[0].GetArrayLength() > 2
            ? (long)(GameData.Number(actions[0][2]) ?? 0) : 0;

    private static void CountJourneyEvent(DBJourneyInfo journey, long id, long chapter) =>
        journey.CompleteEvent[id] = new JourneyCompleteInfo
        {
            ChapterId = chapter, Count = journey.CompleteEvent.GetValueOrDefault(id)?.Count + 1 ?? 1,
        };

    private bool StartJourneyEvent(DBJourneyInfo journey, IEnumerable<long> groupIds, List<AwardGrant> grants)
    {
        var groups = groupIds.Where(id => id > 0).ToHashSet();
        var candidates = _data.Table("JourneyEventConfig").Values
            .Where(row => groups.Contains(GameData.Int(row, "_iGroup"))
                && GameData.Int(row, "_iWeight") > 0 && JourneyEventCondition(journey, GameData.Int(row, "_iCondition")))
            .OrderBy(row => GameData.Int(row, "_iID")).ToArray();
        long total = candidates.Sum(row => GameData.Int(row, "_iWeight"));
        if (total <= 0) return false;
        long ticket = Random.Shared.NextInt64(total);
        var chosen = candidates.First(row => (ticket -= GameData.Int(row, "_iWeight")) < 0);
        long eventId = GameData.Int(chosen, "_iID");
        journey.CurEvents.Clear();
        journey.CurEvents[eventId] = true;
        journey.EventInfo = new JourneyEventInfo { EventId = eventId };
        var previous = journey.Attributes.ToDictionary(kv => kv.Key, kv => kv.Value);
        if (!ApplyJourneyActions(journey, chosen, "_kEnterAction", grants, out _)) return false;
        foreach (var (attr, value) in journey.Attributes)
            if (value != previous.GetValueOrDefault(attr)) journey.EventInfo.AttrChange[attr] = value - previous.GetValueOrDefault(attr);
        return true;
    }

    private bool JourneyEventCondition(DBJourneyInfo journey, long conditionId)
    {
        if (conditionId == 0) return true;
        if (_data.Row("LC_ConditionList", conditionId) is not { } condition
            || !condition.TryGetProperty("m_kSubConditionList", out var subs) || subs.ValueKind != JsonValueKind.Array) return false;
        foreach (var sub in subs.EnumerateArray())
        {
            if (!sub.TryGetProperty("m_kData", out var values) || values.ValueKind != JsonValueKind.Array
                || values.GetArrayLength() < 4 || GameData.Number(values[0]) != 15100) return false;
            long attr = (long)(GameData.Number(values[1]) ?? 0);
            long min = (long)(GameData.Number(values[2]) ?? 0), max = (long)(GameData.Number(values[3]) ?? 0);
            bool met = journey.Attributes.GetValueOrDefault(attr) >= min && journey.Attributes.GetValueOrDefault(attr) <= max;
            bool inverse = sub.TryGetProperty("m_bInverse", out var inv) && inv.ValueKind == JsonValueKind.True;
            if (met == inverse) return false;
        }
        return true;
    }

    private static bool ApplyJourneyActions(DBJourneyInfo journey, JsonElement row, string field,
        List<AwardGrant> grants, out List<long> groups)
    {
        groups = [];
        if (!row.TryGetProperty(field, out var actions) || actions.ValueKind != JsonValueKind.Array) return true;
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Array || action.GetArrayLength() == 0) return false;
            long type = (long)(GameData.Number(action[0]) ?? 0);
            if (type == 0) continue;
            if (type == 400 && action.GetArrayLength() == 4)
            {
                long attr = (long)(GameData.Number(action[1]) ?? 0);
                long min = (long)(GameData.Number(action[2]) ?? 0), max = (long)(GameData.Number(action[3]) ?? 0);
                if (attr <= 0 || max < min) return false;
                // (inferred) Result action 400 draws an inclusive attribute delta.
                journey.Attributes[attr] = checked(journey.Attributes.GetValueOrDefault(attr) + Random.Shared.NextInt64(min, max + 1));
            }
            else if (type == 401 && action.GetArrayLength() == 3)
            {
                long min = (long)(GameData.Number(action[1]) ?? 0), max = (long)(GameData.Number(action[2]) ?? 0);
                if (min < 0 || max < min || max > int.MaxValue) return false;
                // (inferred) 401 grants JourneyCoinItemId (PublicParamConfig) and tracks cumulative coins.
                int count = checked((int)Random.Shared.NextInt64(min, max + 1));
                journey.GainCoinCount = checked(journey.GainCoinCount + count);
                if (count > 0) grants.Add(new AwardGrant(1100000, count));
            }
            else if (type == 402 && action.GetArrayLength() >= 2)
            {
                for (int index = 1; index < action.GetArrayLength(); index++)
                    groups.Add((long)(GameData.Number(action[index]) ?? 0));
            }
            else return false;
        }
        return true;
    }

    private static long JourneyBossScene(JsonElement row) =>
        row.TryGetProperty("_iAction", out var actions) && actions.ValueKind == JsonValueKind.Array
        && actions.GetArrayLength() > 0 && actions[0].ValueKind == JsonValueKind.Array
        && actions[0].GetArrayLength() >= 3 && GameData.Number(actions[0][0]) == 407
            ? (long)(GameData.Number(actions[0][1]) ?? 0) : 0;

    private async Task<bool> JourneyDungeonWonAsync(GameSession session, DungeonService.JourneyVictory victory,
        Action<Ro3Emu.Server.Accounts.Persistence.RoleItemPlanBuilder> persist, Action committed)
    {
        using var owner = session.Owners == null ? null
            : await session.Owners.EnterWithGateAsync("dungeon/journey-activity", session.ActivityGate,
                [Ownership.ReservationKey.Role(session.Role!.Id)]);
        if (owner == null) await session.ActivityGate.WaitAsync();
        try
        {
            if (!session.ActivitiesReady || session.NewActivities?.ActivityInfo.TryGetValue(112, out var activity) != true)
                return false;
            if (activity.Playbook != victory.PlayBook || activity.CurStartTime != victory.ActivityStart
                || activity.CurEndTime != victory.ActivityEnd) return false;
            var journey = Journey(session, 112);
            if (journey.CompleteAct.ContainsKey(victory.ActId))
            {
                var transaction = _roles.NewTransaction();
                persist(transaction);
                await _roles.CommitAsync(transaction, "dungeon/journey-acknowledge", _ => committed());
                return true;
            }
            if (journey.CurNodeId != victory.NodeId || !journey.CurActIds.ContainsKey(victory.ActId)
                || _data.Row("JourneyActConfig", victory.ActId) is not { } row
                || JourneyBossScene(row) != victory.SceneEntry) return false;
            return await CompleteJourneyAsync(session, 112, new JourneyCompleteReq
            { CompleteId = victory.ActId, Type = EJourneyCompleteType.Act }, serverVictory: true,
                persist: persist, committed: committed, journeySource: victory.Source) == 0;
        }
        finally { session.ActivityGate.Release(); }
    }
}
