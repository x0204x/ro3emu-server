using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task EveryFlatPrimaryBuffMatchesTheSameAllocatedStatInDerivedValues()
    {
        await using var bot = await InWorld("PrimaryFlat");
        await Loop.InvokeAsync(() =>
        {
            var session = Session("PrimaryFlat");
            var data = _app.Services.GetRequiredService<GameData>();
            // 只驗同值不同來源的聚合一致性，不把自造 buff 當成玩家施放驗收。
            for (int stat = 1; stat <= 6; stat++)
            {
                var before = Character.Stats(session);
                session.Progress!.Stats[stat - 1] += 9;
                var allocated = Character.Stats(session);
                session.Progress.Stats[stat - 1] -= 9;
                var buff = new ActiveBuff(-stat, 0, long.MaxValue, 0, [(stat, 9)], 0);
                session.Buffs.Add(buff);
                try { AssertPrimaryAndConversions(allocated, Character.Stats(session), stat, data); }
                finally { session.Buffs.Remove(buff); }
                Assert.Equal(before.Data.OrderBy(p => p.Key), Character.Stats(session).Data.OrderBy(p => p.Key));
            }
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task EveryPrimaryPercentAndBuffGroupFeedsItsDisplayedValueIntoConversions()
    {
        await using var bot = await InWorld("PrimaryGroups");
        await Loop.InvokeAsync(() =>
        {
            var session = Session("PrimaryGroups");
            var data = _app.Services.GetRequiredService<GameData>();
            for (int stat = 1; stat <= 6; stat++)
            {
                var before = Character.Stats(session);
                // AttributeConfig：11..16 基礎百分比、71..76 buff 固定值、81..86 buff 百分比。
                // 同時包含百分比與固定值，避免只修直接 +STR 而漏掉同群組成員。
                var buff = new ActiveBuff(-stat, 0, long.MaxValue, 0,
                    [(stat + 10, 5000), (stat + 80, 2500), (stat + 70, 3)], 0);
                session.Buffs.Add(buff);
                var grouped = Character.Stats(session);
                session.Buffs.Remove(buff);
                int difference = checked((int)(grouped[stat] - before[stat]));
                Assert.True(difference > 0);
                session.Progress!.Stats[stat - 1] += difference;
                try { AssertPrimaryAndConversions(Character.Stats(session), grouped, stat, data); }
                finally { session.Progress.Stats[stat - 1] -= difference; }
            }
            return Task.CompletedTask;
        });
    }

    private static void AssertPrimaryAndConversions(StatSheet expected, StatSheet actual, int stat, GameData data)
    {
        Assert.Equal(expected[stat], actual[stat]);
        var row = data.Row("AttributeConversionConfig", stat)!.Value;
        var targets = GameData.Pairs(row, "_kAttribute1").Concat(GameData.Pairs(row, "_kAttribute2"))
            .Select(pair => (int)pair.Item1).Distinct();
        foreach (int target in targets) Assert.Equal(expected[target], actual[target]);
    }
}
