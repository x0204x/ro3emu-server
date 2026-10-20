namespace Ro3Emu.LoadTest;

public sealed record RunnerOptions(string Scenario, int Bots, int TargetBots, int DurationSeconds,
    bool ServerLocal, string? ServerUrl, string? PemPath, string OutputPrefix, string RepositoryRoot)
{
    public string WalkPhase { get; init; } = "realistic";
    public static RunnerOptions Parse(string[] args)
    {
        if (args.Contains("--help") || args.Length == 0)
            throw new ArgumentException("Usage: --scenario S1|S2|S3|S4|S5 (--server-local | --server URL) [--pem PATH] [--bots N] [--duration-seconds N] [--walk-phase realistic|sync] [--output PREFIX]");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool local = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server-local") { local = true; continue; }
            if (!args[i].StartsWith("--") || i + 1 >= args.Length) throw new ArgumentException($"Invalid argument {args[i]}");
            values[args[i]] = args[++i];
        }
        string scenario = values.GetValueOrDefault("--scenario")?.ToUpperInvariant() ?? throw new ArgumentException("--scenario required");
        if (scenario is not ("S1" or "S2" or "S3" or "S4" or "S5")) throw new ArgumentException("Unknown scenario");
        int target = scenario switch { "S1" or "S3" => 500, "S2" => 300, "S4" => 200, "S5" => 160, _ => 0 };
        int duration = scenario switch { "S1" or "S4" => 60, "S2" or "S5" => 600, _ => 1800 };
        if (values.TryGetValue("--bots", out var b)) target = int.Parse(b);
        int requested = target;
        int qualifiedTarget = scenario switch { "S1" or "S3" => 500, "S4" => 200, "S5" => 160, _ => 300 };
        if (values.TryGetValue("--duration-seconds", out var d)) duration = int.Parse(d);
        if (requested < 1 || duration < 1) throw new ArgumentException("Bot count and duration must be positive");
        string? url = values.GetValueOrDefault("--server");
        if (local == (url != null)) throw new ArgumentException("Specify exactly one of --server-local and --server URL");
        var repo = FindRepoRoot();
        string phase = values.GetValueOrDefault("--walk-phase", "realistic").ToLowerInvariant();
        if (phase is not ("realistic" or "sync")) throw new ArgumentException("--walk-phase must be realistic or sync");
        var prefix = values.GetValueOrDefault("--output") ?? Path.Combine(repo, ".loadtest", "runs", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{scenario}-{requested}");
        return new(scenario, requested, qualifiedTarget, duration, local, url,
            values.GetValueOrDefault("--pem"), Path.GetFullPath(prefix), repo)
        { WalkPhase = phase };
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Run from the repository root");
    }
}
