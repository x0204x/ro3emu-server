using System.Globalization;
using System.Text.Json;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The server's side of the panel's admin tools (IPanelAdminTools, docs/guide/panel.md). Mail goes through
/// MailService (stored first, then announced to an online recipient), gift codes and news through the store's writer
/// with their audit row in the same transaction, GM commands and broadcasts on the game loop to live sessions.
/// </summary>
public sealed partial class PanelAdminTools(AccountStore accounts, RoleStore roles, MailService mail, GameData data, GameLoop loop,
    SessionRegistry sessions, GmCommands gm, Deadlines deadlines, PanelBackend panel, TimeProvider clock, ILogger<PanelAdminTools> log) : IPanelAdminTools
{
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    private Task<bool> AuditAsync(string admin, string action, string target, string detail) =>
        accounts.WriteAsync<bool>(new AdminAuditWritePlan(Now, admin, action, target, detail));

    public IReadOnlyList<PanelItemChoice> FindItems(string query, int limit)
    {
        if (query.Length == 0) return [];
        if (long.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            return ItemConfig.Get(data, id) == null ? [] : [new PanelItemChoice(id, data.ItemName(id), panel.ItemIconOf(id))];
        return [.. data.FindItems(query).Take(limit).Select(i => new PanelItemChoice(i.Id, i.Name, panel.ItemIconOf(i.Id)))];
    }

    /// <summary>The rewards as "2x Name" text, refusing ids the item table does not have.</summary>
    private string RewardText(IReadOnlyList<PanelMailAttachment> rewards)
    {
        var unknown = rewards.Where(r => ItemConfig.Get(data, r.Item) == null).Select(r => r.Item).Distinct().ToList();
        if (unknown.Count > 0)
            throw new PanelRefusalException("Not in the item table: " + string.Join(", ", unknown.Select(i => i.ToString(CultureInfo.InvariantCulture))) + ".");
        return string.Join(", ", rewards.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Count}x {data.ItemName(r.Item)} ({r.Item})")));
    }

    // ---------------------------------------------------------------- mail

    public async Task<PanelMailRecipients> MailRecipientsAsync(PanelMailTarget target)
    {
        var audience = await ResolveMailAudienceAsync(target);
        return new PanelMailRecipients(audience.Count, [.. audience.Recipients.Take(8).Select(r => r.Name)]);
    }

    public async Task<PanelMailAudience> ResolveMailAudienceAsync(PanelMailTarget target) =>
        new(target, (await RecipientsAsync(target)).Select(r => new PanelMailRecipient(r.Id, r.Name)));

    /// <summary>One character by name, an account's characters, or every character at a level; online ones only on request.</summary>
    private async Task<List<(long Id, string Name)>> RecipientsAsync(PanelMailTarget target)
    {
        List<(long Id, string Name)> found = target.Kind switch
        {
            "character" => await roles.FindByNameAsync(target.Name) is { } role ? [(role.Id, role.Name)] : [],
            "account" => await accounts.ReadAsync<PanelAccountRow?>(new PanelAccountRead(target.Name, Now)) is { } account
                ? [.. (await accounts.ReadAsync<List<PanelRoleRow>>(new PanelRolesRead(account.Name, 100))).Select(r => (r.Id, r.Name))] : [],
            _ => [.. (await accounts.ReadAsync<List<(long Id, string Name, string Account)>>(new PanelRecipientsRead(target.MinLevel)))
                .Select(r => (r.Id, r.Name))],
        };
        if (!target.OnlineOnly) return found;
        var online = await OnlineRoleIdsAsync();
        return [.. found.Where(r => online.Contains(r.Id))];
    }

    private async Task<HashSet<long>> OnlineRoleIdsAsync()
    {
        try { return await loop.InvokeAsync(() => Task.FromResult(sessions.All().Select(s => s.Role?.Id ?? 0).Where(id => id != 0).ToHashSet())); }
        catch (OperationCanceledException) { return []; }
    }

    public async Task<PanelMailResult> SendMailAsync(string admin, PanelMailAudience audience, PanelMail letter)
    {
        string rewards = RewardText(letter.Attachments);
        var attachments = letter.Attachments.Select(a => new MailService.Attachment(a.Item, a.Count)).ToList();
        int delivered = 0, failed = 0;
        // API 已對這份固定名單核對人數；此處不得再次解析條件，把後來的新角色加進來。
        foreach (var (id, name) in audience.Recipients)
        {
            try
            {
                await mail.SendRawAsync(id, letter.Title, letter.Text, attachments, letter.Sender);
                delivered++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogMailFailed(log, e, id, name);
                failed++;
            }
        }
        var target = audience.Target;
        string to = target.Kind == "everyone"
            ? "everyone" + (target.MinLevel > 0 ? string.Create(CultureInfo.InvariantCulture, $" from level {target.MinLevel}") : "")
            : target.Kind + ":" + target.Name;
        if (target.OnlineOnly) to += " (online)";
        await AuditAsync(admin, AdminAudit.Mail, to, string.Create(CultureInfo.InvariantCulture,
            $"{letter.Title}{(rewards.Length > 0 ? " [" + rewards + "]" : "")}: {delivered} delivered, {failed} failed"));
        return new PanelMailResult(delivered, failed);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Panel mail to role {Role} ({Name}) failed")]
    private static partial void LogMailFailed(ILogger logger, Exception error, long role, string name);

    // ---------------------------------------------------------------- gift codes

    public async Task<IReadOnlyList<PanelGiftCode>> GiftCodesAsync()
    {
        long now = Now;
        return [.. (await accounts.ReadAsync<List<GiftCodeRow>>(new GiftCodeListRead(500))).Select(row =>
        {
            var rewards = ParseRewards(row.Items);
            string text = string.Join(", ", rewards.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Count}x {data.ItemName(r.Item)}")));
            string state = row.Disabled ? "disabled" : row.ExpiresUtc is { } e && now >= e ? "expired"
                : row.StartsUtc is { } s && now < s ? "scheduled" : row.MaxUses > 0 && row.Uses >= row.MaxUses ? "used up" : "active";
            return new PanelGiftCode(row.Code, rewards, text, row.MaxUses, row.PerAccount, Time(row.StartsUtc), Time(row.ExpiresUtc),
                row.Disabled, row.CreatedBy, DateTimeOffset.FromUnixTimeSeconds(row.CreatedUtc), row.Note, row.Uses, state);
        })];
    }

    private static DateTimeOffset? Time(long? seconds) => seconds is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    /// <summary>gift_code.items, [[entry, count], ...] (GiftCodeService.Attachments); what does not parse is left out.</summary>
    private static List<PanelMailAttachment> ParseRewards(string items)
    {
        try
        {
            using var json = JsonDocument.Parse(items);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return [];
            return [.. json.RootElement.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2 && p[0].TryGetInt32(out _) && p[1].TryGetInt32(out _))
                .Select(p => new PanelMailAttachment(p[0].GetInt32(), p[1].GetInt32()))];
        }
        catch (JsonException) { return []; }
    }

    public async Task<IReadOnlyList<string>> CreateGiftCodesAsync(string admin, IReadOnlyList<string> codes, PanelGiftCodeRules rules)
    {
        RewardText(rules.Rewards);
        string items = JsonSerializer.Serialize(rules.Rewards.Select(r => new[] { r.Item, r.Count }));
        return await accounts.WriteAsync<string[]>(new GiftCodeCreatePlan(codes, items, rules.MaxUses, rules.PerAccount,
            rules.Starts?.ToUnixTimeSeconds(), rules.Expires?.ToUnixTimeSeconds(), rules.Note, admin, Now));
    }

    public Task<bool> SetGiftCodeDisabledAsync(string admin, string code, bool disabled) =>
        accounts.WriteAsync<bool>(new GiftCodeDisablePlan(code, disabled, admin, Now));

    // ---------------------------------------------------------------- GM console

    /// <summary>
    /// Runs the line for the chosen online character (or the admin's own) on the loop, as POST /ro3emu/gm does, and
    /// collects the GM replies sent to that session while the command ran. The panel already checked the admin, so the
    /// character's own account need not be a GM.
    /// </summary>
    public async Task<PanelGmResult> RunGmAsync(string admin, string? character, string line)
    {
        PanelGmResult result;
        try
        {
            result = await loop.InvokeAsync(async () =>
            {
                var session = sessions.All().FirstOrDefault(s => s.Role is { } r && (character == null
                    ? string.Equals(s.Account, admin, StringComparison.Ordinal)
                    : string.Equals(r.Name, character, StringComparison.OrdinalIgnoreCase)));
                if (session?.Role is not { } role)
                    return new PanelGmResult(false, character ?? "", [character == null
                        ? "Your account has no character in the game. Choose an online character to run it as."
                        : character + " is not in the game."]);
                List<string> replies = [];
                session.GmReplies = replies;
                try { await gm.RunForPanelAdminAsync(session, line); }
                finally { session.GmReplies = null; }
                return new PanelGmResult(true, role.Name, replies);
            });
        }
        catch (OperationCanceledException) { return new PanelGmResult(false, character ?? "", ["The server is stopping."]); }
        if (result.Ran)
        {
            string reply = result.Replies.Count > 0 ? result.Replies[0] : "";
            await AuditAsync(admin, AdminAudit.Gm, result.Character, line + (reply.Length > 0 ? " => " + (reply.Length > 200 ? reply[..200] + "..." : reply) : ""));
        }
        return result;
    }

    public async Task<PanelAuditPage> GmHistoryAsync(string admin, int page, int pageSize)
    {
        var (total, rows) = await accounts.ReadAsync<(int, List<AdminAuditRow>)>(
            new AdminAuditRead((page - 1) * pageSize, pageSize, AdminAudit.Gm, admin));
        return new PanelAuditPage([.. rows.Select(r => new PanelAuditEntry(r.Id, DateTimeOffset.FromUnixTimeSeconds(r.Utc), r.Admin, r.Action, r.Target, r.Detail))],
            total, page, pageSize);
    }

    // ---------------------------------------------------------------- news

    public async Task<IReadOnlyList<PanelNewsItem>> AllNewsAsync() =>
        [.. (await accounts.ReadAsync<List<PanelNewsRow>>(new PanelNewsRead(200, Now, scheduled: true)))
            .Select(n => new PanelNewsItem(n.Id, n.Title, n.Body, n.Tag, DateTimeOffset.FromUnixTimeSeconds(n.PublishedUtc), n.Author, n.Pinned))];

    public Task<long> SaveNewsAsync(string admin, PanelNewsDraft draft) =>
        accounts.WriteAsync<long>(new PanelNewsSavePlan(draft.Id, draft.Title, draft.Body, draft.Tag, draft.Published.ToUnixTimeSeconds(),
            draft.Pinned, admin, Now));

    public Task<bool> DeleteNewsAsync(string admin, long id) => accounts.WriteAsync<bool>(new PanelNewsDeletePlan(id, admin, Now));
}
