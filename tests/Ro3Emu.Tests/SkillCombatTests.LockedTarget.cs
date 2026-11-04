using System.Text.Json;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Locked rows with _iRangeType 2 and _kRange [0] (122 rows of 11 groups) hit the locked target where it stands.</summary>
public sealed partial class SkillCombatTests
{
    // The 11 groups, each row a player reaches by a mode at its low and high level: the job, the group, the level, the row
    // the talent node selects. Each text names the target ("the designated ST enemy", "the selected ST enemy", "the locked
    // target", "1 enemies"), which walks 2 m once the cast is accepted. White Imprison has a 2 s chant, the others none.
    [ClientDataTheory]
    [InlineData(1200, 11108, 1, 1110811, 10102127)] // Brandish Spear, Cavalry's Might
    [InlineData(1200, 11108, 10, 1110820, 10102127)]
    [InlineData(1200, 11110, 1, 1111021, 10102115)] // Spear Throw, Explosive Spear
    [InlineData(1200, 11110, 5, 1111025, 10102115)]
    [InlineData(1300, 11213, 1, 1121311, 10103103)] // Spiral Pierce, Spiral Blast
    [InlineData(1300, 11213, 5, 1121315, 10103103)]
    [InlineData(1300, 11221, 1, 1122111, 10104103)] // Joint Beat, Joint Beat Blast
    [InlineData(1300, 11221, 10, 1122120, 10104103)]
    [InlineData(1400, 11310, 1, 1131001, 0)] // Rain of Spears
    [InlineData(1400, 11310, 5, 1131005, 0)]
    [InlineData(1400, 11316, 1, 1131601, 0)] // Sonic Burst
    [InlineData(1400, 11316, 5, 1131605, 0)]
    [InlineData(1210, 11411, 1, 1141101, 0)] // Holy Cross
    [InlineData(1210, 11411, 10, 1141110, 0)]
    [InlineData(1210, 11418, 1, 1141801, 0)] // Shield Boomerang
    [InlineData(1210, 11418, 5, 1141805, 0)]
    [InlineData(1310, 11504, 1, 1150401, 0)] // Rapid Smiting
    [InlineData(1310, 11504, 10, 1150410, 0)]
    [InlineData(2400, 12303, 1, 1230301, 0)] // White Imprison
    [InlineData(2400, 12313, 1, 1231301, 0)] // Chain Lightning: its jump, not this rule, already follows the target
    [InlineData(2400, 12313, 5, 1231305, 0)]
    public async Task ALockedSkillHitsItsTargetWhereItStands(int job, int group, int level, int row, int talent)
    {
        var (bot, me, strikes) = await Caster("Alice", job, group, level, talent);
        await using var _ = bot;
        var mark = await Mark(me, 1);
        Assert.Equal(0, (await Cast(bot, me, group * 100 + level, mark)).Result);
        await Loop.InvokeAsync(() => { mark.X += 2; return Task.CompletedTask; });
        await Eventually(() => strikes.Count > 0);
        var strike = Assert.Single(strikes);
        Assert.Equal(row, strike.Row);
        Assert.Equal(mark.Uid, strike.Target);
        Assert.True(mark.Hp < mark.Template.MaxHp);
    }

    [ClientDataFact] // "to a designated ST enemy within 3 meters"; its row's _iTargetMax 5 finds nobody else
    public async Task HolyCrossHitsOnlyItsTargetInACrowd()
    {
        var (bot, me, strikes) = await Caster("Alice", 1210, 11411, 1);
        await using var _ = bot;
        var mark = await Mark(me, 1);
        await Mark(me, 1);
        await Mark(me, 1);
        Assert.Equal(0, (await Cast(bot, me, 1141101, mark)).Result);
        await Eventually(() => strikes.Count > 0);
        Assert.Equal(mark.Uid, Assert.Single(strikes).Target);
    }

    [Theory] // The pure rule on invented rows.
    [InlineData(2, 1, "[0]", true)]
    [InlineData(2, 1, "[300]", false)] // a real radius
    [InlineData(2, 1, "[]", false)] // no _kRange: the 3 m default
    [InlineData(2, 8, "[0]", false)] // aimed at a point, not at a target
    [InlineData(1, 1, "[0]", false)] // already single-target
    public void OnlyALockedRowWithARangeOfNothingLocksItsTarget(int rangeType, int release, string range, bool locks)
    {
        using var row = JsonDocument.Parse($$"""{"_iRangeType":{{rangeType}},"_iRelease":{{release}},"_kRange":{{range}}}""");
        Assert.Equal(locks, CombatService.LocksItsTarget(row.RootElement));
    }
}
