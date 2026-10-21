namespace Ro3Emu.Server.Game.Messaging;

/// <summary>
/// How far a game connection has come: connected (the gate key is exchanged, no account signed in yet), on the role
/// screen (the account is signed in, no role selected) or in the world with a role. A message is refused below the
/// stage it is declared for (<see cref="MessageRoutes.AllowFrom"/>); a message declared for no stage needs a role.
/// </summary>
public enum SessionStage { Connected, RoleScreen, World }

public static class SessionStages
{
    public static SessionStage Of(GameSession session) =>
        session.Account == null ? SessionStage.Connected
        : session.Role == null ? SessionStage.RoleScreen
        : SessionStage.World;
}
