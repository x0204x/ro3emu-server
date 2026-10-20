using System.Globalization;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>
/// The last few thousand log lines the server wrote (Information and above, after the usual Logging filters), kept in
/// memory so an in-game bug report (FeedbackService) can carry what the server logged about that player shortly
/// before. One formatting per log call and a short lock; nothing is written anywhere. ServerOptions.Feedback.ContextLines
/// 0 leaves it out of the logging pipeline.
/// </summary>
public sealed class RecentLogBuffer(TimeProvider clock) : ILoggerProvider
{
    public const int Capacity = 4096;

    private readonly record struct Line(DateTimeOffset Time, LogLevel Level, string Category, string Text);

    private readonly Line[] _lines = new Line[Capacity];
    private readonly object _sync = new();
    private long _written;

    public ILogger CreateLogger(string categoryName) => new Writer(this, ShortCategory(categoryName));

    public void Dispose() { }

    public void Add(LogLevel level, string category, string text)
    {
        var line = new Line(clock.GetUtcNow(), level, category, text);
        lock (_sync) _lines[_written++ % Capacity] = line;
    }

    /// <summary>
    /// The newest <paramref name="max"/> lines since <paramref name="since"/> that name the account or the role (plain
    /// substring, case-sensitive: log lines carry both verbatim), oldest first, as "HH:mm:ss.fff Level Category: text".
    /// </summary>
    public IReadOnlyList<string> Matching(string? account, string? role, int max, DateTimeOffset since)
    {
        var keys = new[] { account, role }.Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).Distinct().ToArray();
        if (max <= 0 || keys.Length == 0) return [];
        var found = new List<Line>(max);
        lock (_sync)
        {
            long first = Math.Max(0, _written - Capacity);
            for (long i = _written - 1; i >= first && found.Count < max; i--)
            {
                var line = _lines[i % Capacity];
                if (line.Time < since) break;
                if (keys.Any(k => line.Text.Contains(k, StringComparison.Ordinal))) found.Add(line);
            }
        }
        found.Reverse();
        return found.Select(l => string.Create(CultureInfo.InvariantCulture,
            $"{l.Time:HH:mm:ss.fff} {Short(l.Level)} {l.Category}: {l.Text}")).ToList();
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => level.ToString().ToLowerInvariant(),
    };

    private static string ShortCategory(string category) => category[(category.LastIndexOf('.') + 1)..];

    private sealed class Writer(RecentLogBuffer owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            string text = formatter(state, exception);
            if (exception != null) text += " | " + exception.GetType().Name + ": " + exception.Message;
            owner.Add(level, category, text);
        }
    }
}
