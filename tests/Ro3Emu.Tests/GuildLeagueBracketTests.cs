using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The Guild League bracket as the client derives it (LC_GuildLeagueHelper.lua:727-875) and the tier lifts.</summary>
public sealed class GuildLeagueBracketTests
{
    [Fact]
    public void BracketDerivesTheClientsSemifinalsFinalsAndPlaces()
    {
        GuildLeagueBracket.Pair[] prelim = [new(1, 2), new(3, 4), new(5, 6), new(7, 8)];
        var scores = Enumerable.Range(1, 8).ToDictionary(i => (long)i, _ => 0);
        void Win(GuildLeagueBracket.Pair[] pairs, int round)
        {
            // Blue wins every pair of the round.
            foreach (var p in pairs) scores[p.Blue] += GuildLeagueBracket.Weight(round);
        }
        Win(GuildLeagueBracket.Pairs(prelim, scores, 1), 1);
        var semi = GuildLeagueBracket.Pairs(prelim, scores, 2);
        // OnHandleFightTwoSchedule: winners of pairs 1/3, losers of 1/3, winners of 2/4, losers of 2/4.
        Assert.Equal([new(1, 5), new(2, 6), new(3, 7), new(4, 8)], semi);
        Win(semi, 2);
        var final = GuildLeagueBracket.Pairs(prelim, scores, 3);
        // OnHandleFightThreeSchedule: places 1-2, 5-6, 3-4, 7-8.
        Assert.Equal([new(1, 3), new(2, 4), new(5, 7), new(6, 8)], final);
        Win(final, 3);
        var places = scores.ToDictionary(p => p.Key, p => GuildLeagueBracket.Place(p.Value));
        Assert.Equal([1, 5, 2, 6, 3, 7, 4, 8], Enumerable.Range(1, 8).Select(i => places[i]));
        // The derivation is stable once later rounds are scored (the client recomputes it from final scores).
        Assert.Equal(semi, GuildLeagueBracket.Pairs(prelim, scores, 2));

        // A short group: the draw spreads the byes, an empty blue swaps with its guild (LC_GuildLeagueHelper.lua:756-768).
        var draw = GuildLeagueBracket.Draw([11, 12], new Random(1));
        Assert.Equal(2, draw.Count(p => p.Blue > 0 && p.Red == 0));
        Assert.Equal((12L, 0L), GuildLeagueBracket.Decide(new(0, 12), new Dictionary<long, int>()));

        // Promotion/relegation by bracket place (GuildLeagueRankConfig _iGroupUpNum / _iGroupDownNum).
        Assert.Equal(GuildLeagueRankLift.Up, GuildLeagueBracket.Lift(1, 1, 2, true, true));
        Assert.Equal(GuildLeagueRankLift.None, GuildLeagueBracket.Lift(2, 1, 2, true, true));
        Assert.Equal(GuildLeagueRankLift.Down, GuildLeagueBracket.Lift(7, 1, 2, true, true));
        Assert.Equal(GuildLeagueRankLift.None, GuildLeagueBracket.Lift(1, 0, 2, false, true)); // Ragnarok has no higher tier
        Assert.Equal(GuildLeagueRankLift.None, GuildLeagueBracket.Lift(8, 1, 0, true, false)); // nor Black Iron a lower one
    }
}
