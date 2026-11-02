using System.Collections.Concurrent;
using System.Globalization;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;
using Rule = Ro3Emu.Server.Game.ReconstructedDungeons.TrialRule;
using Who = Ro3Emu.Server.Game.ReconstructedDungeons.TrialWho;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Raid boss trials (ReconstructedDungeons.RaidTrials, Reconstructed/raid-trials.json), inferred as a whole: while a raid
/// boss with a script fights, its trials take turns. A trial sends its tips, holds the boss, puts out its objects (scene
/// props, or destructible monsters outside the room's counted actors), deals colours and marks, and after its window
/// judges its rule: the players (or the party) who failed take a share of their maximum HP as the boss's hit, so a wrong
/// position punishes at any gear but never wipes by itself; a solved trial can reward and stagger the boss. A boss that
/// leaves combat ends its trial without a verdict. Generic: nothing here knows a boss.
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>The engine's tick: touches, deaths of the boss and the next trial are looked at this often.</summary>
    private const int TrialTickMs = 500;
    /// <summary>No trial starts within this long after another ended (inferred).</summary>
    private const int TrialGapMs = 6000;

    /// <summary>A raid's trial state for one run.</summary>
    internal sealed class RaidRun(ReconstructedDungeons.RaidScript script)
    {
        public ReconstructedDungeons.RaidScript Script { get; } = script;
        public long EngagedAtMs { get; set; }
        public long[] NextDueMs { get; } = new long[script.Trials.Count];
        public HashSet<(int Trial, int Hp)> HpFired { get; } = [];
        public TrialRun? Active { get; set; }
        public long QuietUntilMs { get; set; }
    }

    /// <summary>A trial being played.</summary>
    internal sealed class TrialRun
    {
        public required ReconstructedDungeons.RaidTrial Trial { get; init; }
        public required int Index { get; init; }
        public required MonsterUnit Boss { get; init; }
        public required long EndsAtMs { get; init; }
        public required int Players { get; init; }
        public long ShowSession { get; set; }
        public List<TrialUnit> Objects { get; } = [];
        public HashSet<long> Marked { get; } = [];
        public bool Over { get; set; }
    }

    /// <summary>One object of a trial: a prop or a destructible monster, its colour (-1: none) and whether it is gone.</summary>
    internal sealed class TrialUnit
    {
        public required WorldUnit Unit { get; init; }
        public required ReconstructedDungeons.TrialObject Spec { get; init; }
        public int Color { get; init; } = -1;
        public bool Chosen { get; init; }
        public bool Gone { get; set; }
        public bool Pickup => Spec.Use != ReconstructedDungeons.TrialUse.None;
    }

    /// <summary>The trials' destructible monsters, for the damage rules (their colour, their hits).</summary>
    private readonly ConcurrentDictionary<long, (Attempt Attempt, TrialUnit Object)> _trialMonsters = new();

    private void WireRaidTrials(CombatService combat, GmCommands gm)
    {
        combat.DamageAdjusters.Add(TrialDamage);
        combat.Killed.Add(TrialObjectKilledAsync);
        combat.DealtAsync.Add(TrialBossHitAsync);
        combat.BossAreaLanded.Add(TrialBreakersAsync);
        gm.Register("raidtrial", "[list | <n> | stop] (raid test aid: the raid boss's scripted trials; start trial n now, or end the "
            + "running one without a verdict)", RaidTrialGmAsync);
    }

    /// <summary>Called when a raid's fight begins: a boss with a script gets its trial tick.</summary>
    private void StartRaidTrials(Attempt a)
    {
        if (a.Plan.Multi == null || a.Plan.Waves.Count == 0 || _catalog.Reconstructed is not { } reconstructed) return;
        var script = a.Plan.Waves[0].Spawns.Select(s => reconstructed.Raid(s.NpcId)).FirstOrDefault(r => r != null);
        if (script == null) return;
        a.Raid = new RaidRun(script);
        TrialLater(a, TrialTickMs, () => TrialTickAsync(a));
    }

    private void TrialLater(Attempt a, int ms, Func<Task> work) =>
        _loop.ScheduleLegacy(Paced(ms), () => a.Settled || a.Room.Closed ? Task.CompletedTask : work(), "raid trial");

    private static MonsterUnit? TrialBoss(Attempt a) => a.Raid is { } raid
        ? a.Room.Monsters.FirstOrDefault(m => !m.Dead && m.Hp > 0 && raid.Script.Bosses.Contains(m.Template.NpcId)) : null;

    private static GameSession[] Fighters(Attempt a) =>
        [.. Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading && !m.Dead)];

    private async Task TrialTickAsync(Attempt a)
    {
        if (a.Raid is not { } raid) return;
        try
        {
            var boss = TrialBoss(a);
            long now = MonsterService.NowMs;
            if (boss == null || boss.Target == null)
            {
                // Out of combat (not engaged yet, or reset): the trial in play ends without a verdict, the clock restarts.
                if (raid.Active is { } stale) await EndTrialAsync(a, stale, judge: false);
                raid.EngagedAtMs = 0;
                return;
            }
            if (raid.EngagedAtMs == 0)
            {
                raid.EngagedAtMs = now;
                for (int i = 0; i < raid.Script.Trials.Count; i++)
                    raid.NextDueMs[i] = raid.Script.Trials[i].FirstMs > 0 ? now + PacedMs(raid.Script.Trials[i].FirstMs) : long.MaxValue;
            }
            if (raid.Active is { } run)
            {
                await TrialTouchesAsync(a, run);
                return;
            }
            if (now < raid.QuietUntilMs) return;
            int percent = (int)(boss.Hp * 100 / Math.Max(1, boss.Template.MaxHp));
            for (int i = 0; i < raid.Script.Trials.Count; i++)
            {
                var trial = raid.Script.Trials[i];
                int threshold = trial.Hp.Where(h => percent <= h && !raid.HpFired.Contains((i, h))).DefaultIfEmpty(-1).Max();
                if (threshold < 0 && now < raid.NextDueMs[i]) continue;
                // A threshold passes every higher one of the same trial too: one big hit does not queue them up.
                foreach (int h in trial.Hp.Where(h => percent <= h)) raid.HpFired.Add((i, h));
                await StartTrialAsync(a, raid, i, boss);
                return;
            }
        }
        finally
        {
            if (!a.Settled && !a.Room.Closed) TrialLater(a, TrialTickMs, () => TrialTickAsync(a));
        }
    }

    private long PacedMs(int ms) => (long)Paced(ms).TotalMilliseconds;

    private async Task StartTrialAsync(Attempt a, RaidRun raid, int index, MonsterUnit boss)
    {
        var trial = raid.Script.Trials[index];
        var fighters = Fighters(a);
        if (fighters.Length == 0) return;
        long now = MonsterService.NowMs, window = PacedMs(trial.WindowMs);
        var run = new TrialRun { Trial = trial, Index = index, Boss = boss, EndsAtMs = now + window, Players = fighters.Length };
        raid.Active = run;
        raid.NextDueMs[index] = trial.EveryMs > 0 ? now + window + PacedMs(trial.EveryMs) : long.MaxValue;
        LogTrialStart(_log, raid.Script.Key, trial.Name, fighters.Length, boss.Hp, boss.Template.MaxHp);

        // Colours: dealt out so that every colour in play is someone's (a shuffled round), or picked up later.
        var colorOf = new Dictionary<GameSession, int>();
        if (trial.Assign && trial.Colors.Count > 0)
        {
            var order = fighters.OrderBy(_ => Random.Shared.Next()).ToArray();
            int offset = Random.Shared.Next(trial.Colors.Count);
            for (int i = 0; i < order.Length; i++)
            {
                int color = (offset + i) % trial.Colors.Count;
                colorOf[order[i]] = color;
                await SetColorAsync(order[i], trial, color, (int)window + 2000, boss.Uid);
            }
        }
        // Marks: random players (a trap, a target).
        foreach (var marked in fighters.OrderBy(_ => Random.Shared.Next()).Take(Math.Min(trial.Marks, fighters.Length)))
        {
            run.Marked.Add(marked.Role!.Id);
            if (trial.MarkBuff > 0) await _buffs.AddAsync(marked, trial.MarkBuff, (int)window + 1000, boss.Uid);
        }
        await PlaceTrialObjectsAsync(a, run, fighters, colorOf);

        int count = run.Objects.Count(o => !o.Pickup);
        var tips = trial.TipByCount.TryGetValue(count, out int byCount) ? [byCount] : trial.Tips;
        // A CombatCountdownTip counts ${1} down from the seconds it is sent (UI_Sys_GVE_MarqueeWidgetCtrl: param 1 + now).
        string? param = trial.TipParam switch
        {
            "countdown" => ((window + 999) / 1000).ToString(CultureInfo.InvariantCulture),
            "marked" => fighters.FirstOrDefault(p => run.Marked.Contains(p.Role!.Id))?.Role!.Name,
            _ => null,
        };
        foreach (int tip in tips)
        {
            var notice = new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = tip };
            if (param != null) notice.ParamList[1] = new ChatParameter { Params = { param }, Type = ChatParamType.ChatParamString };
            await BroadcastAsync(a, SystemBroadcast, notice);
        }
        if (trial.Hold) boss.BusyUntilMs = Math.Max(boss.BusyUntilMs, now + window);
        if (trial.Show > 0) run.ShowSession = await _combat.PlayBossShowAsync(boss, (int)trial.Show, (int)window);
        foreach (var cast in trial.Casts) TrialCastLater(a, run, cast);
        TrialLater(a, trial.WindowMs, () => EndTrialAsync(a, run, judge: true));
    }

    /// <summary>A forced boss cast every so often during the window, at a random fighter (Drake's barrels), or a beam
    /// aimed at one (Osiris's Dark Pulse).</summary>
    private void TrialCastLater(Attempt a, TrialRun run, ReconstructedDungeons.TrialCast cast) => TrialLater(a, cast.EveryMs, async () =>
    {
        if (run.Over || run.Boss.Dead) return;
        if (Fighters(a) is { Length: > 0 } fighters)
        {
            var target = fighters[Random.Shared.Next(fighters.Length)];
            if (cast.BeamWidth > 0) await TrialBeamAsync(a, run, cast, target);
            else _monsters.CastBossSkillNow(run.Boss, target, (int)cast.Skill);
        }
        if (!run.Over) TrialCastLater(a, run, cast);
    });

    /// <summary>A beam (inferred): the skill's show from the boss toward the player, and the trial's objects on its line
    /// destroyed. It hits no one: the players steer it by where they stand.</summary>
    private async Task TrialBeamAsync(Attempt a, TrialRun run, ReconstructedDungeons.TrialCast cast, GameSession target)
    {
        var boss = run.Boss;
        float face = MathF.Atan2(target.Position.X - boss.X, target.Position.Z - boss.Z) * 180 / MathF.PI;
        await _combat.PlayBossShowAsync(boss, (int)cast.Skill, 2000, target.Position.Clone(), face);
        await BreakTrialObjectsAsync(a, run, o =>
        {
            var (forward, side) = CombatService.FacingOffset(o.Unit.X - boss.X, o.Unit.Z - boss.Z, face);
            return forward >= 0 && forward <= cast.BeamLength && Math.Abs(side) <= cast.BeamWidth / 2;
        });
    }

    private async Task SetColorAsync(GameSession p, ReconstructedDungeons.RaidTrial trial, int color, int ms, long source)
    {
        foreach (var other in p.Buffs.Where(b => trial.Colors.Contains(b.ConfigId) && b.ConfigId != trial.Colors[color]).ToArray())
            await _buffs.RemoveAsync(p, other);
        await _buffs.AddAsync(p, trial.Colors[color], ms, source);
    }

    /// <summary>A player's colour in the trial: the first of its colour buffs the player carries (-1: none).</summary>
    private static int ColorOf(GameSession p, ReconstructedDungeons.RaidTrial trial)
    {
        long now = MonsterService.NowMs;
        for (int i = 0; i < trial.Colors.Count; i++)
            if (p.Buffs.Any(b => b.ConfigId == trial.Colors[i] && b.ExpireMs > now)) return i;
        return -1;
    }

    private async Task PlaceTrialObjectsAsync(Attempt a, TrialRun run, GameSession[] fighters, Dictionary<GameSession, int> colorOf)
    {
        if (_catalog.Map(a.Plan.SceneEntry) is not { } map) return;
        var boss = run.Boss;
        var heldColors = colorOf.Values.Distinct().ToArray();
        foreach (var spec in run.Trial.Objects)
        {
            var places = TrialPlaces(map, spec.Place, boss, fighters, run.Marked);
            int wanted = spec.Place.Count > 0 ? spec.Place.Count
                : spec.Place.PerPlayer > 0 ? Math.Clamp(spec.Place.PerPlayer * fighters.Length, spec.Place.Min, spec.Place.Max)
                : places.Count;
            bool everyPlace = spec.Place.OnMarked || spec.Place.OnPlayers || spec.Place.AtBoss;
            var drawn = everyPlace || wanted >= places.Count ? places : [.. places.OrderBy(_ => Random.Shared.Next()).Take(wanted)];
            int chosen = spec.Chosen && drawn.Count > 0 ? Random.Shared.Next(drawn.Count) : -1;
            for (int i = 0; i < drawn.Count; i++)
            {
                // A coloured object takes the colours the players hold in turn (every one of them can be matched); a pickup
                // stands for its own colour; without dealt colours they are mixed at random.
                int color = spec.Npcs.Count <= 1 ? -1
                    : spec.Use != ReconstructedDungeons.TrialUse.None ? i % spec.Npcs.Count
                    : heldColors.Length > 0 ? heldColors[i % heldColors.Length] : Random.Shared.Next(spec.Npcs.Count);
                int npc = color >= 0 ? spec.Npcs[color] : spec.Npcs[0];
                var at = map.Grounded(drawn[i]);
                var unit = spec.Monster ? await SpawnTrialMonsterAsync(a, boss, npc, at, spec, run.Trial.WindowMs)
                    : await AddTrialPropAsync(a, npc, at, spec.Buffs);
                if (unit == null) continue;
                var obj = new TrialUnit { Unit = unit, Spec = spec, Color = color, Chosen = i == chosen };
                run.Objects.Add(obj);
                if (unit is MonsterUnit m) _trialMonsters[m.Uid] = (a, obj);
                if (obj.Chosen && spec.MarkBuff > 0)
                {
                    int markMs = spec.MarkMs > 0 ? (int)PacedMs(spec.MarkMs) : (int)PacedMs(run.Trial.WindowMs);
                    if (unit is MonsterUnit chosenMonster) await _buffs.AddSupportAsync(chosenMonster, spec.MarkBuff, markMs, boss.Uid);
                    else if (unit is SceneActorUnit actor) await SetActorBuffsAsync(actor, [.. actor.Buffs.Select(b => b.ConfigId), spec.MarkBuff]);
                }
            }
        }
    }

    /// <summary>The places an object spec names, in the map's coordinates, each moved <c>Inward</c> toward the boss.</summary>
    private static List<MapPoint> TrialPlaces(MapWorld map, ReconstructedDungeons.TrialPlace place, MonsterUnit boss,
        GameSession[] fighters, HashSet<long> marked)
    {
        var raw = new List<MapPoint>();
        foreach (long id in place.Points) if (map.Points.TryGetValue(id, out var p)) raw.Add(p);
        foreach (long id in place.Regions) if (map.Regions.TryGetValue(id, out var r)) raw.Add(new MapPoint(r.X, r.Y, r.Z, r.Direction));
        if (place.Group != 0 && map.DungeonGroups.TryGetValue(place.Group, out var group))
            raw.AddRange(group.Select(p => new MapPoint(p.X, p.Y, p.Z, p.Face)));
        if (place.AtBoss) raw.Add(new MapPoint(boss.X, boss.Y, boss.Z, boss.Face));
        foreach (var p in fighters)
            if (place.OnPlayers || place.OnMarked && marked.Contains(p.Role!.Id))
                raw.Add(new MapPoint(p.Position.X, p.Position.Y, p.Position.Z, p.Face));
        return place.Inward <= 0 ? raw
            : [.. raw.Select(p => new MapPoint(p.X + (boss.X - p.X) * place.Inward, p.Y, p.Z + (boss.Z - p.Z) * place.Inward, p.Face))];
    }

    private async Task<WorldUnit?> AddTrialPropAsync(Attempt a, int npc, MapPoint at, IReadOnlyList<int> buffs)
    {
        if (NPCConfig.Get(_data, npc) is not { } row) return null;
        int type = (int)row.NPCType;
        var unit = new SceneActorUnit
        {
            Uid = SceneActorUidBase | Interlocked.Increment(ref _sceneActorSerial), SceneEntry = a.Plan.SceneEntry,
            InstanceId = a.Room.Id, X = at.X, Y = at.Y, Z = at.Z, Face = at.Face, NpcId = npc, Point = 0,
            EntityType = type, Camp = row.Camp, Name = type == 2 && row.Name.Length > 0 ? _data.Text(row.Name[0]) : "",
            Template = type == 1 ? _monsters.Template(npc) : null, BornMs = MonsterService.NowMs,
        };
        // Its standing buffs as a monster has them from birth (NPCConfig _kBornEventBuff: a bell's glow, a circle's note), then
        // the trial's own; only rows the client has.
        var shown = row.BornEventBuff.Where(b => b.Length > 0).Select(b => (int)b[0]).Concat(buffs).Distinct()
            .Where(id => BuffConfig.Get(_data, id) is { BuffId: > 0 });
        unit.Buffs.AddRange(shown.Select(id => ActorBuff(unit, id)));
        return await _instances.AddPropAsync(a.Room, unit) ? unit : null;
    }

    /// <summary>A destructible object: a monster outside the room's counted actors (its death never decides the run).
    /// Without an AI of its own (NPCConfig _iAI 0: a bell, a horn, a coffin), or held <c>Still</c>, it stands still and never
    /// strikes back.</summary>
    private async Task<WorldUnit?> SpawnTrialMonsterAsync(Attempt a, MonsterUnit boss, int npc, MapPoint at,
        ReconstructedDungeons.TrialObject spec, int windowMs)
    {
        if (_monsters.Template(npc) is not { } template || NPCConfig.Get(_data, npc) is not { } row) return null;
        bool still = row.Ai == 0 || spec.Still;
        if (still) template = template with { SpeedCm = 0, RoamDistance = 0, AttackRange = 0, NormalSkill = 0, Aggressive = false };
        if (await _monsters.SpawnSingleAsync(boss.RuntimeMap, npc, at, customTemplate: template) is not { } monster) return null;
        if (!await _instances.TrackPropAsync(a.Room, monster)) return null;
        foreach (int buff in spec.Buffs) await _buffs.AddSupportAsync(monster, buff, (int)PacedMs(windowMs) + 1000, boss.Uid);
        if (!still && Fighters(a) is { Length: > 0 } fighters) _monsters.Engage(monster, fighters[Random.Shared.Next(fighters.Length)]);
        return monster;
    }

    /// <summary>Whether (x, z) lies in an object's area: its lane, else the trial's radius around it.</summary>
    internal static bool InTrialArea(TrialUnit o, float radius, float x, float z)
    {
        float dx = x - o.Unit.X, dz = z - o.Unit.Z;
        if (o.Spec.Lane is not { } lane) return dx * dx + dz * dz <= radius * radius;
        var (forward, side) = CombatService.FacingOffset(dx, dz, o.Unit.Face);
        return forward >= -lane.Width / 2 && forward <= lane.Length && Math.Abs(side) <= lane.Width / 2;
    }

    // ---- touches, interactions, kills, the boss hit ----

    /// <summary>Walking into an object: a Collect trial takes it (a matching colour only), a Carry trial hands its buff.</summary>
    private async Task TrialTouchesAsync(Attempt a, TrialRun run)
    {
        if (run.Over || run.Trial.Rule is not (Rule.Collect or Rule.Carry or Rule.Guard)) return;
        foreach (var p in Fighters(a))
            foreach (var o in run.Objects.Where(o => !o.Gone && !o.Pickup && o.Unit is SceneActorUnit))
            {
                if (!InTrialArea(o, Math.Max(1, run.Trial.Radius), p.Position.X, p.Position.Z)) continue;
                if (o.Spec.Match && o.Color != ColorOf(p, run.Trial)) continue;
                o.Gone = true;
                await _instances.RemovePropAsync(a.Room, o.Unit);
                if (run.Trial.Rule == Rule.Collect)
                {
                    if (run.Trial.Success is { Buff: > 0 } reward) await _buffs.AddAsync(p, reward.Buff, (int)PacedMs(reward.BuffMs), run.Boss.Uid);
                    if (run.Objects.All(x => x.Gone || x.Pickup)) { await EndTrialAsync(a, run, judge: true); return; }
                }
                else if (run.Trial.Carry.Buff > 0)
                {
                    await _buffs.AddAsync(p, run.Trial.Carry.Buff, (int)PacedMs(run.Trial.Carry.Ms), run.Boss.Uid);
                    if (run.Trial.Carry.Say > 0) await p.SendGameAsync(SystemBroadcast, new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = run.Trial.Carry.Say });
                }
                break;
            }
    }

    /// <summary>A trial's object with a use (its NPC's own interaction, NPCConfig _iInteractID): 32700 hands the player its
    /// colour (Ilmo's notes) or takes the player to the next object of its kind (the wormholes, inferred: their interaction
    /// skill says "Emerges from the other end of the Wormhole").</summary>
    private async Task<bool> TryTrialInteractAsync(GameSession s, NpcInteractionReq req)
    {
        if (Active(s) is not { Raid.Active: { } run } || run.Objects.FirstOrDefault(o => o.Unit.Uid == req.NpcUid) is not { } o) return false;
        int code = !o.Pickup || o.Gone || o.Unit is not SceneActorUnit actor || !_catalog.Interactions(actor.NpcId).Contains(req.InteractionId)
            ? ErrorCodes.NpcParameter
            : MonsterService.Distance(s.Position.X, s.Position.Z, o.Unit.X, o.Unit.Z) > SceneActorReach ? ErrorCodes.NpcTooFar : 0;
        await s.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionRsp { Result = code, Req = req });
        if (code != 0) return true;
        if (o.Spec.Use == ReconstructedDungeons.TrialUse.Color && o.Color >= 0)
            await SetColorAsync(s, run.Trial, o.Color, (int)Math.Max(1000, run.EndsAtMs - MonsterService.NowMs + 2000), run.Boss.Uid);
        else if (o.Spec.Use == ReconstructedDungeons.TrialUse.Warp)
        {
            var kind = run.Objects.Where(x => ReferenceEquals(x.Spec, o.Spec) && !x.Gone).ToList();
            var to = kind[(kind.IndexOf(o) + 1) % kind.Count].Unit;
            if (!ReferenceEquals(to, o.Unit)) await _scenes.TeleportInMapAsync(s, new MapPoint(to.X + 1, to.Y, to.Z, s.Face));
        }
        return true;
    }

    private long TrialDamage(GameSession s, MonsterUnit m, System.Text.Json.JsonElement row, DamageHitResult result, long damage)
    {
        if (!_trialMonsters.TryGetValue(m.Uid, out var entry)) return damage;
        var (a, o) = entry;
        if (a.Raid?.Active is not { } run || !run.Objects.Contains(o)) return damage;
        if (o.Spec.Match && o.Color >= 0 && ColorOf(s, run.Trial) != o.Color) return 0;
        return o.Spec.Hits > 0 ? (m.Template.MaxHp + o.Spec.Hits * run.Players - 1) / (o.Spec.Hits * run.Players) : damage;
    }

    private async Task TrialObjectKilledAsync(GameSession s, MonsterUnit m)
    {
        if (!_trialMonsters.TryRemove(m.Uid, out var entry)) return;
        var (a, o) = entry;
        o.Gone = true;
        if (a.Raid?.Active is { } run && run.Objects.Contains(o) && !run.Over && run.Trial.Rule == Rule.Kill && KillSolved(run))
            await EndTrialAsync(a, run, judge: true);
    }

    private static bool KillSolved(TrialRun run) => run.Objects.Any(o => o.Chosen)
        ? run.Objects.Where(o => o.Chosen).All(o => o.Gone) : run.Objects.Where(o => !o.Pickup).All(o => o.Gone);

    /// <summary>A Carry trial is solved when a carrier hits the boss.</summary>
    private async Task TrialBossHitAsync(GameSession s, MonsterUnit m, System.Text.Json.JsonElement row, long damage)
    {
        if (damage <= 0 || Active(s) is not { Raid.Active: { } run } a || run.Over || run.Trial.Rule != Rule.Carry
            || !ReferenceEquals(m, run.Boss) || run.Trial.Carry.Buff <= 0) return;
        if (s.Buffs.FirstOrDefault(b => b.ConfigId == run.Trial.Carry.Buff && b.ExpireMs > MonsterService.NowMs) is not { } carried) return;
        await _buffs.RemoveAsync(s, carried);
        foreach (var o in run.Objects) o.Gone = true;
        await EndTrialAsync(a, run, judge: true);
    }

    /// <summary>A boss special landing on a trial's objects destroys those its breakers name (Drake's barrels).</summary>
    private async Task TrialBreakersAsync(MonsterUnit boss, BossSkill skill, Vec3 origin, Vec3 center, float face)
    {
        var a = _sessions.All().Select(s => s.DungeonAttempt).FirstOrDefault(x => x?.Raid?.Active?.Boss == boss);
        if (a?.Raid?.Active is not { } run || run.Over || !run.Trial.Breakers.Contains(skill.Id)) return;
        await BreakTrialObjectsAsync(a, run, o => CombatService.InBossArea(skill, origin, center, face, o.Unit.X, o.Unit.Z));
    }

    /// <summary>Destroys the trial's standing objects that <paramref name="hit"/> reaches; a Kill trial solved by it ends.</summary>
    private async Task BreakTrialObjectsAsync(Attempt a, TrialRun run, Func<TrialUnit, bool> hit)
    {
        foreach (var o in run.Objects.Where(o => !o.Gone && !o.Pickup && hit(o)).ToArray())
        {
            o.Gone = true;
            if (o.Unit is MonsterUnit m) { _trialMonsters.TryRemove(m.Uid, out _); await _monsters.KillAsync(m); }
            else await _instances.RemovePropAsync(a.Room, o.Unit);
        }
        if (!run.Over && run.Trial.Rule == Rule.Kill && KillSolved(run)) await EndTrialAsync(a, run, judge: true);
    }

    // ---- the verdict ----

    private async Task EndTrialAsync(Attempt a, TrialRun run, bool judge)
    {
        if (run.Over || a.Raid is not { } raid) return;
        run.Over = true;
        if (ReferenceEquals(raid.Active, run)) raid.Active = null;
        raid.QuietUntilMs = MonsterService.NowMs + PacedMs(TrialGapMs);
        var trial = run.Trial;
        var boss = run.Boss;
        var fighters = Fighters(a);
        bool bossFights = !boss.Dead && boss.Hp > 0 && boss.Target != null && !a.Settled && !a.Room.Closed;
        if (judge && bossFights)
        {
            var (passed, failing, partyFailed) = Judge(trial, run, boss, fighters);
            LogTrialVerdict(_log, raid.Script.Key, trial.Name, partyFailed || failing.Count > 0 ? "failed" : "solved", failing.Count);
            int left = run.Objects.Count(o => !o.Gone && !o.Pickup);
            if (partyFailed) await TrialOutcomeAsync(a, run, trial.Fail, fighters, fighters, left);
            else if (failing.Count > 0) await TrialOutcomeAsync(a, run, trial.Fail, failing, fighters, left);
            if (!partyFailed && trial.Success is { } success && (failing.Count == 0 || IsPerPlayer(trial.Rule)))
                await TrialOutcomeAsync(a, run, success, passed, fighters, left);
        }
        if (run.ShowSession != 0 && (!judge || trial.Success?.Stagger is not > 0)) await _combat.InterruptBossShowAsync(boss, run.ShowSession);
        if (trial.Hold && boss.BusyUntilMs > MonsterService.NowMs && trial.Success?.StaggerMs is not > 0) boss.BusyUntilMs = MonsterService.NowMs;
        foreach (var o in run.Objects)
        {
            if (o.Unit is MonsterUnit m) _trialMonsters.TryRemove(m.Uid, out _);
            if (!o.Gone || o.Unit is MonsterUnit) await _instances.RemovePropAsync(a.Room, o.Unit);
        }
        // What the trial dealt out ends with it: colours, marks (a trap opens), carried objects.
        foreach (var p in Present(a))
            foreach (var buff in p.Buffs.Where(b => trial.Colors.Contains(b.ConfigId) && trial.Assign || b.ConfigId == trial.MarkBuff
                         || b.ConfigId == trial.Carry.Buff).ToArray())
                await _buffs.RemoveAsync(p, buff);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Raid {Raid} trial '{Trial}' starts ({Players} players, boss at {Hp}/{Max} HP)")]
    private static partial void LogTrialStart(ILogger logger, string raid, string trial, int players, long hp, long max);

    [LoggerMessage(Level = LogLevel.Information, Message = "Raid {Raid} trial '{Trial}': {Verdict} ({Failing} failing)")]
    private static partial void LogTrialVerdict(ILogger logger, string raid, string trial, string verdict, int failing);

    private static bool IsPerPlayer(Rule rule) => rule is Rule.Stand or Rule.Avoid or Rule.Shelter or Rule.Guard or Rule.Spread;

    /// <summary>The verdict: the players who passed, those who failed, and whether the party as a whole failed.</summary>
    internal static (List<GameSession> Passed, List<GameSession> Failing, bool PartyFailed) Judge(ReconstructedDungeons.RaidTrial trial,
        TrialRun run, MonsterUnit boss, IReadOnlyList<GameSession> fighters)
    {
        var objects = run.Objects.Where(o => !o.Gone && !o.Pickup).ToArray();
        bool Inside(GameSession p, TrialUnit o) => InTrialArea(o, trial.Radius, p.Position.X, p.Position.Z);
        bool Matches(GameSession p, TrialUnit o) => !o.Spec.Match || o.Color < 0 || ColorOf(p, trial) == o.Color;
        bool Carrier(GameSession p) => trial.Carry.Buff > 0 && p.Buffs.Any(b => b.ConfigId == trial.Carry.Buff && b.ExpireMs > MonsterService.NowMs);
        bool IsMarked(GameSession p) => run.Marked.Contains(p.Role!.Id);
        var failing = new List<GameSession>();
        bool party = false;
        switch (trial.Rule)
        {
            case Rule.Stand:
                failing.AddRange(fighters.Where(p => !objects.Any(o => Inside(p, o) && Matches(p, o))));
                break;
            case Rule.Avoid:
                failing.AddRange(fighters.Where(p => objects.Any(o => Inside(p, o))));
                break;
            case Rule.Shelter:
                failing.AddRange(fighters.Where(p => !objects.Any(o => Sheltered(boss.X, boss.Z, o.Unit.X, o.Unit.Z, p.Position.X, p.Position.Z,
                    trial.Width, trial.Depth))));
                break;
            case Rule.Guard:
                failing.AddRange(fighters.Where(p => IsMarked(p) && !fighters.Any(g => Carrier(g)
                    && MonsterService.Distance(g.Position.X, g.Position.Z, p.Position.X, p.Position.Z) <= trial.Radius)));
                break;
            case Rule.Spread:
                foreach (var p in fighters.Where(IsMarked))
                {
                    var near = fighters.Where(o => !ReferenceEquals(o, p)
                        && MonsterService.Distance(o.Position.X, o.Position.Z, p.Position.X, p.Position.Z) <= trial.Radius).ToArray();
                    if (near.Length == 0) continue;
                    failing.AddRange(near.Prepend(p).Where(x => !failing.Contains(x)));
                }
                break;
            case Rule.Occupy:
                party = objects.Any(o => !fighters.Any(p => Inside(p, o) && Matches(p, o)));
                break;
            case Rule.Collect:
                party = objects.Length > 0;
                break;
            case Rule.Kill:
                party = !KillSolved(run);
                break;
            case Rule.Carry:
                party = !run.Objects.All(o => o.Gone) || run.Objects.Count == 0;
                break;
            case Rule.Lure:
                party = objects.Length > 0 && !objects.Any(o => InTrialArea(o, trial.Radius, boss.X, boss.Z));
                break;
        }
        return ([.. fighters.Where(p => !failing.Contains(p))], failing, party);
    }

    /// <summary>An outcome on the players it reaches: the share of their maximum HP as the boss's hit, a buff, a line, a
    /// stagger of the boss.</summary>
    private async Task TrialOutcomeAsync(Attempt a, TrialRun run, ReconstructedDungeons.TrialOutcome outcome,
        IReadOnlyList<GameSession> chosen, IReadOnlyList<GameSession> fighters, int left)
    {
        var boss = run.Boss;
        var reached = outcome.Who switch
        {
            Who.All => fighters,
            Who.Marked => [.. fighters.Where(p => run.Marked.Contains(p.Role!.Id))],
            Who.Inside => [.. fighters.Where(p => run.Objects.Any(o => !o.Gone && !o.Pickup
                && InTrialArea(o, run.Trial.Radius, p.Position.X, p.Position.Z)))],
            _ => chosen,
        };
        int percent = outcome.PerObject ? Math.Min(outcome.CapPercent, outcome.HpPercent * Math.Max(1, left)) : outcome.HpPercent;
        long skill = outcome.Skill > 0 ? outcome.Skill : boss.Template.NormalSkill;
        if (percent > 0 && reached.Count > 0 && _data.Row("SkillConfig", skill) is { } row)
            await _combat.MonsterSkillHitAsync(boss, row, reached, boss.Uid, bossDebuffsOnly: true, maxHpBasisPoints: percent * 100);
        foreach (var p in reached.Where(p => !p.Dead))
        {
            if (outcome.Buff > 0) await _buffs.AddAsync(p, outcome.Buff, (int)PacedMs(Math.Max(1000, outcome.BuffMs)), boss.Uid);
            if (outcome.Say > 0) await p.SendGameAsync(SystemBroadcast, new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = outcome.Say });
        }
        if (outcome.Stagger > 0 || outcome.StaggerMs > 0)
        {
            if (run.ShowSession != 0) await _combat.InterruptBossShowAsync(boss, run.ShowSession);
            run.ShowSession = 0;
            long until = MonsterService.NowMs + PacedMs(outcome.StaggerMs);
            boss.BusyUntilMs = until;
            if (outcome.Stagger > 0) await _combat.PlayBossShowAsync(boss, (int)outcome.Stagger, (int)PacedMs(outcome.StaggerMs));
        }
    }

    // ---- GM ----

    /// <summary>"!raidtrial [list | n | stop]": the raid boss's trials; start one now (engaging the GM if the boss is idle),
    /// or end the running one without a verdict (test aid, not original behaviour).</summary>
    private async Task RaidTrialGmAsync(GameSession s, string[] args)
    {
        if (Active(s) is not { Raid: { } raid } a || TrialBoss(a) is not { } boss)
        {
            await GmCommands.ReplyAsync(s, "Not in a raid whose boss has scripted trials (start the battle first).");
            return;
        }
        if (args.Length == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            await GmCommands.ReplyAsync(s, $"{raid.Script.Name}: " + string.Join("; ", raid.Script.Trials.Select((t, i) =>
                $"{i + 1} {t.Name} ({t.Rule}{(t.Hp.Count > 0 ? $", HP {string.Join('/', t.Hp)}%" : "")}{(t.EveryMs > 0 ? $", every {t.EveryMs / 1000} s" : "")})"))
                + (raid.Active is { } running ? $". Running: {running.Trial.Name}." : "."));
            return;
        }
        if (args[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (raid.Active is { } running) await EndTrialAsync(a, running, judge: false);
            await GmCommands.ReplyAsync(s, "Trial ended without a verdict (test aid).");
            return;
        }
        if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > raid.Script.Trials.Count)
            throw new ArgumentException($"list | 1-{raid.Script.Trials.Count} | stop");
        if (raid.Active is { } previous) await EndTrialAsync(a, previous, judge: false);
        if (boss.Target == null) _monsters.Engage(boss, s);
        if (raid.EngagedAtMs == 0) raid.EngagedAtMs = MonsterService.NowMs;
        await StartTrialAsync(a, raid, n - 1, boss);
        await GmCommands.ReplyAsync(s, $"Trial {n} '{raid.Script.Trials[n - 1].Name}' started (test aid).");
    }
}
