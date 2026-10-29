using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Battle Merit's rules on invented tables (no client data, so CI runs them): the summed rank conditions, the
/// repeat and power scaling, the day's cap and a claim's refusals.</summary>
public sealed class BattleGloryRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-glory-rules-" + Guid.NewGuid().ToString("N"));
    private readonly BattleGloryRules _rules;

    public BattleGloryRulesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Write("BattleGloryDan", new object[]
        {
            new { _iID = 1, _iGloryCondition = 10, _iNormalRewardGroup = 0, _iRankCondition = 0 },
            new { _iID = 2, _iGloryCondition = 20, _iNormalRewardGroup = 7, _iRankCondition = 0 },
            new { _iID = 3, _iGloryCondition = 0, _iNormalRewardGroup = 7, _iRankCondition = 0 },
            new { _iID = 4, _iGloryCondition = 0, _iNormalRewardGroup = 7, _iRankCondition = 5 },
        });
        Write("BattleGloryKillDif", new object[] { new { _iID = 1, _iCount = 1, _iCoe = 10000 }, new { _iID = 2, _iCount = 2, _iCoe = 5000 }, new { _iID = 3, _iCount = 3, _iCoe = 0 } });
        Write("BattleGloryDeathDif", new object[] { new { _iID = 1, _iCount = 1, _iCoe = 10000 }, new { _iID = 2, _iCount = 2, _iCoe = 2500 } });
        Write("BattleGloryPowerDif", new object[] { new { _iID = 1, _iBattle = 100, _iCoe = 15000 }, new { _iID = 2, _iBattle = -100, _iCoe = 5000 }, new { _iID = 3, _iBattle = -300, _iCoe = 0 } });
        _rules = new BattleGloryRules(new GameData(Options.Create(new ServerOptions { GameDataDir = _root }), NullLogger<GameData>.Instance));
    }

    private void Write(string table, object rows) =>
        File.WriteAllText(Path.Combine(_root, "config", table + ".json"), JsonSerializer.Serialize(rows));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public void ARankNeedsTheSummedConditionsOfTheRanksBelowAndTheLeaderboardRankIsNotOne()
    {
        Assert.Equal(1, _rules.RankFor(0));
        Assert.Equal(1, _rules.RankFor(9));
        Assert.Equal(2, _rules.RankFor(10));
        Assert.Equal(2, _rules.RankFor(29));
        Assert.Equal(3, _rules.RankFor(30));
        Assert.Equal(3, _rules.RankFor(1_000_000)); // rank 4 is the leaderboard's (_iRankCondition)
    }

    [Fact]
    public void RepeatsDecayByTheirOwnTableAndKillsScaleByThePowerGap()
    {
        Assert.Equal(100, _rules.Points(TerritoryWarsService.GloryKind.Kill, 100, 1, 0));
        Assert.Equal(50, _rules.Points(TerritoryWarsService.GloryKind.Kill, 100, 2, 0));
        Assert.Equal(0, _rules.Points(TerritoryWarsService.GloryKind.Kill, 100, 3, 0));
        Assert.Equal(0, _rules.Points(TerritoryWarsService.GloryKind.Assist, 100, 7, 0)); // past the table: its last row
        Assert.Equal(25, _rules.Points(TerritoryWarsService.GloryKind.Death, 100, 2, null));
        Assert.Equal(150, _rules.Points(TerritoryWarsService.GloryKind.Kill, 100, 1, 150)); // a stronger victim
        Assert.Equal(50, _rules.Points(TerritoryWarsService.GloryKind.Kill, 100, 1, -150)); // a weaker one
        Assert.Equal(0, _rules.Points(TerritoryWarsService.GloryKind.Kill, 100, 1, -300));
        Assert.Equal(100, _rules.Points(TerritoryWarsService.GloryKind.Kill, 100, 1, 99)); // inside the first threshold
    }

    [Fact]
    public void TheDayTakesWhatItsCapLeaves()
    {
        Assert.Equal(40, BattleGloryRules.Cap(40, 0, 100));
        Assert.Equal(10, BattleGloryRules.Cap(40, 90, 100));
        Assert.Equal(0, BattleGloryRules.Cap(40, 100, 100));
        Assert.Equal(40, BattleGloryRules.Cap(40, 1_000, 0)); // no limit configured
    }

    [Fact]
    public void ARankRewardIsClaimedOnceWhenReachedAndOnlyForRewardRanks()
    {
        var glory = new TerritoryWarsService.GloryState { Rank = 2 };
        Assert.Equal(ErrorCodes.FunctionLocked, _rules.ClaimError(2, glory, functionOpen: false));
        Assert.Equal(ErrorCodes.RewardMissing, _rules.ClaimError(1, glory, true)); // no reward group
        Assert.Equal(ErrorCodes.RewardMissing, _rules.ClaimError(4, glory, true)); // the leaderboard rank
        Assert.Equal(ErrorCodes.NoRankReward, _rules.ClaimError(3, glory, true));
        Assert.Equal(0, _rules.ClaimError(2, glory, true));
        glory.Claimed.Add(2);
        Assert.Equal(ErrorCodes.RewardClaimed, _rules.ClaimError(2, glory, true));
    }

    [Fact]
    public void TheChampionRankNeedsTheTopPointsRankAndAPlaceWithinItsRankCondition()
    {
        // Rank 4 is the champion row (_iRankCondition 5); rank 3 is the highest points rank.
        Assert.False(_rules.ChampionPossible(2));
        Assert.True(_rules.ChampionPossible(3));
        Assert.True(_rules.IsChampion(3, 1));
        Assert.True(_rules.IsChampion(3, 5));
        Assert.False(_rules.IsChampion(3, 6)); // past the top 5
        Assert.False(_rules.IsChampion(3, 0)); // not on the board
        Assert.False(_rules.IsChampion(2, 1)); // first on the board, but not the top points rank
    }

    [Fact]
    public void ASeasonsPointsAreReadFromTheCurrentStateOrItsArchive()
    {
        var role = new TerritoryWarsService.RoleState
        {
            Glory = new TerritoryWarsService.GloryState { Epoch = "s2", Points = 40 },
            GlorySeasons = { ["s1"] = new TerritoryWarsService.GloryState { Epoch = "s1", Points = 70 } },
        };
        byte[] saved = JsonSerializer.SerializeToUtf8Bytes(role);
        Assert.Equal(40, TerritoryWarsService.GloryPointsOf(saved, "s2"));
        Assert.Equal(70, TerritoryWarsService.GloryPointsOf(saved, "s1"));
        Assert.Equal(0, TerritoryWarsService.GloryPointsOf(saved, "s3"));
        Assert.Equal(0, TerritoryWarsService.GloryPointsOf(saved, ""));
    }
}
