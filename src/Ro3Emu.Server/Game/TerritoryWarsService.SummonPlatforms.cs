using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The stronghold battle's SummonPlatforms (docs/systems/territory-wars.md "Summon platforms"): gameplay points 49, 57 and
/// 83 (LC_TerritoryWars_GameplayPointHelper enum_GameplayPointType 9). A guild activates a ready platform by standing in
/// its ring together: the ring is a MutiInteractSubNpc whose "Activate" interaction puts the players into
/// GameplayStatusConfig 22 "领土战-多人交互-仅单阵营可交互" (territory war multi-person interaction, one side only; status
/// type 18, InteractInfo.EState.MultiPersonProgress) while the ring's custom data 10000/10001/10002 (InteractionProgress,
/// InteractionCurrentProgress, InteractionRate: "the total progress of the multi-person interaction", "its current
/// progress", "the speed of one interactor, points per second", DirtyDataConfig) fill the client's progress bar
/// (LC_PerformGfx_StrongholdProgress, UI_Sys_TerritoryWars_NewProgressWidget). A full bar raises the platform's vehicle
/// (NpcLevelPointRespawnConfig 4905008-4905010: the Mithril Madogear 4900402) for that guild, as a war vehicle of the
/// guild (TerritoryWarsService.WarVehicles.cs), and ChatConfig 4111018-4111020 announce it; when the vehicle is gone the
/// platform recharges and 4111013-4111015 announce it ready again.
/// </summary>
public sealed partial class TerritoryWarsService
{
    private const int SummonPlatformType = 9;
    /// <summary>NPCConfig MutiInteractSubNpc (_iNPCSubType 251, UnitConfig 35004 "跳舞位置圈", a ring to stand in):
    /// 4905011 with NPCInteractConfig 4905011 (NPC condition 490021, open to any guild) and 4905014 with 4905012 (NPC
    /// condition 490022: CheckSameAtkGuild 1, the ring's BelongGuild must be the player's guild, else UIWords 61612
    /// "Another faction is currently using this.").</summary>
    private const int OpenRingNpc = 4905011, GuildRingNpc = 4905014;
    /// <summary>The platform of scene group 82 "载具-防守方" (vehicle, defending side); 49 and 57 are groups 48/53
    /// "载具-交互组" (vehicle interaction group).</summary>
    private const int DefendersPlatform = 83;
    /// <summary>enum_CustomData InteractionProgress, InteractionCurrentProgress, InteractionRate (Lua_CustomDataDefine).</summary>
    private const long InteractionProgress = 10000, InteractionCurrentProgress = 10001, InteractionRate = 10002;
    /// <summary>GameplayStatusConfig _iStatusType 18: enum_InteractState TerritoryWarsInteract.</summary>
    private const long MultiPersonStatusType = 18;
    /// <summary>LC_ConditionDefine CheckSameAtkGuild; LC_ConditionList m_eType OR (enum_NodeType).</summary>
    private const long SameAttackGuild = 9012, ConditionOr = 2;
    /// <summary>(inferred) Seconds from the vehicle's fall until its platform is ready again: no config row names it.</summary>
    private const long PlatformRechargeSeconds = 120;

    /// <summary>ChatConfig (ready, activated) per platform: 4111013/4111018 "Southern Vehicle Point", 4111014/4111019
    /// "Eastern", 4111015/4111020 "Base". (inferred) South and east by the editor positions: 49 lies south (z 227) and 57
    /// east (x 726) of the map; 83 is the defenders' platform by the stronghold.</summary>
    private static readonly IReadOnlyDictionary<int, (int Ready, int Activated)> PlatformChats = new Dictionary<int, (int, int)>
    {
        [49] = (4111013, 4111018), [57] = (4111014, 4111019), [DefendersPlatform] = (4111015, 4111020),
    };

    private readonly Dictionary<GameSession, Activation> _activators = [];
    private Dictionary<int, int>? _platformVehicleNpcs;

    private sealed record Activation(Battle Battle, PlatformRing Ring, long Interaction, Vec3 Start);

    /// <summary>A platform's ring: the guild activating it (or, for the defenders' platform, the guild allowed to), and
    /// the progress its entity carries.</summary>
    internal sealed class PlatformRing : WorldUnit
    {
        public required int PointId { get; init; }
        public required int NpcId { get; init; }
        public MonsterTemplate? Template { get; init; }
        public long BornMs { get; init; }
        public long Guild { get; set; }
        public long Total { get; init; }
        public long Progress { get; set; }
        public long Rate { get; set; }
        public HashSet<GameSession> Activators { get; } = [];

        public IEnumerable<CustomData> Data()
        {
            yield return CannonLong(BelongGuild, Guild);
            yield return CannonLong(InteractionProgress, Total);
            yield return CannonLong(InteractionCurrentProgress, Progress);
            yield return CannonLong(InteractionRate, Rate);
        }

        public override EntityBase Entity()
        {
            var entity = new EntityBase
            {
                Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = 1, Face = Face, State = EntityState.Alive,
                BornTimeMs = BornMs, NotifyType = EntityBase.Types.NotifyType.All, EntityCamp = Template?.Camp ?? 1, GuildId = Guild,
                MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs, MoveType = MovementType.MoveTypeNone },
            };
            entity.CustomDatas.AddRange(Data());
            if (Template is { } t)
                foreach (var (idx, value) in new (long, long)[] { (101, t.MaxHp), (2000, t.MaxHp), (2002, t.Level) })
                    entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
            return entity;
        }
    }

    /// <summary>NpcLevelPointRespawnConfig rows of the stronghold scene that place a vehicle (NPCConfig _iNPCSubType 103):
    /// platform point -> vehicle NPC.</summary>
    private Dictionary<int, int> PlatformVehicleNpcs() => _platformVehicleNpcs ??= NpcLevelPointRespawnConfig.All(_data)
        .Where(r => r.Mapid == StrongholdScene && NPCConfig.Get(_data, (int)r.Npcid) is { NPCSubType: 103 })
        .GroupBy(r => (int)r.Respawnpointid).ToDictionary(g => g.Key, g => (int)g.First().Npcid);

    private IEnumerable<ScenePoint> Platforms(Battle b) => ScenePointsOf(b.State).Values
        .Where(p => p.Type == SummonPlatformType && PlatformVehicleNpcs().ContainsKey(p.Id)).OrderBy(p => p.Id);

    /// <summary>(inferred) The defenders' platform answers only to the guild holding the stronghold (the battle's flag,
    /// point 0); the others are open.</summary>
    private static long PlatformHolder(Battle b, int point) =>
        point == DefendersPlatform ? b.State.Points.GetValueOrDefault(0)?.Owner ?? 0 : 0;

    /// <summary>
    /// The one-second battle clock for the platforms: a ready platform has its ring and fills with its activators; an
    /// activated one recharges once its vehicle is gone; a recharged one is ready again.
    /// </summary>
    private async Task PlatformClockAsync(Battle b, long elapsed)
    {
        foreach (var point in Platforms(b).ToList())
        {
            switch ((EnumGlobalScenePoint)point.Status)
            {
                case EnumGlobalScenePoint.Ready:
                    if (await RingAsync(b, point.Id) is { } ring) await FillRingAsync(b, ring, elapsed);
                    break;
                case EnumGlobalScenePoint.Complete:
                    await RemoveRingAsync(b, point.Id);
                    if (b.PlatformVehicles.GetValueOrDefault(point.Id) is { Dead: false, Removed: false }) break;
                    b.PlatformVehicles.Remove(point.Id);
                    await SetScenePointAsync(b, point.Id, EnumGlobalScenePoint.UnReady, (_clock.Now + PlatformRechargeSeconds) * 1000, 0);
                    break;
                default:
                    await RemoveRingAsync(b, point.Id);
                    if (point.ReadyTime > 0 && _clock.Now * 1000 >= point.ReadyTime)
                    {
                        await SetScenePointAsync(b, point.Id, EnumGlobalScenePoint.Ready, 0, 0);
                        await PlatformChatAsync(b, PlatformChats.GetValueOrDefault(point.Id).Ready);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// The platform's ring, spawned when missing (inferred: no row places it). It stands at the other point of the
    /// platform's scene group (49/50, 57/58, 83/84 share a position): the open ring 4905011, or 4905014 belonging to the
    /// stronghold's holder on the defenders' platform.
    /// </summary>
    private async Task<PlatformRing?> RingAsync(Battle b, int pointId)
    {
        if (b.PlatformRings.TryGetValue(pointId, out var ring)) return ring;
        if (b.Room.Closed || _maps.Map(b.State.Scene) is not { } map || !map.Points.TryGetValue(pointId, out var at)) return null;
        var spot = map.DungeonGroups.Values.FirstOrDefault(g => g.Any(p => p.Id == pointId))?.FirstOrDefault(p => p.Id != pointId);
        int npc = pointId == DefendersPlatform ? GuildRingNpc : OpenRingNpc;
        ring = new PlatformRing
        {
            Uid = ScenePointUidBase | Interlocked.Increment(ref _scenePointSerial), SceneEntry = b.Room.SceneEntry,
            InstanceId = b.Room.Id, X = spot?.X ?? at.X, Y = spot?.Y ?? at.Y, Z = spot?.Z ?? at.Z, Face = at.Face,
            PointId = pointId, NpcId = npc, Template = _monsters.Template(npc), BornMs = MonsterService.NowMs,
            Guild = PlatformHolder(b, pointId), Total = Gvg("GVGFieldOccupyAmount", 30),
        };
        if (!await _instances.AddPropAsync(b.Room, ring)) return null;
        b.PlatformRings[pointId] = ring;
        return ring;
    }

    private async Task RemoveRingAsync(Battle b, int pointId)
    {
        if (!b.PlatformRings.Remove(pointId, out var ring)) return;
        foreach (var s in ring.Activators.ToList()) await StopActivatingAsync(s, interrupt: true);
        await _instances.RemovePropAsync(b.Room, ring);
    }

    /// <summary>
    /// One clock step of a ring: activators who died, left the battle or the ring drop out; the rest fill it at
    /// min(count x GVGFieldOccupySpeed, GVGFieldOccupyMaxSpeed) points a second up to GVGFieldOccupyAmount (inferred: the
    /// GVG field occupation numbers the territory flag uses, as no row names the platform's), and a full ring activates
    /// the platform. An empty ring starts over (inferred).
    /// </summary>
    private async Task FillRingAsync(Battle b, PlatformRing ring, long elapsed)
    {
        foreach (var s in ring.Activators.Where(s => !StillActivating(s, b, ring)).ToList()) await StopActivatingAsync(s, interrupt: true);
        long holder = PlatformHolder(b, ring.PointId);
        if (ring.NpcId == GuildRingNpc && ring.Guild != holder)
            foreach (var s in ring.Activators.ToList()) await StopActivatingAsync(s, interrupt: true);
        long rate = Math.Min(Gvg("GVGFieldOccupyMaxSpeed", 3), ring.Activators.Count * Gvg("GVGFieldOccupySpeed", 1));
        long progress = ring.Activators.Count == 0 ? 0 : Math.Min(ring.Total, ring.Progress + elapsed * rate);
        long guild = ring.NpcId == GuildRingNpc ? holder : ring.Activators.Count == 0 ? 0 : ring.Guild;
        if ((progress, rate, guild) == (ring.Progress, ring.Rate, ring.Guild) && progress < ring.Total) return;
        (ring.Progress, ring.Rate, ring.Guild) = (progress, rate, guild);
        await PublishRingAsync(ring);
        if (ring.Activators.Count > 0 && progress >= ring.Total) await ActivatePlatformAsync(b, ring);
    }

    private bool StillActivating(GameSession s, Battle b, PlatformRing ring) =>
        !s.Dead && ReferenceEquals(s.TerritoryBattle, b) && Active(b) && Member(s, b) && OwnId(s) == ring.Guild
        && MonsterService.Distance(ring, s) <= RingReach(ring);

    private float RingReach(PlatformRing ring) => (float)Math.Max(NPCConfig.Get(_data, ring.NpcId)?.InteractDistance ?? 3, 3) + 1;

    /// <summary>31018 the ring's BelongGuild and progress to everyone who sees it.</summary>
    private async Task PublishRingAsync(PlatformRing ring)
    {
        var change = new CustomDataChange { EntityId = ring.Uid, CustomDatas = { ring.Data() } };
        foreach (var viewer in ring.Viewers.ToList()) await Send(viewer, WorldCoreService.CustomChange, change);
    }

    /// <summary>
    /// The full ring: its activators are back on their feet, the ring leaves, the platform's vehicle stands at the
    /// platform point for the guild (camp 1 like every territory player, its guildId and BelongGuild: the client's seat
    /// conditions then pass for that guild's Vehicle operators only), the point is complete with the guild as its owner
    /// (30129) and ChatConfig 4111018-4111020 tell the battle.
    /// </summary>
    private async Task ActivatePlatformAsync(Battle b, PlatformRing ring)
    {
        long camp = ring.Activators.Select(CampOf).FirstOrDefault(1);
        long guild = ring.Guild;
        foreach (var s in ring.Activators.ToList()) await StopActivatingAsync(s, interrupt: false);
        await RemoveRingAsync(b, ring.PointId);
        if (_maps.Map(b.State.Scene) is not { } map || !map.Points.TryGetValue(ring.PointId, out var at)) return;
        int npc = PlatformVehicleNpcs()[ring.PointId];
        if (NewVehicle(npc, b.Room, map.Grounded(at), owner: 0, camp, guild) is not { } vehicle
            || !await _instances.AddPropAsync(b.Room, vehicle)) return;
        _vehicles[vehicle.Uid] = (vehicle, b.Room);
        b.PlatformVehicles[ring.PointId] = vehicle;
        await SetScenePointAsync(b, ring.PointId, EnumGlobalScenePoint.Complete, 0, guild);
        await PlatformChatAsync(b, PlatformChats.GetValueOrDefault(ring.PointId).Activated);
    }

    private static async Task PlatformChatAsync(Battle b, int chatId)
    {
        if (chatId == 0) return;
        var chat = new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = chatId };
        await RoleFanout.EachAsync(LoadedMembers(b), s => s.SendGameAsync(SystemBroadcast, chat));
    }

    /// <summary>32700 on a platform's ring; false for any other NPC.</summary>
    private async Task<bool> RingInteractAsync(GameSession s, NpcInteractionReq req)
    {
        if (s.TerritoryBattle is not { } b || b.PlatformRings.Values.FirstOrDefault(r => r.Uid == req.NpcUid) is not { } ring) return false;
        int code = CanActivate(s, b, ring, req);
        await Send(s, SceneService.NpcInteraction, new NpcInteractionRsp { Result = code, Req = req });
        if (code != 0) return true;
        ring.Guild = OwnId(s);
        ring.Activators.Add(s);
        _activators[s] = new Activation(b, ring, req.InteractionId, s.Position.Clone());
        await ActivationStateAsync(s, _activators[s], interrupt: false);
        await PublishRingAsync(ring);
        return true;
    }

    /// <summary>
    /// The ring's interaction (NPCConfig _iInteractID, GameplayStatusConfig of status type 18) by a member of the active
    /// battle within NPCConfig _iInteractDistance, on a ready platform, under both conditions: the interaction's
    /// (7000031: in no gameplay status) and the ring's (490021 open, 490022 CheckSameAtkGuild 1). One guild at a time, as
    /// status 22's name says (inferred: refused like a failed condition).
    /// </summary>
    private int CanActivate(GameSession s, Battle b, PlatformRing ring, NpcInteractionReq req)
    {
        if (s.Role == null || s.Dead || !Active(b) || !Member(s, b) || ring.Removed) return ErrorCodes.NpcMissing;
        if (NPCConfig.Get(_data, ring.NpcId) is not { } npc || !npc.InteractId.Contains(req.InteractionId)
            || NPCInteractConfig.Get(_data, req.InteractionId) is not { } interaction
            || GameplayStatusConfig.Get(_data, interaction.StatusId) is not { StatusType: MultiPersonStatusType })
            return ErrorCodes.NpcParameter;
        if (MonsterService.Distance(ring, s) > RingReach(ring)) return ErrorCodes.NpcTooFar;
        if (ScenePointsOf(b.State).GetValueOrDefault(ring.PointId) is not { Status: (int)EnumGlobalScenePoint.Ready }
            || ring.Guild != 0 && ring.Guild != OwnId(s)) return ErrorCodes.NpcParameter;
        return RingCondition(s, ring, interaction.Condition) && RingCondition(s, ring, interaction.NPCCondition) ? 0 : ErrorCodes.NpcParameter;
    }

    private bool RingCondition(GameSession s, PlatformRing ring, long conditionId)
    {
        if (conditionId == 0) return true;
        if (LCConditionList.Get(_data, conditionId) is not { } condition) return false;
        if (condition.SubConditionList is not { ValueKind: System.Text.Json.JsonValueKind.Array } list) return RingSubCondition(s, ring, condition);
        var subs = list.EnumerateArray().Select(sub => RingSubCondition(s, ring, new LCConditionList(sub)));
        return condition.Type == ConditionOr ? subs.Any(x => x) : subs.All(x => x);
    }

    /// <summary>LC_InteractCondition.CheckInteractState (12001; 0: in any gameplay status) and CheckSameAtkGuild (9012:
    /// the ring's custom data 104 BelongGuild is 0 for argument 0, the player's guild for 1). Any other type is closed.</summary>
    private bool RingSubCondition(GameSession s, PlatformRing ring, LCConditionList sub)
    {
        if (sub.Data is not [var type, var arg, ..]) return false;
        bool? yes = type switch
        {
            InStatus when arg == 0 => _activators.ContainsKey(s) || _riders.ContainsKey(s) || _operators.ContainsKey(s),
            SameAttackGuild when arg == 0 => ring.Guild == 0,
            SameAttackGuild when arg == 1 => ring.Guild == OwnId(s),
            _ => null,
        };
        return yes is { } value && value != sub.Inverse;
    }

    /// <summary>32702 MultiPersonProgress (status 22, the interaction, the ring as activeObjId) or Idle, to the player and
    /// everyone who sees them: LC_GamePlayStatusMgr.TerritoryWarsInteract and LC_State_Unit_Interact draw the link to the
    /// ring.</summary>
    private async Task ActivationStateAsync(GameSession s, Activation? activation, bool interrupt)
    {
        if (s.Role == null) return;
        var info = activation == null ? new InteractInfo { EType = InteractInfo.Types.EState.Idle } : new InteractInfo
        {
            EType = InteractInfo.Types.EState.MultiPersonProgress,
            StateId = NPCInteractConfig.Get(_data, activation.Interaction)?.StatusId ?? 0,
            InteractConfigId = (int)activation.Interaction, ActiveObjId = activation.Ring.Uid,
        };
        var message = new InteractChangeNtf { Uid = s.Role.Id, DataInfo = info, Interrupt = interrupt };
        await Send(s, WorldInteractionService.Change, message);
        foreach (var viewer in _world.Viewers(s)) await Send(viewer, WorldInteractionService.Change, message);
    }

    /// <summary>The player leaves the ring (moved, died, left the scene or the ring is gone): back to Idle, and the ring
    /// stops counting them.</summary>
    private async Task StopActivatingAsync(GameSession s, bool interrupt)
    {
        if (!_activators.Remove(s, out var activation)) return;
        activation.Ring.Activators.Remove(s);
        if (!s.SceneLoading) await ActivationStateAsync(s, null, interrupt);
    }

    /// <summary>(inferred) A walk that moves the activator ends the activation, as the gathering's does: GameplayStatus 22's
    /// exit events (2008, 2009, 2026, 2028) are server events the client does not name.</summary>
    public Task PlatformMovedAsync(GameSession s)
    {
        if (!_activators.TryGetValue(s, out var activation)) return Task.CompletedTask;
        float dx = s.Position.X - activation.Start.X, dz = s.Position.Z - activation.Start.Z;
        return dx * dx + dz * dz > 0.01f ? StopActivatingAsync(s, interrupt: true) : Task.CompletedTask;
    }

    private Task PlatformLeavingAsync(GameSession s) => StopActivatingAsync(s, interrupt: true);

    /// <summary>An activator is shown in the ring to a new viewer.</summary>
    private Task PlatformActivatorSeenAsync(GameSession viewer, GameSession subject) =>
        !_activators.TryGetValue(subject, out var activation) || subject.Role == null ? Task.CompletedTask
            : Send(viewer, WorldInteractionService.Change, new InteractChangeNtf
            {
                Uid = subject.Role.Id,
                DataInfo = new InteractInfo
                {
                    EType = InteractInfo.Types.EState.MultiPersonProgress,
                    StateId = NPCInteractConfig.Get(_data, activation.Interaction)?.StatusId ?? 0,
                    InteractConfigId = (int)activation.Interaction, ActiveObjId = activation.Ring.Uid,
                },
            });

    /// <summary>(inferred) The settlement ends the activations and takes the rings away; the platforms' vehicles leave with
    /// the other machines (EndWarMachinesAsync).</summary>
    private async Task EndPlatformsAsync(Battle b)
    {
        foreach (int point in b.PlatformRings.Keys.ToList()) await RemoveRingAsync(b, point);
        b.PlatformVehicles.Clear();
    }
}
