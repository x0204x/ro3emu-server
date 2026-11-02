using System.Globalization;
using System.Runtime.CompilerServices;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Http;

/// <summary>
/// GM actions on one character. A character in the game is changed through its live session on the loop, by the GM
/// commands' own code (!warp, !item, !delitem: the grant paths that sync the bag to the client at once) or the
/// reconnect service's kick. A character out of the game is changed under its lifecycle reservation, refused if it is
/// attached meanwhile, as the HTTP character deletion does. Every action is audited.
/// </summary>
public sealed partial class PanelOperations
{
    /// <summary>The game's name rule for a character (RoleSessionService.CreateRoleAsync: trimmed, 2 to 16 characters).</summary>
    private const int NameMin = 2, NameMax = 16;
    /// <summary>Zeny (ItemConfig 1, the currency !zeny moves).</summary>
    private const int Zeny = 1;

    public Task<PanelActionResult> CharacterActionAsync(string admin, long character, PanelCharacterAction action) => action.Kind switch
    {
        "kick" => KickAsync(admin, character, action.Reason ?? ""),
        "teleport" => TeleportAsync(admin, character, action),
        "rename" => RenameAsync(admin, character, action.Name ?? ""),
        "item" => ItemAsync(admin, character, action.Item, action.Count),
        "restore" => RestoreAsync(admin, character),
        _ => Task.FromResult(new PanelActionResult(false, "Unknown action.")),
    };

    private static PanelActionResult Refused(string message) => new(false, message);

    private static string Target(long character, string? name) => AdminAudit.Character(name, character);

    /// <summary>The character's session in the game, on the loop.</summary>
    private GameSession? Online(long character) => sessions.All().FirstOrDefault(s => s.Role?.Id == character);

    /// <summary>Runs a GM line for the character's live session as the panel's GM console does; null when it is not in
    /// the game, else the replies. A <paramref name="refuse"/> that answers (on the loop, before the line runs) stops it:
    /// its text is the only reply, and <paramref name="refused"/> is set.</summary>
    private async Task<List<string>?> RunLineAsync(long character, string line, Func<GameSession, string?>? refuse = null,
        StrongBox<bool>? refused = null)
    {
        try
        {
            return await loop.InvokeAsync(async () =>
            {
                if (Online(character) is not { Role: not null } session) return null;
                if (refuse?.Invoke(session) is { } no)
                {
                    if (refused != null) refused.Value = true;
                    return [no];
                }
                List<string> replies = [];
                session.GmReplies = replies;
                try { await gm.RunForPanelAdminAsync(session, line); }
                finally { session.GmReplies = null; }
                return replies;
            });
        }
        catch (OperationCanceledException) { return null; }
    }

    private async Task<PanelActionResult> KickAsync(string admin, long character, string reason)
    {
        var role = await roles.GetAsync(character);
        if (role == null) return Refused("There is no such character.");
        bool kicked;
        try
        {
            kicked = await loop.InvokeAsync(() =>
            {
                var session = Online(character);
                if (session != null) reconnect.KickNow(session);
                return Task.FromResult(session != null);
            });
        }
        catch (OperationCanceledException) { return Refused("The server is stopping."); }
        if (!kicked) return Refused(role.Name + " is not in the game.");
        await AuditAsync(admin, AdminAudit.CharacterKick, Target(character, role.Name), reason.Length > 0 ? reason : "kicked");
        return new PanelActionResult(true, role.Name + " was disconnected.");
    }

    private async Task<PanelActionResult> TeleportAsync(string admin, long character, PanelCharacterAction action)
    {
        var role = await roles.GetAsync(character);
        if (role == null) return Refused("There is no such character.");
        if (world.Map(action.Map) is not { } map) return Refused("There is no map " + action.Map.ToString(CultureInfo.InvariantCulture) + ".");
        string where = data.MapName(action.Map) is { Length: > 0 } mapName ? mapName : "map " + action.Map.ToString(CultureInfo.InvariantCulture);
        if (!world.ClientHasScene(action.Map))
            return Refused("The game client has no scene for " + where + ": a character sent there would never finish loading.");
        string point = action.X is { } x && action.Z is { } z ? string.Create(CultureInfo.InvariantCulture, $" {x:0.##} {z:0.##}") : "";
        if (await RunLineAsync(character, "!warp " + action.Map.ToString(CultureInfo.InvariantCulture) + point) is { } replies)
        {
            if (replies.Any(r => r.StartsWith("Usage", StringComparison.Ordinal) || r.StartsWith(GmCommands.WarpRefused, StringComparison.Ordinal)))
                return Refused("The server could not move " + role.Name + " there.");
            await AuditAsync(admin, AdminAudit.CharacterTeleport, Target(character, role.Name), where + point + " (in the game)");
            return new PanelActionResult(true, role.Name + " was moved to " + where + ".");
        }
        var at = action.X is { } px && action.Z is { } pz ? map.Grounded(new Maps.MapPoint(px, role.Y, pz, role.Face)) : map.Born is { } born ? map.Grounded(born) : (Maps.MapPoint?)null;
        if (at is not { } target) return Refused(where + " has no arrival point; give coordinates.");
        var result = await OfflineAsync(character, "Panel teleport", () =>
        {
            roles.SavePosition(character, action.Map, target.X, target.Y, target.Z, target.Face);
            return PanelRoleResult.Done;
        });
        if (result != null) return result;
        await AuditAsync(admin, AdminAudit.CharacterTeleport, Target(character, role.Name), where + point + " (offline)");
        return new PanelActionResult(true, role.Name + " will log in at " + where + ".");
    }

    /// <summary>
    /// Runs <paramref name="change"/> on the loop under the character's lifecycle reservation, refused while the
    /// character is attached (in the game, loading, or waiting for a reconnect). Null when it was done.
    /// </summary>
    private async Task<PanelActionResult?> OfflineAsync(long character, string origin, Func<PanelRoleResult> change,
        string taken = "That name is taken.")
    {
        try
        {
            return await loop.InvokeAsync(async () =>
            {
                using var lifecycle = await owners.EnterAsync(origin, ReservationKey.Lifecycle(character));
                if (RoleAttachment.IsAttached(owners, sessions, character)) return Refused("The character is in the game; kick it first or try again when it has logged out.");
                return change() switch
                {
                    PanelRoleResult.Done => null,
                    PanelRoleResult.NameTaken => Refused(taken),
                    _ => Refused("There is no such character."),
                };
            });
        }
        catch (OperationCanceledException) { return Refused("The server is stopping."); }
        catch (OwnerRevisionConflictException) { return Refused("The character changed meanwhile. Try again."); }
    }

    private async Task<PanelActionResult> RenameAsync(string admin, long character, string name)
    {
        name = name.Trim();
        if (name.Length is < NameMin or > NameMax || name.Any(char.IsControl) || name.Contains('~'))
            return Refused("A character name has 2 to 16 characters.");
        var role = await roles.GetAsync(character);
        if (role == null) return Refused("There is no such character.");
        if (string.Equals(role.Name, name, StringComparison.Ordinal)) return Refused("That is already its name.");
        var result = await OfflineAsync(character, "Panel rename", () =>
            accounts.WriteLegacySync<PanelRoleResult>(new PanelRoleRenamePlan(character, name, admin, Now)));
        return result ?? new PanelActionResult(true, role.Name + " is now called " + name + ". The old name is free.");
    }

    /// <summary>Items and currencies for a character in the game, through !item (the GM grant) and !delitem.</summary>
    private async Task<PanelActionResult> ItemAsync(string admin, long character, int item, long count)
    {
        var role = await roles.GetAsync(character);
        if (role == null) return Refused("There is no such character.");
        if (ItemConfig.Get(data, item) == null) return Refused("Item " + item.ToString(CultureInfo.InvariantCulture) + " is not in the item table.");
        long wanted = Math.Min(Math.Abs(count), int.MaxValue);
        string amount = wanted.ToString(CultureInfo.InvariantCulture);
        string line = (count > 0 ? "!item " : "!delitem ") + item.ToString(CultureInfo.InvariantCulture) + " " + amount;
        string name = item == Zeny ? "Zeny" : data.ItemName(item);
        // !delitem takes what there is (a currency down to 0): the panel takes all of it or nothing, checked on the
        // loop right before the line runs, so nothing is spent in between.
        var refused = new StrongBox<bool>();
        Func<GameSession, string?>? enough = count > 0 ? null : session => characters.CountOf(session, item) is var held && held < wanted
            ? role.Name + " holds only " + held.ToString("N0", CultureInfo.InvariantCulture) + " " + name + ", so nothing was taken."
            : null;
        if (await RunLineAsync(character, line, enough, refused) is not { } replies)
            return Refused(role.Name + " is not in the game. Send items by mail (Mail & rewards) instead; they arrive at the next login.");
        string reply = replies.Count > 0 ? replies[0] : "";
        if (refused.Value) return Refused(reply);
        if (reply.StartsWith("Usage", StringComparison.Ordinal)) return Refused("The server refused it: " + reply);
        string what = name + " (" + item.ToString(CultureInfo.InvariantCulture) + ")";
        await AuditAsync(admin, AdminAudit.CharacterItem, Target(character, role.Name),
            (count > 0 ? "+" : "-") + amount + " " + what + (reply.Length > 0 ? " => " + reply : ""));
        return new PanelActionResult(true, reply.Length > 0 ? reply : "Done.");
    }

    private async Task<PanelActionResult> RestoreAsync(string admin, long character)
    {
        var result = await OfflineAsync(character, "Panel restore", () =>
        {
            var restored = accounts.WriteLegacySync<PanelRoleResult>(new PanelRoleRestorePlan(character, admin, Now));
            if (restored == PanelRoleResult.Done) owners.RoleRestored(character);
            return restored;
        }, "Its old name belongs to another character now, so it cannot come back under it.");
        if (result != null) return result;
        var role = await roles.GetAsync(character);
        return new PanelActionResult(true, (role?.Name ?? "The character") + " is back on its account.");
    }

    public async Task<IReadOnlyList<PanelDeletedCharacter>> DeletedCharactersAsync(string account) =>
        [.. (await accounts.ReadAsync<List<DeletedRoleRow>>(new DeletedRolesRead(account))).Select(r => new PanelDeletedCharacter(
            r.Id, r.Name, ClassName(r.Career), r.Level, DateTimeOffset.FromUnixTimeSeconds(r.DeletedTime), r.NameFree))];

    /// <summary>The class's English name (JobConfig._iName), as the panel's other pages show it.</summary>
    private string ClassName(long career) =>
        JobConfig.Get(data, career)?.Name is [var text, ..] && data.Text(text) is { Length: > 0 } name
            ? name : "Class " + career.ToString(CultureInfo.InvariantCulture);
}
