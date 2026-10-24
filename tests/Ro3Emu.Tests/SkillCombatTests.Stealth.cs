using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int StealthAttr = 3010, ShowStealthAttr = 3011;

    /// <summary>Alice and Bob in one room where Alice may hit Bob; Bob an Assassin with Disguise, Alice a Mage with Fire Bolt.</summary>
    private async Task<(GameSession A, GameSession B)> HidingDuelAsync(BotClient alice, BotClient bob, string aliceJob = "2100", int aliceSkill = 12004)
    {
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job " + aliceJob); await Teach("alice", aliceSkill);
        await Gm("bob", "!lvl 99"); await Gm("bob", "!job 5200"); await Teach("bob", 15112);
        var a = Session("alice");
        var b = Session("bob");
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(a.SceneEntry, [a, b])));
        foreach (var (bot, session) in new[] { (alice, a), (bob, b) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, session, reason: 3)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        }
        await Loop.InvokeAsync(async () =>
        {
            _app.Services.GetRequiredService<CombatService>().SetEventPvPHandlers((source, target) => source == a && target == b,
                (_, _) => Task.CompletedTask);
            await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(b,
                new MapPoint(a.Position.X + 2, a.Position.Y, a.Position.Z, b.Face));
            await Character.SetHpSpAsync(b, long.MaxValue, long.MaxValue);
        });
        return (a, b);
    }

    private static Task<AOIAttrChange> StealthChange(BotClient bot, GameSession subject, int attr, long value) =>
        Next(bot, CombatService.AoiAttrChange, AOIAttrChange.Parser, c => c.EntityUuid == (ulong)subject.Role!.Id
            && c.Property.Any(p => p.Idx == attr && p.Data == value));

    // Disguise: "you cannot be seen or targeted. You are revealed after taking damage @{1} times or dealing damage." The
    // client hides a unit by its Stealth 3010 attribute; Sight "Reveals hidden targets within a @{1}-meter radius".
    [ClientDataFact]
    public async Task AHiddenEnemyIsRefusedUntilDetectedAndAHitRevealsIt()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var (a, b) = await HidingDuelAsync(alice, bob);
        var combat = _app.Services.GetRequiredService<CombatService>();

        await bob.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1511201, Face = 0 });
        Assert.Equal(0, (await Next(bob, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await StealthChange(bob, b, StealthAttr, 2);
        await StealthChange(alice, b, StealthAttr, 2); // the viewer's client hides him
        Assert.Contains(b.Buffs, x => x.ConfigId == 1500602);
        Assert.Contains(b.Buffs, x => x.ConfigId == 1500702);

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = b.Role!.Id });
        Assert.Equal(3022, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 1201201, 10000, a.Role!.Id));
        await StealthChange(alice, a, ShowStealthAttr, 350);
        Assert.Equal(350, await Loop.InvokeAsync(() => Task.FromResult(combat.DetectionRadiusCm(a))));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = b.Role.Id });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        await StealthChange(bob, b, StealthAttr, 0);
        Assert.DoesNotContain(b.Buffs, x => x.ConfigId is 1500602 or 1500702); // Camouflage and Hiding end together
    }

    // Enhance Cloaking level 1 [15305, 1, 5]: five hits taken reveal; the fourth does not.
    [ClientDataFact]
    public async Task EnhanceCloakingEndsOnItsFifthHitTaken()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var (a, b) = await HidingDuelAsync(alice, bob);
        await Loop.InvokeAsync(async () =>
        {
            var buffs = _app.Services.GetRequiredService<BuffService>();
            await buffs.AddAsync(b, 1530401, 60000, b.Role!.Id);
            await buffs.AddAsync(b, 1530501, 60000, b.Role.Id);
            await buffs.AddAsync(a, 1330301, 60000, a.Role!.Id); // Keen Senses: 12 m
        });
        for (int hit = 1; hit <= 5; hit++)
        {
            await Loop.InvokeAsync(() => { a.SkillCds.Clear(); a.SkillModeCds.Clear(); return Character.SetHpSpAsync(a, null, long.MaxValue); });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = b.Role!.Id });
            var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.Equal(0, accepted.Result);
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            await Task.Delay(300);
            Assert.Equal(hit < 5, b.Buffs.Any(x => x.ConfigId == 1530401));
        }
        Assert.DoesNotContain(b.Buffs, x => x.ConfigId == 1530501); // its linked speed group
    }

    // Ruwach "locates one enemy ... under the [Disguise] Status Effect. Removes their [Disguise] Status Effect and deals
    // Holy Ranged Magic damage ... disappears after Discovering a target": its hit row 1400601 removes category 31.
    [ClientDataFact]
    public async Task RuwachRevealsTheNearestHiddenEnemyAndHitsIt()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var (a, b) = await HidingDuelAsync(alice, bob, "4100", 14005);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(b, 1500602, 60000, b.Role!.Id));
        await StealthChange(alice, b, StealthAttr, 2);

        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 1400501, 10000, a.Role!.Id));
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1400601);
        Assert.Contains(hit.Dsts, d => d.DstID == b.Role!.Id && d.Damage > 0);
        await StealthChange(alice, b, StealthAttr, 0);
        Assert.DoesNotContain(b.Buffs, x => x.ConfigId == 1500602);
        Assert.DoesNotContain(a.Buffs, x => x.ConfigId == 1400501);
    }

    // Disguise: "Passive: Using Back Slide also grants [Camouflage] for ${4} seconds" (10): its level 1 Camouflage 1500602.
    [ClientDataFact]
    public async Task BackSlideGrantsCamouflageWithDisguiseLearned()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 5200"); await Teach("alice", 15104);
        var a = Session("alice");
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510401, Face = 0 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == 1500702));
        Assert.DoesNotContain(a.Buffs, b => b.ConfigId == 1500602);

        await Teach("alice", 15112);
        await Task.Delay(1000); // the slide still moves her
        await Loop.InvokeAsync(() => { a.SkillCds.Clear(); a.SkillModeCds.Clear(); return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510401, Face = 0 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var camouflage = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == 1500602));
        Assert.Equal(10000, camouflage.Buffs.Single(b => b.ConfigId == 1500602).Duration);
        await StealthChange(alice, a, StealthAttr, 2);
    }

    // Category 109 rows carry their radius in _kLogicData[0]: Steady Focus 350, Detecting 500, Keen Senses 1200. Sight
    // Sprout: "You Discover hidden enemy targets within a ${1}-meter radius" (3.5), a Wizard's from the start (_iInitialLevel 1).
    [ClientDataFact]
    public async Task DetectionRadiiComeFromTheirRowsAndSightSprout()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2100");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var a = Session("alice");
        Assert.Equal(0, await Loop.InvokeAsync(() => Task.FromResult(combat.DetectionRadiusCm(a))));
        await Gm("alice", "!job 2200");
        Assert.Equal(350, await Loop.InvokeAsync(() => Task.FromResult(combat.DetectionRadiusCm(a))));
        foreach (var (buff, radius) in new[] { (1310501, 500), (1330301, 1200) })
        {
            await Loop.InvokeAsync(() => buffs.AddAsync(a, buff, 60000, a.Role!.Id));
            await StealthChange(alice, a, ShowStealthAttr, radius);
        }
        Assert.Equal(1200, await Loop.InvokeAsync(() => Task.FromResult(combat.DetectionRadiusCm(a))));
    }
}
