using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

public sealed class PublicParamConfigTests
{
    [Theory]
    [InlineData("[10000,12000]")]
    [InlineData("[10000,12000,\"17000\",19000,21000]")]
    [InlineData("[10000,12000,-1,19000,21000]")]
    [InlineData("[10000,12000,17000.5,19000,21000]")]
    [InlineData("[10000,12000,2147483648,19000,21000]")]
    public void MalformedPartyExperienceParametersFailBeforeUse(string encoded)
    {
        using var fixture = new Parameters("{\"TeamExpBuffNum\":" + encoded + "}");
        var error = Assert.Throws<InvalidDataException>(() => PublicParamConfig.TeamExpBuffNum(fixture.Data));
        Assert.Contains("PublicParamConfig.TeamExpBuffNum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingHandmadeParameterHasNoInventedPartyGrades()
    {
        using var fixture = new Parameters("{}");
        Assert.Empty(PublicParamConfig.TeamExpBuffNum(fixture.Data));
    }

    private sealed class Parameters : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-party-params-" + Guid.NewGuid().ToString("N"));
        public GameData Data { get; }

        public Parameters(string json)
        {
            Directory.CreateDirectory(Path.Combine(_root, "config"));
            File.WriteAllText(Path.Combine(_root, "config", "PublicParamConfig.json"), json);
            Data = new GameData(Options.Create(new ServerOptions { GameDataDir = _root }), NullLogger<GameData>.Instance);
        }

        public void Dispose() => Directory.Delete(_root, true);
    }
}
