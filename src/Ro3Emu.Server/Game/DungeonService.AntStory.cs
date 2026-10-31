using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    private const long AntVortexScene = 1000201, AntDeniroScene = 1000301;
    private const int DeniroNpc = 5300005, DeniroRide = 5300005, DeniroRideBuff = 5365401;
    private const int AntAutoPathVar = 1100;
    internal const long AntSlideState = 5400001;
    private const long AntSlideSpeed = 5400003, AntSlideView = 5400004, AntSlideCamera = 5400005;
    private const long AntSlideSelect = 5400006, AntSlideBody = 5400007, AntSlideAim = 5400008;
    // 原服本次 31018 的 [600,3000] 為側向／下滑 cm/s；Deniro 的 31081 水平速度為 12 m/s。
    private const double AntSlideMetresPerSecond = 30, AntRideMetresPerSecond = 12;

    // WorldCoreService 已依賴 DungeonService，以委派連接其 typed custom 同步，避免 DI 循環。
    internal Func<GameSession, CustomData, Task>? SetAntCustomAsync { get; set; }
    private readonly Dictionary<GameSession, AntTraversal> _antTraversals = new();
    private readonly Dictionary<Attempt, AntStory> _antStories = new();

    private sealed class AntStory
    {
        public bool CameraPlayed { get; set; }
        public bool CompletionPlayed { get; set; }
        public Vec3? ActorTarget { get; set; }
        public int TargetGeneration { get; set; }
    }

    private sealed class AntTraversal(GameSession session, Attempt attempt, int generation)
    {
        public Attempt Attempt { get; } = attempt;
        public int Generation { get; } = generation;
        public long SceneGeneration { get; } = session.SceneGeneration;
        public Role? Role { get; } = session.Role;
        public Ownership.AttachmentLease? Attachment { get; } = session.Attachment;
        public WorldSessionState WorldState { get; } = session.WorldState;
        public bool Sliding { get; set; }
        public SceneActorUnit? Carrier { get; set; }
        public long SeatToken { get; set; }
        public Dictionary<long, CustomData?> Previous { get; } = new();
        public Dictionary<long, CustomData> Applied { get; } = new();
    }

    private static bool AntAttemptCurrent(Attempt a, int generation) => !a.Settled && !a.Room.Closed
        && a.StepGeneration == generation && Present(a).Any(s => a.Loaded.Contains(s) && !s.SceneLoading
            && s.Handoff == null && s.Attachment?.Cancelled != true);

    private static bool AntTraversalCurrent(GameSession s, AntTraversal state) => ReferenceEquals(s.DungeonAttempt, state.Attempt)
        && ReferenceEquals(s.Role, state.Role) && ReferenceEquals(s.Attachment, state.Attachment)
        && ReferenceEquals(s.WorldState, state.WorldState) && s.SceneGeneration == state.SceneGeneration
        && s.Attachment?.Cancelled != true && s.Handoff == null
        && s.InstanceId == state.Attempt.Room.Id && state.Attempt.Loaded.Contains(s) && !s.SceneLoading
        && AntAttemptCurrent(state.Attempt, state.Generation);

    /// <summary>已接受進度的步驟入口；舊 generation 的鏡頭／移動回呼不得作用於下一階段。</summary>
    private async Task BeginAntStoryStepAsync(Attempt a)
    {
        if (a.Plan.SceneEntry is not (AntVortexScene or AntDeniroScene)) return;
        if (!_antStories.TryGetValue(a, out var story)) _antStories[a] = story = new();
        foreach (var s in Present(a).Where(a.Loaded.Contains))
        {
            if (_antTraversals.TryGetValue(s, out var old) && old.Generation != a.StepGeneration)
                await ClearAntTraversalAsync(s, old);
            if (a.Plan.SceneEntry == AntVortexScene && a.Wave == 2) await StartAntSlideAsync(a, s);
        }
        if (a.Plan.SceneEntry != AntDeniroScene) return;
        story.TargetGeneration = a.StepGeneration;
        if (a.Wave == 1 && !story.CameraPlayed)
        {
            story.CameraPlayed = true;
            await AntClientFuncAsync(a, "ActiveCameraTimeline",
                new EventParams { StrParams = { "mianstorybingyicamera.prefab" } }, new EventParams { DoubleParams = { 9000 } });
            int generation = a.StepGeneration;
            // 本次實包鏡頭至 1100 相隔約 4 秒；只還原這段順序，不宣稱所有分支皆有固定延遲。
            _loop.ScheduleLegacy(Paced(4000), () => AntAttemptCurrent(a, generation)
                ? SetAntActorTargetAsync(a, story, new Vec3 { X = 87, Y = 252, Z = 325 }) : Task.CompletedTask, "ant story camera target");
        }
        else if (a.Wave == 2)
            await SetAntActorTargetAsync(a, story, new Vec3 { X = 69, Y = 251, Z = 292 });
    }

    private async Task StartAntSlideAsync(Attempt a, GameSession s)
    {
        if (SetAntCustomAsync == null || _antTraversals.ContainsKey(s) || !AntAttemptCurrent(a, a.StepGeneration)) return;
        var state = new AntTraversal(s, a, a.StepGeneration) { Sliding = true };
        _antTraversals[s] = state;
        // 31018 實包順序先給參數，再開啟 SlideState；LC_CustomDataHandler_Slide 交給移動控制器。
        CustomData[] values = [AntList(AntSlideSpeed, 600, 3000), AntLong(AntSlideView, 3), AntLong(AntSlideCamera, 71),
            AntLong(AntSlideSelect, 0), AntList(AntSlideBody, 0, 0), AntList(AntSlideAim, 0, 0), AntLong(AntSlideState, 1)];
        foreach (var value in values)
        {
            if (!AntTraversalCurrent(s, state)) { await ClearAntTraversalAsync(s, state); return; }
            state.Previous[value.Id] = s.WorldState.CustomData.GetValueOrDefault(value.Id)?.Clone();
            state.Applied[value.Id] = value.Clone();
            await SetAntCustomAsync(s, value);
        }
    }

    private static CustomData AntLong(long id, long value) => new()
    {
        Id = id, LongMin = long.MinValue, LongMax = long.MaxValue,
        CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = value },
    };

    private static CustomData AntList(long id, params long[] values) => new()
    { Id = id, CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.ListLong, ListLongParam = { values } } };

    /// <summary>只提高已授權 traversal 的速度上限；nav／airwall 仍由 SceneService 驗證。</summary>
    internal double? AntStoryMovementSpeed(GameSession s) => _antTraversals.TryGetValue(s, out var state) && AntTraversalCurrent(s, state)
        ? state.Sliding ? AntSlideMetresPerSecond : AntSeatCurrent(s, state) ? AntRideMetresPerSecond : null
        : null;

    private Task SetAntActorTargetAsync(Attempt a, AntStory story, Vec3 target)
    {
        if (a.SceneActors.FirstOrDefault(u => u.NpcId == DeniroNpc) is not { } actor) return Task.CompletedTask;
        story.ActorTarget = target;
        story.TargetGeneration = a.StepGeneration;
        return BroadcastAsync(a, Event, new DungeonEventNotify
        {
            DungeonEntry = a.Plan.SceneEntry, EventType = (DungeonEventType)4,
            Vars = { new DungeonVar { VarId = AntAutoPathVar, Vi64 = { actor.Uid, (long)target.X, (long)target.Y, (long)target.Z } } },
        });
    }

    /// <summary>31089 只接受當前 1100 指定的 Deniro 終點；不是任意 NPC 傳送介面。</summary>
    internal async Task<bool> TrySyncAntActorPositionAsync(GameSession s, ClientTeleportObject req)
    {
        if (s.SceneEntry != AntDeniroScene) return false;
        if (Active(s) is not { } a || !_antStories.TryGetValue(a, out var story) || story.ActorTarget is not { } target
            || story.TargetGeneration != a.StepGeneration || req.TargetPos is not { } pos
            || !float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z) || !float.IsFinite(req.Face)
            || MonsterService.Distance(pos.X, pos.Z, target.X, target.Z) > 4 || Math.Abs(pos.Y - target.Y) > 5
            || a.SceneActors.FirstOrDefault(u => u.Uid == req.TargetUid && u.NpcId == DeniroNpc) is not { } actor) return true;
        actor.X = pos.X; actor.Y = pos.Y; actor.Z = pos.Z; actor.Face = req.Face;
        await _world.MoveUnitAsync(actor, new MovementChangeNtf
        {
            Uid = actor.Uid, TransType = MovementTransitionType.MoveTransForceTeleport,
            MovementInfo = new MovementInfo { Pos = pos.Clone(), Face = req.Face, ServerTime = MonsterService.NowMs },
        });
        return true;
    }

    /// <summary>第一次 5300008 不上車；第二次 5300005 持續載乘，直到區域31、退出或離場。</summary>
    private async Task<bool> TrySeatAntRiderAsync(Attempt a, GameSession s, SceneActorUnit actor, int interaction)
    {
        if (a.Plan.SceneEntry != AntDeniroScene) return false;
        if (_antTraversals.TryGetValue(s, out var stale) && (!AntTraversalCurrent(s, stale) || !AntSeatCurrent(s, stale)))
            await ClearAntTraversalAsync(s, stale);
        if (a.Wave != 4 || a.StepDone || actor.NpcId != DeniroNpc || interaction != DeniroRide
            || actor.Buffs.All(b => b.ConfigId != DeniroRideBuff) || _antTraversals.ContainsKey(s)) return true;
        var state = new AntTraversal(s, a, a.StepGeneration) { Carrier = actor };
        long token;
        try
        {
            token = await _vehicles.SeatOnSceneActorAsync(s, actor.Uid, actor.NpcId, interaction,
                beforeNotify: () => BoardAntRiderAsync(a, s, actor, state), send3C: false);
        }
        catch
        {
            // seat owner會回收當次token；AOI快照也必須立即回到Idle，送出失敗不得留下幽靈載具。
            actor.StoryInteraction = new InteractInfo { EType = InteractInfo.Types.EState.Idle };
            if (!a.Room.Closed)
                try { await AntCarrierStateAsync(a, actor, active: false); }
                catch (Exception e) { LogRideMoveFailed(e, a.Plan.SceneEntry, actor.NpcId); }
            throw;
        }
        if (token == 0) return true;
        state.SeatToken = token;
        // 等候通知期間可能已換場／重連；舊登車結果不可覆蓋新scope。
        if (!AntTraversalCurrent(s, state) || _antTraversals.ContainsKey(s))
        { await ClearAntSeatAsync(s, state); return true; }
        _antTraversals[s] = state;
        if (!AntTraversalCurrent(s, state)) { await ClearAntTraversalAsync(s, state); return true; }
        if (_antStories.TryGetValue(a, out var story)) story.ActorTarget = null;
        await BroadcastAsync(a, Event, new DungeonEventNotify
        {
            DungeonEntry = a.Plan.SceneEntry, EventType = (DungeonEventType)4,
            Vars = { new DungeonVar { VarId = AntAutoPathVar } },
        });
        return true;
    }

    private async Task BoardAntRiderAsync(Attempt a, GameSession s, SceneActorUnit actor, AntTraversal state)
    {
        if (s.Role is not { } role || !AntTraversalCurrent(s, state)) return;
        await AntCarrierStateAsync(a, actor, active: true);
        if (!AntTraversalCurrent(s, state)) return;
        var offset = VehicleSeatConfig.Get(_data, DeniroRide)?.Offset ?? [];
        var shown = actor.Pos.Clone();
        if (offset.Length >= 3) { shown.X += offset[0] / 100f; shown.Y += offset[1] / 100f; shown.Z += offset[2] / 100f; }
        var move = new MovementChangeNtf
        {
            Uid = role.Id, TransType = MovementTransitionType.MoveTransBeCarriedMove,
            MovementInfo = new MovementInfo { Pos = shown, Face = actor.Face, ServerTime = MonsterService.NowMs },
        };
        // 原服上車畫面加上座位高度，但之後 31082 的 XYZ 是載具地面基準。
        s.PreviousPosition = s.Position; s.Position = actor.Pos.Clone(); s.Face = actor.Face; s.LastWalkUtc = DateTime.UtcNow;
        s.Walking = false;
        await s.SendGameAsync(GameAoiSink.MovementChange, move);
        await _world.MoveAsync(s, new WalkReq { Pos = s.Position.Clone(), Face = s.Face, BStop = true, SpeedRate = 1 }, movement: move);
    }

    private Task AntCarrierStateAsync(Attempt a, SceneActorUnit actor, bool active)
    {
        actor.StoryInteraction = active
            ? new InteractInfo { EType = InteractInfo.Types.EState.Vehicle, StateId = 3, ActiveObjId = actor.Uid }
            : new InteractInfo { EType = InteractInfo.Types.EState.Idle };
        return BroadcastAsync(a, WorldInteractionService.Change,
            new InteractChangeNtf { Uid = actor.Uid, DataInfo = actor.StoryInteraction.Clone() });
    }

    private bool AntSeatCurrent(GameSession s, AntTraversal state) => state.Carrier is { } carrier && state.SeatToken != 0
        && _vehicles.IsSeatedOnSceneActor(s, state.SeatToken, carrier.Uid);

    private bool AntStoryRegionAllowed(GameSession s, long region) => s.SceneEntry != AntDeniroScene || region != 31
        || _antTraversals.TryGetValue(s, out var state) && AntTraversalCurrent(s, state)
        && AntSeatCurrent(s, state);

    /// <summary>只跟隨已接受的本人移動，不再用 guide36 強制拉動玩家或按推測時間完成任務。</summary>
    private async Task AntStoryPlayerMovedAsync(GameSession s)
    {
        if (!_antTraversals.TryGetValue(s, out var state)) return;
        if (!AntTraversalCurrent(s, state) || state.Carrier != null && !AntSeatCurrent(s, state))
        { await ClearAntTraversalAsync(s, state); return; }
        if (state.Carrier is not { } carrier) return;
        carrier.X = s.Position.X; carrier.Y = s.Position.Y; carrier.Z = s.Position.Z; carrier.Face = s.Face;
        var move = new MovementInfo { Pos = s.Position.Clone(), Face = s.Face, ServerTime = MonsterService.NowMs };
        if (s.Walking)
        {
            double radians = s.Face * Math.PI / 180;
            // (inferred) 1秒預測上限沿用既有飛行同步；本次實包約1.4秒，通用原服計算式尚未知。
            move.MoveType = MovementType.MoveTypeMove; move.MoveDuration = 1000;
            move.MovementMove = new MovementMove
            {
                StartVelocity = new Vec3 { X = (float)(Math.Sin(radians) * AntRideMetresPerSecond), Z = (float)(Math.Cos(radians) * AntRideMetresPerSecond) },
                AccVelocity = new Vec3(),
            };
        }
        await _world.MoveUnitAsync(carrier, new MovementChangeNtf { Uid = carrier.Uid, MovementInfo = move });
    }

    private async Task ClearAntTraversalAsync(GameSession s, AntTraversal state)
    {
        if (!_antTraversals.TryGetValue(s, out var current) || !ReferenceEquals(current, state)) return;
        _antTraversals.Remove(s);
        await ClearAntSeatAsync(s, state);
        // 先停滑行，再還原此scope實際寫入的快照；重連後的新WorldState不屬於舊scope。
        foreach (var (id, applied) in state.Applied.OrderBy(p => p.Key == AntSlideState ? 0 : 1))
        {
            if (_antTraversals.ContainsKey(s)) break;
            if (!state.WorldState.CustomData.TryGetValue(id, out var now) || !now.Equals(applied)) continue;
            var previous = state.Previous[id];
            var restore = previous ?? (applied.CustomParam.Tp == CustomParam.Types.ParamType.ListLong
                ? AntList(id, applied.CustomParam.ListLongParam.Select(_ => 0L).ToArray()) : AntLong(id, 0));
            if (SetAntCustomAsync != null && s.Role != null && !s.SceneLoading
                && ReferenceEquals(s.WorldState, state.WorldState) && ReferenceEquals(s.Role, state.Role))
                try { await SetAntCustomAsync(s, restore); }
                catch (Exception e) { LogAntCleanupFailed(e, state.Attempt.Plan.SceneEntry); }
            if (_antTraversals.ContainsKey(s)) break;
            if (state.WorldState.CustomData.TryGetValue(id, out var changed) && !changed.Equals(applied) && !changed.Equals(restore)) continue;
            if (previous == null) state.WorldState.CustomData.Remove(id); else state.WorldState.CustomData[id] = previous.Clone();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ant story scene {Scene} cleanup notification failed; local state is still cleared")]
    private partial void LogAntCleanupFailed(Exception e, long scene);

    private async Task ClearAntSeatAsync(GameSession s, AntTraversal state)
    {
        if (state.Carrier != null)
        {
            state.Carrier.StoryInteraction = new InteractInfo { EType = InteractInfo.Types.EState.Idle };
            if (!state.Attempt.Room.Closed)
                try { await AntCarrierStateAsync(state.Attempt, state.Carrier, active: false); }
                catch (Exception e) { LogAntCleanupFailed(e, state.Attempt.Plan.SceneEntry); }
            if (state.SeatToken != 0)
                try { await _vehicles.PutDownFromSceneActorAsync(s, state.SeatToken); }
                catch (Exception e) { LogAntCleanupFailed(e, state.Attempt.Plan.SceneEntry); }
        }
    }

    private Task AntStorySeatEndedAsync(GameSession s, long token, long actorUid) => _antTraversals.TryGetValue(s, out var state)
        && state.SeatToken == token && state.Carrier?.Uid == actorUid ? ClearAntTraversalAsync(s, state) : Task.CompletedTask;

    private async Task LeaveAntStoryAsync(GameSession s)
    {
        if (_antTraversals.TryGetValue(s, out var state)) await ClearAntTraversalAsync(s, state);
        if (s.DungeonAttempt is { } a && Present(a).Length <= 1) _antStories.Remove(a);
    }

    private async Task EndAntStoryAsync(Attempt a)
    {
        foreach (var (s, state) in _antTraversals.Where(p => ReferenceEquals(p.Value.Attempt, a)).ToArray())
            await ClearAntTraversalAsync(s, state);
    }

    /// <summary>成功30132／任務交付之後才落下；重送或多位參與者不重播。</summary>
    private async Task CompleteAntStoryAsync(Attempt a)
    {
        if (a.Plan.SceneEntry != AntVortexScene || a.Room.Closed || !_antStories.TryGetValue(a, out var story) || story.CompletionPlayed) return;
        story.CompletionPlayed = true;
        if (_catalog.ScenePoint(a.Plan.SceneEntry, 27) is not { } end) return;
        foreach (var s in Present(a).Where(s => a.Loaded.Contains(s) && !s.SceneLoading))
        {
            if (s.Role is not { } role) continue;
            await s.SendGameAsync(Event, new DungeonEventNotify
            {
                EventType = (DungeonEventType)6, CliFuncname = "PerformUnitMove",
                Params = { new EventParams { IntParams = { role.Id } }, new EventParams { Vec3Params = { new Vec3 { X = end.X, Y = end.Y, Z = end.Z } } },
                    new EventParams { DoubleParams = { 0.3914491534233093 } }, new EventParams { IntParams = { 6 } }, new EventParams { BoolParams = { false } } },
            });
        }
    }

    private Task AntClientFuncAsync(Attempt a, string name, params EventParams[] args) => BroadcastAsync(a, Event, new DungeonEventNotify
    { EventType = (DungeonEventType)6, CliFuncname = name, Params = { args } });
}
