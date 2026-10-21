using System.Globalization;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Scene and unit presentation owned by the server. Persistent state is replayed only after 31037 AOI bases.
/// The wire shapes and client consumers are in LC_Scene_Sync.lua:4541-4558,5100-5202,
/// 7598-7670,8542-8588,8790-8827,9082-9133,9309-9354. Encounter-specific producers are not inferred here.
/// </summary>
public sealed class WorldEffectsService
{
    public const uint Pendant = 31015, HitRules = 31023, ForceFields = 31025, Animation = 31026,
        BossShout = 31028, Camp = 31070, Enforce = 31075, BeEnforced = 31076,
        Turning = 31085, LocalFace = 31086, MovementConfig = 31091;

    private sealed class UnitEffects
    {
        public EntityEnforceInfo? EnforceInfo;
        public long[]? BeEnforcedUids;
        public Dictionary<int, PendantData>? Pendants;
        public int[]? HitDecorateRules;
        public Dictionary<int, int>? Animations;
    }

    private readonly World _world;
    private readonly GameData _data;
    private readonly GameLoop _loop;
    private readonly Dictionary<long, Dictionary<uint, ForceFieldInfo>> _fields = new();
    private readonly Dictionary<long, long> _latency = new();
    private readonly Dictionary<MonsterUnit, UnitEffects> _units = new();

    public WorldEffectsService(World world, GameData data, SceneService scene, MonsterService monsters,
        GmCommands gm, GameLoop loop, ILogger<WorldEffectsService> log)
    {
        this._world = world;
        this._data = data;
        this._loop = loop;
        scene.AfterLoaded.Add(ReplayAsync);
        scene.Resync.Add(ReplayAsync); // presentation only: the reconnected client lost the scene's latency/force fields
        world.UnitEntered.Add(UnitEnteredAsync);
        world.UnitRemoved.Add(RemovedAsync);
        monsters.TargetChanged.Add(TargetChangedAsync);
        gm.Register("worldlatency", "<0..2000 ms>", SetLatencyCommandAsync);
        gm.Register("worldface", "<0..360 degrees>", FaceCommandAsync);
        gm.Register("bossshout", "<visible boss uid> <ChatConfig id>", BossShoutCommandAsync);
        gm.Register("forcefield", "<id> <radius> <direction> <speed> <seconds>", ForceFieldCommandAsync);
        gm.Register("removeforcefield", "<id>", RemoveForceFieldCommandAsync);
        log.LogInformation("World effect lifecycle registered");
    }

    private static bool Finite(float value) => float.IsFinite(value);
    private static bool FiniteVec(Vec3? value) => value != null && Finite(value.X) && Finite(value.Y) && Finite(value.Z);
    private static bool ValidScene(GameSession s) => s.Role != null && !s.SceneLoading;
    private bool Active(MonsterUnit unit) => !unit.Dead && _world.AllUnits().Contains(unit);
    private static bool Angle(float value) => Finite(value) && value >= 0 && value <= 360;

    /// <summary>31091 is per scene and replayed after scene load. The client uses (ms + 75)/1000 smoothing.</summary>
    public async Task<bool> SetMovementLatencyAsync(long worldScene, long milliseconds)
    {
        if (milliseconds is < 0 or > 2000) return false;
        _latency[worldScene] = milliseconds;
        var packet = new MovementConfigSyncNtf();
        packet.Params["movement_latency_ms"] = milliseconds;
        foreach (var player in _world.PlayersIn(worldScene)) await player.SendGameAsync(MovementConfig, packet);
        return true;
    }

    /// <summary>31025: only customField is consumed by the Lua client. ID is unique within a world scene.</summary>
    public async Task<bool> PutForceFieldAsync(long worldScene, ForceFieldInfo field, TimeSpan lifetime)
    {
        if (field.Uuid == 0 || field.Remove || field.CustomField is not { } custom
            || custom.FieldType is not (ForceFieldType.Target or ForceFieldType.Direction)
            || custom.FieldType == ForceFieldType.Direction && !Angle(custom.ForceDir)
            || custom.FieldType == ForceFieldType.Target && !FiniteVec(custom.ForceTargetPos)
            || !Finite(custom.ForceSpeed) || custom.ForceSpeed <= 0 || custom.ForceSpeed > 100
            || custom.ForceRangeInfo is not { } range || !ValidRange(range)
            || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(5)) return false;
        if (!_fields.TryGetValue(worldScene, out var scene)) _fields[worldScene] = scene = new();
        if (scene.ContainsKey(field.Uuid)) return false;
        var copy = field.Clone();
        scene.Add(copy.Uuid, copy);
        await BroadcastSceneAsync(worldScene, ForceFields, new ForceFieldNotify { ForceFields = { copy } });
        _loop.ScheduleLegacy(lifetime, () => RemoveForceFieldAsync(worldScene, copy.Uuid, copy));
        return true;
    }

    private static bool ValidRange(ShapeRange range) => range.Type switch
    {
        RangeType.Circle => range.CircleRange is { } circle && FiniteVec(circle.Center) && Finite(circle.Radius) && circle.Radius is > 0 and <= 100,
        RangeType.Rect => range.RectRange is { } rect && FiniteVec(rect.Center) && Angle(rect.Angle) && Finite(rect.Length) && Finite(rect.Height) && rect.Length is > 0 and <= 200 && rect.Height is > 0 and <= 200,
        RangeType.CircularSector => range.SectorRange is { } sector && FiniteVec(sector.Center) && Angle(sector.Face) && Finite(sector.Section) && sector.Section is > 0 and <= 360 && Finite(sector.Radius) && sector.Radius is > 0 and <= 100,
        _ => false,
    };

    public Task<bool> RemoveForceFieldAsync(long worldScene, uint id) => RemoveForceFieldAsync(worldScene, id, null);

    private async Task<bool> RemoveForceFieldAsync(long worldScene, uint id, ForceFieldInfo? expected)
    {
        if (!_fields.TryGetValue(worldScene, out var scene) || !scene.TryGetValue(id, out var current)
            || expected != null && !ReferenceEquals(expected, current)) return false;
        scene.Remove(id);
        if (scene.Count == 0) _fields.Remove(worldScene);
        await BroadcastSceneAsync(worldScene, ForceFields, new ForceFieldNotify
        { ForceFields = { new ForceFieldInfo { Uuid = id, Remove = true } } });
        return true;
    }

    /// <summary>31028 is transient text. The ID must resolve through ChatConfig; no historical trigger is inferred.</summary>
    public async Task<bool> ShoutAsync(MonsterUnit boss, int chatId)
    {
        if (!Active(boss) || _data.Row("ChatConfig", chatId) is not { } chat ||
            _data.Row("NPCConfig", boss.Template.NpcId) is not { } npc ||
            GameData.Int(npc, "_iNPCSubType") is not (3 or 4 or 6)) return false;
        string content = _data.TextOf(chat, "_iContent");
        // MessagePush has no parameter payload on 31028; templated messages belong to 1022.
        if (string.IsNullOrWhiteSpace(content) || content.Contains("${", StringComparison.Ordinal)
            || content.Contains("@{", StringComparison.Ordinal) || content.Contains("^{", StringComparison.Ordinal)) return false;
        await _world.ToUnitViewersAsync(boss, BossShout, new SceneBossShoutNotify
        { MonsterId = boss.Template.NpcId, ShoutId = chatId });
        return true;
    }

    /// <summary>31085 stops or starts a server-owned monster turn and updates its AOI base facing.</summary>
    public async Task<bool> TurnAsync(MonsterUnit unit, float face, float degreesPerSecond = 0, int durationMs = 0)
    {
        if (!Active(unit) || !Angle(face) || !Finite(degreesPerSecond) || degreesPerSecond < 0 || durationMs < 0 || durationMs > 30000) return false;
        unit.Face = face;
        await _world.ToUnitViewersAsync(unit, Turning, new ContinueTurningChangeNtf
        {
            Uid = unit.Uid, TurningInfo = new ContinueTurningInfo
            { Face = face, ServerTime = MonsterService.NowMs, AngleSpeed = degreesPerSecond, TurnDuration = durationMs },
        });
        return true;
    }

    private Task TargetChangedAsync(MonsterUnit monster, GameSession? previous, GameSession? target)
    {
        if (target == null || monster.Moving || target.WorldScene != monster.WorldScene) return Task.CompletedTask;
        float face = MonsterService.Angle(monster.X, monster.Z, target.Position.X, target.Position.Z);
        return TurnAsync(monster, face);
    }

    /// <summary>31086 changes only the controlled local player; 31014 relays the same authoritative face to others.</summary>
    public async Task<bool> SetLocalFaceAsync(GameSession player, float face)
    {
        if (!ValidScene(player) || !Angle(face)) return false;
        player.Face = face;
        await player.SendGameAsync(LocalFace, new LocalPlayerFaceChangeNtf { Face = face });
        await _world.TurnAsync(player, face);
        return true;
    }

    public async Task<bool> SetEnforceAsync(MonsterUnit unit, EntityEnforceInfo info)
    {
        if (!Active(unit) || info.EnforceType < 0 || !Finite(info.EnforceSpeed) || info.EnforceSpeed < 0
            || !Finite(info.EnforceDir) || info.Size.Any(v => !Finite(v) || v < 0)) return false;
        Effects(unit).EnforceInfo = info.Clone();
        await _world.ToUnitViewersAsync(unit, Enforce, new EntityEnforceChange { Uuid = unit.Uid, EnforceInfo = info });
        return true;
    }

    public async Task<bool> SetBeEnforcedAsync(MonsterUnit unit, IReadOnlyCollection<long> sourceUids)
    {
        if (!Active(unit) || sourceUids.Count > 32 || sourceUids.Any(id => id <= 0 ||
            !_world.AllUnits().Any(other => other.Uid == id && other.WorldScene == unit.WorldScene))) return false;
        Effects(unit).BeEnforcedUids = sourceUids.ToArray();
        await _world.ToUnitViewersAsync(unit, BeEnforced, new EntityBeEnforcedChange
        { Uuid = unit.Uid, BeEnforcedUids = { sourceUids } });
        return true;
    }

    public async Task<bool> SetPendantsAsync(MonsterUnit unit, IReadOnlyDictionary<int, PendantData> pendants)
    {
        if (!Active(unit) || pendants.Count > 16 || pendants.Any(p => _data.Row("PendantConfig", p.Key) == null
            || p.Value.PendantId != p.Key || p.Value.Piles < 0)) return false;
        Effects(unit).Pendants = pendants.ToDictionary(p => p.Key, p => p.Value.Clone());
        var packet = new PendantUpdate { EntityID = unit.Uid };
        foreach (var (id, pendant) in pendants) packet.PendantInfo[id] = pendant;
        await _world.ToUnitViewersAsync(unit, Pendant, packet);
        return true;
    }

    public async Task<bool> SetHitRulesAsync(MonsterUnit unit, IReadOnlyCollection<int> rules)
    {
        if (!Active(unit) || rules.Count > 32 || rules.Any(id => id < 0)) return false;
        Effects(unit).HitDecorateRules = rules.ToArray();
        await _world.ToUnitViewersAsync(unit, HitRules, new HitDecorateRulesNotify
        { EntityId = unit.Uid, HitDecorateRules = { rules } });
        return true;
    }

    public async Task<bool> SetAnimationOverridesAsync(MonsterUnit unit, IReadOnlyDictionary<int, int> overrides)
    {
        if (!Active(unit) || overrides.Count > 64 || overrides.Any(p => p.Key < 0 || p.Value < 0)) return false;
        Effects(unit).Animations = overrides.ToDictionary(p => p.Key, p => p.Value);
        var packet = new UpdateEntityAnimation { EntityID = unit.Uid };
        foreach (var (id, animation) in overrides) packet.AnimationInfo[id] = animation;
        await _world.ToUnitViewersAsync(unit, Animation, packet);
        return true;
    }

    /// <summary>31070 updates the monster's AOI camp as well as current viewers. Combat relation AI is separate.</summary>
    public async Task<bool> SetCampAsync(MonsterUnit unit, int camp)
    {
        if (!Active(unit) || camp <= 0) return false;
        unit.CampOverride = camp;
        await _world.ToUnitViewersAsync(unit, Camp, new EntityChangeCampNotify { EntityId = unit.Uid, Camp = camp });
        return true;
    }

    private UnitEffects Effects(MonsterUnit unit) => _units.TryGetValue(unit, out var state) ? state : _units[unit] = new();
    private Task RemovedAsync(WorldUnit unit) { if (unit is MonsterUnit monster) _units.Remove(monster); return Task.CompletedTask; }

    private async Task ReplayAsync(GameSession player)
    {
        if (_latency.TryGetValue(player.WorldScene, out long ms))
        {
            var packet = new MovementConfigSyncNtf();
            packet.Params["movement_latency_ms"] = ms;
            await player.SendGameAsync(MovementConfig, packet);
        }
        if (_fields.TryGetValue(player.WorldScene, out var scene) && scene.Count > 0)
            await player.SendGameAsync(ForceFields, new ForceFieldNotify { ForceFields = { scene.Values } });
    }

    private async Task UnitEnteredAsync(GameSession player, WorldUnit entered)
    {
        if (entered is not MonsterUnit unit) return;
        if (!player.VisibleUnits.Contains(unit)) return;
        if (!_units.TryGetValue(unit, out var state)) return;
        if (state.EnforceInfo != null) await player.SendGameAsync(Enforce, new EntityEnforceChange { Uuid = unit.Uid, EnforceInfo = state.EnforceInfo });
        if (state.BeEnforcedUids != null) await player.SendGameAsync(BeEnforced, new EntityBeEnforcedChange { Uuid = unit.Uid, BeEnforcedUids = { state.BeEnforcedUids } });
        if (state.Pendants != null) { var packet = new PendantUpdate { EntityID = unit.Uid }; packet.PendantInfo.Add(state.Pendants); await player.SendGameAsync(Pendant, packet); }
        if (state.HitDecorateRules != null) await player.SendGameAsync(HitRules, new HitDecorateRulesNotify { EntityId = unit.Uid, HitDecorateRules = { state.HitDecorateRules } });
        if (state.Animations != null) { var packet = new UpdateEntityAnimation { EntityID = unit.Uid }; packet.AnimationInfo.Add(state.Animations); await player.SendGameAsync(Animation, packet); }
    }

    private async Task BroadcastSceneAsync(long worldScene, uint msg, Google.Protobuf.IMessage payload)
    {
        foreach (var player in _world.PlayersIn(worldScene)) await player.SendGameAsync(msg, payload);
    }

    private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    private static uint U(string s) => uint.Parse(s, CultureInfo.InvariantCulture);
    private static long L(string s) => long.Parse(s, CultureInfo.InvariantCulture);
    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    private async Task SetLatencyCommandAsync(GameSession s, string[] args)
    {
        if (!ValidScene(s) || args.Length != 1 || !await SetMovementLatencyAsync(s.WorldScene, L(args[0]))) throw new ArgumentException("latency");
        await GmCommands.ReplyAsync(s, "Movement latency updated for this scene.");
    }
    private async Task FaceCommandAsync(GameSession s, string[] args)
    {
        if (args.Length != 1 || !await SetLocalFaceAsync(s, F(args[0]))) throw new ArgumentException("face");
        await GmCommands.ReplyAsync(s, "Facing updated.");
    }
    private async Task BossShoutCommandAsync(GameSession s, string[] args)
    {
        if (!ValidScene(s) || args.Length != 2) throw new ArgumentException("boss shout");
        var boss = _world.AllUnits().OfType<MonsterUnit>().FirstOrDefault(m => m.Uid == L(args[0]) && m.WorldScene == s.WorldScene && m.Viewers.Contains(s));
        if (boss == null || !await ShoutAsync(boss, I(args[1]))) throw new ArgumentException("visible boss or ChatConfig id");
        await GmCommands.ReplyAsync(s, "Boss shout sent.");
    }
    private async Task ForceFieldCommandAsync(GameSession s, string[] args)
    {
        if (!ValidScene(s) || args.Length != 5) throw new ArgumentException("force field");
        uint id = U(args[0]); float radius = F(args[1]), direction = F(args[2]), speed = F(args[3]); int seconds = I(args[4]);
        if (!Angle(direction) || seconds is < 1 or > 300) throw new ArgumentException("direction or lifetime");
        var field = new ForceFieldInfo
        {
            Uuid = id, CustomField = new CustomForceField
            {
                FieldType = ForceFieldType.Direction, ForceDir = direction, ForceSpeed = speed,
                ForceRangeInfo = new ShapeRange
                {
                    Type = RangeType.Circle,
                    CircleRange = new CircleShape { Center = s.Position.Clone(), Radius = radius }
                }
            }
        };
        if (!await PutForceFieldAsync(s.WorldScene, field, TimeSpan.FromSeconds(seconds))) throw new ArgumentException("field shape or duplicate id");
        await GmCommands.ReplyAsync(s, "Force field active.");
    }
    private async Task RemoveForceFieldCommandAsync(GameSession s, string[] args)
    {
        if (!ValidScene(s) || args.Length != 1 || !await RemoveForceFieldAsync(s.WorldScene, U(args[0]))) throw new ArgumentException("field id");
        await GmCommands.ReplyAsync(s, "Force field removed.");
    }
}
