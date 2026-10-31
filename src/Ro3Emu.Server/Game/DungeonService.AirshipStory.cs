using System.Runtime.CompilerServices;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;
using Kind = Ro3Emu.Server.Game.ReconstructedDungeons.TriggerKind;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 飛船 1000501 的場景演出。依 2026-10-31 單次原服成功路徑重建事件與相對節奏，
/// 不是封包重播；戰鬥、QTE 結束及獎勵仍由原有權威流程處理。毫秒排程是觀察值的近似。
/// </summary>
public sealed partial class DungeonService
{
    private const int AirshipOpeningEnd = 5300541, AirshipAftermathEnd = 5300566;
    private const int AirshipCaptainOption = 5300039, AirshipRollSkill = 5352801;
    private readonly ConditionalWeakTable<Attempt, AirshipStoryState> _airshipStories = new();

    private sealed class AirshipStoryState
    {
        public int Generation;
        public int ClearedWave = -1;
        public int SceneWave = -1;
        public int SceneChat = 5300001;
        public bool CannonReady, CaptainStarted, Rolled, Stopped;
        public long RollSerial;
    }

    private AirshipStoryState AirshipState(Attempt a) => _airshipStories.GetValue(a, static _ => new AirshipStoryState());

    /// <summary>Catalog 僅認可本場景兩個內部標記，不能放寬其他任務訊息的配置驗證。</summary>
    internal static bool IsAirshipStoryTimedTrigger(long scene, ReconstructedDungeons.ScriptedStep step,
        ReconstructedDungeons.StepTrigger trigger) => scene == AirshipScene && trigger.Kind == Kind.Message
        && (step.Quest, trigger.Id) is (1, AirshipOpeningEnd) or (5, AirshipAftermathEnd);

    /// <summary>巡視開始後約八秒才重新出現船員；不要先送 AOI 再立即刪除。</summary>
    private static IReadOnlyList<ReconstructedDungeons.SceneActor> AirshipStoryActors(
        Attempt a, ReconstructedDungeons.ScriptedStep step) =>
        a.Plan.SceneEntry == AirshipScene && a.Wave == 5 ? [] : step.Actors;

    private async Task BeginAirshipStoryStepAsync(Attempt a)
    {
        if (a.Plan.SceneEntry != AirshipScene) return;
        var state = AirshipState(a);
        state.SceneWave = a.Wave;
        state.SceneChat = AirshipPhaseChat(a.Wave);
        await AirshipSceneChatAsync(a, state.SceneChat);
        switch (a.Wave)
        {
            case 0: await StartAirshipOpeningAsync(a, state); break;
            case 2:
                AirshipLater(a, state, 500, () => AirshipShoutsAsync(a, 5300558, 4380041), () => a.Wave == 2);
                break;
            case 5: await StartAirshipAftermathAsync(a, state); break;
        }
    }

    private async Task StartAirshipOpeningAsync(Attempt a, AirshipStoryState state)
    {
        await AirshipClientAsync(a, "ActiveCameraTimeline", Text("mainstoryfeikongtingcamera1.prefab"), Real(3500));
        AirshipLines(a, state, 5000, 5300540, 4380035);
        AirshipLines(a, state, 10000, 5300541);
        AirshipLater(a, state, 14000, () => CompleteAirshipTimedStepAsync(a, 0, AirshipOpeningEnd));
        AirshipLines(a, state, 16000, 4380036, 5300550);
        AirshipLater(a, state, 20000, async () =>
        {
            await AirshipShoutsAsync(a, 5300551);
            await AirshipClientAsync(a, "ActiveCameraTimeline", Text("mainstoryfeikongtingcamera2.prefab"), Real(9500));
        }, () => a.Wave <= 1);
        AirshipLines(a, state, 24000, 4380037, 5300552);
        AirshipLines(a, state, 32000, 4380038, 5300553);
        AirshipLines(a, state, 36000, 4380039, 5300554);
        AirshipLater(a, state, 39500, async () =>
        {
            state.CannonReady = true;
            await BroadcastAsync(a, BaseInfo, Snapshot(a));
        }, () => a.Wave == 1);
    }

    private async Task StartAirshipAftermathAsync(Attempt a, AirshipStoryState state)
    {
        await ResetAirshipRollAsync(a, state, 0);
        await AirshipShoutsAsync(a, 5300561, 4380042);
        AirshipLines(a, state, 4000, 5300562, wave: 5);
        AirshipLines(a, state, 8000, 5300563, 4380043, wave: 5);
        AirshipLater(a, state, 8200, () => SyncSceneActorsAsync(a, Step(a)!.Actors), () => a.Wave == 5);
        AirshipLines(a, state, 10500, 5300564, wave: 5);
        AirshipLines(a, state, 13000, 5300565, wave: 5);
        AirshipLater(a, state, 15500, async () =>
        {
            await AirshipShoutsAsync(a, 4380044, 5300566);
            await CompleteAirshipTimedStepAsync(a, 5, AirshipAftermathEnd);
        }, () => a.Wave == 5);
    }

    /// <summary>內部訊號只是劇本計時完成，不是新增 wire OPCODE，也不是玩家的互動授權。</summary>
    private async Task CompleteAirshipTimedStepAsync(Attempt a, int wave, long message)
    {
        if (a.Wave == wave && Present(a).FirstOrDefault(a.Loaded.Contains) is { } member)
            await SendAirshipInternalStepEventAsync(a, member, message);
    }

    private async Task SendAirshipInternalStepEventAsync(Attempt a, GameSession member, long message)
    {
        await StepEventAsync(a, member, [(Kind.Message, message)], airshipInternal: true);
    }

    /// <summary>計時標記不得由玩家偽造；130161／84012 仍無法跳過原有 QTE 結束閘。</summary>
    private static bool AirshipStoryStepEventAllowed(Attempt a, bool airshipInternal)
    {
        if (a.Plan.SceneEntry != AirshipScene || a.Wave is not (0 or 5 or 6)) return true;
        return a.Wave == 6 && a.StepOutroPending || airshipInternal;
    }

    /// <summary>最後一艘船真正死亡後才喊話並等待登船；重送死亡事件不重啟排程。</summary>
    private async Task AirshipStoryWaveClearedAsync(Attempt a)
    {
        if (a.Plan.SceneEntry != AirshipScene || a.Wave != 1) return;
        var state = AirshipState(a);
        if (state.ClearedWave == a.Wave) return;
        state.ClearedWave = a.Wave;
        state.SceneChat = 5300004;
        await AirshipSceneChatAsync(a, state.SceneChat);
        await AirshipShoutsAsync(a, 5300556);
        AirshipLater(a, state, 3000, () => AirshipShoutsAsync(a, 4380040, 5300557), () => a.Wave == 1);
    }

    /// <summary>上砲在開場約 39.5 秒才開放。仍須先通過原有 NPC、距離與佔用驗證。</summary>
    private int AirshipStoryInteractionError(Attempt a, SceneActorUnit actor, long interaction) =>
        a.Plan.SceneEntry == AirshipScene && actor.NpcId == 5300022 && interaction == 5300006
        && (!_airshipStories.TryGetValue(a, out var state) || !state.CannonReady)
            ? ErrorCodes.NpcParameter : 0;

    /// <summary>
    /// 原服5353001用targetsId的一個目標，不帶targetId。只在已驗證上砲的grant內繫結；
    /// 多目標、互相矛盾或房外UID均拒絕，不取任意第一個。
    /// </summary>
    private bool BindAirshipCannonTarget(Attempt a, GameSession member, ReconstructedDungeons.ScriptedStep step, CastSkillReq req)
    {
        if (a.Plan.SceneEntry != AirshipScene) return true;
        if (req.TargetsId.Count > 1 || req.TargetId != 0 && req.TargetsId.Count == 1 && req.TargetsId[0] != req.TargetId)
            return false;
        long target = req.TargetId != 0 ? req.TargetId : req.TargetsId.SingleOrDefault();
        if (target == 0) return AirshipCannonEmptyAimAllowed(member, req);
        if (!a.Room.Monsters.Any(m => m.Uid == target && !m.Dead && m.Hp > 0
            && step.Fight.Any(spawn => spawn.NpcId == m.Template.NpcId))) return false;
        req.TargetId = target;
        return true;
    }

    /// <summary>原服50995及51081的空瞄仍成功施放；位置須有限、在射程內且可見，不自行尋敵。</summary>
    private bool AirshipCannonEmptyAimAllowed(GameSession member, CastSkillReq req)
    {
        if (Active(member) is not { } a || a.Plan.SceneEntry != AirshipScene || Step(a)?.Cannon is not { } cannon
            || !a.CannonUsers.TryGetValue(member, out var actor) || req.SkillId != cannon.FireSkill
            || req.OpUnitId != 0 && req.OpUnitId != member.Role?.Id && req.OpUnitId != actor.Uid
            || req.TargetId != 0 || req.TargetsId.Count != 0 || req.TargetPos is not { } point
            || !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)
            || SkillConfig.Get(_data, cannon.FireSkill) is not { } config || !_buffs.CanSeePosition(member, point)) return false;
        double dx = (double)point.X - member.Position.X, dy = (double)point.Y - member.Position.Y,
            dz = (double)point.Z - member.Position.Z;
        double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return distance >= config.DistanceMin / 100f && distance <= config.DistanceMax / 100f + .5f;
    }

    /// <summary>已接受的空瞄只播放及支付原有施放，不把附近單位重新當作命中目標。</summary>
    private static bool AirshipCannonPositionOnlyCast(GameSession member, ActiveSkillCast cast) =>
        member.SceneEntry == AirshipScene && cast.Request.SkillId == 5353001
        && cast.Request.TargetId == 0 && cast.Request.TargetsId.Count == 0;

    /// <summary>
    /// 原服 5353001 的 18 次砲擊皆送出 31108／6120105，目標為座標；61098 模板無彈道事件。
    /// 飛行時間由 EffectData 的速度 50 計算；空瞄也呈現，但不產生命中。
    /// </summary>
    private CastProjectile? AirshipCannonProjectile(GameSession member, ActiveSkillCast cast) =>
        member.SceneEntry == AirshipScene && cast.Request.SkillId == 5353001
        && Active(member) is { } attempt && attempt.CannonUsers.ContainsKey(member)
            ? new CastProjectile(6120105, PositionTarget: true) : null;

    /// <summary>合法泡泡只 ACK，不再要求四船員／五次戰後互動。船長選項只啟動一次收尾。</summary>
    private async Task<bool> TryAirshipStoryInteractionAsync(Attempt a, GameSession member,
        SceneActorUnit actor, long interaction)
    {
        if (a.Plan.SceneEntry != AirshipScene) return false;
        if (a.Wave == 0 || a.Wave == 5) return true;
        if (a.Wave != 6 || actor.NpcId != 5300038) return false;
        if (interaction != AirshipCaptainOption) return true;
        var state = AirshipState(a);
        if (state.CaptainStarted) return true;
        state.CaptainStarted = true;
        await AirshipClientAsync(a, "ClearAllShouts");
        AirshipLines(a, state, 4600, 5300568, 4380045, wave: 6);
        AirshipLater(a, state, 12000, async () =>
        {
            // 互動者已斷線時使用仍在同一房間的參與者，不保留失效 session 當作權威來源。
            var current = Present(a).FirstOrDefault(s => ReferenceEquals(s, member) && a.Loaded.Contains(s))
                ?? Present(a).FirstOrDefault(a.Loaded.Contains);
            if (current != null) await SendAirshipInternalStepEventAsync(a, current, AirshipCaptainOption);
        }, () => a.Wave == 6);
        return true;
    }

    /// <summary>三艘敵船的出生位置來自原服 AOI 50761；不是三次射擊或固定傷害規則。</summary>
    private static MapPoint AirshipStorySpawnPoint(Attempt a, int npc, MapPoint fallback, int waveIndex)
    {
        if (a.Plan.SceneEntry != AirshipScene || a.Wave != 1) return fallback;
        return (npc, waveIndex) switch
        {
            (5300015, 0) => new(346.8f, 207.65002f, 451.9f, fallback.Face),
            (5300053, 1) => new(332.9f, 207.65002f, 530.8f, fallback.Face),
            (5300053, 2) => new(333.6f, 207.65002f, 358.8f, fallback.Face),
            _ => fallback,
        };
    }

    private static bool AirshipStorySpawnGrounded(Attempt a) => a.Plan.SceneEntry != AirshipScene || a.Wave != 1;

    /// <summary>30401 與 30404 共用：登船敵人跨兩批累計 9；開砲前不提早解鎖第二項。</summary>
    private void ApplyAirshipStoryVars(Attempt a, IList<DungeonVar> vars)
    {
        if (a.Plan.SceneEntry != AirshipScene) return;
        var state = AirshipState(a);
        if (a.Wave == 1 && !state.CannonReady) ReplaceAirshipVar(vars, 10001, [1]);
        if (a.Wave is 2 or 3)
        {
            long killed = a.Stats.Values.Sum(s => s.KillNpcData.Where(p => p.Key is >= 5300042 and <= 5300050)
                .Sum(p => p.Value.LongValue));
            ReplaceAirshipVar(vars, 10003, [Math.Clamp(killed, 0, 9), 9]);
        }
        ReplaceAirshipVar(vars, 5, [state.SceneWave == a.Wave ? state.SceneChat : AirshipPhaseChat(a.Wave)]);
    }

    private static void ReplaceAirshipVar(IList<DungeonVar> vars, int id, IEnumerable<long> values)
    {
        var existing = vars.FirstOrDefault(v => v.VarId == id);
        if (existing != null) vars.Remove(existing);
        var next = new DungeonVar { VarId = id };
        next.Vi64.Add(values);
        vars.Add(next);
    }

    /// <summary>32702 同步砲台及操作者；砲台端不帶只屬於操作者的 interactConfigId。</summary>
    private async Task<bool> TryNotifyAirshipCannonEndpointsAsync(Attempt a, GameSession member, SceneActorUnit cannon,
        bool mounted, bool interrupt)
    {
        if (a.Plan.SceneEntry != AirshipScene) return false;
        InteractInfo CannonState() => mounted ? new InteractInfo
        {
            EType = InteractInfo.Types.EState.UpperCannon, StateId = 12, ActiveObjId = cannon.Uid,
            UpperCannonInfo = new UpperCannonState { CannonGuId = cannon.Uid },
        } : new InteractInfo { EType = InteractInfo.Types.EState.Idle };
        var cannonState = CannonState();
        cannon.StoryInteraction = cannonState;
        await BroadcastAsync(a, WorldInteractionService.Change, new InteractChangeNtf
        { Uid = cannon.Uid, Interrupt = interrupt, DataInfo = cannonState.Clone() });
        var operatorState = CannonState();
        if (mounted) operatorState.InteractConfigId = 5300006;
        if (member.Role is { } role)
            await BroadcastAsync(a, WorldInteractionService.Change, new InteractChangeNtf
            { Uid = role.Id, Interrupt = interrupt, DataInfo = operatorState });
        if (mounted)
        {
            var state = AirshipState(a);
            if (state.SceneChat == 5300002)
            {
                state.SceneChat = 5300003;
                await AirshipSceneChatAsync(a, state.SceneChat);
            }
        }
        return true;
    }

    /// <summary>只跟隨這次首領真實施放；不以副本經過時間無條件播放全場傾斜。</summary>
    private Task AirshipBossCastStartedAsync(MonsterUnit boss, int skillId, long castSerial)
    {
        if (skillId != AirshipRollSkill || boss.Template.NpcId != 5300018 || boss.SceneEntry != AirshipScene)
            return Task.CompletedTask;
        var a = _instances.Find(boss.InstanceId)?.ActiveMembers.Select(m => m.Session.DungeonAttempt)
            .FirstOrDefault(run => run?.Plan.SceneEntry == AirshipScene);
        if (a == null || a.Wave != 4) return Task.CompletedTask;
        var state = AirshipState(a);
        AirshipLater(a, state, 3440, async () =>
        {
            if (boss.Dead || boss.Removed || boss.Hp <= 0 || boss.BossSkills?.CastSerial != castSerial
                || _buffs.UnitForbidden(boss, ForbiddenState.CastSkill)) return;
            await ResetAirshipRollAsync(a, state, 0);
            state.Rolled = true;
            state.RollSerial = castSerial;
            await AirshipClientAsync(a, "SetMainCameraConfigId", Integer(88), Integer(11000), Integer(1200), Boolean(false));
            await AirshipClientAsync(a, "RollTheWholeScene", Real(0), Integer(400), Boolean(true),
                new EventParams { Vec3Params = { new Vec3 { X = -25 } } }, Integer(0));
        }, () => a.Wave == 4);
        AirshipLater(a, state, 8440, () => state.RollSerial == castSerial
            ? ResetAirshipRollAsync(a, state, 6000) : Task.CompletedTask);
        return Task.CompletedTask;
    }

    private Task ResetAirshipRollAsync(Attempt a, AirshipStoryState state, int duration)
    {
        if (!state.Rolled) return Task.CompletedTask;
        state.Rolled = false;
        return AirshipClientAsync(a, "ResetRollTheWholeScene", Integer(duration), Boolean(false), Integer(0));
    }

    /// <summary>離場／結算前呼叫，取消舊房間排程並復原仍在傾斜的鏡頭。</summary>
    private async Task StopAirshipStoryAsync(Attempt a)
    {
        if (a.Plan.SceneEntry != AirshipScene || !_airshipStories.TryGetValue(a, out var state)) return;
        state.Stopped = true;
        state.Generation++;
        await ResetAirshipRollAsync(a, state, 0);
    }

    private void AirshipLater(Attempt a, AirshipStoryState state, int ms, Func<Task> work, Func<bool>? valid = null)
    {
        int generation = state.Generation;
        _loop.ScheduleLegacy(Paced(ms), () => !a.Settled && !a.Room.Closed && !state.Stopped
            && state.Generation == generation && (valid == null || valid()) ? work() : Task.CompletedTask);
    }

    private void AirshipLines(Attempt a, AirshipStoryState state, int ms, int first, int? second = null, int? wave = null) =>
        AirshipLater(a, state, ms, () => second is { } next ? AirshipShoutsAsync(a, first, next) : AirshipShoutsAsync(a, first),
            () => wave is { } required ? a.Wave == required : a.Wave <= 1);

    private async Task AirshipShoutsAsync(Attempt a, params int[] chats)
    {
        foreach (int chat in chats) await AirshipClientAsync(a, "Shouting", Integer(chat));
    }

    private Task AirshipSceneChatAsync(Attempt a, int chat) => BroadcastAsync(a, Event, new DungeonEventNotify
    {
        DungeonEntry = AirshipScene, EventType = (DungeonEventType)4, StageNum = a.Wave + 1, LevelNum = 1,
        Vars = { new DungeonVar { VarId = 5, Vi64 = { chat } } },
    });

    private Task AirshipClientAsync(Attempt a, string function, params EventParams[] arguments) =>
        BroadcastAsync(a, Event, new DungeonEventNotify
        {
            DungeonEntry = AirshipScene, EventType = (DungeonEventType)6, StageNum = a.Wave + 1, LevelNum = 1,
            CliFuncname = function, Params = { arguments },
        });

    private static EventParams Integer(long value) => new() { IntParams = { value } };
    private static EventParams Real(double value) => new() { DoubleParams = { value } };
    private static EventParams Text(string value) => new() { StrParams = { value } };
    private static EventParams Boolean(bool value) => new() { BoolParams = { value } };
    private static int AirshipPhaseChat(int wave) => wave switch
    { 0 => 5300001, 1 => 5300002, 2 or 3 => 5300005, 4 => 5300006, _ => 5300008 };
}
