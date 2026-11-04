using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Does a cast's self buff land on a player standing next to the caster, in or out of the caster's party?</summary>
    private async Task<bool> BobGetsAsync(int job, int group, int skill, int buff, bool sameParty)
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job " + job); await Teach("alice", group);
        var (a, b) = (Session("alice"), Session("bob"));
        a.GmCastSkill = skill;
        var combat = _app.Services.GetRequiredService<CombatService>();
        var before = combat.SameParty;
        combat.SameParty = (x, y) => sameParty;
        try
        {
            await Loop.InvokeAsync(() => { b.Position = a.Position.Clone(); return Task.CompletedTask; });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetPos = a.Position.Clone(), Face = a.Face });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            var deadline = DateTime.UtcNow + Wait;
            while (!a.Buffs.Any(x => x.ConfigId == buff) && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.Contains(a.Buffs, x => x.ConfigId == buff);
            await Task.Delay(200);
            return b.Buffs.Any(x => x.ConfigId == buff);
        }
        finally { combat.SameParty = before; }
    }

    [ClientDataFact] // Sacred Lily: "You and Party Members": a stranger stands aside
    public async Task APartyTextSkipsAStranger()
    {
        int buff = (int)SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1430401)!.Value.Buff1[0][0];
        Assert.False(await BobGetsAsync(1430, 14304, 1430401, buff, sameParty: false));
    }

    [ClientDataFact]
    public async Task APartyTextReachesAMember()
    {
        int buff = (int)SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1430401)!.Value.Buff1[0][0];
        Assert.True(await BobGetsAsync(1430, 14304, 1430401, buff, sameParty: true));
    }

    [ClientDataFact] // Magnificat: "for you and up to ${2} allies", under a self-only selector
    public async Task MagnificatReachesAnAlly()
    {
        int buff = (int)SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1410501)!.Value.Buff1[0][0];
        Assert.True(await BobGetsAsync(1410, 14105, 1410501, buff, sameParty: false));
    }

    [ClientDataFact] // Safety Wall: "melee Physical"; a melee magic hit passes through and spends nothing
    public async Task SafetyWallStopsMeleePhysicalOnly()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 1200"); await Teach("alice", 12011);
        var a = Session("alice");
        a.GmCastSkill = 1201101;
        var data = _app.Services.GetRequiredService<GameData>();
        var physical = SkillConfig.Get(data, 1001001)!.Value;
        var magic = new SkillConfig(data.Table("SkillConfig").Values.First(r => new SkillConfig(r).DmgType == 2 && new SkillConfig(r).DistanceType == 1));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1201101, TargetPos = a.Position.Clone(), Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var deadline = DateTime.UtcNow + Wait;
        while (a.SkillObjects.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.NotEmpty(a.SkillObjects);
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(async () =>
        {
            int charges = a.SkillObjects[0].Charges;
            Assert.False(await combat.GroundProtectsAsync(a, magic.Row));
            Assert.Equal(charges, a.SkillObjects[0].Charges);
            Assert.True(await combat.GroundProtectsAsync(a, physical.Row));
        });
    }
}
