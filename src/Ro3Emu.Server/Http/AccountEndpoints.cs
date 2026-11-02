using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Http;

/// <summary>The account's own tools on the HTTP port, public and guarded by the account's password and the credential budget.</summary>
public static class AccountEndpoints
{
    /// <summary>
    /// Character deletion for the launcher / website (the client itself has none): POST form identity, credential,
    /// role (id or name). A role that is in the world cannot be deleted.
    /// </summary>
    public static void Map(WebApplication app) =>
        app.MapPost("/ro3emu/account/delete-role", async (HttpRequest request, CredentialLimiter limiter, AccountStore accounts, RoleStore roles, SessionRegistry sessions, GameLoop loop, OwnerRegistry owners, ILogger<RoleStore> log) =>
        {
            if (!limiter.TryAcquire(request.HttpContext)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            if (!request.HasFormContentType) return Results.StatusCode(StatusCodes.Status400BadRequest);
            IFormCollection form;
            try { form = await request.ReadFormAsync(SdkEndpoints.LoginForm); }
            catch (InvalidDataException) { return Results.StatusCode(StatusCodes.Status400BadRequest); }
            string typed = form["identity"].ToString().Trim(), credential = form["credential"].ToString(), which = form["role"].ToString().Trim();
            if (credential.Length > SdkEndpoints.MaxCredential) return Results.Json(new { code = 401, message = "bad account or password" });
            // Roles are keyed by the account's stored name, whatever letter case the form used.
            if (await accounts.VerifiedAccountAsync(typed, credential) is not { } identity)
                return Results.Json(new { code = 401, message = "bad account or password" });
            var role = roles.ForAccount(identity).FirstOrDefault(r => r.Id.ToString() == which || string.Equals(r.Name, which, StringComparison.OrdinalIgnoreCase));
            if (role == null) return Results.Json(new { code = 404, message = "no such character on this account" });
            return await loop.InvokeAsync(async () =>
            {
                using var lifecycle = await owners.EnterAsync("HTTP delete", ReservationKey.Lifecycle(role.Id));
                var current = roles.Get(role.Id);
                if (current == null || current.Account != identity)
                    return Results.Json(new { code = 404, message = "no such character on this account" });
                if (RoleAttachment.IsAttached(owners, sessions, role.Id))
                    return Results.Json(new { code = 409, message = "the character is in the game; log out first" });
                bool ok = roles.Delete(identity, role.Id);
                if (ok) owners.RoleDeleted(role.Id, roles.ReadRevisionVector(role.Id).Epoch);
                string shown = Micro.MicroServiceRouter.Clip(identity);
                log.LogInformation("Account {Account} deletes role {Name} ({Id}): {Result}", shown, current.Name, role.Id, ok);
                return Results.Json(new { code = ok ? 0 : 500, message = ok ? $"{current.Name} deleted" : "not deleted" });
            });
        });
}
