namespace Ro3Emu.Panel;

/// <summary>
/// The admin area's tools (docs/guide/panel.md): in-game mail, gift codes, the GM console, the news editor and
/// broadcasts. Like <see cref="IPanelBackend"/>, every one goes through the running server, and every change it makes
/// is written to the audit log.
/// </summary>
public interface IPanelAdminTools
{
    /// <summary>Items whose id or English name matches <paramref name="query"/>, best first (mail and gift code rewards).</summary>
    IReadOnlyList<PanelItemChoice> FindItems(string query, int limit);

    /// <summary>The recipients a mail to <paramref name="target"/> would reach, and a few of their names.</summary>
    Task<PanelMailRecipients> MailRecipientsAsync(PanelMailTarget target);

    /// <summary>解析這次送出的固定名單；確認人數與逐封發送必須使用同一份快照。</summary>
    Task<PanelMailAudience> ResolveMailAudienceAsync(PanelMailTarget target);

    /// <summary>只寄給已確認快照內的收件人，沿既有郵件 owner 發送並記錄。</summary>
    Task<PanelMailResult> SendMailAsync(string admin, PanelMailAudience audience, PanelMail letter);

    Task<IReadOnlyList<PanelGiftCode>> GiftCodesAsync();

    /// <summary>Creates the codes, all or none; the codes already taken when it refused.</summary>
    Task<IReadOnlyList<string>> CreateGiftCodesAsync(string admin, IReadOnlyList<string> codes, PanelGiftCodeRules rules);

    /// <summary>False when there is no such code.</summary>
    Task<bool> SetGiftCodeDisabledAsync(string admin, string code, bool disabled);

    /// <summary>Runs a GM chat command for <paramref name="character"/> (online), or for the admin's own character when null.</summary>
    Task<PanelGmResult> RunGmAsync(string admin, string? character, string line);

    /// <summary>The admin's GM commands, newest first (from the audit log).</summary>
    Task<PanelAuditPage> GmHistoryAsync(string admin, int page, int pageSize);

    /// <summary>Every news row, scheduled ones included, pinned first.</summary>
    Task<IReadOnlyList<PanelNewsItem>> AllNewsAsync();

    /// <summary>Saves a news row (<see cref="PanelNewsDraft.Id"/> 0 creates one); its id, or 0 when the row to update is gone.</summary>
    Task<long> SaveNewsAsync(string admin, PanelNewsDraft draft);

    Task<bool> DeleteNewsAsync(string admin, long id);

    /// <summary>Shows <paramref name="text"/> on every online player's screen now, and again every
    /// <paramref name="everyMinutes"/> minutes <paramref name="repeats"/> more times. How many saw the first one.</summary>
    Task<int> BroadcastAsync(string admin, string text, int everyMinutes, int repeats);

    /// <summary>Broadcasts that still repeat.</summary>
    IReadOnlyList<PanelBroadcast> Broadcasts();

    Task<bool> CancelBroadcastAsync(string admin, long id);
}

/// <summary>A request the server refused for a reason the admin can fix (an unknown item, a character not online);
/// the panel answers 400 with its message.</summary>
public sealed class PanelRefusalException(string message) : Exception(message);

public sealed record PanelItemChoice(long Id, string Name, string Icon = "");

/// <param name="Kind">character (one, by name), account (all of an account's characters) or everyone.</param>
/// <param name="MinLevel">Everyone: only characters at this base level or above.</param>
public sealed record PanelMailTarget(string Kind, string Name, bool OnlineOnly, int MinLevel);

public sealed record PanelMailRecipients(int Count, IReadOnlyList<string> Sample);

public sealed record PanelMailRecipient(long Id, string Name);

/// <summary>伺服器解析的收件人快照；複製並唯讀封裝，避免確認後因原清單變動而多寄。</summary>
public sealed class PanelMailAudience(PanelMailTarget target, IEnumerable<PanelMailRecipient> recipients)
{
    public PanelMailTarget Target { get; } = target;
    public IReadOnlyList<PanelMailRecipient> Recipients { get; } = Array.AsReadOnly(recipients.ToArray());
    public int Count => Recipients.Count;
}

public sealed record PanelMailAttachment(int Item, int Count);

public sealed record PanelMail(string Title, string Text, string Sender, IReadOnlyList<PanelMailAttachment> Attachments);

/// <param name="Failed">Recipients the server could not deliver to (deleted meanwhile, or an error the log names).</param>
public sealed record PanelMailResult(int Delivered, int Failed);

public sealed record PanelGiftCodeRules(IReadOnlyList<PanelMailAttachment> Rewards, int MaxUses, int PerAccount,
    DateTimeOffset? Starts, DateTimeOffset? Expires, string Note);

public sealed record PanelGiftCode(string Code, IReadOnlyList<PanelMailAttachment> Rewards, string RewardText, long MaxUses,
    long PerAccount, DateTimeOffset? Starts, DateTimeOffset? Expires, bool Disabled, string? CreatedBy, DateTimeOffset Created,
    string? Note, long Uses, string State);

/// <param name="Ran">False when no online character could run it (<see cref="Replies"/> then says why).</param>
public sealed record PanelGmResult(bool Ran, string Character, IReadOnlyList<string> Replies);

public sealed record PanelNewsDraft(long Id, string Title, string Body, string Tag, DateTimeOffset Published, bool Pinned);

public sealed record PanelBroadcast(long Id, string Text, string Admin, int EveryMinutes, int Remaining, DateTimeOffset Next);
