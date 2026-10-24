using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>
    /// SkillConfig._iIfAutoAtk 2 (enum_AutoNormalAttackType.AutoNormalAttack) on an active skill: the client follows
    /// the cast with the career's normal attack (LC_AutoAttackAppendState: GetNormalSkill). The server's attack loop
    /// repeated Blade Smash itself every swing, past its 7 s cooldown.
    /// </summary>
    [ClientDataFact]
    public async Task AutoNormalAttackSkillIsFollowedByTheNormalAttackNotRepeatedPastItsCooldown()
    {
        const int bladeSmash = 1509901; // group 15099, _iIfAutoAtk 2, _iIsNormalAtk 0, _iCD 7000
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100"); await Teach("alice", 15099);
        int normal = _app.Services.GetRequiredService<SkillService>().NormalAttack(Session("alice"));
        Assert.NotEqual(bladeSmash, normal);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000_000; return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = bladeSmash, TargetId = target.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        Assert.Equal(bladeSmash, (await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
            u => u.SessionId == accepted.SessionId)).SkillID);

        // The loop's next swings, well inside the cooldown, are normal attacks on the same target.
        long self = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Role!.Id));
        for (int i = 0; i < 2; i++)
        {
            var swing = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
                u => u.SrcID == self && u.SessionId != accepted.SessionId);
            Assert.Equal(normal, swing.SkillID);
            Assert.Equal(target.Uid, swing.TargetId);
            Assert.Equal(target.Pos.X, swing.TargetPos.X);
            Assert.Equal(target.Pos.Y, swing.TargetPos.Y);
            Assert.Equal(target.Pos.Z, swing.TargetPos.Z);
            Assert.True(swing.PlayAct);
            Assert.True(swing.SkillMs > 0);
            Assert.True(swing.StartTime > 0);
        }
        await Loop.InvokeAsync(() => { Assert.Equal(normal, Session("alice").Attack?.SkillId); return Task.CompletedTask; });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = bladeSmash, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillCooldown, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>
    /// Clicking an enemy out of range: the client walks, sends the normal attack on reaching range and its stop a few
    /// milliseconds later (in-game 11.10). That stop moved the player off the loop's start point and ended the
    /// auto-attack after one swing, so clicking from afar only ever attacked once.
    /// </summary>
    [ClientDataFact]
    public async Task AnAutoAttackStartedOnTheWayInKeepsSwingingAfterTheArrivalStop()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000_000; return Task.CompletedTask; });
        var start = Session("alice").Position.Clone();
        var nav = _app.Services.GetRequiredService<WorldData>().Map(Session("alice").SceneEntry)!.Nav!;
        var away = new[] { (5f, 0f), (-5f, 0f), (0f, 5f), (0f, -5f), (3.6f, 3.6f), (-3.6f, -3.6f) }
            .Select(o => new Vec3 { X = start.X + o.Item1, Y = start.Y, Z = start.Z + o.Item2 })
            .First(p => nav.IsWalkable(p.X, p.Y, p.Z) && nav.CanWalk(p.X, p.Y, p.Z, start.X, start.Z));
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = away, Face = 180, BStop = true, SpeedRate = 1 });
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = away, Dir = 180, Face = 180, SpeedRate = 1 });
        await Task.Delay(500);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var arrived = new Vec3 { X = start.X + (away.X - start.X) * 0.5f, Y = start.Y, Z = start.Z + (away.Z - start.Z) * 0.5f };
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = arrived, Face = 180, BStop = true, SpeedRate = 1 });

        long self = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Role!.Id));
        var swing = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
            u => u.SrcID == self && u.SessionId != accepted.SessionId);
        Assert.Equal(target.Uid, swing.TargetId);
        await Loop.InvokeAsync(() => { Assert.NotNull(Session("alice").Attack); return Task.CompletedTask; });

        // A real step away still ends it.
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = away, Face = 180, BStop = true, SpeedRate = 1 });
        var until = DateTime.UtcNow.AddSeconds(5);
        while (await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Attack != null)))
        {
            Assert.True(DateTime.UtcNow < until, "the auto-attack outlived a real step away");
            await Task.Delay(20);
        }
    }
}
