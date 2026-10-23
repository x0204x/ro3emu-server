using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Field monsters: templates from NPCConfig / AttriMonsterConfig / AIConfig / the normal-attack
/// skill, spawning the scene-editor groups when a map is first entered, the AI (roam, chase, attack, leash) on the map
/// tick, death, corpse removal and respawn. Combat math and hits go through CombatService.
/// </summary>
public sealed partial class MonsterService : MonsterBrain, IGameTicker
{
    private readonly World _world;
    private readonly WorldData _worldData;
    private readonly GameData _data;
    private readonly GameLoop _loop;
    private readonly ILogger<MonsterService> _log;
    private readonly ReconstructedDungeons? _reconstructed;
    private readonly BossSkillOptions _bossOptions;

    public MonsterService(World world, WorldData worldData, GameData data, GameLoop loop, ILogger<MonsterService> log,
        ReconstructedDungeons? reconstructed = null, Microsoft.Extensions.Options.IOptions<ServerOptions>? options = null)
    {
        this._reconstructed = reconstructed;
        _bossOptions = options?.Value.BossSkills ?? new BossSkillOptions();
        this._world = world;
        this._worldData = worldData;
        this._data = data;
        this._loop = loop;
        this._log = log;
        world.UnitRemoved.Add(unit =>
        {
            if (unit is MonsterUnit monster && _spawns.TryGetValue(monster.RuntimeMap, out var owner))
                owner.Units.Remove(monster);
            return unit is MonsterUnit { BossSkills: not null } boss ? EndBossSkillsTask(boss, interrupt: false) ?? Task.CompletedTask : Task.CompletedTask;
        });
    }

    private const long UidBase = 1L << 41;
    private const int AggressiveAi = 20000004;

    /// <summary>A walk segment is capped so a monster seen mid-way is re-synchronized soon.</summary>
    private const float MaxSegment = 12f;

    private readonly Dictionary<int, MonsterTemplate?> _templates = new();
    private sealed class SpawnState
    {
        public bool InitialSpawned;
        public bool Retired;
        public int Pending;
        public readonly Dictionary<int, HashSet<int>> UsedPoints = new();
        public readonly HashSet<MonsterUnit> Units = new();
        public readonly List<(TimeSpan Due, Func<Task> Work)> Deadlines = new();
        public readonly TaskCompletionSource Quiesced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<RuntimeMapKey, SpawnState> _spawns = new();
    private readonly Random _random = new();
    private long _serial;

    /// <summary>Set by CombatService: a monster's attack on its target (the brain decides when).</summary>
    public Func<MonsterUnit, GameSession, Task>? Attack { get; set; }
    public Func<MonsterUnit, TowerPetUnit, Task>? AttackPet { get; set; }

    public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public MonsterTemplate? Template(int npcId)
    {
        if (!_templates.TryGetValue(npcId, out var t)) _templates[npcId] = t = Build(npcId);
        return t;
    }

    private MonsterTemplate? Build(int npcId)
    {
        if (_data.Row("NPCConfig", npcId) is not { } npc || GameData.Int(npc, "_iNPCType") != 1) return null;
        int level = (int)GameData.Int(npc, "_iLevel");
        long attrId = GameData.Int(npc, "_iAttribute");
        if (attrId == 0) attrId = GameData.Int(npc, "_iAttributeVariated") * 1000 + level;
        var attr = new Dictionary<long, double>();
        if (_data.Row("AttriMonsterConfig", attrId) is { } am)
            foreach (var (id, v) in GameData.Pairs(am, "_kAttri")) attr[id] = v;
        foreach (var (id, scale) in GameData.Pairs(npc, "_kAttriScale"))
            if (attr.ContainsKey(id)) attr[id] = attr[id] * scale / 10000;
        long A(long id) => (long)Math.Round(attr.GetValueOrDefault(id));

        int ai = (int)GameData.Int(npc, "_iAI");
        var aiRow = _data.Row("AIConfig", ai);
        float Cm(string field, float fallback) => aiRow is { } r && GameData.Int(r, field) is var v and > 0 ? v / 100f : fallback;

        int skill = 0, skillMs = 1000, swing = 2000;
        float hitPoint = 0.2f, range = 2f;
        long normal = NormalAttack(npcId, npc);
        // A template without a normal attack of its own: a reconstruction may name whose it borrows.
        if (normal == 0 && _reconstructed?.AttackSource(npcId) is int from && _data.Row("NPCConfig", from) is { } source)
            normal = NormalAttack(from, source);
        if (normal != 0 && _data.Row("SkillConfig", normal) is { } row)
        {
            skill = (int)normal;
            skillMs = (int)Math.Max(300, GameData.Int(row, "_iActionTime"));
            swing = (int)Math.Max(skillMs, GameData.Int(row, "_iCD"));
            range = Math.Max(1f, GameData.Int(row, "_iDistanceMax") / 100f);
            if (_data.SkillTemplate(GameData.Int(row, "_iScriptShow")) is { } tpl && tpl.TryGetProperty("m_kAtkPoints", out var pts)
                && pts.ValueKind == JsonValueKind.Array && pts.GetArrayLength() > 0 && GameData.Number(pts[0]) is { } hp)
                hitPoint = (float)hp;
        }
        int speed = (int)(attr.ContainsKey(113) ? A(113) : GameData.Int(npc, "_iSpeed"));
        // A reconstructed story boss fought solo instead of beside its original NPC companions (inferred scaling).
        if (_reconstructed?.SoloTuning(npcId) is { } solo)
        {
            foreach (long id in new long[] { 101, 103, 104 }.Where(attr.ContainsKey))
                attr[id] = attr[id] * (id == 101 ? solo.Hp : solo.Attack) / 10000;
        }
        return new MonsterTemplate(
            npcId, level, Math.Max(1, A(101)), A(102), A(103), A(104), A(105), A(106),
            Hit: attr.ContainsKey(109) ? A(109) : level + 1, Flee: attr.ContainsKey(110) ? A(110) : level, Cri: A(111),
            speed, (int)GameData.Int(npc, "_iElement"), (int)GameData.Int(npc, "_iRace"), (int)GameData.Int(npc, "_iSize"),
            Math.Max(1, (int)GameData.Int(npc, "_iCamp")),
            Aggressive: ai == AggressiveAi,
            RoamDistance: speed > 0 ? Cm("_iRoamDis", 0) : 0, RoamTimeMs: aiRow is { } r2 ? (int)Math.Max(2000, GameData.Int(r2, "_iRoamTime")) : 8000,
            ViewRange: Cm("_iViewRange", 5), LeashDistance: aiRow is { } r3 && r3.TryGetProperty("_iUnFightParam", out var uf) && uf.ValueKind == JsonValueKind.Array
                && uf.GetArrayLength() > 0 && GameData.Number(uf[0]) is { } leash ? (float)leash / 100 : Cm("_iChaseDistance", 18),
            skill, skillMs, hitPoint, swing, range, (int)Math.Max(500, GameData.Int(npc, "_iBodyRemainTime")))
        {
            Skills = BossBook(npcId, npc, skill),
            CastSpeed = A(AttributeId.CastSpeed),
        };
    }

    private BossNormalAttacks? _bossAttacks;
    private BossNormalAttacks BossAttacks => _bossAttacks ??= new BossNormalAttacks(_data);

    /// <summary>
    /// A monster's normal attack (0: none). A boss's own row (<see cref="BossNormalAttacks"/>) comes first, also ahead of a
    /// generic row its NPCConfig lists (the mini bosses list Poporing's 5003301); then the first listed _kSkills row flagged
    /// _iIsNormalAtk; then the listed row named as a swing with the shortest cooldown: the airship crew list their own
    /// MainStory_EvilKnightNormalAttack / EvilHunterNormalAttack unflagged, and 5300088 also lists a 15 s special that
    /// reuses its swing's script (inferred by name).
    /// </summary>
    private long NormalAttack(int npcId, JsonElement npc)
    {
        if (BossAttacks.For(npcId) is var boss and > 0) return boss;
        var listed = new List<(long Id, JsonElement Row)>();
        if (npc.TryGetProperty("_kSkills", out var skills) && skills.ValueKind == JsonValueKind.Array)
            foreach (var s in skills.EnumerateArray())
                if (GameData.Number(s) is { } sid && _data.Row("SkillConfig", (long)sid) is { } row) listed.Add(((long)sid, row));
        foreach (var (id, row) in listed) if (GameData.Int(row, "_iIsNormalAtk") == 1) return id;
        return listed.Where(s => BossNormalAttacks.IsNamedSwing(s.Row)).OrderBy(s => GameData.Int(s.Row, "_iCD"))
            .Select(s => s.Id).FirstOrDefault();
    }

    // ---- spawning ----

    /// <summary>Compatibility entry point for the original public map, line 1.</summary>
    public Task EnsureSpawnedAsync(long scene) => EnsureSpawnedAsync(RuntimeMapKey.Public(scene));

    /// <summary>Spawn each public line independently from the same authored groups (inferred line policy).</summary>
    public async Task EnsureSpawnedAsync(RuntimeMapKey runtime)
    {
        if (runtime.Kind != RuntimeMapKind.PublicLine || _worldData.Map(runtime.SceneEntry) is not { } map) return;
        if (!_spawns.TryGetValue(runtime, out var state)) _spawns[runtime] = state = new SpawnState();
        if (state.InitialSpawned || state.Retired) return;
        state.InitialSpawned = true;
        state.Pending++;
        int count = 0;
        try
        {
            foreach (var group in map.SpawnGroups)
            {
                if (state.Retired) break;
                if (group.Points.Count == 0) continue;
                if (group.Pool)
                {
                    foreach (var (npcId, min, max) in group.Limits)
                    {
                        if (state.Retired) break;
                        if (Template(npcId) == null) continue;
                        int n = _random.Next(min, max + 1);
                        for (int i = 0; i < n && !state.Retired; i++) count += await SpawnAsync(runtime, state, map, group, npcId) ? 1 : 0;
                    }
                }
                else
                {
                    int alive = Math.Min(Math.Max(1, group.Total), group.Points.Count);
                    foreach (var point in group.Points.OrderBy(_ => _random.Next()).Take(alive))
                        if (!state.Retired && Template(point.ConfigId) != null)
                            count += await SpawnAsync(runtime, state, map, group, point.ConfigId, point) ? 1 : 0;
                }
            }
        }
        finally { EndPending(state); }
        _log.LogInformation("Spawned {Count} monsters on {Runtime} {Name}", count, runtime, _data.MapName(runtime.SceneEntry));
    }

    private async Task<bool> SpawnAsync(RuntimeMapKey runtime, SpawnState state, MapWorld map, SpawnGroup group, int npcId, SpawnPoint? at = null)
    {
        if (state.Retired) return false;
        if (!state.UsedPoints.TryGetValue(group.Id, out var used)) state.UsedPoints[group.Id] = used = new();
        var point = at ?? group.Points.Where(p => !used.Contains(p.Id)).OrderBy(_ => _random.Next()).FirstOrDefault();
        if (point == null || Template(npcId) is not { } template) return false;
        used.Add(point.Id);
        var pos = map.Grounded(new MapPoint(point.X, point.Y, point.Z, point.Face));
        var monster = new MonsterUnit
        {
            Uid = UidBase | ++_serial,
            SceneEntry = runtime.SceneEntry, RuntimeMap = runtime,
            X = pos.X, Y = pos.Y, Z = pos.Z, Face = point.Face,
            Template = template,
            Group = group,
            Home = point,
            Hp = template.MaxHp,
            BornTimeMs = NowMs,
            NextRoamMs = NowMs + _random.Next(template.RoamTimeMs),
            Brain = this,
        };
        state.Units.Add(monster);
        await _world.AddUnitAsync(monster);
        if (!state.Retired)
        {
            foreach (var handler in Born) await handler(monster);
            return true;
        }
        if (state.Units.Remove(monster)) await _world.RemoveUnitAsync(monster);
        return false;
    }

    private void EndPending(SpawnState state)
    {
        if (--state.Pending == 0 && state.Retired) state.Quiesced.TrySetResult();
    }

    /// <summary>In-flight work and owned corpse/respawn deadlines delay empty-line retirement.</summary>
    public bool HasPendingWork(RuntimeMapKey runtime) => _spawns.TryGetValue(runtime, out var state)
        && (state.Pending > 0 || state.Deadlines.Count > 0);

    private void Schedule(SpawnState state, TimeSpan delay, Func<Task> work) =>
        state.Deadlines.Add((_loop.Now + delay, work));

    /// <summary>Owned deadlines can be dropped immediately when a runtime retires.</summary>
    public void Tick(GameTime time)
    {
        // The capture of each state lives in RunDue: a lambda over the loop variable here allocated a closure for
        // every spawn state on every tick, due or not.
        List<SpawnState>? ready = null;
        foreach (var state in _spawns.Values)
            if (!state.Retired && state.Deadlines.Count > 0 && state.Deadlines.Exists(d => d.Due <= time.Now)) (ready ??= new()).Add(state);
        if (ready != null) foreach (var state in ready) RunDue(state, time.Now);
    }

    // Stage G: monster timers (deadlines, swings, target changes, speed and health broadcasts) run in runtime scopes.
    // As Realm (1484 target changes, 2009 swings in 5 min of S3 at 500 players) each waited for every reservation to
    // drain and then stopped all owned work, players' walks included. Durable consequences admit their own keys.
    private void RunDue(SpawnState state, TimeSpan now)
    {
        var due = state.Deadlines.Where(d => d.Due <= now).ToArray();
        state.Deadlines.RemoveAll(d => d.Due <= now);
        foreach (var deadline in due)
        {
            state.Pending++;
            _loop.TrackRuntimeDetached("monster deadline", () => RunDeadlineAsync(state, deadline.Work));
        }
    }

    private async Task RunDeadlineAsync(SpawnState state, Func<Task> work)
    {
        try { if (!state.Retired) await work(); }
        catch (Exception e) { _log.LogError(e, "Monster deadline failed"); }
        finally { EndPending(state); }
    }

    /// <summary>Retire all owned actors and invalidate corpse/respawn callbacks before releasing the runtime.</summary>
    public async Task RetireRuntimeAsync(RuntimeMapKey runtime)
    {
        if (!_spawns.Remove(runtime, out var state)) return;
        state.Retired = true;
        state.Deadlines.Clear();
        foreach (var unit in state.Units.ToArray())
            if (state.Units.Remove(unit)) await _world.RemoveUnitAsync(unit);
        if (state.Pending > 0) await state.Quiesced.Task;
        state.UsedPoints.Clear();
    }

    /// <summary>After a monster joined its map, from a spawn group or alone (MvpService: NPCConfig _kBornEventBuff).</summary>
    public List<Func<MonsterUnit, Task>> Born { get; } = [];

    /// <summary>
    /// A monster outside the map's spawn groups (field MVPs): no group respawn, the caller decides when it comes back.
    /// </summary>
    public async Task<MonsterUnit?> SpawnSingleAsync(long scene, int npcId, MapPoint at, long instanceId = 0,
        int hpRatio = 10000, int damageRatio = 10000, MonsterTemplate? customTemplate = null)
        => await SpawnSingleAsync(instanceId == 0 ? RuntimeMapKey.Public(scene) : RuntimeMapKey.Private(scene, instanceId),
            npcId, at, hpRatio, damageRatio, customTemplate);

    /// <summary>Spawn an independent actor on an explicit public line/private room/event runtime.</summary>
    public async Task<MonsterUnit?> SpawnSingleAsync(RuntimeMapKey runtime, int npcId, MapPoint at,
        int hpRatio = 10000, int damageRatio = 10000, MonsterTemplate? customTemplate = null)
    {
        if ((customTemplate ?? Template(npcId)) is not { } template || template.NpcId != npcId
            || _worldData.Map(runtime.SceneEntry) is not { } map
            || hpRatio is < 1 or > 10000 || damageRatio is < 1 or > 10000) return null;
        SpawnState? state = null;
        if (runtime.Kind == RuntimeMapKind.PublicLine)
        {
            if (!_spawns.TryGetValue(runtime, out state)) _spawns[runtime] = state = new SpawnState();
            if (state.Retired) return null;
            state.Pending++;
        }
        try
        {
            // PetFloorConfig's 1011/per_maxhp modifies this room's actor, never the cached NPC template.
            long hp = Math.Max(1, template.MaxHp / 10000 * hpRatio
                + (template.MaxHp % 10000 * hpRatio + 5000) / 10000);
            template = template with { MaxHp = hp };
            var point = new SpawnPoint(-1, at.X, at.Y, at.Z, at.Face, npcId);
            var pos = map.Grounded(at);
            var monster = new MonsterUnit
            {
                Uid = UidBase | ++_serial, SceneEntry = runtime.SceneEntry, RuntimeMap = runtime,
                InstanceId = runtime.Kind == RuntimeMapKind.PublicLine ? 0 : runtime.Id,
                X = pos.X, Y = pos.Y, Z = pos.Z, Face = at.Face, Template = template,
                Group = new SpawnGroup(-1, false, 1, 0, [], [point]), Home = point, Hp = template.MaxHp, BornTimeMs = NowMs,
                DamageRatio = damageRatio,
                NextRoamMs = NowMs + _random.Next(template.RoamTimeMs), Brain = this,
            };
            state?.Units.Add(monster);
            await _world.AddUnitAsync(monster);
            if (state is not { Retired: true })
            {
                foreach (var handler in Born) await handler(monster);
                return monster;
            }
            if (state.Units.Remove(monster)) await _world.RemoveUnitAsync(monster);
            return null;
        }
        finally { if (state != null) EndPending(state); }
    }

    // ---- AI ----

    // Lambdas live in these helpers, not in Tick: a lambda over Tick's monster made the compiler allocate its closure
    // on every tick of every monster, whether or not it ran (~250 MB per two minutes with a populated world).
    private void SendStop(MonsterUnit m, long now) => _loop.TrackRuntimeDetached("monster stop", () => _world.MoveUnitAsync(m, Stop(m, now)));
    private void SendSpeed(MonsterUnit m, long now) =>
        _loop.TrackRuntimeDetached("monster speed", () => _world.ToUnitViewersAsync(m, CombatService.AoiAttrChange, SpeedChange(m, now)));
    private void SendHealth(MonsterUnit m) => _loop.TrackRuntimeDetached("monster health", () => _world.ToUnitViewersAsync(m, CombatService.AoiAttrChange, Hp(m)));

    private GameSession? NearestTarget(MonsterUnit m)
    {
        GameSession? near = null;
        float best = float.MaxValue;
        foreach (var p in m.Viewers)
        {
            if (p.Role == null || p.Dead || TargetVisible?.Invoke(m, p) == false) continue;
            float d = Distance(m, p);
            if (d <= m.Template.ViewRange && d < best) { best = d; near = p; }
        }
        return near;
    }

    internal Func<MonsterUnit, GameSession, bool>? TargetVisible { get; set; }

    internal Func<WorldUnit, int, bool>? Forbidden { get; set; }

    /// <summary>A guardian control that forbids walking stops a moving monster where it is.</summary>
    private void StopWhenRooted(MonsterUnit m, long now)
    {
        if (Forbidden?.Invoke(m, ForbiddenState.Walk) != true || !m.Moving) return;
        m.Advance(now);
        m.Moving = false;
        SendStop(m, now);
    }

    public void Tick(MonsterUnit m, MapInstance map)
    {
        if (m.RuntimeMap.Kind == RuntimeMapKind.PublicLine
            && (!_spawns.TryGetValue(m.RuntimeMap, out var state) || state.Retired)) return;
        if (m.Dead || m.Hp <= 0) return; // lethal HP is authoritative before the awaited death broadcast finishes
        long now = NowMs;
        StopWhenRooted(m, now);
        if (m.SlowMovementExpireMs > 0 && now >= m.SlowMovementExpireMs)
        {
            m.SlowMovementBp = 0;
            m.SlowMovementExpireMs = 0;
            if (m.Moving)
            {
                m.Advance(now);
                m.Moving = false;
                SendStop(m, now);
            }
            m.NextChaseMs = 0;
            SendSpeed(m, now);
            if (m.Returning && m.Target == null) WalkTo(m, map, m.Home.X, m.Home.Z, 2f);
        }
        if (m.Moving)
        {
            m.Advance(now);
            map.Moved(m);
            if (now >= m.EndMs)
            {
                m.Moving = false;
                if (m.Target == null) SendStop(m, now);
            }
        }
        if (m.PetTarget != null)
        {
            FightPet(m, map, now);
            return;
        }
        if (m.Target != null)
        {
            Fight(m, map, now);
            return;
        }
        if (m.Template.Aggressive && m.Viewers.Count > 0 && !m.Returning)
        {
            var near = NearestTarget(m);
            if (near != null)
            {
                Engage(m, near);
                return;
            }
        }
        if (m.Returning && !m.Moving)
        {
            m.Returning = false;
            if (m.Hp < m.Template.MaxHp)
            {
                m.Hp = m.Template.MaxHp;
                SendHealth(m);
            }
        }
        // Roam only where somebody can see it.
        if (!m.Moving && m.Template.RoamDistance > 0 && now >= m.NextRoamMs)
        {
            m.NextRoamMs = now + m.Template.RoamTimeMs + _random.Next(-m.Template.RoamTimeMs / 4, m.Template.RoamTimeMs / 4);
            if (m.Viewers.Count > 0) Roam(m, map);
        }
    }

    /// <summary>Totem Icebound Protection: apply a timed movement reduction to one attacking monster.</summary>
    public async Task ApplySlowAsync(MonsterUnit m, int reductionBp, int durationMs)
    {
        if (m.Dead || m.Template.SpeedCm <= 0 || reductionBp <= 0 || durationMs <= 0) return;
        long now = NowMs;
        m.SlowMovementBp = Math.Max(m.SlowMovementExpireMs > now ? m.SlowMovementBp : 0, Math.Clamp(reductionBp, 0, 9000));
        m.SlowMovementExpireMs = Math.Max(m.SlowMovementExpireMs, now + durationMs);
        if (m.Moving)
        {
            m.Advance(now);
            m.Moving = false;
            await _world.MoveUnitAsync(m, Stop(m, now));
        }
        m.NextChaseMs = 0;
        await _world.ToUnitViewersAsync(m, CombatService.AoiAttrChange, SpeedChange(m, now));
        if (m.Returning && m.Target == null) WalkTo(m, _world.Map(m.WorldScene), m.Home.X, m.Home.Z, 2f);
    }

    private static AOIAttrChange SpeedChange(MonsterUnit m, long now) => new()
    {
        EntityUuid = (ulong)m.Uid,
        Property = { new AoIProperty { Idx = 113, Data = m.SpeedCm(now), BaseData = m.Template.SpeedCm } },
    };

    /// <summary>A guardian's speed change, or null to return to the template. A unit that moves keeps at least 1 cm/s:
    /// a walk divides by its speed.</summary>
    internal async Task SetBuffSpeedAsync(MonsterUnit monster, int? speed)
    {
        int? effective = speed is { } value ? Math.Max(monster.Template.SpeedCm > 0 ? 1 : 0, value) : null;
        bool changed = (effective ?? monster.Template.SpeedCm) != (monster.BuffSpeedCm ?? monster.Template.SpeedCm);
        monster.BuffSpeedCm = effective;
        if (!changed) return;
        long now = NowMs;
        if (monster.Moving)
        {
            monster.Advance(now);
            monster.Moving = false;
            await _world.MoveUnitAsync(monster, Stop(monster, now));
        }
        monster.NextChaseMs = 0;
        await _world.ToUnitViewersAsync(monster, CombatService.AoiAttrChange, SpeedChange(monster, now));
    }

    /// <summary>A player hit it (or came into view of an aggressive one): chase and attack.</summary>
    public void Engage(MonsterUnit m, GameSession target)
    {
        if (m.Dead || m.WorldScene != target.WorldScene || TargetVisible?.Invoke(m, target) == false || m.Template.SpeedCm == 0 && m.Template.AttackRange <= 0) return;
        if (m.Target == null) m.NextSwingMs = NowMs + 300;
        SetTarget(m, target);
        m.Returning = false;
    }

    /// <summary>Authoritative target transitions, consumed by hatred/lock notifications and facing effects.</summary>
    public List<Func<MonsterUnit, GameSession?, GameSession?, Task>> TargetChanged { get; } = new();

    private void SetTarget(MonsterUnit monster, GameSession? target)
    {
        var previous = monster.Target;
        if (ReferenceEquals(previous, target)) return;
        monster.Target = target;
        foreach (var handler in TargetChanged) _loop.TrackRuntimeDetached("monster target change", () => handler(monster, previous, target));
    }

    /// <summary>A tower pet's attack can draw aggro without losing the player fallback (inferred).</summary>
    public void Engage(MonsterUnit m, TowerPetUnit target)
    {
        if (m.Dead || m.Hp <= 0 || target.Dead || target.Hp <= 0 || m.InstanceId == 0 || m.WorldScene != target.WorldScene) return;
        if (m.PetTarget == null) m.NextSwingMs = NowMs + 300;
        m.PetTarget = target;
        m.Returning = false;
    }

    private void FightPet(MonsterUnit m, MapInstance map, long now)
    {
        var target = m.PetTarget!;
        if (target.Dead || target.Hp <= 0 || target.Owner.SceneLoading || target.WorldScene != m.WorldScene
            || Distance(m.Home.X, m.Home.Z, target.X, target.Z) > m.Template.LeashDistance)
        {
            m.PetTarget = null;
            if (m.Target == null) Disengage(m, map);
            return;
        }
        if (m.BusyUntilMs > now)
        {
            if (m.Moving)
            {
                m.Moving = false;
                SendStop(m, now);
            }
            return;
        }
        float dist = Distance(m.X, m.Z, target.X, target.Z);
        // A tower boss uses its specials on the pets it fights (inferred: the Pet Tower bosses run the MVP scripts).
        if (m.Template.Skills != null && !m.SpecialsOwnedByMvp && TryBossSkill(m, null, target, dist, now)) return;
        if (dist <= m.Template.AttackRange + 0.5f)
        {
            if (m.Moving)
            {
                m.Moving = false;
                SendStop(m, now);
            }
            if (now >= m.NextSwingMs && AttackPet != null)
            {
                m.NextSwingMs = now + m.Template.SwingIntervalMs;
                m.Face = Angle(m.X, m.Z, target.X, target.Z);
                _loop.TrackRuntimeDetached("monster pet attack", () => AttackPet(m, target));
            }
            return;
        }
        if (m.Template.SpeedCm <= 0) return;
        if (!m.Moving || now >= m.NextChaseMs)
        {
            m.NextChaseMs = now + 400;
            float dx = target.X - m.X, dz = target.Z - m.Z;
            float go = Math.Min(MaxSegment, dist - m.Template.AttackRange * 0.8f);
            if (go > 0.2f) WalkTo(m, map, m.X + dx / dist * go, m.Z + dz / dist * go, 1f);
        }
    }

    private void Fight(MonsterUnit m, MapInstance map, long now)
    {
        var target = m.Target!;
        bool lost = target.Role == null || target.Dead || target.SceneLoading || TargetVisible?.Invoke(m, target) == false || target.WorldScene != m.WorldScene
                    || Distance(m.Home.X, m.Home.Z, target.Position.X, target.Position.Z) > m.Template.LeashDistance;
        if (lost)
        {
            Disengage(m, map);
            return;
        }
        if (m.BusyUntilMs > now)
        {
            // A special skill is being cast: the boss holds its ground and does not swing.
            if (m.Moving)
            {
                m.Moving = false;
                SendStop(m, now);
            }
            return;
        }
        float dist = Distance(m, target);
        if (m.Template.Skills != null && !m.SpecialsOwnedByMvp && TryBossSkill(m, target, null, dist, now)) return;
        float reach = m.Template.AttackRange + 0.5f;
        if (dist <= reach)
        {
            if (m.Moving)
            {
                m.Moving = false;
                SendStop(m, now);
            }
            if (now >= m.NextSwingMs && Attack != null)
            {
                m.NextSwingMs = now + m.Template.SwingIntervalMs;
                m.Face = Angle(m.X, m.Z, target.Position.X, target.Position.Z);
                _loop.TrackRuntimeDetached("monster attack", () => Attack(m, target));
            }
            return;
        }
        if (m.Template.SpeedCm <= 0) return;
        // Chase: re-path toward the target every 400 ms or when the segment ended.
        if (!m.Moving || now >= m.NextChaseMs)
        {
            m.NextChaseMs = now + 400;
            float dx = target.Position.X - m.X, dz = target.Position.Z - m.Z;
            float len = MathF.Sqrt(dx * dx + dz * dz);
            float go = Math.Min(MaxSegment, len - m.Template.AttackRange * 0.8f);
            if (go > 0.2f) WalkTo(m, map, m.X + dx / len * go, m.Z + dz / len * go, 1f);
        }
    }

    private void Disengage(MonsterUnit m, MapInstance map)
    {
        EndBossSkills(m, interrupt: true);
        SetTarget(m, null);
        m.PetTarget = null;
        m.Returning = true;
        if (m.Template.SpeedCm > 0) WalkTo(m, map, m.Home.X, m.Home.Z, 2f); // buff 901501: +100 % MSPD on the way back
    }

    private void Roam(MonsterUnit m, MapInstance map)
    {
        double angle = _random.NextDouble() * Math.PI * 2;
        float r = (float)(_random.NextDouble() * m.Template.RoamDistance);
        WalkTo(m, map, m.Home.X + (float)Math.Cos(angle) * r, m.Home.Z + (float)Math.Sin(angle) * r, 1f);
    }

    /// <summary>One straight Walk segment when the nav grid allows it.</summary>
    private void WalkTo(MonsterUnit m, MapInstance map, float x, float z, float speedFactor)
    {
        if (Forbidden?.Invoke(m, ForbiddenState.Walk) == true) return;
        var nav = _worldData.Map(m.SceneEntry)?.Nav;
        float? y = nav?.WalkHeight(x, z, m.Y) ?? m.Y;
        if (y == null || nav != null && !nav.CanWalk(m.X, m.Y, m.Z, x, z)) return;
        float dx = x - m.X, dz = z - m.Z, len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 0.1f) return;
        long now = NowMs;
        float speed = m.SpeedCm(now) / 100f * speedFactor;
        m.FromX = m.X; m.FromY = m.Y; m.FromZ = m.Z;
        m.ToX = x; m.ToY = y.Value; m.ToZ = z;
        m.StartMs = now;
        m.EndMs = now + (long)Math.Max(1, len / speed * 1000);
        m.SegmentSpeed = speed;
        m.Moving = true;
        m.Face = Angle(m.X, m.Z, x, z);
        _loop.TrackRuntimeDetached("monster move", () => _world.MoveUnitAsync(m, new MovementChangeNtf
        {
            Uid = m.Uid,
            TransType = MovementTransitionType.MoveTransNormal,
            MovementInfo = new MovementInfo
            {
                Pos = new Vec3 { X = m.X, Y = m.Y, Z = m.Z },
                ServerTime = now,
                MoveDuration = (int)(m.EndMs - now),
                Face = m.Face,
                MoveType = MovementType.MoveTypeWalk,
                MovementWalk = new MovementWalk { Dir = m.Face, Speed = speed, Acc = 0, EndHeight = y.Value },
            },
        }));
    }

    private static MovementChangeNtf Stop(MonsterUnit m, long now) => new()
    {
        Uid = m.Uid,
        TransType = MovementTransitionType.MoveTransNormal,
        MovementInfo = new MovementInfo { Pos = m.Pos, ServerTime = now, Face = m.Face, MoveType = MovementType.MoveTypeNone },
    };

    public static AOIAttrChange Hp(MonsterUnit m) => new()
    {
        EntityUuid = (ulong)m.Uid,
        Property = { new AoIProperty { Idx = 2000, Data = m.Dead ? 0 : m.Hp } },
    };

    // ---- death and respawn ----

    /// <summary>
    /// HP reached 0: HP 0, the Dead state to every viewer, the corpse removed after
    /// _iBodyRemainTime, a replacement after the group's respawn time.
    /// </summary>
    public async Task KillAsync(MonsterUnit m)
    {
        if (m.Dead) return;
        m.Dead = true;
        m.Moving = false;
        EndBossSkills(m, interrupt: true);
        SetTarget(m, null);
        m.PetTarget = null;
        await _world.ToUnitViewersAsync(m, CombatService.AoiAttrChange, Hp(m));
        await _world.ToUnitViewersAsync(m, CombatService.EntityBaseChange, m.StateChange());
        if (m.RuntimeMap.Kind != RuntimeMapKind.PublicLine)
        {
            // Private/event room ownership stays with InstanceService; its one-shot corpse timer does not
            // retain a public-line spawn state after room teardown.
            _loop.ScheduleLegacy(TimeSpan.FromMilliseconds(m.Template.BodyRemainMs + 200), () => _world.RemoveUnitAsync(m));
            return;
        }
        if (!_spawns.TryGetValue(m.RuntimeMap, out var state) || state.Retired) return;
        Schedule(state, TimeSpan.FromMilliseconds(m.Template.BodyRemainMs + 200), async () =>
        {
            if (state.Retired) return;
            if (!state.Units.Remove(m)) return;
            await _world.RemoveUnitAsync(m);
            if (state.Retired) return;
            if (m.Group.Id < 0) return; // single spawn: no group respawn
            if (state.UsedPoints.TryGetValue(m.Group.Id, out var used)) used.Remove(m.Home.Id);
            Schedule(state, TimeSpan.FromSeconds(Math.Max(1, m.Group.RespawnSeconds)), async () =>
            {
                if (state.Retired || _worldData.Map(m.SceneEntry) is not { } map) return;
                await SpawnAsync(m.RuntimeMap, state, map, m.Group, m.Template.NpcId);
            });
        });
    }

    public static float Distance(WorldUnit u, GameSession p) => Distance(u.X, u.Z, p.Position.X, p.Position.Z);

    public static float Distance(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Facing in degrees as WalkReq.dir (0 = +z, clockwise toward +x).</summary>
    public static float Angle(float fromX, float fromZ, float toX, float toZ)
    {
        float deg = MathF.Atan2(toX - fromX, toZ - fromZ) * 180f / MathF.PI;
        return deg < 0 ? deg + 360 : deg;
    }
}
