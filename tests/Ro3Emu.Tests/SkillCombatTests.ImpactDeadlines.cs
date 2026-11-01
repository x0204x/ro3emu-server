using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoltDeadlineDoesNotRepeatElapsedTimelineWorkAndTraceSeparatesSettlement(bool slowSettlement)
    {
        await using var bot = await InWorld("ImpactDeadline");
        await Gm("impactdeadline", "!job 2100");
        await Teach("impactdeadline", 12004, 10);
        var target = await Target("impactdeadline", stationary: true, immobile: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var samples = new List<CombatService.ImpactTimingSample>();
        await Loop.InvokeAsync(() =>
        {
            target.Hp = slowSettlement ? 1 : 1_000_000;
            combat.ImpactTimingObserver = sample =>
            {
                if (sample.SkillId != 1200410 || samples.Count >= 64) return;
                samples.Add(sample);
                // 真正阻塞 timeline 後處理，再走 GameLoop timer 與 TCP；不是僅測時間算式。
                if (!slowSettlement && sample.Stage == "timeline-sent") Thread.Sleep(500);
            };
            if (slowSettlement)
                combat.BeforeKilled.Insert(0, (_, monster) => monster == target ? Task.Delay(1200) : Task.CompletedTask);
            return Task.CompletedTask;
        });
        var received = new ConcurrentDictionary<uint, long>();
        bot.Inbox.OnExpectedReply = (e, _) => received[e.MsgId] = MonsterService.NowMs;
        using var timelineWait = bot.Inbox.Expect(e =>
            (e.IsGame(CombatService.UseInSkill) || e.IsGame(CombatService.UseInSkillHigh))
            && UseInSkillInfo.Parser.ParseFrom(e.Body).SkillID == 1200410);
        using var bulletWait = bot.Inbox.Expect(e =>
            (e.IsGame(CombatService.SkillBullet) || e.IsGame(CombatService.SkillBulletHigh))
            && SkillFixTimeBulletNotify.Parser.ParseFrom(e.Body).SkillConfigId == 1200410);
        using var hitWait = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).Dsts.Any(d => d.DstID == target.Uid));
        using var hpWait = bot.Inbox.Expect(e => e.IsGame(CombatService.AoiAttrChange)
            && AOIAttrChange.Parser.ParseFrom(e.Body) is var hp && hp.EntityUuid == (ulong)target.Uid
            && hp.Property.Any(p => p.Idx == 2000));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200410, TargetId = target.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var timeline = UseInSkillInfo.Parser.ParseFrom((await timelineWait.WaitAsync(Wait)).Body);
        var bulletEvent = await bulletWait.WaitAsync(Wait);
        var bullet = SkillFixTimeBulletNotify.Parser.ParseFrom(bulletEvent.Body);
        var hit = SkillHitInfo.Parser.ParseFrom((await hitWait.WaitAsync(Wait)).Body);
        await hpWait.WaitAsync(Wait);
        await Loop.InvokeAsync(() =>
        {
            combat.ImpactTimingObserver = null;
            return Task.CompletedTask;
        });
        long Stage(string stage) => Assert.Single(samples, s => s.Stage == stage).TimeMs;
        Assert.Equal(slowSettlement, Assert.Single(hit.Dsts, d => d.DstID == target.Uid).IsDead);
        Assert.InRange(samples.Count, 7, 64);
        Assert.True(Stage("hp-sent") >= Stage("hit-sent"));
        if (!slowSettlement)
        {
            // 舊排程會多等剛注入的 500ms；新的發射仍對齊已送出的絕對開始時間。
            Assert.InRange(Stage("bullet-sent") - bullet.StartTimestampMs, 0, 250);
            Assert.InRange(Stage("resolve-entered") - bullet.StartTimestampMs - bullet.FixedTime, 0, 250);
            Assert.True(Stage("bullet-sent") - Stage("timeline-sent") >= 500);
        }
        else
        {
            Assert.True(Stage("settlement-complete") - Stage("settlement-entered") >= 1150);
            Assert.True(Stage("hit-sent") >= Stage("settlement-complete"));
            Assert.True(Stage("damage-callbacks-complete") >= Stage("settlement-complete"));
        }
        _impactOutput.WriteLine("IMPACT_DEADLINE " + JsonSerializer.Serialize(new
        {
            slowSettlement, timeline.StartTime, bullet.StartTimestampMs, bullet.FixedTime,
            projectileLateness = Stage("bullet-sent") - bullet.StartTimestampMs,
            resolveLateness = Stage("resolve-entered") - bullet.StartTimestampMs - bullet.FixedTime,
            settlementMs = Stage("settlement-complete") - Stage("settlement-entered"),
            bulletReceiveLateness = received[bulletEvent.MsgId] - bullet.StartTimestampMs,
            hitReceiveAfterResolve = received[CombatService.SkillHit] - Stage("resolve-entered"), samples,
        }));
    }
}
