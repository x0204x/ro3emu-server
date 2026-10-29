using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// A normal attack at the same target waits for the swing before it (CombatService.SwingInFlight, issue #121), as
/// the client does. Tests that strike one target again right away wait here first: until the swing in flight at that
/// target is within CombatService.SwingJitterMs of its end, or at once when there is none.
/// </summary>
internal static class SwingWait
{
    public static async Task UntilDoneAsync(GameLoop loop, GameSession session, long targetId)
    {
        long end = await loop.InvokeAsync(() => Task.FromResult(session.SkillCast is { Cancelled: false } swing && swing.Request.TargetId == targetId
            ? swing.StartMs + swing.ChantMs + swing.ActionMs : 0));
        long wait = end - CombatService.SwingJitterMs + 10 - MonsterService.NowMs;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
    }
}
