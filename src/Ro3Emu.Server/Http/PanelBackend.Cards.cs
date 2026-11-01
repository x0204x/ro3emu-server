using System.Globalization;
using Ro3Emu.Panel;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The card database. A card is an item (ItemConfig _iType 4) with four CardConfig rows, one per star (_iTier 0-3):
/// each star's _kBasicAttribute and special effects (_kSkillAttribute, a Flash copy's _kFlashAttribute), the level cap
/// (_iLevel) and the copies the next star costs (_kCost). A card's level adds CardUpgradeConfig _iAttr of its part and
/// quality on top (CharacterService.EntityData), as CardService levels and stars it.
/// </summary>
public sealed partial class PanelBackend
{
    private sealed record CardEntry(PanelCardSummary Summary, ItemConfig Item, List<CardConfig> Tiers);

    public async Task<PanelCardList> CardsAsync()
    {
        var cards = await CardEntriesAsync();
        var listed = cards.Values.Where(c => c.Tiers.Any(t => t.IsOpen == 1)).Select(c => c.Summary)
            .OrderBy(c => c.Part).ThenByDescending(c => c.Quality).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new PanelCardList(listed,
            [.. listed.Select(c => c.Part).Distinct().Order().Select(p => new PanelOption(p, CardPartName(p)))],
            [.. listed.Select(c => c.Quality).Distinct().Order().Select(q => new PanelOption(q, QualityLabel(q)))]);
    }

    public async Task<PanelCard?> CardAsync(long id)
    {
        if (!(await CardEntriesAsync()).TryGetValue(id, out var card)) return null;
        var item = card.Item;
        var tiers = card.Tiers.Select(t => new PanelCardTier((int)t.Tier, (int)t.Level, (int)t.Power, CardEffects(t),
            [.. t.FlashAttribute.SelectMany(EffectLines)], t.Cost.Where(c => c.Length >= 2).Sum(c => c[1]))).ToList();
        var levels = CardUpgradeConfig.All(data).Where(u => u.Parts == item.CardPart && u.Quality == item.Quality).OrderBy(u => u.Level)
            .Select(u => new PanelCardLevel((int)u.Level, [.. u.Attr.Where(a => a.Length >= 2).Select(a => AttributeLine(a[0], a[1]))], Amounts(u.Cost)))
            .ToList();
        var rules = new List<string>();
        if (item.IfSameCard == 1) rules.Add("Only one of this card per piece of equipment.");
        if (item.IsEleCard == 1) rules.Add("An element card: it sets the equipment's element.");
        var refund = CardReturnCostConfig.All(data).Where(r => r.Quality == item.Quality).SelectMany(r => r.Cost).FirstOrDefault(c => c.Length >= 2);
        var drops = (await CatalogAsync()).Item(id)?.DroppedBy ?? [];
        return new PanelCard(card.Summary, tiers, levels, drops, Amounts(item.Decompose), rules, refund != null ? Amount(refund[0], refund[1]) : null);
    }

    /// <summary>A star's effects: its attributes, then its special effects (LC_CardHelper.GetCardAttributes).</summary>
    private List<IReadOnlyList<PanelRun>> CardEffects(CardConfig tier) =>
        [.. tier.BasicAttribute.Where(a => a.Length >= 2).Select(a => (IReadOnlyList<PanelRun>)AttributeRuns(a[0], a[1])),
            .. tier.SkillAttribute.SelectMany(EffectLines)];

    private Task<Dictionary<long, CardEntry>> CardEntriesAsync() => CachedAsync("cards", () => Task.FromResult(BuildCards()));

    private Dictionary<long, CardEntry> BuildCards()
    {
        var cards = new Dictionary<long, CardEntry>();
        foreach (var rows in CardConfig.All(data).GroupBy(c => c.CardId))
        {
            if (ItemConfig.Get(data, rows.Key) is not { } item || Name(item.Name) is not { Length: > 0 } name) continue;
            var tiers = rows.OrderBy(t => t.Tier).ToList();
            string summary = string.Join(" · ", CardEffects(tiers[^1]).Select(e => string.Concat(e.Select(r => r.Text))));
            string picture = item.Pic.StartsWith("item_null", StringComparison.OrdinalIgnoreCase) ? "" : Image("items", item.Pic);
            cards[rows.Key] = new CardEntry(new PanelCardSummary(rows.Key, name, (int)item.Quality, QualityLabel(item.Quality), (int)item.CardPart,
                CardPartName(item.CardPart), ItemIcon(item), picture, summary, tiers.Any(t => t.ShowLibrary == 1)), item, tiers);
        }
        return cards;
    }

    /// <summary>
    /// ItemConfig _iCardPart, the equipment a card goes into (CardService.GridsFor): the client's names (UIWords
    /// 30004-30013) are not in the export, so the kinds' names stand in, from the equipment kinds where the client has
    /// them; parts 8 and 9 are the MVP sketch cards (inferred).
    /// </summary>
    private string CardPartName(long part) => part switch
    {
        1 => "Weapon",
        2 => EnumText(10100000057, "Shield"),
        3 => EnumText(10100000058, "Clothes"),
        4 => EnumText(10100000059, "Muffler"),
        5 => EnumText(10100000060, "Shoes"),
        6 => EnumText(10100000061, "Accessory"),
        7 => "Headgear",
        8 => "Sketch I",
        9 => "Sketch II",
        _ => "Part " + part.ToString(CultureInfo.InvariantCulture),
    };
}
