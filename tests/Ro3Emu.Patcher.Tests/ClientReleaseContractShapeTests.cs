using Ro3Emu.ClientRelease;

namespace Ro3Emu.Patcher.Tests;

public sealed class ClientReleaseContractShapeTests
{
    private static ClientReleaseRequirement Requirement() => new()
    {
        ServerVersion = "7.8.9", Channel = "test", Sequence = 42, Version = "fixture-release",
        ManifestSha256 = new string('a', 64), PatchSha256 = new string('b', 64),
        BaseClient = new(12345, "fixture-genesis", "fixture-build", new string('c', 64), new string('d', 64)),
    };

    [Fact]
    public void OfficialAndPublisherSequencesHaveIndependentMeaning()
    {
        var requirement = Requirement();
        requirement.Validate();
        Assert.NotEqual(requirement.Sequence, requirement.BaseClient!.Sequence);
        Assert.True(requirement.MatchesServerVersion("7.8.9+fixture-commit"));
        Assert.False(requirement.MatchesServerVersion("7.8.10+fixture-commit"));
        Assert.False((requirement with { ServerVersion = "7.8.9+one" }).MatchesServerVersion("7.8.9+two"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MissingOfficialSequenceIsRejected(long sequence)
    {
        var requirement = Requirement();
        var wrong = requirement with { BaseClient = requirement.BaseClient! with { Sequence = sequence } };
        Assert.Throws<InvalidDataException>(wrong.Validate);
    }

    [Fact]
    public void IncompleteOrUnrecognizedContractCannotBeAccepted()
    {
        var valid = Requirement();
        ClientReleaseRequirement[] invalid =
        [
            valid with { Format = 999 }, valid with { BaseClient = null }, valid with { PatchSha256 = "" },
            valid with { ManifestSha256 = new string('A', 64) }, valid with { Channel = "../other" },
            valid with { ServerVersion = "" }, valid with { Sequence = 0 },
        ];
        foreach (var value in invalid) Assert.Throws<InvalidDataException>(value.Validate);
    }
}
