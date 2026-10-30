using System.Globalization;
using System.Text.RegularExpressions;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Http;

/// <summary>The admin's economy log: the server's own ledger (economy_entry and operation_receipt), read-only.</summary>
public sealed partial class PanelBackend
{
    /// <summary>An item named instead of an id matches at most this many items (exact names first).</summary>
    private const int AssetMatches = 20;

    public async Task<PanelEconomyPage> EconomyAsync(PanelEconomyQuery query)
    {
        var assets = new List<PanelOption>();
        if (query.Asset > 0) assets.Add(new PanelOption(query.Asset, AssetName(query.Asset)));
        else if (query.AssetText.Length > 0)
        {
            var catalog = await CatalogAsync();
            assets.AddRange(catalog.AllItems.Where(i => i.Name.Contains(query.AssetText, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => !string.Equals(i.Name, query.AssetText, StringComparison.OrdinalIgnoreCase)).ThenBy(i => i.Name.Length)
                .Take(AssetMatches).Select(i => new PanelOption(i.Id, i.Name)));
            // A name nothing carries matches nothing, not everything.
            if (assets.Count == 0) return new PanelEconomyPage([], 0, 0, 0, [], query.Page, query.PageSize);
        }
        var filter = new PanelEconomyFilter(query.Account, query.Character, [.. assets.Select(a => a.Id)],
            query.From?.ToUnixTimeMilliseconds(), query.To?.ToUnixTimeMilliseconds());
        var (total, sumIn, sumOut, rows) = await accounts.ReadAsync<(int, long, long, List<PanelEconomyRow>)>(
            new PanelEconomyRead(filter, (query.Page - 1) * query.PageSize, query.PageSize));
        return new PanelEconomyPage([.. rows.Select(Entry)], total, sumIn, sumOut, assets, query.Page, query.PageSize);
    }

    private PanelEconomyEntry Entry(PanelEconomyRow r) => new(r.Id, DateTimeOffset.FromUnixTimeMilliseconds(r.RecordedMs), r.Account,
        Holder(r.OwnerKind, r.OwnerId, r.OwnerName), r.Command, Words(((EconomyReason)r.Reason).ToString()),
        Words(((EconomyLegKind)r.LegKind).ToString()), r.AssetId,
        r.AssetKind is (int)EconomyAssetKind.Item or (int)EconomyAssetKind.Currency ? AssetName(r.AssetId)
            : Words(((EconomyAssetKind)r.AssetKind).ToString()) + " " + r.AssetId.ToString(CultureInfo.InvariantCulture),
        r.Delta, Words(((EconomyCompartment)r.Compartment).ToString()),
        r.CounterpartyKind is { } kind ? Holder(kind, r.CounterpartyId ?? "", r.CounterpartyName) : "", r.Operation);

    private string AssetName(long id) => data.ItemName(id) is { Length: > 0 } name ? name : "Item " + id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Who holds the asset: a character by name (its id when it was deleted), a guild, the system, escrow.</summary>
    private static string Holder(int kind, string id, string name) => (EconomyOwnerKind)kind switch
    {
        EconomyOwnerKind.Role => name.Length > 0 ? name : "Character " + id,
        EconomyOwnerKind.Guild => "Guild " + id,
        EconomyOwnerKind.System => "System",
        EconomyOwnerKind.Escrow => "Escrow",
        EconomyOwnerKind.Recovery => "Recovery",
        _ => id,
    };

    /// <summary>MonsterDrop -> "Monster drop", NpcShopGoods -> "NPC shop goods"; an unknown persisted value stays its number.</summary>
    private static string Words(string name)
    {
        if (name.Length == 0 || !char.IsLetter(name[0])) return name;
        string words = char.ToUpperInvariant(name[0]) + WordBreak().Replace(name[1..], m => " " + char.ToLowerInvariant(m.Value[0]));
        return words.StartsWith("Npc ", StringComparison.Ordinal) ? "NPC" + words[3..]
            : words.StartsWith("Gm ", StringComparison.Ordinal) ? "GM" + words[2..] : words;
    }

    [GeneratedRegex("[A-Z]")]
    private static partial Regex WordBreak();
}
