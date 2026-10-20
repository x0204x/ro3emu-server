using System.Text.Json;
using System.Globalization;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class StallsService
{
    /// <summary>
    /// The extracted system-item conditions are empty or [1,1,day]. Interpreting the latter as server day is
    /// (inferred); use SeasonService.Start, never the obsolete multiverse schedule. Unknown forms fail closed.
    /// </summary>
    private bool SystemItemAvailable(JsonElement row)
    {
        if (!InSeason(row)) return false;
        if (!row.TryGetProperty("_kCond", out var cond) || cond.ValueKind != JsonValueKind.Array) return true;
        int day = seasons.OpenContent.ServerDayFree ? int.MaxValue : (clock.GetLocalNow().Date - seasons.Start.Date).Days + 1;
        return cond.EnumerateArray().All(c => Numbers(c) is { Length: 3 } p && p[0] == 1 && p[1] == 1 && day >= p[2]);
    }

    /// <summary>
    /// Trigger positions and sale row IDs come from StallSystemTrigger. Initial population on scene use,
    /// minimum stock in _iTakeFirst/_iTakeCycle, lifetime and restock cadence are (inferred) scheduling choices;
    /// the hidden dynamic trigger/target selection algorithm is not reconstructed.
    /// </summary>
    private void PopulateSystems(MarketTransaction tx, IReadOnlySet<long> scenes, List<StallRow> changed)
    {
        if (scenes.Count == 0) return;
        foreach (var trigger in data.Table("StallSystemTrigger").Values)
        {
            if (!trigger.TryGetProperty("_kCond", out var cond) || cond.ValueKind != JsonValueKind.Array) continue;
            var location = cond.EnumerateArray().Select(Numbers).FirstOrDefault(p => p.Length == 5 && p[0] == 3 && p[1] == 2 && scenes.Contains(p[2]));
            var goods = Field(trigger, "_kSaleGoods");
            if (location == null || goods.Length == 0) continue;
            long id = GameData.Int(trigger, "_iID");
            var row = tx.Get<StallRow>(Kind, SystemKey(id));
            bool fresh = row == null || row.Timeout <= Now;
            if (fresh)
            {
                // Replacing an expired system stall retires its still-persisted stock exactly once.
                if (row != null)
                    foreach (var old in row.Stock.Where(stock => stock.Item.Stack > 0))
                        tx.Economy.Sink(new(EconomyAssetKind.Item, old.Item.Entry), old.Item.Stack, EconomyReason.SystemRetire,
                            EconomyCompartment.Stall, new(EconomyOwnerKind.System, id.ToString(CultureInfo.InvariantCulture)));
                row = new StallRow
                {
                    Key = SystemKey(id), SystemId = id, Scene = location[2], Title = data.ClientText(Param("StallsSystemDefaultName", 181000)),
                    OwnerName = data.ClientText(Param("StallsSystemDefaultRoleName", 182000)), Career = (int)Field(trigger, "_kJob").FirstOrDefault(1000),
                    Level = (int)Field(trigger, "_kLevel").FirstOrDefault(1), Sex = 1,
                    X = location[3], Z = location[4], OpenTime = Now, Timeout = Now + Param("StallsSystemTime", 259200),
                    MaxGrid = goods.Length, RestockTime = Now + Param("StallsSystemRestockCD", 600),
                };
            }
            bool restock = fresh || row!.RestockTime <= Now;
            bool touched = fresh;
            foreach (long entry in goods)
            {
                if (data.Row("StallSystemItem", entry) is not { } config || !SystemItemAvailable(config)) continue;
                var stock = row!.Stock.FirstOrDefault(i => i.SystemConfig == entry);
                var price = Field(config, "_kPrice");
                var first = Field(config, "_iTakeFirst");
                if (price.Length != 2 || first.Length < 2 || first[0] <= 0 || price[1] <= 0 || price[1] > int.MaxValue) continue;
                if (stock == null)
                {
                    int itemId = (int)GameData.Int(config, "_iItemID");
                    if (data.Row("ItemConfig", itemId) is not { } item) continue;
                    var bind = Field(config, "_kIsBindGive");
                    // These 54 catalogue rows are consumables/materials. Real per-instance metadata is persisted.
                    stock = new StallItem
                    {
                        Id = entry, SystemConfig = (int)entry, Price = (int)price[1], Currency = (int)price[0],
                        Item = new InvItem
                        {
                            Entry = itemId, Pack = CharacterService.ConfigPack(item), Stack = (int)first[0],
                            BindType = bind.Length > 0 ? (int)bind[0] : 0, BindExpire = bind.Length > 1 ? Now + bind[1] : 0
                        },
                    };
                    row.Stock.Add(stock); touched = true;
                    tx.Economy.Source(new(EconomyAssetKind.Item, itemId), stock.Item.Stack, EconomyReason.SystemRestock,
                        EconomyCompartment.Stall, new(EconomyOwnerKind.System, id.ToString(CultureInfo.InvariantCulture)));
                }
                else if (restock)
                {
                    var cycle = Field(config, "_iTakeCycle");
                    int amount = cycle.Length > 0 ? (int)cycle[0] : 0;
                    int before = stock.Item.Stack;
                    stock.Item.Stack = (int)Math.Min(first[1], (long)stock.Item.Stack + amount);
                    int delta = stock.Item.Stack - before;
                    if (delta > 0) tx.Economy.Source(new(EconomyAssetKind.Item, stock.Item.Entry), delta, EconomyReason.SystemRestock,
                        EconomyCompartment.Stall, new(EconomyOwnerKind.System, id.ToString(CultureInfo.InvariantCulture)));
                    else if (delta < 0) tx.Economy.Sink(new(EconomyAssetKind.Item, stock.Item.Entry), -delta, EconomyReason.SystemRetire,
                        EconomyCompartment.Stall, new(EconomyOwnerKind.System, id.ToString(CultureInfo.InvariantCulture)));
                    touched = true;
                }
            }
            if (!touched) continue;
            row!.RestockTime = Now + Param("StallsSystemRestockCD", 600);
            Save(tx, row); changed.Add(row);
        }
    }

    /// <summary>Run the maintenance now, including this session's scene (stall commands act on fresh state).</summary>
    public Task RefreshWorldAsync(GameSession s)
    {
        RequestScene(s);
        return RefreshWorldMaintenanceAsync();
    }

    /// <summary>Ask the realm maintenance to keep this session's scene populated (login and scene load, C/D 15). Visited
    /// scenes stay in the set: the maintenance also replaces their expired system stalls and restocks them.</summary>
    internal bool RequestScene(GameSession s) => s.InstanceId == 0 && s.SceneEntry > 0 && maintenanceScenes.Add(s.SceneEntry);

    private void PopulateFirstVisit(GameSession s)
    {
        if (RequestScene(s)) loop.TrackRuntimeDetached("stall first visit", RefreshWorldMaintenanceAsync);
    }

    /// <summary>Use captured scene values only; timed settlement belongs to its own owner plan.</summary>
    internal Task RefreshWorldMaintenanceAsync() => RefreshWorldCoreAsync(new HashSet<long>(maintenanceScenes));

    private async Task RefreshWorldCoreAsync(IReadOnlySet<long> sceneEntries)
    {
        var changed = new List<StallRow>();
        var restore = new List<StallRow>();
        var pins = new List<StallRow>();
        int code = await market.RunMaintenanceAsync(tx =>
        {
            changed.Clear();
            restore.Clear();
            pins.Clear();
            foreach (var row in tx.All<StallRow>(Kind).Where(r => r.Owner > 0 && !r.Ended))
            {
                bool timeout = row.Timeout <= Now;
                // Localization 37825 explicitly protects the first six minutes. Choosing that over the
                // unexplained StallsSafeFirstTime=3 is (inferred); its original unit remains unresolved.
                if (timeout || (row.Stock.Count == 0 && Now - row.OpenTime >= 360))
                {
                    Close(tx, row, (int)Param(timeout ? "StallsCloseTimeMailId" : "StallsCloseNullMailId", timeout ? 1031 : 1032));
                    changed.Add(row);
                }
                else
                {
                    // Restore the numbered line before exposing the durable offline stall after restart.
                    pins.Add(row);
                    var meta = Metadata(tx, row.Owner);
                    if (row.TaxRate != meta.CurTax.Key)
                    {
                        row.TaxRate = meta.CurTax.Key;
                        Save(tx, row, meta); changed.Add(row);
                    }
                    else if (activeLines.Contains(Ro3Emu.Server.Maps.RuntimeMapKey.Public(row.Scene, row.LineNumber))
                        && !actors.ContainsKey((row.Key, row.LineNumber))) restore.Add(row);
                }
            }
            PopulateSystems(tx, sceneEntries, changed);
            foreach (var row in tx.All<StallRow>(Kind).Where(r => r.SystemId > 0 && r.Timeout > Now))
                if (activeLines.Any(line => line.SceneEntry == row.Scene && !actors.ContainsKey((row.Key, line.LineNumber)))
                    && changed.All(r => r.Key != row.Key)) restore.Add(row);
            return 0;
        });
        if (code != 0) return;
        foreach (var row in pins)
        {
            if (!lines.RestorePinnedLine(row.Scene, row.LineNumber))
                throw new InvalidOperationException($"Cannot restore active player stall {row.Key} on line {row.LineNumber} of {row.Scene}.");
            ActivateLine(Ro3Emu.Server.Maps.RuntimeMapKey.Public(row.Scene, row.LineNumber));
            Pin(row);
            if (!actors.ContainsKey((row.Key, row.LineNumber)) && restore.All(x => x.Key != row.Key)) restore.Add(row);
        }
        foreach (var row in restore) await PublishAsync(row, notify: false);
        foreach (var row in changed) await PublishAsync(row);
        foreach (var actor in actors.Values.Where(a => a.Row.SystemId > 0 && a.Row.Timeout <= Now).ToList())
        {
            actors.Remove((actor.Row.Key, actor.RuntimeMap.LineNumber));
            await world.RemoveUnitAsync(actor);
        }
    }

    private async Task MaintainAsync()
    {
        try
        {
            await RefreshWorldMaintenanceAsync();
            await trade.TickMaintenanceAsync();
        }
        finally { loop.ScheduleRuntime(TimeSpan.FromSeconds(1), MaintainAsync); }
    }
}
