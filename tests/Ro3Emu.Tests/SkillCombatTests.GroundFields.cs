using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task<int> FieldStrikes(string name, int job, int group, int skill, int race = 1, int element = 1, int waitMs = 10000)
    {
        await using var bot = await InWorld(name);
        await Gm(name.ToLowerInvariant(), $"!job {job}"); await Teach(name.ToLowerInvariant(), group);
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CritRolls(1);
        var target = await Target(name.ToLowerInvariant(), stationary: true);
        var s = Session(name.ToLowerInvariant());
        s.GmCombat |= GmCombatSwitches.Unlimited; // the costs are not what this measures
        int strikes = 0;
        Func<GameSession, MonsterUnit, System.Text.Json.JsonElement, long, Task> count = (_, m, _, _) => { if (m == target) strikes++; return Task.CompletedTask; };
        combat.DealtAsync.Add(count);
        await Loop.InvokeAsync(() =>
        {
            target.Template = target.Template with { Race = race, Element = element, MaxHp = 100_000_000 };
            target.Hp = 100_000_000;
            target.X = s.Position.X + 2;
            target.Z = s.Position.Z;
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = s.Face });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Task.Delay(waitMs);
        combat.DealtAsync.Remove(count);
        return strikes;
    }

    [ClientDataFact] // "every 0.5 sec." "For the next 4 sec."
    public async Task MagnusExorcismusStrikesEightTimes() => Assert.InRange(await FieldStrikes("Mag", 4100, 14109, 1410901), 7, 8); // a late tick can lose the last under load

    [ClientDataFact] // "lasts 4 seconds and strikes a total of 8 times"
    public async Task GravitationFieldStrikesEightTimes() => Assert.InRange(await FieldStrikes("Grav", 2200, 12212, 1221201), 7, 8);

    [ClientDataFact] // "The meteor shower strikes in 2 waves"
    public async Task MeteorStormStrikesInItsWaves() => Assert.Equal(2, await FieldStrikes("Met", 2200, 12106, 1210601));

    [ClientDataFact] // "disappear early after being touched by enemies 5 times"
    public async Task FireWallGoesAfterItsTouches() => Assert.Equal(5, await FieldStrikes("Fire", 2100, 12010, 1201001));

    [ClientDataFact] // "every 0.6 seconds": 7 strikes in 4 s, on a Demon
    public async Task SanctuaryHurtsADemon() => Assert.InRange(await FieldStrikes("SanD", 4100, 14115, 1411501, race: 3), 6, 7);

    [ClientDataFact] // only Undead-property and Demon Race monsters take its damage
    public async Task SanctuaryLeavesOtherMonstersAlone() => Assert.Equal(0, await FieldStrikes("SanO", 4100, 14115, 1411501, race: 1));
}
