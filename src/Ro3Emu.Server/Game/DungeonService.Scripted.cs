using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Kind = Ro3Emu.Server.Game.ReconstructedDungeons.TriggerKind;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Scripted story scenes (ReconstructedDungeons.Scripted, Reconstructed/scripted-scenes.json). A step
/// shows its scene actors, may start a story, and completes on one of its client events: a 32700 interaction on one
/// of its actors (or the dungeon message that interaction's server-hosted action 210 names), the participant's walked
/// position in a server region or near a point, or 84011 at the end of a story. Then the room moves on to the next
/// step, and the last one settles the room as a clear (so DungeonService.Completed counts quest target 101018).
/// The event flow is generic; named scene-specific compatibility rules are scoped to their authored steps.
/// </summary>
public sealed partial class DungeonService
{
    private const long AirshipScene = 1000501;
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
        await ExitAllCannonsAsync(a, interrupt: true);
        a.StepSeen.Clear();
        a.StepOutroPending = false;
        // A fight-only step has no client event to wait for: its kills complete it.
        a.StepDone = step.Complete.Count == 0;
        await SyncSceneActorsAsync(a, step.Actors);
        await SyncCompanionsAsync(a, step.Companions);
        a.StepGeneration++;
        if (step.Seek is { } seek) StartSeek(a, seek);
        if (step.Shelter is { } shelter) StartShelter(a, shelter);
        if (step.React is { } react) StartReaction(a, react);
        if (step.Story != 0 && !a.Settled && !a.Room.Closed)
            await BroadcastStoryAsync(a, step.Story);
    }

    /// <summary>Once, in a var-change event: the client calls PlayCutSceneStory on every push of this var.</summary>
    private Task BroadcastStoryAsync(Attempt a, long story) =>
        BroadcastAsync(a, Event, new DungeonEventNotify
        {
            DungeonEntry = a.Plan.SceneEntry, EventType = (DungeonEventType)4, // DungeonEventDungeonVarChange
            StageNum = a.Wave + 1, LevelNum = 1, Vars = { new DungeonVar { VarId = StoryVar, Vi64 = { story } } },
        });

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
                InstanceId = a.Room.Id, X = at.X + spec.Dx, Y = at.Y, Z = at.Z + spec.Dz, Face = at.Face, NpcId = spec.NpcId, Point = spec.Point,
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
        var cannon = a == null ? null : Step(a)?.Cannon;
        bool entersCannon = actor != null && cannon != null && req.InteractionId == cannon.Interaction;
        int code = actor == null ? ErrorCodes.NpcMissing
            : req.SceneConfigId != 0 && req.SceneConfigId != s.SceneEntry ? ErrorCodes.NpcParameter
            : !_catalog.Interactions(actor.NpcId).Contains(req.InteractionId) ? ErrorCodes.NpcParameter
            : MonsterService.Distance(s.Position.X, s.Position.Z, actor.X, actor.Z) > SceneActorReach ? ErrorCodes.NpcTooFar
            : a!.Dracula is { } lair ? lair.Owner.InteractionError(a, s, actor.NpcId) : 0;
        if (code == 0 && entersCannon
            && (a!.CannonUsers.ContainsKey(s) || a.CannonOccupants.ContainsKey(actor!.Uid)))
            code = ErrorCodes.NpcParameter;
        _log.LogInformation("{Name} interacts {Interaction} with scene actor {Npc} {Uid} (scene {Scene}, config scene {ConfigScene}, "
            + "distance {Distance:0.0} m): {Result}", s.Role?.Name, req.InteractionId, actor?.NpcId, req.NpcUid, s.SceneEntry,
            req.SceneConfigId, actor == null ? -1 : MonsterService.Distance(s.Position.X, s.Position.Z, actor.X, actor.Z), code);
        await s.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = code, Req = req });
        if (code != 0) return true;
        // Dracula's Lair: a bush search may find the key; its finder is the rescuer, the only one the cage opens for.
        if (a!.Dracula is { } dracula) await dracula.Owner.InteractedAsync(a, s, actor!.NpcId);
        if (entersCannon)
        {
            a.CannonUsers[s] = actor!;
            a.CannonOccupants[actor!.Uid] = s;
            await NotifyCannonAsync(s, actor, cannon!, interrupt: false);
            return true;
        }
        await StrikeAsync(a!, s, actor!, req.InteractionId);
        var events = new List<(Kind, long)> { (Kind.Interaction, req.InteractionId) };
        if (_catalog.DungeonMessage(req.InteractionId) is { } message) events.Add((Kind.Message, message));
        await StepEventAsync(a!, s, events, actor);
        return true;
    }

    /// <summary>
    /// The G client grants 5353001 only while UpperCannon status 12 is active. This grant bypasses the player's job tree,
    /// but keeps CombatService's ordinary target, range, cooldown, cost, damage, hit and death processing.
    /// </summary>
    private bool CannonSkillGranted(GameSession s, CastSkillReq req)
    {
        if (Active(s) is not { } a || Step(a) is not { Cannon: { } cannon } step
            || !a.CannonUsers.TryGetValue(s, out var actor) || req.SkillId != cannon.FireSkill
            || req.OpUnitId != 0 && req.OpUnitId != s.Role?.Id && req.OpUnitId != actor.Uid)
            return false;
        if (req.TargetId == 0) return true; // TPS aiming may supply a position instead of a locked target.
        return a.Room.Monsters.Any(m => m.Uid == req.TargetId && !m.Dead && m.Hp > 0
            && step.Fight.Any(spawn => spawn.NpcId == m.Template.NpcId));
    }

    /// <summary>
    /// The beginner cannon is a 990 m TPS projectile aimed at aerial NPC-tag 4000 targets. Generic player combat's
    /// three-metre vertical and walk-nav gates do not model that projectile; only those two errors are waived, and only
    /// after the mounted cannon grant has already bound the request to a living authored enemy ship.
    /// </summary>
    private bool CannonTargetErrorAllowed(GameSession s, CastSkillReq req, int error) =>
        (error is ErrorCodes.SkillHeight or ErrorCodes.SkillBlocked) && CannonSkillGranted(s, req);

    private async Task NotifyCannonAsync(GameSession s, SceneActorUnit actor,
        ReconstructedDungeons.Cannon cannon, bool interrupt)
    {
        if (s.Role == null || s.SceneLoading) return;
        var message = new InteractChangeNtf
        {
            Uid = s.Role.Id, Interrupt = interrupt,
            DataInfo = new InteractInfo
            {
                EType = InteractInfo.Types.EState.UpperCannon, StateId = cannon.Status,
                InteractConfigId = (int)cannon.Interaction, ActiveObjId = actor.Uid,
                UpperCannonInfo = new UpperCannonState { CannonGuId = actor.Uid },
            },
        };
        foreach (var viewer in _world.Viewers(s).Prepend(s).Distinct())
            await viewer.SendGameAsync(WorldInteractionService.Change, message);
    }

    private async Task ExitCannonAsync(Attempt a, GameSession s, bool interrupt)
    {
        if (!a.CannonUsers.Remove(s, out var actor)) return;
        a.CannonOccupants.Remove(actor.Uid);
        if (s.Role == null || s.SceneLoading) return;
        var message = new InteractChangeNtf
        {
            Uid = s.Role.Id, Interrupt = interrupt,
            DataInfo = new InteractInfo { EType = InteractInfo.Types.EState.Idle },
        };
        foreach (var viewer in _world.Viewers(s).Prepend(s).Distinct())
            await viewer.SendGameAsync(WorldInteractionService.Change, message);
    }

    private async Task ExitAllCannonsAsync(Attempt a, bool interrupt)
    {
        foreach (var user in a.CannonUsers.Keys.ToArray()) await ExitCannonAsync(a, user, interrupt);
        a.CannonOccupants.Clear();
    }

    /// <summary>32703 while mounted: button 28 is the authored QuitStatus action. False delegates non-dungeon states.</summary>
    public async Task<bool> TryUseActionAsync(GameSession s, UseInteractActionReq req)
    {
        if (Active(s) is not { } a || !a.CannonUsers.ContainsKey(s)) return false;
        int code = ErrorCodes.NpcParameter;
        if (Step(a) is { Cannon: { } cannon } && req.ButtonId == cannon.ExitButton
            && Ro3Emu.Server.Data.Config.GameplayStatusButtonConfig.Get(_data, cannon.ExitButton) is { } button
            && button.ButtonFuncType == 0
            && button.Action?.ToString().Contains("5004", StringComparison.Ordinal) == true)
        {
            await ExitCannonAsync(a, s, interrupt: false);
            code = 0;
        }
        await s.SendGameAsync(WorldInteractionService.Action, new UseInteractActionRes { Result = code });
        return true;
    }

    /// <summary>
    /// A step's strike: the interaction (the airship's 'Board the Cannon') casts its skill from the player at the nearest
    /// living monster of the step's fight, through the ordinary extra-cast path (damage, kills, the tracker) (inferred:
    /// the original's cannon gameplay status is a server script the client does not ship). With shots, the k-th strike on
    /// a target first brings it down to its (shots - k) / shots share of the maximum HP, so that many strikes sink it at
    /// any player level, even when the far target resets between them (inferred: the rapid fire button stood for a volley).
    /// </summary>
    private async Task StrikeAsync(Attempt a, GameSession s, SceneActorUnit actor, long interaction)
    {
        if (Step(a) is not { Strikes: { } strikes } || a.Settled) return;
        foreach (var (id, skill, shots) in strikes)
        {
            if (id != interaction || _data.Row("SkillConfig", skill) is not { } row) continue;
            var target = a.Room.Monsters.Where(m => !m.Dead && m.Hp > 0)
                .OrderBy(m => MonsterService.Distance(actor.X, actor.Z, m.X, m.Z)).FirstOrDefault();
            if (target == null) continue;
            if (shots > 0)
            {
                int struck = a.Struck[target.Uid] = a.Struck.GetValueOrDefault(target.Uid) + 1;
                target.Hp = Math.Min(target.Hp, target.Template.MaxHp * Math.Max(0, shots - struck) / shots + 1);
            }
            await _combat.CastExtraAsync(s, row, [target]);
        }
    }

    /// <summary>
    /// 84011 or 84012 from a participant: only a plot explicitly listed by the step may complete it. The airship helm
    /// QTE is local: 130161 opens it; success/failure starts 130162/130163, whose action 601 sends the authoritative end.
    /// </summary>
    public Task PlotEndedAsync(GameSession s, long plotId) =>
        Active(s) is { } a && Step(a) != null ? StepEventAsync(a, s, [(Kind.Plot, plotId)]) : Task.CompletedTask;

    /// <summary>
    /// G 飛船結局以 action 601 的 84011 回報完成；action 150 的 84012 是另一種事件。
    /// 即使 84012 偽帶結局 story ID，也不能把船舵 QTE 提前當成已播完。
    /// 其他重建場景保留原有事件相容路徑。
    /// </summary>
    public Task PlotTriggeredAsync(GameSession s, long plotId) =>
        Active(s)?.Plan.SceneEntry == AirshipScene ? Task.CompletedTask : PlotEndedAsync(s, plotId);

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
                throw new ArgumentException("not in a scripted scene step that is waiting (a fight-only step ends on its kills)");
            var t = used.StepOutroPending ? current.Outro!.Complete[0] : current.Complete[0];
            _log.LogWarning("GM test aid: {Role} completes scripted scene {Scene} step {Step} with {Kind} {Id}",
                s.Role?.Id, used.Plan.SceneEntry, used.Wave + 1, t.Kind, t.Id);
            // An "all" step takes every one of its events.
            await StepEventAsync(used, s, current.All ? [.. current.Complete.Select(c => (c.Kind, c.Id))] : [(t.Kind, t.Id)]);
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
        if (step.Seek != null) return; // a hiding place counts when the seeker looks (DungeonService.HideAndSeek)
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

    /// <param name="used">The scene actor the player interacted with, which a reaction's effect is placed by.</param>
    private async Task StepEventAsync(Attempt a, GameSession s, IReadOnlyList<(Kind Kind, long Id)> events,
        SceneActorUnit? used = null)
    {
        if (a.Settled || a.Room.Closed || a.Spawning || a.StepDone || Step(a) is not { } step) return;
        bool Matches(ReconstructedDungeons.StepTrigger t) => events.Any(e => e.Kind == t.Kind && e.Id == t.Id);
        var expected = a.StepOutroPending ? step.Outro?.Complete ?? [] : step.Complete;
        if (!expected.Any(Matches))
        {
            if (a.StepOutroPending || !step.Ignore.Any(Matches)) return;
            _log.LogInformation("Scripted scene {Scene} step {Step}: {Role} sent {Events}, which does not advance it",
                a.Plan.SceneEntry, a.Wave + 1, s.Role?.Id, string.Join(", ", events));
            await RespondAsync(a, step.React, step.React?.Ignored, used);
            return;
        }
        if (step.All && !a.StepOutroPending)
        {
            foreach (var t in step.Complete.Where(Matches)) a.StepSeen.Add((t.Kind, t.Id));
            if (a.StepSeen.Count < step.Complete.Count)
            {
                // The tracker's counter follows each event of an "all" step.
                if (GoalParams(a) != null) await BroadcastAsync(a, BaseInfo, Snapshot(a));
                return;
            }
        }
        if (!a.StepOutroPending && step.Outro is { } outro)
        {
            a.StepOutroPending = true;
            a.StepSeen.Clear();
            _log.LogInformation("Scripted scene {Scene} step {Step} '{Stage}' starts outro story {Story} after {Events}",
                a.Plan.SceneEntry, a.Wave + 1, step.Stage, outro.Story, string.Join(", ", events));
            await BroadcastStoryAsync(a, outro.Story);
            return;
        }
        a.StepOutroPending = false;
        a.StepDone = true;
        _log.LogInformation("Scripted scene {Scene} step {Step} '{Stage}' completed by {Role} ({Events})",
            a.Plan.SceneEntry, a.Wave + 1, step.Stage, s.Role?.Id, string.Join(", ", events));
        int hold = await RespondAsync(a, step.React, step.React?.Complete, used);
        if (step.TeleportAfter != 0 && _catalog.ScenePoint(a.Plan.SceneEntry, step.TeleportAfter) is { } to)
            foreach (var member in Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading))
                await _scenes.TeleportInMapAsync(member, to);
        // The next step or the settlement runs in its own owner scope, like the room's timers: the message that
        // completed the step (a walk, an interaction, a plot end) reserves only what its own route needs. A completing
        // reaction's line is read first (inferred).
        if (hold > 0)
            _loop.ScheduleLegacy(Paced(hold), () => a.Room.Closed ? Task.CompletedTask : AdvanceAsync(a), "dungeon scripted step");
        else _loop.PostLegacy(() => AdvanceAsync(a), "dungeon scripted step");
    }
}
