using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    // Scouting (10103107 for the Ranger line): "[Detect Stealth] range +${1} meters", the scope-1 tuple
    // ["1", "109", "4", "1", "300", "0"] on the detection category: Detecting's 500 becomes 800.
    [ClientDataFact]
    public async Task ScoutingWidensTheDetectionRadius()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 3400");
        await ChooseProfessionTalent(alice, "alice", 10103107);
        var a = Session("alice");
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 1310501, 60000, a.Role!.Id));
        Assert.Equal(800, await Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<CombatService>().DetectionRadiusCm(a))));
    }

    // Falcon's Might (10102129 for the Hunter line, bought on the way to 10102130): SpecialEffect [20, 3, 215, 100], "Damage Increase: ${1}% while fighting
    // alongside [Falcon]". The falcon is the leased pendant CartRemouldTable 1500100 (_iSubType 3).
    [ClientDataFact]
    public async Task FalconsMightCountsOnlyWithTheFalconLeased()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 3200");
        await ChooseProfessionTalent(alice, "alice", 10102130);
        var a = Session("alice");
        long without = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(a)[215]));
        long with = await Loop.InvokeAsync(async () =>
        {
            a.CareerSpecial!.PendantSlot ??= new DBCareerSpecialPendantSlot();
            a.CareerSpecial.PendantSlot.CartRemouldCfg = 1500100;
            await Character.RefreshStatsAsync(a);
            return Character.Stats(a)[215];
        });
        Assert.Equal(without + 100, with);
    }

    // Shield Bash Echo (10104107 for the Royal Guard): "When casting [Rapid Smiting], there is a ${1}% Chance to change the
    // Cooldown of your next Shield Boomerang to @{1} (0) seconds ... up to @{2} (1) times every ${2} (8) seconds."
    [ClientDataFact]
    public async Task ShieldBashEchoResetsShieldBoomerangOnRapidSmiting()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 1410"); await Teach("alice", 11418); await Teach("alice", 11504);
        await ChooseProfessionTalent(alice, "alice", 10104107);
        _app.Services.GetRequiredService<TalentProcs>().Roll = _ => 0;
        var a = Session("alice");
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1141801, TargetId = monster.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(a.SkillModeCds.GetValueOrDefault((11418, 0)) > MonsterService.NowMs);
        await Task.Delay(1500);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1150401, TargetId = monster.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var reset = await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser,
            n => n.Infos.Any(i => i.SkillId == 11418 && i.CdEndTimestampMs == i.CdStartTimestampMs));
        Assert.NotNull(reset);
        Assert.False(a.SkillModeCds.GetValueOrDefault((11418, 0)) > MonsterService.NowMs);
    }
}
