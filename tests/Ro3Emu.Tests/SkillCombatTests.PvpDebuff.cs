using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Skill 1111201 has a sure Stun in its _kBuff2 (901601, 10000, 1000): it lands on an enemy player as on a monster.</summary>
    [ClientDataFact]
    public async Task ASkillsHostileBuffLandsOnTheEnemyPlayerItHits()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 1200");
        await Teach("alice", 11112);
        await Gm("bob", "!lvl 99");
        var source = Session("alice");
        var victim = Session("bob");
        var start = source.Position.Clone();
        await Loop.InvokeAsync(() =>
        {
            victim.Position = new Vec3 { X = start.X + 2, Y = start.Y, Z = start.Z };
            Combat.PlayerHostile = (a, b) => a == source && b == victim;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1111201, TargetId = victim.Role!.Id, TargetPos = victim.Position });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var deadline = DateTime.UtcNow + Wait;
        bool Stunned() => victim.Buffs.Any(b => b.ConfigId == 901601);
        while (!await Loop.InvokeAsync(() => Task.FromResult(Stunned())) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(Stunned())));
    }
}
