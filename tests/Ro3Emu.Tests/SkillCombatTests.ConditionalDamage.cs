using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, 0, 5000, 0, 9120, 50)]
    [InlineData(1, 0, 5000, 1, 7600, 41)]
    [InlineData(10, 0, 5000, 0, 10200, 60)]
    [InlineData(10, 0, 5000, 1, 8500, 50)]
    [InlineData(1, 1, 6200, 0, 9500, 52)]
    [InlineData(1, 1, 6200, 1, 7600, 41)]
    [InlineData(10, 10, 8000, 0, 10625, 63)]
    [InlineData(10, 10, 8000, 1, 8500, 50)]
    public async Task SonicBlowPvpUsesTheSameLowHealthAndLearnedPassiveThresholds(int level, int passive,
        int threshold, int excessHp, int ratio, int flat)
    {
        await using var alice = await InWorld("SonicSource");
        await using var bob = await InWorld("SonicTarget");
        await Gm("sonicsource", "!job 5200");
        await Gm("sonictarget", "!job 5200"); // 同職業排除圖騰相剋，單獨驗證 HP 門檻。
        if (passive > 0) await Teach("sonicsource", 15103, passive);
        var source = Session("sonicsource"); var target = Session("sonictarget");
        await CardOffenceRoom((alice, source), (bob, target));
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        Assert.Equal(10000, combat.ApplyPlayerRestraints(source, target, 10000));
        var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1510200 + level)!.Value;
        long before = Character.Stats(target)[101] * threshold / 10000 + excessHp;
        await Loop.InvokeAsync(async () =>
        {
            source.Position = target.Position.Clone();
            combat.SetEventPvPHandlers((s, t) => s == source && t == target, (_, _) => Task.CompletedTask);
            await Character.SetHpSpAsync(target, before, null);
        });
        long expected = (long)Math.Round(Character.Stats(source)[103] * ratio / 10000d + flat);
        await Loop.InvokeAsync(() => combat.CastExtraOnPlayerAsync(source, row, target));
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == row.Id
            && h.Dsts.Any(d => d.DstID == target.Role!.Id));
        Assert.Equal(expected, Assert.Single(hit.Dsts, d => d.DstID == target.Role!.Id).Damage);
        Assert.Equal(before - expected, target.CurrentHp);
    }

    [ClientDataTheory]
    [InlineData(1, 0, 0, 5100)]
    [InlineData(10, 0, 0, 6000)]
    [InlineData(1, 2040130, 1, 5100)]
    [InlineData(10, 2040130, 1, 6000)]
    [InlineData(1, 2050120, 1, 5355)]
    [InlineData(10, 2050120, 1, 6300)]
    [InlineData(1, 2050120, 0, 5100)]
    [InlineData(10, 2050120, 0, 6000)]
    public async Task HolyCrossWeaponBranchRequiresAnEquippedTwoHandedSpear(int level, int entry, int grid, int ratio)
    {
        await using var bot = await InWorld("HolyCrossSpear");
        var source = Session("holycrossspear");
        var data = _app.Services.GetRequiredService<GameData>();
        await Loop.InvokeAsync(() =>
        {
            source.Items.RemoveAll(i => i.Grid is 1 or 2);
            if (entry != 0)
            {
                var weapon = ItemConfig.Get(data, entry)!.Value;
                Assert.Equal(entry == 2050120 ? 5 : 4, weapon.SubType);
                Assert.Equal(entry == 2050120 ? 1 : 2, weapon.EquipPart);
                source.Items.Add(new InvItem { Guid = 9872901, Entry = entry, Pack = grid == 0 ? 1 : 2, Grid = grid, Stack = 1 });
            }
            return Task.CompletedTask;
        });
        await HealingStat(bot, 103, 1000);
        var target = await Target("holycrossspear", stationary: true); await CardOffenceDummyAsync(target);
        await Loop.InvokeAsync(() => { target.Template = target.Template with { Element = 1 }; return Task.CompletedTask; });
        var row = SkillConfig.Get(data, 1141100 + level)!.Value;
        _app.Services.GetRequiredService<CombatService>().Rolls = new CardOffenceRolls();
        // EleConfig 的聖對無屬性為原倍率；不可套用念對無屬性的 -25%。
        long expected = (long)Math.Round(Character.Stats(source)[103] * ratio / 10000d + row.DamageParam2[0]);
        Assert.Equal(expected, await CardOffenceHitAsync(bot, source, target, row.Row));
        Assert.Equal(1_000_000 - expected, target.Hp);
    }

    [ClientDataTheory]
    [InlineData(1160801, 1, 1, 2400)]
    [InlineData(1160805, 1, 1, 4000)]
    [InlineData(1161101, 1, 1, 3000)]
    [InlineData(1161105, 1, 1, 7000)]
    [InlineData(1163841, 1, 1, 2400)]
    [InlineData(1163845, 1, 1, 4000)]
    [InlineData(1163871, 1, 1, 4800)]
    [InlineData(1163875, 1, 1, 8000)]
    [InlineData(11163861, 1, 1, 2400)]
    [InlineData(11163865, 1, 1, 4000)]
    [InlineData(11163891, 1, 1, 4800)]
    [InlineData(11163895, 1, 1, 8000)]
    [InlineData(1200801, 1, 1, 622)]
    [InlineData(1200810, 1, 1, 1365)]
    [InlineData(1200801, 1, 10, 1556)]
    [InlineData(1200810, 1, 10, 3412)]
    [InlineData(1410901, 1, 1, 74)]
    [InlineData(1410910, 1, 1, 160)]
    [InlineData(1410901, 3, 1, 89)]
    [InlineData(1410910, 3, 1, 190)]
    [InlineData(1410901, 5, 1, 89)]
    [InlineData(1410910, 5, 1, 190)]
    [InlineData(1410901, 1, 8, 111)]
    [InlineData(1410910, 1, 8, 238)]
    [InlineData(1410901, 1, 10, 134)]
    [InlineData(1410910, 1, 10, 285)]
    public async Task OrdinaryDamageChoosesTheAuthoredMonsterRaceAndElementBranch(int id, int race, int element, long expected)
    {
        await using var bot = await InWorld("DamageBranch");
        var source = Session("damagebranch");
        Character.DynamicProperties.Add(_ => [(3, 0L)]); // 隔離大地毀滅的體質共鳴，只驗目標分支。
        await HealingStat(bot, 103, 1000); await HealingStat(bot, 104, 1000);
        Assert.Equal(1000, Character.Stats(source)[103]); Assert.Equal(1000, Character.Stats(source)[104]);
        var target = await Target("damagebranch", stationary: true); await CardOffenceDummyAsync(target);
        await Loop.InvokeAsync(() =>
        {
            target.Template = target.Template with { Race = race, Element = element };
            return Task.CompletedTask;
        });
        var row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", id)!.Value;
        _app.Services.GetRequiredService<CombatService>().Rolls = new CardOffenceRolls();
        Assert.Equal(expected, await CardOffenceHitAsync(bot, source, target, row));
        Assert.Equal(1_000_000 - expected, target.Hp);
    }

    [ClientDataTheory]
    [InlineData(1, 74)]
    [InlineData(10, 160)]
    public async Task MagnusExorcismusPersistsForEightAuthoredPulsesAndDespawns(int level, long damage)
    {
        await using var bot = await InWorld("MagnusPulses");
        await Gm("magnuspulses", "!job 4200"); await Teach("magnuspulses", 14109, level);
        await HealingStat(bot, 104, 1000); await HealingStat(bot, 102, 10000);
        var source = Session("magnuspulses"); var target = await Target("magnuspulses", stationary: true);
        await CardOffenceDummyAsync(target);
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        await Loop.InvokeAsync(async () =>
        {
            target.Template = target.Template with { Race = 1, Element = 1 };
            var world = _app.Services.GetRequiredService<World>();
            foreach (var ambient in world.Map(source.WorldScene).Units.OfType<MonsterUnit>().Where(m => m != target).ToArray())
                await world.RemoveUnitAsync(ambient);
        });
        int id = 1410900 + level;
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = id, TargetPos = target.Pos });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        for (int i = 0; i < 8; i++)
        {
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == id);
            Assert.Equal(damage, Assert.Single(hit.Dsts, d => d.DstID == target.Uid).Damage);
        }
        await Task.Delay(800);
        Assert.Equal(1_000_000 - damage * 8, target.Hp);
        Assert.DoesNotContain(source.SkillObjects, u => u.Cast.Request.SkillId == id);
    }
}
