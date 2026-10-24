using Romsg;
using Ro3Emu.Server.Data.Config;
using Kind = Ro3Emu.Server.Game.ReconstructedDungeons.TriggerKind;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A scripted step's hide-and-seek game (Moonlight Flower's 3120802), inferred as a whole: the client has the game's quest
/// ("Hide in the darkness. Don't let them Discover you."), its lines (ChatConfig 5310005-5310014, which nothing in the
/// client sends: the original scene script did) and a ring of server points around the room, but not the script. The
/// rounds below are built from those lines: "I'm about to catch you! Watch out!", the 5 s countdown tip, "You're pretty
/// good at hide-and-seek! Play with me a little longer!", "I had a great time~" and, when found, "You're no good at
/// hide-and-seek! Hahaha! Play with me again~". The lines go out as 1022 with the seeker's uid, which a Bubbling row
/// (type 2) needs for its head bubble (LC_SystemMessageDecodeFunc.PushBubbling uses playerId as the unit).
/// </summary>
public sealed partial class DungeonService
{
    private const uint SystemBroadcast = 1022;
    // (inferred) the gaps between the opening bubbles, from a round's warning to its countdown and after a look.
    private const int SeekOpeningGapMs = 1500, SeekWarnMs = 3000, SeekPauseMs = 3000;

    private void StartSeek(Attempt a, ReconstructedDungeons.Seek seek)
    {
        a.SeekRound = 1;
        a.Searched.Clear();
        int generation = ++a.StepGeneration;
        for (int i = 0; i < seek.Opening.Count; i++)
        {
            int chat = seek.Opening[i];
            StepLater(a, generation, i * SeekOpeningGapMs, () => SeekChatAsync(a, chat));
        }
        StepLater(a, generation, seek.Opening.Count * SeekOpeningGapMs, () => SeekRoundAsync(a, generation, seek));
    }

    /// <summary>A timer of the step: it does nothing once a newer game or step started, or (unless
    /// <paramref name="whileFighting"/>: a fight-only step is done from its start) once the step is done.</summary>
    private void StepLater(Attempt a, int generation, int ms, Func<Task> work, bool whileFighting = false) =>
        _loop.ScheduleLegacy(TimeSpan.FromMilliseconds((long)ms * Math.Max(1, _options.ScriptedScenePacePercent) / 100),
            () => !a.Settled && !a.Room.Closed && (whileFighting || !a.StepDone) && a.StepGeneration == generation
                ? work() : Task.CompletedTask);

    private Task SeekChatAsync(Attempt a, int chat) => BroadcastAsync(a, SystemBroadcast, new SystemBoardCastMsgNtf
    { Source = MsgSource.Npc, ChatId = chat, PlayerId = a.SceneActors.FirstOrDefault()?.Uid ?? 0 });

    /// <summary>A round: the warning, then the countdown tip, then the look when it runs out.</summary>
    private async Task SeekRoundAsync(Attempt a, int generation, ReconstructedDungeons.Seek seek)
    {
        await SeekChatAsync(a, seek.Round);
        int countdown = _data.Row("ChatConfig", seek.Countdown) is { } row ? (int)new ChatConfig(row).Time : 5000;
        StepLater(a, generation, SeekWarnMs, () => SeekChatAsync(a, seek.Countdown));
        StepLater(a, generation, SeekWarnMs + countdown, () => SeekLookAsync(a, generation, seek));
    }

    private async Task SeekLookAsync(Attempt a, int generation, ReconstructedDungeons.Seek seek)
    {
        var members = Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading).ToArray();
        if (members.Length == 0 || Step(a) is not { } step) return;
        var places = members.Select(m => HidingPlace(a, step, m)).ToArray();
        if (places.Any(p => p == null))
        {
            // Found: the game starts over (searched places are free again).
            await SeekChatAsync(a, seek.Found);
            a.SeekRound = 1;
            a.Searched.Clear();
            StepLater(a, generation, SeekPauseMs, () => SeekRoundAsync(a, generation, seek));
            return;
        }
        foreach (var place in places)
            if (_catalog.ScenePoint(a.Plan.SceneEntry, place!.Value) is { } at)
                foreach (var t in step.Complete)
                    if (_catalog.ScenePoint(a.Plan.SceneEntry, t.Id) is { } p
                        && MonsterService.Distance(at.X, at.Z, p.X, p.Z) <= seek.SearchedRadius) a.Searched.Add(t.Id);
        if (a.SeekRound >= seek.Rounds)
        {
            await SeekChatAsync(a, seek.Won);
            await StepEventAsync(a, members[0], [(Kind.Point, places[0]!.Value)]);
            return;
        }
        await SeekChatAsync(a, seek.Longer);
        a.SeekRound++;
        StepLater(a, generation, SeekPauseMs, () => SeekRoundAsync(a, generation, seek));
    }

    /// <summary>The step's point a participant hides at: within its radius and not searched in this game.</summary>
    private long? HidingPlace(Attempt a, ReconstructedDungeons.ScriptedStep step, GameSession s) =>
        step.Complete.FirstOrDefault(t => !a.Searched.Contains(t.Id) && _catalog.ScenePoint(a.Plan.SceneEntry, t.Id) is { } p
            && MonsterService.Distance(s.Position.X, s.Position.Z, p.X, p.Z) <= t.Radius)?.Id;
}
