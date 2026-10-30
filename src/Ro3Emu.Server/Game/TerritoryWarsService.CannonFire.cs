using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Operating a built cannon (docs/systems/territory-wars.md "Firing the cannons"). The builder walks up and uses the cannon
/// NPC's interaction (NPCConfig _iInteractID 4900005 -> NPCInteractConfig _iStatusID 2, GameplayStatusConfig type 2): 32702
/// GVGCannon with enterCannonInfo makes LC_GamePlayStatusMgr.OperateCannon call LC_GVGFireCannon.StartFire, which enters
/// the cannon's own camera (GVGCannonConfig _iChange3CConfigID, client side). The fire button
/// (GameplayStatusButtonConfig _iButtonFuncType 2) sends 32703 with cannonUseSkill: the cannon skill and one
/// bulletExtraParam (gravity, speed, direction) per bullet of the volley (_iSingleShotNum). Each bullet is a unit of the
/// skill's bullet NPC (SkillConfig _kLogicData[2], NPCConfig _iNPCType 10) flying the ballistic MovementMove the client
/// draws (LC_Movement_MoveSyncMove: pos + v t + a t^2 / 2), and it explodes where it lands. The quit button (action 5004
/// QuitStatus) or the exit interaction (_iExitInteractID) leaves.
/// </summary>
public sealed partial class TerritoryWarsService
{
    public const uint NpcSkillCd = 32705;
    /// <summary>DirtyDataConfig 4900014 GVGCannonBulletRestNum (Long, on the cannon: its skill cost [6, 4900014, 1]) and
    /// 4900006 CameraFollowBullet_ListLong (on the shooter, sync scope 0: bullet uid, camera distance cm, x, y).</summary>
    private const long BulletRestNum = 4900014, CameraFollowBullet = 4900006;
    private const int QuitStatusAction = 5004, CannonStatusType = 2;
    private const int FireButton = 2;
    /// <summary>LC_ConditionList sub-condition types of the enter condition (4900001): CheckBuilder, CheckCamp, and
    /// 12001 the gameplay status, inverted (not already in one).</summary>
    private const long CheckBuilder = 2201, CheckCamp = 2200, InStatus = 12001;
    private const long BulletUidFlag = 1L << 40;
    private long _bulletSerial;
    private readonly Dictionary<GameSession, CannonUnit> _operators = [];

    private static long CampOf(GameSession s) => s.EventFaction > 0 ? s.EventFaction : 1;

    /// <summary>32700 on a cannon and 32703 from its operator; anything else goes on to its usual owner.</summary>
    public async Task<bool> CannonSharedAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (id == SceneService.NpcInteraction)
        {
            var req = NpcInteractionReq.Parser.ParseFrom(body.Span);
            if (!_cannons.TryGetValue(req.NpcUid, out var cannon)) return false;
            int code = await CannonInteractAsync(s, cannon.Unit, cannon.Room, req);
            if (code != 0) await Send(s, id, new NpcInteractionRsp { Result = code, Req = req });
            return true;
        }
        if (id != WorldInteractionService.Action || !_operators.TryGetValue(s, out var unit)) return false;
        var action = UseInteractActionReq.Parser.ParseFrom(body.Span);
        int result = await CannonActionAsync(s, unit, action);
        await Send(s, id, new UseInteractActionRes { Result = result });
        return true;
    }

    private async Task<int> CannonInteractAsync(GameSession s, CannonUnit unit, InstanceService.Room room, NpcInteractionReq req)
    {
        if (CannonConfig(unit) is not { } cfg) return ErrorCodes.NpcMissing;
        if (_operators.TryGetValue(s, out var operating))
        {
            // The exit interaction (NPCInteractConfig 4900002, ActionConfig 6 LeaveFiredCannon) on the operated cannon.
            if (operating != unit || req.InteractionId != cfg.ExitInteractId) return ErrorCodes.NpcParameter;
            await Send(s, SceneService.NpcInteraction, new NpcInteractionRsp { Result = 0, Req = req });
            await LeaveCannonAsync(s, interrupt: false);
            return 0;
        }
        int code = CanEnterCannon(s, unit, room, req);
        if (code != 0) return code;
        await Send(s, SceneService.NpcInteraction, new NpcInteractionRsp { Result = 0, Req = req });
        unit.Operator = s;
        _operators[s] = unit;
        await CannonStateAsync(s, unit, cfg, interrupt: false);
        await CannonBulletsAsync(s, unit);
        await CannonCooldownAsync(s, unit, cfg);
        return 0;
    }

    /// <summary>
    /// NPCConfig _iInteractID lists the interaction; NPCInteractConfig _iStatusID selects a GameplayStatusConfig of type 2
    /// (GVGCannon); its NPC condition is evaluated here: the builder, the camp and not already operating
    /// (LC_ConditionList sub-conditions; any other type is closed). Within NPCConfig _iInteractDistance, one operator.
    /// </summary>
    private int CanEnterCannon(GameSession s, CannonUnit unit, InstanceService.Room room, NpcInteractionReq req)
    {
        if (s.Role == null || s.Dead || s.SceneLoading || WarRoomOf(s)?.Room != room || unit.Removed || room.Closed)
            return ErrorCodes.NpcMissing;
        if (NPCConfig.Get(_data, unit.NpcId) is not { } npc || !npc.InteractId.Contains(req.InteractionId))
            return ErrorCodes.NpcParameter;
        if (NPCInteractConfig.Get(_data, req.InteractionId) is not { } interaction
            || GameplayStatusConfig.Get(_data, interaction.StatusId) is not { StatusType: CannonStatusType })
            return ErrorCodes.NpcParameter;
        if (MonsterService.Distance(s.Position.X, s.Position.Z, unit.X, unit.Z) > Math.Max(npc.InteractDistance, 3) + 1)
            return ErrorCodes.NpcTooFar;
        if (!CannonCondition(s, unit, interaction.NPCCondition)) return ErrorCodes.NpcParameter;
        return unit.Operator is { } other && other != s ? ErrorCodes.NpcParameter : 0;
    }

    private bool CannonCondition(GameSession s, CannonUnit unit, long conditionId)
    {
        if (conditionId == 0) return true;
        if (LCConditionList.Get(_data, conditionId) is not { } condition) return false;
        if (condition.SubConditionList is not { ValueKind: System.Text.Json.JsonValueKind.Array } list)
            return CannonSubCondition(s, unit, condition);
        return list.EnumerateArray().All(sub => CannonSubCondition(s, unit, new LCConditionList(sub)));
    }

    private bool CannonSubCondition(GameSession s, CannonUnit unit, LCConditionList sub)
    {
        if (sub.Data is not [var type, ..]) return false;
        bool? yes = type switch
        {
            // LC_CommonCondition.CheckBuilder and CheckCamp ignore their argument: the unit's Owner is the player, and
            // the unit and the player share a camp (inferred: a guild in a territory battle, where every camp is 1).
            CheckBuilder => unit.Builder == s.Role!.Id,
            CheckCamp => OwnSide(s, unit.Camp, unit.Guild),
            // (inferred) 12001 with argument 0: the player is in any gameplay status (here: operating a cannon).
            InStatus => _operators.ContainsKey(s),
            _ => null,
        };
        return yes is { } value && value != sub.Inverse;
    }

    private GVGCannonConfig? CannonConfig(CannonUnit unit) => GVGCannonConfig.Get(_data, unit.ConfigId);

    /// <summary>32702 GVGCannon (or Idle) to the operator and the players who see them.</summary>
    private async Task CannonStateAsync(GameSession s, CannonUnit? unit, GVGCannonConfig? cfg, bool interrupt)
    {
        if (s.Role == null) return;
        var info = unit == null || cfg == null ? new InteractInfo { EType = InteractInfo.Types.EState.Idle } : new InteractInfo
        {
            EType = InteractInfo.Types.EState.Gvgcannon, StateId = CannonStatusType, ActiveObjId = unit.Uid,
            EnterCannonInfo = new EnterCannonState { CannonUId = unit.Uid, GvgCannonConfigId = cfg.Value.Id },
        };
        var message = new InteractChangeNtf { Uid = s.Role.Id, DataInfo = info, Interrupt = interrupt };
        await Send(s, WorldInteractionService.Change, message);
        foreach (var viewer in _world.Viewers(s)) await Send(viewer, WorldInteractionService.Change, message);
    }

    /// <summary>31018 GVGCannonBulletRestNum on the cannon (the fire button's cost and count).</summary>
    private async Task CannonBulletsAsync(GameSession s, CannonUnit unit)
    {
        var change = new CustomDataChange { EntityId = unit.Uid, CustomDatas = { CannonLong(BulletRestNum, unit.Bullets) } };
        await Send(s, WorldCoreService.CustomChange, change);
        foreach (var viewer in _world.Viewers(s)) await Send(viewer, WorldCoreService.CustomChange, change);
    }

    /// <summary>32705 the cannon skill's cooldown on the cannon NPC.</summary>
    private Task CannonCooldownAsync(GameSession s, CannonUnit unit, GVGCannonConfig cfg)
    {
        var cd = new SkillCdInfo { SkillId = cfg.SkillId, CdStartTimestampMs = unit.CdStartMs, CdEndTimestampMs = unit.ReadyMs };
        return Send(s, NpcSkillCd, new NpcSkillsCdNotify { NpcUid = unit.Uid, SkillCds = { [cfg.SkillId] = cd } });
    }

    private static CustomData CannonLong(long id, long value) =>
        new() { Id = id, CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = value } };

    /// <summary>32703 of the operator: the quit button leaves, the fire button fires; anything else is refused.</summary>
    private async Task<int> CannonActionAsync(GameSession s, CannonUnit unit, UseInteractActionReq req)
    {
        if (CannonConfig(unit) is not { } cfg || !cfg.ButtonActionIds.Contains(req.ButtonId)
            || GameplayStatusButtonConfig.Get(_data, req.ButtonId) is not { } button) return ErrorCodes.NpcParameter;
        if (button.ButtonFuncType == 0 && QuitsStatus(button))
        {
            await LeaveCannonAsync(s, interrupt: false);
            return 0;
        }
        if (button.ButtonFuncType != FireButton || button.Skill != cfg.SkillId) return ErrorCodes.NpcParameter;
        return await FireCannonAsync(s, unit, cfg, req.CannonUseSkill);
    }

    private bool QuitsStatus(GameplayStatusButtonConfig button) =>
        button.Action is { } action && action.ToString().Contains(QuitStatusAction.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        && ActionConfig.Get(_data, QuitStatusAction) is { FunctionName: "QuitStatus" };

    /// <summary>Leaves the cannon: Idle to the operator (unless it is gone) and the cannon free for its builder again.</summary>
    private async Task LeaveCannonAsync(GameSession s, bool interrupt)
    {
        if (!_operators.Remove(s, out var unit)) return;
        if (unit.Operator == s) unit.Operator = null;
        if (s.Role != null && !s.SceneLoading) await CannonStateAsync(s, null, null, interrupt);
    }

    private Task CannonLeavingAsync(GameSession s) => LeaveCannonAsync(s, interrupt: true);

    private Task CannonDiedAsync(GameSession s) => LeaveCannonAsync(s, interrupt: true);

    /// <summary>The cannon is gone (replaced, or its siege ended): its operator is put back on foot.</summary>
    private async Task EjectCannonOperatorAsync(CannonUnit unit)
    {
        if (unit.Operator is { } s) await LeaveCannonAsync(s, interrupt: true);
    }
}
