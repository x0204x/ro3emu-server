using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Ro3Emu.ClientTools;

/// <summary>
/// Starts ro3.exe the way RO3Launcher.exe does. RO3.ClientSecurity.LauncherProofGuard kills the game (exit 70) unless
/// RO3_LAUNCH_PIPE_HANDLE names an inherited anonymous pipe that delivers, within one second, exactly five CRLF lines:
/// RO3_LAUNCH_PROOF_V1, the game's PID, issued-at and expiry unix seconds (1..30 s apart), 64 lowercase hex chars.
/// Nothing in it is signed.
/// </summary>
public static class Launcher
{
    public static int Launch(string clientDir, string[] gameArgs, long? verifiedGenesisSequence = null)
    {
        using var game = Start(clientDir, gameArgs, verifiedGenesisSequence);
        if (game.HasExited)
        {
            Console.WriteLine($"ro3.exe exited early with code {game.ExitCode}" + (game.ExitCode == 70 ? " (launcher proof rejected)" : ""));
            return game.ExitCode;
        }
        return 0;
    }

    /// <summary>Starts the game and hands it the proof; returns once it has read it (or after it exited early).</summary>
    public static Process Start(string clientDir, string[] gameArgs, long? verifiedGenesisSequence = null)
    {
        var exe = Path.Combine(clientDir, "ro3.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("ro3.exe not found", exe);

        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = clientDir,
        };
        foreach (var a in gameArgs) start.ArgumentList.Add(a);
        start.Environment.Remove("RO3EMU_VERIFIED_GENESIS_SEQUENCE");
        if (verifiedGenesisSequence is { } sequence)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
            start.Environment["RO3EMU_VERIFIED_GENESIS_SEQUENCE"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        start.Environment["RO3_LAUNCH_PIPE_HANDLE"] = pipe.GetClientHandleAsString();

        var game = Process.Start(start) ?? throw new InvalidOperationException("ro3.exe did not start");
        pipe.DisposeLocalCopyOfClientHandle();

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var proof = string.Join("\r\n",
            "RO3_LAUNCH_PROOF_V1",
            game.Id.ToString(),
            now.ToString(),
            (now + 30).ToString(),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))) + "\r\n";
        var bytes = Encoding.ASCII.GetBytes(proof);
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();
        Console.WriteLine($"started ro3.exe pid {game.Id}");

        // Keep our end open while the game reads the proof (it gives itself one second).
        game.WaitForExit(5000);
        return game;
    }
}
