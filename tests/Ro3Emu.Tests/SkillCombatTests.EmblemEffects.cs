using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    // Fixture僅建立原EmblemConfig的已點亮實例；效果授權仍經正式ReachedStages與職業線判定。
    private Task EmblemOwnedAsync(GameSession player, Func<Task> work) => Loop.InvokeAsync(() =>
        _app.Services.GetRequiredService<OwnerRegistry>().RunSessionAsync("test/emblem-effects",
            [ReservationKey.Role(player.Role!.Id)], player, work));

    private Task EmblemMarksAsync(GameSession player, int mark, int count) => EmblemOwnedAsync(player, async () =>
    {
        player.Items.RemoveAll(i => i.Guid is >= 9871000 and < 9871010);
        var plan = new EmblemService.Plan();
        int entry = mark switch { 1001 => 1890112, 1002 => 1890122, 1003 => 1890132, 1004 => 1890142, _ => throw new ArgumentOutOfRangeException(nameof(mark)) };
        for (int slot = 1, left = count; left > 0; slot++)
        {
            int lit = Math.Min(left, 4); left -= lit;
            var item = new InvItem
            {
                Guid = 9871000 + slot, Entry = entry, Stack = 1, Pack = 5, Grid = 0,
                Attrs = new() { [1001] = lit * 5, [10001] = 2, [10002] = slot }
            };
            for (int index = 0; index < lit; index++) item.Attrs[1021 + index] = mark;
            player.Items.Add(item); plan.Slots[slot] = item.Guid;
        }
        player.Emblems.Plans[0] = plan; player.Emblems.Plans[1] = new EmblemService.Plan();
        player.Emblems.CurrentPlan = 0;
        await _app.Services.GetRequiredService<EmblemService>().SyncGenrePlanAsync(player, player.EmblemPower);
    });

    [ClientDataTheory]
    [InlineData(3400, 13305, 1, 1001, 6, 5600, 5900)]
    [InlineData(3400, 13305, 5, 1001, 6, 6000, 6300)]
    [InlineData(2400, 12315, 1, 1004, 10, 22000, 42000)]
    [InlineData(2400, 12315, 5, 1004, 10, 42000, 62000)]
    [InlineData(5400, 15302, 1, 1002, 10, 3200, 4200)]
    [InlineData(5400, 15302, 5, 1002, 10, 4000, 5000)]
    public async Task EmblemDamageUsesExactStageJobAndVariantWithoutPermanentSkillGrant(int job, int group, int level,
        int mark, int required, int normal, int boosted)
    {
        await using var bot = await InWorld("EmblemDamage");
        await Gm("emblemdamage", $"!job {job}"); await Teach("emblemdamage", group, level);
        var player = Session("emblemdamage");
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var data = _app.Services.GetRequiredService<GameData>();
        int id = group * 100 + level;
        var target = await Target("emblemdamage", stationary: true); await CardOffenceDummyAsync(target);

        async Task Check(int ratio)
        {
            var row = new SkillConfig(combat.EffectiveSkillRow(player, id) ?? throw new InvalidOperationException("Effective row required."));
            Assert.Equal(ratio, row.DamageParam1[0]);
            var stats = Character.Stats(player);
            double extra = group == 12315 ? Math.Min(stats[5], 100) * 10 : 0;
            double multiplier = group == 15302 ? 1 + Math.Min(stats[115], 500) * 12 / 10000d : 1;
            long expected = (long)Math.Round(stats[group == 12315 ? 104 : 103] * (ratio + extra) / 10000d * multiplier);
            Assert.Equal(expected, await CardOffenceHitAsync(bot, player, target, row.Row));
        }

        await EmblemMarksAsync(player, mark, required - 1); await Check(normal);
        await EmblemMarksAsync(player, mark, required); await Check(boosted);
        Assert.DoesNotContain(player.Progress!.Skills.Keys, g => g is 113360 or 112308 or 115318);
        if (group == 15302)
            Assert.Equal(boosted * 1.1, new SkillConfig(combat.EffectiveSkillRow(player, id)!.Value).DamageParam1[1], precision: 6);

        await EmblemOwnedAsync(player, async () =>
        {
            player.Emblems.CurrentPlan = 1;
            await _app.Services.GetRequiredService<EmblemService>().SyncGenrePlanAsync(player, player.EmblemPower);
        });
        await Check(normal);
        await Loop.InvokeAsync(() => { player.Emblems.CurrentPlan = 0; return Task.CompletedTask; });
        Assert.Equal(boosted, new SkillConfig(combat.EffectiveSkillRow(player, id)!.Value).DamageParam1[0]);
        await Gm("emblemdamage", "!job 1000");
        Assert.Equal(normal, new SkillConfig(combat.EffectiveSkillRow(player, id)!.Value).DamageParam1[0]);
        await Gm("emblemdamage", $"!job {job}");
        await EmblemMarksAsync(player, mark, 0); await Check(normal);
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task EmblemShieldBlockDoublesOnlyItsAuthoredDefenceAndStopsOnUnequip(int level)
    {
        await using var bot = await InWorld("EchoShield");
        await Gm("echoshield", "!job 1410"); await Teach("echoshield", 11638, level);
        var player = Session("echoshield"); var buffs = _app.Services.GetRequiredService<BuffService>();
        var data = _app.Services.GetRequiredService<GameData>();
        await EmblemMarksAsync(player, 1004, 5);
        await Loop.InvokeAsync(() => buffs.AddSupportAsync(player, 1164501, 10000, player.Role!.Id));
        Assert.Contains(player.Buffs.Single(b => b.ConfigId == 1164501).Changes, c => c.Attr == 1052 && c.Value == 5000);
        await EmblemMarksAsync(player, 1004, 6);
        await Loop.InvokeAsync(() => buffs.AddSupportAsync(player, 1164501, 10000, player.Role!.Id));
        var active = player.Buffs.Single(b => b.ConfigId == 1164501);
        Assert.Contains(active.Changes, c => c.Attr == 1052 && c.Value == 10000);
        Assert.Contains(active.Changes, c => c.Attr == 1062 && c.Value == 10000);
        Assert.Contains(active.Changes, c => c.Attr == 602 && c.Value == 3000);
        Assert.Contains(active.Changes, c => c.Attr == 604 && c.Value == 3000);
        Assert.Equal(10000, EmblemService.BuffAttributes(player, data,
            data.Row("BuffConfig", 11165801)!.Value, [(1052, 10000), (1062, 10000)])[0].Value);
        await EmblemMarksAsync(player, 1004, 0);
        await Loop.InvokeAsync(() => buffs.AddSupportAsync(player, 1164501, 10000, player.Role!.Id));
        Assert.Contains(player.Buffs.Single(b => b.ConfigId == 1164501).Changes, c => c.Attr == 1052 && c.Value == 5000);
    }

    [ClientDataFact]
    public async Task EmblemDamageDoesNotRepeatAnExistingModeOrIndependentTalentModifier()
    {
        await using var bot = await InWorld("EchoNoDouble");
        await Gm("echonodouble", "!job 5400"); var player = Session("echonodouble");
        var combat = _app.Services.GetRequiredService<CombatService>();
        await EmblemMarksAsync(player, 1002, 10);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddSupportAsync(player, 11531801, 10000, player.Role!.Id));
        var row = new SkillConfig(combat.EffectiveSkillRow(player, 1530201) ?? throw new InvalidOperationException("Mode required."));
        Assert.Equal(1, row.Mode); Assert.Equal(4200, row.DamageParam1[0]); Assert.Equal(4620, row.DamageParam1[1]);
        await Gm("echonodouble", "!job 3400");
        await EmblemMarksAsync(player, 1001, 6);
        player.TalentSkillModifiers = [(0, 0, new SkillParameterRules.Modifier(13305, 29, 1, 500, 0))];
        Assert.Equal(6400, new SkillConfig(combat.EffectiveSkillRow(player, 1330501)!.Value).DamageParam1[0]);
        player.TalentSkillModifiers = [];
        Assert.Equal(5900, new SkillConfig(combat.EffectiveSkillRow(player, 1330501)!.Value).DamageParam1[0]);
    }
}
