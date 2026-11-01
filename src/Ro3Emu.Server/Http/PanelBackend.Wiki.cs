using Ro3Emu.Panel;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The rest of the public database (IPanelBackend.Wiki.cs): each part built once from the game data on first use, off
/// the request thread, and kept in memory; a failed build is tried again by the next request. The calendar and the MVP
/// timers are not cached here: the server's clocks answer them (PanelBackend.Live.cs).
/// </summary>
public sealed partial class PanelBackend
{
    private readonly object _wikiGate = new();
    private readonly Dictionary<string, Task<object>> _wiki = [];

    /// <summary>The part <paramref name="key"/>, built once by <paramref name="build"/>.</summary>
    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> build) where T : class
    {
        Task<object> task;
        lock (_wikiGate)
        {
            if (!_wiki.TryGetValue(key, out task!) || task.IsFaulted || task.IsCanceled)
                _wiki[key] = task = Task.Run(async () => (object)await build());
        }
        return (T)await task;
    }

    /// <summary>An item and a count, named and pictured as the item database shows it.</summary>
    private PanelAmount Amount(long item, long count) => ItemConfig.Get(data, item) is { } row
        ? new PanelAmount(item, Name(row.Name) is { Length: > 0 } name ? name : "Item " + item, (int)row.Quality, ItemIcon(row), count)
        : new PanelAmount(item, "Item " + item, 1, "", count);

    /// <summary>[[item, count], ...] as amounts; rows without an item are left out.</summary>
    private List<PanelAmount> Amounts(IEnumerable<long[]> pairs) =>
        [.. pairs.Where(p => p.Length >= 2 && p[0] > 0).Select(p => Amount(p[0], p[1]))];

    private string QualityLabel(long quality) => QualityName(quality) is { Length: > 0 } name ? name : "Quality " + quality;
}
