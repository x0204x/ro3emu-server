namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// Every table of the realm database, created or migrated in this order when AccountStore opens it. Stores and
/// services never create their own tables, so constructing one writes nothing. Each plan is idempotent; the role plan
/// goes first because later migrations read role_lifecycle.
/// </summary>
internal static class DatabaseSchema
{
    internal static DbPlan[] Plans() =>
    [
        new RoleSchemaPlan(),
        new ItemSchemaPlan(),
        new FriendSchemaPlan(),
        new GuildSchemaPlan(),
        new GuildChallengeSchemaPlan(),
        new AdventureGroupSchemaPlan(),
        new AuctionSchemaPlan(),
        new DungeonObligationSchemaPlan(),
        new MarketSchemaPlan(),
        new SeasonStateSchemaPlan(),
        new MvpSchemaPlan(),
        new FestivalSchemaPlan(),
        new CampfireSchemaPlan(),
        new GuildHuntingSchemaPlan(),
        new CaravanSchemaPlan(),
        new AdventureSchemaPlan(),
        new PronteraDefenseSchemaPlan(),
        new TerritorySchemaPlan(RealmStateKind.TerritoryWars),
        new TerritorySchemaPlan(RealmStateKind.TerritoryEscort),
        new TerritorySchemaPlan(RealmStateKind.GuildLeague),
        new TimedBattleSchemaPlan(),
        new CasualBattleSchemaPlan(),
    ];
}
