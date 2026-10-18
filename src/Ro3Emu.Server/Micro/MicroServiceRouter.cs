using MessagePack;

namespace Ro3Emu.Server.Micro;

/// <summary>A micro service reachable as MsRequest{service, method, params(msgpack)}.</summary>
public interface IMicroService
{
    string Name { get; }

    /// <summary>
    /// Returns the msgpack-serializable response, or null when the method is unknown. <paramref name="args"/> is the
    /// decoded params: a map for Phonest DTOs ([MessagePackObject(true)]), an object[] for Lua cmsgpack calls.
    /// </summary>
    ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args);
}

public sealed class MicroServiceRouter(IEnumerable<IMicroService> services, ILogger<MicroServiceRouter> log, Game.GameLoop? loop = null, Game.Ownership.OwnerRegistry? owners = null, Game.SessionRegistry? sessions = null)
{
    private readonly Dictionary<string, IMicroService> _services = services.ToDictionary(s => s.Name);

    /// <summary>
    /// Every msgpack payload a client sends (micro args, game-gate rpc args, login tokens): collision-resistant map
    /// hashing and a shallow depth limit. The trusted default let a crafted 160 KB map cost ~600 ms of CPU before login.
    /// </summary>
    public static readonly MessagePackSerializerOptions Untrusted = MessagePackSerializerOptions.Standard
        .WithSecurity(MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(32));

    public async ValueTask<byte[]> InvokeAsync(MicroSession session, string service, string method, ReadOnlyMemory<byte> packedArgs)
    {
        object? args = packedArgs.IsEmpty ? null : MessagePackSerializer.Deserialize<object>(packedArgs, Untrusted);
        log.LogInformation("Micro {Service}.{Method} {Args}", service, method, Describe(args));

        object? result = null;
        if (_services.TryGetValue(service, out var handler))
        {
            // PhonestGate touches only the micro connection and the locked GateAuthRegistry: no Realm admission.
            // RankService only reads committed rows through the read pool and its own cached snapshot.
            if (loop == null || owners == null || service is "FriendService" or "UserDataService" or "MailService" or "PhonestGate" or "RankService")
                result = await handler.InvokeAsync(session, method, args);
            else result = await loop.InvokeAsync(async () =>
            {
                var selected = sessions?.All().FirstOrDefault(s => string.Equals(s.Account, session.Account, StringComparison.Ordinal));
                var selectedRole = selected?.Role?.Id;
                var selectedLease = selected?.Attachment;
                // Before verifySdk binds an account an unverified session reserves only itself. One shared key for
                // every unverified session serialised all first logins behind each other's token resolution (S4 on
                // 10.10: 210 waits, up to 2.5 s each).
                Game.Ownership.ReservationKey[] keys = service == "LoginService"
                    ? [Game.Ownership.ReservationKey.Account(session.Account ?? "micro-session:" + session.Id)]
                    : service == "MailService" && selected?.Role is { } role
                        ? [Game.Ownership.ReservationKey.Role(role.Id)] : [Game.Ownership.ReservationKey.Realm];
                using var scope = await owners.EnterAsync("micro/" + service, keys);
                if (service == "MailService" && (selected?.Role?.Id != selectedRole ||
                    !ReferenceEquals(selected?.Attachment, selectedLease) ||
                    !ReferenceEquals(selected, sessions?.All().FirstOrDefault(s => string.Equals(s.Account, session.Account, StringComparison.Ordinal)))))
                    return Fail("no selected role");
                return await handler.InvokeAsync(session, method, args);
            });
        }
        if (result == null)
        {
            log.LogWarning("Micro {Service}.{Method} is not implemented", service, method);
            result = Fail($"{service}.{method} not implemented");
        }
        return MessagePackSerializer.Serialize(result);
    }

    /// <summary>The "Ret": [ok, message] pair every Phonest MicroResponse carries.</summary>
    public static object[] Ret(bool ok, string message = "") => [ok, message];

    public static Dictionary<string, object?> Fail(string message) => new() { ["Ret"] = Ret(false, message) };

    /// <summary>String value of a map key, for Phonest DTO args.</summary>
    public static string? MapString(object? args, string key) =>
        args is IDictionary<object, object?> map && map.TryGetValue(key, out var v) ? v as string : null;

    /// <summary>Argument keys that carry login secrets (verifySdk Ticket, PhonestGate Auth, engine Sign/Token...).
    /// Logs and the diagnostics zip must never hold a live one.</summary>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ticket", "Token", "Sign", "Auth", "OldAuth", "NewAuth", "Password", "Credential", "Secret", "SessionKey",
        "LoginToken", "MixedLoginToken", "AccessToken", "RefreshToken",
    };

    public static string Describe(object? value, int depth = 0) => value switch
    {
        null => "nil",
        byte[] b => $"bin[{b.Length}]",
        string s => $"\"{s}\"",
        IDictionary<object, object?> map when depth < 4 => "{" + string.Join(", ", map.Select(kv =>
            $"{kv.Key}={(kv.Key is string key && SecretKeys.Contains(key) ? "<redacted>" : Describe(kv.Value, depth + 1))}")) + "}",
        object?[] array when depth < 4 => "[" + string.Join(", ", array.Select(v => Describe(v, depth + 1))) + "]",
        _ => value.ToString() ?? "?",
    };
}
