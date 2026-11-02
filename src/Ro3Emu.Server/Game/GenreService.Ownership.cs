using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

public sealed partial class GenreService
{
    private static ReservationKey[] Keys(long role, long other, bool codes) =>
        [.. new[] { ReservationKey.Role(role), ReservationKey.Entity("genre-seeker", role.ToString()) }
            .Concat(other > 0 ? [ReservationKey.Role(other), ReservationKey.Entity("genre-seeker", other.ToString())] : [])
            .Concat(codes ? [ReservationKey.Entity("genre-codes", "registry")] : []).Distinct()];

    /// <summary>13b: acquire the seeker, responder/donor snapshot and code index together. No gate callback
    /// may carry a detached whole-blob snapshot across another owner's reservation.</summary>
    internal async Task RunOwnedAsync(GameSession session, Func<Task> command, long other = 0, bool codes = false)
    {
        if (session.Owners is not { } owners) { await command(); return; }
        using var scope = await owners.EnterAsync("genre/command", Keys(session.Role!.Id, other, codes));
        PinParticipants(session, other, scope.Context);
        await command();
    }

    private void PinParticipants(GameSession session, long other, OwnerContext context)
    {
        context.AssertRole(session.Role!.Id);
        context.AssertRequest();
        if (session.Owner is { } owner) { context.Pin(owner); context.BindPresentation(session); }
        if (other <= 0 || other == session.Role.Id || roles.Get(other) == null) return;
        context.Pin(session.Owners!.Get(other));
        if (Online(other) is { Attachment: not null } target) context.BindPresentation(target);
    }

    internal async Task RespondCodeRequestAsync(GameSession session, GenreResponseCodeReq request)
    {
        if (session.Owners is not { } owners) { await RespondCodeAsync(session, request); return; }
        while (true)
        {
            long other = roles.FindGenreCode(request.Code, Now()) ?? 0;
            using var scope = await owners.EnterAsync("genre/code-response", Keys(session.Role!.Id, other, codes: true));
            // A different role may have acquired/replaced the code while the complete set was waiting.
            // Dispose and restage; never expand a held set to the newly discovered role.
            if ((roles.FindGenreCode(request.Code, Now()) ?? 0) != other) continue;
            PinParticipants(session, other, scope.Context);
            await RespondCodeAsync(session, request);
            return;
        }
    }

    private async Task SaveGenreAsync(GameSession session, DBGenre next)
    {
        var bytes = next.ToByteArray();
        if (roles.BlobUnchanged(session.Role!.Id, "genre", bytes)) { session.Genre = next; return; } // a login's normalization
        var tx = roles.NewTransaction();
        tx.RoleBlob(session.Role.Id, "genre", bytes);
        await roles.CommitAsync(tx, "cd13b/genre-state", _ => session.Genre = next);
    }

    internal Task MyRecommendationsAsync(GameSession session) => session.SendGameAsync(82808,
        new GenreGetAllMyRecommendResp { MyRecommend = session.Genre!.MyRecommend?.Clone() ?? new() });

    internal async Task QueryRecommendationsAsync(GameSession session)
    {
        await session.SendGameAsync(82828, new GenreQueryAllOtherRecommendResp());
        await SyncAsync(session);
    }
}
