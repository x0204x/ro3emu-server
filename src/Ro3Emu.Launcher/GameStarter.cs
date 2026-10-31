using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Ro3Emu.Patcher;

namespace Ro3Emu.Launcher;

/// <summary>
/// Starts ro3.exe the way RO3Launcher.exe does (same as Ro3Emu.ClientTools' Launcher): RO3.ClientSecurity.LauncherProofGuard
/// kills the game (exit 70) unless RO3_LAUNCH_PIPE_HANDLE names an inherited anonymous pipe that delivers, within one
/// second, five CRLF lines: RO3_LAUNCH_PROOF_V1, the game's PID, issued-at and expiry unix seconds, 64 lowercase hex chars.
/// </summary>
public static class GameStarter
{
    public sealed record StartResult(Process? Process, string? Failure);

    /// <summary>Returns the exact running process, or the reason it stopped within the first seconds.</summary>
    public static async Task<StartResult> StartAsync(string clientDir, LaunchInfo launch, long? verifiedGenesisSequence = null)
    {
        if (!string.Equals(launch.Exe, "ro3.exe", StringComparison.OrdinalIgnoreCase))
            return new(null, $"the signed package launch target is '{launch.Exe}', not ro3.exe");
        string exe = PathRules.ToLocal(clientDir, launch.Exe);
        if (!File.Exists(exe)) return new(null, "ro3.exe not found");

        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = clientDir };
        // 不沿用父程序殘留的版本顯示；只有本次完整契約驗證成功才提供官方序號。
        start.Environment.Remove("RO3EMU_VERIFIED_GENESIS_SEQUENCE");
        if (verifiedGenesisSequence is { } sequence)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
            start.Environment["RO3EMU_VERIFIED_GENESIS_SEQUENCE"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        start.Environment["RO3_LAUNCH_PIPE_HANDLE"] = pipe.GetClientHandleAsString();
        var game = Process.Start(start) ?? throw new InvalidOperationException("ro3.exe did not start");
        try
        {
            pipe.DisposeLocalCopyOfClientHandle();

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string proof = string.Join("\r\n", "RO3_LAUNCH_PROOF_V1", game.Id.ToString(), now.ToString(), (now + 30).ToString(),
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))) + "\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(proof);
            await pipe.WriteAsync(bytes);
            await pipe.FlushAsync();

            // Keep our end open while the game reads the proof (it gives itself one second).
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await game.WaitForExitAsync(wait.Token);
                string failure = game.ExitCode == 70 ? "the game rejected the launch proof (exit 70)"
                    : $"the game closed right away (exit {game.ExitCode})";
                game.Dispose();
                return new(null, failure);
            }
            catch (OperationCanceledException)
            {
                return new(game, null);
            }
        }
        catch
        {
            game.Dispose();
            throw;
        }
    }
}
