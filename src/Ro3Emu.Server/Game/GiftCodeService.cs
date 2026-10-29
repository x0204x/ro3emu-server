using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Server.Game;

/// <summary>What the client posts to /gift-card/use/v2 that the server uses (LC_RedemptionCodeManager.UseGiftCard).</summary>
public sealed record GiftCodeRequest(string? AccountId, string? AccountToken, string? RoleId, string? Code);

/// <summary>The JSON answer: <see cref="Code"/> 1959 is success, anything else a refusal whose message the client shows.</summary>
public readonly record struct GiftCodeAnswer(int Code, string Message);

/// <summary>
/// Gift code redemption (docs/systems/gift-codes.md). The client sends the engine credentials it holds
/// (NW_CBBNetworkManager.GetEngineClientAccountAndToken, Happyengine.Network: the MixedLoginToken's Username and Sign
/// that LoginService.verifySdk issued), the selected role and the typed code. The server checks the credentials, the
/// role, then the code's gift_code row, and mails its items once per allowed use: the mail and its
/// gift_code_redemption row commit in one transaction (MailService.SendRawOnceAsync), so the grant reaches an
/// offline role or a full bag and a replay never grants twice. Every rule past the credentials is (inferred).
/// </summary>
public sealed partial class GiftCodeService(AccountStore accounts, RoleStore roles, MailService mail, GameData data,
    GiftCodeLimiter limiter,
    TimeProvider clock, ILogger<GiftCodeService> log)
{
    /// <summary>(inferred) The longest code text, as gift_code's CHECK allows; longer input is an unknown code.</summary>
    public const int MaxCodeLength = 64;

    /// <summary>(inferred) The gift mail's title and text (raw mail from "RO3"): the client has no template for it.</summary>
    public const string MailTitle = "Gift code reward";
    public static string MailBody(string code) => $"Here are the rewards of gift code {code}.";

    /// <summary>
    /// One redemption request. Any unexpected failure still answers JSON (1972): the client treats every other response
    /// as a network error.
    /// </summary>
    public async Task<GiftCodeAnswer> RedeemAsync(GiftCodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try { return await RedeemCoreAsync(request); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogRequestFailed(log, e, request.Code ?? "");
            return Answer(ErrorCodes.GiftCardFailed);
        }
    }

    private async Task<GiftCodeAnswer> RedeemCoreAsync(GiftCodeRequest request)
    {
        if (await AccountAsync(request) is not { } account) return Answer(ErrorCodes.GiftCardBadCredentials);
        // (inferred) The account budget counts only authenticated attempts, so a wrong token naming someone else's
        // account cannot use up that account's attempts; unauthenticated guessing is bounded by the address budget.
        if (!limiter.TryAcquire(null, account)) return Answer(ErrorCodes.GiftCardRateLimited);
        if (await RoleAsync(request.RoleId, account) is not { } role) return Answer(ErrorCodes.GiftCardRoleNotOwned);
        string code = request.Code?.Trim() ?? "";
        if (code.Length is 0 or > MaxCodeLength) return Answer(ErrorCodes.GiftCardUnknown);
        if (await accounts.ReadGiftCodeAsync(code, account) is not { } state) return Answer(ErrorCodes.GiftCardUnknown);
        if (state.Refusal(clock.GetUtcNow().ToUnixTimeSeconds()) is { } refusal) return Answer(refusal);
        if (Attachments(state.Items) is not { } items)
        {
            LogInvalidItems(log, state.Code, state.Items);
            return Answer(ErrorCodes.GiftCardInvalidItems);
        }
        return await GrantAsync(state, account, role.Id, items);
    }

    /// <summary>The account whose valid, unexpired engine sign is account_token, when account_id names it.</summary>
    private async Task<string?> AccountAsync(GiftCodeRequest request)
    {
        if (string.IsNullOrEmpty(request.AccountId) || string.IsNullOrEmpty(request.AccountToken)) return null;
        string? owner = await accounts.ResolveEngineSignAsync(request.AccountToken);
        return string.Equals(owner, request.AccountId, StringComparison.Ordinal) ? owner : null;
    }

    /// <summary>The live (not deleted) role <paramref name="roleId"/> when it belongs to <paramref name="account"/>.</summary>
    private async Task<Role?> RoleAsync(string? roleId, string account)
    {
        if (!long.TryParse(roleId, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0) return null;
        var role = await roles.GetAsync(id);
        return role != null && string.Equals(role.Account, account, StringComparison.Ordinal) ? role : null;
    }

    /// <summary>
    /// gift_code.items: a JSON array of [itemEntry, count] pairs, each entry an ItemConfig row and each count positive.
    /// Null when the list is empty or any pair is not valid; the code is then refused rather than partly granted.
    /// </summary>
    internal IReadOnlyList<MailService.Attachment>? Attachments(string items)
    {
        try
        {
            using var json = JsonDocument.Parse(items);
            if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() == 0) return null;
            var attachments = new List<MailService.Attachment>();
            foreach (var pair in json.RootElement.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2
                    || pair[0].ValueKind != JsonValueKind.Number || pair[1].ValueKind != JsonValueKind.Number
                    || !pair[0].TryGetInt32(out int entry) || !pair[1].TryGetInt32(out int count)
                    || count <= 0 || ItemConfig.Get(data, entry) == null)
                    return null;
                attachments.Add(new MailService.Attachment(entry, count));
            }
            return attachments;
        }
        catch (JsonException) { return null; }
    }

    private async Task<GiftCodeAnswer> GrantAsync(GiftCodeState state, string account, long role,
        IReadOnlyList<MailService.Attachment> items)
    {
        string key = state.NextKey(account);
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        try
        {
            await mail.SendRawOnceAsync(role, MailTitle, MailBody(state.Code), items, key,
                tx => tx.Add(new GiftCodeRedeemPlan(key, state.Code, account, role, now)));
        }
        catch (Exception e) when (Refused(e) is { } refused)
        {
            return Answer(refused.Code);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(log, e, state.Code, role);
            return Answer(ErrorCodes.GiftCardFailed);
        }
        LogRedeemed(log, state.Code, account, role, key);
        return Answer(ErrorCodes.GiftCardUseSuccess);
    }

    private static GiftCodeRefusedException? Refused(Exception? e)
    {
        for (; e != null; e = e.InnerException)
        {
            if (e is GiftCodeRefusedException refused) return refused;
            if (e is AggregateException { InnerExceptions.Count: 1 } aggregate) return Refused(aggregate.InnerExceptions[0]);
        }
        return null;
    }

    /// <summary>The answer for <paramref name="code"/>. (inferred) The texts are this server's; the client shows them as sent.</summary>
    public static GiftCodeAnswer Answer(int code) => new(code, code switch
    {
        ErrorCodes.GiftCardUseSuccess => "Redeemed. The rewards were sent to your mailbox.",
        ErrorCodes.GiftCardBadRequest => "Invalid request.",
        ErrorCodes.GiftCardBadCredentials => "Your login is no longer valid. Please log in again.",
        ErrorCodes.GiftCardRoleNotOwned => "This character does not belong to your account.",
        ErrorCodes.GiftCardUnknown => "Invalid code.",
        ErrorCodes.GiftCardDisabled => "This code is no longer active.",
        ErrorCodes.GiftCardNotStarted => "This code is not active yet.",
        ErrorCodes.GiftCardExpired => "This code has expired.",
        ErrorCodes.GiftCardExhausted => "This code has been fully redeemed.",
        ErrorCodes.GiftCardAlreadyRedeemed => "You have already redeemed this code.",
        ErrorCodes.GiftCardInvalidItems => "This code cannot be redeemed right now.",
        ErrorCodes.GiftCardRateLimited => "Too many attempts. Please try again in a minute.",
        _ => "The code could not be redeemed. Please try again later.",
    });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gift code {Code} has invalid items {Items}")]
    private static partial void LogInvalidItems(ILogger logger, string code, string items);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gift code {Code} for role {Role} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, string code, long role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gift code {Code} redeemed by {Account} for role {Role} ({Key})")]
    private static partial void LogRedeemed(ILogger logger, string code, string account, long role, string key);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gift code request for {Code} failed")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception, string code);
}
