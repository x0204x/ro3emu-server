using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>A field MVP's line-1 state for the web panel's timers, in Unix milliseconds (0 = unknown).</summary>
/// <param name="NextSpawnMs">When it comes back: the death plus its refresh time while it waits (REBORN); 0 otherwise.</param>
public sealed record MvpTimer(int ConfigId, int NpcId, long MapId, int Level, long RefreshMs, bool Alive, long LastDeathMs, long NextSpawnMs);

public sealed partial class MvpService
{
    /// <summary>
    /// Every field MVP of the public line as the loop holds it: alive, or dead with its respawn (REBORN: StateTimeMs is
    /// the death, the next spawn RefreshMs later), with the latest death of any line. Call it on the game loop.
    /// </summary>
    internal IReadOnlyList<MvpTimer> Timers() =>
        [.. States().Values.OrderBy(s => s.Def.CfgId).Select(st =>
        {
            long last = Math.Max(_lastDeath.GetValueOrDefault(st.Def.CfgId), st.DeadMs.Count > 0 ? st.DeadMs.Max() : 0);
            bool waiting = st.Phase == MVP_STATE.Reborn;
            return new MvpTimer(st.Def.CfgId, st.Def.NpcId, st.Def.MapId, st.Def.Level, st.Def.RefreshMs, !waiting && st.Unit is { Dead: false },
                waiting ? Math.Max(last, st.StateTimeMs) : last, waiting ? st.StateTimeMs + st.Def.RefreshMs : 0);
        })];
}
