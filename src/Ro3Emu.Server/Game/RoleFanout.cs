namespace Ro3Emu.Server.Game;

/// <summary>
/// A realm pass that runs one role's owned work per online session (projection, settlement, kill credit). A role
/// that switched or is logging out refuses new mutations with <see cref="OperationCanceledException"/> (owner pin,
/// TaskLock attachment check). That must skip only that role, whose next login reconciles it, instead of aborting
/// the pass for every later session in the same order.
/// </summary>
internal static class RoleFanout
{
    internal static async Task EachAsync(IEnumerable<GameSession> sessions, Func<GameSession, Task> work)
    {
        foreach (var session in sessions.ToArray())
        {
            try { await work(session); }
            catch (OperationCanceledException) { } // that role reconciles on its next login
        }
    }
}
