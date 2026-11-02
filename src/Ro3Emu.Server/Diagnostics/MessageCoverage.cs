using System.Collections.Concurrent;
using System.Text.Json;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>
/// Protocol coverage evidence for coverage reports. With RO3EMU_MSG_COVERAGE=&lt;file&gt; (set for a full test run) the
/// process records every game route registered, every client message handled or answered only by the unrouted empty
/// reply, and every message actually sent; at exit it merges them into the file. Off (one null check) otherwise.
/// </summary>
public static class MessageCoverage
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("RO3EMU_MSG_COVERAGE");
    private static readonly ConcurrentDictionary<string, string> Routes = new();
    private static readonly ConcurrentDictionary<uint, long> Handled = new(), Unrouted = new(), Sent = new();

    static MessageCoverage()
    {
        if (Path != null) AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    public static bool Enabled => Path != null;

    public static void Route(uint from, uint to, string owner)
    {
        if (Path != null) Routes[from == to ? from.ToString() : $"{from}-{to}"] = owner;
    }

    /// <summary>A client message reached the game dispatcher.</summary>
    public static void Received(uint msgId)
    {
        if (Path != null) Handled.AddOrUpdate(msgId, 1, (_, n) => n + 1);
    }

    /// <summary>A client message only got the generic empty reply (no owner).</summary>
    public static void Unanswered(uint msgId)
    {
        if (Path != null) Unrouted.AddOrUpdate(msgId, 1, (_, n) => n + 1);
    }

    public static void Outbound(uint msgId)
    {
        if (Path != null) Sent.AddOrUpdate(msgId, 1, (_, n) => n + 1);
    }

    private sealed class Evidence
    {
        public Dictionary<string, string> Routes { get; set; } = [];
        public Dictionary<string, long> Handled { get; set; } = [];
        public Dictionary<string, long> Unrouted { get; set; } = [];
        public Dictionary<string, long> Sent { get; set; } = [];
    }

    /// <summary>Merges this process's evidence into the file (counts add up across runs until the file is deleted).</summary>
    public static void Flush()
    {
        if (Path == null) return;
        try
        {
            using var gate = new Mutex(false, "ro3emu-msg-coverage");
            gate.WaitOne(TimeSpan.FromSeconds(10));
            try
            {
                var evidence = File.Exists(Path) ? JsonSerializer.Deserialize<Evidence>(File.ReadAllText(Path)) ?? new() : new();
                foreach (var (key, owner) in Routes) evidence.Routes[key] = owner;
                Add(evidence.Handled, Handled);
                Add(evidence.Unrouted, Unrouted);
                Add(evidence.Sent, Sent);
                File.WriteAllText(Path, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception e) { Console.Error.WriteLine($"MessageCoverage: could not write {Path}: {e.Message}"); }
    }

    private static void Add(Dictionary<string, long> into, ConcurrentDictionary<uint, long> from)
    {
        foreach (var (id, count) in from) into[id.ToString()] = into.GetValueOrDefault(id.ToString()) + count;
    }
}
