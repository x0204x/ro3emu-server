using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Http;

namespace Ro3Emu.Tests;

/// <summary>
/// Gift codes (docs/systems/gift-codes.md): the admin panel writes gift_code rows straight into the database, the
/// client posts LC_RedemptionCodeManager's JSON to /gift-card/use/v2 with its engine credentials, and the server mails
/// the items once per allowed use. The rows here are written through a separate connection, as the panel does.
/// </summary>
public sealed class GiftCodeTests(GiftCodeTests.HostFixture fixture) : IClassFixture<GiftCodeTests.HostFixture>
{
    private const int Potion = 1000000, Herb = 1004000;
    private readonly SyntheticHost _host = fixture.Host;

    public sealed class HostFixture : IAsyncLifetime
    {
        public SyntheticHost Host { get; private set; } = null!;
        public async Task InitializeAsync() => Host = await SyntheticHost.StartAsync();
        public async Task DisposeAsync() => await Host.DisposeAsync();
    }

    private sealed record Player(string Account, string Token, long Role);

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..10];

    private static async Task<Player> PlayerAsync(SyntheticHost host, string? account = null)
    {
        account ??= Unique("gift");
        string token = await host.Service<AccountStore>().IssueEngineSignAsync(account);
        long role = host.Service<RoleStore>().Create(account, 1, Unique("G"), 1, 1000, 1)!.Id;
        return new(account, token, role);
    }

    /// <summary>A gift_code row as the panel inserts it (its own connection, no server code).</summary>
    private static void Panel(SyntheticHost host, string code, string items, int maxUses = 0, int perAccount = 1,
        long? starts = null, long? expires = null, int disabled = 0)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "ro3emu.db")};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO gift_code(code,items,max_uses,per_account,starts_utc,expires_utc,disabled,created_by,note)
            VALUES($code,$items,$max,$per,$starts,$expires,$disabled,'panel','test')
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$items", items);
        cmd.Parameters.AddWithValue("$max", maxUses);
        cmd.Parameters.AddWithValue("$per", perAccount);
        cmd.Parameters.AddWithValue("$starts", (object?)starts ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expires", (object?)expires ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$disabled", disabled);
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SyntheticHost host, string sql, params (string Name, object Value)[] parameters)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "ro3emu.db")};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>The redemption and mail delivery key of an account's use of a code.</summary>
    private static string Key(string code, string account, int use = 1) =>
        $"giftcode:{code.Length}:{code}:{use}:{account}";

    private static long Redemptions(SyntheticHost host, string code) =>
        Scalar(host, "SELECT COUNT(*) FROM gift_code_redemption WHERE code=$c", ("$c", code));

    private static string Body(Player player, string code, object? roleId = null) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["gift_card_code"] = code, ["game_code"] = "ro3", ["platform_code"] = "pc", ["group_id"] = "1",
        ["open_id"] = player.Account, ["role_id"] = roleId ?? player.Role.ToString(CultureInfo.InvariantCulture), ["language"] = "en",
        ["big_area"] = 168, ["account_id"] = player.Account, ["account_token"] = player.Token,
    });

    private static async Task<(HttpStatusCode Status, int Code, string Message)> PostAsync(SyntheticHost host, string body,
        string? contentType = "application/json")
    {
        using var client = new HttpClient();
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (contentType != null) content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, host.Http + GiftCodeEndpoints.UsePath) { Content = content };
        request.Headers.Add("GameCode", "ro3");
        using var response = await client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, json.RootElement.GetProperty("code").GetInt32(),
            json.RootElement.GetProperty("message").GetString()!);
    }

    private Task<(HttpStatusCode Status, int Code, string Message)> RedeemAsync(Player player, string code) =>
        PostAsync(_host, Body(player, code));

    private List<MailService.Letter> Letters(long role, string deliveryKey) =>
        [.. JsonSerializer.Deserialize<MailService.Mailbox>(_host.Service<RoleStore>().LoadMailbox(role) ?? "{}"u8.ToArray())!
            .Letters.Where(l => l.DeliveryKey == deliveryKey)];

    [Fact]
    public async Task ARedeemedCodeMailsItsItemsOnceAndRecordsTheRedemption()
    {
        string code = Unique("OK");
        Panel(_host, code, $"[[{Potion},3],[{Herb},1]]");
        var player = await PlayerAsync(_host);

        var (status, result, message) = await RedeemAsync(player, code);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(ErrorCodes.GiftCardUseSuccess, result);
        Assert.False(string.IsNullOrWhiteSpace(message));
        var letter = Assert.Single(Letters(player.Role, Key(code, player.Account)));
        Assert.Equal([new MailService.Attachment(Potion, 3), new MailService.Attachment(Herb, 1)], letter.Attachments);
        Assert.Equal(1, Redemptions(_host, code));
        Assert.Equal(player.Role, Scalar(_host, "SELECT role_id FROM gift_code_redemption WHERE code=$c", ("$c", code)));
    }

    [Fact]
    public async Task AReplayOfTheSameRequestDoesNotGrantTwice()
    {
        string code = Unique("RE");
        Panel(_host, code, $"[[{Potion},1]]");
        var player = await PlayerAsync(_host);

        Assert.Equal(ErrorCodes.GiftCardUseSuccess, (await RedeemAsync(player, code)).Code);
        Assert.Equal(ErrorCodes.GiftCardAlreadyRedeemed, (await RedeemAsync(player, code)).Code);

        Assert.Single(Letters(player.Role, Key(code, player.Account)));
        Assert.Equal(1, Redemptions(_host, code));
    }

    [Fact]
    public async Task PerAccountLimitCountsEveryRoleOfTheAccount()
    {
        string twice = Unique("PA"), once = Unique("P1");
        Panel(_host, twice, $"[[{Potion},1]]", perAccount: 2);
        Panel(_host, once, $"[[{Potion},1]]");
        var player = await PlayerAsync(_host);
        var alt = player with { Role = _host.Service<RoleStore>().Create(player.Account, 1, Unique("A"), 1, 1000, 1)!.Id };

        Assert.Equal(ErrorCodes.GiftCardUseSuccess, (await RedeemAsync(player, twice)).Code);
        Assert.Equal(ErrorCodes.GiftCardUseSuccess, (await RedeemAsync(alt, twice)).Code);
        Assert.Equal(ErrorCodes.GiftCardAlreadyRedeemed, (await RedeemAsync(player, twice)).Code);
        Assert.Equal(ErrorCodes.GiftCardUseSuccess, (await RedeemAsync(player, once)).Code);
        Assert.Equal(ErrorCodes.GiftCardAlreadyRedeemed, (await RedeemAsync(alt, once)).Code);

        Assert.Single(Letters(player.Role, Key(twice, player.Account)));
        Assert.Single(Letters(alt.Role, Key(twice, player.Account, 2)));
        Assert.Empty(Letters(alt.Role, Key(once, player.Account)));
        Assert.Equal(2, Redemptions(_host, twice));
        Assert.Equal(1, Redemptions(_host, once));
    }

    [Fact]
    public async Task TotalLimitCountsEveryAccount()
    {
        string code = Unique("TL");
        Panel(_host, code, $"[[{Potion},1]]", maxUses: 2);

        Assert.Equal(ErrorCodes.GiftCardUseSuccess, (await RedeemAsync(await PlayerAsync(_host), code)).Code);
        Assert.Equal(ErrorCodes.GiftCardUseSuccess, (await RedeemAsync(await PlayerAsync(_host), code)).Code);
        var late = await PlayerAsync(_host);
        Assert.Equal(ErrorCodes.GiftCardExhausted, (await RedeemAsync(late, code)).Code);

        Assert.Empty(Letters(late.Role, Key(code, late.Account)));
        Assert.Equal(2, Redemptions(_host, code));
    }

    [Fact]
    public async Task CodeTextIsMatchedTrimmedAndCaseInsensitive()
    {
        string code = Unique("CI").ToUpperInvariant();
        Panel(_host, code, $"[[{Potion},1]]");
        var player = await PlayerAsync(_host);

        Assert.Equal(ErrorCodes.GiftCardUseSuccess, (await RedeemAsync(player, "  " + code.ToLowerInvariant() + " ")).Code);

        Assert.Single(Letters(player.Role, Key(code, player.Account)));
    }

    [Fact]
    public async Task ARowThePanelInsertsLaterIsSeenWithoutARestart()
    {
        string code = Unique("LT");
        var player = await PlayerAsync(_host);
        Assert.Equal(ErrorCodes.GiftCardUnknown, (await RedeemAsync(player, code)).Code);

        Panel(_host, code, $"[[{Potion},1]]");

        Assert.Equal(ErrorCodes.GiftCardUseSuccess, (await RedeemAsync(player, code)).Code);
    }

    [Fact]
    public async Task EveryCodeRuleRefusesWithItsOwnCode()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var cases = new (string Code, int Expected)[]
        {
            (Unique("UN"), ErrorCodes.GiftCardUnknown),
            (Unique("DI"), ErrorCodes.GiftCardDisabled),
            (Unique("NS"), ErrorCodes.GiftCardNotStarted),
            (Unique("EX"), ErrorCodes.GiftCardExpired),
            (Unique("I1"), ErrorCodes.GiftCardInvalidItems),
            (Unique("I2"), ErrorCodes.GiftCardInvalidItems),
            (Unique("I3"), ErrorCodes.GiftCardInvalidItems),
            (Unique("I4"), ErrorCodes.GiftCardInvalidItems),
        };
        Panel(_host, cases[1].Code, $"[[{Potion},1]]", disabled: 1);
        Panel(_host, cases[2].Code, $"[[{Potion},1]]", starts: now + 3600);
        Panel(_host, cases[3].Code, $"[[{Potion},1]]", expires: now - 1);
        Panel(_host, cases[4].Code, "[[999999999,1]]"); // no ItemConfig row
        Panel(_host, cases[5].Code, $"[[{Potion},0]]");
        Panel(_host, cases[6].Code, "not json");
        Panel(_host, cases[7].Code, "[]");
        var player = await PlayerAsync(_host);

        foreach (var (code, expected) in cases)
        {
            var (status, result, message) = await RedeemAsync(player, code);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(expected == result, $"{code}: expected {expected}, got {result} ({message})");
            Assert.False(string.IsNullOrWhiteSpace(message));
        }
        Assert.Equal(0, Scalar(_host, "SELECT COUNT(*) FROM gift_code_redemption WHERE account=$a", ("$a", player.Account)));
        Assert.Null(_host.Service<RoleStore>().LoadMailbox(player.Role));
    }

    [Fact]
    public async Task OnlyAValidUnexpiredEngineTokenOfTheAccountIsAccepted()
    {
        string code = Unique("TK");
        Panel(_host, code, $"[[{Potion},1]]");
        var player = await PlayerAsync(_host);
        var other = await PlayerAsync(_host);
        string sdk = await _host.Service<AccountStore>().IssueSdkTokenAsync(player.Account);
        string expired = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        Scalar(_host, "INSERT INTO auth_token(token,kind,account,expires_utc) VALUES($t,'engine',$a,$e); SELECT 0",
            ("$t", expired), ("$a", player.Account), ("$e", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1));

        foreach (string token in new[] { other.Token, sdk, expired, "", "nope" })
            Assert.Equal(ErrorCodes.GiftCardBadCredentials, (await RedeemAsync(player with { Token = token }, code)).Code);

        Assert.Equal(0, Redemptions(_host, code));
        Assert.Equal(ErrorCodes.GiftCardUseSuccess, (await RedeemAsync(player, code)).Code);
    }

    [Fact]
    public async Task ARoleOfAnotherAccountOrADeletedRoleIsRefused()
    {
        string code = Unique("RO");
        Panel(_host, code, $"[[{Potion},1]]");
        var player = await PlayerAsync(_host);
        var other = await PlayerAsync(_host);
        var roles = _host.Service<RoleStore>();
        long deleted = roles.Create(player.Account, 1, Unique("D"), 1, 1000, 1)!.Id;
        Assert.True(roles.Delete(player.Account, deleted));

        Assert.Equal(ErrorCodes.GiftCardRoleNotOwned, (await RedeemAsync(player with { Role = other.Role }, code)).Code);
        Assert.Equal(ErrorCodes.GiftCardRoleNotOwned, (await RedeemAsync(player with { Role = deleted }, code)).Code);
        Assert.Equal(ErrorCodes.GiftCardRoleNotOwned, (await PostAsync(_host, Body(player, code, roleId: "x"))).Code);

        Assert.Equal(0, Redemptions(_host, code));
        Assert.Null(roles.LoadMailbox(other.Role));
    }

    [Fact]
    public async Task TheClientBodyIsReadWithANumericRoleIdAndWithoutAContentType()
    {
        string code = Unique("CT");
        Panel(_host, code, $"[[{Potion},1]]");
        var player = await PlayerAsync(_host);

        Assert.Equal(ErrorCodes.GiftCardUseSuccess, (await PostAsync(_host, Body(player, code, roleId: player.Role), contentType: null)).Code);
        Assert.Equal(ErrorCodes.GiftCardBadRequest, (await PostAsync(_host, "{not json")).Code);
        Assert.Equal(ErrorCodes.GiftCardBadRequest, (await PostAsync(_host, "[]")).Code);
    }

    [Fact]
    public async Task ARateLimitedRequestStillAnswersJson()
    {
        await using var host = await SyntheticHost.StartAsync(o =>
        {
            o.GiftCodeAttemptsPerMinute = 2;
            o.LoginLimitIncludesLoopback = true;
        });
        var player = await PlayerAsync(host);
        string code = Unique("RL");

        var answers = new List<(HttpStatusCode Status, int Code, string Message)>();
        for (int i = 0; i < 3; i++) answers.Add(await PostAsync(host, Body(player, code)));

        Assert.All(answers.Take(2), a => Assert.Equal(ErrorCodes.GiftCardUnknown, a.Code));
        Assert.Equal(HttpStatusCode.OK, answers[2].Status);
        Assert.Equal(ErrorCodes.GiftCardRateLimited, answers[2].Code);
        Assert.False(string.IsNullOrWhiteSpace(answers[2].Message));
    }

    [Fact]
    public void TheAccountBudgetAppliesOnLoopbackToo()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new Ro3Emu.Server.ServerOptions { GiftCodeAttemptsPerMinute = 2 });
        var limiter = new GiftCodeLimiter(options, TimeProvider.System);

        Assert.True(limiter.TryAcquire(IPAddress.Loopback, "acct"));
        Assert.True(limiter.TryAcquire(IPAddress.Loopback, "acct"));
        Assert.False(limiter.TryAcquire(IPAddress.Loopback, "acct"));
        Assert.True(limiter.TryAcquire(IPAddress.Loopback, "another"));
    }

    // Verifier B1: a panel-written entry or count that is not a JSON number is refused as JSON, not an HTTP 500.
    [Theory]
    [InlineData("[[\"1000000\",1]]")]
    [InlineData("[[1000000,\"1\"]]")]
    [InlineData("[[null,1]]")]
    [InlineData("[[true,1]]")]
    [InlineData("[[{},1]]")]
    [InlineData("[[[],1]]")]
    public async Task ItemsThatAreNotNumbersAreRefusedAsJson(string items)
    {
        string code = Unique("NN");
        Panel(_host, code, items);
        var player = await PlayerAsync(_host);

        var (status, result, _) = await RedeemAsync(player, code);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(ErrorCodes.GiftCardInvalidItems, result);
        Assert.Equal(0, Redemptions(_host, code));
    }

    // Verifier A6: a panel number column holding text closes the code instead of opening it.
    [Theory]
    [InlineData("max_uses", "'abc'")]
    [InlineData("per_account", "'x'")]
    [InlineData("starts_utc", "'tomorrow'")]
    [InlineData("expires_utc", "'never'")]
    public async Task APanelNumberThatIsTextClosesTheCode(string column, string value)
    {
        string code = Unique("TX");
        Panel(_host, code, $"[[{Potion},1]]");
        Scalar(_host, $"UPDATE gift_code SET {column}={value} WHERE code=$c; SELECT changes()", ("$c", code));
        var player = await PlayerAsync(_host);

        var (_, result, _) = await RedeemAsync(player, code);

        Assert.Equal(ErrorCodes.GiftCardInvalidItems, result);
        Assert.Equal(0, Redemptions(_host, code));
    }

    // Verifier A4: names with ':' cannot make two different (code, account) pairs share a redemption key.
    [Fact]
    public void RedemptionKeysOfDifferentCodesAndAccountsNeverCollide()
    {
        var withColon = new GiftCodeState("a:b", "[]", 0, 1, null, null, false, 0, 0);
        var plain = new GiftCodeState("a", "[]", 0, 1, null, null, false, 0, 0);
        Assert.NotEqual(withColon.NextKey("c"), plain.NextKey("b:c"));
        // An account named "x:2" on its first use and account "x" on its second use (verifier recheck).
        var firstUse = new GiftCodeState("K", "[]", 0, 2, null, null, false, 0, 0);
        var secondUse = firstUse with { AccountUses = 1 };
        Assert.NotEqual(firstUse.NextKey("x:2"), secondUse.NextKey("x"));
    }

    // Verifier A1: requests with someone else's account_id and a wrong token do not spend that account's budget.
    [Fact]
    public async Task BadTokensDoNotSpendTheNamedAccountsBudget()
    {
        string code = Unique("BG");
        Panel(_host, code, $"[[{Potion},1]]");
        var victim = await PlayerAsync(_host);
        var attacker = victim with { Token = "not-a-sign" };
        int budget = _host.Service<IOptions<ServerOptions>>().Value.GiftCodeAttemptsPerMinute;

        for (int i = 0; i <= budget; i++)
            Assert.Equal(ErrorCodes.GiftCardBadCredentials, (await RedeemAsync(attacker, code)).Code);

        Assert.Equal(ErrorCodes.GiftCardUseSuccess, (await RedeemAsync(victim, code)).Code);
    }
}
