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
    [InlineData(1, 2, 110)]
    [InlineData(2, 3, 120)]
    [InlineData(3, 3, 130)]
    [InlineData(4, 4, 140)]
    [InlineData(5, 4, 150)]
    [InlineData(6, 5, 160)]
    [InlineData(7, 5, 170)]
    [InlineData(8, 6, 180)]
    [InlineData(9, 6, 190)]
    [InlineData(10, 7, 200)]
    public async Task MeteorStormUsesItsAuthoredWaveCountRatherThanFillingTheWholeVisualLifetime(int level, int waves, long perWave)
    {
        await using var alice = await InWorld("MeteorWaves");
        await Gm("meteorwaves", "!job 2200");
        await Teach("meteorwaves", 12106, level);
        await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq
        { GmType = (GmOpEnum)33, ParamString = $"{alice.Self!.Uuid},104,1000" });
        Assert.Equal(0, (await Next(alice, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser)).Result);
        _app.Services.GetRequiredService<CombatService>().Rolls = new StormBoundaryRolls(200);
        var target = await Target("meteorwaves", stationary: true);
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>();
            foreach (var ambient in world.Map(Session("meteorwaves").WorldScene).Units.OfType<MonsterUnit>()
                .Where(m => m != target).ToArray())
            {
                ambient.Hp = 0; ambient.Dead = true;
                await world.RemoveUnitAsync(ambient);
            }
            target.Hp = 1_000_000;
            target.Template = target.Template with { Mdef = 0, Element = 1 };
            target.StaysOutOfBattle = true;
            target.KeepsDamage = true;
        });
        int skillId = 1210600 + level;
        var config = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), skillId)!.Value;
        Assert.Equal(waves.ToString(), config.DamageScript);
        Assert.Equal(waves, config.LogicData[5]);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skillId, TargetPos = target.Pos });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, x => x.SkillID == skillId);
        for (int i = 0; i < waves; i++)
        {
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SkillId == skillId);
            Assert.Equal(perWave, Assert.Single(hit.Dsts, d => d.DstID == target.Uid).Damage);
        }
        // 等過整個地面物件生命期，排除達到應有波數後仍繼續造成傷害。
        await Task.Delay((int)config.LogicData[2] + 300);
        Assert.Equal(waves, alice.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == skillId));
        Assert.Equal(1_000_000 - perWave * waves, target.Hp);
        await Loop.InvokeAsync(() =>
        {
            Assert.DoesNotContain(Session("meteorwaves").SkillObjects, x => x.Cast.Request.SkillId == skillId);
            return Task.CompletedTask;
        });
    }
}
