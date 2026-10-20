using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Client dungeon joins: DungeonEntranceConfig -> TenGameplayConfig._kBoss[markId][2] ->
/// TenGameplayBossConfig._iDungeon -> DungeonConfig/map/editor (Lua_CFG_TenGameplayConfig.lua:107-129).
/// Mark is the one-based array position, not tuple[1] (the fourth raid tuple also says 3).
/// </summary>
public sealed class DungeonCatalog(GameData data, WorldData worldData, MonsterService monsters,
    SeasonService seasons, IOptions<ServerOptions> options, ReconstructedDungeons? reconstructed = null)
{
    public sealed record Spawn(int NpcId, MapPoint Point);
    /// <summary>
    /// A wave; <paramref name="Quest"/> is the DungeonQuestConfig quest it plays (0: none known). A scripted step
    /// (<paramref name="Step"/>, ReconstructedDungeons.Scripted) has no monsters: it completes on its client events.
    /// </summary>
    public sealed record Wave(int Number, IReadOnlyList<Spawn> Spawns, int Quest = 0, ReconstructedDungeons.ScriptedStep? Step = null);
    /// <summary><paramref name="Layer"/>: a Valhalla Trials run (HeroicSpiritLayerConfig row), see ValhallaService.</summary>
    public sealed record Plan(JsonElement Config, JsonElement? Multi, JsonElement? Boss, int Mark,
        int MinPlayers, int MaxPlayers, IReadOnlyList<Wave> Waves, JsonElement? Layer = null)
    {
        public long LayerId => Layer is { } row ? GameData.Int(row, "_iID") : 0;
        public long SceneEntry => GameData.Int(Config, "_iID");
        public int MultiId => Multi is { } row ? (int)GameData.Int(row, "_iID") : 0;
        public int DurationMs => (int)Math.Clamp(GameData.Int(Config, "_iLimitTime"), 1000, int.MaxValue);
        /// <summary>A Hidden Realm run: the random entry (TenGameplayConfig _iIsRandomDungeon) this boss was drawn for.</summary>
        public RandomOrigin? Origin { get; init; }
        /// <summary>The entry whose rewards, allowance and record the run counts against: the Hidden Realm's own row when
        /// drawn from it (inferred: its boss row 4300099 carries the awards and _iMatchID), else the fought boss's.</summary>
        public JsonElement? RewardMulti => Origin?.Multi ?? Multi;
        public JsonElement? RewardBoss => Origin?.Boss ?? Boss;
        public int RewardMark => Origin?.Mark ?? Mark;
        public int RewardMultiId => RewardMulti is { } row ? (int)GameData.Int(row, "_iID") : 0;
        public DungeonInitData Init(long teamId = 0, long instanceId = 0)
        {
            var init = LayerId != 0
                // The client never reads it (LC_DunGeon_Valhalla); filled as the proto defines it (inferred).
                ? new DungeonInitData { HeroicSpiritDungeonInitData = new HeroicSpiritDungeonInitData { ChallengeId = LayerId } }
                : MultiId == 0 ? new DungeonInitData()
                : new DungeonInitData
                {
                    MultiDungeonInitData = new MultiDungeonInitData
                    { MultiDungeonId = MultiId, MarkId = Mark, TeamID = teamId, DungeonId = instanceId }
                };
            if (Origin is { } origin && init.MultiDungeonInitData != null)
                init.MultiDungeonInitData.Origin = new MultiDungeonInfo { MultiDungeonId = GameData.Int(origin.Multi, "_iID"), MarkId = origin.Mark };
            return init;
        }
    }

    public sealed record RandomOrigin(JsonElement Multi, JsonElement Boss, int Mark);

    public IEnumerable<JsonElement> MultiRows => data.Table("TenGameplayConfig").Values
        .Where(r => TowerService.FitsPlayBook(r, seasons.PlayBook));

    public JsonElement? Boss(JsonElement multi, int mark)
    {
        if (!multi.TryGetProperty("_kBoss", out var list) || list.ValueKind != JsonValueKind.Array
            || mark < 1 || mark > list.GetArrayLength()) return null;
        var tuple = list[mark - 1];
        return tuple.ValueKind == JsonValueKind.Array && tuple.GetArrayLength() >= 2
            ? data.Row("TenGameplayBossConfig", (long)(GameData.Number(tuple[1]) ?? 0)) : null;
    }

    public static int Marks(JsonElement multi) => multi.TryGetProperty("_kBoss", out var list)
        && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;

    public JsonElement? Refresh(JsonElement multi) => data.Table("TenGameplayRefreshTimeConfig").Values
        .Where(r => GameData.Int(r, "_iGroupID") == GameData.Int(multi, "_iGroup"))
        .Select(r => (JsonElement?)r).FirstOrDefault();

    /// <summary>Group/family accounting is (inferred); Normal and Hard raw counters are distinct in the Lua DAL.</summary>
    public static string AllowanceKey(JsonElement multi, int mark) =>
        $"{GameData.Int(multi, "_iGroup")}:{GameData.Number(multi.GetProperty("_kBoss")[mark - 1][2])}:{GameData.Int(multi, "_iDifficulty")}";

    /// <summary>Lua_CFG_TenGameplayConfig:169-204 matches _iType, not _iRelation. Lowest-id tie order is (inferred).</summary>
    public JsonElement? HardCounterpart(JsonElement multi) => MultiRows
        .Where(r => GameData.Int(r, "_iType") == GameData.Int(multi, "_iType") && GameData.Int(r, "_iDifficulty") == 2)
        .OrderBy(r => GameData.Int(r, "_iID")).Select(r => (JsonElement?)r).FirstOrDefault();

    public Plan? Resolve(EnterDungeonReq req)
    {
        if (data.Row("DungeonConfig", req.SceneEntry) is not { } config
            || worldData.Map(req.SceneEntry) is not { Born: not null }) return null;
        // Separate owners handle tower, PvP, AFK and activity init branches; never accept forged init data.
        var init = req.InitData?.Clone() ?? new DungeonInitData();
        var selected = init.MultiDungeonInitData;
        init.MultiDungeonInitData = null;
        if (init.CalculateSize() != 0 || req.Param != null && req.Param.CalculateSize() != 0) return null;
        JsonElement? multi = null, boss = null;
        int mark = 0, min = 1, max = (int)GameData.Int(config, "_iRoleCntLimit");
        long mode = GameData.Int(config, "_iGamePlayType"), kind = GameData.Int(config, "_iDungeonType");
        if (selected != null)
        {
            multi = MultiRows.Where(r => GameData.Int(r, "_iID") == selected.MultiDungeonId).Select(r => (JsonElement?)r).FirstOrDefault();
            if (multi == null || selected.IsMatch || selected.NeedCreateTeam || selected.Origin != null) return null;
            mark = selected.MarkId;
            boss = Boss(multi.Value, mark);
            if (boss == null || GameData.Int(boss.Value, "_iDungeon") != req.SceneEntry) return null;
            var limits = multi.Value.GetProperty("_kLimit");
            min = (int)(GameData.Number(limits[0]) ?? 1);
            max = Math.Min(max, (int)(GameData.Number(limits[1]) ?? max));
        }
        // A scripted scene may also be a DanceParty (26: the opening dance, 1010601), which the client runs with the
        // plain lc_dungeon_base like an ordinary dungeon (GamePlayConfig.lua:136-140).
        else if (kind is not (1 or 2) || mode is not (6 or 24) && !(mode == 26 && reconstructed?.Scripted(req.SceneEntry) != null)
            || data.Table("TenGameplayBossConfig").Values.Any(r => GameData.Int(r, "_iDungeon") == req.SceneEntry)) return null;
        var waves = mode == 24 && selected == null ? JourneySpawns(config) : Spawns(config, boss, worldData.Map(req.SceneEntry)!);
        return max >= min && waves.Count > 0 ? new Plan(config, multi, boss, mark, min, max, waves) : null;
    }

    /// <summary>
    /// Whale's NpcLevelPointRespawnConfig supplies per-difficulty templates at authored editor points.
    /// Camp 2 whale/platform actors and the post-boss NPC are not enemies.
    /// (inferred) Defeating the explicit target completes this stage; the hidden KunBenBoss_GVE server script
    /// is not exported. A map without an authored combat target is refused, never auto-cleared.
    /// </summary>
    internal Plan? GuildChallenge(long scene, int limit)
    {
        if (data.Row("DungeonConfig", scene) is not { } config || GameData.Int(config, "_iGamePlayType") != 19
            || worldData.Map(scene) is not { Born: not null }) return null;
        string editor = config.GetProperty("_kResSceneEditorName").GetString() ?? "";
        if (SceneEditorFile(editor) is not { } path) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!doc.RootElement.TryGetProperty("m_kMonsterSpawnPoints", out var points)) return null;
        int difficulty = data.Table("GuildChallengeConfig").Values.Where(r => r.GetProperty("_kDungeonId").EnumerateArray()
            .Any(v => GameData.Number(v) == scene)).Select(r => (int)GameData.Int(r, "_iLevel")).Single();
        var spawns = new List<Spawn>();
        foreach (var binding in data.Table("NpcLevelPointRespawnConfig").Values
            // (inferred) The matching level selects the difficulty's boss. Level 0 is an alternate legacy
            // easy template; level -1 contains script-controlled mechanisms/post-boss NPCs, not an initial wave.
            .Where(r => GameData.Int(r, "_iMapid") == scene && GameData.Int(r, "_iLevel") == difficulty)
            .OrderBy(r => GameData.Int(r, "_iRespawnpointid")))
        {
            int id = (int)GameData.Int(binding, "_iNpcid");
            string pointId = GameData.Int(binding, "_iRespawnpointid").ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!points.TryGetProperty(pointId, out var point) || GameData.Int(point, "monsterType") != 0 || data.Row("NPCConfig", id) is not { } npc
                || GameData.Int(npc, "_iNPCType") != 1 || GameData.Int(npc, "_iCamp") != 3
                || GameData.Int(npc, "_iNoSelected") != 0 || monsters.Template(id) == null) continue;
            var p = point.GetProperty("position");
            spawns.Add(new(id, new MapPoint(WorldNpc.Float(p, "x"), WorldNpc.Float(p, "y"), WorldNpc.Float(p, "z"), WorldNpc.Float(point, "direction"))));
        }
        // (inferred) Recommended Players is advisory; no configured minimum was recovered. Solo entry is allowed.
        return spawns.Count == 0 ? null : new Plan(config, null, null, 0, 1,
            Math.Min(limit, (int)GameData.Int(config, "_iRoleCntLimit")), [new Wave(1, spawns)]);
    }

    /// <summary>
    /// One boss at a uniquely named "BOSS" point, else at the first ungrouped server spawn point (inferred: the
    /// encounter scripts that placed bosses are not in the client). 3020101 has no ungrouped point but authors one
    /// exact "BOSS" point (93, group 90).
    /// </summary>
    private IReadOnlyList<Wave> BossWave(int npc, JsonElement[] serverPoints, Func<JsonElement, MapPoint> place)
    {
        var tagged = serverPoints.Where(p => p.TryGetProperty("name", out var label)
            && label.ValueKind == JsonValueKind.String && string.Equals(label.GetString(), "BOSS", StringComparison.OrdinalIgnoreCase)).ToArray();
        var point = tagged.Length == 1 ? tagged[0] : serverPoints.FirstOrDefault(p => GameData.Int(p, "GroupId") == 0);
        return point.ValueKind == JsonValueKind.Object && monsters.Template(npc) != null
            ? [new Wave(1, [new Spawn(npc, place(point))])] : [];
    }

    /// <summary>
    /// A Valhalla Trials layer (HeroicSpiritLayerConfig): its dungeon (_iGamePlayType 23) with the layer's boss,
    /// _kMonsterPreview[0], as the only enemy, placed as <see cref="BossWave"/> does (inferred: the client names the
    /// boss but not its spawn point; every spawn point of these scenes has npcId 0). Solo: the panel challenges alone,
    /// although DungeonConfig allows 10 (inferred).
    /// </summary>
    internal Plan? Valhalla(JsonElement layer)
    {
        long scene = GameData.Int(layer, "_iDungeonId");
        if (data.Row("DungeonConfig", scene) is not { } config || GameData.Int(config, "_iGamePlayType") != 23
            || worldData.Map(scene) is not { Born: not null }
            || !layer.TryGetProperty("_kMonsterPreview", out var bosses) || bosses.ValueKind != JsonValueKind.Array
            || bosses.GetArrayLength() == 0) return null;
        string editor = config.GetProperty("_kResSceneEditorName").GetString() ?? "";
        if (SceneEditorFile(editor) is not { } path) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!doc.RootElement.TryGetProperty("m_kMonsterSpawnPoints", out var points) || points.ValueKind != JsonValueKind.Object) return null;
        var serverPoints = points.EnumerateObject().Select(p => p.Value.Clone())
            .Where(p => GameData.Int(p, "monsterType") == 0 && p.TryGetProperty("position", out _))
            .OrderBy(p => GameData.Int(p, "Id")).ToArray();
        var waves = BossWave((int)(GameData.Number(bosses[0]) ?? 0), serverPoints, p =>
        {
            var v = p.GetProperty("position");
            return new MapPoint(WorldNpc.Float(v, "x"), WorldNpc.Float(v, "y"), WorldNpc.Float(v, "z"), WorldNpc.Float(p, "direction"));
        });
        return waves.Count == 0 ? null : new Plan(config, null, null, 0, 1, 1, waves, layer);
    }

    /// <summary>Journey action 407 maps to the authored NPC/map/point relation, not a guessed scene actor.</summary>
    private IReadOnlyList<Wave> JourneySpawns(JsonElement config)
    {
        long scene = GameData.Int(config, "_iID");
        if (!data.Table("JourneyActConfig").Values.Any(r => r.TryGetProperty("_iAction", out var actions)
            && actions.ValueKind == JsonValueKind.Array && actions.GetArrayLength() > 0
            && actions[0].ValueKind == JsonValueKind.Array && actions[0].GetArrayLength() >= 3
            && GameData.Number(actions[0][0]) == 407 && GameData.Number(actions[0][1]) == scene)) return [];
        if (worldData.Map(scene) is not { } map) return [];
        var rows = data.Table("NpcLevelPointRespawnConfig").Values
            .Where(r => GameData.Int(r, "_iMapid") == scene && GameData.Int(r, "_iNpcid") > 0)
            .OrderBy(r => GameData.Int(r, "_iId")).ToArray();
        var spawns = new List<Spawn>();
        foreach (var row in rows)
        {
            long pointId = GameData.Int(row, "_iRespawnpointid");
            int npcId = (int)GameData.Int(row, "_iNpcid");
            if (!map.Points.TryGetValue(pointId, out var point) || monsters.Template(npcId) == null) return [];
            spawns.Add(new Spawn(npcId, point));
        }
        return spawns.Count > 0 ? [new Wave(1, spawns)] : [];
    }

    private string? SceneEditorFile(string editor) =>
        DataFiles.Find(Path.Combine(options.Value.GameDataDir ?? options.Value.DataDir, "sceneeditor"), editor + ".json");

    /// <summary>
    /// Scene-editor points carry positions, not a recovered server encounter script. The selected multi-boss at
    /// a uniquely named BOSS point, else the first ungrouped server spawn point, and sequential groups of
    /// explicit server NPCs in story maps, are
    /// (inferred). Client-only monsterType=1 actors and zero-template mechanic points are never spawned as enemies.
    /// NpcLevelPointRespawnConfig supplies NPC ids for otherwise zero-template editor points. The one exported
    /// NpcLevelRegionRespawnConfig scene supplies a weighted monster group and region geometry; evenly spreading
    /// its configured count along authored inner points is (inferred) until the server placement algorithm is known.
    /// No phase, wave delay, hidden reward or boss mechanic is fabricated from DungeonPhaseConfig's UI descriptions.
    /// </summary>
    private IReadOnlyList<Wave> Spawns(JsonElement config, JsonElement? boss, MapWorld map)
    {
        string? editor = config.TryGetProperty("_kResSceneEditorName", out var name) ? name.GetString() : null;
        if (string.IsNullOrEmpty(editor) || SceneEditorFile(editor) is not { } path) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!doc.RootElement.TryGetProperty("m_kMonsterSpawnPoints", out var points) || points.ValueKind != JsonValueKind.Object) return [];
        var serverPoints = points.EnumerateObject().Select(p => p.Value)
            .Where(p => GameData.Int(p, "monsterType") == 0 && p.TryGetProperty("position", out _))
            .OrderBy(p => GameData.Int(p, "Id")).ToArray();
        MapPoint Point(JsonElement p)
        {
            var v = p.GetProperty("position");
            return new MapPoint(WorldNpc.Float(v, "x"), WorldNpc.Float(v, "y"), WorldNpc.Float(v, "z"), WorldNpc.Float(p, "direction"));
        }
        // A scripted story scene without a fight (inferred, see ReconstructedDungeons.Scripted): steps, not monsters.
        if (boss == null && reconstructed?.Scripted(GameData.Int(config, "_iID")) is { } scripted)
            return ScriptedWaves(scripted, map);
        // A reconstructed story encounter (inferred, see ReconstructedDungeons) replaces the unresolvable
        // server-only templates: client-known monsters at authored points, one wave per quest stage.
        if (boss == null && reconstructed?.For(GameData.Int(config, "_iID")) is { } rebuilt)
        {
            var waves = new List<Wave>();
            foreach (var wave in rebuilt.Waves)
            {
                var spawns = new List<Spawn>();
                foreach (var s in wave.Spawns)
                {
                    if (!points.TryGetProperty(s.Point.ToString(System.Globalization.CultureInfo.InvariantCulture), out var p)
                        || GameData.Int(p, "monsterType") != 0 || monsters.Template(s.NpcId) == null) return [];
                    spawns.Add(new Spawn(s.NpcId, Point(p)));
                }
                waves.Add(new Wave(waves.Count + 1, spawns, wave.Quest));
            }
            return waves;
        }
        if (boss is { } b) return BossWave((int)GameData.Int(b, "_iBoss"), serverPoints, Point);
        var explicitPoints = serverPoints.Where(p => GameData.Int(p, "npcId") > 0).ToArray();
        if (explicitPoints.Any(p => monsters.Template((int)GameData.Int(p, "npcId")) == null)) return [];
        if (explicitPoints.Length > 0)
            return explicitPoints.GroupBy(p => GameData.Int(p, "GroupId")).OrderBy(g => g.Key)
                .Select((g, i) => new Wave(i + 1, g.Select(p => new Spawn((int)GameData.Int(p, "npcId"), Point(p))).ToArray())).ToArray();

        var mapped = new List<Spawn>();
        foreach (var row in data.Table("NpcLevelPointRespawnConfig").Values
            .Where(r => GameData.Int(r, "_iMapid") == GameData.Int(config, "_iID")))
        {
            int npc = (int)GameData.Int(row, "_iNpcid");
            if (monsters.Template(npc) == null) continue; // exported rows also name noncombat scene actors
            string id = GameData.Int(row, "_iRespawnpointid").ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!points.TryGetProperty(id, out var point) || GameData.Int(point, "monsterType") != 0) return [];
            mapped.Add(new Spawn(npc, Point(point)));
        }

        foreach (var row in data.Table("NpcLevelRegionRespawnConfig").Values
            .Where(r => GameData.Int(r, "_iMapid") == GameData.Int(config, "_iID")))
        {
            int regionId = (int)GameData.Int(row, "_iRespawnregionid");
            if (!doc.RootElement.TryGetProperty("m_kRegins", out var regions)
                || !regions.TryGetProperty(regionId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var region)
                || !region.TryGetProperty("innerPoints", out var inner) || inner.ValueKind != JsonValueKind.Object)
                return [];
            var inside = inner.EnumerateObject().OrderBy(p => int.TryParse(p.Name, out int n) ? n : int.MaxValue)
                .Select(p => p.Value).ToArray();
            if (inside.Length == 0 || !row.TryGetProperty("_iNpcrespawngroup", out var groups)
                || groups.ValueKind != JsonValueKind.Array) return [];
            foreach (var groupId in groups.EnumerateArray())
            {
                if (data.Row("NpcRespawnGroupConfig", (long)(GameData.Number(groupId) ?? 0)) is not { } group
                    || !group.TryGetProperty("_kNpcgroup", out var npcIds) || npcIds.ValueKind != JsonValueKind.Array
                    || npcIds.GetArrayLength() != 1) return []; // multi-NPC weight selection is unproven
                int npc = (int)(GameData.Number(npcIds[0]) ?? 0);
                int count = (int)GameData.Int(group, "_iByweightnum");
                if (monsters.Template(npc) == null || count is < 1 or > 100) return [];
                // Region inner points are authored positions. Interpolation is (inferred); all samples remain
                // on an authored segment, and the map grounds them against its navigation layer at spawn.
                for (int i = 0; i < count; i++)
                {
                    double t = count == 1 ? 0 : (double)i * (inside.Length - 1) / (count - 1);
                    int a = (int)t, next = Math.Min(a + 1, inside.Length - 1);
                    double f = t - a;
                    float Lerp(string axis) => (float)(WorldNpc.Float(inside[a], axis) * (1 - f) + WorldNpc.Float(inside[next], axis) * f);
                    mapped.Add(new Spawn(npc, map.Grounded(new MapPoint(Lerp("x"), Lerp("y"), Lerp("z"),
                        WorldNpc.Float(region, "monsterDirection")))));
                }
            }
        }
        return mapped.Count > 0 ? [new Wave(1, mapped)] : [];
    }

    /// <summary>
    /// A scripted scene's steps (ReconstructedDungeons.Scripted): one wave without monsters per step. Every reference
    /// must resolve in the client data and the scene's map, or the scene is refused like any unresolved encounter:
    /// the quests the tracker shows, the actors' NPCConfig rows, points and buffs, the stories, regions and points,
    /// and the interactions and dungeon messages the step's actors can actually send.
    /// </summary>
    private IReadOnlyList<Wave> ScriptedWaves(ReconstructedDungeons.ScriptedScene scene, MapWorld map)
    {
        var waves = new List<Wave>();
        foreach (var step in scene.Steps)
        {
            if (!data.Table("DungeonQuestConfig").Values.Any(q => GameData.Int(q, "_iDungeonID") == scene.Scene
                && GameData.Int(q, "_iQuestID") == step.Quest)) return [];
            foreach (var actor in step.Actors)
                if (data.Row("NPCConfig", actor.NpcId) is not { } npc || GameData.Int(npc, "_iNPCType") is not (1 or 2)
                    || !map.Points.ContainsKey(actor.Point)
                    || actor.Buffs.Any(b => data.Row("BuffConfig", b) is not { } buff || GameData.Int(buff, "_iBuffID") <= 0)) return [];
            if (step.Story != 0 && data.Row("PlotInterpretation", step.Story) == null) return [];
            if (step.TeleportAfter != 0 && !map.Points.ContainsKey(step.TeleportAfter)) return [];
            foreach (var t in step.Complete.Concat(step.Ignore))
            {
                bool known = t.Kind switch
                {
                    ReconstructedDungeons.TriggerKind.Interaction => step.Actors.Any(a => Interactions(a.NpcId).Contains(t.Id)),
                    ReconstructedDungeons.TriggerKind.Message => step.Actors.Any(a => Interactions(a.NpcId).Any(i => DungeonMessage(i) == t.Id)),
                    ReconstructedDungeons.TriggerKind.Region => map.Regions.ContainsKey(t.Id),
                    ReconstructedDungeons.TriggerKind.Point => map.Points.ContainsKey(t.Id) && t.Radius > 0,
                    ReconstructedDungeons.TriggerKind.Plot => data.Row("PlotInterpretation", t.Id) != null,
                    _ => false,
                };
                if (!known) return [];
            }
            waves.Add(new Wave(waves.Count + 1, [], step.Quest, step));
        }
        // An unplayed quest is one of the scene's own quests that no step shows.
        if (scene.Unplayed.Any(q => scene.Steps.Any(s => s.Quest == q) || !data.Table("DungeonQuestConfig").Values.Any(r =>
                GameData.Int(r, "_iDungeonID") == scene.Scene && GameData.Int(r, "_iQuestID") == q))) return [];
        return waves;
    }

    /// <summary>
    /// The lair's four fights, in task order (TaskConfig 9000101/103/105/107 "Defeat the Marionette / Wraith /
    /// Doppelganger / Wind Ghost"): the elite (its NpcLevelPointRespawnConfig point is 1号..4号精英怪, elite 1 to 4), the
    /// scene-editor region the preceding chase ends in (406 傀儡娃娃, 407 恶灵, 408 死灵, 409 风魔巫师 任务区域: the
    /// elite's "task region") and its m_kGroups (118 第一波怪 "first wave" to 121 "fourth wave"; the third wave is
    /// 120, 280 and 281). Joining them by their names is (inferred); every id is client data.
    /// </summary>
    internal static readonly (int Elite, long Region, int[] Groups)[] DraculaFights =
        [(4114025, 406, [118]), (4114027, 407, [119]), (4114029, 408, [120, 280, 281]), (4114031, 409, [121])];
    /// <summary>Region 410 德古拉任务区域 (Dracula's task region), where the last chase ends and Dracula fights.</summary>
    internal const long DraculaRegion = 410;
    /// <summary>The four "Search" bushes (6000018-6000021) and the "Unlock" cage (6000023, condition 7000039: the key
    /// buff 4121301), NPCConfig type 2 bound to points 186/200/228/250/293 (NpcLevelPointRespawnConfig, NotGroundBorn).</summary>
    internal static readonly int[] DraculaBushes = [4114033, 4114034, 4114035, 4114036];
    internal const int DraculaCage = 4114038, DraculaUnlock = 6000023;

    /// <summary>
    /// Dracula's Lair (DungeonConfig 3010401, gameplay 31 DraculaWedding) for DraculaWeddingService: one wave per dungeon
    /// task of the lair (TaskConfig 9000100-9000110: _iDungeonID 3010401, a _kPreTask chain alternating "chase Dracula"
    /// and "defeat ..."). A chase is a scripted step that completes when a participant walks into the next foe's task
    /// region (<see cref="DraculaFights"/>); a fight spawns the elite at its point with its wave groups, each point's
    /// monster bound by NpcLevelPointRespawnConfig (Deviruchi, Whisper, Nightmare, Ghoul). Dracula
    /// (PublicParamConfig.DraculaNpcConfigId 4114015) fights at the centre of region 410 (inferred: his own point 127 is
    /// beside the entrance, where the lair story 3003 shows him taking the bride). The last step shows the bushes and the
    /// cage and completes on the cage's Unlock. The mines, falling rocks and bats of the chases (4114004-4114014,
    /// unselectable, AI 25000009) need the original server script and are not spawned. Null when any reference is missing.
    /// </summary>
    internal Plan? Dracula(long scene)
    {
        if (data.Row("DungeonConfig", scene) is not { } config || GameData.Int(config, "_iGamePlayType") != 31
            || worldData.Map(scene) is not { Born: not null } map) return null;
        var bound = data.Table("NpcLevelPointRespawnConfig").Values.Where(r => GameData.Int(r, "_iMapid") == scene)
            .GroupBy(r => GameData.Int(r, "_iRespawnpointid")).ToDictionary(g => g.Key, g => (int)GameData.Int(g.First(), "_iNpcid"));
        Spawn? At(long point) => bound.TryGetValue(point, out int npc) && map.Points.TryGetValue(point, out var p)
            && monsters.Template(npc) != null ? new Spawn(npc, p) : null;
        ReconstructedDungeons.ScriptedStep Chase(long region) => new($"chase to region {region}", 0, (0, 1), [], 0,
            [new(ReconstructedDungeons.TriggerKind.Region, region)], [], 0);
        var waves = new List<Wave>();
        foreach (var (elite, region, groups) in DraculaFights)
        {
            if (!map.Regions.ContainsKey(region)) return null;
            waves.Add(new Wave(waves.Count + 1, [], 0, Chase(region)));
            var spawns = new List<Spawn>();
            long elitePoint = bound.Where(p => p.Value == elite).Select(p => p.Key).SingleOrDefault();
            if (At(elitePoint) is not { } boss) return null;
            spawns.Add(boss);
            foreach (int group in groups)
            {
                if (!map.DungeonGroups.TryGetValue(group, out var members) || members.Count == 0) return null;
                foreach (var member in members)
                    if (At(member.Id) is { } minion) spawns.Add(minion);
                    else return null;
            }
            waves.Add(new Wave(waves.Count + 1, spawns));
        }
        int dracula = data.Table("PublicParamConfig").TryGetValue("DraculaNpcConfigId", out var id)
            ? (int)(GameData.Number(id) ?? 0) : 0;
        if (!map.Regions.TryGetValue(DraculaRegion, out var lair) || monsters.Template(dracula) == null) return null;
        waves.Add(new Wave(waves.Count + 1, [], 0, Chase(DraculaRegion)));
        waves.Add(new Wave(waves.Count + 1, [new Spawn(dracula, map.Grounded(new MapPoint(lair.X, lair.Y, lair.Z, lair.Direction)))]));
        var actors = new List<ReconstructedDungeons.SceneActor>();
        foreach (int npc in DraculaBushes.Append(DraculaCage))
        {
            long point = bound.Where(p => p.Value == npc).Select(p => p.Key).SingleOrDefault();
            if (!map.Points.ContainsKey(point) || data.Row("NPCConfig", npc) is not { } row || GameData.Int(row, "_iNPCType") != 2
                || Interactions(npc).Count == 0) return null;
            actors.Add(new(npc, point, []));
        }
        if (!Interactions(DraculaCage).Contains(DraculaUnlock)) return null;
        waves.Add(new Wave(waves.Count + 1, [], 0, new ReconstructedDungeons.ScriptedStep("find the key", 0, (0, 1), actors, 0,
            [new(ReconstructedDungeons.TriggerKind.Interaction, DraculaUnlock)],
            DraculaBushes.SelectMany(Interactions).Select(i => new ReconstructedDungeons.StepTrigger(
                ReconstructedDungeons.TriggerKind.Interaction, i)).ToArray(), 0)));
        return new Plan(config, null, null, 0, 1, (int)GameData.Int(config, "_iRoleCntLimit"), waves);
    }

    /// <summary>A scene-editor point (m_kAllPoints) of the scene's map.</summary>
    public MapPoint? ScenePoint(long scene, long point) =>
        worldData.Map(scene)?.Points.TryGetValue(point, out var p) == true ? p : null;

    /// <summary>A scene-editor region (m_kRegins) of the scene's map.</summary>
    public MapRegion? SceneRegion(long scene, long region) =>
        worldData.Map(scene)?.Regions.TryGetValue(region, out var r) == true ? r : null;

    /// <summary>An NPCConfig row's _iInteractID list (the interactions the client offers on that unit).</summary>
    public IReadOnlyList<long> Interactions(int npcId) => data.Row("NPCConfig", npcId) is { } npc
        && npc.TryGetProperty("_iInteractID", out var ids) && ids.ValueKind == JsonValueKind.Array
        ? ids.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).Where(v => v > 0).ToArray() : [];

    /// <summary>
    /// The dungeon message an interaction sends: its NPCInteractConfig _iEXECAction ["210", id, ...]. ActionConfig 210
    /// SendDungeonMessage is server-hosted (_iEXECHost 1): the client never runs it, the dungeon script receives it.
    /// </summary>
    public long? DungeonMessage(long interactionId)
    {
        if (data.Row("NPCInteractConfig", interactionId) is not { } row || !row.TryGetProperty("_iEXECAction", out var actions)
            || actions.ValueKind != JsonValueKind.Array) return null;
        foreach (var action in actions.EnumerateArray())
            if (action.ValueKind == JsonValueKind.Array && action.GetArrayLength() >= 2
                && GameData.Number(action[0]) == 210 && GameData.Number(action[1]) is { } id) return (long)id;
        return null;
    }
}
