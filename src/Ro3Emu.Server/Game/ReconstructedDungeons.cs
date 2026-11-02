using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Reconstructed story dungeon encounters (docs/systems/dungeon-inventory.md): the
/// original server's encounter scripts and its 2000xxxx monster templates are not in the client. The embedded
/// Reconstructed/dungeon-monsters.json places monsters the client knows (NPCConfig) at authored scene-editor points,
/// one wave per quest stage, with a reason and a confidence for every decision, and can name the monster whose normal
/// attack a template without one of its own borrows. The embedded Reconstructed/scripted-scenes.json
/// rebuilds the scripted story scenes without a fight (ReconstructedDungeons.Scripted.cs). Switch: Ro3Emu:ReconstructedDungeons.
/// </summary>
public sealed partial class ReconstructedDungeons
{
    public sealed record Spawn(long Point, int NpcId);
    public sealed record Wave(int Quest, IReadOnlyList<Spawn> Spawns);
    public sealed record Dungeon(long Scene, string Name, string Confidence, IReadOnlyList<Wave> Waves);

    private readonly Dictionary<long, Dungeon> _dungeons = [];
    private readonly Dictionary<int, int> _attackSources = [];
    private readonly Dictionary<int, (int Hp, int Attack)> _soloTuning = [];

    public ReconstructedDungeons(IOptions<ServerOptions> options)
    {
        Enabled = options.Value.ReconstructedDungeons;
        using var stream = typeof(ReconstructedDungeons).Assembly.GetManifestResourceStream("Ro3Emu.Server.Reconstructed.dungeon-monsters.json")
            ?? throw new InvalidOperationException("Embedded Reconstructed/dungeon-monsters.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        foreach (var attack in doc.RootElement.GetProperty("monsterAttacks").EnumerateObject())
            if (int.TryParse(attack.Name, out int npc)) _attackSources[npc] = attack.Value.GetProperty("from").GetInt32();
        if (doc.RootElement.TryGetProperty("soloTuning", out var tuning))
            foreach (var boss in tuning.EnumerateObject())
                if (int.TryParse(boss.Name, out int npc))
                    _soloTuning[npc] = (boss.Value.GetProperty("hp").GetInt32(), boss.Value.GetProperty("attack").GetInt32());
        foreach (var entry in doc.RootElement.GetProperty("dungeons").EnumerateObject())
        {
            long scene = long.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture);
            var waves = entry.Value.GetProperty("waves").EnumerateArray()
                .Select(w => new Wave(w.TryGetProperty("quest", out var q) ? q.GetInt32() : 0, [.. w.GetProperty("spawns").EnumerateArray().Select(s => new Spawn(s.GetProperty("point").GetInt64(), s.GetProperty("npc").GetInt32()))]))
                .ToArray();
            _dungeons[scene] = new Dungeon(scene, entry.Value.GetProperty("name").GetString() ?? "",
                entry.Value.GetProperty("confidence").GetString() ?? "", waves);
        }
        LoadScriptedScenes();
        LoadRaidTrials();
    }

    public bool Enabled { get; }
    public IReadOnlyCollection<Dungeon> All => _dungeons.Values;

    /// <summary>The reconstruction of a scene, when the switch is on.</summary>
    public Dungeon? For(long scene) => Enabled && _dungeons.TryGetValue(scene, out var d) ? d : null;

    /// <summary>The monster whose normal attack a template without its own borrows (applies whenever the template is used).</summary>
    public int? AttackSource(int npcId) => Enabled && _attackSources.TryGetValue(npcId, out int from) ? from : null;

    /// <summary>A story boss's solo scaling (basis points of HP and of attack); its allied companions are not reproduced.</summary>
    public (int Hp, int Attack)? SoloTuning(int npcId) => Enabled && _soloTuning.TryGetValue(npcId, out var t) ? t : null;
}
