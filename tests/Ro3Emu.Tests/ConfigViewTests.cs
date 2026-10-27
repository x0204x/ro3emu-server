using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>The typed config views (Data/Config), their readers and the startup check of the game data.</summary>
public sealed class ConfigViewTests
{
    private static readonly double[] Fraction = [1.5];
    private static readonly int[] AwardEntry = [2, 1, 0];

    private static readonly ConfigTableInfo Probe = new("ProbeConfig", false, false,
    [
        new("_iCount", ConfigKind.Integral, false),
        new("_kName", ConfigKind.Text, false),
        new("_kList", ConfigKind.Ints, true),
    ]);

    private sealed class DataDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ro3emu-config-" + Guid.NewGuid().ToString("N"));

        public DataDir(bool export, params (string Table, object Rows)[] tables)
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            if (export) File.WriteAllText(Path.Combine(Root, GameData.ExportManifest), "{}");
            foreach (var (table, rows) in tables)
                File.WriteAllText(Path.Combine(Root, "config", table + ".json"), JsonSerializer.Serialize(rows));
        }

        public GameData Data => Load(Root);

        public void Dispose() => Directory.Delete(Root, true);
    }

    private static GameData Load(string root) =>
        new(Options.Create(new ServerOptions { GameDataDir = root }), NullLogger<GameData>.Instance);

    private static IReadOnlyList<string> Errors(DataDir dir, bool strict) =>
        ConfigValidator.Validate(dir.Data, [Probe], strict);

    [Fact]
    public void AClientExportMustCarryEveryDeclaredTableAndField()
    {
        using (var empty = new DataDir(export: true))
            Assert.Equal(["ProbeConfig: the table is missing or empty"], Errors(empty, strict: true));
        using var typo = new DataDir(export: true,
            ("ProbeConfig", new[] { new { _iID = 1, _iCuont = 3, _kName = "a" } }));
        Assert.Equal(["ProbeConfig._iCount: missing in 1 of 1 rows (first: 1)"], Errors(typo, strict: true));
        Assert.Empty(Errors(typo, strict: false)); // a hand-made data set may leave fields out
    }

    [Fact]
    public void EveryPresentValueMustHaveItsFieldsKind()
    {
        using var dir = new DataDir(export: false, ("ProbeConfig", new object[]
        {
            new { _iID = 1, _iCount = 3, _kName = "a", _kList = new { } }, // Lua's empty list
            new { _iID = 2, _iCount = "3", _kName = "b" },
            new { _iID = 3, _iCount = 3, _kName = 7, _kList = Fraction },
        }));
        Assert.Equal(
        [
            "ProbeConfig._iCount: row 2 holds string \"3\", not an integer",
            "ProbeConfig._kName: row 3 holds number 7, not a text",
            "ProbeConfig._kList: row 3 holds array [1.5], not a list of integers",
        ], Errors(dir, strict: false));
    }

    [Fact]
    public void ACheckFailureNamesEveryMismatch()
    {
        using var dir = new DataDir(export: true);
        var failure = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Check(dir.Data, [Probe], strict: true));
        Assert.Contains("ProbeConfig: the table is missing or empty", failure.Message);
    }

    [Fact]
    public void ReadersKeepLargeIdsExactAndReadEmptyLuaListsAsEmpty()
    {
        var row = JsonDocument.Parse("""
            { "_iBig": 9007199254740993, "_iFrac": 2.9, "_fRate": "1.25", "_kNone": {}, "_kRows": [[1, 2], {}],
              "_kPairs": [[101, 5], [102, 7.5, 1]], "_kNames": ["a", 3] }
            """).RootElement;
        Assert.Equal(9007199254740993, ConfigValue.Integral(row, "_iBig"));
        Assert.Equal(9007199254740993, GameData.Int(row, "_iBig"));
        Assert.Equal(2, ConfigValue.Integral(row, "_iFrac"));
        Assert.Equal(1.25, ConfigValue.Number(row, "_fRate"));
        Assert.Empty(ConfigValue.Ints(row, "_kNone"));
        Assert.Equal([[1, 2], []], ConfigValue.IntRows(row, "_kRows"));
        Assert.Equal([(101L, 5.0), (102L, 7.5)], ConfigValue.Pairs(row, "_kPairs"));
        Assert.Equal(["a", ""], ConfigValue.Texts(row, "_kNames"));
        Assert.Equal(0, ConfigValue.Integral(row, "_iAbsent"));
        Assert.Null(ConfigValue.Raw(row, "_iAbsent"));
    }

    [Fact]
    public void AGroupedTableChecksEachEntryAndGetReturnsTheGroup()
    {
        using var dir = new DataDir(export: true, ("AwardConfig", new Dictionary<string, object>
        {
            ["7"] = new object[]
            {
                new { _iID = 7, _kAward = new[] { AwardEntry } },
                new { _iID = 7, _kAward = new { } },
            },
        }));
        var data = dir.Data;
        var award = AwardConfig.Info with
        {
            Fields = [.. AwardConfig.Info.Fields.Where(f => f.Name is "_iID" or "_kAward")],
        };
        Assert.Empty(ConfigValidator.Validate(data, [award], strict: true));
        var group = AwardConfig.Get(data, 7);
        Assert.Equal(2, group.Count);
        Assert.Equal([[2, 1, 0]], group[0].Award);
        Assert.Empty(group[1].Award);
        Assert.Empty(AwardConfig.Get(data, 8));
    }

    [Fact]
    public void EveryViewIsListedForTheStartupCheck()
    {
        var names = ConfigTables.All.Select(t => t.Name).ToArray();
        Assert.Equal(names.Distinct().Count(), names.Length);
        Assert.Contains(SkillConfig.Info, ConfigTables.All);
        Assert.All(ConfigTables.All, t => Assert.NotEmpty(t.Fields));
    }

    [ClientDataFact]
    public void TheClientExportMatchesEveryDeclaredView()
    {
        var data = Load(Path.Combine(ClientData.RepoRoot, "data"));
        Assert.Empty(ConfigValidator.Validate(data, ConfigTables.All, strict: true));
    }

    // Each client field name written as a string outside Data/Config is a read the startup check cannot see. The
    // count only goes down: lower it when a change removes such reads, never raise it.
    private const int FieldNamesOutsideViews = 2666;
    private static readonly string Obj = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;

    [Fact]
    public void ClientFieldNamesLiveInTheConfigViews()
    {
        var source = Path.Combine(ClientData.RepoRoot, "src", "Ro3Emu.Server");
        var config = Path.Combine(source, "Data", "Config") + Path.DirectorySeparatorChar;
        var pattern = new Regex("\"(?:_[a-z]|m_[a-z])[A-Z][A-Za-z0-9_]*\"");
        int count = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(config, StringComparison.Ordinal)
                && !path.Contains(Obj, StringComparison.Ordinal))
            .Sum(path => pattern.Count(File.ReadAllText(path)));
        Assert.Equal(FieldNamesOutsideViews, count);
    }
}
