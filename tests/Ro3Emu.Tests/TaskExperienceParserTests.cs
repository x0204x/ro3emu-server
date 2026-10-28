using System.Text.Json;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class TaskExperienceParserTests
{
    [Fact]
    public void CurrentEmptyAndBaseExperienceShapesKeepTheirMeaning()
    {
        Assert.Equal(0, TaskService.ParseTaskBaseExperience(42, Array.Empty<long>()));
        Assert.Equal(0, TaskService.ParseTaskBaseExperience(42, [1, 0]));
        Assert.Equal(7, TaskService.ParseTaskBaseExperience(42, [1, 7]));

        using var missing = JsonDocument.Parse("{}");
        using var empty = JsonDocument.Parse("""{"_kEXP":[]}""");
        using var exportedEmpty = JsonDocument.Parse("""{"_kEXP":{}}""");
        Assert.Equal(0, TaskService.ParseTaskBaseExperience(42, missing.RootElement));
        Assert.Equal(0, TaskService.ParseTaskBaseExperience(42, empty.RootElement));
        Assert.Equal(0, TaskService.ParseTaskBaseExperience(42, exportedEmpty.RootElement));
    }

    [Fact]
    public void MalformedUnknownAndNegativeExperienceFailClosed()
    {
        long[][] rejected =
        [
            [1],
            [1, 7, 9],
            [1, -1],
            [2, 100],
            [99, 100]
        ];

        foreach (long[] encoded in rejected)
        {
            var error = Assert.Throws<InvalidDataException>(() => TaskService.ParseTaskBaseExperience(42, encoded));
            Assert.Contains("TaskConfig 42 _kEXP", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NonArrayAndNonIntegerJsonFailClosed()
    {
        foreach (string json in new[] { """{"_kEXP":7}""", """{"_kEXP":[1,"7"]}""", """{"_kEXP":[1,1.5]}""" })
        {
            using var document = JsonDocument.Parse(json);
            var error = Assert.Throws<InvalidDataException>(() => TaskService.ParseTaskBaseExperience(42, document.RootElement));
            Assert.Contains("TaskConfig 42 _kEXP", error.Message, StringComparison.Ordinal);
        }
    }
}
