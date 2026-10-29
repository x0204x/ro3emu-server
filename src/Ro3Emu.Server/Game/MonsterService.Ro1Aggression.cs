using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The RO1 aggression reference (inferred, RO1 reference, not client evidence): field monsters whose Ragnarok Online 1
/// counterpart is aggressive in rAthena's pre-renewal mob_db, matched by English name in the embedded
/// Reconstructed/ro1-aggressive-monsters.json. With the switch Ro3Emu:Ro1AggressiveMonsters on, they attack on sight
/// like the client's AIConfig 20000004 monsters (<see cref="AggressiveAi"/>); off, nothing changes.
/// </summary>
public sealed partial class MonsterService
{
    public const string Ro1AggressiveResource = "Ro3Emu.Server.Reconstructed.ro1-aggressive-monsters.json";

    /// <summary>A listed monster: the RO3 NPCConfig row and the RO1 (rAthena) entry it was matched to.</summary>
    public sealed record Ro1AggressiveMonster(int NpcId, string Name, int Ro1Id, string AegisName, string Ro1Name, string Match, string Why);

    /// <summary>The listed monsters, empty when the switch is off.</summary>
    private readonly HashSet<int> _ro1Aggressive;

    private static HashSet<int> Ro1Aggressive(ServerOptions? options) =>
        options?.Ro1AggressiveMonsters == true ? [.. LoadRo1AggressiveMonsters().Keys] : [];

    /// <summary>The embedded list, keyed by NPCConfig id (parsed on each call; the service keeps its own set).</summary>
    public static IReadOnlyDictionary<int, Ro1AggressiveMonster> LoadRo1AggressiveMonsters()
    {
        using var stream = typeof(MonsterService).Assembly.GetManifestResourceStream(Ro1AggressiveResource)
            ?? throw new InvalidOperationException("Embedded Reconstructed/ro1-aggressive-monsters.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        var monsters = new Dictionary<int, Ro1AggressiveMonster>();
        foreach (var entry in doc.RootElement.GetProperty("monsters").EnumerateObject())
        {
            int npcId = int.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture);
            var m = entry.Value;
            var ro1 = m.GetProperty("ro1");
            monsters[npcId] = new Ro1AggressiveMonster(npcId, m.GetProperty("name").GetString() ?? "",
                ro1.GetProperty("id").GetInt32(), ro1.GetProperty("aegisName").GetString() ?? "", ro1.GetProperty("name").GetString() ?? "",
                m.GetProperty("match").GetString() ?? "", m.GetProperty("why").GetString() ?? "");
        }
        return monsters;
    }
}
