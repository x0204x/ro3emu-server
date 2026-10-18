using Microsoft.Extensions.Options;

namespace Ro3Emu.Server.Micro;

/// <summary>First-login server recommendation: {"Cluster": int} marks that cluster's first area.</summary>
public sealed class ClusterRecommendService(IOptions<ServerOptions> options) : IMicroService
{
    public string Name => "ClusterRecommendService";

    public ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args) =>
        ValueTask.FromResult<object?>(method == "getRecommendCluster"
            ? new Dictionary<string, object?> { ["Ret"] = MicroServiceRouter.Ret(true), ["Cluster"] = options.Value.ClusterId }
            : null);
}

/// <summary>
/// Platform text filter. LC_DAL_Stall sends sensitiveCheck [text, false] before renaming a stall and reads only
/// {Detected} (StallSensitiveCheck event). The original's platform word list is not available, so nothing is
/// flagged (inferred placeholder until a word list exists).
/// </summary>
public sealed class PlatformProxyService : IMicroService
{
    public string Name => "PlatformProxyService";

    public ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args) =>
        ValueTask.FromResult<object?>(method == "sensitiveCheck"
            ? new Dictionary<string, object?> { ["Ret"] = MicroServiceRouter.Ret(true), ["Detected"] = false }
            : null);
}

/// <summary>
/// Phonest gate auth (ClientAvatarEntity: PhonestGate.bind {Id, Auth, ClusterId} after the game gate's OnGetAuth RPC,
/// refreshAuth {Id, OldAuth} hourly, whose Ret message is the new secret in base64). bind verifies the secret issued
/// with OnGetAuth and binds this micro connection to its account, as verifySdk does.
/// unbind keeps today's acceptance: the connection stays bound to the account it verified.
/// </summary>
public sealed class PhonestGateService(GateAuthRegistry auths, MicroSessions micro, ILogger<PhonestGateService> log) : IMicroService
{
    public string Name => "PhonestGate";

    public ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args) =>
        ValueTask.FromResult<object?>(method switch
        {
            "bind" => Bind(session, args),
            "unbind" => new Dictionary<string, object?> { ["Ret"] = MicroServiceRouter.Ret(true) },
            "refreshAuth" => Refresh(session, args),
            _ => null,
        });

    private Dictionary<string, object?> Bind(MicroSession session, object? args)
    {
        string? id = MicroServiceRouter.MapString(args, "Id");
        var account = auths.Verify(id, MapBytes(args, "Auth"));
        if (account == null || (session.Account != null && !string.Equals(session.Account, account, StringComparison.Ordinal)))
        {
            log.LogWarning("PhonestGate.bind refused for {Id} from {Remote}", id, session.Channel.Remote);
            return MicroServiceRouter.Fail("bind refused");
        }
        micro.SignIn(session, account);
        log.LogInformation("PhonestGate.bind: {Account} ({Id}) bound {Remote}", account, id, session.Channel.Remote);
        return new() { ["Ret"] = MicroServiceRouter.Ret(true) };
    }

    private Dictionary<string, object?> Refresh(MicroSession session, object? args)
    {
        var fresh = auths.Refresh(MicroServiceRouter.MapString(args, "Id"), MapBytes(args, "OldAuth"), session.Account);
        return fresh == null
            ? MicroServiceRouter.Fail("refresh refused")
            : new() { ["Ret"] = MicroServiceRouter.Ret(true, Convert.ToBase64String(fresh)) };
    }

    private static byte[]? MapBytes(object? args, string key) =>
        args is IDictionary<object, object?> map && map.TryGetValue(key, out var v) ? v as byte[] : null;
}
