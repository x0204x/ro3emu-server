namespace Ro3Emu.Server.Game;

/// <summary>
/// A read-only copy of the territory map for the web panel (Http/PanelBackend.Territory.cs): owners, the war's stages,
/// the battles running now and the siege cities. Built on the game loop, where the service's state changes, so the
/// panel never reads it while a transition is half done.
/// </summary>
public sealed partial class TerritoryWarsService
{
    /// <summary>The siege cities that have an owner source (SiegeZone): the main city and its three prefectures.</summary>
    private static readonly int[] PanelSiegeCities = [1001, 1101, 1102, 1103];

    internal sealed record PanelBattle(int Zone, int Round, long Start, long End, IReadOnlyDictionary<long, long> Scores, int Players);

    /// <param name="Stage">The siege stage while the city's siege runs (0 Prepare to 3 Crystal); -1 otherwise.</param>
    internal sealed record PanelSiegeCity(int City, int Zone, long Attack, long Defense, int Stage, bool Settled, long Winner);

    internal sealed record PanelView(TerritoryWarsClock.Stage Current, IReadOnlyList<TerritoryWarsClock.Stage> Stages, long Now,
        IReadOnlyDictionary<int, long> Owners, IReadOnlyList<PanelBattle> Battles, (long Start, long End)? SiegeWindow,
        IReadOnlyList<PanelSiegeCity> Cities);

    /// <summary>On the loop: the committed map, the battles of the stage in force and the siege cities.</summary>
    internal PanelView PanelSnapshot()
    {
        if (_loop.IsWorkerActive && !_loop.IsOnLoop) throw new InvalidOperationException("The territory map is read on the game loop.");
        long now = _clock.Now;
        var current = _clock.Current;
        var battles = state.Battles.Values.Where(b => !b.Settled && b.Start <= now && now < b.End)
            .Select(b => new PanelBattle(b.Zone, b.Round, b.Start, b.End, new Dictionary<long, long>(b.Scores),
                _battles.GetValueOrDefault(BattleKey(b.Round, b.Zone))?.Room.ActiveMembers.Count ?? 0))
            .ToList();
        var window = SiegeWindow();
        var cities = PanelSiegeCities.Select(city =>
        {
            var (attack, defense) = SiegeGuilds(city);
            var run = window != null ? state.SiegeRuns.GetValueOrDefault(SiegeKey(city, window.Start)) : null;
            return new PanelSiegeCity(city, SiegeZone(city), attack, defense, run is { Settled: false } ? run.Stage : -1,
                run?.Settled ?? false, run?.Winner ?? 0);
        }).ToList();
        return new PanelView(current, [.. _clock.Stages], now, new Dictionary<int, long>(state.Owners), battles,
            window != null ? (window.Start, window.End) : null, cities);
    }
}
