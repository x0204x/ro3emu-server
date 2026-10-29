using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    // The G client owns QTE 6 and chooses the ending story locally. These are the two
    // wire paths after the same captain dialogue, with their exact video/config linkage.
    [ClientDataTheory]
    [InlineData(130162L, 30162L, "Xinshou_Fktcursh_Part2success.mp4")]
    [InlineData(130163L, 30163L, "Xinshou_Fktcursh_Part2fail.mp4")]
    public async Task AirshipHelmQtePlaysItsEndingBeforeTheDungeonClears(
        long ending, long endingVideo, string endingFile)
    {
        const long scene = 1000501;
        var data = Service<GameData>();
        JsonElement opening = data.Row("PlotInterpretation", 130161)!.Value;
        JsonElement branch = data.Row("PlotInterpretation", ending)!.Value;
        Assert.Equal(30161, GameData.Int(opening, "_iVideoID"));
        Assert.Equal(6, GameData.Int(opening, "_iQTEID"));
        Assert.Equal(endingVideo, GameData.Int(branch, "_iVideoID"));
        Assert.Equal("Xinshou_Fktcursh_part1.mp4", data.Row("VideoConfig", 30161)!.Value.GetProperty("_kVideo").GetString());
        Assert.Equal(endingFile, data.Row("VideoConfig", endingVideo)!.Value.GetProperty("_kVideo").GetString());
        var qte = data.Table("QTEEventConfig").Values.Single(row => GameData.Int(row, "_iQTEID") == 6);
        Assert.Equal((6000L, 3L), (GameData.Int(qte, "_iTime"), GameData.Int(qte, "_iType")));
        Assert.Equal("F", qte.GetProperty("_kShortcutKey").GetString()); // inherited G Lua default
        Assert.Equal(new long[] { 2, 130162 }, qte.GetProperty("_kSuccessEvent").EnumerateArray().Select(x => x.GetInt64()));
        Assert.Equal(new long[] { 2, 130163 }, qte.GetProperty("_kFailureEvent").EnumerateArray().Select(x => x.GetInt64()));

        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await Stage(alice, 1, quest: 1);
        await Gm(alice, "!actors use"); // four opening bubbles; covered directly by ScriptedClient tests

        await Stage(alice, 2, quest: 2);
        foreach (var ship in await WaveIn(instance, 2)) await Kill(alice, ship);
        for (int wave = 3; wave <= 5; wave++)
        {
            await Stage(alice, wave, quest: 3);
            foreach (var boarder in await WaveIn(instance, 3)) await Kill(alice, boarder);
        }
        await Stage(alice, 6, quest: 4);
        await Kill(alice, await BossIn(instance, 5300018));
        await Stage(alice, 7, quest: 5);
        await Gm(alice, "!actors use"); // five postbattle bubbles

        await Stage(alice, 8, quest: 6);
        var captain = Assert.Single(await ActorsIn(alice, 1));
        Assert.Equal(5300038, captain.NpcId);
        await Beside(alice, captain.X, captain.Y, captain.Z);
        Assert.Equal(0, await Interact(alice, captain, 5300035)); // dialogue 530003802
        Assert.Equal(0, await Interact(alice, captain, 5300039)); // its option, message 5300039
        await Story(alice, 130161);
        await PlotEnd(alice, 130161);
        await Task.Delay(100);
        Assert.Equal(7, Session(alice).DungeonAttempt!.Wave); // first video only opens the QTE
        Assert.False(Session(alice).DungeonAttempt!.Settled);

        // The client starts this story from QTEEventConfig's success/failure event.
        // Its action 601 sends 84011 only after the branch video ends.
        await PlotEnd(alice, ending);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
    }
}
