using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Ro3Emu.Panel;

/// <summary>
/// The admin tools' API (admins only, audited by the server): mail, gift codes, the GM console, news and broadcasts.
/// The limits on lengths and counts below are the panel's own (inferred), sized for the client's text fields.
/// </summary>
public sealed partial class PanelSite
{
    private const int TitleMax = 60, TextMax = 1000, SenderMax = 24, AttachmentsMax = 10, CountMax = 1_000_000_000;
    private const int GmLineMax = 500, NewsTitleMax = 120, NewsBodyMax = 10_000, BroadcastMax = 200, BatchMax = 500;
    private static readonly string[] NewsTags = ["news", "event", "maintenance", "patch"];
    private static readonly string[] MailKinds = ["character", "account", "everyone"];
    /// <summary>Generated codes: no 0/O, 1/I/L, so a code read aloud or from a screenshot is typed right.</summary>
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    internal sealed record TargetForm(string? Kind, string? Name, bool OnlineOnly, int MinLevel);
    internal sealed record MailForm(TargetForm? Target, string? Title, string? Text, string? Sender,
        List<PanelMailAttachment>? Attachments, bool Confirm, int Expected);
    internal sealed record GiftCodeForm(string? Codes, int GenerateCount, string? Prefix, List<PanelMailAttachment>? Rewards,
        int MaxUses, int PerAccount, DateTimeOffset? Starts, DateTimeOffset? Expires, string? Note);
    internal sealed record GmForm(string? Character, string? Line);
    internal sealed record NewsForm(long Id, string? Title, string? Body, string? Tag, DateTimeOffset? Published, bool Pinned);
    internal sealed record BroadcastForm(string? Text, int EveryMinutes, int Repeats);

    [GeneratedRegex("^[A-Za-z0-9_-]{3,32}$")]
    private static partial Regex CodePattern();

    private IEnumerable<Route> ToolRoutes() =>
    [
        Get("/api/admin/items", Access.Admin, call => Task.FromResult(Reply.Ok(new { items = _tools.FindItems(call.Query("q").Trim(), 20) }))),
        Post("/api/admin/mail/recipients", Access.Admin, MailRecipientsAsync),
        Post("/api/admin/mail", Access.Admin, SendMailAsync),
        Get("/api/admin/gift-codes", Access.Admin, async _ => Reply.Ok(new { codes = await _tools.GiftCodesAsync() })),
        Post("/api/admin/gift-codes", Access.Admin, CreateGiftCodesAsync),
        Post("/api/admin/gift-codes/{}/disable", Access.Admin, call => SetGiftCodeAsync(call, disabled: true)),
        Post("/api/admin/gift-codes/{}/enable", Access.Admin, call => SetGiftCodeAsync(call, disabled: false)),
        Post("/api/admin/gm", Access.Admin, RunGmAsync),
        Get("/api/admin/gm/history", Access.Admin, async call => Reply.Ok(await _tools.GmHistoryAsync(call.Signed, call.Page(), PageSize))),
        Get("/api/admin/news", Access.Admin, async _ => Reply.Ok(new { news = await _tools.AllNewsAsync() })),
        Post("/api/admin/news", Access.Admin, SaveNewsAsync),
        Post("/api/admin/news/{}/delete", Access.Admin, DeleteNewsAsync),
        Get("/api/admin/broadcasts", Access.Admin, _ => Task.FromResult(Reply.Ok(new { broadcasts = _tools.Broadcasts() }))),
        Post("/api/admin/broadcasts", Access.Admin, BroadcastAsync),
        Post("/api/admin/broadcasts/{}/cancel", Access.Admin, CancelBroadcastAsync),
    ];

    private static Reply Bad(string message) => Reply.Error(StatusCodes.Status400BadRequest, message);

    // ---------------------------------------------------------------- mail

    private static PanelMailTarget? Target(TargetForm? form) =>
        form?.Kind is { } kind && MailKinds.Contains(kind) && (kind == "everyone" || form.Name?.Trim() is { Length: > 0 and <= NameMax })
            ? new PanelMailTarget(kind, form.Name?.Trim() ?? "", form.OnlineOnly, Math.Clamp(form.MinLevel, 0, 9999)) : null;

    private async Task<Reply> MailRecipientsAsync(Call call) =>
        Target((await BodyAsync<MailForm>(call))?.Target) is { } target
            ? Reply.Ok(await _tools.MailRecipientsAsync(target)) : Bad("Choose who gets the mail.");

    private async Task<Reply> SendMailAsync(Call call)
    {
        var form = await BodyAsync<MailForm>(call);
        if (form == null || Target(form.Target) is not { } target) return Bad("Choose who gets the mail.");
        string title = form.Title?.Trim() ?? "", text = form.Text?.Trim() ?? "", sender = form.Sender?.Trim() is { Length: > 0 } s ? s : "GM";
        if (title.Length is 0 or > TitleMax) return Bad("Give a title of at most 60 characters.");
        if (text.Length > TextMax || sender.Length > SenderMax) return Bad("The text has at most 1000 characters, the sender 24.");
        if (Rewards(form.Attachments, required: false) is not { } attachments) return Bad("Up to 10 attachments, each a count from 1 to 1,000,000,000.");
        var recipients = await _tools.ResolveMailAudienceAsync(target);
        if (recipients.Count == 0) return Bad("Nobody matches: no mail was sent.");
        // 確認與寄送共用同一份收件人快照；驗證後不得因新角色或在線狀態變動而擴大名單。
        if (target.Kind != "character" && (!form.Confirm || form.Expected != recipients.Count))
            return Reply.Error(StatusCodes.Status409Conflict, $"This mail goes to {recipients.Count} characters. Confirm to send it.");
        var result = await _tools.SendMailAsync(call.Signed, recipients, new PanelMail(title, text, sender, attachments));
        return Reply.Ok(result);
    }

    /// <summary>The reward list, or null when it is too long or a count is out of range (item ids the server checks).</summary>
    private static List<PanelMailAttachment>? Rewards(List<PanelMailAttachment>? rewards, bool required)
    {
        rewards ??= [];
        if (rewards.Count > AttachmentsMax || required && rewards.Count == 0) return null;
        return rewards.All(r => r.Item > 0 && r.Count is > 0 and <= CountMax) ? rewards : null;
    }

    // ---------------------------------------------------------------- gift codes

    private async Task<Reply> CreateGiftCodesAsync(Call call)
    {
        var form = await BodyAsync<GiftCodeForm>(call);
        if (form == null) return Bad("Fill in the form.");
        if (Rewards(form.Rewards, required: true) is not { } rewards) return Bad("Give 1 to 10 rewards, each a count from 1 to 1,000,000,000.");
        if (form.MaxUses < 0 || form.PerAccount < 1) return Bad("Max uses is 0 (no limit) or more, uses per account 1 or more.");
        if (form.Starts is { } starts && form.Expires is { } expires && expires <= starts) return Bad("The code must expire after it starts.");
        string note = form.Note?.Trim() ?? "";
        if (note.Length > TitleMax) return Bad("The note has at most 60 characters.");
        List<string> codes;
        if (form.GenerateCount > 0)
        {
            string prefix = form.Prefix?.Trim().ToUpperInvariant() ?? "";
            if (form.GenerateCount > BatchMax || prefix.Length > 12 || !CodePattern().IsMatch(prefix + "XXXXXXXXXX"))
                return Bad("Generate 1 to 500 codes; a prefix has at most 12 letters, digits, - or _.");
            codes = [.. Enumerable.Range(0, form.GenerateCount).Select(_ => prefix + RandomNumberGenerator.GetString(CodeAlphabet, 10))];
        }
        else
        {
            codes = [.. (form.Codes ?? "").Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            if (codes.Count is 0 or > BatchMax || !codes.All(c => CodePattern().IsMatch(c)))
                return Bad("Codes are 3 to 32 letters, digits, - or _, up to 500 at once.");
        }
        var taken = await _tools.CreateGiftCodesAsync(call.Signed, codes,
            new PanelGiftCodeRules(rewards, form.MaxUses, form.PerAccount, form.Starts, form.Expires, note));
        return taken.Count > 0
            ? Reply.Error(StatusCodes.Status409Conflict, "Already taken: " + string.Join(", ", taken.Take(10)) + ". No code was created.")
            : Reply.Ok(new { created = codes });
    }

    private async Task<Reply> SetGiftCodeAsync(Call call, bool disabled) =>
        await _tools.SetGiftCodeDisabledAsync(call.Signed, call.Args[0], disabled)
            ? Reply.Ok(new { code = call.Args[0], disabled }) : Reply.Error(StatusCodes.Status404NotFound, "There is no such code.");

    // ---------------------------------------------------------------- GM console

    private async Task<Reply> RunGmAsync(Call call)
    {
        var form = await BodyAsync<GmForm>(call);
        string line = form?.Line?.Trim() ?? "";
        if (line.Length is 0 or > GmLineMax || line.Any(char.IsControl)) return Bad("Type a command of at most 500 characters.");
        if (!line.StartsWith('!')) line = "!" + line;
        string? character = form?.Character?.Trim() is { Length: > 0 and <= NameMax } c ? c : null;
        return Reply.Ok(await _tools.RunGmAsync(call.Signed, character, line));
    }

    // ---------------------------------------------------------------- news

    private async Task<Reply> SaveNewsAsync(Call call)
    {
        var form = await BodyAsync<NewsForm>(call);
        if (form == null) return Bad("Fill in the form.");
        string title = form.Title?.Trim() ?? "", body = form.Body?.Trim() ?? "", tag = form.Tag ?? "news";
        if (title.Length is 0 or > NewsTitleMax) return Bad("Give a title of at most 120 characters.");
        if (body.Length > NewsBodyMax) return Bad("The text has at most 10,000 characters.");
        if (!NewsTags.Contains(tag)) return Bad("The category is news, event, maintenance or patch.");
        long id = await _tools.SaveNewsAsync(call.Signed,
            new PanelNewsDraft(Math.Max(0, form.Id), title, body, tag, form.Published ?? _clock.GetUtcNow(), form.Pinned));
        return id > 0 ? Reply.Ok(new { id }) : Reply.Error(StatusCodes.Status404NotFound, "This news item no longer exists.");
    }

    private async Task<Reply> DeleteNewsAsync(Call call) =>
        long.TryParse(call.Args[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id)
            && await _tools.DeleteNewsAsync(call.Signed, id)
            ? Reply.Ok(new { deleted = id }) : Reply.Error(StatusCodes.Status404NotFound, "This news item no longer exists.");

    // ---------------------------------------------------------------- broadcasts

    private async Task<Reply> BroadcastAsync(Call call)
    {
        var form = await BodyAsync<BroadcastForm>(call);
        string text = form?.Text?.Trim() ?? "";
        if (form == null || text.Length is 0 or > BroadcastMax || text.Any(char.IsControl)) return Bad("Type a message of at most 200 characters on one line.");
        int every = form.EveryMinutes, repeats = form.Repeats;
        if (every is < 0 or > 1440 || repeats is < 0 or > 100 || (every == 0) != (repeats == 0))
            return Bad("Repeat every 1 to 1440 minutes, 1 to 100 more times, or not at all.");
        return Reply.Ok(new { reached = await _tools.BroadcastAsync(call.Signed, text, every, repeats) });
    }

    private async Task<Reply> CancelBroadcastAsync(Call call) =>
        long.TryParse(call.Args[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id)
            && await _tools.CancelBroadcastAsync(call.Signed, id)
            ? Reply.Ok(new { cancelled = id }) : Reply.Error(StatusCodes.Status404NotFound, "This broadcast no longer repeats.");
}
