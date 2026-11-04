using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>Three dummies: the aim, one within the text's reach and one beyond. How many of them a cast strikes.</summary>
    private async Task<int> StruckAsync(int skill, float near, float far, bool lockTarget)
    {
        await using var alice = await InWorld("Alice");
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, skill)!.Value;
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job " + row.Job); await Teach("alice", (int)row.SkillId);
        var session = Session("alice");
        session.GmCastSkill = skill;
        var start = session.Position.Clone();
        MonsterUnit[] dummies = [await Target("alice", stationary: true, npcId: 5000002), await Target("alice", stationary: true, npcId: 5000002), await Target("alice", stationary: true, npcId: 5000002)];
        await Loop.InvokeAsync(() =>
        {
            foreach (var other in _app.Services.GetRequiredService<World>().Map(session.WorldScene).Units.OfType<MonsterUnit>())
                if (!dummies.Contains(other)) { other.X += 200; other.Z += 200; }
            float[] at = [2, 2 + near, 2 + far];
            for (int i = 0; i < 3; i++)
            {
                dummies[i].StaysOutOfBattle = true; dummies[i].Hp = 1000000;
                dummies[i].X = start.X + at[i]; dummies[i].Y = start.Y; dummies[i].Z = start.Z;
            }
            return Task.CompletedTask;
        });
        var point = dummies[0].Pos;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        {
            SkillId = skill, TargetId = lockTarget ? dummies[0].Uid : 0, TargetPos = point, Face = MonsterService.Angle(start.X, start.Z, point.X, point.Z),
        });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => dummies.Any(m => m.Uid == d.DstID)));
        return hit.Dsts.Count(d => dummies.Any(m => m.Uid == d.DstID));
    }

    [ClientDataTheory]
    [InlineData(1310201, 2.5f, 3.5f, true, 2)] // Blitz Beat: others within 3 m of the target
    [InlineData(1600601, 1.5f, 2.5f, true, 2)] // Cart Revolution: others within 2 m
    [InlineData(1631201, 5f, 7f, true, 2)]     // Arm Cannon: others within 6 m
    [InlineData(1320401, 3f, 30f, true, 2)]    // Sharp Shooting: in a line, within its 18 m
    [InlineData(1330501, 3f, 30f, true, 2)]    // Focused Arrow Strike: in a line
    [InlineData(1631501, 3f, 30f, true, 2)]    // Flame Launcher: a 7 * 15 rectangle in front
    [InlineData(1510501, 2.5f, 3.5f, true, 2)] // Envenomed Blade: within 3 m of the target
    [InlineData(1530301, 1f, 30f, false, 2)]   // Rolling Cutter: within 5 m of you
    [InlineData(1530601, 1f, 30f, false, 2)]   // Evil Spirit Threat: within 5 m
    [InlineData(1510601, 2.5f, 3.5f, false, 2)] // Virus Spread: around the point, within 3 m
    [InlineData(1511001, 2.5f, 3.5f, true, 2)] // Venom Splasher: after its fuse, the target and others within 3 m
    public async Task AnAreaNamedByTheTextReachesItsTargets(int skill, float near, float far, bool lockTarget, int struck) =>
        Assert.Equal(struck, await StruckAsync(skill, near, far, lockTarget));

    [ClientDataTheory] // "If the target is a Blacksmith class, grants an Extra": another class gets only the first buff
    [InlineData(6200, true)]
    [InlineData(1100, false)]
    public async Task AdrenalineRushExtraIsForTheBlacksmithLine(int job, bool extra)
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job " + job); await Teach("alice", 16105);
        var a = Session("alice");
        a.GmCastSkill = 1610501;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1610501, TargetPos = a.Position.Clone(), Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var deadline = DateTime.UtcNow + Wait;
        while (!a.Buffs.Any(b => b.ConfigId == (extra ? 1610801 : 1610501)) && DateTime.UtcNow < deadline) await Task.Delay(50);
        await Task.Delay(300); // the second buff would land just after the first
        Assert.Equal(extra, a.Buffs.Any(b => b.ConfigId == 1610801));
        Assert.Contains(a.Buffs, b => b.ConfigId == 1610501);
    }

    [ClientDataFact] // "within a 5-meter radius centered 1 meters in front of You": 5.5 m ahead counts, 5.5 m behind does not
    public async Task ShadowBreaksCircleSitsAheadOfTheCaster()
    {
        await using var alice = await InWorld("Alice");
        var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1532401)!.Value.Row;
        var at = new Vec3 { X = 0, Y = 0, Z = 0 };
        var ahead = new Vec3 { X = 0, Y = 0, Z = 5.5f };
        var behind = new Vec3 { X = 0, Y = 0, Z = -5.5f };
        Assert.True(CombatService.SkillAreaContains(row, at, at, 0, ahead));
        Assert.False(CombatService.SkillAreaContains(row, at, at, 0, behind));
    }
}
