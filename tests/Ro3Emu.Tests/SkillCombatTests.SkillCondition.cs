using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Skill 1110801 asks a weapon of subtype 4 or 5 (LC_ConditionList 1100002): the client does not send it without
    /// one, and now the server does not take it either.</summary>
    [ClientDataFact]
    public async Task ASkillThatAsksAWeaponIsRefusedWithoutIt()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1200");
        await Teach("alice", 11108);
        await Loop.InvokeAsync(() =>
        {
            Session("alice").Items.RemoveAll(i => i.Grid is 1 or 2);
            return Task.CompletedTask;
        });
        var target = await Target("alice", stationary: true);
        var cast = new CastSkillReq { SkillId = 1110801, TargetId = target.Uid, TargetPos = target.Pos };
        await alice.SendGameAsync(CombatService.UseSkill, cast);
        Assert.Equal(ErrorCodes.SkillRequirements, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        var data = _app.Services.GetRequiredService<GameData>();
        int weapon = (int)data.Table("ItemConfig").Values.Select(row => new ItemConfig(row)).First(item => item.SubType == 4 && item.EquipPart > 0).Id;
        await HoldInMainHand("alice", weapon);
        await alice.SendGameAsync(CombatService.UseSkill, cast);
        Assert.NotEqual(ErrorCodes.SkillRequirements, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>Skill 1140801 asks subtype 50, which is worn in part 3 and not in a hand: all gear counts, as in
    /// LC_InteractCondition.CheckEquipStatue.</summary>
    [ClientDataFact]
    public async Task ASkillAcceptsTheGearItAsksOutsideTheHands()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1400");
        await Teach("alice", 11408);
        var data = _app.Services.GetRequiredService<GameData>();
        int gear = (int)data.Table("ItemConfig").Values.Select(row => new ItemConfig(row)).First(item => item.SubType == 50 && item.EquipPart == 3).Id;
        await Loop.InvokeAsync(async () =>
        {
            var session = Session("alice");
            using (await CharacterService.Lock(session))
            {
                session.Items.RemoveAll(i => i.Grid != 0);
                session.Items.Add(new Ro3Emu.Server.Accounts.InvItem { Guid = 990003, Entry = gear, Stack = 1, Grid = 3 });
            }
        });
        var target = await Target("alice", stationary: true);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1140801, TargetId = target.Uid, TargetPos = target.Pos });
        Assert.NotEqual(ErrorCodes.SkillRequirements, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }
}
