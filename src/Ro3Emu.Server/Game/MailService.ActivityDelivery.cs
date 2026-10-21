using System.Globalization;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class MailService
{
    /// <summary>
    /// Prepare a gift under the complete sender/recipient owner reservation. Persist enlists the
    /// recipient's versioned mailbox snapshot in the sender's bag transaction (C/D 11, L2/D6).
    /// </summary>
    public async Task<ActivityDeliveryLease> PrepareActivityDeliveryAsync(long recipient, long sender, string senderName,
        long activityId, string message, int entitlementEntry)
    {
        if (data.Row("ItemConfig", entitlementEntry) == null)
            throw new ArgumentException("Recipient or entitlement item is not configured.");
        var mailboxLease = await EnterMailboxAsync(recipient);
        try
        {
            var original = await accounts.ReadAsync<RoleBlobSnapshot>(new RoleBlobSnapshotRead(recipient, "mailbox"));
            if (!original.Exists || original.Vector!.Deleted)
                throw new ArgumentException("Recipient role does not exist.", nameof(recipient));
            var box = DecodeMailbox(original.Value);
            string marker = $"Battle Pass gift for activity {activityId}";
            bool pending = box.Letters.Any(m => Active(m, Now) && m.Abstract == marker
                && !m.Claimed && m.Attachments.Any(a => a.Entry == entitlementEntry));
            if (pending) return new ActivityDeliveryLease(this, recipient, mailboxLease, null, original, true);
            var letter = new Letter
            {
                Category = 1,
                Title = "Battle Pass Gift", Abstract = marker, Content = message,
                Sender = senderName, SenderId = sender.ToString(CultureInfo.InvariantCulture),
                Attachments = [new Attachment(entitlementEntry, 1)],
            };
            AddLetter(box, letter, Now, null, 30);
            return new ActivityDeliveryLease(this, recipient, mailboxLease, box, original, false);
        }
        catch { mailboxLease.Dispose(); throw; }
    }

    public sealed class ActivityDeliveryLease : IDisposable
    {
        private readonly MailService _owner;
        private readonly long _recipient;
        private readonly IDisposable _mailboxLease;
        private readonly Mailbox? _mailbox;
        private readonly RoleBlobSnapshot _original;
        private readonly bool _pending;
        internal ActivityDeliveryLease(MailService owner, long recipient, IDisposable mailboxLease, Mailbox? mailbox,
            RoleBlobSnapshot original, bool pending)
        {
            this._owner = owner; this._recipient = recipient; this._mailboxLease = mailboxLease; this._mailbox = mailbox;
            this._original = original; this._pending = pending;
        }
        public bool Pending => _pending;
        public void Persist(Ro3Emu.Server.Accounts.Persistence.RoleItemPlanBuilder builder)
        {
            if (_mailbox == null) throw new InvalidOperationException("No prepared gift.");
            builder.Guard(new RoleBlobGuardPlan(_recipient, "mailbox", _original));
            _owner.Purge(_mailbox, _owner.Now, builder, _recipient);
            builder.RoleBlob(_recipient, "mailbox", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_mailbox));
        }

        /// <summary>Call only after Persist's transaction committed.</summary>
        public async Task NotifyAsync()
        {
            if (_mailbox == null) return;
            await _owner.NotifyAsync(_recipient, 1);
        }

        public void Dispose() => _mailboxLease.Dispose();
    }

    public sealed record ActivityTemplateDelivery(int TemplateId, IReadOnlyList<Attachment> Attachments);

    /// <summary>
    /// Settle multiple configured activity letters and the caller's once-only marker in one SQLite transaction.
    /// The activity caller already owns the role and ActivityGate. Publish its settled marker before notices.
    /// </summary>
    public async Task DeliverActivityBatchAsync(GameSession session,
        IReadOnlyList<ActivityTemplateDelivery> deliveries, Action<RoleItemPlanBuilder> persist, Action? committed = null)
    {
        long role = session.Role?.Id ?? throw new InvalidOperationException("Activity mail needs a selected role.");
        if (loop?.Owners is { } owners)
            await loop.InvokeAsync(() => owners.RunSessionAsync("activity mail",
                [Ownership.ReservationKey.Role(role)], session,
                () => DeliverActivityBatchCoreAsync(session, deliveries, persist, committed)));
        else await DeliverActivityBatchCoreAsync(session, deliveries, persist, committed);
        // Delivery and source Apply are complete. A failed notice must not permit another settlement.
        if (deliveries.Count > 0)
        {
            try { await NotifyAsync(role, 1); }
            catch (Exception e) { log.LogWarning(e, "Activity mail notice failed for role {Role}", role); }
        }
    }

    private async Task DeliverActivityBatchCoreAsync(GameSession session,
        IReadOnlyList<ActivityTemplateDelivery> deliveries, Action<RoleItemPlanBuilder> persist, Action? committed)
    {
        long role = session.Role?.Id ?? throw new InvalidOperationException("Activity mail needs a selected role.");
        using (await CharacterService.Lock(session))
        {
            using (await EnterMailboxAsync(role))
            {
                var transaction = accounts.NewTransaction();
                var box = await CaptureMailboxAsync(role, transaction);
                long now = Now;
                Purge(box, now, transaction, role);
                foreach (var delivery in deliveries)
                {
                    if (data.Row("MailConfig", delivery.TemplateId) is not { } template
                        || GameData.Int(template, "_iTimerMethod") != 0)
                        throw new InvalidOperationException($"Unsupported activity mail template {delivery.TemplateId}.");
                    if (!ConfiguredAttachments(delivery.Attachments))
                        throw new InvalidOperationException("Unsupported activity mail attachment.");
                    int days = checked((int)GameData.Int(template, "_kTimes"));
                    AddLetter(box, new Letter
                    {
                        TemplateId = delivery.TemplateId,
                        SenderId = "0",
                        Category = 1,
                        Attachments = delivery.Attachments.ToList(),
                    }, now, null, days > 0 ? days : 30);
                }
                transaction.RoleBlob(role, "mailbox", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(box));
                persist(transaction);
                await roles.CommitAsync(transaction, "mail/activity-batch", _ => committed?.Invoke());
            }
        }
    }
}
