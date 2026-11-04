using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact] // Magnum Break: "Basic Attack becomes Fire"; Aspersio Holy; Enchant Poison Poison
    public async Task ABuffChangesANormalAttacksElement()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var row = _app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>().Row("SkillConfig", _app.Services.GetRequiredService<SkillService>().CurrentAttack(a))!.Value;
        Assert.Equal(1, combat.AttackElement(a, row));
        await Loop.InvokeAsync(() => buffs.AddAsync(a, 1100201, 60000, a.Role!.Id));
        Assert.Equal(4, combat.AttackElement(a, row));
    }

    [ClientDataFact] // Piety: "Armor element Holy"
    public async Task PietyMakesTheArmorHoly()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        Assert.Equal(1, _app.Services.GetRequiredService<CombatService>().ArmorElement(a));
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 1162201, 60000, a.Role!.Id));
        Assert.Equal(7, _app.Services.GetRequiredService<CombatService>().ArmorElement(a));
    }

    [Theory]
    [InlineData(602, 2, 0.7)] // a boss
    [InlineData(604, 1, 0.7)] // a normal monster
    [InlineData(602, 1, 1.0)] // only its own kind
    [InlineData(604, 0, 1.0)] // "damage from monsters"
    public void MonsterResistanceFollowsTheAttackersKind(int attr, int attacker, double factor)
    {
        long Sheet(int id) => id == attr ? 3000 : 0;
        Assert.Equal(factor, DamageAttributes.Factor(null, Sheet, new DamageAttributes.Hit(false, false, false, 1, 0, 0, 0, 2, 2, 0, (DamageAttributes.Kind)attacker)), 6);
    }

    [ClientDataFact] // Dragon Howling: "Removes all status ailments"
    public async Task DragonHowlingCleansesThroughItsCast()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1400"); await Teach("alice", 11306);
        var a = Session("alice");
        a.GmCombat |= GmCombatSwitches.Unlimited;
        a.GmCastSkill = 1130601; // its row asks for the Rune Knight's mount
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 902601, 60000, 1));
        Assert.Contains(a.Buffs, b => b.ConfigId == 902601);
        await alice.SendGameAsync(CombatService.UseSkill, new Romsg.CastSkillReq { SkillId = 1130601, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, Romsg.CastSkillRsp.Parser)).Result);
        var deadline = DateTime.UtcNow + Wait;
        while (await Loop.InvokeAsync(() => Task.FromResult(a.Buffs.Any(b => b.ConfigId == 902601))) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.False(await Loop.InvokeAsync(() => Task.FromResult(a.Buffs.Any(b => b.ConfigId == 902601))));
    }
}
