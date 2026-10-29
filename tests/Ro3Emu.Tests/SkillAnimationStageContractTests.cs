using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>
/// Locks down the two exported timelines whose attack-point, server-bullet and ActionDamage inventories have equal
/// cardinality. Equal cardinality is evidence of a multi-stage animation candidate, not proof that entries at the
/// same ordinal are one protocol stage, and this test intentionally makes no assertion about damage distribution.
/// </summary>
public sealed class SkillAnimationStageContractTests
{
    private static readonly GameData Data = new(
        Options.Create(new ServerOptions { DataDir = Path.Combine(ClientData.RepoRoot, "data") }),
        NullLogger<GameData>.Instance);

    [ClientDataFact]
    public void EqualCardinalityCandidatesKeepTheirExportedOrderedInventories()
    {
        AssertCandidate(
            scriptShow: 111101,
            attackPoints: [0.06666667, 0.2333333],
            bulletEffects: [11110102, 11110103],
            actionEffects: [11110105, 11110106]);

        AssertCandidate(
            scriptShow: 155000,
            attackPoints: [0.1666667, 0.3333333, 0.3333333],
            bulletEffects: [15500008, 15500010, 15500012],
            actionEffects: [15500009, 15500011, 15500013]);
    }

    private static void AssertCandidate(
        long scriptShow, double[] attackPoints, long[] bulletEffects, long[] actionEffects)
    {
        var template = Assert.IsType<JsonElement>(Data.SkillTemplate(scriptShow));
        Assert.False(new SkillTimelineData(template).HasClientPoints);

        var actualPoints = template.GetProperty("m_kAtkPoints").EnumerateArray()
            .Select(point => point.GetDouble()).ToArray();
        Assert.Equal(attackPoints.Length, actualPoints.Length);
        for (int i = 0; i < attackPoints.Length; i++)
            Assert.Equal(attackPoints[i], actualPoints[i], precision: 7);

        var effects = Data.SkillTemplateTable("Lua_Config_EffectData");
        var actualBullets = new List<long>();
        var actualActions = new List<long>();
        for (long id = scriptShow * 100; id < scriptShow * 100 + 100; id++)
        {
            if (!effects.TryGetValue(id.ToString(), out var effect)
                || new SkillEffectData(effect).Damage is not { } damage) continue;
            if (damage is { DamageType: 2, Bullet: { } bullet }
                && !string.IsNullOrWhiteSpace(bullet.Name)) actualBullets.Add(id);
            if (damage is { DamageType: 3, HasActionDamage: true }) actualActions.Add(id);
        }

        Assert.Equal(bulletEffects, actualBullets);
        Assert.Equal(actionEffects, actualActions);
        Assert.Equal(actualPoints.Length, actualBullets.Count);
        Assert.Equal(actualPoints.Length, actualActions.Count);
    }
}
