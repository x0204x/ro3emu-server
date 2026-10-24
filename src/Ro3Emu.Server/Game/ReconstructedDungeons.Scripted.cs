using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Scripted story scenes, with or without fights (Reconstructed/scripted-scenes.json; its "contract" section cites the client
/// code for every event used here). The original server's scene script is not in the client: each scene is rebuilt as
/// steps, each naming the DungeonQuestConfig quest the tracker shows, the scene actors shown during it, an optional
/// run-time story the client plays, and the client events that complete it. Nothing is keyed by a task id: a step
/// completes on any listed event (interaction, dungeon message, region, point or plot end).
/// </summary>
public sealed partial class ReconstructedDungeons
{
    public enum TriggerKind { Interaction, Message, Region, Point, Plot }

    /// <summary>A client event that completes (or, under "ignore", deliberately does not complete) a step.</summary>
    public sealed record StepTrigger(TriggerKind Kind, long Id, float Radius = 0);

    /// <summary>A scene actor: an NPCConfig id at a scene-editor point, with the buffs (BuffConfig ids) it carries.
    /// <paramref name="Dx"/>/<paramref name="Dz"/> (metres) place it beside the point when the scene has no point of its own
    /// for it (inferred placement).</summary>
    public sealed record SceneActor(int NpcId, long Point, IReadOnlyList<int> Buffs, float Dx = 0, float Dz = 0);

    /// <summary>
    /// One scripted step. <paramref name="Story"/> is a PlotInterpretation id the client plays at the step's start
    /// (0: none); <paramref name="TeleportAfter"/> a scene-editor point the participants are moved to when it
    /// completes (0: none). <paramref name="Goal"/> is the tracker's "(done/total)" while the step runs.
    /// <paramref name="Fight"/> are monsters spawned at the step's start: the step also waits until every one is
    /// defeated (a story dungeon that mixes fights with scripted parts). <paramref name="All"/>: every completing event
    /// is needed, not any one ("Have a Chitchat with them"). <paramref name="Strikes"/>: an interaction of an actor that
    /// casts a skill at the nearest monster of the step's fight (the airship's cannons); with Shots above 0, that many
    /// strikes defeat a monster whatever the skill's damage. <paramref name="Seek"/>: the step is a hide-and-seek game
    /// whose hiding places are its Point triggers; walking there does not complete it, winning the game does.
    /// </summary>
    public sealed record ScriptedStep(string Stage, int Quest, (int Done, int Total) Goal, IReadOnlyList<SceneActor> Actors,
        long Story, IReadOnlyList<StepTrigger> Complete, IReadOnlyList<StepTrigger> Ignore, long TeleportAfter,
        IReadOnlyList<Spawn> Fight, bool All, IReadOnlyList<(long Interaction, long Skill, int Shots)>? Strikes = null,
        Seek? Seek = null, Shelter? Shelter = null);

    /// <summary>
    /// Cover from a boss's sweeping attack (inferred, the story Pharaoh's 3120702 after the raid Pharaoh's "Pharaoh is
    /// unleashing Cleansing Light! Quickly, hide behind the Pyramid!"): every <paramref name="EveryMs"/> while the fight's
    /// <paramref name="Boss"/> lives, the <paramref name="Tip"/> ChatConfig row goes out and the boss holds its ground for
    /// <paramref name="WarnMs"/>; then <paramref name="Skill"/> hits every participant not in the shadow of the
    /// <paramref name="Cover"/> actor: behind it as seen from the boss, within <paramref name="Width"/> metres of that line
    /// and <paramref name="Depth"/> metres past the cover.
    /// </summary>
    public sealed record Shelter(int Boss, int Cover, int Tip, long Skill, int EveryMs, int WarnMs, float Width, float Depth);

    /// <summary>
    /// A hide-and-seek game (inferred, Moonlight Flower's 3120802): the step's first actor opens with its
    /// <paramref name="Opening"/> bubbles; each round warns (<paramref name="Round"/>), counts down
    /// (<paramref name="Countdown"/>, a CombatCountdownTip row whose time is the count) and looks. Every participant must
    /// then stand at one of the step's points that this game has not searched yet. A found participant restarts the game
    /// (<paramref name="Found"/>, a bubble); a passed round searches the points within <paramref name="SearchedRadius"/>
    /// of each hiding place (<paramref name="Longer"/>), and the last of <paramref name="Rounds"/> wins
    /// (<paramref name="Won"/>), completing the step. Every message is a ChatConfig row sent as 1022.
    /// </summary>
    public sealed record Seek(int Rounds, float SearchedRadius, IReadOnlyList<int> Opening, int Round, int Countdown, int Longer,
        int Won, int Found);

    /// <summary>
    /// A scene. <paramref name="Unplayed"/> are DungeonQuestConfig quests of the scene that no step shows: the branch
    /// that shows them cannot be reached in the reconstruction (each has its reason in the json, "unplayed").
    /// </summary>
    public sealed record ScriptedScene(long Scene, string Name, string Confidence, IReadOnlyList<ScriptedStep> Steps,
        IReadOnlyList<int> Unplayed);

    private readonly Dictionary<long, ScriptedScene> _scripted = new();

    public IReadOnlyCollection<ScriptedScene> AllScripted => _scripted.Values;

    /// <summary>The scripted reconstruction of a scene, when the switch is on.</summary>
    public ScriptedScene? Scripted(long scene) => Enabled && _scripted.TryGetValue(scene, out var s) ? s : null;

    private void LoadScriptedScenes()
    {
        using var stream = typeof(ReconstructedDungeons).Assembly.GetManifestResourceStream("Ro3Emu.Server.Reconstructed.scripted-scenes.json")
            ?? throw new InvalidOperationException("Embedded Reconstructed/scripted-scenes.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        foreach (var entry in doc.RootElement.GetProperty("scenes").EnumerateObject())
        {
            long scene = long.Parse(entry.Name, CultureInfo.InvariantCulture);
            var steps = entry.Value.GetProperty("steps").EnumerateArray().Select(Step).ToArray();
            if (steps.Length == 0) throw new InvalidOperationException($"Scripted scene {scene} has no steps.");
            var unplayed = entry.Value.TryGetProperty("unplayed", out var u)
                ? u.EnumerateArray().Select(q => q.GetProperty("quest").GetInt32()).ToArray() : [];
            _scripted[scene] = new ScriptedScene(scene, entry.Value.GetProperty("name").GetString() ?? "",
                entry.Value.GetProperty("confidence").GetString() ?? "", steps, unplayed);
        }
    }

    private static ScriptedStep Step(JsonElement s)
    {
        var goal = s.TryGetProperty("goal", out var g) ? (g[0].GetInt32(), g[1].GetInt32()) : (0, 1);
        var actors = s.TryGetProperty("actors", out var a)
            ? a.EnumerateArray().Select(x => new SceneActor(x.GetProperty("npc").GetInt32(), x.GetProperty("point").GetInt64(),
                x.TryGetProperty("buffs", out var b) ? b.EnumerateArray().Select(v => v.GetInt32()).ToArray() : [],
                x.TryGetProperty("offset", out var o) ? o[0].GetSingle() : 0, x.TryGetProperty("offset", out o) ? o[1].GetSingle() : 0)).ToArray()
            : [];
        var complete = Triggers(s, "complete");
        var fight = s.TryGetProperty("fight", out var f)
            ? f.EnumerateArray().Select(x => new Spawn(x.GetProperty("point").GetInt64(), x.GetProperty("npc").GetInt32())).ToArray()
            : [];
        if (complete.Count == 0 && fight.Length == 0)
            throw new InvalidOperationException($"Scripted step '{s.GetProperty("stage").GetString()}' cannot complete.");
        return new ScriptedStep(s.GetProperty("stage").GetString() ?? "", s.GetProperty("quest").GetInt32(), goal, actors,
            s.TryGetProperty("story", out var story) ? story.GetInt64() : 0, complete, Triggers(s, "ignore"),
            s.TryGetProperty("after", out var after) && after.TryGetProperty("teleport", out var to) ? to.GetInt64() : 0,
            fight, s.TryGetProperty("all", out var all) && all.GetBoolean(),
            s.TryGetProperty("strike", out var strike)
                ? strike.EnumerateArray().Select(x => (x.GetProperty("interaction").GetInt64(), x.GetProperty("skill").GetInt64(),
                    x.TryGetProperty("shots", out var shots) ? shots.GetInt32() : 0)).ToArray()
                : null,
            s.TryGetProperty("seek", out var seek)
                ? new Seek(seek.GetProperty("rounds").GetInt32(), seek.GetProperty("searchedRadius").GetSingle(),
                    [.. seek.GetProperty("opening").EnumerateArray().Select(v => v.GetInt32())], seek.GetProperty("round").GetInt32(),
                    seek.GetProperty("countdown").GetInt32(), seek.GetProperty("longer").GetInt32(), seek.GetProperty("won").GetInt32(),
                    seek.GetProperty("found").GetInt32())
                : null,
            s.TryGetProperty("shelter", out var shelter)
                ? new Shelter(shelter.GetProperty("boss").GetInt32(), shelter.GetProperty("cover").GetInt32(), shelter.GetProperty("tip").GetInt32(),
                    shelter.GetProperty("skill").GetInt64(), shelter.GetProperty("everyMs").GetInt32(), shelter.GetProperty("warnMs").GetInt32(),
                    shelter.GetProperty("width").GetSingle(), shelter.GetProperty("depth").GetSingle())
                : null);
    }

    private static IReadOnlyList<StepTrigger> Triggers(JsonElement step, string field)
    {
        if (!step.TryGetProperty(field, out var list)) return [];
        var triggers = new List<StepTrigger>();
        foreach (var t in list.EnumerateArray())
        {
            var kinds = Enum.GetValues<TriggerKind>().Where(k => t.TryGetProperty(k.ToString().ToLowerInvariant(), out _)).ToArray();
            if (kinds.Length != 1) throw new InvalidOperationException($"A scripted trigger names {kinds.Length} kinds: {t}");
            var kind = kinds[0];
            triggers.Add(new StepTrigger(kind, t.GetProperty(kind.ToString().ToLowerInvariant()).GetInt64(),
                t.TryGetProperty("radius", out var r) ? r.GetSingle() : 0));
        }
        return triggers;
    }
}
