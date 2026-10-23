using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    // Issue #45: with split 1 the caster's client shows the running total (Dmg_Accumulate) next to an equal hit word.
    [ClientDataFact]
    public async Task SkillHitSplitsIntoTheSkillsShownHitCount()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1200"); await Teach("alice", 11120);
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });
        var pierce = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1112001);
        Assert.NotNull(pierce);
        Assert.True(pierce.Value.ShowDmg[0] > 1);

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1112001, TargetId = monster.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
        Assert.Equal(pierce.Value.ShowDmg[0], hit.Split);
    }

    [ClientDataFact]
    public async Task SkillHitWithoutShownHitCountIsOneWord()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100"); await Teach("alice", 11002);
        var monster = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { monster.Hp = 1_000_000; return Task.CompletedTask; });
        Assert.Empty(SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1100201)!.Value.ShowDmg);

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100201 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
        Assert.Equal(1, hit.Split);
    }
}
