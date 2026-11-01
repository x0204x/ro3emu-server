using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Ro3Emu.Panel;

/// <summary>
/// The player-facing pages over the realm's own state: public character profiles and guild pages, the territory and
/// siege map, the market; and the admin's item tracking. All read only; every number comes from
/// <see cref="IPanelBackend"/>.
/// </summary>
public sealed partial class PanelSite
{
    private const int MarketPageSize = 50;

    private IEnumerable<Route> PlayerPageRoutes() =>
    [
        Get("/api/profiles/{}", Access.Public, ProfileAsync),
        Get("/api/guilds", Access.Public, async _ => Reply.Ok(new { guilds = await _backend.GuildsAsync() })),
        Get("/api/guilds/{}", Access.Public, GuildAsync),
        Get("/api/territory", Access.Public, async _ => Reply.Ok(await _backend.TerritoryAsync())),
        Get("/api/market", Access.Public, async call => Reply.Ok(await _backend.MarketAsync(Text(call, "q"), call.Page(), MarketPageSize))),
        Get("/api/market/{}", Access.Public, MarketItemAsync),
        Get("/api/admin/items/dupes", Access.Admin, async _ => Reply.Ok(await _backend.DupeCheckAsync())),
        Get("/api/admin/items/{}/trace", Access.Admin, TraceItemAsync),
    ];

    private static bool TryId(Call call, out long id) => long.TryParse(call.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out id);

    /// <summary>A profile by the character's name, as the page's address carries it.</summary>
    private async Task<Reply> ProfileAsync(Call call)
    {
        string name = call.Args[0].Trim();
        return name.Length is > 0 and <= NameMax && await _backend.ProfileAsync(name) is { } profile
            ? Reply.Ok(profile) : Reply.Error(StatusCodes.Status404NotFound, "There is no such character.");
    }

    private async Task<Reply> GuildAsync(Call call) =>
        TryId(call, out long id) && await _backend.GuildAsync(id) is { } guild
            ? Reply.Ok(guild) : Reply.Error(StatusCodes.Status404NotFound, "There is no such guild.");

    private async Task<Reply> MarketItemAsync(Call call) =>
        TryId(call, out long id) && await _backend.MarketItemAsync(id) is { } item
            ? Reply.Ok(item) : Reply.Error(StatusCodes.Status404NotFound, "There is no such item.");

    private async Task<Reply> TraceItemAsync(Call call) =>
        TryId(call, out long id) && await _backend.TraceItemAsync(id) is { } trace
            ? Reply.Ok(trace) : Reply.Error(StatusCodes.Status404NotFound, "There is no such item.");
}
