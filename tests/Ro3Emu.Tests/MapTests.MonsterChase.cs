using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class MapTests
{
    [ClientDataFact]
    public void MonsterLeashUsesTheAuthoredChaseDistanceRatherThanTheUnfightTimer()
    {
        var monsters = _app.Services.GetRequiredService<MonsterService>();

        // AI 25000003: _iChaseDistance=2000 cm, _iUnFightParam[0]=1500 ms.
        Assert.Equal(20f, monsters.Template(15000057)!.LeashDistance);

        // AI 25000011: _iChaseDistance=1000000 cm, _iUnFightParam[0]=15000 ms.
        Assert.Equal(10_000f, monsters.Template(6020023)!.LeashDistance);
    }
}
