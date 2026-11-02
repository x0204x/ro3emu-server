using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The picks of a DropPoolConfig slot [group, chance, rolls, picks] (inferred): one successful roll takes up to that many
/// different DropGroupConfig rows by _iWeights. Pool 31121 gives [121121, 100%, 1, 10] over the weapon kit of each class: every
/// weapon once, where drawing with replacement gave one weapon ten times. Pools also set picks to their group's row count.
/// </summary>
internal static class DropPicks
{
    public static List<JsonElement> Distinct(IReadOnlyList<JsonElement> candidates, int picks, Func<long, long> below)
    {
        var left = candidates.Where(row => GameData.Int(row, "_iWeights") > 0).ToList();
        long total = left.Sum(row => GameData.Int(row, "_iWeights"));
        var chosen = new List<JsonElement>(Math.Min(picks, left.Count));
        while (chosen.Count < picks && left.Count > 0)
        {
            long roll = below(total);
            for (int i = 0; i < left.Count; i++)
            {
                long weight = GameData.Int(left[i], "_iWeights");
                roll -= weight;
                if (roll >= 0) continue;
                chosen.Add(left[i]);
                left.RemoveAt(i);
                total -= weight;
                break;
            }
        }
        return chosen;
    }
}
