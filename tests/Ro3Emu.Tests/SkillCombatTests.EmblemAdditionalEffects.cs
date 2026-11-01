using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1400, 11301, 1, 1001, 15000, 20000)]
    [InlineData(1400, 11301, 5, 1001, 25000, 30000)]
    [InlineData(5400, 15320, 1, 1003, 10500, 12000)]
    [InlineData(5400, 15320, 10, 1003, 15000, 16500)]
    [InlineData(5400, 15322, 1, 1003, 7500, 9000)]
    [InlineData(5400, 15322, 10, 1003, 12000, 13500)]
    [InlineData(5400, 15323, 1, 1003, 5500, 7000)]
    [InlineData(5400, 15323, 10, 1003, 10000, 11500)]
    public async Task EmblemAdditionalModesRequireTheExactStageAndRestoreDamageAndControlOnRemoval(
        int job, int group, int level, int mark, int baseline, int enhanced)
    {
        await using var bot = await InWorld("EchoVariant");
        await Gm("echovariant", "!lvl 99"); await Gm("echovariant", $"!job {job}");
        await Teach("echovariant", group, level); await HealingStat(bot, 103, 1000);
        var source = Session("echovariant");
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var target = await Target("echovariant", stationary: true, immobile: true);
        await CardOffenceDummyAsync(target);
        int id = group * 100 + level;
        async Task Check(bool active)
        {
            var row = new SkillConfig(combat.EffectiveSkillRow(source, id)
                ?? throw new InvalidOperationException("Effective skill row required."));
            Assert.Equal(active ? 1 : 0, row.Mode);
            Assert.Equal(active ? enhanced : baseline, row.DamageParam1[0]);
            Assert.Equal(group == 11301 || !active ? 600 : 700, row.Range[0]);
            await Loop.InvokeAsync(() => buffs.ClearUnitAsync(target));
            var stats = Character.Stats(source);
            Assert.Equal(1000, stats[103]);
            long expected = (long)Math.Round(stats[103] * (active ? enhanced : baseline) / 10000d
                * (1 + Math.Min(stats[115], 500) * (group == 11301 ? 20 : 50) / 10000d));
            Assert.Equal(expected, await CardOffenceHitAsync(bot, source, target, row.Row));
            if (group == 11301)
            {
                await Loop.InvokeAsync(() =>
                {
                    Assert.Equal(active, buffs.UnitHasGroup(target, 9016));
                    Assert.Equal(active, buffs.UnitActionBlocked(target, ForbiddenState.CastSkill));
                    return Task.CompletedTask;
                });
                if (active)
                {
                    var packet = await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
                        b => b.UnitId == target.Uid && b.Buffs.Any(x => x.ConfigId == 901601));
                    Assert.Equal(1500, Assert.Single(packet.Buffs, b => b.ConfigId == 901601).Duration);
                }
            }
        }
        await EmblemMarksAsync(source, mark, 5); await Check(false);
        await EmblemMarksAsync(source, mark, 6); await Check(true);
        Assert.DoesNotContain(source.Progress!.Skills.Keys, g => g is 111301 or 115331);
        await EmblemMarksAsync(source, mark, 0); await Check(false);
        await EmblemMarksAsync(source, mark, 6);
        await Gm("echovariant", "!job 1000");
        Assert.Equal(0, new SkillConfig(combat.EffectiveSkillRow(source, id)!.Value).Mode);
    }

    [ClientDataFact]
    public async Task EmblemShadowExtendedRadiusHitsThroughTheRealCastRequest()
    {
        await using var bot = await InWorld("EchoRadius");
        await Gm("echoradius", "!lvl 99"); await Gm("echoradius", "!job 5400");
        await Teach("echoradius", 15320); await HealingStat(bot, 103, 1000);
        var source = Session("echoradius"); var combat = _app.Services.GetRequiredService<CombatService>();
        await Gm("echoradius", "!item 2010120 1");
        long dagger = source.Items.First(i => i.Entry == 2010120).Guid;
        await bot.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        combat.Rolls = new CardOffenceRolls();
        var target = await Target("echoradius", stationary: true, immobile: true);
        await CardOffenceDummyAsync(target);
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(source.WorldScene);
            foreach (var ambient in map.Units.OfType<MonsterUnit>().Where(m => m != target).ToArray())
            { ambient.Hp = 0; ambient.Dead = true; await world.RemoveUnitAsync(ambient); }
            target.X = source.Position.X + 6.5f; target.Y = source.Position.Y; target.Z = source.Position.Z;
            map.Moved(target);
        });
        await EmblemMarksAsync(source, 1003, 6);
        Assert.Equal(700, new SkillConfig(combat.EffectiveSkillRow(source, 1532001)!.Value).Range[0]);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1532001, TargetPos = source.Position.Clone() });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 1532011 && h.Dsts.Any(d => d.DstID == target.Uid));
        Assert.True(Assert.Single(hit.Dsts, d => d.DstID == target.Uid).Damage > 0);
        Assert.True(target.Hp < 1_000_000);
    }
}
