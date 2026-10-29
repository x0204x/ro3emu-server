using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The commander's tools in a running siege: map marks, gather and command letters, each for its own side.</summary>
public sealed partial class SiegeTests
{
    [ClientDataFact]
    public async Task TheCommanderMarksTheMapGathersAndLettersTargetsForItsOwnSide()
    {
        await using var attack = await Bot("SiegeGm"); // the fixture's GM account seeds the siege
        await using var defense = await Bot("SiegeCmdDef");
        long ga = await Guild(attack, "SgCmdA"), gd = await Guild(defense, "SgCmdD");
        await attack.JoinChatAsync("4_1");
        _clock.Set(_siege.Start + 25);
        await attack.SendChatAsync("4_1", 2, $"!siege seed 1001 {ga} {gd}", channelIndex: 4);
        await Next(attack, GmCommands.BroadcastNotify, BroadCastNotify.Parser);
        await Base(attack, currentCity: 1001);
        await Enter(attack, 1); await Enter(defense, 2);
        // Loading into the battle sends the empty boards.
        await Next(attack, TerritoryWarsService.MarkNotify, DungeonSetMarkPosNtf.Parser, n => n.MarkType == DungeonMarkType.DungeonMarkCommander);
        await Next(attack, TerritoryWarsService.CommandMarkNotify, GvgWarUpdateCommandMarkNotify.Parser);

        // The guild president holds right 12: commander marks go to its side only.
        for (int i = 0; i < 10; i++)
        {
            await attack.SendGameAsync(TerritoryWarsService.SetMark, new DungeonSetMarkPosReq
            {
                Pos = new DungeonMarkPos { MarkType = DungeonMarkType.DungeonMarkCommander, Pos = new Vec3 { X = 150 + i, Y = 27, Z = 120 } },
            });
            Assert.Equal(0, (await Next(attack, TerritoryWarsService.SetMark, DungeonSetMarkPosRsp.Parser)).Result);
        }
        var board = await Next(attack, TerritoryWarsService.MarkNotify, DungeonSetMarkPosNtf.Parser, n => n.Pos.Count == 10);
        Assert.Equal(Enumerable.Range(1, 10), board.Pos.Select(p => p.Id));
        Assert.DoesNotContain(defense.Inbox.Snapshot(), e => e.IsGame(TerritoryWarsService.MarkNotify)
            && DungeonSetMarkPosNtf.Parser.ParseFrom(e.Body).Pos.Count > 0);
        // An eleventh is refused; a team-leader mark needs a party it leads.
        await attack.SendGameAsync(TerritoryWarsService.SetMark, new DungeonSetMarkPosReq
        {
            Pos = new DungeonMarkPos { MarkType = DungeonMarkType.DungeonMarkCommander, Pos = new Vec3 { X = 160, Y = 27, Z = 120 } },
        });
        Assert.Equal(ErrorCodes.Parameter, (await Next(attack, TerritoryWarsService.SetMark, DungeonSetMarkPosRsp.Parser)).Result);
        await attack.SendGameAsync(TerritoryWarsService.SetMark, new DungeonSetMarkPosReq
        {
            Pos = new DungeonMarkPos { MarkType = DungeonMarkType.DungeonMarkTeamLeader, Pos = new Vec3 { X = 160, Y = 27, Z = 120 } },
        });
        Assert.Equal(ErrorCodes.NoPermission, (await Next(attack, TerritoryWarsService.SetMark, DungeonSetMarkPosRsp.Parser)).Result);

        // Delete one by its global id (commander slot 3 = id 3), then gather at slot 4.
        await attack.SendGameAsync(TerritoryWarsService.SetMark, new DungeonSetMarkPosReq { BDel = true, Pos = new DungeonMarkPos { Id = 3 } });
        Assert.Equal(0, (await Next(attack, TerritoryWarsService.SetMark, DungeonSetMarkPosRsp.Parser)).Result);
        await Next(attack, TerritoryWarsService.MarkNotify, DungeonSetMarkPosNtf.Parser, n => n.Pos.Count == 9 && n.Pos.All(p => p.Id != 3));
        await attack.SendGameAsync(TerritoryWarsService.GatherMark, new DungeonGatherMarkPosReq
        {
            GatherType = DungeonGatherType.DungeonGatherMark, Pos = new DungeonMarkPos { Id = 4, MarkType = DungeonMarkType.DungeonMarkCommander },
        });
        Assert.Equal(0, (await Next(attack, TerritoryWarsService.GatherMark, DungeonGatherMarkPosRsp.Parser)).Result);
        var gather = await Next(attack, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == 4110041);
        Assert.Equal(ChatParamType.ChatParamGather, gather.ParamList[1].Type);
        Assert.Equal(["1", "4"], gather.ParamList[1].Params.Skip(1));
        // The gather at the emptied slot is refused.
        await attack.SendGameAsync(TerritoryWarsService.GatherMark, new DungeonGatherMarkPosReq
        {
            GatherType = DungeonGatherType.DungeonGatherMark, Pos = new DungeonMarkPos { Id = 3, MarkType = DungeonMarkType.DungeonMarkCommander },
        });
        Assert.Equal(ErrorCodes.Parameter, (await Next(attack, TerritoryWarsService.GatherMark, DungeonGatherMarkPosRsp.Parser)).Result);

        // Command letters: add two targets, remove the first, clear.
        long[] targets = [36, 46];
        foreach (long point in targets)
        {
            await attack.SendGameAsync(TerritoryWarsService.CommandMark, new GvgWarUpdateCommandMarkReq { UpdateType = CommandMarkUpdateType.Add, MarkId = point });
            Assert.Equal(0, (await Next(attack, TerritoryWarsService.CommandMark, GvgWarUpdateCommandMarkResp.Parser)).Result);
        }
        await Next(attack, TerritoryWarsService.CommandMarkNotify, GvgWarUpdateCommandMarkNotify.Parser,
            n => n.MarkIds.SequenceEqual([36L, 46, 0, 0, 0, 0]));
        await attack.SendGameAsync(TerritoryWarsService.CommandMark, new GvgWarUpdateCommandMarkReq { UpdateType = CommandMarkUpdateType.Remove, MarkId = 36 });
        await Next(attack, TerritoryWarsService.CommandMarkNotify, GvgWarUpdateCommandMarkNotify.Parser,
            n => n.MarkIds.SequenceEqual([0L, 46, 0, 0, 0, 0]));
        await attack.SendGameAsync(TerritoryWarsService.CommandMark, new GvgWarUpdateCommandMarkReq { UpdateType = CommandMarkUpdateType.Clear });
        await Next(attack, TerritoryWarsService.CommandMarkNotify, GvgWarUpdateCommandMarkNotify.Parser, n => n.MarkIds.All(id => id == 0));
        Assert.DoesNotContain(defense.Inbox.Snapshot(), e => e.IsGame(TerritoryWarsService.CommandMarkNotify)
            && GvgWarUpdateCommandMarkNotify.Parser.ParseFrom(e.Body).MarkIds.Any(id => id != 0));
    }
}
