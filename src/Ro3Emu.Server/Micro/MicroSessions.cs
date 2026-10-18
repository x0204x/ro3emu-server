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

    public void Bind(MicroSession session)
    {
        lock (_sync) _byAccount[session.Account!] = session;
    }

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
