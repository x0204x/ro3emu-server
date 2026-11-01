namespace Ro3Emu.Panel;

/// <summary>
/// The admin's suspicious-activity list: accounts whose requests the server refused as forged or impossible (anti-cheat
/// refusal counts), characters that gained far more Zeny, items or EXP than the others in the same window, and, on
/// request, the item duplication check's flags. Read only; the thresholds are the server's Ro3Emu:AntiCheat:Alerts.
/// </summary>
public partial interface IPanelBackend
{
    /// <summary>The list over the last <paramref name="windowHours"/> hours (null = the configured window), worst first;
    /// with <paramref name="duplication"/> the duplication check runs too.</summary>
    Task<PanelSuspicionReport> SuspicionAsync(int? windowHours, bool duplication);
}

/// <param name="Kind">"Refused requests", "Zeny gain", "Item gain", "EXP gain" or "Duplication".</param>
/// <param name="Account">The account, or "" (a duplication flag names an item, not an account).</param>
/// <param name="CharacterId">The character the row is about (the one with most refusals for an account row); 0 when none.</param>
/// <param name="Value">The refusals or the gain in the window.</param>
/// <param name="Threshold">What the value had to reach to be listed.</param>
/// <param name="Counts">An account row's refusals by kind (walk, attack-rate, flood ...).</param>
/// <param name="ItemId">A duplication row's item.</param>
public sealed record PanelSuspicionRow(string Kind, string Account, long CharacterId, string Character, long Value, long Threshold,
    string Detail, DateTimeOffset? Last, IReadOnlyList<PanelSuspicionCount> Counts, long ItemId = 0);

public sealed record PanelSuspicionCount(string Kind, long Count);

/// <summary>The thresholds in force (Ro3Emu:AntiCheat:Alerts) and the medians the gain thresholds came from.</summary>
public sealed record PanelSuspicionThresholds(int WindowHours, long Refusals, double GainFactor, long ZenyFloor, long ItemFloor,
    long ExpFloor, long ZenyMedian, long ItemMedian, long ExpMedian);

/// <param name="Since">The start of the window.</param>
/// <param name="Accounts">Accounts with at least one refusal in the window, listed or not.</param>
public sealed record PanelSuspicionReport(IReadOnlyList<PanelSuspicionRow> Rows, PanelSuspicionThresholds Thresholds,
    DateTimeOffset Since, int Accounts, bool Duplication);
