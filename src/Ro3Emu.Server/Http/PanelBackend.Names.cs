using System.Globalization;
using Ro3Emu.Panel;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The public lists show one row per name: players read two "Poring" rows as the same monster twice, whatever their
/// numbers. A row stands for every variant of its name that the filters match (a variant being a group of rows a player
/// cannot tell apart, PanelBackend.Catalog.cs): its picture and numbers are those of the variant that spawns most in the
/// field, with the level (and HP) range of all of them and how many there are. A page lists the variants of its name
/// and merges what they drop, sell and spawn.
/// </summary>
public sealed partial class PanelBackend
{
    /// <summary>(inferred) A level-1 monster with this much HP and no field spawn is an actor of a story or scene ("Dancer",
    /// 46 million HP), not one a player hunts: the lists leave it out, its id still finds it.</summary>
    private const long ActorHp = 100_000;

    public async Task<PanelItemPage> FindItemsAsync(PanelItemQuery query)
    {
        var catalog = await CatalogAsync();
        long id = long.TryParse(query.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : -1;
        // An id finds its item even when the lists leave it out (a copy, a "TBD" row).
        if (id >= 0 && catalog.ItemSummary(id) is { } exact)
            return new PanelItemPage([exact], 1, 1, query.PageSize, catalog.Types, catalog.Qualities);
        var rows = catalog.Items.Where(i =>
                (query.Text.Length == 0 || i.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                && (query.Type == 0 || i.Type == query.Type) && (query.Quality == 0 || i.Quality == query.Quality)
                && (query.MinLevel == 0 || i.Level >= query.MinLevel) && (query.MaxLevel == 0 || i.Level <= query.MaxLevel))
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g => ItemRow(catalog, [.. g]))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id).ToList();
        return new PanelItemPage(Page(rows, query.Page, query.PageSize), rows.Count, query.Page, query.PageSize, catalog.Types, catalog.Qualities);
    }

    /// <summary>A name's row: the variant the game repeats under the most ids, with every variant's level range.</summary>
    private static PanelItemSummary ItemRow(PanelCatalog catalog, List<PanelItemSummary> variants)
    {
        var rep = variants.OrderByDescending(v => catalog.IdsOf(v.Id).Count).ThenBy(v => v.Id).First();
        return rep with { Level = variants.Min(v => v.Level), MaxLevel = variants.Max(v => v.Level), Variants = variants.Count };
    }

    public async Task<PanelItem?> ItemAsync(long id) => (await CatalogAsync()).Item(id);

    public async Task<PanelMonsterPage> FindMonstersAsync(PanelMonsterQuery query)
    {
        var (catalog, index) = (await CatalogAsync(), await SpawnIndexAsync());
        long id = long.TryParse(query.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long n) ? n : -1;
        if (id >= 0 && catalog.MonstersById.TryGetValue(id, out var exact))
            return new PanelMonsterPage([exact.Rep.Summary], 1, 1, query.PageSize, catalog.Elements, catalog.Races, catalog.Sizes);
        var rows = catalog.Monsters.Where(g => !Actor(g, index)).Where(g =>
                (query.Text.Length == 0 || g.Rep.Summary.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                && (query.Element == 0 || g.Rep.Summary.Element == query.Element) && (query.Race == 0 || g.Rep.RaceId == query.Race)
                && (query.Size == 0 || g.Rep.SizeId == query.Size) && (query.Rank.Length == 0 || g.Rep.Summary.Rank == query.Rank)
                && (query.MinLevel == 0 || g.Rep.Summary.Level >= query.MinLevel) && (query.MaxLevel == 0 || g.Rep.Summary.Level <= query.MaxLevel))
            .GroupBy(g => g.Rep.Summary.Name, StringComparer.OrdinalIgnoreCase).Select(g => MonsterRow([.. g], index))
            .OrderBy(m => m.Level).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new PanelMonsterPage(Page(rows, query.Page, query.PageSize), rows.Count, query.Page, query.PageSize,
            catalog.Elements, catalog.Races, catalog.Sizes);
    }

    /// <summary>How many of a variant the field keeps alive, over all its maps.</summary>
    private static int Spawned(MonsterGroup group, Dictionary<long, List<PanelSpawn>> index) =>
        group.Members.Sum(m => index.GetValueOrDefault(m.Summary.Id)?.Sum(s => s.Count) ?? 0);

    private static bool Actor(MonsterGroup group, Dictionary<long, List<PanelSpawn>> index) =>
        group.Rep.Summary.Level <= 1 && group.Rep.Summary.Hp >= ActorHp && Spawned(group, index) == 0;

    private static PanelMonsterSummary MonsterRow(List<MonsterGroup> variants, Dictionary<long, List<PanelSpawn>> index)
    {
        var rep = variants.OrderByDescending(v => Spawned(v, index)).ThenBy(v => v.Rep.Summary.Id).First().Rep.Summary;
        string icon = rep.Icon.Length > 0 ? rep.Icon : variants.Select(v => v.Rep.Summary.Icon).FirstOrDefault(i => i.Length > 0) ?? "";
        return rep with
        {
            Level = variants.Min(v => v.Rep.Summary.Level),
            MaxLevel = variants.Max(v => v.Rep.Summary.Level),
            Hp = variants.Min(v => v.Rep.Summary.Hp),
            MaxHp = variants.Max(v => v.Rep.Summary.Hp),
            Variants = variants.Count,
            Icon = icon,
        };
    }

    /// <summary>
    /// A monster's page: the numbers of the variant asked for, the variants of its name (the actors only when one of them is
    /// asked for), and their spawns and drops merged; a drop row names its variants when not all of them drop it.
    /// </summary>
    public async Task<PanelMonster?> MonsterAsync(long id)
    {
        var (catalog, index) = (await CatalogAsync(), await SpawnIndexAsync());
        if (!catalog.MonstersById.TryGetValue(id, out var group)) return null;
        var variants = catalog.MonstersNamed(group.Rep.Summary.Name).Where(v => v == group || !Actor(v, index))
            .OrderBy(v => v.Rep.Summary.Level).ThenBy(v => v.Rep.Summary.Hp).ThenBy(v => v.Rep.Summary.Id).ToList();
        var spawns = MergeSpawns(variants.SelectMany(v => v.Ids).SelectMany(v => index.GetValueOrDefault(v) ?? []));
        var listed = variants.Select(v => new PanelMonsterVariant(v.Rep.Summary.Id, v.Ids, v.Rep.Summary.Level, v.Rep.Summary.Hp, v.Rep.Summary.Size,
            v.Rep.Summary.Rank, v.Rep.Summary.ElementName,
            [.. v.Ids.SelectMany(i => index.GetValueOrDefault(i) ?? []).Select(s => s.Map).Distinct(StringComparer.OrdinalIgnoreCase)])).ToList();
        var rep = group.Rep;
        return new PanelMonster(rep.Summary with { Id = id }, rep.Stats, rep.Speed, MergeDrops(variants), spawns, group.Ids, listed);
    }

    /// <summary>One line per map: the variants' counts added, the shortest respawn.</summary>
    private static List<PanelSpawn> MergeSpawns(IEnumerable<PanelSpawn> spawns) =>
        [.. spawns.GroupBy(s => s.MapId).Select(g => g.First() with { Count = g.Sum(s => s.Count), RespawnSeconds = g.Min(s => s.RespawnSeconds) })
            .OrderByDescending(s => s.Count).ThenBy(s => s.Map, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Every variant's drops as one list: per item, kind and map limit the best chance and the widest count.</summary>
    private static List<PanelMonsterDrop> MergeDrops(List<MonsterGroup> variants)
    {
        var rows = variants.SelectMany(v => v.Members.SelectMany(m => m.Drops).Select(d => (Variant: v.Rep.Summary.Id, Drop: d)))
            .GroupBy(r => (r.Drop.ItemId, r.Drop.Kind, Maps: string.Join(',', r.Drop.Maps)));
        return [.. rows.Select(g =>
            {
                var from = g.Select(r => r.Variant).Distinct().Order().ToList();
                var best = g.MaxBy(r => r.Drop.Chance).Drop;
                return best with
                {
                    Min = g.Min(r => r.Drop.Min), Max = g.Max(r => r.Drop.Max), From = from.Count < variants.Count ? from : null,
                };
            })
            .OrderBy(d => d.Kind.Length > 0).ThenByDescending(d => d.Chance).ThenBy(d => d.Item, StringComparer.OrdinalIgnoreCase)];
    }
}
