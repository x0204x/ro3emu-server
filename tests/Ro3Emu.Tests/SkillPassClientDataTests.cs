using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Ant Hell 10540 positions from the client's sc_anthell02_001 nav and scene-editor data. The target is a grounded
/// monster spawn in group 51. These are geometry regressions, not evidence of a completed client cast.
/// </summary>
public sealed class SkillPassClientDataTests
{
    private const long AntHell = 10540;
    private const float TargetX = 126.2496f;
    private const float TargetY = 6.19f;
    private const float TargetZ = 170.8085f;

    [Fact]
    public void LoadedSceneWithoutAirWallTableHasNoAuthoredWallCells()
    {
        using var doc = JsonDocument.Parse("{}");
        var walls = WorldData.LoadAirWallCells(doc.RootElement);
        Assert.Equal(0, walls.MaxLevel);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyDictionary<(int X, int Z), int>>(walls.Cells));
    }

    [ClientDataTheory]
    [InlineData(1200401)] // Fire Bolt level 1
    [InlineData(1201201)] // Fire Ball level 1
    public void AntHellFireSkillsPassThroughNonWalkableFloorCells(int skillId)
    {
        var (map, data) = AntHellMap();
        var nav = Assert.IsType<NavGrid>(map.Nav);
        var skill = Assert.IsType<SkillConfig>(SkillConfig.Get(data, skillId));
        Assert.Equal(2, skill.PassBlock);
        Assert.True(nav.IsWalkable(117.5f, 6.19f, 163.5f));
        Assert.True(nav.IsWalkable(TargetX, TargetY, TargetZ));

        // The walking ray reaches a non-walkable intermediate cell (120,166), although both endpoints are standable.
        Assert.False(nav.CanWalk(117.5f, 6.19f, 163.5f, TargetX, TargetZ));
        Assert.True(map.CanSkillPass(117.5f, 6.19f, 163.5f, TargetX, TargetY, TargetZ, (int)skill.PassBlock));
    }

    [ClientDataTheory]
    [InlineData(1200401)]
    [InlineData(1201201)]
    public void AntHellFireSkillsPassDiagonalCornerWithoutAnAirWall(int skillId)
    {
        var (map, data) = AntHellMap();
        var nav = Assert.IsType<NavGrid>(map.Nav);
        var skill = Assert.IsType<SkillConfig>(SkillConfig.Get(data, skillId));
        Assert.Equal(2, skill.PassBlock);
        Assert.True(nav.IsWalkable(117.5f, 6.16f, 164.5f));
        Assert.True(nav.IsWalkable(TargetX, TargetY, TargetZ));

        // Walking rejects the (117,164) -> (118,165) diagonal because side cell (117,165) is blocked.
        Assert.False(nav.CanWalk(117.5f, 6.16f, 164.5f, TargetX, TargetZ));
        Assert.True(map.CanSkillPass(117.5f, 6.16f, 164.5f, TargetX, TargetY, TargetZ, (int)skill.PassBlock));
    }

    [ClientDataFact]
    public void SkillPassRejectsPositionsOutsideAntHellNavBounds()
    {
        var (map, _) = AntHellMap();
        Assert.False(map.CanSkillPass(-1f, TargetY, TargetZ, TargetX, TargetY, TargetZ, 2));
        Assert.False(map.CanSkillPass(TargetX, TargetY, TargetZ, -1f, TargetY, TargetZ, 2));
    }

    [ClientDataFact]
    public void AuthoredLevelThreeWallBlocksOnlyLowerPassLevelsAtItsCells()
    {
        var (map, _) = LoadMap(1021301); // sc_pay_fild03_001_01_sceneeditor
        Assert.Equal(3, map.MaxAuthoredAirWallLevel);
        Assert.False(map.HasUntrackedUnitAirWall);
        for (int level = 0; level <= 2; level++)
            Assert.False(map.CanSkillPass(167.5f, 7.24f, 86.5f, 169.5f, 7.24f, 86.5f, level));
        Assert.True(map.CanSkillPass(167.5f, 7.24f, 86.5f, 169.5f, 7.24f, 86.5f, 4));
    }

    [ClientDataFact]
    public void HigherWallElsewhereDoesNotTurnAnUnrelatedNonWalkableCellIntoSkillBlock()
    {
        var (map, _) = LoadMap(1021301);
        var nav = Assert.IsType<NavGrid>(map.Nav);
        Assert.False(nav.CanWalk(344.5f, 0, 61.5f, 346.5f, 61.5f));
        foreach (int level in new[] { 0, 1, 2, 4 })
            Assert.True(map.CanSkillPass(344.5f, 0, 61.5f, 346.5f, 0, 61.5f, level));
    }

    [ClientDataFact]
    public void SiegeUnitAirWallKeepsConservativeBlockedEndpointFallback()
    {
        var (map, _) = LoadMap(2230101);
        Assert.True(map.HasUntrackedUnitAirWall); // UnitAirWallConfig[4900022] in NpcLevelPointRespawnConfig.
        foreach (int level in new[] { 0, 1, 2, 4 })
            Assert.False(map.CanSkillPass(150.5f, 27.15f, 119.5f, 152.38f, 27.15f, 119.53f, level));
    }

    private static (MapWorld Map, GameData Data) AntHellMap() => LoadMap(AntHell);

    private static (MapWorld Map, GameData Data) LoadMap(long scene)
    {
        var options = Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") });
        var data = new GameData(options, NullLogger<GameData>.Instance);
        var world = new WorldData(options, data, NullLogger<WorldData>.Instance);
        return (Assert.IsType<MapWorld>(world.Map(scene)), data);
    }
}
