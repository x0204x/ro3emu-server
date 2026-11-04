using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Microsoft.Extensions.DependencyInjection;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>The hits a row deals, against the total its text gives.</summary>
    [ClientDataTheory]
    [InlineData(1630601, 2, 12000)] // Spinning Axe, "2 hits ... totaling PATK * 120%"
    [InlineData(1532601, 2, 22000)] // Piercing Shadow, "a total of PATK*220%"
    [InlineData(1162421, 11, 35000)] // Earthshaker's spin, "a total of PATK * 350%"
    [InlineData(1162801, 3, 9000)] // Spear Formation - Crescent Moon, hits of PATK * 30%
    [InlineData(1230201, 2, 8000)] // Soul Expansion, two bursts of 40%
    [InlineData(1231601, 4, 32000)] // Tetra Vortex, four values of 80% (inferred: the row counts over "totaling")
    [InlineData(1234201, 2, 19950)] // Gravitational Impact, 66.5% then the Burst of 133%
    [InlineData(1162401, 2, 15000)] // Fearless Charge, 30% then 120%
    [InlineData(1630401, 7, 75000)] // Axe Tornado, "total ... PATK * 750%"
    [InlineData(1630455, 13, 313600)] // its enhanced row, "PATK*3136%"
    [InlineData(1631201, 2, 4000)] // Arm Cannon, "totaling PATK * 40%"
    [InlineData(1631245, 4, 20800)] // its enhanced row, "PATK*208%"
    [InlineData(1161301, 10, 100000)] // Shield Defense, 100% every 0.5 s for 5 s
    public async Task AMultiHitSkillDealsTheTotalItsTextGives(int skill, int hits, double total)
    {
        await using var alice = await InWorld("Alice");
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, skill)!.Value;
        var dealt = CombatService.ChannelHits(row.Row, data.SkillTemplate(row.ScriptShow));
        Assert.Equal(hits, dealt.Length);
        Assert.Equal(total, dealt.Sum(hit => new SkillConfig(hit.Row).DamageParam1[0]));
    }

    /// <summary>A skill with several attack points but one hit by its text stays one hit.</summary>
    [ClientDataTheory]
    [InlineData(1130905)] // Sonic Wave: "3 segments totaling" one value, split into words
    [InlineData(1300301)] // Double Strafe: one value, split into words (inferred)
    [InlineData(1141201)] // Grand Cross: its 150 is an HP cost
    [InlineData(1110401)] // Bowling Bash: "three hits" is how one hit is shown
    public async Task AOneHitSkillStaysOneHit(int skill)
    {
        await using var alice = await InWorld("Alice");
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, skill)!.Value;
        Assert.Empty(CombatService.ChannelHits(row.Row, data.SkillTemplate(row.ScriptShow)));
    }

    [ClientDataFact] // "Sequentially deals Water, Fire, Earth, and Wind", each with its own 50%, and at least one status for sure
    public async Task TetraVortexHitsInItsFourElements()
    {
        await using var alice = await InWorld("Alice");
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, 1231601)!.Value;
        var hits = CombatService.ChannelHits(row.Row, data.SkillTemplate(row.ScriptShow)).Select(hit => new SkillConfig(hit.Row)).ToArray();
        Assert.Equal([5L, 4, 3, 2], hits.Select(hit => hit.EleType));
        Assert.Equal([902001L, 902701, 902101, 901601], hits.Select(hit => hit.Buff2.Single()[0]));
        Assert.All(hits, hit => Assert.Equal(5000, hit.Buff2.Single()[1]));
        Assert.False(hits[0].Row.TryGetProperty(SkillConfig.GuaranteedStatusField, out _));
        Assert.True(hits[3].Row.TryGetProperty(SkillConfig.GuaranteedStatusField, out _));
    }

    [ClientDataFact] // the last stage has its own word count: Axe Tornado [3,1]
    public async Task AStagesLastHitHasItsOwnWords()
    {
        await using var alice = await InWorld("Alice");
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, 1630401)!.Value;
        var hits = CombatService.ChannelHits(row.Row, data.SkillTemplate(row.ScriptShow));
        Assert.Equal(3, new SkillConfig(hits[0].Row).ShowDmg[0]);
        Assert.Equal(1, new SkillConfig(hits[^1].Row).ShowDmg[0]);
        Assert.True(hits.Zip(hits.Skip(1)).All(pair => pair.First.Point < pair.Second.Point));
    }

    [ClientDataTheory]
    [InlineData(1220101, 500)] // Magic Crasher: MATK
    [InlineData(1220111, 500)] // Magic Cannon: MATK
    [InlineData(1521001, 800)] // Soul Destroyer: both
    [InlineData(1524001, 800)] // Advanced Soul Destroyer: both
    [InlineData(1431601, 500)] // Double Holy Light's physical hit: MATK
    [InlineData(1000001, 300)] // a physical row: ATK
    public async Task AHitsRatioMultipliesTheSourceItsTextNames(int skill, double source)
    {
        await using var alice = await InWorld("Alice");
        var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), skill)!.Value;
        Assert.Equal(source, CombatService.DamageSource(row.Row, atk: 300, matk: 500, magic: false));
    }
}
