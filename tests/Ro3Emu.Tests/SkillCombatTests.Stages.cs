using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact] // High-Energy Impact: "become Flying ... Tap the skill again or wait until the Flying duration ends to attack Below"
    public async Task HighEnergyImpactTakesOffThenLandsByItself()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 6400, 16307, 1630701);
        await CastAtAsync(bot, 1630701, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Task.Delay(1000);
        Assert.Equal(1000000, dummy.Hp); // in the air
        Assert.Contains(a.Buffs, b => b.ConfigId == 1632901);
        Assert.NotNull(await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == dummy.Uid && d.Damage > 0)));
        await Task.Delay(100);
        Assert.DoesNotContain(a.Buffs, b => b.ConfigId == 1632901);
        Assert.Contains(a.Buffs, b => b.ConfigId == 1633601);
    }

    [ClientDataFact] // Move back Shadow Echo: "Cast again within 6 seconds to return to the shadow"
    public async Task ShadowEchoReturnsOnTheSecondPress()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 5400"); await Teach("alice", 15329);
        var a = Session("alice");
        a.GmCastSkill = 1532901;
        var start = a.Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1532901, TargetPos = start, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var away = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)a.Role!.Id);
        await Task.Delay(500);
        await Loop.InvokeAsync(() => { a.SkillCds.Clear(); a.SkillModeCds.Clear(); return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1532901, TargetPos = away.EndPos, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var back = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)a.Role!.Id);
        Assert.InRange(MonsterService.Distance(start.X, start.Z, back.EndPos.X, back.EndPos.Z), 0f, 0.1f);
    }

    [ClientDataFact] // [Shield Stance]: "At 20 stacks, your next 3 Powerful Shield Bash": a full meter turns into three
    public async Task AFullShieldStanceMakesThreeUltraBashes()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 1410");
        var a = Session("alice");
        await Loop.InvokeAsync(async () =>
        {
            a.Energy[141002].Value = 19;
            await _app.Services.GetRequiredService<BuffService>().AddShieldStanceAsync(a);
            Assert.Equal(0, a.Energy[141002].Value);
            Assert.Equal(3, a.Buffs.Single(b => b.ConfigId == 1163101).PileCount);
        });
    }
}
