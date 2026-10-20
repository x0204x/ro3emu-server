using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Ro3Emu.LoadTest;

public sealed class LocalServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _stdout;
    private readonly Task _stderr;
    public string Url { get; }
    public string DataDir { get; }
    public int ProcessId => _process.Id;
    public string? CaptureFailure { get; private set; }

    private LocalServer(Process process, Task stdout, Task stderr, string url, string dataDir)
    { _process = process; _stdout = stdout; _stderr = stderr; Url = url; DataDir = dataDir; }

    public static async Task<LocalServer> StartAsync(string repo, string outputDir, CancellationToken ct)
    {
        var dll = Path.Combine(repo, "src", "Ro3Emu.Server", "bin", "Release", "net10.0", "Ro3Emu.Server.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("Build the Release server first: dotnet build src/Ro3Emu.Server -c Release", dll);
        string runId = Guid.NewGuid().ToString("N");
        string data = Path.Combine(repo, ".loadtest", "server-" + runId);
        Directory.CreateDirectory(data);
        // MsgCatalog reads DataDir, while game tables read GameDataDir. Preserve normal traffic-log
        // decoding in the fresh database directory; the shared data junction is only read.
        File.Copy(Path.Combine(repo, "data", "msgtable.tsv"), Path.Combine(data, "msgtable.tsv"));
        int http = FreePort(), micro = FreePort(), game = FreePort();
        while (micro == http) micro = FreePort();
        while (game == http || game == micro) game = FreePort();
        string url = $"http://127.0.0.1:{http}";
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(dll);
        start.Environment["Ro3Emu__PublicHost"] = "127.0.0.1";
        start.Environment["Ro3Emu__BindAddress"] = "127.0.0.1";
        start.Environment["Ro3Emu__HttpPort"] = http.ToString();
        start.Environment["Ro3Emu__MicroGatePort"] = micro.ToString();
        start.Environment["Ro3Emu__GameGatePort"] = game.ToString();
        start.Environment["Ro3Emu__DataDir"] = data;
        start.Environment["Ro3Emu__GameDataDir"] = Path.Combine(repo, "data"); // shared junction: read only
        // The measured configuration is production's: tables parsed before admission, no traffic log.
        start.Environment["Ro3Emu__PreloadGameData"] = "true";
        start.Environment["Ro3Emu__TrafficLog"] = "off";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        Directory.CreateDirectory(outputDir);
        Process? process = null;
        LocalServer? result = null;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Could not start server process");
            process.PriorityClass = ProcessPriorityClass.Normal;
            var stdout = CaptureAsync(process.StandardOutput, Path.Combine(outputDir, $"server-{runId}.stdout.log"));
            var stderr = CaptureAsync(process.StandardError, Path.Combine(outputDir, $"server-{runId}.stderr.log"));
            result = new LocalServer(process, stdout, stderr, url, data);
            using var httpClient = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(2) };
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromMinutes(2))
            {
                ct.ThrowIfCancellationRequested();
                if (stdout.IsFaulted || stderr.IsFaulted) throw new IOException("Local server log capture failed", (stdout.Exception ?? stderr.Exception));
                if (process.HasExited) throw new InvalidOperationException($"Server exited {process.ExitCode}; see server-{runId}.stderr.log");
                try
                {
                    using var response = await httpClient.GetAsync("/ro3emu/metrics", ct);
                    if (response.IsSuccessStatusCode && JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["schemaVersion"]?.GetValue<int>() == 1)
                        return result;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
                await Task.Delay(250, ct);
            }
            throw new TimeoutException("Local server metrics endpoint did not become ready in 2 minutes");
        }
        catch
        {
            if (result != null) await result.DisposeAsync();
            else if (process != null)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                process.Dispose();
            }
            throw;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private static async Task CaptureAsync(StreamReader reader, string path)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await reader.BaseStream.CopyToAsync(file);
    }
    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        try { await Task.WhenAll(_stdout, _stderr).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) { CaptureFailure = e.Message; }
        _process.Dispose();
    }
}
