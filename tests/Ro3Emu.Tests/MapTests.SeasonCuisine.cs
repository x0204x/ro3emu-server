using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class MapTests
{
    private const long SeasonCuisineFunction = 3614;

    [ClientDataFact]
    public async Task SeasonCuisineStaysClosedAt31And33ThenPublishesItsUnlockAt34()
    {
        await using var alice = await InWorld("CuisineGate");
        var initial = await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser,
            message => message.SyncAllMark);
        Assert.False(initial.FunctionOpenInfos.ContainsKey(SeasonCuisineFunction));
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var functions = _app.Services.GetRequiredService<FunctionService>();
        foreach (int level in new[] { 31, 33 })
        {
            await Gm("cuisinegate", "!lvl " + level);
            Assert.False(await loop.InvokeAsync(() => Task.FromResult(functions.Build(
                sessions.All().Single(session => session.Account == alice.Account && session.Role != null))
                .FunctionOpenInfos.ContainsKey(SeasonCuisineFunction))));
        }
        await Gm("cuisinegate", "!lvl 34");
        var unlocked = await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser,
            message => !message.SyncAllMark && message.FunctionOpenInfos.TryGetValue(SeasonCuisineFunction, out var value) && value.All);
        Assert.True(unlocked.FunctionOpenInfos[SeasonCuisineFunction].All);
    }
}
