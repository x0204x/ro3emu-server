using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact] // LC_Buff_System.UpdateBuffData never copies configId: a new level needs a new GUID
    public async Task ANewLevelOfABuffIsANewBuff()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(a, 1150301, 60000, a.Role!.Id);
            long first = a.Buffs.Single(b => b.ConfigId == 1150301).Guid;
            await buffs.AddAsync(a, 1150310, 60000, a.Role!.Id);
            Assert.DoesNotContain(a.Buffs, b => b.ConfigId == 1150301);
            Assert.NotEqual(first, a.Buffs.Single(b => b.ConfigId == 1150310).Guid);
        });
    }

    [ClientDataFact] // the same level keeps its GUID
    public async Task TheSameLevelOfABuffKeepsItsGuid()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(a, 1150301, 60000, a.Role!.Id);
            long first = a.Buffs.Single(b => b.ConfigId == 1150301).Guid;
            await buffs.AddAsync(a, 1150301, 60000, a.Role!.Id);
            Assert.Equal(first, a.Buffs.Single(b => b.ConfigId == 1150301).Guid);
        });
    }

    [ClientDataFact] // a shield on a monster takes a player's hit before its HP
    public async Task AMonstersShieldTakesTheHitFirst()
    {
        await using var alice = await InWorld("Alice");
        var (bot, a, dummy) = await ReadyAsync(alice, 1100, 10010, 1001001);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(() => buffs.AddSupportAsync(dummy, 1150301, 60000, a.Role!.Id, shieldAmount: 100000000));
        await CastAtAsync(bot, 1001001, dummy);
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == dummy.Uid));
        var info = hit.Dsts.Single(d => d.DstID == dummy.Uid);
        Assert.Equal(0, info.Damage);
        Assert.True(info.SheildDamage > 0);
        Assert.Equal(1000000, dummy.Hp);
    }

    [ClientDataFact] // Heal: EffectData 14007800 holds the heal's effect and no flinch
    public async Task AHealShowsItsOwnEffect()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 1400"); await Teach("alice", 14007);
        var a = Session("alice");
        a.GmCastSkill = 1400701;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400701, TargetId = a.Role!.Id, TargetPos = a.Position.Clone(), Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var heal = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DamageType == DamageType.Heal));
        Assert.Equal(14007800, heal.ShowPoint);
    }
}
