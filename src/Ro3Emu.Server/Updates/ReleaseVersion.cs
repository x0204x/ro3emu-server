using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Ro3Emu.Server.Updates;

/// <summary>
/// A release's version as its tag and the build name it (tools/release/pack.ps1 -Version 1.0.24, tag v1.0.24): three
/// numbers and an optional pre-release suffix. A suffix sorts before the plain version (1.1.0-rc.1 &lt; 1.1.0); the
/// build's "+commit" is not part of it.
/// </summary>
public readonly partial record struct ReleaseVersion(int Major, int Minor, int Patch, string Suffix) : IComparable<ReleaseVersion>
{
    /// <summary>The running server's informational version ("1.0.24+&lt;commit&gt;"; a build from source says 1.0.0).</summary>
    public static string Running =>
        typeof(ServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    [GeneratedRegex(@"^[vV]?(\d{1,6})\.(\d{1,6})\.(\d{1,6})(?:-([0-9A-Za-z.\-]{1,32}))?(?:\+[0-9A-Za-z.\-]*)?$")]
    private static partial Regex Pattern();

    /// <summary>"v1.0.24", "1.0.24", "1.0.24+7cf31d4", "1.1.0-rc.1"; false for anything else.</summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (text == null || Pattern().Match(text.Trim()) is not { Success: true } m) return false;
        version = new ReleaseVersion(Number(m.Groups[1].Value), Number(m.Groups[2].Value), Number(m.Groups[3].Value), m.Groups[4].Value);
        return true;
    }

    private static int Number(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    public int CompareTo(ReleaseVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A plain version comes after any pre-release of it.
        if (Suffix.Length == 0 || other.Suffix.Length == 0) return (Suffix.Length == 0).CompareTo(other.Suffix.Length == 0);
        return Math.Sign(string.CompareOrdinal(Suffix, other.Suffix));
    }

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (Suffix.Length > 0 ? "-" + Suffix : "");
}
