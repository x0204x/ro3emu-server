using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Ro3Emu.Panel;

/// <summary>
/// The rest of the public game database (cards, crafting, classes and skills, pets, mounts, shops, the element table, a
/// map's world, a monster's EXP, the event calendar and the MVP timers). Every number comes from the server
/// (<see cref="IPanelBackend"/>).
/// </summary>
public sealed partial class PanelSite
{
    private IEnumerable<Route> WikiRoutes() =>
    [
        Get("/api/cards", Access.Public, async _ => Reply.Ok(await _backend.CardsAsync())),
        Get("/api/cards/{}", Access.Public, call => ById(call, _backend.CardAsync, "There is no such card.")),
        Get("/api/crafting", Access.Public, async _ => Reply.Ok(await _backend.RecipesAsync())),
        Get("/api/items/{}/crafting", Access.Public, async call => Id(call) is { } id
            ? Reply.Ok(await _backend.ItemCraftingAsync(id)) : Reply.Error(StatusCodes.Status404NotFound, "There is no such item.")),
        Get("/api/classes", Access.Public, async _ => Reply.Ok(new { classes = await _backend.ClassesAsync() })),
        Get("/api/classes/{}", Access.Public, call => ById(call, _backend.ClassAsync, "There is no such class.")),
        Get("/api/skills/{}", Access.Public, call => ById(call, _backend.SkillAsync, "There is no such skill.")),
        Get("/api/pets", Access.Public, async _ => Reply.Ok(new { pets = await _backend.PetsAsync() })),
        Get("/api/pets/{}", Access.Public, call => ById(call, _backend.PetAsync, "There is no such pet.")),
        Get("/api/mounts", Access.Public, async _ => Reply.Ok(new { mounts = await _backend.MountsAsync() })),
        Get("/api/shops", Access.Public, async _ => Reply.Ok(new { shops = await _backend.ShopsAsync() })),
        Get("/api/shops/{}", Access.Public, call => ById(call, _backend.ShopAsync, "There is no such shop.")),
        Get("/api/elements", Access.Public, async _ => Reply.Ok(await _backend.CombatRulesAsync())),
        Get("/api/monsters/{}/combat", Access.Public, call => ById(call, _backend.MonsterCombatAsync, "There is no such monster.")),
        Get("/api/maps/{}/world", Access.Public, call => ById(call, _backend.MapWorldAsync, "There is no such map.")),
        Get("/api/events", Access.Public, async _ => Reply.Ok(await _backend.CalendarAsync())),
        Get("/api/mvps", Access.Public, async _ => Reply.Ok(new { mvps = await _backend.MvpTimersAsync() })),
    ];

    private static long? Id(Call call) =>
        long.TryParse(call.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : null;

    /// <summary>The thing the route's id names, or 404 with <paramref name="missing"/>.</summary>
    private static async Task<Reply> ById<T>(Call call, Func<long, Task<T?>> find, string missing) where T : class =>
        Id(call) is { } id && await find(id) is { } found ? Reply.Ok(found) : Reply.Error(StatusCodes.Status404NotFound, missing);
}
