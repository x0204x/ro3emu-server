using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>State shortcuts used only by tests that replace a full fight with one real wire attack.</summary>
internal static class CombatTestFixture
{
    /// <summary>
    /// Artificial one-hit fixtures immediately change targets after a lethal hit. End the previous auto-attack before
    /// the fixture selects its next target.
    /// Must be called on the game loop together with the fixture's controlled HP setup.
    /// </summary>
    public static void ReadyNormalAttack(GameLoop loop, GameSession session)
    {
        if (!loop.IsOnLoop) throw new InvalidOperationException("Combat fixture state must be changed on the game loop");
        session.Attack = null;
    }
}
