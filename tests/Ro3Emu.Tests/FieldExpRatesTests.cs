using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Ro3Emu:FieldExpRates: the Base EXP of an open-field kill by the killer's base level.</summary>
public sealed class FieldExpRatesTests
{
    private const int Fabre = 5000003, AntEggOfTheStory = 5300036;

    private static FieldExpRule Rule(int min, int max, int percent) => new() { MinLevel = min, MaxLevel = max, Percent = percent };

    [Fact]
    public void WithoutARuleTheExpIsWhatTheClientsTablesPay() =>
        Assert.Equal(55, FieldExpRates.Scale([], 30, 55));

    [Theory]
    [InlineData(19, 55)] // below the range
    [InlineData(20, 165)]
    [InlineData(40, 165)]
    [InlineData(41, 55)] // above it
    public void ARuleScalesOnlyTheLevelsItNames(long level, long expected) =>
        Assert.Equal(expected, FieldExpRates.Scale([Rule(20, 40, 300)], level, 55));

    [Fact]
    public void TheFirstRuleThatHoldsTheLevelWins() =>
        Assert.Equal(110, FieldExpRates.Scale([Rule(20, 30, 200), Rule(25, 40, 900)], 28, 55));

    [Fact]
    public void ASmallPercentNeverPaysLessThanOneExp() =>
        Assert.Equal(1, FieldExpRates.Scale([Rule(1, 99, 1)], 30, 55));

    [Fact]
    public void ARuleThatIsNotUsableIsIgnored()
    {
        var options = new ServerOptions
        {
            FieldExpRates = [Rule(0, 40, 200), Rule(30, 20, 200), Rule(20, 40, 0), Rule(20, 40, FieldExpRates.MaxPercent + 1), Rule(20, 40, 150)],
        };
        Assert.Equal(150, Assert.Single(FieldExpRates.Valid(options)).Percent);
        Assert.Empty(FieldExpRates.Valid(new ServerOptions()));
        Assert.Empty(FieldExpRates.Valid((ServerOptions?)null));
    }

    [Theory]
    [InlineData("20 40 300", 1)]
    [InlineData("20 30 200 31 40 400", 2)]
    public void ASetLineHoldsOneRulePerGroupOfThreeNumbers(string line, int rules) =>
        Assert.Equal(rules, FieldExpRates.Parse(line.Split(' ')).Length);

    [Theory]
    [InlineData("")]
    [InlineData("20 40")] // a group is three numbers
    [InlineData("0 40 300")] // levels start at 1
    [InlineData("40 20 300")] // min above max
    [InlineData("20 40 0")]
    [InlineData("20 40 10001")]
    public void ALineWithAnUnusableRuleIsRefused(string line) =>
        Assert.Throws<ArgumentException>(() => FieldExpRates.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void TheDescriptionNamesEveryRule()
    {
        Assert.Equal("none (the client's rates)", FieldExpRates.Describe([]));
        Assert.Equal("Lv 20-30 200%, Lv 31-40 400%", FieldExpRates.Describe(FieldExpRates.Parse("20 30 200 31 40 400".Split(' '))));
    }

    private static async Task<(long Field, long Story, long OtherLevel)> ExpOfAsync(params FieldExpRule[] rules)
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-fieldexp-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.FieldExpRates = [.. rules];
        });
        await app.StartAsync();
        try
        {
            var rewards = app.Services.GetRequiredService<RewardService>();
            return (rewards.ScaleFieldExp(Fabre, 30, 100),
                rewards.ScaleFieldExp(AntEggOfTheStory, 30, 100),
                rewards.ScaleFieldExp(Fabre, 50, 100));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    // The Fabre is an open-field monster (_iWildMonsters 1); the story dungeon's Ant Egg is not.
    [ClientDataFact]
    public async Task OnlyFieldMonstersGetTheRateAndOnlyInItsLevelRange() =>
        Assert.Equal((300L, 100L, 100L), await ExpOfAsync(Rule(20, 40, 300)));

    [ClientDataFact]
    public async Task WithNoRuleTheHostPaysTheClientsExp() =>
        Assert.Equal((100L, 100L, 100L), await ExpOfAsync());

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
