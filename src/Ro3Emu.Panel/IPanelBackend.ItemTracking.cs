namespace Ro3Emu.Panel;

/// <summary>
/// The admin's item tracking: who holds an item now (bags, the cart, worn, unclaimed mail, exchange listings, stalls,
/// auction lots), how it moved (the economy ledger) and a duplication check (inferred rules, docs/guide/panel.md).
/// Read on the read pool from the server's database; nothing here changes anything.
/// </summary>
public partial interface IPanelBackend
{
    /// <summary>One item's holders, ledger and flags; null when the item is unknown.</summary>
    Task<PanelItemTrace?> TraceItemAsync(long id);

    /// <summary>The duplication check over every item of the realm, worst first.</summary>
    Task<PanelDupeReport> DupeCheckAsync();
}

/// <param name="Place">"Bag", "Worn", "Cart", "Mail", "Exchange", "Stall" or "Auction".</param>
/// <param name="Owner">The character's name (or "System" for an auction lot or a system stall).</param>
/// <param name="CharacterId">The character's id; 0 for the system.</param>
/// <param name="Instances">The instance guids of the stacks (0 for a configured mail grant or an auction lot).</param>
public sealed record PanelHolding(string Place, string Owner, long CharacterId, string Account, long Count, IReadOnlyList<long> Instances);

/// <param name="Entered">What the ledger says entered the economy (its source legs).</param>
/// <param name="Left">What the ledger says left it (its sink legs, a negative number).</param>
/// <param name="First">The ledger's first entry for the item; null when it has none.</param>
/// <param name="StarterKits">What the starter kits of characters that got theirs before the ledger recorded kits hold
/// of the item: explained, though the ledger never saw it.</param>
public sealed record PanelLedgerTotals(long Entered, long Left, int Entries, DateTimeOffset? First, DateTimeOffset? Last, long StarterKits = 0)
{
    public long Net => Entered + Left;

    /// <summary>What the ledger and the unrecorded starter kits explain together.</summary>
    public long Explained => Net + StarterKits;
}

/// <param name="Kind">"Exceeds ledger" (more held than the ledger let in), "Unrecorded" (held, but the ledger never
/// saw it) or "Instance seen twice" (one guid in two places).</param>
public sealed record PanelDupeFlag(string Kind, string Detail, long Instance = 0);

/// <param name="Currency">A currency (held in characters' purses, not as items): only the ledger is shown.</param>
public sealed record PanelItemTrace(PanelItemSummary Item, bool Currency, IReadOnlyList<PanelHolding> Holders, long Held,
    PanelLedgerTotals Ledger, IReadOnlyList<PanelEconomyEntry> Recent, IReadOnlyList<PanelDupeFlag> Flags);

public sealed record PanelDupeRow(PanelItemSummary Item, long Held, long Net, IReadOnlyList<PanelDupeFlag> Flags);

/// <param name="Items">Items with at least one flag.</param>
/// <param name="Checked">Different items held anywhere.</param>
/// <param name="LedgerSince">The ledger's first entry; items older than it are "Unrecorded" by nature.</param>
public sealed record PanelDupeReport(IReadOnlyList<PanelDupeRow> Items, int Checked, DateTimeOffset? LedgerSince);
