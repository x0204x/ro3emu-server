using System.Text;

namespace Ro3Emu.Setup;

/// <summary>
/// The server's output in logs\server-*.log: a new file once one holds <see cref="MaxBytes"/>, and only the newest
/// <see cref="KeepFiles"/> files are kept, so a flood of log lines cannot fill the disk. Thread safe.
/// </summary>
internal sealed class RollingLog : IDisposable
{
    internal const long MaxBytes = 50L * 1024 * 1024;
    internal const int KeepFiles = 5;

    private readonly string _dir;
    private readonly string _stamp;
    private readonly long _maxBytes;
    private readonly int _keep;
    private readonly object _gate = new();
    private StreamWriter _writer;
    private int _part = 1;

    public string Path { get; private set; }

    public RollingLog(string dir, long maxBytes = MaxBytes, int keep = KeepFiles)
    {
        _dir = dir;
        _maxBytes = maxBytes;
        _keep = keep;
        _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(dir);
        (_writer, Path) = Open();
    }

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_writer.BaseStream.Length >= _maxBytes)
            {
                _writer.Dispose();
                _part++;
                (_writer, Path) = Open();
            }
            _writer.WriteLine(line);
        }
    }

    public void Flush() { lock (_gate) _writer.Flush(); }

    public void Dispose() { lock (_gate) _writer.Dispose(); }

    private (StreamWriter, string) Open()
    {
        string path = System.IO.Path.Combine(_dir, _part == 1 ? $"server-{_stamp}.log" : $"server-{_stamp}-{_part}.log");
        var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        Prune(path);
        return (writer, path);
    }

    /// <summary>Deletes all but the newest <see cref="KeepFiles"/> server logs (the one just opened always stays).</summary>
    private void Prune(string current)
    {
        var older = new DirectoryInfo(_dir).GetFiles("server-*.log")
            .Where(f => !string.Equals(f.FullName, System.IO.Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).Skip(_keep - 1);
        foreach (var old in older)
        {
            // Another RO3Emu.exe may still be writing it: that one stays until a later start.
            try { old.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
