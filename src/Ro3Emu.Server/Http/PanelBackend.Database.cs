using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ro3Emu.Panel;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The panel's item, monster and map databases: built once from the game data on first use (off the request thread)
/// and kept in memory (PanelBackend.Catalog.cs); the spawn index, which reads every map's scene editor, is built on its
/// own the first time a monster or map page asks. Numbers come from the server's own rules: a monster's HP from
/// MonsterService.TemplateAttributes, its drops from RewardService.KillDropRows and the pools they roll.
/// </summary>
public sealed partial class PanelBackend
{
    private const int DetailRows = 200;
    /// <summary>Items 100 and 101 are the Base and Job EXP a kill grants, not loot.</summary>
    private const long BaseExpItem = 100, JobExpItem = 101;

    /// <summary>The game data, for the catalog (a nested class cannot see the constructor's parameter).</summary>
    private GameData Tables => data;

    /// <summary>Ro3Emu:LordsManePercent, the chance MvpService gives a Lord's Mane per MVP kill.</summary>
    private int LordsManePercent => options.Value.LordsManePercent;

    private readonly object _databaseGate = new();
    private Task<PanelCatalog>? _catalog;
    private Task<Dictionary<long, List<PanelSpawn>>>? _spawnIndex;

    public async Task<IReadOnlyList<PanelMapSummary>> MapsAsync()
    {
        var (spawns, online) = (await MapSpawnsAsync(), await OnlineRowsAsync());
        var players = online.GroupBy(r => r.Scene).ToDictionary(g => g.Key, g => g.Count());
        return [.. MapConfig.All(data).Select(map => (map, name: data.MapName(map.Id))).Where(m => m.name.Length > 0)
            .Select(m => MapSummary(m.map.Id, m.name, spawns.GetValueOrDefault(m.map.Id) ?? [], players.GetValueOrDefault(m.map.Id)))
            .OrderBy(m => m.Monsters == 0).ThenBy(m => m.MinLevel).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<PanelMapDetail?> MapAsync(long id)
    {
        string name = data.MapName(id);
        if (MapConfig.Get(data, id) == null || name.Length == 0) return null;
        var monsters = (await MapSpawnsAsync()).GetValueOrDefault(id) ?? [];
        var players = (await OnlineRowsAsync()).Where(r => r.Scene == id).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new PanelMapPlayer(r.Name, ClassName(r.Career), ClassIcon(r.Career), r.Level)).ToList();
        return new PanelMapDetail(MapSummary(id, name, monsters, players.Count), monsters, players);
    }

    private PanelMapSummary MapSummary(long id, string name, List<PanelMapMonster> monsters, int players) =>
        new(id, name, MapImage(id), monsters.Count > 0 ? monsters.Min(m => m.Monster.Level) : 0, monsters.Count > 0 ? monsters.Max(m => m.Monster.Level) : 0,
            monsters.Count, players);

    /// <summary>The monsters of each map from the spawn index, a monster's variants as one (the database's entry).</summary>
    private async Task<Dictionary<long, List<PanelMapMonster>>> MapSpawnsAsync()
    {
        var (catalog, index) = (await CatalogAsync(), await SpawnIndexAsync());
        var byMap = new Dictionary<long, Dictionary<long, (PanelMonsterSummary Monster, int Count, int Respawn)>>();
        foreach (var (npc, spawns) in index)
        {
            if (!catalog.MonstersById.TryGetValue(npc, out var group) || PanelCatalog.Hidden(group.Rep.Summary.Name)) continue;
            foreach (var spawn in spawns)
            {
                if (!byMap.TryGetValue(spawn.MapId, out var monsters)) byMap[spawn.MapId] = monsters = [];
                var summary = group.Rep.Summary;
                monsters[summary.Id] = monsters.TryGetValue(summary.Id, out var seen)
                    ? (summary, seen.Count + spawn.Count, Math.Min(seen.Respawn, spawn.RespawnSeconds)) : (summary, spawn.Count, spawn.RespawnSeconds);
            }
        }
        return byMap.ToDictionary(m => m.Key, m => m.Value.Values.Select(v => new PanelMapMonster(v.Monster, v.Count, v.Respawn))
            .OrderBy(v => v.Monster.Level).ThenBy(v => v.Monster.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static List<T> Page<T>(List<T> rows, int page, int size) => [.. rows.Skip((page - 1) * size).Take(size)];

    /// <summary>The catalog, built once; a failed build is tried again by the next request.</summary>
    private Task<PanelCatalog> CatalogAsync()
    {
        lock (_databaseGate)
        {
            if (_catalog == null || _catalog.IsFaulted || _catalog.IsCanceled) _catalog = Task.Run(() => new PanelCatalog(this));
            return _catalog;
        }
    }

    private Task<Dictionary<long, List<PanelSpawn>>> SpawnIndexAsync()
    {
        lock (_databaseGate)
        {
            if (_spawnIndex == null || _spawnIndex.IsFaulted || _spawnIndex.IsCanceled) _spawnIndex = Task.Run(BuildSpawnIndex);
            return _spawnIndex;
        }
    }

    /// <summary>
    /// Where each monster lives: every open-world map's scene-editor spawn groups (WorldData.LoadSpawnGroups, the groups
    /// MonsterService spawns), how many a map keeps alive and the respawn; field MVPs from MVPConfig.
    /// </summary>
    private Dictionary<long, List<PanelSpawn>> BuildSpawnIndex()
    {
        string dir = Path.Combine(options.Value.GameDataDir ?? options.Value.DataDir, "sceneeditor");
        var found = new System.Collections.Concurrent.ConcurrentBag<(long Npc, long Map, int Count, int Respawn)>();
        Parallel.ForEach(MapConfig.All(data).Where(m => m.ResSceneEditorName.Length > 0), map =>
        {
            if (DataFiles.Find(dir, map.ResSceneEditorName + ".json") is not { } path) return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var group in WorldData.LoadSpawnGroups(doc.RootElement))
            {
                if (group.Pool)
                {
                    foreach (var (npc, _, max) in group.Limits.Where(l => l.Max > 0))
                        found.Add((npc, map.Id, max, group.RespawnSeconds));
                }
                else
                {
                    foreach (var points in group.Points.Where(p => p.ConfigId > 0).GroupBy(p => p.ConfigId))
                        found.Add((points.Key, map.Id, Math.Min(points.Count(), Math.Max(1, group.Total)), group.RespawnSeconds));
                }
            }
        });
        foreach (var mvp in MVPConfig.All(data).Where(m => m.NPCId > 0 && m.MapId > 0))
            found.Add((mvp.NPCId, mvp.MapId, 1, (int)mvp.RefreshTime));
        return found.GroupBy(f => f.Npc).ToDictionary(g => g.Key, g => g.GroupBy(f => f.Map)
            .Select(m => new PanelSpawn(m.Key, MapName(m.Key), m.Sum(f => f.Count), m.Min(f => f.Respawn), MapImage(m.Key)))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Map, StringComparer.OrdinalIgnoreCase).ToList());
    }

    // ---------------------------------------------------------------- names the client gives its enums

    /// <summary>
    /// The client's own English for its enums: Localization_en 10100000010.. holds the quality colours, then races,
    /// elements, sizes and weapon kinds in their id order (TypeConfig names them by these ids). A data set without the
    /// texts falls back to the same English.
    /// </summary>
    private string EnumText(long textId, string fallback) => data.Text(textId) is { Length: > 0 } text ? text : fallback;

    private static readonly string[] QualityNames = ["White", "Green", "Blue", "Purple", "Orange", "Red"];
    private static readonly string[] RaceNames = ["Brute", "Demi-Human", "Demon", "Plant", "Undead", "Formless", "Fish", "Angel", "Insect", "Dragon"];
    private static readonly string[] ElementNames = ["Neutral", "Wind", "Earth", "Fire", "Water", "Poison", "Holy", "Shadow", "Ghost", "Undead"];
    private static readonly string[] SizeNames = ["Small", "Medium", "Large"];
    private static readonly string[] WeaponNames = ["Dagger", "One-Handed Sword", "Two-Handed Sword", "One-Handed Spear", "Two-Handed Spear",
        "One-Handed Axe", "Two-Handed Axe", "Mace", "Wand", "Staff", "Bow", "Katar"];

    private string Named(string[] names, long firstText, long value) =>
        value >= 1 && value <= names.Length ? EnumText(firstText + value - 1, names[value - 1]) : "";

    private string QualityName(long quality) => Named(QualityNames, 10100000010, quality);
    private string RaceName(long race) => Named(RaceNames, 10100000022, race);
    private string ElementName(long element) => Named(ElementNames, 10100000032, element);
    private string SizeName(long size) => Named(SizeNames, 10100000042, size);

    /// <summary>
    /// ItemConfig _iType as the bag sorts it (inferred from the items of each type; the client's names where it has
    /// them: Currency 10100000062, Consumable ..080, Item Pack ..098, Card ..007, Equip ..006).
    /// </summary>
    private string TypeName(long type) => type switch
    {
        1 => EnumText(10100000062, "Currency"),
        2 => EnumText(10100000080, "Consumable"),
        3 => EnumText(10100000098, "Item Pack"),
        4 => EnumText(10100000007, "Card"),
        5 => EnumText(10100000006, "Equip"),
        7 => "Mount",
        8 => "Treasure",
        9 => "Material",
        14 => "Vehicle Part",
        _ => "Other",
    };

    /// <summary>An equipment's kind: weapons 1-12, then shield 50, clothes 55, muffler 60, shoes 65, accessory 70.</summary>
    private string SubTypeName(long type, long subType) => type != 5 ? "" : subType switch
    {
        >= 1 and <= 12 => Named(WeaponNames, 10100000045, subType),
        50 => EnumText(10100000057, "Shield"),
        55 => EnumText(10100000058, "Clothes"),
        60 => EnumText(10100000059, "Muffler"),
        65 => EnumText(10100000060, "Shoes"),
        70 => EnumText(10100000061, "Accessory"),
        _ => "",
    };

    /// <summary>
    /// NPCConfig _iNPCSubType: 3 mini boss, 4 field MVP (MVPConfig lists them), 6 boss of a dungeon or story; the
    /// server treats 3, 4 and 6 as bosses (TitleService, ParagonService). The words are (inferred).
    /// </summary>
    private static string Rank(long subType, bool mvp) => mvp || subType == 4 ? "mvp" : subType switch { 3 => "mini", 6 => "boss", _ => "normal" };

    /// <summary>A name field [text id, param...] read through a view: the text with @{n} replaced by the n-th value.</summary>
    private string Name(long[] text)
    {
        if (text.Length == 0) return "";
        string name = data.Text(text[0]);
        for (int i = 1; i < text.Length; i++) name = name.Replace("@{" + i.ToString(CultureInfo.InvariantCulture) + "}", text[i].ToString(CultureInfo.InvariantCulture));
        return name;
    }

    /// <summary>The colour marks the client draws in a description (^{n}, &lt;color=...&gt;) are left out.</summary>
    [GeneratedRegex(@"\^\{\d+\}|</?color[^>]*>")]
    private static partial Regex ColourMark();

    /// <summary>(inferred) The developers' own monster rows ("Test Death", "3D Bounding Box Test", "Magic Circle_Empty",
    /// "Blank NPC"): no player meets them, so the database leaves them out, as it does rows of level 0.</summary>
    [GeneratedRegex(@"\btest\b|_|\bblank npc\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeveloperRow();

    /// <summary>An attribute's value as the client writes it (Lua_CFG_AttributeConfig): data type 1 a percentage of
    /// value/100, else a whole number; data type 2 (attack speed) as value/100 is (inferred).</summary>
    private string AttributeValue(long attribute, double value)
    {
        long type = AttributeConfig.Get(data, attribute)?.DataType ?? 0;
        return type switch
        {
            1 => (value / 100).ToString("0.##", CultureInfo.InvariantCulture) + "%",
            2 => (value / 100).ToString("0.##", CultureInfo.InvariantCulture),
            _ => Math.Round(value).ToString("N0", CultureInfo.InvariantCulture),
        };
    }

    private static readonly Dictionary<long, string> AttributeFallback = new()
    {
        [101] = "HP", [102] = "SP", [103] = "PATK", [104] = "MATK", [105] = "PDEF", [106] = "MDEF", [109] = "HIT", [110] = "FLEE",
        [111] = "CRIT", [113] = "MSPD",
    };

    private string AttributeName(long attribute) =>
        AttributeConfig.Get(data, attribute) is { } row && Name(row.Name) is { Length: > 0 } name ? name
        : AttributeFallback.GetValueOrDefault(attribute) ?? "Attribute " + attribute.ToString(CultureInfo.InvariantCulture);
}
