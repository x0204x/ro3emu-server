using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(75)]
    [InlineData(25)]
    public async Task EnergyReleaseChildAppliesNonlethalAftercostOnlyAfterTheRealHit(int healthPercent)
    {
        await using var bot = await InWorld("ExplodeCost");
        await Gm("explodecost", "!job 6400"); await Teach("explodecost", 16352);
        IsolateDirectHealing();
        await HealingStat(bot, 101, 100_000); await HealingStat(bot, 102, 10000); await HealingStat(bot, 103, 1000);
        var source = Session("explodecost");
        var target = await Target("explodecost", stationary: true); await CardOffenceDummyAsync(target);
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>();
            foreach (var other in world.Map(source.WorldScene).Units.OfType<MonsterUnit>().Where(m => m != target).ToArray())
                await world.RemoveUnitAsync(other);
        });
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        long maxHp = Character.Stats(source)[101];
        long initial = maxHp * healthPercent / 100;
        long expected = Math.Max(1, initial - maxHp / 2);
        _impactOutput.WriteLine($"ENERGY_RELEASE_INPUT maxHp={maxHp} initial={initial} expected={expected}");
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        combat.SkillCastCompleted.Add((s, _, _) =>
        { if (s == source) completed.TrySetResult(); return Task.CompletedTask; });
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(source, initial, 10000));
        await bot.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1635201, TargetId = target.Uid, TargetPos = source.Position.Clone() });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 1635201 && h.Dsts.Any(d => d.DstID == target.Uid));
        long damage = Assert.Single(hit.Dsts, d => d.DstID == target.Uid).Damage;
        Assert.True(damage > 0);
        await completed.Task.WaitAsync(Wait);
        Assert.Equal(expected, source.CurrentHp); // 先驗真實狀態，錯誤數值直接顯示，不能只等封包逾時。
        await Next(bot, CharacterService.AoiAttrChange, AOIAttrChange.Parser,
            a => a.EntityUuid == (ulong)source.Role!.Id && a.Property.Any(p => p.Idx == 2000 && p.Data == expected));
        Assert.Equal(expected, source.CurrentHp); Assert.False(source.Dead);
        Assert.Equal(1_000_000 - damage, target.Hp);
        Assert.Contains(source.Buffs, b => b.ConfigId == 1634801 && b.DurationMs == 5000);
    }
}
