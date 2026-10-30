using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The RO1 HP reference (inferred, RO1 reference, not client evidence): monsters the client makes far weaker than their
/// Ragnarok Online 1 counterpart (the Pupa: 4 HP against RO1's 427), listed with their RO1 HP in the
/// embedded Reconstructed/ro1-monster-hp.json. With the switch Ro3Emu:Ro1MonsterHp on, their templates take that HP.
/// </summary>
public sealed partial class MonsterService
{
    public const string Ro1MonsterHpResource = "Ro3Emu.Server.Reconstructed.ro1-monster-hp.json";

    /// <summary>A listed monster: the RO3 NPCConfig row, the RO1 (rAthena) entry and HP it was matched to, and the HP used.</summary>
    public sealed record Ro1MonsterHpEntry(int NpcId, string Name, int Ro1Id, int Ro1Hp, int Hp, string Why);

    /// <summary>NPCConfig id -> HP, empty when the switch is off.</summary>
    private readonly Dictionary<int, int> _ro1Hp;

    private static Dictionary<int, int> Ro1Hp(ServerOptions? options) =>
        options?.Ro1MonsterHp == true ? LoadRo1MonsterHp().ToDictionary(e => e.Key, e => e.Value.Hp) : [];

    /// <summary>The embedded list, keyed by NPCConfig id.</summary>
    public static IReadOnlyDictionary<int, Ro1MonsterHpEntry> LoadRo1MonsterHp()
    {
        using var stream = typeof(MonsterService).Assembly.GetManifestResourceStream(Ro1MonsterHpResource)
            ?? throw new InvalidOperationException("Embedded Reconstructed/ro1-monster-hp.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        var monsters = new Dictionary<int, Ro1MonsterHpEntry>();
        foreach (var m in doc.RootElement.GetProperty("monsters").EnumerateArray())
        {
            var entry = new Ro1MonsterHpEntry(m.GetProperty("npc").GetInt32(), m.GetProperty("name").GetString() ?? "",
                m.GetProperty("ro1Id").GetInt32(), m.GetProperty("ro1Hp").GetInt32(), m.GetProperty("hp").GetInt32(),
                m.GetProperty("why").GetString() ?? "");
            monsters[entry.NpcId] = entry;
        }
        return monsters;
    }
}
