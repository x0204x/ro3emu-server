using System.Globalization;
using System.Text.Json;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The admin's item tracking and duplication check, over the server's committed state on the read pool. An item is
/// counted where the server keeps it: the item table (bags, the cart, worn), the unclaimed letters of every mailbox,
/// the exchange's listings, the stalls' stock and the open auction lots. Other holding places of the economy
/// (EconomyCompartment: caravan, escort and territory pendings, recovery) are not counted, so the held totals are a
/// lower bound.
/// </summary>
public sealed partial class PanelBackend
{
    private const int TraceEntries = 25, DupeRows = 200;
    private static readonly string[] HoldingKinds = [TradeService.Listings, StallsService.Kind];

    /// <summary>One stack wherever it is held; <see cref="Guid"/> 0 for a configured mail grant or an auction lot.</summary>
    private sealed record HeldStack(long Entry, string Place, string Owner, long RoleId, string Account, long Count, long Guid);

    public async Task<PanelItemTrace?> TraceItemAsync(long id)
    {
        var catalog = await CatalogAsync();
        if (catalog.ItemSummary(id) is not { } item) return null;
        var kits = await UnrecordedKitsAsync();
        var ledger = Ledger(await accounts.ReadAsync<List<PanelLedgerRow>>(new PanelLedgerTotalsRead(id)), id, kits);
        var recent = (await EconomyAsync(new PanelEconomyQuery("", "", id, "", null, null, 1, TraceEntries))).Entries;
        if (ItemConfig.Get(data, id)?.Type == (long)ItemTypeEnum.ItemTypeCurrency)
            return new PanelItemTrace(item, true, [], 0, ledger, recent, []);
        var stacks = await HeldStacksAsync(id);
        var holders = stacks.GroupBy(s => (s.Place, s.RoleId, s.Owner, s.Account))
            .Select(g => new PanelHolding(g.Key.Place, g.Key.Owner, g.Key.RoleId, g.Key.Account, g.Sum(s => s.Count),
                [.. g.Select(s => s.Guid).Where(guid => guid != 0).Order()]))
            .OrderByDescending(h => h.Count).ThenBy(h => h.Owner, StringComparer.OrdinalIgnoreCase).ToList();
        long held = stacks.Sum(s => s.Count);
        return new PanelItemTrace(item, false, holders, held, ledger, recent, Flags(stacks, held, ledger));
    }

    public async Task<PanelDupeReport> DupeCheckAsync()
    {
        var catalog = await CatalogAsync();
        var ledgerRows = await accounts.ReadAsync<List<PanelLedgerRow>>(new PanelLedgerTotalsRead(null));
        var kits = await UnrecordedKitsAsync();
        var byAsset = ledgerRows.GroupBy(r => r.Asset).ToDictionary(g => g.Key, g => Ledger(g, g.Key, kits));
        var stacks = await HeldStacksAsync(null);
        var rows = new List<PanelDupeRow>();
        foreach (var group in stacks.GroupBy(s => s.Entry))
        {
            if (catalog.ItemSummary(group.Key) is not { } item) continue;
            var ledger = byAsset.GetValueOrDefault(group.Key) ?? Ledger([], group.Key, kits);
            long held = group.Sum(s => s.Count);
            var flags = Flags([.. group], held, ledger);
            if (flags.Count > 0) rows.Add(new PanelDupeRow(item, held, ledger.Net, flags));
        }
        long first = ledgerRows.Count > 0 ? ledgerRows.Min(r => r.FirstMs) : 0;
        return new PanelDupeReport([.. rows.OrderBy(r => r.Flags.Min(f => Severity(f.Kind))).ThenByDescending(r => r.Held - r.Net).Take(DupeRows)],
            stacks.Select(s => s.Entry).Distinct().Count(), first > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(first) : null);
    }

    /// <summary>
    /// (inferred) The duplication rules. The server's own economy invariant (EconomyExactnessChecker) is that what is
    /// held changes by exactly the ledger's sources minus its sinks; from an empty realm, what is held can never exceed
    /// them. So: one instance guid in two places (an item table row is unique, a clone in escrow keeps its guid only
    /// while the row is gone); more held than the ledger let in; held while the ledger never recorded the item (an item
    /// older than the ledger, or one that came in around it).
    /// </summary>
    private static List<PanelDupeFlag> Flags(IReadOnlyList<HeldStack> stacks, long held, PanelLedgerTotals ledger)
    {
        var flags = stacks.Where(s => s.Guid != 0).GroupBy(s => s.Guid).Where(g => g.Count() > 1 && !PartialListing([.. g]))
            .Select(g => new PanelDupeFlag("Instance seen twice", string.Join(" and ", g.Select(s => $"{s.Place} of {s.Owner}")), g.Key)).ToList();
        if (ledger.Entries == 0 && held > ledger.StarterKits)
            flags.Add(new PanelDupeFlag("Unrecorded", $"{held.ToString("N0", CultureInfo.InvariantCulture)} held, the ledger never recorded this item."));
        else if (ledger.Entries > 0 && held > ledger.Explained)
            flags.Add(new PanelDupeFlag("Exceeds ledger", $"{held.ToString("N0", CultureInfo.InvariantCulture)} held, the ledger let in " +
                $"{ledger.Entered.ToString("N0", CultureInfo.InvariantCulture)} and out {(-ledger.Left).ToString("N0", CultureInfo.InvariantCulture)}" +
                (ledger.StarterKits > 0 ? $" (and {ledger.StarterKits.ToString("N0", CultureInfo.InvariantCulture)} in starter kits it never recorded)" : "") +
                $": {(held - ledger.Explained).ToString("N0", CultureInfo.InvariantCulture)} more than it explains."));
        return flags;
    }

    /// <summary>
    /// One seller's stack and the part of it they listed: an exchange listing or a stall offer of part of a stack keeps the
    /// stack's guid in escrow while the rest stays in the bag (TradeService.Mutations, StallsService.Player), and a stall's
    /// partial take-back mails that clone. Whatever the escrow part is later claimed into gets a new guid. Not a duplicate.
    /// </summary>
    private static bool PartialListing(IReadOnlyList<HeldStack> stacks) =>
        stacks.Select(s => s.RoleId).Distinct().Count() == 1 && stacks.Count(s => s.Place is "Bag" or "Worn" or "Cart") <= 1;

    private static int Severity(string kind) => kind switch { "Instance seen twice" => 0, "Exceeds ledger" => 1, _ => 2 };

    /// <summary>
    /// (inferred) Characters that got their starter kit before the kit was written to the ledger (no
    /// CharacterService.StarterKitEconomyKind receipt): their kits are explained, though the ledger never saw them.
    /// </summary>
    private async Task<long> UnrecordedKitsAsync() =>
        await accounts.ReadAsync<long>(new PanelUnrecordedStarterKitsRead(CharacterService.StarterKitEconomyKind));

    /// <summary>An asset's source and sink legs (EconomyLegKind) added up, with what <paramref name="kits"/> unrecorded
    /// starter kits hold of it; transfers only move what is already held.</summary>
    private static PanelLedgerTotals Ledger(IEnumerable<PanelLedgerRow> rows, long asset, long kits)
    {
        long starter = kits * CharacterService.StarterKit.Where(k => k.Entry == asset).Sum(k => (long)k.Count);
        var mine = rows.Where(r => r.Asset == asset).ToList();
        long first = mine.Count > 0 ? mine.Min(r => r.FirstMs) : 0, last = mine.Count > 0 ? mine.Max(r => r.LastMs) : 0;
        return new PanelLedgerTotals(mine.Where(r => r.LegKind == (int)EconomyLegKind.Source).Sum(r => r.Sum),
            mine.Where(r => r.LegKind == (int)EconomyLegKind.Sink).Sum(r => r.Sum), mine.Sum(r => r.Count),
            first > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(first) : null, last > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(last) : null, starter);
    }

    /// <summary>Every stack of the item (or of every item) where the server keeps it.</summary>
    private async Task<List<HeldStack>> HeldStacksAsync(long? entry)
    {
        var rows = await accounts.ReadAsync<PanelHoldingsRows>(new PanelHoldingsRead(entry, HoldingKinds));
        bool Wanted(long e) => entry == null || e == entry;
        var stacks = rows.Items.Select(i => new HeldStack(i.Entry, i.Grid > 0 ? "Worn" : i.Pack == CartPack ? "Cart" : "Bag", i.Name, i.RoleId,
            i.Account, i.Stack, i.Guid)).ToList();
        foreach (var box in rows.Mailboxes)
            foreach (var letter in Decode<MailService.Mailbox>(System.Text.Encoding.UTF8.GetString(box.Mailbox))?.Letters.Where(l => !l.Claimed) ?? [])
            {
                stacks.AddRange(letter.Attachments.Where(a => Wanted(a.Entry) && a.Count > 0)
                    .Select(a => new HeldStack(a.Entry, "Mail", box.Name, box.RoleId, box.Account, a.Count, 0)));
                stacks.AddRange(letter.Items.Where(i => Wanted(i.Entry) && i.Stack > 0)
                    .Select(i => new HeldStack(i.Entry, "Mail", box.Name, box.RoleId, box.Account, i.Stack, i.Guid)));
            }
        var market = MarketStacks(rows.Market, Wanted);
        var names = await accounts.ReadAsync<Dictionary<long, (string Name, int Career, int Level)>>(
            new PanelRoleNamesRead([.. market.Select(s => s.RoleId).Where(r => r > 0).Distinct()]));
        stacks.AddRange(market.Select(s => s.RoleId > 0 && names.TryGetValue(s.RoleId, out var who) ? s with { Owner = who.Name } : s));
        stacks.AddRange(rows.Auctions.Where(a => Wanted(a.Entry)).Select(a => new HeldStack(a.Entry, "Auction", "System", 0, "", 1, 0)));
        return stacks;
    }

    /// <summary>The exchange's listings and the stalls' stock (escrow clones of the seller's items).</summary>
    private static List<HeldStack> MarketStacks(List<(string Kind, string Data)> records, Func<long, bool> wanted)
    {
        var stacks = new List<HeldStack>();
        foreach (var (kind, json) in records)
        {
            if (kind == TradeService.Listings && Decode<TradeService.Listing>(json) is { Quantity: > 0 } listing && wanted(listing.Entry))
                stacks.Add(new HeldStack(listing.Entry, "Exchange", "Character " + listing.SellerId.ToString(CultureInfo.InvariantCulture),
                    listing.SellerId, "", listing.Quantity, listing.Item.Guid));
            if (kind == StallsService.Kind && Decode<StallsService.StallRow>(json) is { Ended: false } stall)
                stacks.AddRange(stall.Stock.Where(i => i.Item.Stack > 0 && wanted(i.Item.Entry)).Select(i => new HeldStack(i.Item.Entry, "Stall",
                    stall.SystemId > 0 ? "System" : stall.OwnerName, stall.SystemId > 0 ? 0 : stall.Owner, "", i.Item.Stack, i.Item.Guid)));
        }
        return stacks;
    }
}
