using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Ro3Emu.Panel;

/// <summary>
/// GM actions on one character (GMs and Admins, audited by the server): kick, teleport, rename, items and currencies,
/// and restoring a deleted character. The server runs each through its own path (the GM commands' item grants, its
/// teleport, the role store's name rule) and says what happened.
/// </summary>
public sealed partial class PanelSite
{
    internal sealed record CharacterActionForm(string? Reason, long Map, float? X, float? Z, string? Name, int Item, long Count);

    private IEnumerable<Route> CharacterActionRoutes() =>
    [
        Post("/api/admin/characters/{}/kick", Access.Gm, call => CharacterActionAsync(call, "kick")),
        Post("/api/admin/characters/{}/teleport", Access.Gm, call => CharacterActionAsync(call, "teleport")),
        Post("/api/admin/characters/{}/rename", Access.Gm, call => CharacterActionAsync(call, "rename")),
        Post("/api/admin/characters/{}/items", Access.Gm, call => CharacterActionAsync(call, "item")),
        Post("/api/admin/characters/{}/restore", Access.Gm, call => CharacterActionAsync(call, "restore")),
        Get("/api/admin/accounts/{}/deleted", Access.Moderator, DeletedCharactersAsync),
    ];

    private async Task<Reply> CharacterActionAsync(Call call, string kind)
    {
        if (!long.TryParse(call.Args[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            return Reply.Error(StatusCodes.Status404NotFound, "There is no such character.");
        var form = await BodyAsync<CharacterActionForm>(call) ?? new CharacterActionForm(null, 0, null, null, null, 0, 0);
        string reason = form.Reason?.Trim() ?? "";
        if (reason.Length > ReasonMax) return Bad("Give a reason of at most 300 characters.");
        if (ActionProblem(kind, form) is { } problem) return Bad(problem);
        var result = await _ops.CharacterActionAsync(call.Signed, id, new PanelCharacterAction(kind, reason, form.Map, form.X, form.Z,
            form.Name?.Trim(), form.Item, form.Count));
        return result.Done ? Reply.Ok(result) : Reply.Error(StatusCodes.Status409Conflict, result.Message);
    }

    /// <summary>The form's own checks; what depends on the game (map ids, item ids, the name rule) the server checks.</summary>
    private static string? ActionProblem(string kind, CharacterActionForm form) => kind switch
    {
        "teleport" when form.Map <= 0 => "Choose a map.",
        "teleport" when form.X is { } x && !float.IsFinite(x) || form.Z is { } z && !float.IsFinite(z) || (form.X == null) != (form.Z == null)
            => "Give both coordinates, or neither for the map's arrival point.",
        "rename" when string.IsNullOrWhiteSpace(form.Name) || form.Name.Trim().Length > NameMax || form.Name.Any(char.IsControl)
            => "Type the new name.",
        "item" when form.Item <= 0 || form.Count == 0 || Math.Abs(form.Count) > CountMax => "Choose an item and a count from 1 to 1,000,000,000 (negative removes).",
        _ => null,
    };

    private async Task<Reply> DeletedCharactersAsync(Call call) =>
        await _backend.AccountAsync(call.Args[0]) is { } account
            ? Reply.Ok(new { characters = await _ops.DeletedCharactersAsync(account.Name) })
            : Reply.Error(StatusCodes.Status404NotFound, "There is no such account.");
}
