using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The client's "Redemption Code" screen (LC_RedemptionCodeManager.UseGiftCard): a JSON POST to GMTUrl() +
/// <see cref="UsePath"/> with the header GameCode and the body gift_card_code, game_code, platform_code, group_id,
/// open_id, role_id, language, big_area, account_id, account_token. The client reads a JSON {code, message} answer:
/// 1959 or 1960 is success, anything else a refusal whose message it shows. Every answer here, a rate-limited one
/// included, is that JSON with HTTP 200. See docs/systems/gift-codes.md.
/// </summary>
public static class GiftCodeEndpoints
{
    public const string UsePath = "/gift-card/use/v2";

    /// <summary>(inferred) The largest body read; the client's is a few hundred bytes.</summary>
    public const int MaxBodyBytes = 8192;

    public static void Map(WebApplication app) =>
        app.MapPost(UsePath, async (HttpRequest request, GiftCodeLimiter limiter, GiftCodeService gifts) =>
        {
            // (inferred) The body is read whatever Content-Type says (or if it is missing): the client's HTTP layer
            // sets application/json, but nothing else here depends on it. game_code and the GameCode header are not
            // checked: this server hosts one game.
            var body = await ReadAsync(request);
            if (!limiter.TryAcquire(request.HttpContext.Connection.RemoteIpAddress, null))
                return Reply(GiftCodeService.Answer(ErrorCodes.GiftCardRateLimited));
            if (body == null) return Reply(GiftCodeService.Answer(ErrorCodes.GiftCardBadRequest));
            return Reply(await gifts.RedeemAsync(body));
        });

    private static IResult Reply(GiftCodeAnswer answer) => Results.Json(new { code = answer.Code, message = answer.Message });

    /// <summary>The fields the server uses, or null for a body that is too large or not a JSON object.</summary>
    private static async Task<GiftCodeRequest?> ReadAsync(HttpRequest request)
    {
        if (request.ContentLength > MaxBodyBytes) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        try
        {
            using var json = JsonDocument.Parse(buffer.ToArray());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new GiftCodeRequest(Text(root, "account_id"), Text(root, "account_token"), Text(root, "role_id"),
                Text(root, "gift_card_code"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A string field, or a number's literal text (role_id may come either way); null otherwise.</summary>
    private static string? Text(JsonElement root, string name) =>
        !root.TryGetProperty(name, out var value) ? null : value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
}
