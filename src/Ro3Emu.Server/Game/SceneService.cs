using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Map changes and what a map holds: 70010 sends the client to a map, which then reports 30005; the
/// arrival position goes in the ENTITYSELF that follows, together with every NPC, portal and Kafra of the map (the client
/// destroys all units on a map change). Also NPC interaction (32700: portals, Kafras, dialogs), 30012 / 30131 jumps and
/// the walk check against the client's nav grid.
/// </summary>
public sealed partial class SceneService(IOptions<ServerOptions> options, World world, WorldData worldData, MonsterService monsters, BuffService buffs, Data.GameData data, ILogger<SceneService> log, PublicLineManager lines, TimeProvider clock)
{
    public const uint NpcInteraction = 32700;
    public const uint SceneTeleport = 30012;
    public const uint CustomJumpMap = 30131;
    public const uint ClientNpcInteract = 31208;
    public const uint DialogueActionTrigger = 31207;
    public const uint ClientEnterArea = 31209;

    /// <summary>NPCInteractConfig ids: 8 = walk into a portal (EnterTeleport), 9 = Kafra teleport (PublicParamConfig.CapraTeleportMap).</summary>
    private const int InteractPortal = 8;
    private const int InteractKafraTeleport = 9;

    /// <summary>How far the player may stand from an NPC it talks to (NPCConfig._iInteractDistance is 2-4; client positions lag).</summary>
    private const float InteractSlack = 8f;

    /// <summary>Dungeon owners clean up on every exit and start combat only after the loaded-scene handshake.</summary>
    public List<Func<GameSession, Task>> Leaving { get; } = new();
    /// <summary>Reconcile destination-dependent appearance before ENTITYSELF and the first AOI snapshot.</summary>
    public List<Func<GameSession, Task>> BeforeSelf { get; } = new();
    public List<Func<GameSession, Task>> Loaded { get; } = new();
    /// <summary>Replays require the entity bases installed by JoinAsync.</summary>
    public List<Func<GameSession, Task>> AfterLoaded { get; } = new();
    private readonly HashSet<Func<GameSession, Task>> _ownedAfterLoaded = new();
    /// <summary>Shared keys a loaded hook writes for this session (a hook that writes beyond the role declares them
    /// here when it registers).</summary>
    public List<Func<GameSession, Ownership.ReservationKey[]>> LoadedKeys { get; } = new();

    /// <summary>C/D 15: the loaded hooks and unowned after-loaded hooks reserve the role's own keys plus what the hooks
    /// declare, not Realm. SceneHookScopeAuditTests guards that nothing else is needed.</summary>
    private Ownership.ReservationKey[] HookKeys(GameSession s) =>
        [.. s.Role is { } role ? new[] { Ownership.ReservationKey.Role(role.Id), Ownership.ReservationKey.Lifecycle(role.Id) } : [],
            .. LoadedKeys.SelectMany(keys => keys(s))];

    /// <summary>Declared keys follow session and shared state (escort membership, guild, season), which may change
    /// while the hooks wait: re-admit until the admitted reservation still covers them.</summary>
    private async Task<Ownership.LegacyOwnerScope> EnterHooksAsync(Ownership.OwnerRegistry owners, string origin, GameSession s)
    {
        while (true)
        {
            var scope = await owners.EnterAsync(origin, HookKeys(s));
            if (owners.Reservations.Covers(scope.Context.Operation, HookKeys(s))) return scope;
            scope.Dispose();
        }
    }
    /// <summary>Keep the original hook order while an opted-in hook admits its own participants.</summary>
    internal void AddOwnedAfterLoaded(Func<GameSession, Task> handler)
    { AfterLoaded.Add(handler); _ownedAfterLoaded.Add(handler); }
    public Func<GameSession, WorldNpc, NpcInteractionReq, int>? ValidateInteraction { get; set; }
    public Func<GameSession, WorldNpc, NpcInteractionReq, Task>? ApplyInteraction { get; set; }
    /// <summary>Optional source-bound work begins only when capacity is granted for the actual handoff.</summary>
    public sealed record TransferStart(Func<GameSession, bool> Begin, Action<GameSession> Cancel,
        Func<GameSession, bool>? Valid = null);
    public Func<GameSession, long, MapPoint?, int, TransferStart?, Task<bool>>? QueuePublicAdmission { get; set; }
    public Func<GameSession, long, int>? PreferredLine { get; set; }
    public List<Action<RuntimeMapKey>> PublicLineOpened { get; } = new();

    /// <summary>
    /// Sends the player to <paramref name="scene"/> at <paramref name="at"/> (default: the map's born point). Walks are
    /// dropped until the client reports the map loaded.
    /// </summary>
    public async Task<bool> EnterSceneAsync(GameSession s, long scene, MapPoint? at = null, int reason = 0,
        long instanceId = 0, TransferStart? transfer = null, int lineNumber = 0,
        SwitchSceneSourceType source = SwitchSceneSourceType.SwitchSceneSourceNone, bool createLine = false)
    {
        var role = s.Role;
        var lease = s.Attachment;
        long generation = s.SceneGeneration, sourceScene = s.SceneEntry, sourceInstance = s.InstanceId;
        await s.SceneTransitionGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, lease) || lease?.Cancelled == true
                || s.SceneGeneration != generation || s.SceneEntry != sourceScene || s.InstanceId != sourceInstance)
                return false;
            return await EnterSceneCoreAsync(s, scene, at, reason, instanceId, transfer, lineNumber, source, createLine);
        }
        finally { s.SceneTransitionGate.Release(); }
    }

    private async Task<bool> EnterSceneCoreAsync(GameSession s, long scene, MapPoint? at, int reason, long instanceId,
        TransferStart? transfer, int lineNumber, SwitchSceneSourceType source, bool createLine)
    {
        var role = s.Role;
        var lease = s.Attachment;
        var map = worldData.Map(scene);
        // Unknown scene: refuse, unless no client data was exported at all (then nothing is known and anything goes).
        if (map == null && data.Table("MapConfig").Count > 0) return false;
        // Dungeon admission belongs to 30400 / 32801; a generic teleport must not create a public dungeon.
        if (data.Row("DungeonConfig", scene) != null && instanceId == 0) return false;
        if (s.SceneRecoveryRequired || s.Handoff is { Completing: false }) return false;
        if (s.Handoff is { Completing: true } completing && completing.Destination.SceneEntry == scene)
        {
            // A private return recovery may be superseded after its ACK, but cannot turn into a different
            // runtime of the same config while publication is suspended (30005 has no line/instance nonce).
            if (createLine || instanceId != s.InstanceId || lineNumber > 0 && lineNumber != s.LineNumber) return false;
            if (instanceId == 0) lineNumber = s.LineNumber;
        }
        // A server-forced transition may supersede an acknowledged load still running private return hooks.
        // Client switches cannot do this; every continuation checks its generation below.
        if (s.Handoff?.Reservation is { } prior) lines.Release(s, prior);
        PublicLineManager.Reservation? reservation = null;
        if (instanceId == 0)
        {
            reservation = lines.TryReserve(s, scene, lineNumber, PreferredLine?.Invoke(s, scene) ?? 0, createLine);
            if (reservation == null)
                return lineNumber == 0 && !createLine && QueuePublicAdmission != null
                    && await QueuePublicAdmission(s, scene, at, reason, transfer);
        }
        var arrival = at ?? map?.Born ?? new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, s.Face);
        if (map != null) arrival = map.Grounded(arrival);
        try
        {
            if (transfer != null && !transfer.Begin(s))
            {
                if (reservation != null) lines.Release(s, reservation);
                return false;
            }
        }
        catch
        {
            if (reservation != null) lines.Release(s, reservation);
            transfer?.Cancel(s);
            throw;
        }
        var destination = reservation?.Map ?? RuntimeMapKey.Private(scene, instanceId);
        var handoff = new SceneHandoff(++s.SceneGeneration, destination, clock.GetUtcNow().AddSeconds(30), reservation)
        { AdmissionValid = transfer?.Valid };
        s.Handoff = handoff;
        bool CurrentTransfer() => s.Handoff == handoff && !s.SceneRecoveryRequired
            && ReferenceEquals(s.Role, role) && ReferenceEquals(s.Attachment, lease)
            && lease?.Cancelled != true && handoff.AdmissionValid?.Invoke(s) != false;
        try
        {
            foreach (var handler in Leaving) await handler(s);
            await world.LeaveAsync(s, () => s.Handoff == handoff && ReferenceEquals(s.Role, role)
                && ReferenceEquals(s.Attachment, lease) && lease?.Cancelled != true);
            if (!CurrentTransfer())
            {
                if (reservation != null) lines.Release(s, reservation);
                if (s.Handoff == handoff && !s.SceneRecoveryRequired) RequireReconnect(s, handoff);
                return false;
            }
            s.Attack = null;
            s.LockTarget = 0;
            s.SceneEntry = scene;
            s.InstanceId = instanceId;
            s.RuntimeKind = destination.Kind;
            s.LineNumber = destination.LineNumber;
            s.Position = new Vec3 { X = arrival.X, Y = arrival.Y, Z = arrival.Z };
            s.PreviousPosition = s.Position;
            s.Face = arrival.Face;
            s.SceneLoading = true;
            if (instanceId == 0)
            {
                foreach (var opened in PublicLineOpened) opened(destination);
                await monsters.EnsureSpawnedAsync(destination);
                if (!CurrentTransfer())
                {
                    if (s.Handoff == handoff && !s.SceneRecoveryRequired) RequireReconnect(s, handoff);
                    return false;
                }
            }
            log.LogInformation("{Name} -> scene {Scene} {Map} at {X:F1} {Y:F1} {Z:F1}", s.Role?.Name, scene, data.MapName(scene), arrival.X, arrival.Y, arrival.Z);
            await s.SendGameAsync(MsgId.SwitchSceneLine, new SwitchSceneLineResp
            {
                Result = 0,
                SceneEntry = scene,
                SceneID = destination.WorldScene,
                Linenum = destination.LineNumber,
                Reason = (SwitchSceneReasonType)reason,
                ESource = source,
            });
            if (!CurrentTransfer())
            {
                RequireReconnect(s, handoff);
                return false;
            }
            return true;
        }
        catch
        {
            try { transfer?.Cancel(s); }
            finally { RequireReconnect(s, handoff); }
            throw;
        }
    }

    /// <summary>A server-chosen position for a login: the saved one when it is walkable, else the map's born point.</summary>
    /// <summary>The map's scene-editor born point (m_kBornPoints), where a new character starts.</summary>
    public MapPoint? BornPoint(long scene) => worldData.Map(scene) is { Born: { } born } map ? map.Grounded(born) : null;

    public MapPoint LoginPoint(long scene, Vec3 saved, float face)
    {
        var map = worldData.Map(scene);
        var point = new MapPoint(saved.X, saved.Y, saved.Z, face);
        if (map == null) return point;
        var grounded = map.Grounded(point);
        return map.IsWalkable(grounded.X, grounded.Y, grounded.Z) ? grounded : map.Born is { } born ? map.Grounded(born) : grounded;
    }

    /// <summary>30005: reply, then the player at its arrival point, then everything that lives on the map.</summary>
    public async Task SceneLoadedAsync(GameSession s, SceneLoadedReq req)
    {
        log.LogInformation("{Account} loaded scene {Scene}", s.Account, req.SceneId);
        var role = s.Role;
        var lease = s.Attachment;
        var handoff = s.Handoff;
        bool CurrentScene() => ReferenceEquals(s.Role, role) && ReferenceEquals(s.Attachment, lease)
            && lease?.Cancelled != true && s.Handoff == handoff
            && handoff != null && s.SceneGeneration == handoff.Generation && s.RuntimeMap == handoff.Destination
            && !s.SceneRecoveryRequired && handoff.AdmissionValid?.Invoke(s) != false;
        bool valid = s.Role != null && s.SceneLoading && handoff is { Completing: false }
            && req.SceneId == s.SceneEntry && CurrentScene();
        // Claim before the first yield: two acknowledgments cannot run the publication twice.
        if (valid) handoff!.Completing = true;
        // The client's player cap for the AOI join below (0 / absent keeps the current one).
        if (valid && req.ViewMaxCount > 0) s.ViewLimit = (int)Math.Min(req.ViewMaxCount, (ulong)World.MaxViewLimit);
        await s.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedResp
        {
            Result = valid ? 0 : ErrorCodes.SceneMissing,
            SceneId = req.SceneId,
            ViewMaxCount = req.ViewMaxCount,
            NameBoardMaxCount = req.NameBoardMaxCount,
            LodHighCount = req.LodHighCount,
        });
        // A wrong-config ACK is a refusal, not evidence that the current load failed. Keep its exact claim
        // for the correct ACK or deadline; preserve the private-room wrong/repeated-packet negative path.
        if (!valid) return;
        if (!CurrentScene() || !s.SceneLoading) return;
        try
        {
            s.SceneLoading = false;
            foreach (var handler in BeforeSelf)
            {
                await handler(s);
                // An opted-in private-room return can wait for death publication. A newer handoff owns its own 30005/ENTITYSELF.
                if (!CurrentScene() || s.SceneLoading) return;
            }
            // Revive/death completion above must remain free to resolve or be superseded. Loaded hooks below include
            // persistent world/activity updates: they run under the role's keys plus the keys the hooks declare (HookKeys).
            using (var ownerScope = s.Owners is { } owners
                ? await EnterHooksAsync(owners, "scene loaded hooks", s) : null)
            {
                if (!CurrentScene() || s.SceneLoading) return;
                // Must follow the client's 30005: the ShowBattleUI listener lives in the freshly loaded scene logic.
                await s.SendGameAsync(MsgId.EntitySelf, new EntitySelf { Base = RoleSessionService.PlayerEntity(s) });
                if (!CurrentScene() || s.SceneLoading) return;
                await s.SendGameAsync(MsgId.SelfInfoEndFrameNotify, new SelfInfoEndFrameNotify());
                if (!CurrentScene() || s.SceneLoading) return;
                if (s.Buffs.Count > 0) await buffs.ResendAsync(s);
                if (!CurrentScene() || s.SceneLoading) return;
                bool reconnectAoi = s.ReconnectAoiPending;
                s.ReconnectAoiPending = false;
                if (worldData.Map(s.SceneEntry) is { Npcs.Count: > 0 } map)
                {
                    var view = new AoIViewChangeData();
                    foreach (var npc in map.Npcs) view.Bases.Add(NpcEntity(npc));
                    await s.SendGameAsync(GameAoiSink.SyncAoiView, view);
                    if (!CurrentScene() || s.SceneLoading) return;
                }
                else if (reconnectAoi)
                {
                    // The reconnect resync of the client completes only on a 31037/31044.
                    await s.SendGameAsync(GameAoiSink.SyncAoiView, new AoIViewChangeData());
                    if (!CurrentScene() || s.SceneLoading) return;
                }
                foreach (var handler in Loaded)
                {
                    await handler(s);
                    if (!CurrentScene() || s.SceneLoading) return;
                }
                await world.JoinAsync(s, () => CurrentScene() && !s.SceneLoading);
                if (!CurrentScene() || s.SceneLoading) return;
                if (handoff!.Reservation is { } loadedReservation) lines.Release(s, loadedReservation);
            }
            foreach (var handler in AfterLoaded)
            {
                using var hookScope = !_ownedAfterLoaded.Contains(handler) && s.Owners is { } hookOwners
                    ? await EnterHooksAsync(hookOwners, "scene after-loaded hook", s) : null;
                if (!CurrentScene() || s.SceneLoading) return;
                await handler(s);
                if (!CurrentScene() || s.SceneLoading) return;
            }
            s.Handoff = null;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Scene {Scene} loaded hooks failed for {Role}; the client must reconnect", s.SceneEntry, s.Role?.Id);
            RequireReconnect(s, handoff!);
            throw;
        }
    }

    /// <summary>Presentation-only replays after a silent reconnect (map-scoped views a service pushes in
    /// <see cref="Loaded"/>). Unlike Loaded/AfterLoaded they must not persist anything.</summary>
    public List<Func<GameSession, Task>> Resync { get; } = new();

    /// <summary>
    /// The scene part of the silent-reconnect resync, after 1011: the own actor, its buffs,
    /// the map's NPCs (an empty 31037 when there are none: the client's resync waits for one), then what the player
    /// already sees, replayed to this client only. The caller holds the role's keys.
    /// </summary>
    public async Task ResyncAsync(GameSession s, Func<bool> current)
    {
        await s.SendGameAsync(MsgId.EntitySelf, new EntitySelf { Base = RoleSessionService.PlayerEntity(s) });
        if (!current()) return;
        await s.SendGameAsync(MsgId.SelfInfoEndFrameNotify, new SelfInfoEndFrameNotify());
        if (!current()) return;
        if (s.Buffs.Count > 0) await buffs.ResendAsync(s);
        if (!current()) return;
        var view = new AoIViewChangeData();
        if (worldData.Map(s.SceneEntry) is { Npcs.Count: > 0 } map)
            foreach (var npc in map.Npcs) view.Bases.Add(NpcEntity(npc));
        await s.SendGameAsync(GameAoiSink.SyncAoiView, view);
        if (!current()) return;
        await world.ReplayViewAsync(s, current);
        foreach (var handler in Resync)
        {
            if (!current()) return;
            await handler(s);
        }
    }

    /// <summary>EntityBase of a server NPC: entity type 2, NPCConfig id as template, scene-editor position.</summary>
    public static EntityBase NpcEntity(WorldNpc npc) => new()
    {
        Uuid = (ulong)npc.Uid,
        EntityTemplateID = (ulong)npc.NpcId,
        EntityType = 2, // UT_Npc
        Name = npc.Name,
        EntityCamp = npc.Camp,
        Face = npc.Face,
        MovementInfo = new MovementInfo
        {
            Pos = new Vec3 { X = npc.X, Y = npc.Y, Z = npc.Z },
            Face = npc.Face,
            ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        },
        NotifyType = EntityBase.Types.NotifyType.All,
    };

    /// <summary>32700: always echo the request (the client indexes req.*), then act for portals and Kafra teleports.</summary>
    public async Task InteractAsync(GameSession s, NpcInteractionReq req)
    {
        if (await TryTreasureInteractAsync(s, req)) return;
        var npc = worldData.Npc(req.NpcUid);
        // (inferred from the codes' texts).
        int code = npc == null || npc.SceneEntry != s.SceneEntry ? ErrorCodes.NpcMissing
            : s.SceneLoading ? ErrorCodes.SwitchingScenes
            : req.SceneConfigId != 0 && req.SceneConfigId != s.SceneEntry ? ErrorCodes.NpcParameter
            : req.Pos != null && !WorldCoreService.Finite(req.Pos) ? ErrorCodes.NpcParameter
            : !Near(s, npc) ? ErrorCodes.NpcTooFar
            : data.Row("NPCConfig", npc.NpcId) is not { } npcRow
                || !HasInteraction(npcRow, req.InteractionId, npc.Kind) ? ErrorCodes.NpcParameter
            : req.InteractionId == InteractKafraTeleport && !worldData.IsKafraDestination(req.TargetSceneEntry) ? ErrorCodes.NpcParameter : 0;
        if (code == 0 && req.InteractionId is not (InteractPortal or InteractKafraTeleport))
            code = ValidateInteraction?.Invoke(s, npc!, req) ?? 0;
        bool ok = code == 0;
        log.LogInformation("{Name} interacts {Interaction} with {Npc} {NpcName} -> {Target}: {Ok}", s.Role?.Name, req.InteractionId,
            npc?.NpcId, npc?.Name, req.TargetSceneEntry, ok ? "ok" : "refused");
        await s.SendGameAsync(NpcInteraction, new NpcInteractionRsp { Result = code, Req = req });
        if (!ok) return;
        if (req.InteractionId is not (InteractPortal or InteractKafraTeleport) && ApplyInteraction != null)
            await ApplyInteraction(s, npc!, req);

        if (req.InteractionId == InteractPortal && npc!.Target is { } target)
        {
            var arrival = worldData.Map(target.Map)?.Portal(target.TravelPoint)?.Arrival;
            if (target.Map == s.SceneEntry && arrival is { } here) await TeleportInMapAsync(s, here);
            else await EnterSceneAsync(s, target.Map, arrival);
        }
        else if (req.InteractionId == InteractKafraTeleport)
        {
            var arrival = worldData.Map(req.TargetSceneEntry)?.KafraArrival;
            if (req.TargetSceneEntry == s.SceneEntry && arrival is { } here) await TeleportInMapAsync(s, here);
            else await EnterSceneAsync(s, req.TargetSceneEntry, arrival);
        }
    }

    private static bool Near(GameSession s, WorldNpc npc)
    {
        float dx = s.Position.X - npc.X, dz = s.Position.Z - npc.Z;
        return MathF.Sqrt(dx * dx + dz * dz) <= 4f + InteractSlack;
    }

    private static bool HasInteraction(System.Text.Json.JsonElement row, int id, WorldNpcKind kind) =>
        id == InteractKafraTeleport && kind == WorldNpcKind.Kafra
        || row.TryGetProperty("_iInteractID", out var ids) && ids.ValueKind == System.Text.Json.JsonValueKind.Array
            && ids.EnumerateArray().Any(x => Data.GameData.Number(x) == id);

    /// <summary>30012: travel to a map's born point (world-map travel, failed scene load).</summary>
    public async Task SceneTeleportAsync(GameSession s, SceneTeleportReq req)
    {
        if (s.Handoff is { Completing: false } pending)
        {
            await s.SendGameAsync(SceneTeleport, new SceneTeleportRsp { Result = ErrorCodes.SwitchingScenes });
            RequireReconnect(s, pending); // failed load has no nonce: do not arm an ambiguous same-map retry
            return;
        }
        bool ok = data.Row("MapConfig", req.TargetMapId) != null && worldData.Map(req.TargetMapId) != null;
        await s.SendGameAsync(SceneTeleport, new SceneTeleportRsp { Result = ok ? 0 : ErrorCodes.SceneMissing });
        if (ok) await EnterSceneAsync(s, req.TargetMapId);
    }

    /// <summary>30131: a dialog option jumping to a numbered point of a map (e.g. the palace guard).</summary>
    public async Task CustomJumpAsync(GameSession s, ClientCustomJumpMapReq req)
    {
        var map = data.Row("MapConfig", req.SceneId) != null ? worldData.Map(req.SceneId) : null;
        var point = map?.Points.GetValueOrDefault(req.PointId);
        await s.SendGameAsync(CustomJumpMap, new ClientCustomJumpMapResp { Result = point != null ? 0 : map == null ? ErrorCodes.SceneMissing : ErrorCodes.Parameter });
        if (point is { } p) await EnterSceneAsync(s, req.SceneId, p);
    }

    /// <summary>30016: destination resolved and authorized by the active task, without a 30131 acknowledgement.</summary>
    public async Task<bool> TaskTrackTeleportAsync(GameSession s, long sceneEntry, long pointId)
    {
        if (s.SceneLoading || s.Dead || data.Row("MapConfig", sceneEntry) == null) return false;
        var point = worldData.Map(sceneEntry)?.Points.GetValueOrDefault(pointId);
        if (point == null) return false;
        if (sceneEntry != s.SceneEntry) return await EnterSceneAsync(s, sceneEntry, point);
        await TeleportInMapAsync(s, point.Value);
        return true;
    }

    /// <summary>
    /// Whether the client moves every unit of this scene on surfaces: the novice "reverse" dungeon
    /// (PublicParamConfig.NoviceDungeonReverseDungeonID, 1000301 Ant Queen's Nest, walked on its ceiling). (proven)
    /// LC_Scene_Sync.lua:1142-1145 sets m_bReverseDungeon for that scene id, LC_UnitBase.lua:765-776 copies it to every
    /// unit's m_bReverseMode and LC_Movement_Base.lua:237-240 turns it into m_bMoveOnSurface, the local player's
    /// movement included (LC_Movement_AStarAgentNew -> LC_Movement_Smooth_Local -> LC_Movement_Smooth -> LC_Movement_Base).
    /// </summary>
    public bool MovesOnSurface(long sceneEntry) =>
        sceneEntry != 0 && data.Table("PublicParamConfig").TryGetValue("NoviceDungeonReverseDungeonID", out var id)
        && Data.GameData.Number(id) == sceneEntry;

    /// <summary>
    /// Same-map teleport of the local player (MOVEMENT_CHANGE), then the watchers are told. (proven) The client applies
    /// transType 2 ForceTeleport to its controlled unit only when that unit does not move on surfaces
    /// (LC_Scene_Sync.lua:3569-3603, _syncUnitMoveState); in a surface scene it ignores it and the player stays where
    /// it was, so there the teleport goes as transType 1 ResetPos, which it applies to its controlled unit in every
    /// scene (LC_Scene_Sync.lua:3613-3688 -> LC_Unit_Player.ResetPos, LC_Unit_Player.lua:1808-1830). Watchers apply
    /// either type to other units (LC_Scene_Sync.lua:3707-3724).
    /// </summary>
    public async Task TeleportInMapAsync(GameSession s, MapPoint at)
    {
        if (worldData.Map(s.SceneEntry) is { } map) at = map.Grounded(at);
        var pos = new Vec3 { X = at.X, Y = at.Y, Z = at.Z };
        await s.SendGameAsync(GameAoiSink.MovementChange, new MovementChangeNtf
        {
            Uid = s.Role!.Id,
            TransType = MovesOnSurface(s.SceneEntry) ? MovementTransitionType.MoveTransResetPos : MovementTransitionType.MoveTransForceTeleport,
            MovementInfo = new MovementInfo { Pos = pos, Face = at.Face, ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        });
        s.PreviousPosition = s.Position;
        s.Position = pos;
        s.Face = at.Face;
        s.LastWalkUtc = DateTime.UtcNow;
        s.Walking = false;
        await world.MoveAsync(s, new WalkReq { Pos = pos, Face = at.Face, BStop = true, SpeedRate = 1 }, forceTeleport: true);
    }

    /// <summary>
    /// Checks a client walk against the nav grid and the speed attribute. ServerOptions.WalkCheck:
    /// "off", "log" (default: report only) or "enforce" (snap the client back to the last good position).
    /// </summary>
    public async Task<bool> AcceptWalkAsync(GameSession s, WalkReq walk)
    {
        var mode = options.Value.WalkCheck;
        if (mode == "off" || worldData.Map(s.SceneEntry)?.Nav is not { } nav) return true;
        var now = DateTime.UtcNow;
        var from = s.Position;
        float dx = walk.Pos.X - from.X, dz = walk.Pos.Z - from.Z, dist = MathF.Sqrt(dx * dx + dz * dz);
        double seconds = Math.Clamp((now - s.LastWalkUtc).TotalSeconds, 0.05, 5);
        float maxDist = (float)(s.Speed / 100.0 * seconds * 1.5) + 1.5f;
        string? problem = !nav.IsWalkable(walk.Pos.X, walk.Pos.Y, walk.Pos.Z) ? "unwalkable cell"
            : dist > maxDist ? $"too fast ({dist:F1} m in {seconds:F2} s, max {maxDist:F1})"
            : !nav.CanWalk(from.X, from.Y, from.Z, walk.Pos.X, walk.Pos.Z) ? "path crosses unwalkable cells"
            : null;
        if (problem == null)
        {
            s.LastWalkUtc = now;
            return true;
        }
        // A client off the grid repeats on every walk: the first and every hundredth refusal per session are enough.
        if (++s.WalkCheckWarnings % 100 == 1)
            log.LogWarning("Walk check {Name} scene {Scene} {FromX:F1},{FromZ:F1} -> {X:F1},{Y:F1},{Z:F1}: {Problem} ({Count} so far)",
                s.Role?.Name, s.SceneEntry, from.X, from.Z, walk.Pos.X, walk.Pos.Y, walk.Pos.Z, problem, s.WalkCheckWarnings);
        if (mode != "enforce")
        {
            s.LastWalkUtc = now;
            return true;
        }
        await TeleportInMapAsync(s, new MapPoint(from.X, from.Y, from.Z, s.Face));
        return false;
    }
}
