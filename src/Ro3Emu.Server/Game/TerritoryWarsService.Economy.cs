using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    private EventEconomyMutation? PendingSource(IReadOnlyDictionary<long, List<Grant>> awards,
        string kind, string sourceKey)
    {
        var catalog = EventEconomy.Catalog(_data);
        var grants = awards.SelectMany(pair => pair.Value.Where(g => g.Count > 0 && g.Entry is not (100 or 101))
            .Select(grant => (Role: pair.Key, Grant: grant,
                Asset: catalog.TryAsset(grant.Entry, out var asset) ? asset
                    : throw new InvalidOperationException("Uncatalogued territory award " + grant.Entry)))).ToArray();
        if (grants.Length == 0) return null;
        var goods = grants.GroupBy(x => x.Asset).Select(g => new EconomyQuantity(g.Key,
            g.Aggregate(0L, (n, x) => checked(n + x.Grant.Count)))).ToArray();
        var deltas = grants.GroupBy(x => (x.Role, x.Asset)).Select(g => new EventEconomyOwnerDelta(
            new(EconomyOwnerKind.Role, g.Key.Role.ToString(CultureInfo.InvariantCulture)), g.Key.Asset,
            g.Aggregate(0L, (n, x) => checked(n + x.Grant.Count)))).ToArray();
        return new EventEconomyMutation(catalog, Guid.NewGuid().ToString("N"), "realm", 0,
            "event/territory-" + kind, sourceKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [], goods, deltas, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Kind = kind, Source = sourceKey,
                Grants = grants.Select(x => new { x.Role, x.Grant.Entry, x.Grant.Count, x.Grant.Bind })
            }));
    }
    private EventEconomyMutation? SiegeMailSource(IReadOnlyDictionary<long, List<MailService.Attachment>> awards,
        string sourceKey)
    {
        var catalog = EventEconomy.Catalog(_data);
        var grants = awards.SelectMany(pair => pair.Value.Where(g => g.Count > 0)
            .Select(grant => (Role: pair.Key, Grant: grant,
                Asset: catalog.TryAsset(grant.Entry, out var asset) ? asset
                    : throw new InvalidOperationException("Uncatalogued siege mail award " + grant.Entry)))).ToArray();
        if (grants.Length == 0) return null;
        var goods = grants.GroupBy(x => x.Asset).Select(g => new EconomyQuantity(g.Key,
            g.Aggregate(0L, (n, x) => checked(n + x.Grant.Count)))).ToArray();
        var deltas = grants.GroupBy(x => (x.Role, x.Asset)).Select(g => new EventEconomyOwnerDelta(
            new(EconomyOwnerKind.Role, g.Key.Role.ToString(CultureInfo.InvariantCulture)), g.Key.Asset,
            g.Aggregate(0L, (n, x) => checked(n + x.Grant.Count)))).ToArray();
        return new EventEconomyMutation(catalog, Guid.NewGuid().ToString("N"), "realm", 0,
            "event/territory-siege-settle", sourceKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [], goods, deltas, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Source = sourceKey,
                Awards = grants.Select(x => new { x.Role, x.Grant.Entry, x.Grant.Count, x.Grant.Bind })
            }));
    }
    private static Dictionary<(EconomyOwner Owner, EconomyAsset Asset, string Location), long> WarsHoldings(
        WorldState source, string prefix = "active/")
    {
        var result = new Dictionary<(EconomyOwner, EconomyAsset, string), long>();
        string epoch = source.Epoch;
        void Add(EconomyOwner owner, EconomyAsset asset, string location, long amount)
        {
            if (amount < 0) throw new InvalidOperationException("Negative territory holding.");
            if (amount == 0) return;
            var key = (owner, asset, location);
            result[key] = checked(result.GetValueOrDefault(key) + amount);
        }
        foreach (var (id, drop) in source.WorldDrops)
            Add(new(EconomyOwnerKind.Escrow, "territory-drop:" + epoch),
                new(EconomyAssetKind.Currency, 133), prefix + "drop/" + id.ToString(CultureInfo.InvariantCulture), drop.Amount);
        foreach (var (roundId, round) in source.Rounds)
        {
            if (round.Settled) continue;
            foreach (var (zoneId, zone) in round.Zones)
                foreach (var bid in zone.Entries.Where(x => !x.Occupier))
                    Add(new(EconomyOwnerKind.Guild, bid.GuildId.ToString(CultureInfo.InvariantCulture)),
                        new(EconomyAssetKind.GuildResource, bid.Legacy ? 3 : 5),
                        prefix + "bid/" + epoch + "/" + roundId + "/" + zoneId + "/" + bid.GuildId, bid.Amount);
        }
        return result;
    }

    private static Dictionary<(EconomyOwner Owner, EconomyAsset Asset, string Location), long> GuildHoldings(
        IEnumerable<GuildStore.State> values)
    {
        var result = new Dictionary<(EconomyOwner, EconomyAsset, string), long>();
        foreach (var guild in values)
        {
            var owner = new EconomyOwner(EconomyOwnerKind.Guild, guild.Id.ToString(CultureInfo.InvariantCulture));
            foreach (var (id, count) in new[] { (1L, guild.Money), (2L, guild.Contribute), (3L, guild.Materials),
                         (4L, guild.Reputation), (5L, guild.AuctionMaterials) })
            {
                if (count < 0) throw new InvalidOperationException("Negative territory guild resource.");
                if (count > 0) result[(owner, new(EconomyAssetKind.GuildResource, id), "treasury/" + id)] = count;
            }
        }
        return result;
    }

    private EventEconomyMutation? WarsEconomy(WorldState before, WorldState after,
        IReadOnlyList<GuildStore.State> oldGuilds, IReadOnlyList<GuildStore.State> newGuilds,
        string kind, string sourceKey, GameSession? actor = null,
        IReadOnlyList<(WorldState State, string Prefix)>? beforeArchives = null,
        IReadOnlyList<(WorldState State, string Prefix)>? afterArchives = null)
    {
        var old = WarsHoldings(before);
        var next = WarsHoldings(after);
        foreach (var (archive, prefix) in beforeArchives ?? [])
            foreach (var pair in WarsHoldings(archive, prefix)) old.Add(pair.Key, pair.Value);
        foreach (var (archive, prefix) in afterArchives ?? [])
            foreach (var pair in WarsHoldings(archive, prefix)) next.Add(pair.Key, pair.Value);
        foreach (var pair in GuildHoldings(oldGuilds)) old.Add(pair.Key, pair.Value);
        foreach (var pair in GuildHoldings(newGuilds)) next.Add(pair.Key, pair.Value);
        var changes = old.Keys.Concat(next.Keys).Distinct()
            .Select(key => (key, Delta: checked(next.GetValueOrDefault(key) - old.GetValueOrDefault(key))))
            .Where(x => x.Delta != 0).ToArray();
        if (changes.Length == 0) return null;
        var ownerDeltas = changes.GroupBy(x => (x.key.Owner, x.key.Asset))
            .Select(g => new EventEconomyOwnerDelta(g.Key.Owner, g.Key.Asset,
                g.Aggregate(0L, (n, x) => checked(n + x.Delta))))
            .Where(x => x.Delta != 0).ToArray();
        var totals = changes.GroupBy(x => x.key.Asset).Select(g => (Asset: g.Key,
            Delta: g.Aggregate(0L, (n, x) => checked(n + x.Delta)))).ToArray();
        return new EventEconomyMutation(EventEconomy.Catalog(_data), Guid.NewGuid().ToString("N"),
            actor?.Account ?? "realm", actor?.Role?.Id ?? 0, "event/territory-" + kind, sourceKey,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            totals.Where(x => x.Delta < 0).Select(x => new EconomyQuantity(x.Asset, -x.Delta)).ToArray(),
            totals.Where(x => x.Delta > 0).Select(x => new EconomyQuantity(x.Asset, x.Delta)).ToArray(),
            ownerDeltas, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Kind = kind, Source = sourceKey,
                Changes = changes.Select(x => new { x.key.Owner, x.key.Asset, x.key.Location, x.Delta })
            }));
    }

}
