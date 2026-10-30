using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
namespace Ro3Emu.Tests;

public sealed class TavernClientDataTests
{
    [ClientDataFact]
    public void ActualCategoriesAndTaskThresholdRemainDistinct()
    {
        var data = new GameData(Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance);
        Assert.Equal(13, data.Table("BarroomTypeConfig").Count);
        Assert.True(TavernService.ValidOption(data, 1, 0));
        Assert.False(TavernService.ValidOption(data, 21, 0));
        Assert.True(TavernService.ValidOption(data, 21, 1));
        Assert.False(TavernService.ValidOption(data, 1, 1));
        Assert.False(TavernService.ValidOption(data, 999, 1));
        Assert.Equal(2000, PublicParamConfig.BarroomTaskId(data));
        Assert.Equal(18, PublicParamConfig.BarRoomNameLen(data));
        Assert.Equal(1000, PublicParamConfig.BarroomPersonNum(data));
        Assert.Equal(new long[] { 106010, 1200 }, TaskConfig.Get(data, 2000)!.Value.ProgressTarget.Single());
    }
}
