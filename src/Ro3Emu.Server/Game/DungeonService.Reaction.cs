using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A scripted step's reactions (ReconstructedDungeons.Reaction, inferred as to when they go out): the speaker's ChatConfig
/// lines as 1022 with its uid, as Moonlight Flower's game sends hers, and a BuffConfig effect on the actor standing where
/// the player made a choice. Louie's 3120703 shell game: his lines (Secretary rows 5310002-5310004, 5310008, 5310009)
/// and the right/wrong Bell effects (5402101 SXGD_RIGHT, 5402201 SXGD_WRONG, whose sound and effects play when the buff
/// starts) are in the client, but nothing in the client sends them.
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>The step's opening lines, each once the previous one's display time has run out.</summary>
    private void StartReaction(Attempt a, ReconstructedDungeons.Reaction react)
    {
        int generation = a.StepGeneration, at = 0;
        foreach (int chat in react.Start)
        {
            StepLater(a, generation, at, () => SayAsync(a, react.Speaker, chat));
            at += LineMs(chat);
        }
    }

    /// <summary>
    /// Answers an event with the response's line and effect. Returns how long the line stays on screen, which a completing
    /// reaction waits out before the next step.
    /// </summary>
    private async Task<int> RespondAsync(Attempt a, ReconstructedDungeons.Reaction? react,
        ReconstructedDungeons.Response? response, SceneActorUnit? used)
    {
        if (react == null || response == null || a.Settled || a.Room.Closed) return 0;
        if (response.Buff != 0 && used != null
            && a.SceneActors.FirstOrDefault(u => u.NpcId == response.On && u.Point == used.Point) is { } target)
            await ReplayBuffAsync(target, response.Buff);
        if (response.Say == 0) return 0;
        await SayAsync(a, react.Speaker, response.Say);
        return LineMs(response.Say);
    }

    /// <summary>Puts the effect on again even when the actor already has it, so its start sound and effects play again.</summary>
    private async Task ReplayBuffAsync(SceneActorUnit target, int buff)
    {
        int[] others = [.. target.Buffs.Select(b => b.ConfigId).Where(id => id != buff)];
        await SetActorBuffsAsync(target, others);
        await SetActorBuffsAsync(target, [.. others, buff]);
    }

    private Task SayAsync(Attempt a, int speaker, int chat) => BroadcastAsync(a, SystemBroadcast, new SystemBoardCastMsgNtf
    { Source = MsgSource.Npc, ChatId = chat, PlayerId = a.SceneActors.FirstOrDefault(u => u.NpcId == speaker)?.Uid ?? 0 });

    private int LineMs(int chat) => _data.Row("ChatConfig", chat) is { } row ? (int)new ChatConfig(row).Time : 0;

    /// <summary>A scripted scene's delay, shortened by ScriptedScenePacePercent (tests).</summary>
    private TimeSpan Paced(int ms) => TimeSpan.FromMilliseconds((long)ms * Math.Max(1, _options.ScriptedScenePacePercent) / 100);
}
