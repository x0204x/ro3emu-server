// Checks the analyzer baseline in .editorconfig. Builds every project that builds without the game client, with the
// baseline's rules reported as warnings, and compares each rule's violations with the count written above it. A rule
// over its count fails: new code broke it. A rule under its count fails too, until the count is lowered, so the numbers
// stay true. Before the build it compares with the parent commit, because a count raised together with new violations
// would match: a count may not rise, and no rule may be turned down or lose its count line.
// usage: dotnet run --project tools/AnalyzerBaseline -c Release [repository root]
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : FindRoot(Directory.GetCurrentDirectory());
string editorConfig = Path.Combine(root, ".editorconfig");
string original = File.ReadAllText(editorConfig);
var baseline = ParseBaseline(original);
if (LooserThanParent(root, original, baseline))
{
    Console.Error.WriteLine("The analyzer baseline in .editorconfig got looser than in the parent commit (see above).");
    return 1;
}
if (baseline.Count == 0)
{
    Console.WriteLine("No analyzer baseline in .editorconfig.");
    return 0;
}

// Every project but the ones that compile against the player's own client DLLs, and this tool (running, so its
// output is locked on Windows).
string[] skipped = ["Ro3Emu.ClientHooks", "Ro3Emu.Loc", "Ro3Emu.ClientTools", "AnalyzerBaseline"];
var projects = new[] { "src", "tests", "tools" }
    .SelectMany(dir => Directory.GetFiles(Path.Combine(root, dir), "*.csproj", SearchOption.AllDirectories))
    .Where(path => !skipped.Contains(Path.GetFileNameWithoutExtension(path)))
    .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
    .Order(StringComparer.Ordinal)
    .ToList();

string solution = Path.Combine(root, ".analyzer-baseline.slnx");
var output = new StringBuilder();
int exitCode;
try
{
    File.WriteAllText(solution,
        "<Solution>\n" + string.Concat(projects.Select(p => $"  <Project Path=\"{p}\" />\n")) + "</Solution>\n");
    File.WriteAllText(editorConfig, AsWarnings(original, baseline.Keys));
    Console.CancelKeyPress += (_, _) => Restore();
    exitCode = Build(root, solution, baseline.Keys, output);
}
finally
{
    Restore();
}

if (exitCode != 0)
{
    // Anything outside the baseline is still an error; show what failed.
    var lines = output.ToString().Split('\n');
    var errors = lines.Where(l => l.Contains(": error ", StringComparison.Ordinal)).Distinct().ToArray();
    foreach (var line in errors)
        Console.Error.WriteLine(line.TrimEnd());
    // MSBuild can fail before a compiler diagnostic exists (for example, a project-reference worker exits).
    // Preserve a bounded tail in that case instead of reducing the actionable failure to one generic sentence.
    if (errors.Length == 0)
        foreach (var line in lines.TakeLast(80)) Console.Error.WriteLine(line.TrimEnd());
    Console.Error.WriteLine("The build failed; the baseline was not checked.");
    return exitCode;
}

var found = CountViolations(output.ToString(), baseline.Keys);
int failures = 0;
Console.WriteLine("rule     baseline  now");
foreach (var (rule, count) in baseline.OrderBy(r => r.Key, StringComparer.Ordinal))
{
    int now = found.GetValueOrDefault(rule);
    string verdict = now > count ? "  more: fix the new violations"
        : now < count ? now == 0 ? "  zero: delete the rule's two lines" : $"  fewer: write {now}"
        : "";
    if (verdict.Length > 0) failures++;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{rule,-8} {count,8} {now,4}{verdict}"));
}
if (failures == 0)
{
    Console.WriteLine("The analyzer baseline matches the code.");
    return 0;
}
Console.Error.WriteLine("The analyzer baseline in .editorconfig does not match the code (see above).");
return 1;

void Restore()
{
    File.WriteAllText(editorConfig, original);
    File.Delete(solution);
}

static string FindRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
    throw new InvalidOperationException("Run this inside the repository, or pass its root.");
}

// "# <count>: <title>" followed by "dotnet_diagnostic.<rule>.severity = suggestion".
static Dictionary<string, int> ParseBaseline(string config) =>
    Regex.Matches(config, @"^# (?<count>\d+): .*\r?\ndotnet_diagnostic\.(?<rule>(?:CA|IDE)\d+)\.severity = suggestion\r?$",
            RegexOptions.Multiline)
        .ToDictionary(m => m.Groups["rule"].Value, m => int.Parse(m.Groups["count"].Value, CultureInfo.InvariantCulture));

// The parent commit is the base branch of a pull request and the previous main after a merge (CI checks out two commits).
// An update of the SDK in global.json may bring new violations (.editorconfig says what to do then), so it is not compared.
static bool LooserThanParent(string root, string config, Dictionary<string, int> baseline)
{
    string? parent = Git(root, "show", "HEAD^1:.editorconfig");
    if (parent == null)
    {
        bool ci = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != null;
        (ci ? Console.Error : Console.Out).WriteLine("No .editorconfig in the parent commit to compare the baseline with.");
        return ci;
    }
    string? sdk = Git(root, "show", "HEAD^1:global.json");
    if (sdk?.ReplaceLineEndings() != File.ReadAllText(Path.Combine(root, "global.json")).ReplaceLineEndings())
    {
        Console.WriteLine("global.json changed: the baseline is not compared with the parent commit.");
        return false;
    }

    var problems = new List<string>();
    var before = ParseBaseline(parent);
    foreach (var (rule, count) in baseline)
        if (before.TryGetValue(rule, out int old) && count > old)
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{rule}: {old} in the parent commit, {count} now: fix the new violations"));
    var turnedDown = TurnedDown(config);
    foreach (string setting in turnedDown.Except(TurnedDown(parent)))
        problems.Add($"{setting}: new, and it hides violations from the baseline");
    foreach (string rule in before.Keys.Except(baseline.Keys))
        if (turnedDown.Contains($"[*.cs] dotnet_diagnostic.{rule}.severity"))
            problems.Add($"{rule}: its count line is gone but the rule is still off, so nothing counts it");
    problems.ForEach(Console.Error.WriteLine);
    return problems.Count > 0;
}

// Every setting that keeps a rule, or a whole folder, from failing the build, as "[section] key". The sections of the
// projects that CI cannot build (they compile against the player's client) and of generated code do not count here.
static HashSet<string> TurnedDown(string config)
{
    string[] exempt = ["[src/Ro3Emu.{ClientHooks,Loc}/**.cs]", "[src/Ro3Emu.{ClientHooks,Loc,ClientTools}/**.cs]", "[**/obj/**.cs]"];
    var settings = new HashSet<string>(StringComparer.Ordinal);
    string section = "";
    foreach (string raw in config.Split('\n'))
    {
        string line = raw.Trim();
        if (line.StartsWith('[')) section = line;
        var m = Regex.Match(line, @"^(?<key>dotnet_(?:analyzer_)?diagnostic\.\S*severity|generated_code)\s*=\s*(?<value>\S+)$");
        if (m.Success && !exempt.Contains(section)
            && m.Groups["value"].Value.ToLowerInvariant() is "suggestion" or "silent" or "none" or "default" or "refactoring" or "true")
            settings.Add($"{section} {m.Groups["key"].Value}");
    }
    return settings;
}

// The command's output, or null when it failed.
static string? Git(string root, params string[] arguments)
{
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("-C");
    start.ArgumentList.Add(root);
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
    process.ErrorDataReceived += (_, _) => { };
    process.BeginErrorReadLine();
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0 ? output : null;
}

static string AsWarnings(string config, IEnumerable<string> rules) =>
    rules.Aggregate(config, (text, rule) =>
        text.Replace($"dotnet_diagnostic.{rule}.severity = suggestion", $"dotnet_diagnostic.{rule}.severity = warning",
            StringComparison.Ordinal));

// Warnings are still errors, except the baseline's rules, so the same build also enforces every other rule.
static int Build(string root, string solution, IEnumerable<string> rules, StringBuilder output)
{
    // %3B: a plain ';' would end the -p: value.
    string notAsErrors = string.Join("%3B", rules);
    var start = new ProcessStartInfo("dotnet")
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var arg in new[] { "build", solution, "-c", "Release", "--no-incremental", "-nologo", "-m:1",
        "-p:EnableWindowsTargeting=true", $"-p:WarningsNotAsErrors={notAsErrors}" })
        start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start");
    process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
    process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    process.WaitForExit();
    return process.ExitCode;
}

// MSBuild repeats each warning in its summary; a violation counts once per file, position and rule.
static Dictionary<string, int> CountViolations(string buildOutput, IEnumerable<string> rules)
{
    var wanted = rules.ToHashSet(StringComparer.Ordinal);
    return Regex.Matches(buildOutput, @"^\s*(?<file>[^\s].*?)\((?<line>\d+),(?<col>\d+)\): warning (?<rule>(?:CA|IDE)\d+):",
            RegexOptions.Multiline)
        .Where(m => wanted.Contains(m.Groups["rule"].Value))
        .Select(m => (Rule: m.Groups["rule"].Value, Key: $"{m.Groups["file"].Value}|{m.Groups["line"].Value}|{m.Groups["col"].Value}"))
        .Distinct()
        .GroupBy(v => v.Rule, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}
