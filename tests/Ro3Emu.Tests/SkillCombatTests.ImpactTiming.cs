using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _impactOutput = output;

    /// <summary>真實技能資料與 TCP 收件計時；不等同原生客戶端畫面驗收，也不改動傷害／時間公式。</summary>
    [ClientDataTheory]
    [InlineData(12004, false)]
    [InlineData(12004, true)]
    [InlineData(12006, false)]
    [InlineData(12006, true)]
    public async Task AuthoredBoltImpactTimingIsMeasuredAcrossTheRewardBarrier(int group, bool lethal)
    {
        await using var alice = await InWorld("ImpactClock");
        await Gm("impactclock", "!job 2100");
        await Teach("impactclock", group, 10);
        int skillId = group * 100 + 10;
        var target = await Target("impactclock", stationary: true, immobile: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        long barrierStart = 0, barrierEnd = 0;
        await Loop.InvokeAsync(() =>
        {
            var session = Session("impactclock");
            var start = session.Position;
            var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav;
            var offset = new[] { (4f, 0f), (-4f, 0f), (0f, 4f), (0f, -4f) }
                .First(o => nav == null || nav.CanWalk(start.X, start.Y, start.Z, start.X + o.Item1, start.Z + o.Item2));
            target.X = start.X + offset.Item1;
            target.Z = start.Z + offset.Item2;
            target.Y = nav?.WalkHeight(target.X, target.Z, start.Y) ?? start.Y;
            target.Hp = lethal ? 1 : 1_000_000;
            combat.BeforeKilled.Insert(0, (_, monster) =>
            {
                if (monster == target) barrierStart = Stopwatch.GetTimestamp();
                return Task.CompletedTask;
            });
            combat.Dealt.Add((_, monster, _) =>
            {
                if (monster == target && barrierStart != 0) barrierEnd = Stopwatch.GetTimestamp();
            });
            return Task.CompletedTask;
        });

        long clock = Stopwatch.GetTimestamp();
        var receipts = new ConcurrentDictionary<uint, double>();
        alice.Inbox.OnExpectedReply = (e, _) => receipts[e.MsgId] = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        using var timelineWait = alice.Inbox.Expect(e =>
            (e.IsGame(CombatService.UseInSkill) || e.IsGame(CombatService.UseInSkillHigh))
            && UseInSkillInfo.Parser.ParseFrom(e.Body).SkillID == skillId);
        using var bulletWait = alice.Inbox.Expect(e =>
            (e.IsGame(CombatService.SkillBullet) || e.IsGame(CombatService.SkillBulletHigh))
            && SkillFixTimeBulletNotify.Parser.ParseFrom(e.Body).SkillConfigId == skillId);
        using var hitWait = alice.Inbox.Expect(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).Dsts.Any(d => d.DstID == target.Uid && d.Damage > 0));
        using var hpWait = alice.Inbox.Expect(e => e.IsGame(CombatService.AoiAttrChange)
            && AOIAttrChange.Parser.ParseFrom(e.Body) is var hp && hp.EntityUuid == (ulong)target.Uid
            && hp.Property.Any(p => p.Idx == 2000));
        clock = Stopwatch.GetTimestamp();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skillId, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var timelineEvent = await timelineWait.WaitAsync(Wait);
        var bulletEvent = await bulletWait.WaitAsync(Wait);
        var hitEvent = await hitWait.WaitAsync(Wait);
        var hpEvent = await hpWait.WaitAsync(Wait);
        var timeline = UseInSkillInfo.Parser.ParseFrom(timelineEvent.Body);
        var bullet = SkillFixTimeBulletNotify.Parser.ParseFrom(bulletEvent.Body);
        var hit = SkillHitInfo.Parser.ParseFrom(hitEvent.Body);
        var hp = AOIAttrChange.Parser.ParseFrom(hpEvent.Body);
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, skillId)!.Value;
        var template = data.SkillTemplate(row.ScriptShow)!.Value;
        var effect = data.SkillTemplateTable("Lua_Config_EffectData")[bullet.EffectId.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        double hatchMs = effect.GetProperty("m_kDamage").GetProperty("m_kBulletData").GetProperty("m_fBulletHatchInterval").GetDouble();
        double attackPoint = template.GetProperty("m_kAtkPoints")[0].GetDouble();
        int onset = CombatService.ImpactOffset(template, attackPoint, timeline.ChantMs, timeline.SkillMs);
        Assert.Equal(timeline.StartTime + onset, bullet.StartTimestampMs);
        Assert.Equal(400, bullet.FixedTime);
        Assert.Equal(10, bullet.Split);
        Assert.Equal(100, hatchMs);
        Assert.Equal(lethal, Assert.Single(hit.Dsts, d => d.DstID == target.Uid).IsDead);
        Assert.Equal(target.Hp, Assert.Single(hp.Property, p => p.Idx == 2000).Data);
        Assert.True(receipts[hitEvent.MsgId] >= receipts[bulletEvent.MsgId]);
        Assert.True(receipts[hpEvent.MsgId] >= receipts[hitEvent.MsgId]);
        Assert.Equal(lethal, barrierStart != 0 && barrierEnd >= barrierStart);
        _impactOutput.WriteLine("IMPACT_TIMING " + JsonSerializer.Serialize(new
        {
            skillId, lethal, timelineOpcode = timelineEvent.MsgId, bulletOpcode = bulletEvent.MsgId,
            timeline.ChantMs, timeline.SkillMs, onsetMs = onset,
            firstBulletMs = bullet.FixedTime, hatchMs, lastBulletMs = bullet.FixedTime + (bullet.Split - 1) * hatchMs,
            timelineReceiveMs = receipts[timelineEvent.MsgId], bulletReceiveMs = receipts[bulletEvent.MsgId],
            hitReceiveMs = receipts[hitEvent.MsgId], hpReceiveMs = receipts[hpEvent.MsgId],
            bulletToHitMs = receipts[hitEvent.MsgId] - receipts[bulletEvent.MsgId],
            hitToHpMs = receipts[hpEvent.MsgId] - receipts[hitEvent.MsgId],
            beforeKilledToDealtMs = barrierStart == 0 ? (double?)null : Stopwatch.GetElapsedTime(barrierStart, barrierEnd).TotalMilliseconds,
        }));
    }
}
