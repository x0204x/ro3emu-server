using System.Globalization;
using System.Text.Json;
using Ro3Emu.Panel;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The catalog behind the item and monster databases. The client's tables carry many rows of one thing: an item copied
/// for another season or source with only its id and sort order changed, a monster copied per map, quest or scene. Rows
/// a player cannot tell apart are one entry (a group): the lists show its first id, its page lists every id and merges
/// what the rows drop, sell and spawn. Rows still named "TBD" or with an unfilled "@{n}" in the name are left out of the
/// lists (inferred: names the developers never finished) but stay reachable by id.
/// </summary>
public sealed partial class PanelBackend
{
    private sealed record MonsterEntry(PanelMonsterSummary Summary, long RaceId, long SizeId, IReadOnlyList<PanelStat> Stats, long Speed,
        IReadOnlyList<PanelMonsterDrop> Drops);

    /// <summary>Monster rows a player cannot tell apart; <see cref="Rep"/> is the first by id.</summary>
    private sealed record MonsterGroup(MonsterEntry Rep, List<MonsterEntry> Members)
    {
        public List<long> Ids => [.. Members.Select(m => m.Summary.Id)];
    }

    private sealed record ItemEntry(PanelItemSummary Summary, ItemConfig Row);

    /// <summary>Everything the databases show, from the game data, in memory.</summary>
    private sealed class PanelCatalog
    {
        private readonly PanelBackend _backend;
        private readonly Dictionary<long, ItemEntry> _items = [];
        /// <summary>Every named item id to the ids of its group (itself included), first id first.</summary>
        private readonly Dictionary<long, List<long>> _itemGroups = [];
        /// <summary>The list summaries (first id of each group) by name, unfinished names included.</summary>
        private readonly Dictionary<string, List<PanelItemSummary>> _itemNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<MonsterGroup>> _monsterNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
        private readonly Dictionary<long, List<PanelItemDrop>> _droppedBy = [];
        private readonly Dictionary<long, List<PanelShopOffer>> _shops = [];
        private readonly Dictionary<long, JsonElement> _cards = [];

        /// <summary>The item list: one summary per group, without the unfinished names.</summary>
        public List<PanelItemSummary> Items { get; }
        /// <summary>Every named item row (the economy log's name search finds every id).</summary>
        public List<PanelItemSummary> AllItems { get; }
        public List<MonsterGroup> Monsters { get; } = [];
        /// <summary>Every monster id to its group.</summary>
        public Dictionary<long, MonsterGroup> MonstersById { get; } = [];
        public IReadOnlyList<PanelOption> Types { get; }
        public IReadOnlyList<PanelOption> Qualities { get; }
        public IReadOnlyList<PanelOption> Elements { get; }
        public IReadOnlyList<PanelOption> Races { get; }
        public IReadOnlyList<PanelOption> Sizes { get; }

        public PanelCatalog(PanelBackend backend)
        {
            _backend = backend;
            var data = backend.Tables;
            foreach (var card in CardConfig.All(data).OrderBy(c => c.Level))
                _cards.TryAdd(card.CardId, card.Row);
            foreach (var row in ItemConfig.All(data))
            {
                string name = backend.Name(row.Name);
                if (name.Length == 0) continue; // a row without a name is not shown in the game either
                _items[row.Id] = new ItemEntry(new PanelItemSummary(row.Id, name, (int)row.Type, backend.TypeName(row.Type),
                    backend.SubTypeName(row.Type, row.SubType), (int)row.Quality, backend.QualityName(row.Quality), (int)row.LevelNeed,
                    backend.ItemIcon(row)), row);
            }
            AllItems = [.. _items.Values.Select(i => i.Summary).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id)];
            Items = GroupItems();
            Types = [.. Items.Select(i => (long)i.Type).Distinct().Order().Select(t => new PanelOption(t, backend.TypeName(t)))];
            Qualities = [.. Enumerable.Range(1, QualityNames.Length).Select(q => new PanelOption(q, backend.QualityName(q)))];
            Elements = [.. Enumerable.Range(1, ElementNames.Length).Select(e => new PanelOption(e, backend.ElementName(e)))];
            Races = [.. Enumerable.Range(1, RaceNames.Length).Select(r => new PanelOption(r, backend.RaceName(r)))];
            Sizes = [.. Enumerable.Range(1, SizeNames.Length).Select(s => new PanelOption(s, backend.SizeName(s)))];
            ReadShops(data);
            ReadMonsters(data);
        }

        /// <summary>
        /// (inferred) An unfinished name: the placeholder "TBD", or a "@{n}" its row gives no value for.
        /// </summary>
        public static bool Hidden(string name) => name.Trim().Equals("TBD", StringComparison.OrdinalIgnoreCase) || name.Contains("@{", StringComparison.Ordinal);

        public PanelItemSummary? ItemSummary(long id) => _items.TryGetValue(id, out var entry) ? entry.Summary : null;

        /// <summary>The ids of an item's group (rows that differ only in id and sort order), itself included.</summary>
        public List<long> IdsOf(long id) => _itemGroups.GetValueOrDefault(id) ?? [id];

        /// <summary>Every monster group of a name.</summary>
        public List<MonsterGroup> MonstersNamed(string name) => _monsterNames.GetValueOrDefault(name) ?? [];

        /// <summary>
        /// Items a player cannot tell apart are one group: the same name, type and kind, quality, level, description,
        /// stats, trade, stack and classes. Returns the list's rows, the first id of each group.
        /// </summary>
        private List<PanelItemSummary> GroupItems()
        {
            var list = new List<PanelItemSummary>();
            foreach (var group in _items.Values.OrderBy(i => i.Summary.Id).GroupBy(ItemKey))
            {
                var ids = group.Select(i => i.Summary.Id).ToList();
                foreach (long id in ids) _itemGroups[id] = ids;
                var first = group.First().Summary;
                if (!_itemNames.TryGetValue(first.Name, out var named)) _itemNames[first.Name] = named = [];
                named.Add(first);
                if (!Hidden(first.Name)) list.Add(first);
            }
            return [.. list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id)];
        }

        private string ItemKey(ItemEntry entry)
        {
            var (s, row) = (entry.Summary, entry.Row);
            string stats = string.Join(';', Stats(row).Select(x => x.Name + "=" + x.Value));
            return string.Join('\u001f', s.Name, s.Type.ToString(CultureInfo.InvariantCulture), s.SubType, s.Quality.ToString(CultureInfo.InvariantCulture),
                s.Level.ToString(CultureInfo.InvariantCulture), Description(row), stats, row.Trade.ToString(CultureInfo.InvariantCulture),
                row.StackLimit.ToString(CultureInfo.InvariantCulture), string.Join(',', row.JobNeed));
        }

        private string Description(ItemConfig row) => ColourMark().Replace(_backend.Tables.TextOf(row.Description), "").Trim();

        /// <summary>Shop goods from ShopCommodityConfig and the shop of their _iClassID, except Survivor Battle's match-only
        /// shops (type 8); a fashion row without an item is a look, not an item.</summary>
        private void ReadShops(GameData data)
        {
            var shops = ShopConfig.All(data).GroupBy(s => s.ClassId).ToDictionary(g => g.Key, g => g.First());
            foreach (var goods in ShopCommodityConfig.All(data).Where(c => c.ItemId > 0))
            {
                if (!shops.TryGetValue(goods.ClassId, out var shop) || shop.ShopType == 8) continue;
                long currency = goods.ItemPerPrice.Length > 0 ? goods.ItemPerPrice[0] : 0, price = goods.ItemPerPrice.Length > 1 ? goods.ItemPerPrice[1] : 0;
                if (!_shops.TryGetValue(goods.ItemId, out var list)) _shops[goods.ItemId] = list = [];
                var offer = new PanelShopOffer(_backend.Name(shop.ShopName) is { Length: > 0 } n ? n : "Shop " + shop.ClassId.ToString(CultureInfo.InvariantCulture),
                    currency, currency > 0 ? data.ItemName(currency) : "", price, (int)Math.Max(1, goods.ItemPerMount));
                if (!list.Contains(offer)) list.Add(offer);
            }
        }

        /// <summary>
        /// Every monster row (NPCConfig _iNPCType 1 with a name and a level, the developers' own rows left out), grouped
        /// as a player sees them: the same name, level, HP and other numbers, element, race, size and rank. What each
        /// row drops is kept per row; the item pages list the group.
        /// </summary>
        private void ReadMonsters(GameData data)
        {
            var mvps = MVPConfig.All(data).Select(m => m.NPCId).ToHashSet();
            var entries = new List<MonsterEntry>();
            foreach (var npc in NPCConfig.All(data).Where(n => n.NPCType == 1).OrderBy(n => n.Id))
            {
                string name = _backend.Name(npc.Name);
                if (name.Length == 0 || npc.Level <= 0 || DeveloperRow().IsMatch(name)) continue;
                entries.Add(Monster(npc, name, mvps.Contains(npc.Id)));
            }
            foreach (var members in entries.GroupBy(MonsterKey).Select(g => g.ToList()))
            {
                var group = new MonsterGroup(members[0], members);
                foreach (var member in members) MonstersById[member.Summary.Id] = group;
                if (!_monsterNames.TryGetValue(group.Rep.Summary.Name, out var named)) _monsterNames[group.Rep.Summary.Name] = named = [];
                named.Add(group);
                if (!Hidden(group.Rep.Summary.Name)) Monsters.Add(group);
                AddDroppedBy(group);
            }
            Monsters.Sort((a, b) => a.Rep.Summary.Level != b.Rep.Summary.Level ? a.Rep.Summary.Level.CompareTo(b.Rep.Summary.Level)
                : string.Compare(a.Rep.Summary.Name, b.Rep.Summary.Name, StringComparison.OrdinalIgnoreCase));
        }

        private static string MonsterKey(MonsterEntry m)
        {
            var s = m.Summary;
            return string.Join('\u001f', s.Name, s.Level.ToString(CultureInfo.InvariantCulture), s.Hp.ToString(CultureInfo.InvariantCulture),
                s.Element.ToString(CultureInfo.InvariantCulture), s.Race, s.Size, s.Rank, m.Speed.ToString(CultureInfo.InvariantCulture),
                string.Join(';', m.Stats.Select(x => x.Name + "=" + x.Value)));
        }

        /// <summary>The item pages' "dropped by": each group once per distinct way it drops the item, under its first id.</summary>
        private void AddDroppedBy(MonsterGroup group)
        {
            var rep = group.Rep.Summary;
            foreach (var drop in group.Members.SelectMany(m => m.Drops).Where(d => d.ItemId is not (BaseExpItem or JobExpItem)))
            {
                if (!_droppedBy.TryGetValue(drop.ItemId, out var list)) _droppedBy[drop.ItemId] = list = [];
                var row = new PanelItemDrop(rep.Id, rep.Name, rep.Level, rep.Rank, drop.Chance, drop.Min, drop.Max, drop.Kind, rep.Icon);
                if (!list.Contains(row)) list.Add(row);
            }
        }

        private MonsterEntry Monster(NPCConfig npc, string name, bool mvp)
        {
            var data = _backend.Tables;
            int level = (int)npc.Level;
            var attr = MonsterService.TemplateAttributes(data, npc.Row, level);
            long A(long id) => (long)Math.Round(attr.GetValueOrDefault(id));
            long speed = attr.ContainsKey(113) ? A(113) : npc.Speed;
            var summary = new PanelMonsterSummary(npc.Id, name, level, Math.Max(1, A(101)), (int)npc.Element, _backend.ElementName(npc.Element),
                _backend.RaceName(npc.Race), _backend.SizeName(npc.Size), Rank(npc.NPCSubType, mvp), _backend.Image("monsters", npc.HeadIcon));
            // HIT and FLEE default to level + 1 and level as MonsterService.Build reads them.
            var stats = new List<PanelStat>();
            foreach (long id in new long[] { 101, 102, 103, 104, 105, 106, 109, 110, 111 })
            {
                double value = id switch
                {
                    109 when !attr.ContainsKey(109) => level + 1,
                    110 when !attr.ContainsKey(110) => level,
                    101 => Math.Max(1, A(101)),
                    _ => attr.GetValueOrDefault(id),
                };
                if (id is 101 or 103 or 104 or 105 or 106 or 109 or 110 || value != 0)
                    stats.Add(new PanelStat(_backend.AttributeName(id), _backend.AttributeValue(id, value)));
            }
            return new MonsterEntry(summary, npc.Race, npc.Size, stats, speed, Drops(npc));
        }

        /// <summary>
        /// A kill's loot as RewardService rolls it (bonus time off): every pool slot [group, chance per million, rolls,
        /// picks] of each drop row, each DropGroupConfig row weighted among its group's candidates. The chance shown is
        /// that at least one drops per kill; the player's level and class limits of a group row are not applied
        /// (inferred: the page has no player).
        /// </summary>
        private List<PanelMonsterDrop> Drops(NPCConfig npc)
        {
            var drops = PoolDrops(RewardService.KillDropRows(data: _backend.Tables, npc.Row, bonus: false)
                .SelectMany(r => RewardService.DropPools(r.Row).Select(pool => (pool, r.Maps))), "", 1);
            foreach (var mvp in MVPConfig.All(_backend.Tables).Where(m => m.NPCId == npc.Id).Take(1))
                drops.AddRange(MvpRewards(mvp));
            return [.. drops.Take(DetailRows)];
        }

        /// <summary>
        /// The rewards of a field MVP's kill as MvpService.SettleAsync hands them out, each kind rolled through its award's
        /// pool (RewardService.AwardPool): first strike, final blow, the top 10 parties, every participant, and one rare
        /// winner per kill at _iRareRewardProbability / 10000, who may also get a Lord's Mane (Ro3Emu:LordsManePercent).
        /// Each kind is capped per day per player (3 first strikes, 3 final blows, 3 rankings, 5 participations).
        /// </summary>
        private IEnumerable<PanelMonsterDrop> MvpRewards(MVPConfig mvp)
        {
            var data = _backend.Tables;
            double rare = Math.Clamp(mvp.RareRewardProbability / 10000.0, 0, 1);
            var kinds = new (string Kind, long Award, double Chance)[]
            {
                ("First strike", mvp.FirstDamageAward, 1), ("Final blow", mvp.LastDamageAward, 1), ("Top 10 parties", mvp.TeamAward, 1),
                ("Participation", mvp.PartAward, 1), ("Rare winner", mvp.RareAward, rare),
            };
            foreach (var (kind, award, chance) in kinds)
                if (RewardService.AwardPool(data, award) is var pool and > 0)
                    foreach (var drop in PoolDrops([(pool, [])], kind, chance)) yield return drop;
            int mane = Math.Clamp(_backend.LordsManePercent, 0, 100);
            if (mane > 0 && _items.TryGetValue(LordsMane, out var item))
                yield return new PanelMonsterDrop(LordsMane, item.Summary.Name, item.Summary.Quality, mane / 100.0, 1, 1, [], "Rare winner", item.Summary.Icon);
        }

        /// <summary>Lord's Mane: MvpService gives one per kill at LordsManePercent to a member picked as the rare winner is.</summary>
        private const long LordsMane = 1009015;

        /// <summary>
        /// Every item the pools can give, with the chance that at least one comes per roll of the whole list (each pool
        /// slot [group, chance per million, rolls, picks], each DropGroupConfig row weighted among its group's
        /// candidates), the whole list itself happening at <paramref name="listChance"/>.
        /// </summary>
        private List<PanelMonsterDrop> PoolDrops(IEnumerable<(long Pool, IReadOnlyList<long> Maps)> pools, string kind, double listChance)
        {
            var data = _backend.Tables;
            var merged = new Dictionary<(long Item, string Maps), (double Miss, int Min, int Max, IReadOnlyList<long> Maps)>();
            foreach (var (poolId, maps) in pools)
            {
                if (data.Row("DropPoolConfig", poolId) is not { } pool) continue;
                foreach (var (group, chance, rolls, picks) in RewardService.PoolSlots(pool))
                {
                    var candidates = DropGroupConfig.Get(data, group).Where(r => r.Weights > 0 && r.QuestLimit == 0 && r.Activityid == 0).ToList();
                    double total = candidates.Sum(r => (double)r.Weights);
                    if (total <= 0) continue;
                    double hit = Math.Clamp(chance / 1_000_000, 0, 1);
                    foreach (var candidate in candidates)
                    {
                        long item = candidate.DropItem;
                        if (!_items.ContainsKey(item)) continue;
                        double share = candidate.Weights / total;
                        double missRoll = 1 - hit + hit * Math.Pow(1 - share, picks);
                        double miss = Math.Pow(missRoll, rolls);
                        var key = (item, string.Join(',', maps));
                        int min = (int)Math.Max(1, candidate.MinNumber), max = (int)Math.Max(min, candidate.MaxNumber);
                        merged[key] = merged.TryGetValue(key, out var seen)
                            ? (seen.Miss * miss, Math.Min(seen.Min, min), Math.Max(seen.Max, max), maps)
                            : (miss, min, max, maps);
                    }
                }
            }
            return [.. merged.Select(m => new PanelMonsterDrop(m.Key.Item, _items[m.Key.Item].Summary.Name, _items[m.Key.Item].Summary.Quality,
                    listChance * (1 - m.Value.Miss), m.Value.Min, m.Value.Max, [.. m.Value.Maps.Select(_backend.MapName)], kind, _items[m.Key.Item].Summary.Icon))
                .OrderByDescending(d => d.Chance).ThenBy(d => d.Item, StringComparer.OrdinalIgnoreCase)];
        }

        /// <summary>One item: its own row's numbers, and the drops and shops of every id of its group.</summary>
        public PanelItem? Item(long id)
        {
            if (!_items.TryGetValue(id, out var entry)) return null;
            var row = entry.Row;
            var ids = IdsOf(id);
            var classes = row.JobNeed.Where(j => j > 0).Select(j => _backend.ClassName(j)).ToList();
            string trade = row.Trade switch { 1 => "Exchange", 2 => "Stalls", _ => "Not tradeable" };
            // The variants of its name (an unfinished name's rows are no variants of each other), their drops and shops together.
            var variants = Hidden(entry.Summary.Name) ? [_items[ids[0]].Summary]
                : (_itemNames.GetValueOrDefault(entry.Summary.Name) ?? []).OrderBy(v => v.Level).ThenBy(v => v.Quality).ThenBy(v => v.Id).ToList();
            var dropped = variants.SelectMany(v => IdsOf(v.Id).SelectMany(i => _droppedBy.GetValueOrDefault(i) ?? []).Select(d => d with { ItemId = v.Id }))
                .Distinct().OrderByDescending(d => d.Chance).ThenBy(d => d.Level).Take(DetailRows).ToList();
            var shops = variants.SelectMany(v => IdsOf(v.Id)).SelectMany(i => _shops.GetValueOrDefault(i) ?? []).Distinct().ToList();
            string picture = row.Pic.StartsWith("item_null", StringComparison.OrdinalIgnoreCase) ? "" : _backend.Image("items", row.Pic);
            return new PanelItem(entry.Summary, Description(row), (int)row.StackLimit, trade, row.IsUse == 1, classes, Stats(row), dropped, shops,
                ids, variants, picture);
        }

        /// <summary>Equipment: EquipConfig _kBasicAttribute [attribute, min, max]; a card: the lowest level's
        /// CardConfig _kBasicAttribute [attribute, value].</summary>
        private List<PanelStat> Stats(ItemConfig item)
        {
            var data = _backend.Tables;
            var stats = new List<PanelStat>();
            if (item.EquipId > 0 && EquipConfig.Get(data, item.EquipId) is { } equip)
            {
                foreach (var a in equip.BasicAttribute.Where(a => a.Length >= 2))
                {
                    string low = _backend.AttributeValue(a[0], a[1]), high = a.Length > 2 && a[2] != a[1] ? _backend.AttributeValue(a[0], a[2]) : "";
                    stats.Add(new PanelStat(_backend.AttributeName(a[0]), "+" + low + (high.Length > 0 ? "-" + high : "")));
                }
            }
            if (_cards.TryGetValue(item.Id, out var cardRow))
                foreach (var a in new CardConfig(cardRow).BasicAttribute.Where(a => a.Length >= 2))
                    stats.Add(new PanelStat(_backend.AttributeName(a[0]), "+" + _backend.AttributeValue(a[0], a[1])));
            return stats;
        }
    }
}
