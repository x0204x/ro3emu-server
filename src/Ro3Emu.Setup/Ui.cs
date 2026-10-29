using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ro3Emu.Setup;

/// <summary>The console the player sees: short steps, plain messages, one progress line. Everything also goes to the log.</summary>
internal static class Ui
{
    private static readonly object Gate = new();
    private static bool _progressShown;

    /// <summary>False when nobody can answer (stdin redirected, --yes): questions take their default.</summary>
    public static bool Interactive { get; set; } = !Console.IsInputRedirected;

    /// <summary>
    /// The launcher's window instead of the console (Ro3EmuLauncher drives the same flows): messages and progress go
    /// there and to the log, nothing is written to the console, and nobody is asked (<see cref="Interactive"/> false).
    /// </summary>
    public static IUiSink? Sink { get; set; }

    public static void Title(string text)
    {
        Write(ConsoleColor.Cyan, "\n" + text + "\n" + new string('=', text.Length), UiKind.Title, text);
    }

    public static void Step(string text) => Write(ConsoleColor.Cyan, "\n> " + text, UiKind.Step, text);

    public static void Info(string text) => Write(null, "  " + text, UiKind.Info, text);

    public static void Ok(string text) => Write(ConsoleColor.Green, "  " + text, UiKind.Ok, text);

    public static void Warn(string text) => Write(ConsoleColor.Yellow, "  " + text, UiKind.Warn, text);

    public static void Error(string text) => Write(ConsoleColor.Red, "\n" + text, UiKind.Error, text);

    private static void Write(ConsoleColor? color, string text, UiKind kind, string plain)
    {
        if (Sink is { } sink)
        {
            SetupLog.Write(text.Trim('\n'));
            sink.Message(kind, plain);
            return;
        }
        lock (Gate)
        {
            EndProgress();
            if (color is { } c) Console.ForegroundColor = c;
            Console.WriteLine(text);
            if (color != null) Console.ResetColor();
            SetupLog.Write(text.Trim('\n'));
        }
    }

    /// <summary>
    /// A measured step (bytes or items done of total; total 0 = unknown): the launcher draws its bar from it, the
    /// console shows <paramref name="text"/> as the status line.
    /// </summary>
    public static void Progress(string stage, long done, long total, string text)
    {
        if (Sink is { } sink) sink.Progress(stage, done, total);
        else Progress(text);
    }

    /// <summary>Rewrites one status line (no log).</summary>
    public static void Progress(string text)
    {
        if (Sink is { } sink)
        {
            sink.Progress(text, 0, 0);
            return;
        }
        lock (Gate)
        {
            if (Console.IsOutputRedirected) return;
            int width = Math.Max(20, SafeWidth() - 1);
            if (text.Length > width) text = text[..width];
            Console.Write("\r  " + text.PadRight(width - 2));
            _progressShown = true;
        }
    }

    public static void EndProgress()
    {
        if (Sink != null) return;
        lock (Gate)
        {
            if (!_progressShown) return;
            Console.WriteLine();
            _progressShown = false;
        }
    }

    private static int SafeWidth()
    {
        try { return Console.WindowWidth; } catch (IOException) { return 100; }
    }

    /// <summary>A yes/no question; Enter (or no one to ask) gives the default.</summary>
    public static bool Confirm(string question, bool defaultYes = true)
    {
        if (!Interactive) return defaultYes;
        lock (Gate) EndProgress();
        Console.Write("  " + question + (defaultYes ? " [Y/n] " : " [y/N] "));
        var answer = Console.ReadLine()?.Trim() ?? "";
        SetupLog.Write($"{question} -> '{answer}'");
        if (answer.Length == 0) return defaultYes;
        return answer.StartsWith('y') || answer.StartsWith('Y');
    }

    /// <summary>A free answer; Enter (or no one to ask) gives the default.</summary>
    public static string Ask(string question, string defaultValue = "")
    {
        if (!Interactive) return defaultValue;
        lock (Gate) EndProgress();
        Console.Write("  " + question + (defaultValue.Length > 0 ? $" [{defaultValue}] " : " "));
        var answer = Console.ReadLine()?.Trim() ?? "";
        SetupLog.Write($"{question} -> '{answer}'");
        return answer.Length == 0 ? defaultValue : answer;
    }

    /// <summary>Human sizes: 1.2 GB, 340 MB.</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{Math.Max(0, t.Seconds)} s";

    /// <summary>
    /// True when this program owns its console window (double-clicked in Explorer): the window closes with the
    /// program, so the last message needs a pause to be read.
    /// </summary>
    public static bool OwnsConsole() => _ownsConsole ??= DetectOwnConsole();

    private static bool? _ownsConsole;

    private static bool DetectOwnConsole()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected) return false;
        try
        {
            var list = new uint[4];
            return GetConsoleProcessList(list, (uint)list.Length) <= 1;
        }
        catch (Exception) { return false; }
    }

    public static void PauseIfOwnConsole()
    {
        if (!OwnsConsole()) return;
        Console.WriteLine();
        Console.Write("  Press Enter to close this window.");
        Console.ReadLine();
    }

    /// <summary>Opens Explorer on a file (Windows), so the player sees the zip to attach.</summary>
    public static void Reveal(string path)
    {
        if (!OperatingSystem.IsWindows() || !Interactive) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception e) { SetupLog.Write("explorer: " + e.Message); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}

internal enum UiKind { Title, Step, Info, Ok, Warn, Error }

/// <summary>Where <see cref="Ui"/> goes when the flows run behind a window instead of a console.</summary>
internal interface IUiSink
{
    void Message(UiKind kind, string text);

    /// <summary><paramref name="stage"/> with <paramref name="done"/> of <paramref name="total"/> (0 = unknown).</summary>
    void Progress(string stage, long done, long total);
}
