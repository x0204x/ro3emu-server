using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task RecoveredHighLevelExtraAndBulletRoutesUseTheirOwnOpcodes()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        long source = session.Role!.Id;
        int mark = alice.Inbox.Snapshot().Count;

        await Loop.InvokeAsync(async () =>
        {
            await combat.SendCastTimelineAsync(session, new UseInSkillInfo
            {
                SrcID = source,
                SkillID = 1200401,
                TargetPos = session.Position.Clone(),
                SessionId = 70001,
                SkillMs = 1000,
                StartTime = 1,
                PlayAct = true,
            }, highLevel: true, extra: true);
            await combat.SendSkillBulletAsync(session, new SkillFixTimeBulletNotify
            {
                SourceId = source,
                EffectId = 1200411,
                FixedTime = 400,
                Split = 5,
                TargetPosition = session.Position.Clone(),
                SkillConfigId = 1200401,
                MasterId = source,
                SourcePosition = session.Position.Clone(),
                StartTimestampMs = 1,
            }, highLevel: true);
        });

        var extra = await Next(alice, CombatService.ExtraUseInSkillHigh, UseInSkillInfo.Parser,
            x => x.SessionId == 70001);
        var bullet = await Next(alice, CombatService.SkillBulletHigh, SkillFixTimeBulletNotify.Parser,
            x => x.SkillConfigId == 1200401);
        Assert.Equal(source, extra.SrcID);
        Assert.Equal(1200401, extra.SkillID);
        Assert.True(extra.PlayAct);
        Assert.Equal(source, bullet.SourceId);
        Assert.Equal(1200411, bullet.EffectId);
        Assert.Equal(400, bullet.FixedTime);
        Assert.Equal(5, bullet.Split);

        var emitted = alice.Inbox.Snapshot().Skip(mark).ToList();
        int extraIndex = emitted.FindIndex(e => e.IsGame(CombatService.ExtraUseInSkillHigh)
            && UseInSkillInfo.Parser.ParseFrom(e.Body).SessionId == 70001);
        int bulletIndex = emitted.FindIndex(e => e.IsGame(CombatService.SkillBulletHigh)
            && SkillFixTimeBulletNotify.Parser.ParseFrom(e.Body).SkillConfigId == 1200401);
        Assert.True(extraIndex >= 0 && extraIndex < bulletIndex,
            $"high-level packet order was {extraIndex}, {bulletIndex}");
        Assert.DoesNotContain(emitted, e => e.IsGame(CombatService.UseInSkill));
        Assert.DoesNotContain(emitted, e => e.IsGame(CombatService.UseInSkillHigh));
        Assert.DoesNotContain(emitted, e => e.IsGame(CombatService.ExtraUseInSkill));
        Assert.DoesNotContain(emitted, e => e.IsGame(CombatService.SkillBullet));
    }

    [ClientDataFact]
    public async Task RecoveredStandaloneEffectRoutesPreserveEveryClientField()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        long source = session.Role!.Id;
        int mark = alice.Inbox.Snapshot().Count;

        SkillEffectDisplayNotify Effect(int id) => new()
        {
            SourceId = source,
            TargetId = 123,
            EffectId = id,
            TargetPos = new Vec3 { X = 1, Y = 2, Z = 3 },
            EffectType = 2,
            Angle = 45,
            MasterId = source,
            Is3DPosition = true,
            SkillId = 1200401,
        };

        await Loop.InvokeAsync(async () =>
        {
            await combat.SendSkillEffectAsync(session, Effect(1200404));
            await combat.SendSkillEffectAsync(session, Effect(1200411), highLevel: true);
        });

        var normal = await Next(alice, CombatService.SkillEffect, SkillEffectDisplayNotify.Parser,
            x => x.EffectId == 1200404);
        var high = await Next(alice, CombatService.SkillEffectHigh, SkillEffectDisplayNotify.Parser,
            x => x.EffectId == 1200411);
        foreach (var effect in new[] { normal, high })
        {
            Assert.Equal(source, effect.SourceId);
            Assert.Equal(123, effect.TargetId);
            Assert.Equal(new Vec3 { X = 1, Y = 2, Z = 3 }, effect.TargetPos);
            Assert.Equal(2, effect.EffectType);
            Assert.Equal(45, effect.Angle);
            Assert.Equal(source, effect.MasterId);
            Assert.True(effect.Is3DPosition);
            Assert.Equal(1200401, effect.SkillId);
        }

        var emitted = alice.Inbox.Snapshot().Skip(mark).ToList();
        Assert.Single(emitted, e => e.IsGame(CombatService.SkillEffect));
        Assert.Single(emitted, e => e.IsGame(CombatService.SkillEffectHigh));
    }
}
