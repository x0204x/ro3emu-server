using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Ro3Emu.Setup;

/// <summary>
/// The bundled server (server\Ro3Emu.Server.exe, self-contained) as a child process: its output goes to
/// logs\server-*.log, it shares this console so Ctrl+C or closing the window reaches it too, RO3Emu.exe stops it with
/// POST /ro3emu/shutdown, and a job object kills it if RO3Emu.exe goes away any other way.
/// </summary>
internal sealed class ServerProcess : IDisposable
{
    /// <summary>The server's TCP ports. <paramref name="Voice"/> 0 = the party voice relay is off (the default).</summary>
    public sealed record Ports(int Http = 5380, int MicroGate = 20000, int GameGate = 33100, int Voice = 0)
    {
        /// <summary>Every port the server listens on, voice only when it is on.</summary>
        public IEnumerable<int> All() => Voice > 0 ? [Http, MicroGate, GameGate, Voice] : [Http, MicroGate, GameGate];
    }

    /// <summary>"5380, 20000 and 33100" (with the voice port when it is on): what a host opens or forwards.</summary>
    public static string PortList(Ports ports)
    {
        var all = ports.All().Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        return string.Join(", ", all[..^1]) + " and " + all[^1];
    }

    /// <summary>The server's own arguments: data folder and ports (the voice relay only when a voice port is set).</summary>
    public static List<string> ServerArgs(string dataDir, Ports ports)
    {
        var args = new List<string>
        {
            $"--Ro3Emu:DataDir={dataDir}",
            "--Ro3Emu:PreloadGameData=true",
            $"--Ro3Emu:HttpPort={ports.Http}",
            $"--Ro3Emu:MicroGatePort={ports.MicroGate}",
            $"--Ro3Emu:GameGatePort={ports.GameGate}",
        };
        if (ports.Voice > 0) args.Add($"--Ro3Emu:Voice:Port={ports.Voice}");
        return args;
    }

    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly IntPtr _job;

    public string LogPath { get; }
    public Ports UsedPorts { get; }
    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;

    private ServerProcess(Process process, StreamWriter log, string logPath, IntPtr job, Ports ports)
    {
        _process = process;
        _log = log;
        LogPath = logPath;
        _job = job;
        UsedPorts = ports;
    }

    /// <summary>server\Ro3Emu.Server(.exe) next to RO3Emu.exe; for developers the repository's Release or Debug build.</summary>
    public static string? FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "Ro3Emu.Server.exe" : "Ro3Emu.Server";
        if (SetupHome.Shipped("server", name) is { } shipped) return shipped;
        if (SetupHome.RepositoryRoot() is { } repo)
            foreach (var config in new[] { "Release", "Debug" })
            {
                var dir = Path.Combine(repo, "src", "Ro3Emu.Server", "bin", config, "net10.0");
                if (File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
            }
        return null;
    }

    /// <summary>A port something else already listens on (on any address), or null.</summary>
    public static int? BusyPort(Ports ports)
    {
        foreach (var port in ports.All())
        {
            try
            {
                using var probe = new TcpListener(System.Net.IPAddress.Loopback, port);
                probe.ExclusiveAddressUse = true;
                probe.Start();
                probe.Stop();
            }
            catch (SocketException) { return port; }
        }
        return null;
    }

    public static ServerProcess Start(string exe, string dataDir, Ports ports, IEnumerable<string> extraArgs, IDictionary<string, string> environment)
    {
        Directory.CreateDirectory(SetupHome.LogDir);
        var logPath = Path.Combine(SetupHome.LogDir, $"server-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in ServerArgs(dataDir, ports).Concat(extraArgs)) start.ArgumentList.Add(a);
        foreach (var (k, v) in environment) start.Environment[k] = v;
        // The bundled server is self-contained; never let a machine-wide .NET or a developer variable steer it.
        start.Environment.Remove("DOTNET_ROOT");
        start.Environment["DOTNET_gcServer"] = "0";
        SetupLog.Write($"server: {exe} {string.Join(' ', start.ArgumentList)}");

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        void Line(object _, DataReceivedEventArgs e) { if (e.Data != null) lock (log) log.WriteLine(e.Data); }
        process.OutputDataReceived += Line;
        process.ErrorDataReceived += Line;
        if (!process.Start()) throw new SetupException("The server did not start.", "Run RO3Emu.exe diag and attach the zip to an issue.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var job = OperatingSystem.IsWindows() ? KillOnCloseJob(process) : IntPtr.Zero;
        return new ServerProcess(process, log, logPath, job, ports);
    }

    /// <summary>Waits until the server answers HTTP (it preloads the game data first), or it exits, or the time runs out.</summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout, Action<TimeSpan>? tick = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (_process.HasExited) return false;
            if (await Answers(http, UsedPorts.Http)) return true;
            tick?.Invoke(watch.Elapsed);
            await Task.Delay(1000);
        }
        return false;
    }

    public static async Task<bool> Answers(HttpClient http, int httpPort)
    {
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{httpPort}/ro3emu/gate-key.pem");
            return response.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The last lines of the server's output, for an error message.</summary>
    public string Tail(int lines = 15)
    {
        try
        {
            lock (_log) _log.Flush();
            using var reader = new StreamReader(new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var all = reader.ReadToEnd().Split('\n');
            return string.Join('\n', all.TakeLast(lines)).TrimEnd();
        }
        catch (Exception) { return ""; }
    }

    /// <summary>Stops the server cleanly (POST /ro3emu/shutdown: the loop drains, the database closes), else kills it.</summary>
    public void Stop(TimeSpan grace)
    {
        if (_process.HasExited) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = http.PostAsync($"http://127.0.0.1:{UsedPorts.Http}/ro3emu/shutdown", null).GetAwaiter().GetResult();
            SetupLog.Write($"shutdown: {(int)response.StatusCode}");
        }
        catch (Exception e) { SetupLog.Write("shutdown: " + e.Message); }
        if (!_process.WaitForExit(grace))
        {
            SetupLog.Write("server did not stop in time; killing it");
            try { _process.Kill(entireProcessTree: true); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch (Exception) { }
        _process.Dispose();
        lock (_log) _log.Dispose();
        if (_job != IntPtr.Zero) CloseHandle(_job);
    }

    private static IntPtr KillOnCloseJob(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, 9, ptr, (uint)size) || !AssignProcessToJobObject(job, process.Handle))
                SetupLog.Write("job object: " + Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
