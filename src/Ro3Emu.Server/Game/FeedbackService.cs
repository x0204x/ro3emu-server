using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Diagnostics;

namespace Ro3Emu.Server.Game;

/// <summary>
/// In-game bug reports for the alpha (docs/guide/feedback.md). The client's own Feedback entry (FunctionConfig 41, the
/// function menu) has no panel mapping in this build: LC_FunctionHelper.OpenFunctionPanelDefault finds neither a
/// FunctionSpecialFunc nor a FunctionOpenPanelInfo entry for it and only logs an error. Two ways in:
/// <list type="bullet">
/// <item>the menu button: Ro3Emu.ClientHooks.FeedbackButton maps function 41 to the client's generic text panel
/// (UI_Com_InputContentPanel) and sends the text as game message <see cref="SubmitMsg"/> (UTF-8, no protobuf);</item>
/// <item>chat: "/bug &lt;text&gt;" (or "/feedback &lt;text&gt;") in any channel, never shown to other players.</item>
/// </list>
/// Each report is one JSON line in DataDir/reports/feedback.jsonl with who, where and when, plus the server's recent log
/// lines about that player (RecentLogBuffer). The owner reads them through loopback GET /ro3emu/reports.
/// </summary>
public sealed class FeedbackService(IOptions<ServerOptions> options, GameData data, RecentLogBuffer logs, TimeProvider clock,
    ILogger<FeedbackService> log)
{
    /// <summary>Client hook → server: the report text, UTF-8. Ours (the client's message table has no such id).</summary>
    public const uint SubmitMsg = 77013;

    private static readonly string[] ChatCommands = ["/bug", "/feedback"];

    private readonly IOptions<ServerOptions> _options = options;
    private readonly GameData _data = data;
    private readonly RecentLogBuffer _logs = logs;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<FeedbackService> _log = log;
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);

    public FeedbackStore Store { get; } = new FeedbackStore(Path.Combine(options.Value.DataDir, "reports", "feedback.jsonl"));

    /// <summary>"/bug text" or "/feedback text" (any case); <paramref name="text"/> is the rest, possibly empty.</summary>
    public static bool TryParseChatCommand(string? content, out string text)
    {
        text = "";
        var line = (content ?? "").TrimStart();
        foreach (var command in ChatCommands)
        {
            if (!line.StartsWith(command, StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Length > command.Length && !char.IsWhiteSpace(line[command.Length])) continue;
            text = line[command.Length..].Trim();
            return true;
        }
        return false;
    }

    /// <summary>Stores one report and answers the player on the marquee; null when nothing was stored.</summary>
    public async Task<long?> SubmitAsync(GameSession session, string? text, string source)
    {
        var settings = _options.Value.Feedback;
        text = Clean(text);
        if (text.Length == 0)
        {
            await GmCommands.ReplyAsync(session, "Usage: /bug <what went wrong>, or the Feedback button in the menu.");
            return null;
        }
        var now = _clock.GetUtcNow();
        string key = session.Account ?? "";
        bool tooSoon;
        lock (_last)
        {
            tooSoon = _last.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(settings.CooldownSeconds);
            if (!tooSoon) _last[key] = now;
        }
        if (tooSoon)
        {
            await GmCommands.ReplyAsync(session, "Please wait a few seconds before sending another report.");
            return null;
        }
        bool truncated = text.Length > settings.MaxLength;
        if (truncated) text = text[..settings.MaxLength];
        var role = session.Role;
        var report = new FeedbackReport
        {
            Time = now,
            Source = source,
            Account = session.Account,
            RoleId = role?.Id ?? 0,
            RoleName = role?.Name,
            Level = session.Progress?.Level ?? role?.Level ?? 0,
            Career = session.Progress?.Career ?? role?.Career ?? 0,
            Scene = session.SceneEntry,
            Map = session.SceneEntry == 0 ? null : NullIfEmpty(_data.MapName(session.SceneEntry)),
            Instance = session.InstanceId,
            Line = session.LineNumber,
            X = Round(session.Position.X), Y = Round(session.Position.Y), Z = Round(session.Position.Z),
            Text = text,
            Truncated = truncated,
            Context = _logs.Matching(session.Account, role?.Name, settings.ContextLines,
                now - TimeSpan.FromMinutes(settings.ContextMinutes)),
        };
        long id;
        try
        {
            // File I/O off the game loop.
            id = await Task.Run(() => Store.Append(report));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogError(e, "Feedback from {Account} could not be stored", session.Account);
            await GmCommands.ReplyAsync(session, "Sorry, the report could not be saved. Please tell the team in chat.");
            return null;
        }
        _log.LogInformation("Feedback #{Id} from {Account} ({Name}) via {Source} at scene {Scene} {X:F0},{Z:F0}: {Text}",
            id, session.Account, role?.Name, source, session.SceneEntry, session.Position.X, session.Position.Z, text);
        await GmCommands.ReplyAsync(session, $"Thank you! Report #{id} was saved.");
        return id;
    }

    /// <summary>Control characters out (new lines stay), surrounding blanks trimmed.</summary>
    internal static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (c == '\n' || !char.IsControl(c)) sb.Append(c);
            else if (c == '\t') sb.Append(' ');
        return sb.ToString().Trim();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    private static double Round(float v) => Math.Round(v, 1);
}

/// <summary>One stored report: a line of DataDir/reports/feedback.jsonl.</summary>
public sealed class FeedbackReport
{
    public long Id { get; set; }
    public DateTimeOffset Time { get; set; }
    /// <summary>"button" (the menu's Feedback entry) or "chat" (/bug).</summary>
    public string Source { get; set; } = "";
    public string? Account { get; set; }
    public long RoleId { get; set; }
    public string? RoleName { get; set; }
    public int Level { get; set; }
    public int Career { get; set; }
    /// <summary>Scene entry (MapConfig id) and its name; instance 0 is the shared world map.</summary>
    public long Scene { get; set; }
    public string? Map { get; set; }
    public long Instance { get; set; }
    public int Line { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public string Text { get; set; } = "";
    public bool Truncated { get; set; }
    /// <summary>The server's recent log lines naming the account or the role, oldest first.</summary>
    public IReadOnlyList<string> Context { get; set; } = [];
}

/// <summary>
/// Append-only JSON lines. One server per data dir (RealmLock), so the lock here is the only writer; a line that does
/// not parse (a crash mid-write) is skipped on read.
/// </summary>
public sealed class FeedbackStore(string path)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _sync = new();
    private long _lastId = -1;

    public string Path { get; } = path;

    /// <summary>Assigns the next id, appends the line; returns the id.</summary>
    public long Append(FeedbackReport report)
    {
        lock (_sync)
        {
            if (_lastId < 0) _lastId = ReadAll().Select(r => r.Id).DefaultIfEmpty(0).Max();
            report.Id = _lastId + 1;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using var file = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            // A line cut by a crash has no line feed: start on a new line so this one stays readable.
            bool newLine = false;
            if (file.Length > 0)
            {
                file.Seek(-1, SeekOrigin.End);
                newLine = file.ReadByte() != '\n';
            }
            file.Seek(0, SeekOrigin.End);
            var bytes = new UTF8Encoding(false).GetBytes((newLine ? "\n" : "") + JsonSerializer.Serialize(report, Json) + "\n");
            file.Write(bytes);
            file.Flush(flushToDisk: true);
            _lastId = report.Id;
            return report.Id;
        }
    }

    /// <summary>Reports after id <paramref name="afterId"/> and at or after <paramref name="since"/>, oldest first.</summary>
    public IReadOnlyList<FeedbackReport> Read(long afterId = 0, DateTimeOffset? since = null, int limit = int.MaxValue)
    {
        lock (_sync)
            return ReadAll().Where(r => r.Id > afterId && (since == null || r.Time >= since)).Take(limit).ToList();
    }

    private IEnumerable<FeedbackReport> ReadAll()
    {
        if (!File.Exists(Path)) yield break;
        foreach (var line in File.ReadLines(Path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            FeedbackReport? report;
            try { report = JsonSerializer.Deserialize<FeedbackReport>(line, Json); }
            catch (JsonException) { continue; }
            if (report != null) yield return report;
        }
    }

    /// <summary>Plain text for a terminal: one block per report.</summary>
    public static string Format(IEnumerable<FeedbackReport> reports)
    {
        var sb = new StringBuilder();
        foreach (var r in reports)
        {
            sb.Append(CultureInfo.InvariantCulture, $"#{r.Id} {r.Time:yyyy-MM-dd HH:mm:ss}Z {r.Source} {r.Account} / {r.RoleName} ({r.RoleId}) Lv{r.Level} job {r.Career}\n");
            sb.Append(CultureInfo.InvariantCulture, $"   scene {r.Scene} {r.Map}{(r.Instance != 0 ? $" instance {r.Instance}" : "")} line {r.Line} at {r.X:F1} {r.Y:F1} {r.Z:F1}\n");
            foreach (var textLine in r.Text.Split('\n')) sb.Append("   > ").Append(textLine).Append('\n');
            if (r.Truncated) sb.Append("   (text truncated)\n");
            foreach (var context in r.Context) sb.Append("   | ").Append(context).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
