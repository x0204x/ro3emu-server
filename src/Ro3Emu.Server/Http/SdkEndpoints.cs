using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts;

using Microsoft.AspNetCore.Builder;
namespace Ro3Emu.Server.Http;

/// <summary>
/// HTTP side the client talks to before any socket: SDK login (HttpNetworkManager, JsonUtility
/// RecoveryOfficialSdkLoginResponse{code, data{token}}) and the server list (Lua LC_LoginManager).
/// ClientPatch points both client URLs here.
/// </summary>
public static class SdkEndpoints
{
    public const string LoginPath = "/sdk/login";

    public const string ServerListPath = "/serverlist/v2/168/serverlist.json";

    /// <summary>Plain-JSON marker understood by Lua_UT_Utils.Decrypt (otherwise it expects base64 AES).</summary>
    private const string RecoveryJsonPrefix = "RO3_RECOVERY_JSON:";

    public const int BigArea = 168;
    public const int AreaId = 1;

    /// <summary>Public half of the gate key, so a player's ClientTools can patch against this server from its URL alone.</summary>
    public const string GateKeyPath = "/ro3emu/gate-key.pem";

    public static void Map(WebApplication app)
    {
        app.MapGet(GateKeyPath, (Net.GateCrypto crypto) => Results.Text(crypto.PublicKeyPem, "application/x-pem-file"));

        app.MapPost(LoginPath, async (HttpRequest request, CredentialLimiter limiter, AccountStore accounts, ILogger<AccountStore> log) =>
        {
            if (!limiter.TryAcquire(request.HttpContext.Connection.RemoteIpAddress)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var form = await request.ReadFormAsync();
            string identity = form["identity"].ToString().Trim();
            string credential = form["credential"].ToString();
            if (identity.Length == 0 || credential.Length == 0)
            {
                return Results.Json(new { code = 401 });
            }
            var (result, account) = await accounts.SignInAsync(identity, credential);
            log.LogInformation("SDK login {Identity}: {Result}", account, result);
            if (result == AccountStore.LoginResult.BadPassword)
            {
                return Results.Json(new { code = 401 });
            }
            return Results.Json(new { code = 0, data = new { token = await accounts.IssueSdkTokenAsync(account) } });
        });

        app.MapGet(ServerListPath, (HttpContext context, IOptions<ServerOptions> options) =>
        {
            var o = options.Value;
            string host = GateHost(context.Request.Host.Host, o.PublicHost);
            string microGate = $"{host}:{o.MicroGatePort}";
            string gameGate = $"{host}:{o.GameGatePort}";
            // `ext` is a JSON string that Lua decodes and uses as the cluster entry (LC_LoginManager._GetAreaListCallback).
            // playbook_id 0 = base pack (installed; other ids pop a download dialog), season 1 = "Beginner Season" tab,
            // area numbers must stay numeric, big_area per area becomes regionID in role create (spec-login-flow §2, §11).
            var ext = JsonSerializer.Serialize(new
            {
                playbook_id = 0,
                season = 1,
                is_cross_area = false,
                need_in_white = false,
                areas = new[] { new { area = AreaId, name = o.AreaName, big_area = BigArea } },
            });
            var body = new
            {
                code = 0,
                data = new
                {
                    big_area = BigArea,
                    gate_address = new[] { microGate },
                    ms_gate = new[] { microGate },
                    clusters = new[]
                    {
                        new { cluster_id = o.ClusterId, name = o.ClusterName, state = 1, game_gate = new[] { gameGate }, ext },
                    },
                },
            };
            return Results.Text(RecoveryJsonPrefix + JsonSerializer.Serialize(body), "text/plain");
        });
    }

    /// <summary>
    /// The gates the client should dial: the IP address it reached this server list on, so a host started with its LAN
    /// address still works for a player who joined through the public IP (and the other way round). A name or no
    /// address falls back to PublicHost: whether the client accepts a host name as a gate address is unverified.
    /// </summary>
    public static string GateHost(string? requestHost, string publicHost)
    {
        string bare = (requestHost ?? "").Trim('[', ']');
        if (!System.Net.IPAddress.TryParse(bare, out var address) || System.Net.IPAddress.IsLoopback(address)
            || address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any))
            return publicHost;
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{bare}]" : bare;
    }
}
