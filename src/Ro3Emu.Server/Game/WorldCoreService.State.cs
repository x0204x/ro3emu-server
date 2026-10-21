using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class WorldCoreService
{
    public const uint SceneVariables = 30600, SceneEvent = 30601;
    private readonly Dictionary<long, Dictionary<int, CommonPublicVar>> _sceneVariables = new();

    /// <summary>LC_SceneVariableManager consumes inner var_id and isDelete; never rely on an empty All packet to clear.</summary>
    public async Task SetSceneVariableAsync(long worldScene, CommonPublicVar value)
    {
        if (value.VarId <= 0 || value.Vd64.Any(v => !double.IsFinite(v)) || value.ListVec3.Any(v => !Finite(v)))
            throw new ArgumentException("A scene variable needs a positive id and finite values.");
        if (!_sceneVariables.TryGetValue(worldScene, out var vars)) _sceneVariables[worldScene] = vars = new();
        if (value.IsDelete) vars.Remove(value.VarId); else vars[value.VarId] = value.Clone();
        var packet = new SceneEventNotify { SceneId = worldScene }; packet.Vars[value.VarId] = value.Clone();
        foreach (var player in _world.PlayersIn(worldScene)) await player.SendGameAsync(SceneEvent, packet);
    }

    private Task SceneVariablesAsync(GameSession s)
    {
        if (!_sceneVariables.TryGetValue(s.WorldScene, out var values) || values.Count == 0) return Task.CompletedTask;
        var packet = new SceneBaseInfoNotify { SceneId = s.WorldScene, ChangeType = (PublicVarChangeType)2 };
        foreach (var (id, value) in values) packet.Vars[id] = value.Clone();
        return s.SendGameAsync(SceneVariables, packet);
    }

    private sealed record Admission(long Scene, MapPoint? At, int Reason, long Order, SceneService.TransferStart? Transfer);
    private readonly Dictionary<GameSession, Admission> _admissions = new();
    private long _admissionOrder;

    private async Task<bool> QueueAdmissionAsync(GameSession s, long map, MapPoint? at, int reason, SceneService.TransferStart? transfer)
    {
        if (s.Role == null) return false;
        long order = _admissions.TryGetValue(s, out var old) && old.Scene == map ? old.Order : ++_admissionOrder;
        _admissions[s] = new Admission(map, at, reason, order, transfer);
        s.WorldState.QueueScene = map;
        await QueueSnapshotAsync(s, _admissions[s]);
        return true;
    }
    private Task QueueSnapshotAsync(GameSession s, Admission a) => s.SendGameAsync(SceneQueue, new SceneQueuingRes
    {
        SceneId = a.Scene, SceneEntryId = a.Scene,
        LineOrder = _admissions.Values.Count(q => q.Scene == a.Scene && q.Order <= a.Order), EstimateSeconds = 0,
    });

    public Task QueueAsync(GameSession s, SceneQueuingReq req) => _admissions.TryGetValue(s, out var a) && a.Scene == req.SceneId
        ? QueueSnapshotAsync(s, a) : ErrorAsync(s, SceneQueue, ErrorCodes.Parameter);

    public Task CancelQueueAsync(GameSession s, SceneQueuingCancel req)
    {
        if (_admissions.TryGetValue(s, out var a) && a.Scene == req.SceneId) { _admissions.Remove(s); s.WorldState.QueueScene = 0; }
        return Task.CompletedTask;
    }

    private async Task AdmitQueuedAsync()
    {
        var active = _sessions.All().ToHashSet();
        foreach (var (s, a) in _admissions.OrderBy(p => p.Value.Order).ToArray())
        {
            // The snapshot can be stale after awaiting an earlier player's admission. Removal is the
            // in-flight boundary: cancellation or replacement before it must discard this callback.
            if (!_admissions.TryGetValue(s, out var current) || !ReferenceEquals(current, a)) continue;
            if (!active.Contains(s) || s.Role == null) { _admissions.Remove(s); continue; }
            if (s.SceneLoading || s.Handoff != null || _lines.TryReserve(s, a.Scene, follow: PreferredLine(s, a.Scene)) == null) continue;
            _admissions.Remove(s); s.WorldState.QueueScene = 0;

            try
            {
                await s.SendGameAsync(SceneQueue, new SceneQueuingRes { SceneId = a.Scene, SceneEntryId = a.Scene, LineOrder = 0 });
                await _scene.EnterSceneAsync(s, a.Scene, a.At, a.Reason, transfer: a.Transfer);
            }
            finally { if (s.Handoff == null) _lines.Release(s); }
        }
    }

    /// <summary>Authoritative typed custom data; operator producers are explicit, never accepted from a client push id.</summary>
    public async Task SetCustomAsync(GameSession s, CustomData value)
    {
        if (!Ready(s) || value.Id <= 0 || value.CustomParam == null) throw new ArgumentException("Custom data needs a positive id and typed value.");
        s.WorldState.CustomData[value.Id] = value.Clone();
        var packet = new CustomDataChange { EntityId = s.Role!.Id, CustomDatas = { value } };
        await ToSelfAndViewersAsync(s, CustomChange, packet);
        foreach (var viewer in _sessions.All().Where(v => Ready(v) && v.WorldScene == s.WorldScene && v.WorldState.Monitored.Contains(s.Role.Id)))
            await viewer.SendGameAsync(MonitorSync, packet);
    }

    /// <summary>Operator-requested, cancellable return to the public town; countdown lifecycle is (inferred).</summary>
    public async Task BeginTemporaryLeaveAsync(GameSession s, int seconds)
    {
        if (!Ready(s) || seconds is < 1 or > 600) throw new ArgumentException("Countdown must be 1..600 seconds.");
        s.WorldState.TemporaryLeaveUntilMs = Now + seconds * 1000L;
        await s.SendGameAsync(TempLeave, new TempLeaveNtf { CountdownSeconds = seconds });
    }

    private async Task ForceLeaveAsync(GameSession s)
    {
        s.WorldState.TemporaryLeaveUntilMs = 0;
        await s.SendGameAsync(ForceLeave, new ForceLeaveNtf { LeaveFromSceneEntry = s.SceneEntry });
        await _scene.EnterSceneAsync(s, 10100);
    }

    public void Disconnected(GameSession s)
    {
        _admissions.Remove(s);
        _lines.Release(s);
        if (s.InstanceId != 0 && _world.PlayersIn(s.WorldScene).Count == 0) _sceneVariables.Remove(s.WorldScene);
        s.WorldState = new WorldSessionState();
    }
}
