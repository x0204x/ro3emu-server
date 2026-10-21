using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// NPC-owned gameplay states. NPCInteractConfig selects GameplayStatusConfig; VehicleConfig selects the
/// VehicleSeatConfig rows. LC_GamePlayStatusMgr.lua:876-931 consumes 32702, and LC_VehicleServer.lua:143-167 sends 32708.
/// </summary>
public sealed class WorldInteractionService
{
    public const uint Change = 32702, Action = 32703, Change3C = 32704,
        SeatChange = 32708, VehicleSync = 31074;

    private sealed record Active(WorldNpc Npc, int InteractionId, int StatusId, int StatusType, int SeatIndex);
    private readonly Dictionary<GameSession, Active> _active = new();
    private readonly Dictionary<(long Scene, long Npc, int Seat), GameSession> _seats = new();
    private readonly GameData _data;
    private readonly World _world;
    private readonly WorldData _worldData;

    public WorldInteractionService(GameData data, World world, WorldData worldData, SceneService scene)
    {
        this._data = data;
        this._world = world;
        this._worldData = worldData;
        scene.ValidateInteraction = ValidateInteraction;
        scene.ApplyInteraction = ApplyInteractionAsync;
        scene.Leaving.Add(LeaveAsync);
        // MiniMap_ServerCellWidget refuses line changes while interacting (client lines 350-377).
        scene.LineSwitchChecks.Add(s => _active.ContainsKey(s) ? ErrorCodes.NpcParameter : 0);
        world.PlayerEntered.Add(PlayerEnteredAsync);
    }

    private static int Id(JsonElement row, string key) => (int)GameData.Int(row, key);
    private static int[] Numbers(JsonElement row, string key) => row.TryGetProperty(key, out var values)
        && values.ValueKind == JsonValueKind.Array
        ? values.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToArray() : [];
    private static bool InWorld(GameSession s) => s.Role != null && !s.SceneLoading;
    private static bool Near(GameSession s, WorldNpc npc)
    {
        var dx = s.Position.X - npc.X; var dz = s.Position.Z - npc.Z;
        return dx * dx + dz * dz <= 36;
    }
    private bool StillHere(GameSession s, Active a) => InWorld(s) && a.Npc.SceneEntry == s.SceneEntry
        && _worldData.Npc(a.Npc.Uid) == a.Npc;
    private bool SeatFree(GameSession s, WorldNpc npc, int seat) =>
        !_seats.TryGetValue((s.WorldScene, npc.Uid, seat), out var owner) || owner == s;

    /// <summary>Called before 32700 success is echoed. Unknown predicates are closed, not assumed true.</summary>
    public int ValidateInteraction(GameSession s, WorldNpc npc, NpcInteractionReq req)
    {
        if (!InWorld(s) || s.SceneEntry != npc.SceneEntry || !Near(s, npc)) return ErrorCodes.NpcTooFar;
        if (_data.Row("NPCInteractConfig", req.InteractionId) is not { } interaction) return 0; // dialogue-only NPC action
        int statusId = Id(interaction, "_iStatusID");
        if (statusId == 0)
            return _active.TryGetValue(s, out var current) && current.Npc.Uid == npc.Uid
                && req.InteractionId == 4900025 ? 0 : 0; // ordinary authored action remains with SceneService
        if (_active.ContainsKey(s)) return ErrorCodes.NpcParameter;
        if (_data.Row("GameplayStatusConfig", statusId) is not { } status) return ErrorCodes.NpcParameter;
        int type = Id(status, "_iStatusType");
        if (type is not (3 or 4)) return ErrorCodes.FeatureUnavailable; // cannon/collection require separate simulation
        if (!ConditionMet(s, npc, Id(interaction, "_iCondition"), -1)
            || !ConditionMet(s, npc, Id(interaction, "_iNPCCondition"), -1)
            || !ConditionMet(s, npc, Id(status, "_iCondition"), -1)) return ErrorCodes.NpcParameter;
        if (type == 4) return 0;
        if (_data.Row("NPCConfig", npc.NpcId) is not { } npcRow || Id(npcRow, "_iNPCSubType") != 103
            || _data.Row("VehicleConfig", Id(npcRow, "_iSubTypeValue")) is not { } vehicle) return ErrorCodes.NpcParameter;
        int seat = Id(interaction, "_iStatusLocation") - 1;
        int[] configuredSeats = Numbers(vehicle, "_kSeats");
        return seat >= 0 && seat < configuredSeats.Length && _data.Row("VehicleSeatConfig", configuredSeats[seat]) != null
            && SeatFree(s, npc, seat) ? 0 : ErrorCodes.NpcParameter;
    }

    // (inferred) Only the directly evidenced status/seat predicates are evaluated here. GVG permission,
    // camp ownership and scripted gates require their owners and therefore reject until available.
    private bool ConditionMet(GameSession s, WorldNpc npc, int conditionId, int seat)
    {
        if (conditionId == 0) return true;
        if (_data.Row("LC_ConditionList", conditionId) is not { } row) return false;
        if (row.TryGetProperty("m_kSubConditionList", out var list) && list.ValueKind == JsonValueKind.Array)
            return list.EnumerateArray().All(sub => SubCondition(s, npc, sub, seat));
        return SubCondition(s, npc, row, seat);
    }
    private bool SubCondition(GameSession s, WorldNpc npc, JsonElement row, int seat)
    {
        if (!row.TryGetProperty("m_kData", out var values) || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() < 2) return false;
        int type = (int)(GameData.Number(values[0]) ?? 0), arg = (int)(GameData.Number(values[1]) ?? 0);
        bool yes = type switch
        {
            12001 => _active.TryGetValue(s, out var state) && state.StatusType == arg,
            12002 or 12003 => SeatFree(s, npc, seat >= 0 ? seat : arg),
            _ => false,
        };
        bool inverse = row.TryGetProperty("m_bInverse", out var inv) && inv.ValueKind == JsonValueKind.True;
        return inverse ? !yes && (type is 12001 or 12002 or 12003) : yes;
    }

    public async Task ApplyInteractionAsync(GameSession s, WorldNpc npc, NpcInteractionReq req)
    {
        if (_active.TryGetValue(s, out var old))
        {
            if (old.Npc.Uid == npc.Uid && req.InteractionId == 4900025) await ExitAsync(s, false);
            return;
        }
        if (ValidateInteraction(s, npc, req) != 0 || _data.Row("NPCInteractConfig", req.InteractionId) is not { } interaction) return;
        int statusId = Id(interaction, "_iStatusID");
        if (statusId == 0 || _data.Row("GameplayStatusConfig", statusId) is not { } status) return;
        int type = Id(status, "_iStatusType"), seat = type == 3 ? Id(interaction, "_iStatusLocation") - 1 : -1;
        _active[s] = new Active(npc, req.InteractionId, statusId, type, seat);
        if (seat >= 0) _seats[(s.WorldScene, npc.Uid, seat)] = s;
        await NotifyAsync(s, _active[s], false);
        if (seat >= 0) await Send3CAsync(s, _active[s]);
    }

    private async Task NotifyAsync(GameSession s, Active state, bool interrupt)
    {
        var info = new InteractInfo
        {
            EType = (InteractInfo.Types.EState)state.StatusType, StateId = state.StatusId,
            InteractConfigId = state.InteractionId, ActiveObjId = state.Npc.Uid,
        };
        var message = new InteractChangeNtf { Uid = s.Role!.Id, DataInfo = info, Interrupt = interrupt };
        await s.SendGameAsync(Change, message);
        foreach (var viewer in _world.Viewers(s)) await viewer.SendGameAsync(Change, message);
    }

    private async Task ExitAsync(GameSession s, bool interrupt)
    {
        if (!_active.Remove(s, out var state)) return;
        if (state.SeatIndex >= 0) _seats.Remove((s.WorldScene, state.Npc.Uid, state.SeatIndex));
        if (s.Role == null || s.SceneLoading) return;
        var message = new InteractChangeNtf
        {
            Uid = s.Role.Id,
            DataInfo = new InteractInfo { EType = InteractInfo.Types.EState.Idle }, Interrupt = interrupt
        };
        await s.SendGameAsync(Change, message);
        foreach (var viewer in _world.Viewers(s)) await viewer.SendGameAsync(Change, message);
    }

    private Task LeaveAsync(GameSession s) => ExitAsync(s, true);
    /// <summary>Disconnect and account-mode cleanup when SceneService.Leaving is not invoked.</summary>
    public Task DisconnectAsync(GameSession s)
    {
        if (_active.Remove(s, out var state) && state.SeatIndex >= 0)
            _seats.Remove((s.WorldScene, state.Npc.Uid, state.SeatIndex));
        return Task.CompletedTask;
    }
    private async Task PlayerEnteredAsync(GameSession viewer, GameSession subject)
    {
        if (_active.TryGetValue(subject, out var state) && StillHere(subject, state))
            await viewer.SendGameAsync(Change, new InteractChangeNtf
            {
                Uid = subject.Role!.Id,
                DataInfo = new InteractInfo
                {
                    EType = (InteractInfo.Types.EState)state.StatusType,
                    StateId = state.StatusId, InteractConfigId = state.InteractionId, ActiveObjId = state.Npc.Uid
                }
            });
    }

    /// <summary>32703: only a configured QuitStatus action is accepted; skill grants stay with combat.</summary>
    public async Task UseActionAsync(GameSession s, UseInteractActionReq req)
    {
        int code = ErrorCodes.NpcParameter;
        if (_active.TryGetValue(s, out var state) && StillHere(s, state)
            && _data.Row("GameplayStatusButtonConfig", req.ButtonId) is { } button
            && Id(button, "_iButtonFuncType") == 0
            && button.TryGetProperty("_kAction", out var actions)
            && actions.ToString().Contains("5004", StringComparison.Ordinal)
            && _data.Row("ActionConfig", 5004) is { } action && action.GetProperty("_iFunctionName").GetString() == "QuitStatus"
            && (state.StatusType == 4 && req.ButtonId == 42
                || state.StatusType == 3 && VehicleSeats(state) is { Length: > 0 } configured
                    && state.SeatIndex >= 0 && state.SeatIndex < configured.Length
                    && _data.Row("VehicleSeatConfig", configured[state.SeatIndex]) is { } seatRow
                    && Numbers(seatRow, "_kGameplayBtnId").Contains((int)req.ButtonId)))
        {
            await ExitAsync(s, false); code = 0;
        }
        await s.SendGameAsync(Action, new UseInteractActionRes { Result = code });
    }

    private int[] VehicleSeats(Active state)
    {
        if (_data.Row("NPCConfig", state.Npc.NpcId) is not { } npcRow
            || _data.Row("VehicleConfig", Id(npcRow, "_iSubTypeValue")) is not { } vehicle) return [];
        return Numbers(vehicle, "_kSeats");
    }
    private async Task Send3CAsync(GameSession s, Active state)
    {
        int[] configured = VehicleSeats(state);
        if (state.SeatIndex < 0 || state.SeatIndex >= configured.Length) return;
        if (_data.Row("VehicleSeatConfig", configured[state.SeatIndex]) is { } seat)
            await s.SendGameAsync(Change3C, new Change3CNtf { CfgId = Id(seat, "_iChange3C") });
    }

    /// <summary>32708: seatIdx is the zero-based VehicleConfig._kSeats index.</summary>
    public async Task ChangeSeatAsync(GameSession s, ChangeVehicleSeatReq req)
    {
        int code = ErrorCodes.NpcParameter;
        if (_active.TryGetValue(s, out var state) && state.StatusType == 3 && StillHere(s, state))
        {
            int[] configured = VehicleSeats(state);
            if (req.SeatIdx >= 0 && req.SeatIdx < configured.Length && req.SeatIdx != state.SeatIndex
                && _data.Row("VehicleSeatConfig", configured[req.SeatIdx]) != null
                && SeatFree(s, state.Npc, req.SeatIdx))
            {
                _seats.Remove((s.WorldScene, state.Npc.Uid, state.SeatIndex));
                _seats[(s.WorldScene, state.Npc.Uid, req.SeatIdx)] = s;
                _active[s] = state with { SeatIndex = req.SeatIdx };
                code = 0;
            }
        }
        await s.SendGameAsync(SeatChange, new ChangeVehicleSeatRes { SeatIdx = req.SeatIdx, Result = code });
        if (code == 0) await Send3CAsync(s, _active[s]);
    }

    /// <summary>31074: vehicle facing is only accepted from the occupied driver seat.</summary>
    public async Task SyncVehicleAsync(GameSession s, SyncVehicleReq req)
    {
        if (!float.IsFinite(req.Face) || req.Face < 0 || req.Face > 360
            || !_active.TryGetValue(s, out var state) || state.StatusType != 3 || state.SeatIndex != 0
            || !StillHere(s, state)) return;
        await _world.TurnAsync(s, req.Face);
    }
}
