using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task AntHellNormalAttackAcrossNonWalkableCellStillKillsTarget()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!warp 10540 101.5 43.5");
        var change = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(10540, change.SceneEntry);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = change.SceneEntry });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<SceneService>()
            .TeleportInMapAsync(Session("alice"), new MapPoint(101.5f, 1.19f, 43.5f, 0)));

        var map = _app.Services.GetRequiredService<WorldData>().Map(10540)!;
        Assert.False(map.Nav!.CanWalk(101.5f, 1.19f, 43.5f, 103.5f, 43.5f));
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var template = Assert.IsType<MonsterTemplate>(monsters.Template(5000055));
        var target = await Loop.InvokeAsync(() => monsters.SpawnSingleAsync(10540, 5000055,
            new MapPoint(103.5f, 1.19f, 43.5f, 0),
            customTemplate: template with { RoamDistance = 0, SpeedCm = 0 }));
        Assert.NotNull(target);
        await Loop.InvokeAsync(() => { target.Hp = 1; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Contains(hit.Dsts, d => d.DstID == target.Uid && d.Damage > 0 && d.IsDead);
        Assert.Equal(0, target.Hp);
    }

    [ClientDataTheory]
    [InlineData(1200401, 12004, 117.5f, 6.19f, 163.5f)]
    [InlineData(1201201, 12012, 117.5f, 6.16f, 164.5f)]
    public async Task AntHellFireSkillAcceptedAcrossNonWalkableFloorStillKillsTarget(int skillId, int group,
        float casterX, float casterY, float casterZ)
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100");
        await Teach("alice", group);
        await Gm("alice", FormattableString.Invariant($"!warp 10540 {casterX} {casterZ}"));
        var change = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        Assert.Equal(10540, change.SceneEntry);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = change.SceneEntry });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<SceneService>()
            .TeleportInMapAsync(Session("alice"), new MapPoint(casterX, casterY, casterZ, 0)));

        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var template = Assert.IsType<MonsterTemplate>(monsters.Template(5000055));
        var target = await Loop.InvokeAsync(() => monsters.SpawnSingleAsync(10540, 5000055,
            new MapPoint(126.2496f, 6.19f, 170.8085f, 0),
            customTemplate: template with { RoamDistance = 0, SpeedCm = 0 }));
        Assert.NotNull(target);
        await Loop.InvokeAsync(() => { target.Hp = 1; return Task.CompletedTask; });

        var beforeProgress = Session("alice").Progress!;
        var before = (beforeProgress.Level, beforeProgress.Exp, beforeProgress.JobLevel, beforeProgress.JobExp);
        var reward = _app.Services.GetRequiredService<RewardService>();
        await Loop.InvokeAsync(() => { reward.ResolveKillForTest = (_, _) => ([], 100, 50); return Task.CompletedTask; });
        try
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skillId, TargetId = target.Uid });
            var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.Equal(0, accepted.Result);
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            Assert.Contains(hit.Dsts, d => d.DstID == target.Uid && d.Damage > 0 && d.IsDead);
            Assert.Equal(0, target.Hp);
            Assert.NotNull(target.RewardSource);
            Assert.True(await Character.RewardSourceCommittedAsync(target.RewardSource.Key));
            var afterProgress = Session("alice").Progress!;
            Assert.NotEqual((before.Level, before.Exp), (afterProgress.Level, afterProgress.Exp));
            Assert.NotEqual((before.JobLevel, before.JobExp), (afterProgress.JobLevel, afterProgress.JobExp));
        }
        finally
        {
            await Loop.InvokeAsync(() => { reward.ResolveKillForTest = null; return Task.CompletedTask; });
        }
    }
}
