using System.Net;
using System.Net.Sockets;

if (!Options.TryParse(args, out var options))
{
    Console.Error.WriteLine("usage: Ro3Emu.RepositoryServer <repository directory> --bind <ip> [--port 8080] [--path /ro3/]");
    return 2;
}

using var listener = new HttpListener();
listener.Prefixes.Add(options.Prefix);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); listener.Stop(); };
try
{
    listener.Start();
}
catch (HttpListenerException e)
{
    Console.Error.WriteLine($"cannot listen on {options.Prefix}: {e.Message}");
    return 3;
}

Console.WriteLine($"ready: {options.Prefix} -> {options.Root}");
while (!stop.IsCancellationRequested)
{
    try { _ = ServeAsync(await listener.GetContextAsync(), options); }
    catch (HttpListenerException) when (stop.IsCancellationRequested) { }
    catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
}
return 0;

static async Task ServeAsync(HttpListenerContext context, Options options)
{
    try
    {
        if (context.Request.HttpMethod is not ("GET" or "HEAD"))
        {
            context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            context.Response.Headers[HttpResponseHeader.Allow] = "GET, HEAD";
            return;
        }
        string? file = Resolve(context.Request.Url, options);
        if (file == null || !File.Exists(file)) { context.Response.StatusCode = (int)HttpStatusCode.NotFound; return; }
        await SendAsync(context, file, options, context.Request.HttpMethod == "HEAD");
    }
    catch (Exception e)
    {
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        Console.Error.WriteLine($"request failed: {e.Message}");
    }
    finally { context.Response.Close(); }
}

static string? Resolve(Uri? url, Options options)
{
    string path = url?.AbsolutePath ?? "";
    if (!path.StartsWith(options.Path, StringComparison.Ordinal)) return null;
    string relative;
    try { relative = Uri.UnescapeDataString(path[options.Path.Length..]); }
    catch (UriFormatException) { return null; }
    if (relative.Length == 0 || relative.Contains('\0')) return null;
    string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(options.Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
    return full.StartsWith(options.RootPrefix, StringComparison.OrdinalIgnoreCase) ? full : null;
}

static async Task SendAsync(HttpListenerContext context, string file, Options options, bool head)
{
    var info = new FileInfo(file);
    long start;
    long count;
    bool partial;
    try
    {
        (start, count, partial) = Range(context.Request.Headers["Range"], info.Length);
    }
    catch (InvalidDataException)
    {
        context.Response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
        context.Response.Headers[HttpResponseHeader.ContentRange] = $"bytes */{info.Length}";
        return;
    }
    context.Response.StatusCode = partial ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
    context.Response.ContentType = ContentType(info.Extension);
    context.Response.ContentLength64 = count;
    context.Response.Headers[HttpResponseHeader.AcceptRanges] = "bytes";
    context.Response.Headers[HttpResponseHeader.CacheControl] = CacheControl(file, options);
    if (partial) context.Response.Headers[HttpResponseHeader.ContentRange] = $"bytes {start}-{start + count - 1}/{info.Length}";
    if (head) return;

    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    input.Position = start;
    await CopyAsync(input, context.Response.OutputStream, count);
}

static (long Start, long Count, bool Partial) Range(string? header, long length)
{
    if (string.IsNullOrEmpty(header)) return (0, length, false);
    if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
        throw new InvalidDataException("unsupported byte range");
    string[] bounds = header[6..].Split('-', 2);
    if (!long.TryParse(bounds[0], out long start) || start < 0 || start >= length)
        throw new InvalidDataException("invalid byte range");
    long end = bounds.Length == 2 && long.TryParse(bounds[1], out long requested) ? Math.Min(requested, length - 1) : length - 1;
    if (end < start) throw new InvalidDataException("invalid byte range");
    return (start, end - start + 1, true);
}

static async Task CopyAsync(Stream input, Stream output, long count)
{
    byte[] buffer = new byte[1024 * 1024];
    while (count > 0)
    {
        int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)));
        if (read == 0) throw new EndOfStreamException();
        await output.WriteAsync(buffer.AsMemory(0, read));
        count -= read;
    }
}

static string ContentType(string extension) => extension.ToLowerInvariant() switch
{
    ".json" => "application/json; charset=utf-8",
    ".exe" => "application/vnd.microsoft.portable-executable",
    ".pem" => "application/x-pem-file",
    _ => "application/octet-stream",
};

static string CacheControl(string file, Options options)
{
    string relative = System.IO.Path.GetRelativePath(options.Root, file).Replace('\\', '/');
    return relative.StartsWith("channels/", StringComparison.OrdinalIgnoreCase) || relative.Equals("RO3Launcher.exe", StringComparison.OrdinalIgnoreCase)
        ? "no-cache" : "public, max-age=31536000, immutable";
}

internal sealed record Options(string Root, string RootPrefix, IPAddress Bind, int Port, string Path, string Prefix)
{
    public static bool TryParse(string[] args, out Options value)
    {
        value = null!;
        if (args.Length < 3 || !Directory.Exists(args[0])) return false;
        string? bind = null;
        string path = "/ro3/";
        int port = 8080;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--bind" && ++i < args.Length) bind = args[i];
            else if (args[i] == "--port" && ++i < args.Length && int.TryParse(args[i], out int parsed)) port = parsed;
            else if (args[i] == "--path" && ++i < args.Length) path = args[i];
            else return false;
        }
        if (!IPAddress.TryParse(bind, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort || !ValidPath(path)) return false;
        string root = System.IO.Path.GetFullPath(args[0]);
        string rootPrefix = root.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        string host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        value = new(root, rootPrefix, address, port, path, $"http://{host}:{port}{path}");
        return true;
    }

    private static bool ValidPath(string path) => path.Length > 2 && path[0] == '/' && path[^1] == '/'
        && !path.Contains("//", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal);
}
