using Google.Protobuf;
using MessagePack;
using Phonest.Proto;

namespace Ro3Emu.Server.Micro;

/// <summary>
/// Verified micro-gate sessions by account, and server pushes to them: frame 35 MsPush{org_type "WORLD", service, method,
/// params = msgpack array}. The client routes a push by method name to the same DAL handlers as a response, with req = nil
/// ("WORLD" reaches the avatar entity, other org types need ep_ids).
/// </summary>
public sealed class MicroSessions(ILogger<MicroSessions> log)
{
    private const ushort MicroPush = 35;

    private readonly object _sync = new();
    private readonly Dictionary<string, MicroSession> _byAccount = new(StringComparer.Ordinal);

    /// <summary>
    /// The one account transition of a micro connection; nothing else sets <see cref="MicroSession.Account"/>. Binds the
    /// account (the newest connection of an account receives its pushes); another account on the same connection first
    /// drops the old account's key, so pushes for it never reach this connection.
    /// </summary>
    public void SignIn(MicroSession session, string account)
    {
        lock (_sync)
        {
            if (session.Account != null && !string.Equals(session.Account, account, StringComparison.Ordinal)
                && _byAccount.TryGetValue(session.Account, out var current) && current == session)
                _byAccount.Remove(session.Account);
            session.Account = account;
            _byAccount[account] = session;
        }
    }

    /// <summary>Tests: the session an account is bound to.</summary>
    internal MicroSession? For(string account) { lock (_sync) return _byAccount.GetValueOrDefault(account); }

    public void Remove(MicroSession session)
    {
        lock (_sync)
            if (session.Account != null && _byAccount.TryGetValue(session.Account, out var current) && current == session) _byAccount.Remove(session.Account);
    }

    /// <summary>Pushes <paramref name="args"/> (never empty: a zero-length body is unpacked unsafely) to the account's client.</summary>
    public async Task PushAsync(string account, string service, string method, params object?[] args)
    {
        MicroSession? session;
        lock (_sync) _byAccount.TryGetValue(account, out session);
        if (session == null) return;
        var push = new MsPush
        {
            OrgType = "WORLD", MsgUuid = Guid.NewGuid().ToString("N"), Service = service, Method = method,
            Params = ByteString.CopyFrom(MessagePackSerializer.Serialize<object?[]>(args.Length == 0 ? [true] : args)),
        };
        try
        {
            await session.Channel.SendAsync(MicroPush, push);
            log.LogInformation("Micro push {Service}.{Method} -> {Account}", service, method, account);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            Remove(session);
        }
    }
}
