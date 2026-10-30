using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Ro3Emu.Panel;

/// <summary>
/// The character sheet (a player sees the characters of their own account, an admin every character) and the mailbox
/// viewer (admins). Both read only: nothing here changes a character.
/// </summary>
public sealed partial class PanelSite
{
    private const int MailPageSize = 10;

    private IEnumerable<Route> CharacterRoutes() =>
    [
        Get("/api/characters/{}", Access.Player, CharacterAsync),
        Get("/api/admin/characters/{}/mail", Access.Admin, CharacterMailAsync),
    ];

    /// <summary>Someone else's character is "not found" to a player, the same answer as a missing one.</summary>
    private async Task<Reply> CharacterAsync(Call call)
    {
        if (!long.TryParse(call.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id) || await _backend.CharacterAsync(id) is not { } sheet
            || !call.Admin && !string.Equals(sheet.Account, call.Signed, StringComparison.Ordinal))
            return Reply.Error(StatusCodes.Status404NotFound, "There is no such character.");
        return Reply.Ok(new { sheet, admin = call.Admin, account = call.Admin ? sheet.Account : null });
    }

    private async Task<Reply> CharacterMailAsync(Call call) =>
        long.TryParse(call.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id)
        && await _backend.MailAsync(id, call.Page(), MailPageSize) is { } page
            ? Reply.Ok(page) : Reply.Error(StatusCodes.Status404NotFound, "There is no such character.");
}
