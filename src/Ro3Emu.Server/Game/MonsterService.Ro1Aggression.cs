using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The RO1 aggression reference (inferred, RO1 reference, not client evidence): field monsters whose Ragnarok Online 1
/// counterpart is aggressive in rAthena's pre-renewal mob_db, matched by English name in the embedded
/// Reconstructed/ro1-aggressive-monsters.json. With the switch Ro3Emu:Ro1AggressiveMonsters on, they attack on sight
/// like the client's AIConfig 20000004 monsters (<see cref="AggressiveAi"/>); off, nothing changes.
/// </summary>
public sealed partial class MonsterService
{
    public const string Ro1AggressiveResource = "Ro3Emu.Server.Reconstructed.ro1-aggressive-monsters.json";

    /// <summary>A listed monster: the RO3 NPCConfig row and the RO1 (rAthena) entry it was matched to.</summary>
    public sealed record Ro1AggressiveMonster(int NpcId, string Name, int Ro1Id, string AegisName, string Ro1Name, string Match, string Why);

    /// <summary>The listed monsters, empty when the switch is off.</summary>
    private readonly HashSet<int> _ro1Aggressive;

    private static HashSet<int> Ro1Aggressive(ServerOptions? options) =>
        options?.Ro1AggressiveMonsters == true ? [.. LoadRo1AggressiveMonsters().Keys] : [];

    /// <summary>Ro3Emu:Ro1AggressiveMaxLevelGap; 0 or less: no gate.</summary>
    private readonly int _ro1MaxLevelGap;

    /// <summary>Per public scene: the listed monsters its field pools spawn that the level gate keeps passive there.</summary>
    private readonly Dictionary<long, HashSet<int>> _ro1PassiveOn = [];

    /// <summary>
    /// Whether an idle monster takes a player in view: AIConfig 20000004 or the RO1 list, except a listed monster on a
    /// public map where <see cref="Ro1PassiveOn"/> keeps it passive. A private room (story waves, team bosses) and a monster
    /// the map's pools do not spawn (a field MVP, an event's) keep the list's aggression.
    /// </summary>
    private bool AttacksOnSight(MonsterUnit m) =>
        m.Template.Aggressive && (m.InstanceId != 0 || !Ro1PassiveOn(m.SceneEntry).Contains(m.Template.NpcId));

    /// <summary>
    /// (inferred, owner's decision 31.10) RO3 mixes levels on one field where RO1 kept them apart: Payon Forest (10220)
    /// spawns Horns at 16 among Greatest Generals at 55, and the level 16 story walks through it (10102130). A monster
    /// aggressive only by the RO1 list stays passive, as the client has it, on an open field (MapConfig _iSceneType 1,
    /// enum_SceneType.OutSide, and no _iCaveBelongsTo: Payon Cave is OutSide but a cave) whose own field pools
    /// (m_kWorldMonsterSpawnGroups) hold a common monster more than Ro1AggressiveMaxLevelGap levels below it. Dungeons
    /// (Cave 6), Territory War sandboxes (8) and towns keep the list's aggression.
    /// </summary>
    internal IReadOnlySet<int> Ro1PassiveOn(long scene)
    {
        if (_ro1MaxLevelGap <= 0 || _ro1Aggressive.Count == 0) return NoneGated;
        if (!_ro1PassiveOn.TryGetValue(scene, out var passive))
            _ro1PassiveOn[scene] = passive = MapConfig.Get(_data, scene) is { SceneType: OutSideScene, CaveBelongsTo: 0 }
                && _worldData.Map(scene) is { } map ? Ro1PassiveNpcs(map.SpawnGroups, PoolMonster, _ro1MaxLevelGap) : [];
        return passive;
    }

    private static readonly HashSet<int> NoneGated = [];

    /// <summary>MapConfig _iSceneType enum_SceneType.OutSide (Lua_GameDefine: MainCity 0, OutSide 1, Cave 6, TerritoryWars 8).</summary>
    private const long OutSideScene = 1;

    /// <summary>(inferred) A monster counts for its map's lowest level only when it is at least this share (percent) of the
    /// map's field population, so one rare weak monster (Drops, level 2, among Sograt Desert's Desert Wolves) gates nothing.</summary>
    internal const int Ro1GateMinSharePercent = 10;

    /// <summary>
    /// A pool entry's level and whether the gate may make it passive: aggressive only by the RO1 list and not a boss
    /// (NPCConfig _iNPCSubType = enum_MonsterType MT_MINI 3, MT_MVP 4, MT_BOSS 6, as ParagonService counts bosses); null:
    /// not a monster.
    /// </summary>
    private (int Level, bool Gated)? PoolMonster(int npcId) => Template(npcId) is { } t
        ? (t.Level, _ro1Aggressive.Contains(npcId) && NPCConfig.Get(_data, npcId) is { } npc && npc.Ai != AggressiveAi
            && npc.NPCSubType is not (3 or 4 or 6)) : null;

    /// <summary>
    /// The gateable monsters of these spawn groups whose level exceeds the lowest level among the common ones (at least
    /// <see cref="Ro1GateMinSharePercent"/> of the population) by more than <paramref name="gap"/>. The population counts a
    /// mixed pool's entries by their maximum and a fixed group's points one each.
    /// </summary>
    internal static HashSet<int> Ro1PassiveNpcs(IEnumerable<SpawnGroup> groups, Func<int, (int Level, bool Gated)?> monster, int gap)
    {
        var spawned = new Dictionary<int, (int Level, bool Gated, long Count)>();
        foreach (var group in groups)
        {
            var counts = group.Pool ? group.Limits.Where(l => l.Max > 0).Select(l => (Npc: l.NpcId, Count: (long)l.Max))
                : group.Points.Select(p => (Npc: p.ConfigId, Count: 1L));
            foreach (var (npc, count) in counts)
                if (spawned.TryGetValue(npc, out var seen)) spawned[npc] = seen with { Count = seen.Count + count };
                else if (monster(npc) is { Level: > 0 } m) spawned[npc] = (m.Level, m.Gated, count);
        }
        long total = spawned.Values.Sum(m => m.Count);
        var common = spawned.Values.Where(m => m.Count * 100 >= total * Ro1GateMinSharePercent).ToList();
        if (common.Count == 0) return [];
        int lowest = common.Min(m => m.Level);
        return [.. spawned.Where(e => e.Value.Gated && e.Value.Level - lowest > gap).Select(e => e.Key)];
    }

    /// <summary>The embedded list, keyed by NPCConfig id (parsed on each call; the service keeps its own set).</summary>
    public static IReadOnlyDictionary<int, Ro1AggressiveMonster> LoadRo1AggressiveMonsters()
    {
        using var stream = typeof(MonsterService).Assembly.GetManifestResourceStream(Ro1AggressiveResource)
            ?? throw new InvalidOperationException("Embedded Reconstructed/ro1-aggressive-monsters.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        var monsters = new Dictionary<int, Ro1AggressiveMonster>();
        foreach (var entry in doc.RootElement.GetProperty("monsters").EnumerateObject())
        {
            int npcId = int.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture);
            var m = entry.Value;
            var ro1 = m.GetProperty("ro1");
            monsters[npcId] = new Ro1AggressiveMonster(npcId, m.GetProperty("name").GetString() ?? "",
                ro1.GetProperty("id").GetInt32(), ro1.GetProperty("aegisName").GetString() ?? "", ro1.GetProperty("name").GetString() ?? "",
                m.GetProperty("match").GetString() ?? "", m.GetProperty("why").GetString() ?? "");
        }
        return monsters;
    }
}
