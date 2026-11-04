using Romsg;
using Ro3Emu.Server.Game;
using Microsoft.Extensions.DependencyInjection;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Skill 1110601 is a single-target row released without a lock (_iRelease 6, _iRangeType 1, 5 m): the
    /// target id comes from the client, and nothing but this check measures it.</summary>
    [ClientDataFact]
    public async Task AnUnlockedSingleTargetSkillDoesNotHitATargetOutOfReach()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1200");
        await Teach("alice", 11106);
        var target = await Target("alice", stationary: true);
        var a = Session("alice");
        long full = target.Hp;
        await Loop.InvokeAsync(() =>
        {
            target.X = a.Position.X + 40;
            target.Z = a.Position.Z;
            return Task.CompletedTask;
        });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1110601, TargetId = target.Uid, TargetPos = a.Position.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Task.Delay(1500);
        Assert.Equal(full, await Loop.InvokeAsync(() => Task.FromResult(target.Hp)));

        await Loop.InvokeAsync(() =>
        {
            target.X = a.Position.X + 2;
            target.Z = a.Position.Z;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1110601, TargetId = target.Uid, TargetPos = a.Position.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser, r => r.Result == 0)).Result);
        var deadline = DateTime.UtcNow + Wait;
        while (await Loop.InvokeAsync(() => Task.FromResult(target.Hp)) == full && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(target.Hp)) < full);
    }

    /// <summary>Eddga: UnitConfig cylinder 300 x NPCConfig _iModelRatio 0.8 = 2.4 m, added to the swing's 3 m as the client does.</summary>
    [ClientDataFact]
    public async Task AMeleeSwingReachesPastABigTargetsCentre()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        var target = await Target("alice", stationary: true, npcId: 5300001);
        var a = Session("alice");
        Assert.Equal(2.4f, target.Template.DamageRadius, 3);
        async Task<int> CastFrom(float metres)
        {
            await Loop.InvokeAsync(() =>
            {
                target.X = a.Position.X + metres;
                target.Z = a.Position.Z;
                return Task.CompletedTask;
            });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1001001, TargetId = target.Uid, TargetPos = a.Position.Clone() });
            return (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result;
        }
        Assert.Equal(ErrorCodes.SkillDistance, await CastFrom(6.5f));
        Assert.Equal(0, await CastFrom(4f));
    }

    [Theory]
    [InlineData(2, new long[] { 300, 800 }, 0.8, 0, 0, 2.4)] // Eddga
    [InlineData(3, new long[] { 200 }, 0.0, 0, 0, 2.0)] // no ratio counts as 1
    [InlineData(2, new long[] { 100, 300 }, 1.0, 0, 0, 0.0)] // up to MinCollisionCircleRadius does not count
    [InlineData(1, new long[] { 1100, 2000 }, 1.0, 0, 0, 5.5)] // a box: half its shorter side, with no minimum
    [InlineData(0, new long[0], 0.8, 200, 0, 2.0)] // no collider: the prefab radius, without the ratio
    [InlineData(2, new long[] { 300 }, 1.0, 0, 1, 0.0)] // a bullet box shape
    public void ATargetsBodyFollowsItsCollider(int type, long[] size, double ratio, int prefab, int bullet, double radius)
    {
        var unit = new Ro3Emu.Server.Data.Config.UnitConfig(System.Text.Json.JsonSerializer.SerializeToElement(new { _iCollisionType = type, _kCollisionSize = size, _iBulletBoxShape = bullet }));
        var npc = new Ro3Emu.Server.Data.Config.NPCConfig(System.Text.Json.JsonSerializer.SerializeToElement(new { _iModelRatio = ratio, _iPrefabRadius = prefab }));
        Assert.Equal(radius, unit.DamageRadius(npc, 110), 3);
    }
}
