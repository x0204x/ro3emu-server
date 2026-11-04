using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact] // "disappears after the target takes damage"
    public async Task LexAeternaEndsOnTheNextHit()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var a = Session("alice");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await buffs.AddSupportAsync(target, 1410201, 10000, a.Role!.Id);
            Assert.True(buffs.HasUnitCombatBuffs(target));
            await buffs.BreakHitControlsAsync(target);
            Assert.False(buffs.HasUnitCombatBuffs(target));
        });
    }

    [ClientDataFact] // "up to 4 Other enemies within 5 meters of the target"
    public async Task ChainLightningJumpsToOthersNearItsTarget()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2400"); await Teach("alice", 12313);
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CritRolls(1);
        var a = Session("alice");
        a.GmCombat |= GmCombatSwitches.Unlimited;
        var first = await Target("alice", stationary: true);
        var near = await Target("alice", stationary: true);
        var far = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            foreach (var (m, metres) in new[] { (first, 3f), (near, 6f), (far, 12f) })
            {
                m.Template = m.Template with { MaxHp = 10_000_000 };
                m.Hp = 10_000_000;
                m.X = a.Position.X + metres;
                m.Z = a.Position.Z;
            }
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1231301, TargetId = first.Uid, TargetPos = first.Pos.Clone(), Face = a.Face });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.Dsts.Count > 0);
        Assert.Contains(hit.Dsts, d => d.DstID == first.Uid);
        Assert.Contains(hit.Dsts, d => d.DstID == near.Uid);
        Assert.DoesNotContain(hit.Dsts, d => d.DstID == far.Uid);
    }

    [ClientDataFact] // owner's decision 2.11: a cutscene protects for at most 120 s
    public async Task ACutsceneProtectsThePlayer()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        a.InCutScene = true;
        Assert.True(a.CutSceneSinceMs > 0);
        long hp = Character.Stats(a)[AttributeId.Hp];
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(() => combat.EnvironmentDamageAsync(a, 10));
        Assert.Equal(hp, Character.Stats(a)[AttributeId.Hp]);
    }

    [ClientDataFact] // 1 is enum_AutoNormalAttackType NotInterrupt on these four rows' groups
    public void SomeRowsLeaveTheAttackRunning()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        Assert.Contains(data.Table("SkillConfig").Values, r => new Ro3Emu.Server.Data.Config.SkillConfig(r).IfAutoAtk == 1);
    }
}
