namespace Ro3Emu.Tests;

/// <summary>
/// The game data exported from your own client (data/config and the rest, see README). It is not in git, so tests that
/// need it are skipped when it is missing instead of failing. RO3EMU_NO_CLIENT_DATA=1 treats it as missing even when it
/// is there, to check which tests a public checkout (CI) runs. Tests whose rules do not depend on the real content use
/// the synthetic fixture instead (<see cref="SyntheticData"/>) and always run.
/// </summary>
public static class ClientData
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static bool Available { get; } = Environment.GetEnvironmentVariable("RO3EMU_NO_CLIENT_DATA") is not ("1" or "true")
        && Directory.Exists(Path.Combine(RepoRoot, "data", "config"));

    public const string SkipReason = "Needs the game data exported from your own client (data/config, see README).";

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        return AppContext.BaseDirectory;
    }
}

/// <summary>A fact that needs the exported game data; skipped without it.</summary>
public sealed class ClientDataFactAttribute : FactAttribute
{
    public ClientDataFactAttribute()
    {
        if (!ClientData.Available) Skip = ClientData.SkipReason;
    }
}

/// <summary>A theory that needs the exported game data; skipped without it.</summary>
public sealed class ClientDataTheoryAttribute : TheoryAttribute
{
    public ClientDataTheoryAttribute()
    {
        if (!ClientData.Available) Skip = ClientData.SkipReason;
    }
}
