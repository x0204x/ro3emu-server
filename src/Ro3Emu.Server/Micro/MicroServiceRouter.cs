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

public sealed partial class MicroServiceRouter(IEnumerable<IMicroService> services, ILogger<MicroServiceRouter> log, Game.GameLoop? loop = null, Game.Ownership.OwnerRegistry? owners = null, Game.SessionRegistry? sessions = null)
{
    private readonly Dictionary<string, IMicroService> _services = services.ToDictionary(s => s.Name);

    internal IReadOnlyCollection<string> ServiceNames => _services.Keys;

    /// <summary>
    /// Every msgpack payload a client sends (micro args, game-gate rpc args, login tokens): collision-resistant map
    /// hashing and a shallow depth limit. The trusted default let a crafted 160 KB map cost ~600 ms of CPU before login.
    /// </summary>
    public static readonly MessagePackSerializerOptions Untrusted = MessagePackSerializerOptions.Standard
        .WithSecurity(MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(32));

    /// <summary>
    /// What a micro connection may call before an account is bound to it: the SDK ticket check (LC_LoginManager's
    /// ConnectToMicroServer -> Phonest LoginAgent.VerifyWithSdk, before the area list, role list or any other
    /// service) and the gate auth of a reconnected connection (ClientAvatarEntity PhonestGate.bind).
    /// </summary>
    private static bool AllowedBeforeSignIn(string service, string method) =>
        (service, method) is ("LoginService", "verifySdk") or ("PhonestGate", "bind");

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Micro {Service}.{Method} from session {Id} refused: no account is bound; later ones not logged")]
    private static partial void LogUnverifiedCall(ILogger logger, string service, string method, long id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Micro {Service}.{Method} {Args}")]
    private static partial void LogCall(ILogger logger, string service, string method, string args);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Micro {Service}.{Method} is not implemented ({Suppressed} more since the last such line)")]
    private static partial void LogNotImplemented(ILogger logger, string service, string method, long suppressed);

    private readonly LogThrottle _unknownLog = new();

    public async ValueTask<byte[]> InvokeAsync(MicroSession session, string service, string method, ReadOnlyMemory<byte> packedArgs)
    {
        if (session.Account == null && !AllowedBeforeSignIn(service, method))
        {
            // Refused before the arguments are read and before any reservation: the client never calls it then.
            if (!session.RefusalLogged)
            {
                session.RefusalLogged = true;
                if (log.IsEnabled(LogLevel.Warning)) LogUnverifiedCall(log, Clip(service), Clip(method), session.Id);
            }
            return MessagePackSerializer.Serialize(Fail("not verified"));
        }
        object? args = packedArgs.IsEmpty ? null : MessagePackSerializer.Deserialize<object>(packedArgs, Untrusted);
        if (log.IsEnabled(LogLevel.Debug))
        {
            string loggedService = Clip(service), loggedMethod = Clip(method), loggedArgs = Describe(args);
            LogCall(log, loggedService, loggedMethod, loggedArgs);
        }

        object? result = null;
        if (_services.TryGetValue(service, out var handler))
        {
            // PhonestGate touches only the micro connection, the locked GateAuthRegistry and the ban read: no Realm admission.
            // RankService only reads committed rows through the read pool and its own cached snapshot, and so does
            // NameService (RoleStore.FindByNameAsync, GetAsync): under the Realm key a flood of getUidByName waited
            // behind, and held up, every realm-wide operation of the loop. ClusterRecommendService and PlatformProxyService
            // answer from options alone. PartyService only reads: it goes to the loop itself for the team and tavern
            // directories (their owner) and to the read pool for chat groups, and changes nothing.
            if (loop == null || owners == null || service is "FriendService" or "UserDataService" or "MailService" or "PhonestGate"
                or "RankService" or "NameService" or "ClusterRecommendService" or "PlatformProxyService" or "PartyService")
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
            if (_unknownLog.Allow(out long suppressed) && log.IsEnabled(LogLevel.Warning))
                LogNotImplemented(log, Clip(service), Clip(method), suppressed);
            result = Fail($"{Clip(service)}.{Clip(method)} not implemented");
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

    /// <summary>Characters of one client text in a log line, and entries of one map or array, before the rest is cut.</summary>
    internal const int LoggedText = 128, LoggedEntries = 16;

    /// <summary>
    /// Client text for a log line: cut to <see cref="LoggedText"/> characters, saying how long it was, with control, line
    /// break and invisible format characters escaped, so that no client can write a line of its own into the log.
    /// </summary>
    public static string Clip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string shown = Printable(text.Length <= LoggedText ? text : text[..LoggedText]);
        return text.Length <= LoggedText ? shown : $"{shown}...({text.Length} chars)";
    }

    private static string Printable(string text)
    {
        if (!text.Any(Unprintable)) return text;
        var shown = new System.Text.StringBuilder(text.Length + 16);
        foreach (char c in text)
            if (Unprintable(c)) shown.Append(System.Globalization.CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
            else shown.Append(c);
        return shown.ToString();
    }

    private static bool Unprintable(char c) => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Control
        or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
        or System.Globalization.UnicodeCategory.ParagraphSeparator;

    /// <summary>The arguments for a log line: bounded in depth, entries and text, secrets redacted.</summary>
    public static string Describe(object? value, int depth = 0) => value switch
    {
        null => "nil",
        byte[] b => $"bin[{b.Length}]",
        string s => $"\"{Clip(s)}\"",
        IDictionary<object, object?> map when depth < 4 => "{" + Entries(map.Select(kv =>
            $"{Clip(kv.Key?.ToString())}={(kv.Key is string key && SecretKeys.Contains(key) ? "<redacted>" : Describe(kv.Value, depth + 1))}"), map.Count) + "}",
        object?[] array when depth < 4 => "[" + Entries(array.Select(v => Describe(v, depth + 1)), array.Length) + "]",
        IDictionary<object, object?> or object?[] => "...",
        _ => Clip(value.ToString()) is { Length: > 0 } text ? text : "?",
    };

    private static string Entries(IEnumerable<string> entries, int count) =>
        string.Join(", ", entries.Take(LoggedEntries)) + (count > LoggedEntries ? $", ...(+{count - LoggedEntries})" : "");
}
