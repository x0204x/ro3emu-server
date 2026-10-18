using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using Google.Protobuf;

namespace Ro3Emu.Server.Net;

/// <summary>
/// Server side of one Phonest RPC connection (Phonest.Rpc.RpcChannel/ProtobufChannel).
/// Frame: int32 LE length (excludes itself) | uint16 LE rpc index | protobuf body.
/// After the encryption handshake both directions run through a <see cref="StreamConvertor"/>.
/// </summary>
public sealed class PhonestChannel : IAsyncDisposable
{
    public delegate ValueTask FrameHandler(PhonestChannel channel, ushort index, ReadOnlyMemory<byte> body);

    /// <summary>Largest accepted client frame. The client's biggest requests (settings blobs, chat) are a few KB.</summary>
    private const int MaxFrame = 1024 * 1024;
    /// <summary>
    /// Bytes queued for one connection's writer before it counts as a stalled reader. Past it the connection is closed
    /// (reconnect = full resync) rather than letting the queue grow without bound or dropping reliable messages.
    /// </summary>
    private const long MaxQueuedBytes = 8L * 1024 * 1024;
    /// <summary>Soft cap of one coalesced socket send (a frame is never split across batches).</summary>
    private const int MaxSendBatch = 256 * 1024;

    private readonly Socket _socket;
    private readonly ILogger _log;
    /// <summary>Frames waiting for the writer; sends never block the caller (the game loop).</summary>
    private readonly Channel<(byte[] Frame, StreamConvertor? Convertor)> _outbound =
        Channel.CreateUnbounded<(byte[], StreamConvertor?)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly MemoryStream _pending = new();
    private long _queuedBytes;
    private int _overflowed;
    private int _closing;
    private StreamConvertor? _convertor;
    private bool _inboundConverted;
    private bool _switchInboundAfterCurrentFrame;
    private bool _plaintextNextSend;

    public PhonestChannel(Socket socket, ILogger log)
    {
        _socket = socket;
        _log = log;
        Remote = socket.RemoteEndPoint?.ToString() ?? "?";
        var local = (socket.LocalEndPoint as System.Net.IPEndPoint)?.Address;
        LocalAddress = local is { IsIPv4MappedToIPv6: true } ? local.MapToIPv4() : local;
        _writer = Task.Run(WriteLoopAsync);
    }

    public string Remote { get; }

    /// <summary>This side's address of the connection (the interface the client reached us on); null for a socket
    /// that is not connected. IPv4-mapped addresses of a dual-mode socket are unwrapped to plain IPv4.</summary>
    public System.Net.IPAddress? LocalAddress { get; }
    public Diagnostics.ConnectionMetrics? Metrics { get; init; }

    /// <summary>Nonblocking close observation while frame dispatch is awaiting game-loop selection.
    /// It never reads from the socket or changes frame order.</summary>
    public bool PeerClosed
    {
        get
        {
            if (Volatile.Read(ref _closing) != 0) return true;
            try { return _socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0; }
            catch (SocketException) { return true; }
            catch (ObjectDisposedException) { return true; }
        }
    }

    /// <summary>Arbitrary per-connection state owned by the gate that accepted it.</summary>
    public object? Session { get; set; }

    /// <summary>The encryption handshake (confirmEncryptKey) completed on this connection.</summary>
    public bool Encrypted => _convertor != null;

    /// <summary>
    /// Switches the channel to compressed + RC4 mode. Inbound conversion applies to bytes after the frame that is being
    /// handled right now (the client's plaintext confirmEncryptKey); outbound applies from the next send on.
    /// </summary>
    /// <param name="plaintextNextSend">
    /// Client side: Phonest's RpcChannel only converts sends after the one that follows EnableConvertor
    /// (the client's confirmEncryptKey goes out in plaintext).
    /// </param>
    public void EnableConvertor(byte[] sessionKey, bool plaintextNextSend = false)
    {
        _convertor = new StreamConvertor(sessionKey);
        _switchInboundAfterCurrentFrame = true;
        _plaintextNextSend = plaintextNextSend;
    }

    public async Task RunAsync(FrameHandler handler, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            int n = await _socket.ReceiveAsync(buf, SocketFlags.None, ct);
            Metrics?.ReceivedBytes(n);
            if (n == 0) { Volatile.Write(ref _closing, 1); return; }
            if (_inboundConverted)
            {
                var plain = _convertor!.ConvertInput(buf.AsSpan(0, n), MaxFrame + 6);
                _pending.Write(plain);
            }
            else
            {
                _pending.Write(buf, 0, n);
            }
            await DrainFramesAsync(handler);
        }
    }

    /// <summary>
    /// Hands every complete frame in <see cref="_pending"/> to the handler, reading with a cursor and compacting the
    /// unread tail once at the end (a burst of N frames is one copy, not N copies of the remainder).
    /// </summary>
    private async Task DrainFramesAsync(FrameHandler handler)
    {
        int offset = 0;
        try
        {
            while (true)
            {
                var data = _pending.GetBuffer();
                int available = (int)_pending.Length - offset;
                if (available < 4) return;
                int size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
                if (size < 2 || size > MaxFrame) throw new InvalidDataException($"bad frame size {size} from {Remote}");
                if (available < 4 + size) return;

                ushort index = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4));
                var body = data.AsSpan(offset + 6, size - 2).ToArray();
                offset += 4 + size;

                Metrics?.ReceivedFrame();
                await handler(this, index, body);

                if (_switchInboundAfterCurrentFrame)
                {
                    // Bytes the client sent after its plaintext key frame are already compressed + encrypted.
                    _switchInboundAfterCurrentFrame = false;
                    _inboundConverted = true;
                    var rest = _pending.GetBuffer().AsSpan(offset, (int)_pending.Length - offset).ToArray();
                    _pending.SetLength(0);
                    offset = 0;
                    if (rest.Length > 0) _pending.Write(_convertor!.ConvertInput(rest, MaxFrame + 6));
                }
            }
        }
        finally
        {
            if (offset > 0)
            {
                var data = _pending.GetBuffer();
                int remaining = (int)_pending.Length - offset;
                Buffer.BlockCopy(data, offset, data, 0, remaining);
                _pending.SetLength(remaining);
            }
            _pending.Position = _pending.Length; // RunAsync appends the next received chunk
        }
    }

    public Task SendAsync(ushort index, IMessage message) => SendRawAsync(index, message.ToByteArray());

    /// <summary>
    /// Queues a frame. Whether it is converted is decided now (sends are ordered with EnableConvertor); the RC4 /
    /// deflate state is advanced by the writer in the same order.
    /// </summary>
    public Task SendRawAsync(ushort index, ReadOnlyMemory<byte> body) => SendFrameAsync(Frame(index, body.Span));

    /// <summary>A whole frame: 4-byte length (index + body), 2-byte index, body.</summary>
    public static byte[] Frame(ushort index, ReadOnlySpan<byte> body)
    {
        var frame = new byte[6 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length + 2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), index);
        body.CopyTo(frame.AsSpan(6));
        return frame;
    }

    /// <summary>Queues a built frame. The writer only reads it, so one frame may be queued to many connections
    /// (an AOI broadcast built once instead of once per viewer).</summary>
    public Task SendFrameAsync(byte[] frame)
    {
        if (Interlocked.Add(ref _queuedBytes, frame.Length) > MaxQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -frame.Length);
            if (Interlocked.Exchange(ref _overflowed, 1) == 0)
            {
                _log.LogWarning("{Remote}: {Bytes} bytes queued, the client is not reading; resetting the connection",
                    Remote, Interlocked.Read(ref _queuedBytes));
                // A reset, not a FIN: the FIN would wait behind the kernel's send buffer, which a client that stopped
                // reading drains at a trickle (minutes on Linux with a large buffer and a small receive window).
                AbortReset();
            }
            return Task.CompletedTask;
        }
        lock (_outbound)
        {
            var convertor = _plaintextNextSend ? null : _convertor;
            _plaintextNextSend = false;
            // The stamp goes in before the frame: the writer may send the frame as soon as it is queued, and its
            // completion must find the stamp (a late stamp made drain waits stall a frame behind until timeout).
            Metrics?.Enqueued(frame.Length);
            if (!_outbound.Writer.TryWrite((frame, convertor))) Interlocked.Add(ref _queuedBytes, -frame.Length);
        }
        return Task.CompletedTask;
    }

    /// <summary>Bytes waiting for the writer (not yet handed to the socket).</summary>
    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    /// <summary>Stops sending and makes the reader see the end of the connection, so the gate tears the session down.</summary>
    public void Abort()
    {
        Volatile.Write(ref _closing, 1);
        _outbound.Writer.TryComplete();
        try { _socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
    }

    /// <summary>Like <see cref="Abort"/>, but resets the connection (RST, linger 0) instead of a graceful FIN.</summary>
    public void AbortReset()
    {
        Volatile.Write(ref _closing, 1);
        _outbound.Writer.TryComplete();
        try
        {
            _socket.LingerState = new LingerOption(true, 0);
            _socket.Close();
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            var batch = new MemoryStream();
            var reader = _outbound.Reader;
            while (await reader.WaitToReadAsync())
            {
                // Everything already queued goes out in one socket send. Each frame is still converted on its own
                // (its own deflate sync flush and RC4 advance, in queue order), so the byte stream is exactly the
                // one per-frame sends produced; only the number of send calls drops.
                batch.SetLength(0);
                int frames = 0;
                while (batch.Length < MaxSendBatch && reader.TryRead(out var item))
                {
                    Interlocked.Add(ref _queuedBytes, -item.Frame.Length);
                    if (item.Convertor != null) item.Convertor.ConvertOutput(item.Frame, batch);
                    else batch.Write(item.Frame);
                    frames++;
                }
                var bytes = batch.GetBuffer().AsMemory(0, (int)batch.Length);
                int calls = 0, partial = 0;
                for (int sent = 0; sent < bytes.Length;)
                {
                    int n = await _socket.SendAsync(bytes[sent..], SocketFlags.None);
                    if (n <= 0) throw new SocketException((int)SocketError.ConnectionReset);
                    calls++;
                    if (n < bytes.Length - sent) partial++;
                    sent += n; // partial sends continue with the converted bytes, never converting again
                }
                Metrics?.SentBatch(frames, bytes.Length, calls, partial);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            Metrics?.WriterFailed();
            _log.LogDebug("send to {Remote} stopped: {Error}", Remote, e.Message);
        }
        catch (Exception e)
        {
            Metrics?.WriterFailed();
            _log.LogWarning(e, "send to {Remote} failed", Remote);
        }
        finally
        {
            // A dead writer must not leave the session queuing into the void: end the connection for the reader too.
            Abort();
        }
    }

    /// <summary>Flushes what is queued (a kick message, say), then closes the socket.</summary>
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _closing, 1);
        _outbound.Writer.TryComplete();
        await Task.WhenAny(_writer, Task.Delay(TimeSpan.FromSeconds(2)));
        try { _socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { } catch (ObjectDisposedException) { }
        _socket.Dispose();
        Metrics?.Closed();
    }
}
