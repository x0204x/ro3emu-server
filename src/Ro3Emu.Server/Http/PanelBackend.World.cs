using System.Globalization;
using System.Text.Json;
using Ro3Emu.Panel;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Http;

/// <summary>
/// Shops, the element table, a monster's EXP and a map's world. The element table is EleConfig (a row per defending
/// element, a column per attacking one, per 10000 of the damage, CombatService.ElementFactor). A kill's EXP is item 100
/// in the pools RewardService.KillDropRows names (NPCDropConfig environment 0, 1 in Bonus Time, and the _iNormalDrop
/// rows), at the player levels its DropGroupConfig row allows; Job EXP is the same amount (inferred, RewardService
/// .ResolveKill); the level gap (LvExpCorrectionConfig) and the party (TeamService.ExpBuff) scale both. A map's NPCs and
/// portals come from its scene editor's kNpcList, its neighbours from MapLinkData, its levels from
/// OnHookMonsterRecommendConfig.
/// </summary>
public sealed partial class PanelBackend
{
    /// <summary>ShopConfig _iShopType 8: Survivor Battle's match shops, which the open world never shows.</summary>
    private const long MatchShop = 8;
    private static readonly string[] ElementColumns = ["None", "Wind", "Earth", "Fire", "Water", "Poison", "Holy", "Shadow", "Ghost", "Undead"];

    // ---------------------------------------------------------------- shops

    public Task<IReadOnlyList<PanelShopSummary>> ShopsAsync() => CachedAsync<IReadOnlyList<PanelShopSummary>>("shops", () =>
        Task.FromResult<IReadOnlyList<PanelShopSummary>>([.. Shops().Select(s => ShopSummary(s, Goods(s.ClassId)))
            .Where(s => s.Goods > 0).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)]));

    public Task<PanelShop?> ShopAsync(long id)
    {
        if (Shops().FirstOrDefault(s => s.ClassId == id) is not { Row.ValueKind: JsonValueKind.Object } shop) return Task.FromResult<PanelShop?>(null);
        var goods = Goods(id);
        return Task.FromResult<PanelShop?>(new PanelShop(ShopSummary(shop, goods), goods));
    }

    private IEnumerable<ShopConfig> Shops() =>
        ShopConfig.All(data).Where(s => s.ShopType != MatchShop).GroupBy(s => s.ClassId).Select(g => g.First());

    private PanelShopSummary ShopSummary(ShopConfig shop, List<PanelShopGood> goods) =>
        new(shop.ClassId, ShopName(shop), goods.Count, goods.GroupBy(g => g.Currency).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "");

    private string ShopName(ShopConfig shop) => Name(shop.ShopName) is { Length: > 0 } n ? n : "Shop " + shop.ClassId.ToString(CultureInfo.InvariantCulture);

    private List<PanelShopGood> Goods(long shop) =>
        [.. ShopCommodityConfig.All(data).Where(c => c.ClassId == shop && c.ItemId > 0).Select(c =>
        {
            long currency = c.ItemPerPrice.Length > 0 ? c.ItemPerPrice[0] : 0, price = c.ItemPerPrice.Length > 1 ? c.ItemPerPrice[1] : 0;
            var paid = currency > 0 ? Amount(currency, price) : null;
            return new PanelShopGood(Amount(c.ItemId, Math.Max(1, c.ItemPerMount)), currency, paid?.Item ?? "", paid?.Icon ?? "", price);
        })];

    // ---------------------------------------------------------------- elements and EXP

    public Task<PanelCombatRules> CombatRulesAsync() => CachedAsync("rules", () =>
    {
        var elements = EleConfig.All(data).Where(e => e.Id >= 1 && e.Id <= ElementColumns.Length).OrderBy(e => e.Id)
            .Select(e => new PanelElementRow((int)e.Id, ElementName(e.Id), ElementFactors(e))).ToList();
        var gaps = new List<PanelExpGap>();
        foreach (var row in LvExpCorrectionConfig.All(data).OrderBy(r => r.BaseLvGap))
        {
            int gap = (int)row.BaseLvGap, percent = (int)(row.LvDifferenceCorrection / 100);
            if (gaps.Count > 0 && gaps[^1].Percent == percent && gaps[^1].ToGap == gap - 1) gaps[^1] = gaps[^1] with { ToGap = gap };
            else gaps.Add(new PanelExpGap(gap, gap, percent));
        }
        var party = TeamService.ExpBuff.Select((bonus, i) => new PanelPartyBonus(i + 1, bonus / 100)).ToList();
        return Task.FromResult(new PanelCombatRules(elements, gaps, party));
    });

    /// <summary>The change for each attacking element against a defender row (per 10000 to percent).</summary>
    private List<PanelElementFactor> ElementFactors(EleConfig row)
    {
        long[] values = [row.None, row.Wind, row.Earth, row.Fire, row.Water, row.Poison, row.Holy, row.Shadow, row.Ghost, row.Undead];
        return [.. values.Select((v, i) => new PanelElementFactor(i + 1, ElementName(i + 1), (int)(v / 100)))];
    }

    public async Task<PanelMonsterCombat?> MonsterCombatAsync(long id)
    {
        if (!(await CatalogAsync()).MonstersById.ContainsKey(id) || NPCConfig.Get(data, id) is not { } npc) return null;
        var exp = ExpRows(npc, bonus: false).Concat(ExpRows(npc, bonus: true).Where(r => r.Kind != LowLevelBonus)).ToList();
        var taken = EleConfig.All(data).Where(e => e.Id == npc.Element).Select(ElementFactors).FirstOrDefault() ?? [];
        return new PanelMonsterCombat(exp, taken);
    }

    private const string LowLevelBonus = "Low-level bonus";

    /// <summary>Item 100 in a kill's pools: the environment row first ("Kill" or "Bonus Time"), then the _iNormalDrop rows.</summary>
    private IEnumerable<PanelExpRow> ExpRows(NPCConfig npc, bool bonus)
    {
        bool first = true;
        foreach (var (row, maps) in RewardService.KillDropRows(data, npc.Row, bonus))
        {
            string kind = !first || maps.Count > 0 ? LowLevelBonus : bonus ? "Bonus Time" : "Kill";
            first = false;
            foreach (long poolId in RewardService.DropPools(row))
            {
                if (data.Row("DropPoolConfig", poolId) is not { } pool) continue;
                foreach (var (group, chance, rolls, _) in RewardService.PoolSlots(pool).Where(s => s.Chance >= 1_000_000))
                    foreach (var exp in DropGroupConfig.Get(data, group).Where(r => r.DropItem == BaseExpItem && r.QuestLimit == 0 && r.Activityid == 0))
                        yield return new PanelExpRow(kind, exp.MinNumber * rolls, Math.Max(exp.MinNumber, exp.MaxNumber) * rolls,
                            (int)exp.MinLevelLimit, (int)exp.MaxLevelLimit, [.. maps.Select(MapName)]);
            }
        }
    }

    // ---------------------------------------------------------------- a map's world

    public async Task<PanelMapWorld?> MapWorldAsync(long id) =>
        MapConfig.Get(data, id) is { } map && data.MapName(id).Length > 0
            ? await CachedAsync("world/" + id.ToString(CultureInfo.InvariantCulture), () => Task.FromResult(MapWorld(map)))
            : null;

    private PanelMapWorld MapWorld(MapConfig map)
    {
        var (npcs, portals) = ScenePlacements(map);
        var links = MapLinks();
        var neighbours = links.Neighbours.GetValueOrDefault(SceneEditorLinks.Key(map.ResSceneEditorName)) ?? [];
        var linked = portals.GroupBy(p => p).Select(g => (Map: g.Key, Via: "Portal"))
            .Concat(neighbours.Select(n => (Map: n, Via: "World map")))
            .Where(l => l.Map != map.Id && data.MapName(l.Map).Length > 0)
            .GroupBy(l => l.Map).Select(g => MapLink(g.Key, [.. g.Select(l => l.Via).Distinct()]))
            .OrderBy(l => l.Map, StringComparer.OrdinalIgnoreCase).ToList();
        var levels = OnHookMonsterRecommendConfig.All(data).Where(r => r.MapId.Contains(map.Id) && r.RecommendationLevel.Length >= 2).ToList();
        return new PanelMapWorld(map.Id, SceneTypeName(map.SceneType), levels.Count > 0 ? (int)levels.Min(r => r.RecommendationLevel[0]) : 0,
            levels.Count > 0 ? (int)levels.Max(r => r.RecommendationLevel[1]) : 0, links.Kafra.Contains(map.Id), npcs, linked,
            map.CaveBelongsTo > 0 && data.MapName(map.CaveBelongsTo).Length > 0 ? MapLink(map.CaveBelongsTo, []) : null,
            [.. map.CaveEntrance.Where(f => data.MapName(f).Length > 0).Select(f => MapLink(f, []))]);
    }

    private PanelMapLink MapLink(long map, IReadOnlyList<string> via) => new(map, MapName(map), MapImage(map), via);

    /// <summary>enum_SceneType of MapConfig _iSceneType.</summary>
    private static string SceneTypeName(long type) => type switch
    {
        0 => "Town",
        1 => "Field",
        2 => "Mirror area",
        3 => "Dungeon",
        4 or 5 => "Guild war",
        6 => "Dungeon floor",
        7 => "Training ground",
        8 => "Territory war",
        _ => "Map",
    };

    private sealed record MapLinkIndex(IReadOnlyDictionary<string, long[]> Neighbours, IReadOnlySet<long> Kafra);

    private MapLinkIndex? _mapLinks;

    private MapLinkIndex MapLinks()
    {
        if (_mapLinks != null) return _mapLinks;
        string dir = Path.Combine(options.Value.GameDataDir ?? options.Value.DataDir, "sceneeditor");
        if (DataFiles.Find(dir, "MapLinkData.json") is not { } path) return _mapLinks = new MapLinkIndex(new Dictionary<string, long[]>(), new HashSet<long>());
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return _mapLinks = new MapLinkIndex(SceneEditorLinks.Neighbours(doc.RootElement), SceneEditorLinks.KafraDestinations(doc.RootElement));
    }

    /// <summary>The scene editor's kNpcList: the NPCs and Kafras placed on the map (each kind once, with its count) and the
    /// maps its portals lead to (WorldNpcKind, the target of TravelTargetInfoArray[0]).</summary>
    private (List<PanelMapNpc> Npcs, List<long> Portals) ScenePlacements(MapConfig map)
    {
        string dir = Path.Combine(options.Value.GameDataDir ?? options.Value.DataDir, "sceneeditor");
        var placed = new List<(long Npc, WorldNpcKind Kind)>();
        var portals = new List<long>();
        if (map.ResSceneEditorName.Length > 0 && DataFiles.Find(dir, map.ResSceneEditorName + ".json") is { } path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.TryGetProperty("kNpcList", out var list) && list.ValueKind == JsonValueKind.Object)
                foreach (var entry in list.EnumerateObject().Select(e => e.Value).Where(e => e.ValueKind == JsonValueKind.Object))
                {
                    var kind = (WorldNpcKind)GameData.Int(entry, "NpcTag");
                    if (kind == WorldNpcKind.Portal && new WorldNpc(0, map.Id, kind, 0, "", 0, 0, 0, 0, 0, entry).Target is { } target)
                        portals.Add(target.Map);
                    else if (kind is WorldNpcKind.Npc or WorldNpcKind.Kafra) placed.Add((GameData.Int(entry, "NpcId"), kind));
                }
        }
        // NPC rows a player cannot tell apart (another id, the same name and offers) are one line with their count.
        var npcs = placed.GroupBy(p => p.Npc).Select(g => MapNpc(g.Key, g.Any(p => p.Kind == WorldNpcKind.Kafra), g.Count())).OfType<PanelMapNpc>()
            .GroupBy(n => n.Name + "\u001f" + string.Join(";", n.Functions)).Select(g => g.First() with { Count = g.Sum(n => n.Count) })
            .OrderBy(n => n.Functions.Count == 0).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return (npcs, portals);
    }

    private PanelMapNpc? MapNpc(long id, bool kafra, int count)
    {
        if (NPCConfig.Get(data, id) is not { } npc || Name(npc.Name) is not { Length: > 0 } name) return null;
        string title = Plain(npc.Title);
        var functions = new List<string>();
        if (title.Length > 0) functions.Add(title);
        if (kafra && !functions.Any(f => f.Contains("Kafra", StringComparison.OrdinalIgnoreCase))) functions.Add("Kafra services");
        foreach (long interact in npc.InteractId)
            if (NPCInteractConfig.Get(data, interact)?.EXECAction is { } actions) functions.AddRange(ActionNames(actions, nested: true));
        var shop = ShopConfig.All(data).Where(s => s.NpcId == id && s.ShopType != MatchShop).Select(s => (ShopConfig?)s).FirstOrDefault();
        if (shop is { } s) functions.Add("Shop: " + ShopName(s));
        return new PanelMapNpc(id, name, title, Image("npcs", npc.HeadIcon), [.. functions.Distinct(StringComparer.OrdinalIgnoreCase)], shop?.ClassId ?? 0, count);
    }

    /// <summary>
    /// What an interaction's actions open (ActionConfig ids): 25 a function window (FunctionConfig _iName), 39 the Kafra
    /// warp, 141 a jump to another map; 53 opens a dialogue whose options are read the same way, one level deep.
    /// </summary>
    private IEnumerable<string> ActionNames(JsonElement actions, bool nested)
    {
        if (actions.ValueKind != JsonValueKind.Array) yield break;
        foreach (var action in actions.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0))
        {
            long Arg(int i) => action.GetArrayLength() > i && long.TryParse(Param(action[i]), CultureInfo.InvariantCulture, out long v) ? v : 0;
            switch (Arg(0))
            {
                case 25 when FunctionConfig.Get(data, Arg(1)) is { } function && Plain(function.Name) is { Length: > 0 } name:
                    yield return name;
                    break;
                case 39:
                    yield return "Kafra warp";
                    break;
                case 141 when data.MapName(Arg(1)) is { Length: > 0 } target:
                    yield return "Teleport to " + target;
                    break;
                case 53 when nested:
                    foreach (var option in DialogueConfig.Get(data, Arg(1)))
                        if (option.EXECAction is { } inner)
                            foreach (string name in ActionNames(inner, nested: false)) yield return name;
                    break;
            }
        }
    }
}
