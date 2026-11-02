using System.Text.Json;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>A pool slot's picks take different rows by weight (DropPicks). The rows here are invented.</summary>
public sealed class DropPicksTests
{
    private static List<JsonElement> Rows(params int[] weights) =>
        [.. weights.Select((weight, id) => JsonSerializer.SerializeToElement(new { _iID = id, _iWeights = weight }))];

    private static int[] Ids(IEnumerable<JsonElement> rows) => [.. rows.Select(row => row.GetProperty("_iID").GetInt32())];

    [Fact]
    public void AllRowsComeOnceWhenThePicksCoverThem()
    {
        var random = new Random(1);
        Assert.Equal([0, 1, 2], Ids(DropPicks.Distinct(Rows(1, 1, 1), 3, random.NextInt64)).Order());
        Assert.Equal([0, 1, 2], Ids(DropPicks.Distinct(Rows(1, 1, 1), 10, random.NextInt64)).Order());
    }

    [Fact]
    public void FewerPicksThanRowsTakeThatManyDifferentRows()
    {
        var random = new Random(2);
        for (int i = 0; i < 200; i++)
        {
            int[] ids = Ids(DropPicks.Distinct(Rows(5, 1, 1, 1), 2, random.NextInt64));
            Assert.Equal(2, ids.Distinct().Count());
        }
    }

    [Fact]
    public void AZeroWeightRowIsNeverTaken()
    {
        var random = new Random(3);
        Assert.Equal([1], Ids(DropPicks.Distinct(Rows(0, 1, 0), 5, random.NextInt64)));
    }

    [Fact]
    public void NoRowsTakeNothing() => Assert.Empty(DropPicks.Distinct(Rows(), 5, _ => 0));

    [Fact]
    public void AHeavierRowIsTakenFirstMoreOften()
    {
        var random = new Random(4);
        int heavy = 0;
        for (int i = 0; i < 1000; i++)
            if (Ids(DropPicks.Distinct(Rows(9, 1), 1, random.NextInt64))[0] == 0) heavy++;
        Assert.InRange(heavy, 850, 950);
    }
}
