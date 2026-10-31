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
        AirshipStoryTestPace();
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10103210"); // LAB 前置；正式登船鏈另有專項驗證。
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = scene }));
        long instance = await Load(alice, scene);
        await AirshipStoryThroughCrew(alice, instance);
        var captain = Assert.Single(await ActorsIn(alice, 1, actor => actor.NpcId == 5300038));
        Assert.Equal(5300038, captain.NpcId);
        await Beside(alice, captain.X, captain.Y, captain.Z);
        Assert.Equal(0, await Interact(alice, captain, 5300035)); // dialogue 530003802
        Assert.Equal(0, await Interact(alice, captain, 5300039)); // its option, message 5300039
        await Story(alice, 130161);
        await PlotEnd(alice, 130161);
        await Task.Delay(100);
        Assert.Equal(6, Session(alice).DungeonAttempt!.Wave); // 第一段影片只開 QTE。
        Assert.False(Session(alice).DungeonAttempt!.Settled);

        // The client starts this story from QTEEventConfig's success/failure event.
        // Its action 601 sends 84011 only after the branch video ends.
        await PlotEnd(alice, ending);
        await AirshipStorySuccess(alice);
    }
}
