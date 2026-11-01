using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The admin's suspicious-activity list (docs/systems/anticheat.md): CheatMonitor's refusal counts per account, the
/// characters' gains from the economy ledger's source legs and anticheat_exp, and on request the duplication check. The
/// thresholds are AntiCheatOptions.Alerts: a gain is listed past GainFactor times the window's median gain and its floor
/// (inferred: the server has no notion of a normal rate, so the realm's own median stands in for it).
/// </summary>
public sealed partial class PanelBackend
{
    private const long HourMs = 3_600_000;
    private static readonly string[] SuspicionOrder = ["Duplication", "Refused requests", "Zeny gain", "Item gain", "EXP gain"];

    public async Task<PanelSuspicionReport> SuspicionAsync(int? windowHours, bool duplication)
    {
        var alerts = options.Value.AntiCheat.Alerts;
        int hours = Math.Clamp(windowHours ?? alerts.WindowHours, 1, 24 * 30);
        await cheats.FlushAsync();
        long fromMs = clock.GetUtcNow().ToUnixTimeMilliseconds() - hours * HourMs;
        var refusals = await accounts.ReadAsync<List<AntiCheatRefusalRow>>(new AntiCheatRefusalsRead(fromMs / HourMs));
        var gains = await accounts.ReadAsync<List<AntiCheatGainRow>>(new AntiCheatGainsRead(fromMs, fromMs / HourMs, CharacterService.ZenyId));
        var rows = RefusalRows(refusals, alerts.Refusals);
        long zeny = Median(gains.Select(g => g.Zeny)), items = Median(gains.Select(g => g.Items)), exp = Median(gains.Select(g => g.BaseExp));
        rows.AddRange(GainRows(gains, "Zeny gain", g => g.Zeny, Threshold(zeny, alerts.GainFactor, alerts.ZenyFloor), "Zeny"));
        rows.AddRange(GainRows(gains, "Item gain", g => g.Items, Threshold(items, alerts.GainFactor, alerts.ItemFloor), "items"));
        rows.AddRange(GainRows(gains, "EXP gain", g => g.BaseExp, Threshold(exp, alerts.GainFactor, alerts.ExpFloor), "base EXP"));
        if (duplication) rows.AddRange(DuplicationRows(await DupeCheckAsync()));
        var thresholds = new PanelSuspicionThresholds(hours, alerts.Refusals, alerts.GainFactor, alerts.ZenyFloor, alerts.ItemFloor,
            alerts.ExpFloor, zeny, items, exp);
        return new PanelSuspicionReport([.. rows.OrderBy(r => Array.IndexOf(SuspicionOrder, r.Kind)).ThenByDescending(r => r.Value)],
            thresholds, DateTimeOffset.FromUnixTimeMilliseconds(fromMs), refusals.Select(r => r.Account).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            duplication);
    }

    /// <summary>One row per account with at least <paramref name="threshold"/> refusals, about its character with the most.</summary>
    private static List<PanelSuspicionRow> RefusalRows(List<AntiCheatRefusalRow> refusals, long threshold) =>
        [.. refusals.GroupBy(r => r.Account, StringComparer.OrdinalIgnoreCase).Where(g => g.Sum(r => r.Count) >= Math.Max(1, threshold)).Select(g =>
        {
            var worst = g.Where(r => r.RoleId != 0).GroupBy(r => r.RoleId).OrderByDescending(r => r.Sum(x => x.Count)).FirstOrDefault();
            var last = g.MaxBy(r => r.LastUtc)!;
            var counts = g.GroupBy(r => r.Kind).Select(k => new PanelSuspicionCount(k.Key, k.Sum(r => r.Count))).OrderByDescending(c => c.Count).ToList();
            return new PanelSuspicionRow("Refused requests", g.Key, worst?.Key ?? 0, worst?.First().RoleName ?? "", g.Sum(r => r.Count),
                threshold, $"{last.Kind}: {last.LastDetail}", DateTimeOffset.FromUnixTimeMilliseconds(last.LastUtc), counts);
        })];

    private static IEnumerable<PanelSuspicionRow> GainRows(List<AntiCheatGainRow> gains, string kind, Func<AntiCheatGainRow, long> value,
        long threshold, string unit) =>
        gains.Where(g => value(g) >= threshold).Select(g => new PanelSuspicionRow(kind, g.Account, g.RoleId, g.RoleName, value(g), threshold,
            $"{value(g):N0} {unit} from the game in the window", null, []));

    /// <summary>Duplication flags of items: more held than the ledger let in, or one instance in two places.</summary>
    private static IEnumerable<PanelSuspicionRow> DuplicationRows(PanelDupeReport report) =>
        report.Items.SelectMany(item => item.Flags.Where(f => f.Kind != "Unrecorded").Select(f => new PanelSuspicionRow("Duplication", "", 0,
            item.Item.Name, Math.Max(0, item.Held - item.Net), 0, $"{f.Kind}: {f.Detail}", null, [], item.Item.Id)));

    /// <summary>The median of the positive values; 0 when there are none.</summary>
    internal static long Median(IEnumerable<long> values)
    {
        var sorted = values.Where(v => v > 0).Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (long)Math.Round((sorted[sorted.Length / 2 - 1] / 2.0) + (sorted[sorted.Length / 2] / 2.0));
    }

    internal static long Threshold(long median, double factor, long floor) =>
        Math.Max(Math.Max(1, floor), (long)Math.Min(long.MaxValue / 2.0, Math.Ceiling(median * Math.Max(1, factor))));
}
