using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The siege's bag NPCs (docs/systems/territory-wars.md "Bag NPCs"): NPCConfig 4900201 (camp 1, the attackers) and 4900202
/// (camp 2, the defenders), _iNPCType 2 sub 1, interaction 4900004. NPCInteractConfig 4900004 opens the war bag
/// (_iEXECAction [1, TOPBAR, UI_Sys_Siege_BattleBagPanel], run by the client on the server's answer,
/// LC_InteractManager._onInteractResponse) when its condition 4900004 (LC_ConditionList: no buff of group 49012 or 49013)
/// and its NPC condition 4900003 (CheckCamp 2200, CheckGVGPermission 2202 with 2 = WartimeToolTypeVehicle) hold. A
/// Vehicle-permission member has no other way to its bag in the siege: the button widget shows the bag for Firearm only
/// (UI/Dungeon/Controllers/UI_Sys_Dungeon_Siege_ButtonWidgetCtrl.lua _checkShowInstrument).
/// </summary>
public sealed partial class TerritoryWarsService
{
    /// <summary>
    /// Bag NPC uids: below 2^53 like the other machines' (TerritoryWarsService.WarMachines.cs CannonUidBase). The client's
    /// LC_InteractManager hands an interactable unit's uuid to the native overlap test through a double array (AddRole:
    /// m_kShareMemory[2] = m_kUUID) and looks the returned id up in m_kUnitDic (_calAllUnitInDistance): an id a double
    /// cannot hold exactly comes back as another number, the lookup is nil and the client fails every frame.
    /// </summary>
    private const long SiegeBagUidBase = (1L << 49) | (1L << 46), SiegeBagUidMask = ~((1L << 46) - 1);
    private const long CheckHaveBuff = 1004;
    /// <summary>
    /// (inferred) No table places them (NpcLevelPointRespawnConfig rows 4900201/4900202 are row keys placing Obstacles):
    /// each stands 3 m in front of its camp's born point (DungeonLua_2230101: 75 the attackers', 73 the defenders'), facing
    /// it.
    /// </summary>
    private static readonly (int Npc, int Born)[] SiegeBagNpcs = [(4900201, 75), (4900202, 73)];
    private long _siegeBagSerial;

    /// <summary>
    /// A bag NPC: an NPC entity (type 2) of its camp, not a combat unit. NPCConfig gives it monster stats
    /// (_iAttributeVariated 220, _kAttriScale) and the client draws its HP bar, so (inferred) the entity carries its full
    /// template HP like a war machine's (properties 101, 2000, 2002), not an empty bar.
    /// </summary>
    internal sealed class SiegeBagUnit : WorldUnit
    {
        public required int NpcId { get; init; }
        public required long Camp { get; init; }
        public MonsterTemplate? Template { get; init; }

        public override EntityBase Entity()
        {
            var entity = new EntityBase
            {
                Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = 2, EntityCamp = Camp, Face = Face,
                State = EntityState.Alive, NotifyType = EntityBase.Types.NotifyType.All,
                MovementInfo = new MovementInfo
                {
                    Pos = new Vec3 { X = X, Y = Y, Z = Z }, Face = Face, ServerTime = MonsterService.NowMs,
                    MoveType = MovementType.MoveTypeNone,
                },
            };
            if (Template is { } t)
                foreach (var (idx, value) in new (long, long)[] { (101, t.MaxHp), (2000, t.MaxHp), (2002, t.Level) })
                    entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
            return entity;
        }
    }

    /// <summary>Both bag NPCs, once per room.</summary>
    private async Task SiegeSpawnBagsAsync(SiegeRun battle)
    {
        if (battle.Room.Closed || battle.Bags.Count > 0 || _maps.Map(SiegeScene) is not { } map) return;
        foreach (var (npcId, born) in SiegeBagNpcs)
        {
            if (!map.Points.TryGetValue(born, out var at) || NPCConfig.Get(_data, npcId) is not { } npc) continue;
            float rad = at.Face * MathF.PI / 180f;
            var spot = map.Grounded(new MapPoint(at.X + 3 * MathF.Sin(rad), at.Y, at.Z + 3 * MathF.Cos(rad), at.Face + 180));
            var unit = new SiegeBagUnit
            {
                Uid = SiegeBagUidBase | Interlocked.Increment(ref _siegeBagSerial), SceneEntry = battle.Room.SceneEntry,
                InstanceId = battle.Room.Id, X = spot.X, Y = spot.Y, Z = spot.Z, Face = spot.Face, NpcId = npcId, Camp = npc.Camp,
                Template = _monsters.MachineTemplate(npcId),
            };
            if (!await _instances.AddPropAsync(battle.Room, unit)) return;
            battle.Bags[unit.Uid] = unit;
        }
    }

    /// <summary>32700 on a bag NPC; anything else goes on to its usual owner.</summary>
    public async Task<bool> SiegeBagSharedAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (id != SceneService.NpcInteraction) return false;
        var req = NpcInteractionReq.Parser.ParseFrom(body.Span);
        if ((req.NpcUid & SiegeBagUidMask) != SiegeBagUidBase) return false;
        int code = s.SiegeBattle is { } battle && battle.Bags.TryGetValue(req.NpcUid, out var bag)
            ? SiegeBagInteraction(s, battle, bag, req) : ErrorCodes.NpcMissing;
        await Send(s, id, new NpcInteractionRsp { Result = code, Req = req });
        return true;
    }

    /// <summary>The NPC's interaction, within NPCConfig _iInteractDistance, and both conditions of NPCInteractConfig.</summary>
    private int SiegeBagInteraction(GameSession s, SiegeRun battle, SiegeBagUnit bag, NpcInteractionReq req)
    {
        if (s.Role == null || s.Dead || s.SceneLoading || bag.Removed || battle.Room.Closed || s.InstanceId != battle.Room.Id
            || !SiegeActive(battle) || !SiegeMember(s, battle)) return ErrorCodes.NpcMissing;
        if (NPCConfig.Get(_data, bag.NpcId) is not { } npc || !npc.InteractId.Contains(req.InteractionId)
            || NPCInteractConfig.Get(_data, req.InteractionId) is not { } interaction) return ErrorCodes.NpcParameter;
        if (MonsterService.Distance(s.Position.X, s.Position.Z, bag.X, bag.Z) > Math.Max(npc.InteractDistance, 3) + 1)
            return ErrorCodes.NpcTooFar;
        return SiegeBagCondition(s, bag, interaction.Condition) && SiegeBagCondition(s, bag, interaction.NPCCondition)
            ? 0 : ErrorCodes.NpcParameter;
    }

    private bool SiegeBagCondition(GameSession s, SiegeBagUnit bag, long conditionId)
    {
        if (conditionId == 0) return true;
        if (LCConditionList.Get(_data, conditionId) is not { } condition) return false;
        if (condition.SubConditionList is not { ValueKind: System.Text.Json.JsonValueKind.Array } list)
            return SiegeBagSubCondition(s, bag, condition);
        return list.EnumerateArray().All(sub => SiegeBagSubCondition(s, bag, new LCConditionList(sub)));
    }

    /// <summary>
    /// LC_CommonCondition.CheckCamp (2200: the same EntityCamp, its argument unused), CheckHaveBuff (1004: a buff of that
    /// BuffConfig group, LC_Buff_System.GetBuffByConfigID) and LC_InteractCondition.CheckGVGPermission (2202:
    /// SiegePermissionSelfData eToolType). Any other type is closed.
    /// </summary>
    private bool SiegeBagSubCondition(GameSession s, SiegeBagUnit bag, LCConditionList sub)
    {
        if (sub.Data is not [var type, var arg, ..]) return false;
        bool? yes = type switch
        {
            CheckCamp => bag.Camp == CampOf(s),
            CheckHaveBuff => _buffs.HasBuffPiles(s, (int)arg, 1),
            GvgPermission => (long)_guilds.ToolOfRole(s) == arg,
            _ => null,
        };
        return yes is { } value && value != sub.Inverse;
    }
}
