using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Raid boss trials (Reconstructed/raid-trials.json), inferred as a whole: the original raid bosses ran server AI scripts
/// (NPCConfig _iAISpecial "Raid_S01_Boss01_Test", "RaidFive_Boss01"...) that the client does not ship. What it ships names
/// each scripted mechanic: the raid's on-screen tips (ChatConfig, "Pick up the Giant Blade and interrupt Orc Hero's
/// charging!"), the encounter guide (TenChallengeHandbookIDConfig), the mechanic's NPCs (a Bell Cage, a Golden Horn, a
/// note to collect), its buffs (a note, "Perfect State") and the scene editor's points. A trial puts those together as
/// one rule the party must satisfy within a window; the json gives every number its reason. Generic: nothing in the
/// engine (DungeonService.RaidTrials) knows a boss.
/// </summary>
public sealed partial class ReconstructedDungeons
{
    /// <summary>
    /// What the party must do before the window closes. Per player: <see cref="Stand"/> inside an object, <see cref="Avoid"/>
    /// every object's area, <see cref="Shelter"/> behind an object as seen from the boss, <see cref="Guard"/> (a marked
    /// player with a carrier beside), <see cref="Spread"/> (a marked player with nobody else beside). For the party:
    /// <see cref="Occupy"/> every object at once, <see cref="Collect"/> every object by walking into it, <see cref="Kill"/>
    /// every object (or the chosen one), <see cref="Carry"/> an object to the boss (touch it, then hit the boss),
    /// <see cref="Lure"/> the boss into an object's area (it stands there when the window closes).
    /// </summary>
    public enum TrialRule { Stand, Avoid, Shelter, Guard, Spread, Occupy, Collect, Kill, Carry, Lure }

    /// <summary>Who an outcome reaches: the players who failed (or passed), everyone present, the marked players, or the
    /// players inside an object left at the end (a ghost ship's lane).</summary>
    public enum TrialWho { Failing, All, Marked, Passing, Inside }

    /// <summary>What a trial object's interaction does: nothing (it is judged), hand its colour, or take the player to the
    /// next object of its kind.</summary>
    public enum TrialUse { None, Color, Warp }

    /// <summary>
    /// Where a trial's objects stand: scene-editor <paramref name="Points"/> or <paramref name="Regions"/> (their centres),
    /// a dungeon point <paramref name="Group"/>, under every marked player (<paramref name="OnMarked"/>) or every player
    /// (<paramref name="OnPlayers"/>), or at the boss (<paramref name="AtBoss"/>). <paramref name="Count"/> (or
    /// <paramref name="PerPlayer"/> per present player, within <paramref name="Min"/>-<paramref name="Max"/>) of the listed
    /// places are drawn at random; 0 takes them all. <paramref name="Inward"/> moves each place that share of the way
    /// toward the boss.
    /// </summary>
    public sealed record TrialPlace(IReadOnlyList<long> Points, IReadOnlyList<long> Regions, int Group, bool OnMarked, bool OnPlayers,
        bool AtBoss, int Count, int PerPlayer, int Min, int Max, float Inward);

    /// <summary>
    /// A trial's objects. <paramref name="Npcs"/> lists one NPCConfig id per colour (the trial's <see cref="RaidTrial.Colors"/>);
    /// a single id has no colour. A <paramref name="Monster"/> is a destructible unit: <paramref name="Hits"/> damaging hits
    /// per present player destroy it whatever their damage (0: its own HP), and with <paramref name="Match"/> only a player
    /// of its colour damages it. An object with a <paramref name="Use"/> is not judged: its NPC's own 32700 interaction
    /// hands the player the object's colour (Ilmo's notes) or takes the player to the next object of its kind (a wormhole). <paramref name="Chosen"/>: one object of them is the one that counts, shown by
    /// <paramref name="MarkBuff"/> for <paramref name="MarkMs"/> (0: the whole window). <paramref name="Lane"/>: the object's
    /// area is a rectangle (length, width) ahead of it along its facing instead of a circle.
    /// </summary>
    public sealed record TrialObject(IReadOnlyList<int> Npcs, TrialPlace Place, IReadOnlyList<int> Buffs, bool Monster, int Hits,
        bool Match, TrialUse Use, bool Chosen, int MarkBuff, int MarkMs, (float Length, float Width)? Lane)
    {
        /// <summary>A destructible monster that stands still and never strikes back even though its NPC has an AI
        /// (Baphomet's illusions).</summary>
        public bool Still { get; init; }
    }

    /// <summary>
    /// A result: <paramref name="HpPercent"/> of the maximum HP of each player it reaches (with <paramref name="PerObject"/>,
    /// once per object left, up to <paramref name="CapPercent"/>), shown as <paramref name="Skill"/>'s hit; a
    /// <paramref name="Buff"/> for <paramref name="BuffMs"/>; a <paramref name="Say"/> line to them; the boss staggers
    /// (<paramref name="Stagger"/> show for <paramref name="StaggerMs"/>, its performance interrupted).
    /// </summary>
    public sealed record TrialOutcome(TrialWho Who, int HpPercent, bool PerObject, int CapPercent, long Skill, int Buff, int BuffMs,
        int Say, long Stagger, int StaggerMs);

    /// <summary>
    /// One trial: it starts <paramref name="FirstMs"/> after the boss engages and then every <paramref name="EveryMs"/>, and
    /// once at each of its <paramref name="Hp"/> thresholds (percent of the boss's HP). It sends its <paramref name="Tips"/>
    /// (or the <paramref name="TipByCount"/> line for the number of its objects; their ${1} is <paramref name="TipParam"/>:
    /// "countdown", the window's seconds, which a CombatCountdownTip counts down, or "marked", a marked player's name), holds the boss (<paramref name="Hold"/>,
    /// playing <paramref name="Show"/>) and judges <paramref name="Rule"/> after <paramref name="WindowMs"/>.
    /// <paramref name="Colors"/> are the buffs that give a player a colour; <paramref name="Assign"/> deals them out at the
    /// start. <paramref name="Marks"/> random players get <paramref name="MarkBuff"/> (a trap or a target; taken off at the
    /// end). <paramref name="Carry"/> is the buff a touched object gives, for its time, with a line to its carrier.
    /// <paramref name="Breakers"/>: boss skills whose landing area destroys the objects inside; <paramref name="Casts"/>:
    /// boss skills it casts at a random player every so often during the window (or beams it aims at one, <see cref="TrialCast"/>).
    /// </summary>
    public sealed record RaidTrial(string Name, int FirstMs, int EveryMs, IReadOnlyList<int> Hp, IReadOnlyList<int> Tips,
        IReadOnlyDictionary<int, int> TipByCount, string TipParam, int WindowMs, bool Hold, long Show, IReadOnlyList<int> Colors, bool Assign,
        int Marks, int MarkBuff, (int Buff, int Ms, int Say) Carry, TrialRule Rule, float Radius, float Width, float Depth,
        IReadOnlyList<TrialObject> Objects, TrialOutcome Fail, TrialOutcome? Success, IReadOnlyList<long> Breakers,
        IReadOnlyList<TrialCast> Casts);

    /// <summary>
    /// A boss skill during a trial's window, every <paramref name="EveryMs"/> at a random player: cast from the boss's set,
    /// or with a <paramref name="BeamWidth"/>, a beam: the skill's show aimed at the player (no hit, and the boss keeps
    /// fighting), whose line from the boss, <paramref name="BeamLength"/> metres long and that wide, destroys the trial's
    /// objects on it (Osiris's Dark Pulse detonating the Horongs).
    /// </summary>
    public sealed record TrialCast(long Skill, int EveryMs, float BeamLength, float BeamWidth);

    /// <summary>A raid boss's trials, for each of its difficulty NPCs (<paramref name="Bosses"/>).</summary>
    public sealed record RaidScript(string Key, string Name, IReadOnlyList<int> Bosses, IReadOnlyList<RaidTrial> Trials);

    private readonly Dictionary<int, RaidScript> _raids = [];
    private readonly List<RaidScript> _raidList = [];

    public IReadOnlyList<RaidScript> AllRaids => _raidList;

    /// <summary>The trials of a raid boss NPC, when the switch is on.</summary>
    public RaidScript? Raid(int bossNpc) => Enabled && _raids.TryGetValue(bossNpc, out var r) ? r : null;

    private void LoadRaidTrials()
    {
        using var stream = typeof(ReconstructedDungeons).Assembly.GetManifestResourceStream("Ro3Emu.Server.Reconstructed.raid-trials.json")
            ?? throw new InvalidOperationException("Embedded Reconstructed/raid-trials.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        foreach (var entry in doc.RootElement.GetProperty("raids").EnumerateObject())
        {
            var bosses = Ints(entry.Value, "bosses");
            var trials = entry.Value.GetProperty("trials").EnumerateArray().Select(Trial).ToArray();
            if (bosses.Count == 0 || trials.Length == 0) throw new InvalidOperationException($"Raid {entry.Name} has no boss or trial.");
            var raid = new RaidScript(entry.Name, entry.Value.GetProperty("name").GetString() ?? "", bosses, trials);
            _raidList.Add(raid);
            foreach (int boss in bosses)
                if (!_raids.TryAdd(boss, raid)) throw new InvalidOperationException($"Boss {boss} has two raid scripts.");
        }
    }

    private static RaidTrial Trial(JsonElement t)
    {
        var start = t.GetProperty("start");
        var boss = t.TryGetProperty("boss", out var b) ? b : default;
        var rule = Enum.Parse<TrialRule>(t.GetProperty("rule").GetString()!, ignoreCase: true);
        var tipByCount = new Dictionary<int, int>();
        if (t.TryGetProperty("tipByCount", out var byCount))
            foreach (var p in byCount.EnumerateObject()) tipByCount[int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)] = p.Value.GetInt32();
        var marks = t.TryGetProperty("marks", out var m) ? m : default;
        var carry = t.TryGetProperty("carry", out var c) ? c : default;
        var trial = new RaidTrial(t.GetProperty("name").GetString() ?? "",
            Int(start, "firstMs"), Int(start, "everyMs"), Ints(start, "hp"), Ints(t, "tips"), tipByCount,
            t.TryGetProperty("tipParam", out var tipParam) ? tipParam.GetString() ?? "" : "", t.GetProperty("windowMs").GetInt32(),
            boss.ValueKind == JsonValueKind.Object && boss.TryGetProperty("hold", out var hold) && hold.GetBoolean(),
            boss.ValueKind == JsonValueKind.Object ? Int(boss, "show") : 0,
            Ints(t, "colors"), t.TryGetProperty("assign", out var assign) && assign.GetBoolean(),
            marks.ValueKind == JsonValueKind.Object ? Int(marks, "count") : 0, marks.ValueKind == JsonValueKind.Object ? Int(marks, "buff") : 0,
            (Int(carry, "buff"), Int(carry, "ms"), Int(carry, "say")),
            rule, Float(t, "radius"), Float(t, "width"), Float(t, "depth"),
            t.TryGetProperty("objects", out var objects) ? [.. objects.EnumerateArray().Select(Object)] : [],
            Outcome(t.GetProperty("fail")), t.TryGetProperty("success", out var success) ? Outcome(success) : null,
            boss.ValueKind == JsonValueKind.Object && boss.TryGetProperty("breakers", out var breakers)
                ? [.. breakers.EnumerateArray().Select(v => v.GetInt64())] : [],
            boss.ValueKind == JsonValueKind.Object && boss.TryGetProperty("casts", out var casts)
                ? [.. casts.EnumerateArray().Select(Cast)] : []);
        if (trial.WindowMs <= 0 || trial.FirstMs <= 0 && trial.Hp.Count == 0 || trial.EveryMs is > 0 and < 10000)
            throw new InvalidOperationException($"Raid trial '{trial.Name}' has no window or no start.");
        return trial;
    }

    private static TrialObject Object(JsonElement o)
    {
        var npcs = o.GetProperty("npc") is { ValueKind: JsonValueKind.Array } list ? [.. list.EnumerateArray().Select(v => v.GetInt32())]
            : new[] { o.GetProperty("npc").GetInt32() };
        var at = o.GetProperty("at");
        var place = new TrialPlace(Longs(at, "points"), Longs(at, "regions"), Int(at, "group"), Bool(at, "onMarked"), Bool(at, "onPlayers"),
            Bool(at, "atBoss"), Int(at, "count"), Int(at, "perPlayer"), Int(at, "min"), at.TryGetProperty("max", out var max) ? max.GetInt32() : int.MaxValue,
            Float(at, "inward"));
        return new TrialObject(npcs, place, Ints(o, "buffs"), Bool(o, "monster"), Int(o, "hits"), Bool(o, "match"),
            o.TryGetProperty("use", out var use) ? Enum.Parse<TrialUse>(use.GetString()!, ignoreCase: true) : TrialUse.None,
            Bool(o, "chosen"), Int(o, "markBuff"), Int(o, "markMs"),
            o.TryGetProperty("lane", out var lane) ? (lane[0].GetSingle(), lane[1].GetSingle()) : null)
        {
            Still = Bool(o, "still"),
        };
    }

    private static TrialCast Cast(JsonElement c) => new(c.GetProperty("skill").GetInt64(), c.GetProperty("everyMs").GetInt32(),
        c.TryGetProperty("beam", out var beam) ? beam[0].GetSingle() : 0, c.TryGetProperty("beam", out var wide) ? wide[1].GetSingle() : 0);

    private static TrialOutcome Outcome(JsonElement o) => new(
        o.TryGetProperty("who", out var who) ? Enum.Parse<TrialWho>(who.GetString()!, ignoreCase: true) : TrialWho.Failing,
        Int(o, "hpPercent"), Bool(o, "perObject"), o.TryGetProperty("capPercent", out var cap) ? cap.GetInt32() : 100, Int(o, "skill"),
        Int(o, "buff"), Int(o, "buffMs"), Int(o, "say"), Int(o, "stagger"), Int(o, "staggerMs"));

    private static int Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.GetInt32() : 0;
    private static float Float(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetSingle() : 0;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.GetBoolean();
    private static List<int> Ints(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        ? [.. v.EnumerateArray().Select(x => x.GetInt32())] : [];
    private static List<long> Longs(JsonElement e, string name) => e.TryGetProperty(name, out var v)
        ? [.. v.EnumerateArray().Select(x => x.GetInt64())] : [];
}
