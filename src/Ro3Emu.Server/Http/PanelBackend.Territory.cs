using System.Globalization;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The territory and siege map: TerritoryWarsService's committed state copied on the game loop at most once per
/// <see cref="OnlineTtl"/> (TerritoryWarsService.PanelSnapshot), named from GVGWarZoneConfig, SiegeConfig and the guilds.
/// </summary>
public sealed partial class PanelBackend
{
    private Task<PanelTerritoryMap>? _territory;
    private DateTimeOffset _territoryAt;

    /// <summary>GVGWarZoneConfig _iType: 1 the capital, 2 a region, 3 a stronghold, 4 a field.</summary>
    private static readonly string[] ZoneKinds = ["", "Capital", "Region", "Stronghold", "Field"];

    /// <summary>The siege's stages (TerritoryWarsService.SiegeStages.cs, UI_Sys_Siege_TopBarWidgetCtrl.lua).</summary>
    private static readonly string[] SiegeStages = ["Prepare", "Gate", "Inner city", "Crystal"];

    public async Task<PanelTerritoryMap> TerritoryAsync()
    {
        Task<PanelTerritoryMap> task;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_territory == null || _territory.IsFaulted || _territory.IsCanceled || _territory.IsCompleted && now - _territoryAt >= OnlineTtl)
            {
                _territory = TerritoryMapAsync();
                _territoryAt = now;
            }
            task = _territory;
        }
        return await task;
    }

    private async Task<PanelTerritoryMap> TerritoryMapAsync()
    {
        TerritoryWarsService.PanelView view;
        try { view = await loop.InvokeAsync(() => Task.FromResult(territory.PanelSnapshot())); }
        catch (OperationCanceledException) { view = EmptyTerritory(); } // the loop no longer admits work: the server is stopping
        var guilds = (await accounts.ReadAsync<List<GuildStore.State>>(new GuildReadPlan(GuildReadKind.List))).ToDictionary(g => g.Id);
        PanelGuildRef? Guild(long id) => id > 0 && guilds.TryGetValue(id, out var g) ? GuildRef(g) : null;
        var zones = GVGWarZoneConfig.All(data).OrderBy(z => z.Type).ThenBy(z => z.Id).Select(z =>
        {
            var battle = view.Battles.FirstOrDefault(b => b.Zone == z.Id);
            return new PanelZone((int)z.Id, (int)z.BelongId, ZoneKind(z.Type), ZoneName(z), z.MapId, MapImage(z.MapId), Guild(view.Owners.GetValueOrDefault((int)z.Id)),
                battle == null ? null : new PanelZoneBattle(Seconds(battle.Start), Seconds(battle.End),
                    [.. battle.Scores.OrderByDescending(s => s.Value).Select(s => Guild(s.Key) is { } g ? new PanelGuildScore(g, s.Value) : null).OfType<PanelGuildScore>()],
                    battle.Players));
        }).ToList();
        var window = view.SiegeWindow is { } w ? new PanelSiegeWindow(Seconds(w.Start), Seconds(w.End), w.Start <= view.Now && view.Now < w.End) : null;
        var castles = view.Cities.Where(c => SiegeConfig.Get(data, c.City) != null).Select(c => Castle(c, window, Guild)).ToList();
        int at = view.Stages.ToList().FindIndex(s => s.Id == view.Current.Id);
        var next = at >= 0 ? view.Stages.Skip(at + 1).FirstOrDefault(s => s.Type != view.Current.Type) : view.Stages.FirstOrDefault(s => s.StartTime > view.Now);
        return new PanelTerritoryMap(Seconds(view.Now), Stage(view.Current), next == null ? null : Stage(next),
            [.. view.Stages.Where(s => s.Type is 6 or 10 or 14).Select(Stage)], zones, castles, window);
    }

    private static TerritoryWarsService.PanelView EmptyTerritory() =>
        new(new TerritoryWarsClock.Stage(0, 0, 0, 0, 0, 0), [], 0, new Dictionary<int, long>(), [], null, []);

    private PanelCastle Castle(TerritoryWarsService.PanelSiegeCity c, PanelSiegeWindow? window, Func<long, PanelGuildRef?> guild)
    {
        var row = SiegeConfig.Get(data, c.City);
        string name = row is { } r && data.TextOf(r.CityName) is { Length: > 0 } text ? text : "City " + c.City.ToString(CultureInfo.InvariantCulture);
        string kind = row?.CityType == 1 ? "Main city" : "Prefecture";
        // SiegeBase: Fighting while the window is open, End once the city's run settled, else not open.
        string state = c.Settled ? "Over" : window?.Running == true ? "Fighting" : "Not open";
        string stage = c.Stage >= 0 && c.Stage < SiegeStages.Length ? SiegeStages[c.Stage] : "";
        return new PanelCastle(c.City, name, kind, c.Zone, guild(c.Defense), guild(c.Attack), state, stage, guild(c.Winner));
    }

    private static string ZoneKind(long type) => type > 0 && type < ZoneKinds.Length ? ZoneKinds[type] : "Zone";

    private string ZoneName(GVGWarZoneConfig zone) =>
        data.TextOf(zone.Name) is { Length: > 0 } name ? name : ZoneKind(zone.Type) + " " + zone.Id.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset Seconds(long epoch) => DateTimeOffset.FromUnixTimeSeconds(epoch);

    private static PanelWarStage Stage(TerritoryWarsClock.Stage s)
    {
        var (target, kind) = StageName(s.Type);
        return new PanelWarStage(s.Id, s.Round, target.Length > 0 ? target + " " + kind.ToLowerInvariant() : kind, kind, Seconds(s.StartTime), Seconds(s.EndTime));
    }

    /// <summary>
    /// MG_TerritoryWars TerritoryWarStageType in words. Point stages fight over the strongholds (round 1), Capital stages
    /// over the regions (round 2) and Siege stages over the capital (round 3), as TerritoryWarsService.BattleZones pairs
    /// them; the words are the panel's own (inferred).
    /// </summary>
    private static (string Target, string Kind) StageName(int type) => type switch
    {
        1 => ("", "Escort preparation"),
        2 => ("", "Escort"),
        3 or 7 or 11 => (StageTarget(type), "Auction preparation"),
        4 or 8 or 12 => (StageTarget(type), "Auction"),
        16 or 17 or 18 => (StageTarget(type), "Final auction"),
        5 or 9 or 13 => (StageTarget(type), "Battle preparation"),
        6 or 10 or 14 => (StageTarget(type), "Battle"),
        15 => ("", "Season over"),
        99 => ("", "Off season"),
        _ => ("", "Not open"),
    };

    private static string StageTarget(int type) => type is >= 3 and <= 6 or 16 ? "Stronghold" : type is >= 7 and <= 10 or 17 ? "Region" : "Capital";
}
