using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryEscortService
{
    private string EscortReconcileSource(Ledger before, Ledger after)
    {
        string oldEpoch = TerritoryWarsClock.EpochOf(before.SeasonStart, before.PlayBook, before.Cycle);
        string newEpoch = TerritoryWarsClock.EpochOf(after.SeasonStart, after.PlayBook, after.Cycle);
        var markers = new List<string>();
        if (oldEpoch != newEpoch) markers.Add("season:" + oldEpoch + ">" + newEpoch);
        foreach (var (guild, rounds) in after.StockRounds)
            foreach (int round in rounds.Where(r => !before.StockRounds.TryGetValue(guild, out var priorRounds)
                || !priorRounds.Contains(r)))
                markers.Add($"stock:{newEpoch}:{round}:{guild}");
        foreach (var (id, listing) in before.Listings.Where(p => p.Value.State == 0))
            if (!after.Listings.TryGetValue(id, out var current) || current.State != 0)
                markers.Add($"listing-close:{oldEpoch}:{id}");
        foreach (long id in before.Carts.Keys.Except(after.Carts.Keys))
            markers.Add($"cart-close:{oldEpoch}:{id}");
        foreach (var (id, cargo) in before.Cargo.Where(p => !p.Value.Picked))
            if (!after.Cargo.TryGetValue(id, out var current) || current.Picked)
                markers.Add($"cargo-expire:{oldEpoch}:{id}");
        if (markers.Count == 0) return "escort-reconcile-state:" + newEpoch;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("|", markers.Order(StringComparer.Ordinal)));
        return "escort-reconcile:" + Convert.ToHexString(SHA256.HashData(bytes));
    }
    private static Dictionary<(EconomyOwner Owner, EconomyAsset Asset, string Location), long> EscortHoldings(Ledger source)
    {
        var result = new Dictionary<(EconomyOwner, EconomyAsset, string), long>();
        static EconomyOwner Guild(long id) => new(EconomyOwnerKind.Guild, id.ToString(CultureInfo.InvariantCulture));
        static string Epoch(long season, int playbook, long cycle) => TerritoryWarsClock.EpochOf(season, playbook, cycle);
        void Add(EconomyOwner owner, EconomyAsset asset, string location, long count)
        {
            if (count < 0) throw new InvalidOperationException("Negative escort holding.");
            if (count == 0) return;
            var key = (owner, asset, location);
            result[key] = checked(result.GetValueOrDefault(key) + count);
        }
        void AddGoods(string epoch, string prefix, Dictionary<long, long> goods, Dictionary<long, Listing> listings)
        {
            var asset = new EconomyAsset(EconomyAssetKind.EventResource, 1, epoch);
            foreach (var (guild, count) in goods)
                Add(Guild(guild), asset, prefix + "/goods/" + guild.ToString(CultureInfo.InvariantCulture), count);
            foreach (var (id, listing) in listings)
                if (listing.State == 0)
                    Add(Guild(listing.Seller), asset, prefix + "/listing/" + id.ToString(CultureInfo.InvariantCulture), listing.Count);
        }
        string current = Epoch(source.SeasonStart, source.PlayBook, source.Cycle);
        AddGoods(current, "active", source.Goods, source.Listings);
        foreach (var (epoch, archive) in source.Archives)
            AddGoods(epoch, "archive/" + epoch, archive.Goods, archive.Listings);
        foreach (var (id, cart) in source.Carts)
            Add(Guild(cart.Guild), new(EconomyAssetKind.EventResource, 1, current),
                "cart/" + id.ToString(CultureInfo.InvariantCulture), cart.Count);
        foreach (var (id, cargo) in source.Cargo)
            if (!cargo.Picked)
                Add(new(EconomyOwnerKind.Escrow, "escort-cargo:" + id.ToString(CultureInfo.InvariantCulture)),
                    new(EconomyAssetKind.Item, 1258002), "cargo/" + id.ToString(CultureInfo.InvariantCulture), 1);
        return result;
    }

    private EventEconomyMutation? EscortEconomy(Ledger before, Ledger after, string kind, string sourceKey,
        GameSession? actor = null, IReadOnlyList<EventEconomyOwnerDelta>? extraDeltas = null,
        IReadOnlyList<EconomyQuantity>? extraCosts = null, IReadOnlyList<EconomyQuantity>? extraGoods = null)
    {
        var old = EscortHoldings(before);
        var next = EscortHoldings(after);
        var changes = old.Keys.Concat(next.Keys).Distinct()
            .Select(key => (key, Delta: checked(next.GetValueOrDefault(key) - old.GetValueOrDefault(key))))
            .Where(x => x.Delta != 0).ToArray();
        if (changes.Length == 0 && (extraDeltas?.Count ?? 0) == 0) return null;
        var deltas = changes.GroupBy(x => (x.key.Owner, x.key.Asset))
            .Select(g => new EventEconomyOwnerDelta(g.Key.Owner, g.Key.Asset,
                g.Aggregate(0L, (n, x) => checked(n + x.Delta))))
            .Where(x => x.Delta != 0).Concat(extraDeltas ?? []).ToArray();
        var totals = changes.GroupBy(x => x.key.Asset).Select(g => (Asset: g.Key,
            Delta: g.Aggregate(0L, (n, x) => checked(n + x.Delta)))).ToArray();
        var costs = totals.Where(x => x.Delta < 0).Select(x => new EconomyQuantity(x.Asset, -x.Delta))
            .Concat(extraCosts ?? []).ToArray();
        var goods = totals.Where(x => x.Delta > 0).Select(x => new EconomyQuantity(x.Asset, x.Delta))
            .Concat(extraGoods ?? []).ToArray();
        return new EventEconomyMutation(EventEconomy.Catalog(_data), Guid.NewGuid().ToString("N"),
            actor?.Account ?? "realm", actor?.Role?.Id ?? 0, "event/escort-" + kind, sourceKey,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), costs, goods, deltas,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                Kind = kind, Source = sourceKey,
                Locations = changes.Select(x => new { x.key.Owner, x.key.Asset, x.key.Location, x.Delta })
            }));
    }
}
