using System.Text.Json;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class MailService
{
    /// <summary>Deliver one season's resolved rank awards with its page rollover and economic source.
    /// The caller holds the whole-role owner and character lock across this commit.</summary>
    internal async Task<string> SendSeasonReissueAsync(GameSession session, int templateId,
        IReadOnlyList<Attachment> attachments, string sourceKey, Action<RoleItemPlanBuilder> persist,
        Action committed)
    {
        long role = session.Role?.Id ?? throw new InvalidOperationException("Season reissue needs a selected role.");
        if (string.IsNullOrWhiteSpace(sourceKey) || data.Row("MailConfig", templateId) is not { } template
            || GameData.Int(template, "_iTimerMethod") != 0 || !ConfiguredAttachments(attachments)
            || attachments.Any(a => a.Bind != 0))
            throw new AwardResolutionException("Unsupported season reissue mail or attachment.");
        using var mailbox = await EnterMailboxAsync(role);
        var transaction = accounts.NewTransaction();
        var box = await CaptureMailboxAsync(role, transaction);
        // Previous builds used a mailbox-local key without the role ID. Keep it as a read-only alias
        // so upgrading an old save cannot create a second letter for that epoch.
        string legacyKey = "season-rank-reissue:" + sourceKey[(sourceKey.LastIndexOf(':') + 1)..];
        if (box.Letters.FirstOrDefault(m => m.DeliveryKey == sourceKey || m.DeliveryKey == legacyKey) is { } existing)
        {
            // The old season was already minted. Repair a restored old page/meta without creating mail.
            persist(transaction);
            await roles.CommitAsync(transaction, "season-page/reissue-existing", _ => committed());
            return existing.Id;
        }
        int days = (int)GameData.Int(template, "_kTimes");
        string id = AddLetter(box, new Letter
        {
            TemplateId = templateId, SenderId = "0", DeliveryKey = sourceKey,
            Attachments = [.. attachments],
        }, Now, null, days > 0 ? days : 30);
        transaction.ItemDelta(role, session.Items, session.Items);
        transaction.RoleBlob(role, "mailbox", JsonSerializer.SerializeToUtf8Bytes(box));
        persist(transaction);
        transaction.WithEconomy(character.ProgressionEconomy(session, "cd13c/season-reissue", [],
            [.. attachments.Select(a => (a.Entry, a.Count))],
            new { Season = session.SeasonState!.SeasonId, Source = sourceKey, MailId = id }, sourceKey));
        await roles.CommitAsync(transaction, "season-page/reissue-mail", _ => committed());
        try { await NotifyAsync(role, 1); }
        catch (Exception e) { log.LogWarning(e, "Season reissue notice failed for role {Role}", role); }
        return id;
    }
}
