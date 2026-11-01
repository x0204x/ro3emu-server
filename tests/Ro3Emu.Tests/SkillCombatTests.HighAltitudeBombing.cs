using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task<(BotClient Bot, GameSession Source)> BombingFixtureAsync(int level)
    {
        var bot = await InWorld("MechaBombing");
        IsolateDirectHealing();
        await Gm("mechabombing", "!lvl 99"); await Gm("mechabombing", "!job 6400");
        await Teach("mechabombing", 16309); await Teach("mechabombing", 16314); await Teach("mechabombing", 16318, level);
        await LeaseCompanion(bot, 1500300);
        var source = Session("mechabombing");
        Assert.Equal(0, MountService.Rider(source));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1631800 + level, Face = 0 });
        Assert.Equal(ErrorCodes.SkillRequirements, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.False(source.SkillCds.ContainsKey(16318));
        await bot.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)3 });
        Assert.Equal(0, (await Next(bot, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        Assert.Equal(1500300, MountService.Rider(source));
        await HealingStat(bot, 103, 1000); await HealingStat(bot, 102, 10000);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(source, null, 10000));
        _app.Services.GetRequiredService<CombatService>().Rolls = new CardOffenceRolls();
        return (bot, source);
    }

    [ClientDataTheory]
    [InlineData(1, 0, 2600)]
    [InlineData(3, 90, 3000)]
    public async Task HighAltitudeBombingRealMechaDealsSevenPulsesInForwardFifteenByTenRectangle(int level, float face, long eachHit)
    {
        var fixture = await BombingFixtureAsync(level); await using var bot = fixture.Bot;
        var source = fixture.Source;
        var combat = _app.Services.GetRequiredService<CombatService>();
        var targets = new List<MonsterUnit>();
        for (int i = 0; i < 10; i++)
        { var target = await Target("mechabombing", stationary: true, immobile: true); await CardOffenceDummyAsync(target); targets.Add(target); }
        // 五名內側、同範圍第六名、後方、側外、前方超距；最後一名探查15m邊界內。
        (float Ahead, float Side)[] points = [(8, 0), (8, 4.9f), (8, -4.9f), (10, 0), (14.9f, 0), (14.95f, 0), (-1, 0), (8, 5.1f), (15.1f, 0), (14.99f, 4.9f)];
        int completed = 0, resolved = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(source.WorldScene);
            foreach (var ambient in map.Units.OfType<MonsterUnit>().Where(m => !targets.Contains(m)).ToArray())
            { ambient.Hp = 0; ambient.Dead = true; await world.RemoveUnitAsync(ambient); }
            double angle = face * Math.PI / 180;
            for (int i = 0; i < targets.Count; i++)
            {
                targets[i].X = source.Position.X + (float)(points[i].Ahead * Math.Sin(angle) + points[i].Side * Math.Cos(angle));
                targets[i].Z = source.Position.Z + (float)(points[i].Ahead * Math.Cos(angle) - points[i].Side * Math.Sin(angle));
                targets[i].Y = source.Position.Y; map.Moved(targets[i]);
                var row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", 1631800 + level)
                    ?? throw new InvalidOperationException("HighAltitudeBombing row required.");
                Assert.Equal(i < 6 || i == 9,
                    CombatService.HighAltitudeBombingAreaContains(row, source.Position, face, targets[i].Pos));
            }
            combat.SkillCastCompleted.Add((s, _, _) => { if (s == source) { completed++; done.TrySetResult(); } return Task.CompletedTask; });
            combat.CastResolved.Add((s, _, _, _) => { if (s == source) resolved++; return Task.CompletedTask; });
        });
        int oldCds = bot.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillCdUpdate));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1631800 + level, TargetId = targets[0].Uid, TargetPos = targets[0].Pos.Clone(), Face = face });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var timeline = await Next(bot, CombatService.UseInSkill, UseInSkillInfo.Parser, n => n.SessionId == accepted.SessionId);
        for (int pulse = 0; pulse < 7; pulse++)
        {
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            Assert.Equal(5, hit.Dsts.Count); Assert.Equal(3, hit.Split);
            Assert.Equal(targets.Take(5).Select(m => m.Uid).Order(), hit.Dsts.Select(d => d.DstID).Order());
            Assert.All(hit.Dsts, d => Assert.Equal(eachHit, d.Damage));
        }
        await done.Task.WaitAsync(Wait);
        await Task.Delay((int)Math.Max(1, timeline.StartTime + timeline.SkillMs + 100 - MonsterService.NowMs));
        for (int i = 0; i < targets.Count; i++) Assert.Equal(i < 5 ? eachHit * 7 : 0, 1_000_000 - targets[i].Hp);
        Assert.Equal(1, completed); Assert.Equal(1, resolved); Assert.Equal(9999, source.CurrentSp);
        var cdEnvelope = Assert.Single(bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillCdUpdate)).Skip(oldCds),
            e => SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(c => c.SkillId == 16318));
        var cd = Assert.Single(SkillCdNotify.Parser.ParseFrom(cdEnvelope.Body).Infos, c => c.SkillId == 16318);
        Assert.Equal(20000, cd.CdEndTimestampMs - cd.CdStartTimestampMs);
        Assert.Equal(1500300, MountService.Rider(source));
    }

    [ClientDataFact]
    public async Task HighAltitudeBombingInterruptedAfterFirstPulseDoesNotContinueOrRepay()
    {
        var fixture = await BombingFixtureAsync(1); await using var bot = fixture.Bot;
        var source = fixture.Source; var combat = _app.Services.GetRequiredService<CombatService>();
        var target = await Target("mechabombing", stationary: true, immobile: true);
        await CardOffenceDummyAsync(target); await IsolateAuthoredStageTargetAsync(source, target);
        await Loop.InvokeAsync(() =>
        {
            target.X = source.Position.X; target.Z = source.Position.Z + 8; target.Y = source.Position.Y;
            _app.Services.GetRequiredService<World>().Map(source.WorldScene).Moved(target);
            return Task.CompletedTask;
        });
        int published = 0, completed = 0; long dealt = 0;
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.SkillCastCompleted.Add((s, _, _) => { if (s == source) completed++; return Task.CompletedTask; });
        combat.MonsterSkillHitPublished.Add(async (s, m, _, amount, _) =>
        {
            if (s != source || m != target) return;
            published++; dealt += amount;
            await combat.InterruptAsync(source); stopped.TrySetResult();
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1631801, TargetId = target.Uid, TargetPos = target.Pos.Clone(), Face = 0 });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var timeline = await Next(bot, CombatService.UseInSkill, UseInSkillInfo.Parser, n => n.SessionId == accepted.SessionId);
        await stopped.Task.WaitAsync(Wait);
        long cooldown = source.SkillModeCds[(16318, 0)];
        await Task.Delay((int)Math.Max(1, timeline.StartTime + timeline.SkillMs + 100 - MonsterService.NowMs));
        Assert.Equal(1, published); Assert.Equal(2600, dealt); Assert.Equal(0, completed);
        Assert.Equal(1_000_000 - dealt, target.Hp); Assert.Equal(9999, source.CurrentSp);
        Assert.Equal(cooldown, source.SkillModeCds[(16318, 0)]);
    }
}
