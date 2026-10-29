using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Http;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Tests;

/// <summary>
/// Account bans (docs/systems/account-bans.md): the admin panel writes account_ban rows into the server's database
/// from its own connection; the server reads them on every credential check and refuses a banned account the way it
/// refuses a wrong password. Account names and ban rows here are invented.
/// </summary>
public sealed class AccountBanTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-ban-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "", _pem = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
        _pem = File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem"));
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task ABannedAccountCannotLogInNorUseATicketIssuedBeforeTheBan()
    {
        var (code, ticket) = await SdkLoginAsync("ban-target", "pw");
        Assert.Equal(0, code);
        Ban("ban-target");

        Assert.Equal("{\"code\":401}", await SdkLoginBodyAsync("ban-target", "pw"));
        await using var bot = new BotClient(_http, _pem);
        await bot.LoginAsync("ban-witness", "pw");
        await bot.ReconnectMicroAsync();
        Assert.Equal("invalid ticket", Message(await bot.MicroAsync("LoginService", "verifySdk",
            new Dictionary<string, object?> { ["Ticket"] = ticket })));
    }

    [Fact]
    public async Task AnExpiredBanNoLongerBlocks()
    {
        Assert.Equal(0, (await SdkLoginAsync("ban-expired", "pw")).Code);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Ban("ban-expired", until: now + 3600);
        Assert.Equal(401, (await SdkLoginAsync("ban-expired", "pw")).Code);

        Ban("ban-expired", until: now - 60);
        Assert.Equal(0, (await SdkLoginAsync("ban-expired", "pw")).Code);
    }

    [Fact]
    public async Task DeletingTheBanRowRestoresLogin()
    {
        Assert.Equal(0, (await SdkLoginAsync("ban-lifted", "pw")).Code);
        Ban("ban-lifted");
        Assert.Equal(401, (await SdkLoginAsync("ban-lifted", "pw")).Code);

        Unban("ban-lifted");
        Assert.Equal(0, (await SdkLoginAsync("ban-lifted", "pw")).Code);
    }

    [Fact]
    public async Task AnotherAccountIsUnaffected()
    {
        Assert.Equal(0, (await SdkLoginAsync("ban-target", "pw")).Code);
        Ban("ban-target");

        await using var bot = new BotClient(_http, _pem);
        await bot.LoginAsync("ban-bystander", "pw");
        Assert.Equal("ban-bystander", bot.Account);
        Assert.Equal(0, (await SdkLoginAsync("ban-bystander", "pw")).Code);
    }

    [Fact]
    public async Task ARowThePanelInsertsIsSeenWithoutARestart()
    {
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        Assert.Equal(0, (await SdkLoginAsync("ban-live", "pw")).Code);
        string sign = await accounts.IssueEngineSignAsync("ban-live");
        Assert.Equal("ban-live", await accounts.ResolveEngineSignAsync(sign));

        Ban("ban-live");
        Assert.Equal(401, (await SdkLoginAsync("ban-live", "pw")).Code);
        Assert.Null(await accounts.ResolveEngineSignAsync(sign));
    }

    [Fact]
    public async Task AWrongPasswordOnABannedAccountAnswersLikeAWrongPassword()
    {
        Assert.Equal(0, (await SdkLoginAsync("ban-wrong", "pw")).Code);
        Assert.Equal(0, (await SdkLoginAsync("ban-free", "pw")).Code);
        Ban("ban-wrong");

        string unbanned = await SdkLoginBodyAsync("ban-free", "not-the-password");
        Assert.Equal("{\"code\":401}", unbanned);
        Assert.Equal(unbanned, await SdkLoginBodyAsync("ban-wrong", "not-the-password"));
    }

    [Fact]
    public async Task EveryOtherCredentialPathRefusesABannedAccount()
    {
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        Assert.Equal(0, (await SdkLoginAsync("ban-paths", "pw")).Code);
        string sign = await accounts.IssueEngineSignAsync("ban-paths");
        byte[] gateAuth = _app.Services.GetRequiredService<GateAuthRegistry>().Issue("ban-paths-entity", "ban-paths");
        Ban("ban-paths");

        Assert.Null(accounts.ResolveEngineSign(sign));
        Assert.Null(await accounts.VerifiedAccountAsync("ban-paths", "pw"));
        Assert.False(accounts.Verify("ban-paths", "pw"));
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        { ["identity"] = "ban-paths", ["credential"] = "pw", ["role"] = "1" });
        var deleted = await (await PostAsync("/ro3emu/account/delete-role", form)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(401, deleted.GetProperty("code").GetInt32());

        await using var bot = new BotClient(_http, _pem);
        await bot.LoginAsync("ban-paths-witness", "pw");
        await bot.ReconnectMicroAsync();
        Assert.False(await bot.BindGateAsync("ban-paths-entity", gateAuth));
        // A ban outlives its account (the panel deletes the account, not the ban): the name is refused before it is
        // registered again, so no account is created for it.
        Ban("ban-ghost");
        Assert.Equal(401, (await SdkLoginAsync("ban-ghost", "pw")).Code);
        using (var db = PanelConnection())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM account WHERE name='ban-ghost'";
            Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
        }
    }

    [Fact]
    public async Task ABanOutlivesItsAccountWhateverCaseTheNameIsTypedIn()
    {
        // The account lookup and the registration's uniqueness ignore case (AccountPlans): a case variant of a banned
        // name with no account must not create one, or the original spelling would sign in to it.
        Ban("Ban-Ghost-Case");
        Assert.Equal(401, (await SdkLoginAsync("ban-ghost-case", "pw")).Code);
        Assert.Equal(401, (await SdkLoginAsync("BAN-GHOST-CASE", "pw")).Code);
        Assert.Equal(401, (await SdkLoginAsync("Ban-Ghost-Case", "pw")).Code);
        using var db = PanelConnection();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM account WHERE name='Ban-Ghost-Case' COLLATE NOCASE";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    private async Task<string> SdkLoginBodyAsync(string identity, string credential)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["identity"] = identity, ["credential"] = credential });
        return await (await PostAsync(SdkEndpoints.LoginPath, form)).Content.ReadAsStringAsync();
    }

    private async Task<HttpResponseMessage> PostAsync(string path, HttpContent form)
    {
        using var client = new HttpClient();
        return await client.PostAsync(_http + path, form);
    }

    private async Task<(int Code, string? Ticket)> SdkLoginAsync(string identity, string credential)
    {
        var body = JsonDocument.Parse(await SdkLoginBodyAsync(identity, credential)).RootElement;
        int code = body.GetProperty("code").GetInt32();
        return (code, code == 0 ? body.GetProperty("data").GetProperty("token").GetString() : null);
    }

    /// <summary>What the panel does: its own connection to the database file, insert-or-replace one row.</summary>
    private void Ban(string account, long? until = null)
    {
        using var db = PanelConnection();
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT OR REPLACE INTO account_ban(account,reason,banned_utc,until_utc,banned_by) " +
            "VALUES($account,'test ban',$now,$until,'test-admin')";
        insert.Parameters.AddWithValue("$account", account);
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        insert.Parameters.AddWithValue("$until", until is { } value ? value : DBNull.Value);
        Assert.Equal(1, insert.ExecuteNonQuery());
    }

    private void Unban(string account)
    {
        using var db = PanelConnection();
        using var delete = db.CreateCommand();
        delete.CommandText = "DELETE FROM account_ban WHERE account=$account";
        delete.Parameters.AddWithValue("$account", account);
        Assert.Equal(1, delete.ExecuteNonQuery());
    }

    private SqliteConnection PanelConnection()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_dataDir, "ro3emu.db"), Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static string Message(Dictionary<object, object?> response) =>
        (string)((object[])response["Ret"]!)[1]!;
}
