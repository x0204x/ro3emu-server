using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Ro3Emu.Panel;

/// <summary>
/// The public item, monster and map databases and the admin's economy log. Every number comes from the server
/// (<see cref="IPanelBackend"/>); the query string is clamped here before it reaches it.
/// </summary>
public sealed partial class PanelSite
{
    private const int DatabasePageSize = 50, QueryMax = 64;

    private IEnumerable<Route> DatabaseRoutes() =>
    [
        Get("/api/items", Access.Public, FindItemsAsync),
        Get("/api/items/{}", Access.Public, ItemAsync),
        Get("/api/monsters", Access.Public, FindMonstersAsync),
        Get("/api/monsters/{}", Access.Public, MonsterAsync),
        Get("/api/maps", Access.Public, async _ => Reply.Ok(new { maps = await _backend.MapsAsync() })),
        Get("/api/maps/{}", Access.Public, MapAsync),
        Get("/api/admin/economy", Access.Admin, EconomyAsync),
    ];

    private static int Number(Call call, string key, int max) =>
        int.TryParse(call.Query(key), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 0, max) : 0;

    private static string Text(Call call, string key)
    {
        string text = call.Query(key).Trim();
        return text.Length > QueryMax ? text[..QueryMax] : text;
    }

    private async Task<Reply> FindItemsAsync(Call call) =>
        Reply.Ok(await _backend.FindItemsAsync(new PanelItemQuery(Text(call, "q"), Number(call, "type", 1000), Number(call, "quality", 10),
            Number(call, "min", 10_000), Number(call, "max", 10_000), call.Page(), DatabasePageSize)));

    private async Task<Reply> ItemAsync(Call call) =>
        long.TryParse(call.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id) && await _backend.ItemAsync(id) is { } item
            ? Reply.Ok(item) : Reply.Error(StatusCodes.Status404NotFound, "There is no such item.");

    private async Task<Reply> FindMonstersAsync(Call call) =>
        Reply.Ok(await _backend.FindMonstersAsync(new PanelMonsterQuery(Text(call, "q"), Number(call, "element", 100), Number(call, "race", 100),
            Number(call, "size", 100), Text(call, "rank"), Number(call, "min", 10_000), Number(call, "max", 10_000), call.Page(), DatabasePageSize)));

    private async Task<Reply> MonsterAsync(Call call) =>
        long.TryParse(call.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id) && await _backend.MonsterAsync(id) is { } monster
            ? Reply.Ok(monster) : Reply.Error(StatusCodes.Status404NotFound, "There is no such monster.");

    private async Task<Reply> MapAsync(Call call) =>
        long.TryParse(call.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id) && await _backend.MapAsync(id) is { } map
            ? Reply.Ok(map) : Reply.Error(StatusCodes.Status404NotFound, "There is no such map.");

    /// <summary>The ledger: account, character, item (an id or a name), and a range of days (UTC, both ends included).</summary>
    private async Task<Reply> EconomyAsync(Call call)
    {
        string asset = Text(call, "asset");
        long assetId = long.TryParse(asset, NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : 0;
        DateTimeOffset? Day(string key) => DateTime.TryParseExact(call.Query(key), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var day) ? new DateTimeOffset(day, TimeSpan.Zero) : null;
        var to = Day("to");
        return Reply.Ok(await _backend.EconomyAsync(new PanelEconomyQuery(Text(call, "account"), Text(call, "character"), assetId,
            assetId == 0 ? asset : "", Day("from"), to?.AddDays(1), call.Page(), PageSize)));
    }
}
