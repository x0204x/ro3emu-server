namespace Ro3Emu.Server.Game.Ownership;

/// <summary>
/// Whether a character is attached to the game, so an offline change (deleting it on the launcher's page, a rename or
/// a move on the panel) must wait: its owner is not detached (a deleted owner counts as gone), it is attaching or
/// detaching, or a session holds it. Call it on the loop under the character's lifecycle reservation.
/// </summary>
public static class RoleAttachment
{
    public static bool IsAttached(OwnerRegistry owners, SessionRegistry sessions, long roleId)
    {
        var owner = owners.Cached(roleId);
        return owner is { State: not RoleOwnerState.Detached and not RoleOwnerState.Deleted } || owner?.Attachment != null
            || owner?.Detaching != null || sessions.All().Any(s => s.Role?.Id == roleId || s.Owner?.RoleId == roleId
                || s.Attachment?.RoleId == roleId);
    }
}
