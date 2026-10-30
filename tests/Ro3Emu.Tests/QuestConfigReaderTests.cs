using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>自造資料驗證移入具名 reader 後，主線分支及名稱參數仍保留上游的解碼契約。</summary>
public sealed class QuestConfigReaderTests
{
    [Fact]
    public void BranchChoicesPreserveOrderNumericFormsAndDuplicates()
    {
        using var fixture = new Parameters("""
            { "GuideMainQuestBranchChoice": ["77", 5.9, 0, -4, {}, null, 77] }
            """);
        Assert.Equal([77L, 5L, 77L], PublicParamConfig.GuideMainQuestBranchChoice(fixture.Data));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"GuideMainQuestBranchChoice\":{}}")]
    [InlineData("{\"GuideMainQuestBranchChoice\":null}")]
    public void MissingOrNonListBranchChoicesDoNotInventAnOffer(string json)
    {
        using var fixture = new Parameters(json);
        Assert.Empty(PublicParamConfig.GuideMainQuestBranchChoice(fixture.Data));
    }

    [Theory]
    [InlineData("[\"invented text\",\"17\"]", 17L)]
    [InlineData("[\"invented text\",\"9007199254740993\"]", 9007199254740993L)]
    [InlineData("[\"invented text\",17]", 0L)]
    [InlineData("[\"invented text\",\"17.4\"]", 0L)]
    [InlineData("[\"invented text\"]", 0L)]
    public void NameIntegerArgumentKeepsStringOnlyExactIntegerContract(string name, long expected)
    {
        using var document = JsonDocument.Parse("{\"_iName\":" + name + "}");
        Assert.Equal(expected, new TaskConfig(document.RootElement).NameIntegerArgument);
    }

    private sealed class Parameters : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-quest-params-" + Guid.NewGuid().ToString("N"));
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
