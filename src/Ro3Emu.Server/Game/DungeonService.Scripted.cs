using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Kind = Ro3Emu.Server.Game.ReconstructedDungeons.TriggerKind;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Scripted story scenes without a fight (ReconstructedDungeons.Scripted, Reconstructed/scripted-scenes.json). A step
/// shows its scene actors, may start a story, and completes on one of its client events: a 32700 interaction on one
/// of its actors (or the dungeon message that interaction's server-hosted action 210 names), the participant's walked
/// position in a server region or near a point, or 84011 at the end of a story. Then the room moves on to the next
/// step, and the last one settles the room as a clear (so DungeonService.Completed counts quest target 101018).
/// Generic: nothing here knows a scene, task or NPC id.
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>DungeonVariableFunctionConfig 100020016: start function PlayCutSceneStory, array; [plot] plays it.</summary>
    public const int StoryVar = 100020016;
    /// <summary>
    /// Scene actor UIDs: their own namespace, so 32700 for them never reaches the world NPC lookup. It must stay below
    /// 2^53: LC_InteractManager hands unit ids to UT_SceneObjectManagerHelper through a double array (m_kShareMemory),
    /// so 2^56 + n came back from OverLapTest rounded to 2^56, missed m_kUnitDic and threw in _calAllUnitInDistance
    /// every frame; no interaction in range was ever offered (the bells of Louie's game).
    /// </summary>
    internal const long SceneActorUidBase = 1L << 47;
    /// <summary>The interaction reach (inferred): SceneService's 4 m plus its walk-report slack of 8 m.</summary>
    private const float SceneActorReach = 12f;
    private long _sceneActorSerial;
    private long _sceneActorBuffGuid = 1L << 56;

    /// <summary>
    /// A scene actor: NPCConfig _iNPCType 1 is created by the client as a monster unit (entity type 1, with its HP
    /// and level so the head bar renders), 2 as an NPC (entity type 2, which TriggerNpcInteract looks up by config id).
    /// Its buffs travel in the entity (AoIBuffInfo) and in 31104/31105 when a later step changes them.
    /// </summary>
    internal sealed class SceneActorUnit : WorldUnit
    {
        public required int NpcId { get; init; }
        public required long Point { get; init; }
        public required int EntityType { get; init; }
        public required long Camp { get; init; }
        public required string Name { get; init; }
        public MonsterTemplate? Template { get; init; }
        public long BornMs { get; init; }
        public List<BuffInfo> Buffs { get; } = new();

        public override EntityBase Entity()
        {
            var entity = new EntityBase
            {
                Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = EntityType, Name = Name, EntityCamp = Camp,
                // An NPC (type 2) is shaped exactly like the server's world NPCs (SceneService.NpcEntity: portals, Kafras,
                // whose 32700 the live client sends): no born time, so it never enters a born state. A monster-type actor
                // keeps it, as MonsterUnit does.
                Face = Face, State = EntityState.Alive, BornTimeMs = EntityType == 2 ? 0 : BornMs,
                NotifyType = EntityBase.Types.NotifyType.All,
                MovementInfo = new MovementInfo
                {
                    Pos = Pos, Face = Face, ServerTime = MonsterService.NowMs,
                    MoveType = MovementType.MoveTypeNone
                },
            };
            if (Template is { } t)
                foreach (var (idx, value) in new (long, long)[] { (101, t.MaxHp), (2000, t.MaxHp), (2002, t.Level), (113, t.SpeedCm),
                             (102, t.MaxSp), (2001, t.MaxSp) })
                    entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
            if (Buffs.Count > 0) entity.BuffData = new AoIBuffInfo { AddBuffs = { Buffs.Select(b => b.Clone()) } };
            return entity;
        }
    }

    private static ReconstructedDungeons.ScriptedStep? Step(Attempt a) =>
        a.Wave < a.Plan.Waves.Count ? a.Plan.Waves[a.Wave].Step : null;

    /// <summary>Called by SpawnWaveAsync for a scripted step instead of spawning monsters.</summary>
    private async Task BeginStepAsync(Attempt a, ReconstructedDungeons.ScriptedStep step)
    {
        a.StepDone = false;
        await SyncSceneActorsAsync(a, step.Actors);
        if (step.Story != 0 && !a.Settled && !a.Room.Closed)
            // Once, in a var-change event: the client calls PlayCutSceneStory on every push of this var.
            await BroadcastAsync(a, Event, new DungeonEventNotify
            {
                DungeonEntry = a.Plan.SceneEntry, EventType = (DungeonEventType)4, // DungeonEventDungeonVarChange
                StageNum = a.Wave + 1, LevelNum = 1, Vars = { new DungeonVar { VarId = StoryVar, Vi64 = { step.Story } } },
            });
    }

    /// <summary>
    /// Shows exactly the step's actors: one already shown at the same point with the same NPC stays (its buffs change in
    /// place), the others leave and the new ones enter.
    /// </summary>
    private async Task SyncSceneActorsAsync(Attempt a, IReadOnlyList<ReconstructedDungeons.SceneActor> wanted)
    {
        var shown = a.SceneActors.ToList();
        var kept = new List<(SceneActorUnit Unit, ReconstructedDungeons.SceneActor Spec)>();
        var added = new List<ReconstructedDungeons.SceneActor>();
        foreach (var spec in wanted)
        {
            var same = shown.FirstOrDefault(u => u.NpcId == spec.NpcId && u.Point == spec.Point);
            if (same == null) { added.Add(spec); continue; }
            shown.Remove(same);
            kept.Add((same, spec));
        }
        foreach (var gone in shown)
        {
            a.SceneActors.Remove(gone);
            await _instances.RemovePropAsync(a.Room, gone);
        }
        foreach (var (unit, spec) in kept) await SetActorBuffsAsync(unit, spec.Buffs);
        foreach (var spec in added)
        {
            if (a.Settled || a.Room.Closed) return;
            if (_catalog.ScenePoint(a.Plan.SceneEntry, spec.Point) is not { } at || _data.Row("NPCConfig", spec.NpcId) is not { } row)
                continue; // the catalog validated both; a changed export only loses the actor
            int type = (int)GameData.Int(row, "_iNPCType");
            var unit = new SceneActorUnit
            {
                Uid = SceneActorUidBase | Interlocked.Increment(ref _sceneActorSerial), SceneEntry = a.Plan.SceneEntry,
                InstanceId = a.Room.Id, X = at.X, Y = at.Y, Z = at.Z, Face = at.Face, NpcId = spec.NpcId, Point = spec.Point,
                EntityType = type, Camp = GameData.Int(row, "_iCamp"), Name = type == 2 ? _data.ClientTextOf(row, "_kName") : "",
                Template = type == 1 ? _monsters.Template(spec.NpcId) : null, BornMs = MonsterService.NowMs,
            };
            unit.Buffs.AddRange(spec.Buffs.Select(id => ActorBuff(unit, id)));
            if (await _instances.AddPropAsync(a.Room, unit)) a.SceneActors.Add(unit);
        }
    }

    private BuffInfo ActorBuff(SceneActorUnit unit, int configId) => new()
    {
        BuffGuid = Interlocked.Increment(ref _sceneActorBuffGuid), ConfigId = configId, ExpireTime = 0, Duration = 0,
        PileCount = 1, SourceId = unit.Uid,
    };

    /// <summary>A kept actor's buffs for the new step (31105/31104 and the AOI attribute change, as BuffService does).</summary>
    private async Task SetActorBuffsAsync(SceneActorUnit unit, IReadOnlyList<int> wanted)
    {
        var removed = unit.Buffs.Where(b => !wanted.Contains(b.ConfigId)).ToArray();
        var added = wanted.Where(id => unit.Buffs.All(b => b.ConfigId != id)).Select(id => ActorBuff(unit, id)).ToArray();
        foreach (var buff in removed) unit.Buffs.Remove(buff);
        unit.Buffs.AddRange(added);
        if (removed.Length > 0)
            await _world.ToUnitViewersAsync(unit, BuffService.BuffRemove,
                new BuffRemoveNotify { UnitId = unit.Uid, BuffGuids = { removed.Select(b => b.BuffGuid) } });
        if (added.Length > 0)
            await _world.ToUnitViewersAsync(unit, BuffService.BuffUpdate,
                new BuffUpdateNotify { UnitId = unit.Uid, Buffs = { added.Select(b => b.Clone()) } });
        if (removed.Length > 0 || added.Length > 0)
            await _world.ToUnitViewersAsync(unit, CombatService.AoiAttrChange, new AOIAttrChange
            {
                EntityUuid = (ulong)unit.Uid,
                BuffInfo = new AoIBuffInfo { AddBuffs = { added.Select(b => b.Clone()) }, RemoveBuffs = { removed.Select(b => b.BuffGuid) } },
            });
    }

    /// <summary>
    /// 32700 on a scene actor (its own UID namespace; false for any other unit). The request is always echoed, as the
    /// client runs the interaction's client-hosted actions from the reply (e.g. 53 opens a dialogue). An interaction
    /// counts only on a shown actor that offers it, near the player.
    /// </summary>
    public async Task<bool> TryInteractAsync(GameSession s, NpcInteractionReq req)
    {
        if (req.NpcUid < SceneActorUidBase || req.NpcUid >= SceneActorUidBase << 1) return false;
        var a = Active(s);
        var actor = a?.SceneActors.FirstOrDefault(u => u.Uid == req.NpcUid);
        int code = actor == null ? ErrorCodes.NpcMissing
            : req.SceneConfigId != 0 && req.SceneConfigId != s.SceneEntry ? ErrorCodes.NpcParameter
            : !_catalog.Interactions(actor.NpcId).Contains(req.InteractionId) ? ErrorCodes.NpcParameter
            : MonsterService.Distance(s.Position.X, s.Position.Z, actor.X, actor.Z) > SceneActorReach ? ErrorCodes.NpcTooFar
            : a!.Dracula is { } lair ? lair.Owner.InteractionError(a, s, actor.NpcId) : 0;
        _log.LogInformation("{Name} interacts {Interaction} with scene actor {Npc} {Uid} (scene {Scene}, config scene {ConfigScene}, "
            + "distance {Distance:0.0} m): {Result}", s.Role?.Name, req.InteractionId, actor?.NpcId, req.NpcUid, s.SceneEntry,
            req.SceneConfigId, actor == null ? -1 : MonsterService.Distance(s.Position.X, s.Position.Z, actor.X, actor.Z), code);
        await s.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = code, Req = req });
        if (code != 0) return true;
        // Dracula's Lair: a bush search may find the key; its finder is the rescuer, the only one the cage opens for.
        if (a!.Dracula is { } dracula) await dracula.Owner.InteractedAsync(a, s, actor!.NpcId);
        var events = new List<(Kind, long)> { (Kind.Interaction, req.InteractionId) };
        if (_catalog.DungeonMessage(req.InteractionId) is { } message) events.Add((Kind.Message, message));
        await StepEventAsync(a!, s, events);
        return true;
    }

    /// <summary>
    /// 84011 or 84012 from a participant: the story the step started (or any listed plot) has ended. The client sends
    /// both at a story's end (its plot's actions 601 and 150, LC_CutSceneManager._doEndAction), also when it is skipped;
    /// the first one completes the step, the second is ignored.
    /// </summary>
    public Task PlotEndedAsync(GameSession s, long plotId) =>
        Active(s) is { } a && Step(a) != null ? StepEventAsync(a, s, [(Kind.Plot, plotId)]) : Task.CompletedTask;

    /// <summary>
    /// GM test aid !actors [npcId]: the current step, what completes it, and its scene actors with their interactions,
    /// reach (NPCConfig _iInteractDistance) and buffs; with an NPC id, a jump 1 m beside that actor; "walk x z" arrives
    /// as a walk report would; "use" completes the step server-side (TEST AID). The client offers an interaction only
    /// within the reach, measured from the player's own position every 0.2 s (LC_InteractManager.Update), so a
    /// teleport is enough.
    /// </summary>
    private async Task ActorsGmAsync(GameSession s, string[] args)
    {
        if (args.Length == 1 && args[0].Equals("use", StringComparison.OrdinalIgnoreCase))
        {
            // TEST AID, not the original: the server takes the step's first completing event as if the client had
            // sent it (for an interaction, as a 32700 on its actor), so a scene can be finished while a client-side
            // problem is investigated. The usual step completion runs (teleport, next step, settlement).
            if (Active(s) is not { } used || Step(used) is not { } current || used.StepDone)
                throw new ArgumentException("not in a scripted scene step that is waiting");
            var t = current.Complete[0];
            _log.LogWarning("GM test aid: {Role} completes scripted scene {Scene} step {Step} with {Kind} {Id}",
                s.Role?.Id, used.Plan.SceneEntry, used.Wave + 1, t.Kind, t.Id);
            await StepEventAsync(used, s, [(t.Kind, t.Id)]);
            await GmCommands.ReplyAsync(s, $"TEST AID: step {used.Wave + 1} '{current.Stage}' completed with {t.Kind} {t.Id}.");
            return;
        }
        if (args.Length == 3 && args[0].Equals("walk", StringComparison.OrdinalIgnoreCase))
        {
            // A server teleport sends no walk report; regions and points are checked on 31082. This aid stands in for
            // the player's own last step: jump there, then run the same check a walk report runs.
            var to = new MapPoint(float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), s.Position.Y,
                float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), s.Face);
            await _scenes.TeleportInMapAsync(s, to);
            await PlayerMovedAsync(s);
            await GmCommands.ReplyAsync(s, FormattableString.Invariant($"Walked to {to.X:0.0},{to.Z:0.0} (test aid: the walk report's region check ran)."));
            return;
        }
        if (Active(s) is not { } a || Step(a) is not { } step)
        {
            await GmCommands.ReplyAsync(s, "Not in a scripted scene step.");
            return;
        }
        string Reach(SceneActorUnit u) => _data.Row("NPCConfig", u.NpcId) is { } row
            ? (GameData.Number(row.GetProperty("_iInteractDistance")) ?? 0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "?";
        if (args.Length == 0)
        {
            var actors = a.SceneActors.Select(u => FormattableString.Invariant(
                $"{u.NpcId} (entity type {u.EntityType}, uid {u.Uid}) at {u.X:0.0},{u.Y:0.0},{u.Z:0.0} reach {Reach(u)} m interactions [{string.Join(',', _catalog.Interactions(u.NpcId))}] buffs [{string.Join(',', u.Buffs.Select(b => b.ConfigId))}]"));
            await GmCommands.ReplyAsync(s, $"Step {a.Wave + 1}/{a.Plan.Waves.Count} '{step.Stage}' completes on "
                + string.Join(" or ", step.Complete.Select(t => $"{t.Kind} {t.Id}")) + ". Actors: "
                + (a.SceneActors.Count == 0 ? "none" : string.Join("; ", actors)));
            return;
        }
        if (!int.TryParse(args[0], out int npc) || a.SceneActors.FirstOrDefault(u => u.NpcId == npc) is not { } actor)
            throw new ArgumentException("<npcId> of a scene actor shown now (see !actors)");
        // One metre beside it (inside every reach used here, 2-5 m), not on its exact point: a zero-length
        // line-of-sight test (LC_InteractManager's CheckDirectPass) is not something to rely on. The jump goes in the
        // form the client applies to its own unit (ResetPos in the reverse dungeon 1000301, SceneService.MovesOnSurface).
        await _scenes.TeleportInMapAsync(s, new MapPoint(actor.X + 1, actor.Y, actor.Z, s.Face));
        await GmCommands.ReplyAsync(s, FormattableString.Invariant($"Beside {npc} at {actor.X + 1:0.0},{actor.Z:0.0}, 1 m from it (reach {Reach(actor)} m)."));
    }

    /// <summary>A participant's accepted walk: server regions and points of the current step.</summary>
    public async Task PlayerMovedAsync(GameSession s)
    {
        if (s.DungeonAttempt == null || Active(s) is not { } a || Step(a) is not { } step || a.StepDone) return;
        foreach (var t in step.Complete)
        {
            bool inside = t.Kind switch
            {
                Kind.Region => _catalog.SceneRegion(a.Plan.SceneEntry, t.Id) is { } region && region.Contains(s.Position.X, s.Position.Z),
                Kind.Point => _catalog.ScenePoint(a.Plan.SceneEntry, t.Id) is { } p
                    && MonsterService.Distance(s.Position.X, s.Position.Z, p.X, p.Z) <= t.Radius,
                _ => false,
            };
            if (inside) { await StepEventAsync(a, s, [(t.Kind, t.Id)]); return; }
        }
    }

    private async Task StepEventAsync(Attempt a, GameSession s, IReadOnlyList<(Kind Kind, long Id)> events)
    {
        if (a.Settled || a.Room.Closed || a.Spawning || a.StepDone || Step(a) is not { } step) return;
        bool Matches(ReconstructedDungeons.StepTrigger t) => events.Any(e => e.Kind == t.Kind && e.Id == t.Id);
        if (!step.Complete.Any(Matches))
        {
            if (step.Ignore.Any(Matches))
                _log.LogInformation("Scripted scene {Scene} step {Step}: {Role} sent {Events}, which does not advance it",
                    a.Plan.SceneEntry, a.Wave + 1, s.Role?.Id, string.Join(", ", events));
            return;
        }
        a.StepDone = true;
        _log.LogInformation("Scripted scene {Scene} step {Step} '{Stage}' completed by {Role} ({Events})",
            a.Plan.SceneEntry, a.Wave + 1, step.Stage, s.Role?.Id, string.Join(", ", events));
        if (step.TeleportAfter != 0 && _catalog.ScenePoint(a.Plan.SceneEntry, step.TeleportAfter) is { } to)
            foreach (var member in Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading))
                await _scenes.TeleportInMapAsync(member, to);
        // The next step or the settlement runs in its own owner scope, like the room's timers: the message that
        // completed the step (a walk, an interaction, a plot end) reserves only what its own route needs.
        _loop.PostLegacy(() => AdvanceAsync(a), "dungeon scripted step");
    }
}
