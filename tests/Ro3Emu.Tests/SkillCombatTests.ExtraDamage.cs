using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1330201, 2, 100, 3820)]
    [InlineData(1330205, 2, 150, 4300)]
    [InlineData(1431401, 5, 100, 2300)]
    [InlineData(1431410, 5, 150, 4100)]
    [InlineData(1160101, 115, 100, 442)]
    [InlineData(1160105, 115, 200, 800)]
    public async Task ExtraFormulaChangesRealDamageAndHealthAtLowAndMaxSkillLevels(int skill, int attribute, int value, long expected)
    {
        await using var alice = await InWorld("ExtraDamage");
        foreach (var (id, amount) in new[] { (103, 1000), (104, 1000), (attribute, value) })
        {
            await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq
            { GmType = (GmOpEnum)33, ParamString = $"{alice.Self!.Uuid},{id},{amount}" });
            Assert.Equal(0, (await Next(alice, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser)).Result);
        }
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new StormBoundaryRolls(200);
        var target = await Target("extradamage", stationary: true);
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 1_000_000;
            target.StaysOutOfBattle = true;
            target.KeepsDamage = true;
            target.Template = target.Template with { Def = 0, Mdef = 0, Element = 1 };
            var player = Session("extradamage");
            Assert.Equal(1000, Character.Stats(player)[103]);
            Assert.Equal(1000, Character.Stats(player)[104]);
            Assert.Equal(value, Character.Stats(player)[attribute]);
            // 經正式傷害與封包路徑，獨立驗證公式。此案不代表職業解鎖／施放准入已驗收。
            await combat.CastExtraAsync(player, _app.Services.GetRequiredService<GameData>().Row("SkillConfig", skill)!.Value, [target]);
        });
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == skill);
        Assert.Equal(expected, Assert.Single(hit.Dsts, d => d.DstID == target.Uid).Damage);
        Assert.Equal(1_000_000 - expected, target.Hp);
    }
}
