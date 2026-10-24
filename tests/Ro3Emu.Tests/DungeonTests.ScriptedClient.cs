using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The scripted scenes seen from the client: every interaction a step waits on is checked against the wire data the
/// client actually received (31037 entities, 31104/31105 buff syncs) with the client's own rules for offering it
/// (LC_InteractManager: a unit registers for interaction when NPCConfig _iInteractID[1] is set, LC_Unit_NPC/Monster
/// StartLogic; every 0.2 s it is in range when the player is within its _iInteractDistance, and of the units in range
/// the one with the highest _iInteractRank, then the nearest, is possessed; an interaction is offered when its
/// _iNPCCondition holds, here 1004 CheckHaveBuff on the unit's buff groups, keyed by BuffConfig _iBuffID).
/// The range is measured from where the client holds the player, not from the server's copy: in the reverse dungeon
/// (PublicParamConfig.NoviceDungeonReverseDungeonID, 1000301) every unit moves on surfaces and the client ignores a
/// MOVEMENT_CHANGE ForceTeleport of its own unit (LC_Scene_Sync.lua:3569-3603); it applies ResetPos there.
/// </summary>
public sealed partial class DungeonTests
{
    [ClientDataTheory]
    [MemberData(nameof(ScriptedScenes))]
    public async Task EveryInteractionAStepWaitsOnIsOfferedByTheClientsOwnRules(long scene)
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99"); // a scene with a fight
        if (scene == 1022001) await Gm(alice, "!quest accept 10102200"); // Payon Forest's condition [1101, 10102200]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        var plan = Session(alice).DungeonAttempt!.Plan;
        var catalog = Service<DungeonCatalog>();
        var map = Service<WorldData>().Map(scene)!;
        for (int wave = 0; wave < plan.Waves.Count; wave++)
        {
            var step = plan.Waves[wave].Step!;
            await Stage(alice, wave + 1, quest: step.Quest);
            await ActorsWhen(alice, shown => shown.Count == step.Actors.Count && step.Actors.All(spec => shown.Any(u =>
                u.NpcId == spec.NpcId && u.Point == spec.Point && u.Buffs.Select(b => b.ConfigId).Order().SequenceEqual(spec.Buffs.Order()))));
            // A step's fight first: its monsters, killed.
            if (plan.Waves[wave].Spawns.Count > 0)
                foreach (var monster in await WaveIn(instance, plan.Waves[wave].Spawns.Count)) await Kill(alice, monster);
            // A hide-and-seek step is won by hiding round after round (its own test); the aid settles it here.
            if (step.Seek != null)
            {
                await Gm(alice, "!actors use");
                continue;
            }
            // An "all" step takes each of its events; another one, its first.
            foreach (var t in step.All ? step.Complete : step.Complete.Take(1))
                switch (t.Kind)
                {
                    case ReconstructedDungeons.TriggerKind.Interaction or ReconstructedDungeons.TriggerKind.Message:
                        {
                            var (actor, interaction) = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
                                Session(alice).DungeonAttempt!.SceneActors.SelectMany(u => catalog.Interactions(u.NpcId)
                                    .Where(i => t.Kind == ReconstructedDungeons.TriggerKind.Interaction ? i == t.Id : catalog.DungeonMessage(i) == t.Id)
                                    .Select(i => (u, i))).First()));
                            var npc = Service<GameData>().Row("NPCConfig", actor.NpcId)!.Value;
                            var view = await ClientSees(alice, actor.Uid, step.Actors.First(a => a.NpcId == actor.NpcId && a.Point == actor.Point).Buffs);
                            // The entity the client creates: its unit class, template and place.
                            Assert.Equal((int)GameData.Int(npc, "_iNPCType"), view.Entity.EntityType);
                            Assert.Equal((ulong)actor.NpcId, view.Entity.EntityTemplateID);
                            // An NPC is shaped like the world NPCs the live client interacts with (portals, Kafras): no born time.
                            if (view.Entity.EntityType == 2) Assert.Equal(0, view.Entity.BornTimeMs);
                            var point = map.Points[actor.Point];
                            Assert.Equal(point.X, view.Entity.MovementInfo.Pos.X, 0.01f);
                            Assert.Equal(point.Z, view.Entity.MovementInfo.Pos.Z, 0.01f);
                            // It registers for interaction and offers the step's interaction (directly, or from its dialogue).
                            Assert.NotEqual(0, catalog.Interactions(actor.NpcId).FirstOrDefault());
                            int offered = Reachable(actor.NpcId, interaction);
                            Assert.True(ConditionHolds(offered, view.BuffGroups),
                                $"interaction {offered} of {actor.NpcId}: NPC condition fails with buff groups [{string.Join(',', view.BuffGroups)}]");
                            // The GM aid puts the player beside the actor; the client's next 0.2 s check finds it in range and
                            // possesses it (no other interactive unit is preferred). Where the player is, is the client's own
                            // view: the teleport counts only if the client applies it to its controlled unit.
                            await Gm(alice, $"!actors {actor.NpcId}");
                            long me = Session(alice).Role!.Id;
                            bool onSurface = MovesOnSurface(scene);
                            var at = ClientPlayerAt(alice, me, onSurface);
                            for (int i = 0; i < 100 && (at == null || MonsterService.Distance(at.X, at.Z, actor.X, actor.Z) > 1.5f); i++)
                            {
                                await Task.Delay(20);
                                at = ClientPlayerAt(alice, me, onSurface);
                            }
                            Assert.NotNull(at);
                            Assert.Equal(actor.Uid, Possessed(alice, at.X, at.Y, at.Z));
                            Assert.Equal(0, await Interact(alice, actor, (int)interaction));
                            break;
                        }
                    case ReconstructedDungeons.TriggerKind.Plot:
                        await Story(alice, t.Id);
                        // 84012 (the plot's action 150 PlotActingTrigger) completes it as well as 84011 does.
                        await alice.SendGameAsync(TaskService.ExitCutSceneState, new ExitCutSceneStateReq());
                        await alice.SendGameAsync(DungeonService.PlotSceneEvent, new PlotActingTriggerReq { PlotId = t.Id });
                        break;
                    default:
                        // The GM aid's arrival runs the walk report's check (the scene tests above walk for real).
                        var p = t.Kind == ReconstructedDungeons.TriggerKind.Region
                            ? new MapPoint(map.Regions[t.Id].X, 0, map.Regions[t.Id].Z, 0) : map.Points[t.Id];
                        await Gm(alice, FormattableString.Invariant($"!actors walk {p.X} {p.Z}"));
                        break;
                }
        }
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
    }

    [Fact]
    public void SceneActorUidsSurviveTheClientsDoubleArray()
    {
        // LC_InteractManager passes unit ids to the scene object manager as doubles: every actor uid must stay exact.
        const long limit = 1L << 53;
        Assert.True((DungeonService.SceneActorUidBase << 1) <= limit);
        Assert.NotEqual((double)DungeonService.SceneActorUidBase, (double)(DungeonService.SceneActorUidBase | 1));
        // The other dynamic NPC namespaces the client can stand next to (guild campfire seeds: prefix | 24-bit room | 12-bit
        // point; treasure hunt NPCs: prefix | role id; the Rift Crisis stones below 2^47).
        Assert.True((GuildCampfireService.UidPrefix << 1) <= limit);
        Assert.True((SceneService.TreasureUidPrefix << 1) <= limit);
        Assert.True(TimedBattleService.IsRiftUid((1L << 47) - 1) && !TimedBattleService.IsRiftUid(1L << 47) && (1L << 47) < limit);
    }

    [ClientDataTheory]
    [MemberData(nameof(ScriptedScenes))]
    public async Task TheUseTestAidFinishesAScriptedSceneServerSide(long scene)
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99"); // a scene with a fight
        await Gm(alice, "!actors use"); // outside a scene: refused, nothing happens
        // Payon Forest's DungeonConfig condition 1000000 is [1101, 10102200]: its story task is running.
        if (scene == 1022001) await Gm(alice, "!quest accept 10102200");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        var plan = Session(alice).DungeonAttempt!.Plan;
        for (int wave = 0; wave < plan.Waves.Count; wave++)
        {
            var step = plan.Waves[wave].Step!;
            await Stage(alice, wave + 1, quest: step.Quest);
            await ActorsIn(alice, step.Actors.Count);
            // A step's fight: its monsters, then its event if it has one.
            if (plan.Waves[wave].Spawns.Count > 0)
                foreach (var monster in await WaveIn(instance, plan.Waves[wave].Spawns.Count)) await Kill(alice, monster);
            if (step.Complete.Count > 0) await Gm(alice, "!actors use");
        }
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
    }

    private sealed record ClientUnit(EntityBase Entity, IReadOnlySet<long> BuffGroups);

    /// <summary>
    /// The unit as the client holds it: its last 31037 base, then the 31104/31105 buff syncs after it (the client's
    /// buff component keys buffs by group, BuffConfig _iBuffID). Waits until its buffs are the step's.
    /// </summary>
    private async Task<ClientUnit> ClientSees(BotClient bot, long uid, IReadOnlyList<int> buffs)
    {
        ClientUnit? seen = null;
        for (int i = 0; i < 150; i++)
        {
            EntityBase? entity = null;
            var held = new Dictionary<long, int>();
            foreach (var e in bot.Inbox.Snapshot())
            {
                if (e.IsGame(GameAoiSink.SyncAoiView))
                {
                    var view = AoIViewChangeData.Parser.ParseFrom(e.Body);
                    if (view.LeaveAoi.Contains((ulong)uid)) { entity = null; held.Clear(); }
                    if (view.Bases.LastOrDefault(b => b.Uuid == (ulong)uid) is { } b)
                    {
                        entity = b;
                        held.Clear();
                        foreach (var buff in b.BuffData?.AddBuffs ?? []) held[buff.BuffGuid] = buff.ConfigId;
                    }
                }
                else if (e.IsGame(BuffService.BuffUpdate) && BuffUpdateNotify.Parser.ParseFrom(e.Body) is { } add && add.UnitId == uid)
                    foreach (var buff in add.Buffs) held[buff.BuffGuid] = buff.ConfigId;
                else if (e.IsGame(BuffService.BuffRemove) && BuffRemoveNotify.Parser.ParseFrom(e.Body) is { } gone && gone.UnitId == uid)
                    foreach (long guid in gone.BuffGuids) held.Remove(guid);
            }
            if (entity != null)
            {
                seen = new ClientUnit(entity, held.Values.Select(id => GameData.Int(Service<GameData>().Row("BuffConfig", id)!.Value, "_iBuffID")).ToHashSet());
                if (held.Values.Order().SequenceEqual(buffs.Order())) return seen;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"The client never held unit {uid} with buffs [{string.Join(',', buffs)}]"
            + (seen == null ? "" : $"; it holds groups [{string.Join(',', seen.BuffGroups)}]"));
    }

    /// <summary>
    /// The interaction the client offers on the unit for <paramref name="interaction"/>: itself unless it is an
    /// ActionTrigger (mode 4), which only a dialogue option's action 211 TriggerNpcInteract runs; then the unit's
    /// interaction whose dialogue (action 53) holds that option.
    /// </summary>
    private int Reachable(int npcId, long interaction)
    {
        var data = Service<GameData>();
        static int[] Modes(JsonElement row) => row.GetProperty("_iInteractionMode").EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).ToArray();
        static IEnumerable<string[]> Actions(JsonElement row, string field) => row.TryGetProperty(field, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Array).Select(x => x.EnumerateArray().Select(v => v.ToString()).ToArray()) : [];
        var row = data.Row("NPCInteractConfig", interaction)!.Value;
        if (!Modes(row).Contains(4)) return (int)interaction;
        foreach (var other in Service<DungeonCatalog>().Interactions(npcId).Where(i => i != interaction))
        {
            var o = data.Row("NPCInteractConfig", other)!.Value;
            // The dialogue the interaction opens (action 53) and the ones its options lead to (action 7).
            var open = new Queue<long>(Actions(o, "_iEXECAction").Where(a => a is ["53", _, ..]).Select(a => long.Parse(a[1])));
            var seen = new HashSet<long>();
            while (open.TryDequeue(out long dialogue))
            {
                if (!seen.Add(dialogue) || data.Row("DialogueConfig", dialogue) is not { } lines) continue;
                var actions = lines.EnumerateArray().SelectMany(line => Actions(line, "_iEXECAction")).ToArray();
                if (actions.Any(a => a is ["211", var n, var i, ..] && n == npcId.ToString() && i == interaction.ToString())) return (int)other;
                foreach (var next in actions.Where(a => a is ["7", _, ..])) open.Enqueue(long.Parse(next[1], System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        throw new Xunit.Sdk.XunitException($"ActionTrigger {interaction} of {npcId}: no dialogue of the unit runs it");
    }

    /// <summary>The interaction's _iNPCCondition as the client evaluates it here: its 1004 CheckHaveBuff parts.</summary>
    private bool ConditionHolds(int interaction, IReadOnlySet<long> groups)
    {
        var data = Service<GameData>();
        long id = GameData.Int(data.Row("NPCInteractConfig", interaction)!.Value, "_iNPCCondition");
        if (id == 0) return true;
        var condition = data.Row("LC_ConditionList", id)!.Value;
        var parts = condition.TryGetProperty("m_kSubConditionList", out var list) ? list.EnumerateArray().ToArray() : [condition];
        return parts.All(p =>
        {
            var d = p.GetProperty("m_kData");
            if (GameData.Number(d[0]) != 1004) return true; // vehicle seat checks (12002/12003) are the client's own state
            bool has = groups.Contains((long)GameData.Number(d[1])!);
            return p.TryGetProperty("m_bInverse", out var inv) && inv.ValueKind == JsonValueKind.True ? !has : has;
        });
    }

    /// <summary>
    /// Whether the client moves every unit of the scene on surfaces, from the client data alone: the scene id equals
    /// PublicParamConfig.NoviceDungeonReverseDungeonID (LC_Scene_Sync.lua:1142-1145 m_bReverseDungeon, LC_UnitBase.lua:765-776
    /// m_bReverseMode, LC_Movement_Base.lua:237-240 SetMoveOnSurface).
    /// </summary>
    private bool MovesOnSurface(long scene) =>
        Service<GameData>().Table("PublicParamConfig").TryGetValue("NoviceDungeonReverseDungeonID", out var id)
        && GameData.Number(id) == scene;

    /// <summary>
    /// The local player's position as the client holds it: the last ENTITYSELF base (31005), then each MOVEMENT_CHANGE
    /// (31081) for its own unit that the client applies to its controlled unit (LC_Scene_Sync.lua:3566-3706,
    /// _syncUnitMoveState): ResetPos always, ForceTeleport only when the unit does not move on surfaces.
    /// </summary>
    private static Vec3? ClientPlayerAt(BotClient bot, long uid, bool onSurface)
    {
        Vec3? at = null;
        foreach (var e in bot.Inbox.Snapshot())
        {
            if (e.IsGame(MsgId.EntitySelf))
            {
                if (EntitySelf.Parser.ParseFrom(e.Body).Base?.MovementInfo?.Pos is { } pos) at = pos;
            }
            else if (e.IsGame(GameAoiSink.MovementChange) && MovementChangeNtf.Parser.ParseFrom(e.Body) is { } move && move.Uid == uid
                && (move.TransType == MovementTransitionType.MoveTransResetPos
                    || move.TransType == MovementTransitionType.MoveTransForceTeleport && !onSurface))
                at = move.MovementInfo.Pos;
        }
        return at;
    }

    /// <summary>The unit the client possesses at the player's position among the units it holds (LC_InteractManager).</summary>
    private long Possessed(BotClient bot, float x, float y, float z)
    {
        var data = Service<GameData>();
        var units = new Dictionary<ulong, EntityBase>();
        foreach (var e in bot.Inbox.Snapshot().Where(e => e.IsGame(GameAoiSink.SyncAoiView)))
        {
            var view = AoIViewChangeData.Parser.ParseFrom(e.Body);
            foreach (var gone in view.LeaveAoi) units.Remove(gone);
            foreach (var b in view.Bases) units[b.Uuid] = b;
        }
        var inRange = units.Values.Select(u => (Unit: u, Npc: data.Row("NPCConfig", (long)u.EntityTemplateID)))
            .Where(u => u.Unit.EntityType is 1 or 2 && u.Npc is { } n && n.TryGetProperty("_iInteractID", out var ids)
                && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0 && GameData.Number(ids[0]) is > 0)
            .Select(u => (u.Unit, Rank: GameData.Int(u.Npc!.Value, "_iInteractRank"),
                Reach: GameData.Number(u.Npc.Value.GetProperty("_iInteractDistance")) ?? 0,
                Distance: Math.Sqrt(Math.Pow(u.Unit.MovementInfo.Pos.X - x, 2) + Math.Pow(u.Unit.MovementInfo.Pos.Y - y, 2)
                    + Math.Pow(u.Unit.MovementInfo.Pos.Z - z, 2))))
            .Where(u => u.Distance <= u.Reach).ToArray();
        Assert.NotEmpty(inRange);
        return (long)inRange.OrderByDescending(u => u.Rank).ThenBy(u => u.Distance).First().Unit.Uuid;
    }
}
