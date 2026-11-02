namespace Ro3Emu.Server.Game;

/// <summary>
/// The Guild League group bracket as the client renders it (LC_GuildLeagueHelper.lua:727-875). A group is eight slots in
/// four preliminary pairs; 30412 always carries those four pairs with cumulative scores and the client derives the later
/// rounds itself, so the server derives them with the same steps: g_kScheduleSort {1,3,2,4}, OnHandleFightTwoSchedule
/// (semifinals: winners of pairs 1/3, losers of 1/3, winners of 2/4, losers of 2/4) and OnHandleFightThreeSchedule (finals
/// for places 1-2, 5-6, 3-4, 7-8). Scores add enum_GuildLeagueFightScore 4/2/1 for a preliminary/semifinal/final win
/// (GetResultByScore reads them in that order), so a guild's place in its group is 8 - score. Guild id 0 is an empty slot.
/// </summary>
public static class GuildLeagueBracket
{
    public readonly record struct Pair(long Blue, long Red);

    /// <summary>enum_GuildLeagueFightScore One/Two/Three (LC_GuildLeagueHelper.lua:100-104).</summary>
    public static int Weight(int round) => round switch { 1 => 4, 2 => 2, 3 => 1, _ => 0 };

    private static readonly int[] ScheduleSort = [1, 3, 2, 4]; // g_kScheduleSort, LC_GuildLeagueHelper.lua:728-737

    /// <summary>
    /// The winner and loser of one displayed pair (LC_GuildLeagueHelper.lua:756-777): blue unless blue is empty and red is
    /// not, or red has a score above zero and above blue's.
    /// </summary>
    public static (long Winner, long Loser) Decide(Pair pair, IReadOnlyDictionary<long, int> scores)
    {
        long winner = pair.Blue, loser = pair.Red;
        if (pair.Blue == 0 && pair.Red > 0) (winner, loser) = (pair.Red, pair.Blue);
        int loserScore = scores.GetValueOrDefault(loser), winnerScore = scores.GetValueOrDefault(winner);
        if (loserScore > 0 && winnerScore < loserScore) (winner, loser) = (loser, winner);
        return (winner, loser);
    }

    /// <summary>The four pairs of <paramref name="round"/> (1-3) in client order, from the preliminary pairs and scores.</summary>
    public static Pair[] Pairs(IReadOnlyList<Pair> preliminary, IReadOnlyDictionary<long, int> scores, int round)
    {
        if (preliminary.Count != 4) throw new ArgumentException("A group has four preliminary pairs.");
        if (round <= 1) return [.. preliminary];
        var semifinal = Next(preliminary, scores, final: false);
        return round == 2 ? semifinal : Next(semifinal, scores, final: true);
    }

    private static Pair[] Next(IReadOnlyList<Pair> source, IReadOnlyDictionary<long, int> scores, bool final)
    {
        var slots = new long[9];
        for (int i = 1; i <= 4; i++)
        {
            int sort = ScheduleSort[i - 1];
            var (winner, loser) = Decide(source[sort - 1], scores);
            if (!final) { slots[i] = winner; slots[i + 4] = loser; }
            else if (i % 2 > 0) { slots[2 * i - 1] = winner; slots[2 * i + 1] = loser; }
            else { slots[2 * (i - 1)] = winner; slots[2 * i] = loser; }
        }
        var result = new Pair[4];
        for (int i = 1; i <= 4; i++)
        {
            int sort = ScheduleSort[i - 1];
            result[i - 1] = new Pair(slots[2 * sort - 1], slots[2 * sort]);
        }
        return result;
    }

    /// <summary>
    /// Promotion/relegation of a place: GuildLeagueRankConfig _iGroupUpNum top places move up and _iGroupDownNum bottom places
    /// of the eight move down (columns proven; reading them as bracket places is inferred, so the empty slots of a short
    /// group take the bottom places and a two-guild Ragnarok group keeps both guilds).
    /// </summary>
    public static Romsg.GuildLeagueRankLift Lift(int place, int up, int down, bool hasUpper, bool hasLower) =>
        place >= 1 && place <= up && hasUpper ? Romsg.GuildLeagueRankLift.Up
        : place > 8 - down && hasLower ? Romsg.GuildLeagueRankLift.Down : Romsg.GuildLeagueRankLift.None;

    /// <summary>A guild's place 1-8 in its group once all three rounds are scored.</summary>
    public static int Place(int score) => 8 - Math.Clamp(score, 0, 7);

    /// <summary>
    /// matcharea of a preliminary pair (0 top, 1 bottom): the client's SortAlternatingArea interleaves area 0 and 1 for
    /// FightOne, and the semifinals pair 1 with 3 and 2 with 4, so pairs 1/3 are one half of the bracket (inferred meaning).
    /// </summary>
    public static int Area(int pairIndex) => pairIndex % 2;

    /// <summary>
    /// The preliminary draw of one group: shuffled guilds, the first four as blue and the rest as red, so the empty slots of
    /// a short group are spread as byes (10960000255: random matching inside the group; the bye layout is inferred).
    /// </summary>
    public static Pair[] Draw(IReadOnlyList<long> guilds, Random random)
    {
        var order = guilds.ToArray();
        random.Shuffle(order);
        var pairs = new Pair[4];
        for (int i = 0; i < 4; i++)
            pairs[i] = new Pair(i < order.Length ? order[i] : 0, i + 4 < order.Length ? order[i + 4] : 0);
        return pairs;
    }
}
