using System.Runtime.InteropServices;

namespace Ro3Emu.Server.Updates;

/// <summary>
/// The release package of a platform and the files it must hold (tools/release/pack.ps1 builds them): the Linux
/// archive RO3Emu-server-linux-x64.tar.gz with its RO3Emu-server/ folder, the Windows zip RO3Emu-win-x64.zip with its
/// RO3Emu/ folder. Both carry the server in server/ and the RO3Emu tool beside it. Other platforms have no package.
/// </summary>
public sealed record UpdatePlatform(string Name, string Asset, string Folder, string ServerFile, string ToolFile)
{
    public const string LinuxX64 = "linux-x64", WindowsX64 = "win-x64";

    public static readonly UpdatePlatform Linux = new(LinuxX64, "RO3Emu-server-linux-x64.tar.gz", "RO3Emu-server", "server/Ro3Emu.Server", "RO3Emu");
    public static readonly UpdatePlatform Windows = new(WindowsX64, "RO3Emu-win-x64.zip", "RO3Emu", "server/Ro3Emu.Server.exe", "RO3Emu.exe");

    /// <summary>The settings files a release ships with its defaults; an operator's edited copy is kept (see UpdateInstaller).</summary>
    public static readonly string[] Settings = ["server/appsettings.json", "server/appsettings.Development.json"];

    /// <summary>linux-x64, win-x64, osx-arm64...: the running server's.</summary>
    public static string Current()
    {
        string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : "other";
        return os + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    }

    /// <summary>The package of <paramref name="platform"/>, or null when the releases have none for it.</summary>
    public static UpdatePlatform? Of(string platform) => platform switch
    {
        LinuxX64 => Linux,
        WindowsX64 => Windows,
        _ => null,
    };

    /// <summary>This platform's package among a release's assets, or null.</summary>
    public ReleaseAsset? Pick(ReleaseInfo release) =>
        release.Assets.FirstOrDefault(a => string.Equals(a.Name, Asset, StringComparison.Ordinal));

    public bool IsTarball => Asset.EndsWith(".tar.gz", StringComparison.Ordinal);
}
