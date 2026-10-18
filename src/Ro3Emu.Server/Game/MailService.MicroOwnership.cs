using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Server.Game;

public sealed partial class MailService
{
    /// <summary>Micro list/read/delete can purge or mutate mail. They use the same owner as game
    /// claim and offline delivery; a cached social projection never authorizes a mailbox write.</summary>
    public async ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args)
    {
        if (method is not ("getUserMailList" or "getMailContent" or "setMailHaveRead" or "deleteMail" or "syncGroupMail"))
            return null;
        if (loop?.Owners is not { } owners) return await InvokeMicroCoreAsync(session, method, args);
        return await loop.InvokeAsync(async () =>
        {
            var selected = sessions.All().FirstOrDefault(s => s.Account != null &&
                string.Equals(s.Account, session.Account, StringComparison.Ordinal));
            if (selected?.Role is not { } role) return MicroServiceRouter.Fail("no selected role");
            var attachment = selected.Attachment;
            using var scope = await owners.EnterAsync("micro/MailService", [ReservationKey.Role(role.Id)]);
            if (selected.Role?.Id != role.Id || !ReferenceEquals(selected.Attachment, attachment)
                || attachment?.Cancelled == true || !ReferenceEquals(selected, sessions.All().FirstOrDefault(s =>
                    string.Equals(s.Account, session.Account, StringComparison.Ordinal))))
                return MicroServiceRouter.Fail("no selected role");
            scope.Context.Pin(owners.Get(role.Id));
            return await InvokeMicroCoreAsync(session, method, args);
        });
    }

    /// <summary>One short read-worker snapshot contains mailbox bytes and their lifecycle/vector.
    /// The caller holds the recipient reservation through commit; no live session is required.</summary>
    private async Task<Mailbox> CaptureMailboxAsync(long role, RoleItemPlanBuilder transaction)
    {
        var original = await accounts.ReadAsync<RoleBlobSnapshot>(new RoleBlobSnapshotRead(role, "mailbox"));
        if (!original.Exists || original.Vector!.Deleted)
            throw new ArgumentException("Recipient role does not exist.", nameof(role));
        transaction.ExpectRole(original.Vector);
        transaction.Guard(new RoleBlobGuardPlan(role, "mailbox", original));
        return DecodeMailbox(original.Value);
    }
}
