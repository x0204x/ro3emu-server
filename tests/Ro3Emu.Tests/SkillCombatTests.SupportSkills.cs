using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact] // Back Slide: _iCD 15000, its own _iPubCD 1000, and the client overwrites by key
    public async Task AShorterPublicCooldownDoesNotReplaceTheSkillsOwn()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3100"); await Teach("alice", 15104);
        var a = Session("alice");
        a.GmCombat |= GmCombatSwitches.Unlimited;
        a.GmCombat &= ~GmCombatSwitches.NoCooldown;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510401, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var notice = await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser, n => n.Infos.Any(i => i.SkillId == 15104));
        var own = Assert.Single(notice.Infos, i => i.SkillId == 15104);
        Assert.True(own.CdEndTimestampMs - own.CdStartTimestampMs >= 10000, $"{own.CdEndTimestampMs - own.CdStartTimestampMs}");
    }

    [ClientDataFact] // "to You and one designated ally"
    public async Task HighnessHealReachesTheCasterToo()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 4300"); await Teach("alice", 14301); await Gm("alice", "!lvl 60 40"); await Gm("bob", "!lvl 60 40");
        _app.Services.GetRequiredService<CombatService>().Rolls = new CritRolls(1);
        var a = Session("alice");
        a.GmCombat |= GmCombatSwitches.Unlimited;
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("bob"), 1, null));
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(a, 1, null));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430101, TargetId = (long)bob.Self!.Uuid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
        Assert.Contains(hit.Dsts, d => d.DstID == (long)bob.Self.Uuid);
        Assert.Contains(hit.Dsts, d => d.DstID == a.Role!.Id);
    }

    [ClientDataFact] // "every second ... for 10 s" to you and your party
    public async Task HymnOfDevotionHealsEverySecond()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4300"); await Teach("alice", 14315); await Gm("alice", "!lvl 60 40");
        _app.Services.GetRequiredService<CombatService>().Rolls = new CritRolls(1);
        var a = Session("alice");
        a.GmCombat |= GmCombatSwitches.Unlimited;
        a.NoRegenUntilMs = long.MaxValue;
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(a, 1, null));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1431501, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Task.Delay(3500);
        Assert.True(Character.Stats(a)[AttributeId.Hp] > 1);
    }

    [ClientDataFact] // "Removes up to ${3} ground effects"
    public async Task GanbanteinRemovesAGroundEffect()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2300"); await Teach("alice", 12010); await Teach("alice", 12213);
        _app.Services.GetRequiredService<CombatService>().Rolls = new CritRolls(1);
        var a = Session("alice");
        a.GmCombat |= GmCombatSwitches.Unlimited;
        var at = a.Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1201001, TargetPos = at, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var deadline = DateTime.UtcNow + Wait;
        while (a.SkillObjects.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.NotEmpty(a.SkillObjects);
        await Task.Delay(1200); // the wall's own cast has played out
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1221301, TargetPos = at, Face = a.Face });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser, r => r.Result == 0)).Result);
        deadline = DateTime.UtcNow + Wait;
        while (a.SkillObjects.Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Empty(a.SkillObjects);
    }
}
