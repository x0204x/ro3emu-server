using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Ro3Emu.Launcher;

/// <summary>重開登入器時，以選定安裝目錄的執行檔辨識仍在執行的遊戲，不讀取命令列。</summary>
internal static class GameProcessDiscovery
{
    internal sealed record Scan(IReadOnlyList<Process> Games, bool Incomplete);

    internal static bool MatchesExecutable(string expected, string? actual) =>
        !string.IsNullOrWhiteSpace(actual)
        && string.Equals(Path.GetFullPath(expected), Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase);

    public static Scan Find(string executable)
    {
        var games = new List<Process>();
        bool incomplete = false;
        foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            bool retained = false;
            try
            {
                if (process.HasExited) continue;
                string? actual = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(actual)) incomplete = true;
                else if (MatchesExecutable(executable, actual))
                {
                    games.Add(process);
                    retained = true;
                }
            }
            catch (InvalidOperationException) { } // 程序在盤點期間結束，下一次操作會重新盤點。
            catch (Win32Exception) { incomplete = true; }
            finally { if (!retained) process.Dispose(); }
        }
        return new(games, incomplete);
    }
}
