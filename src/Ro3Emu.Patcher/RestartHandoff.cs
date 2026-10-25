using System.Diagnostics;

namespace Ro3Emu.Patcher;

/// <summary>Bounded old-process handoff used after the launcher executable is replaced in place.</summary>
public static class RestartHandoff
{
    public const string PreviousProcessOption = "--ro3emu-restarted-from";
    public const string TokenOption = "--ro3emu-restart-token";

    public static bool HasMarker(IReadOnlyList<string> args)
        => args.Contains(PreviousProcessOption, StringComparer.Ordinal) || args.Contains(TokenOption, StringComparer.Ordinal);

    public static bool TryParse(IReadOnlyList<string> args, out int previousProcessId, out string? token)
    {
        previousProcessId = 0;
        token = null;
        int processIndex = IndexOf(args, PreviousProcessOption);
        int tokenIndex = IndexOf(args, TokenOption);
        if (processIndex < 0 && tokenIndex < 0) return false;
        if (processIndex < 0 || tokenIndex < 0 || processIndex + 1 >= args.Count || tokenIndex + 1 >= args.Count) return false;
        return int.TryParse(args[processIndex + 1], System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out previousProcessId) &&
               previousProcessId > 0 && IsValidToken(args[tokenIndex + 1]) &&
               (token = args[tokenIndex + 1]) is not null;
    }

    public static bool WaitForPreviousProcess(IReadOnlyList<string> args, TimeSpan timeout)
    {
        if (!HasMarker(args)) return true;
        if (!TryParse(args, out int previousProcessId, out _)) return false;
        try
        {
            using Process previous = Process.GetProcessById(previousProcessId);
            return previous.HasExited || previous.WaitForExit(checked((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue)));
        }
        catch (ArgumentException)
        {
            return true; // The previous process already exited before this process opened it.
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public static bool IsValidToken(string? token)
        => token is { Length: 32 } && token.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    static int IndexOf(IReadOnlyList<string> args, string value)
    {
        for (int index = 0; index < args.Count; index++) if (string.Equals(args[index], value, StringComparison.Ordinal)) return index;
        return -1;
    }
}
