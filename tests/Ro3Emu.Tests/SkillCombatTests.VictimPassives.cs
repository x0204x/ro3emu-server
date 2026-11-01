using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private Task VictimPassiveHitAsync(GameSession source, GameSession target)
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var row = new SkillConfig(SkillConfig.Get(data, 1100101)!.Value.AsDescribedHit(0, 1000, 1));
        return Loop.InvokeAsync(() => combat.CastExtraOnPlayerAsync(source, row, target));
    }

    private Task PrepareVictimPassiveAsync(GameSession source, GameSession target, int group, int level) => Loop.InvokeAsync(() =>
    {
        target.Progress!.Skills[group] = level;
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CardOffenceRolls();
        combat.SetEventPvPHandlers((a, b) => a == source && b == target, (_, _) => Task.CompletedTask);
        target.Buffs.Clear();
        return Task.CompletedTask;
    });

    [ClientDataFact]
    public async Task VictimEnergyCoatAnswersRealPlayerDamageAndKeepsCooldownAcrossSceneEpoch()
    {
        await using var a = await InWorld("VictimCoatA");
        await using var b = await InWorld("VictimCoatB");
        await Gm("victimcoata", "!job 2200");
        await Gm("victimcoatb", "!job 2200");
        var source = Session("victimcoata"); var target = Session("victimcoatb");
        await CardOffenceRoom((a, source), (b, target));
        await PrepareVictimPassiveAsync(source, target, 12003, 1);
        var passive = _app.Services.GetRequiredService<PassiveSkills>();
        long now = 100_000; passive.VictimNow = () => now;
        await VictimPassiveHitAsync(source, target);
        Assert.Equal(999000, target.CurrentHp);
        Assert.Contains(target.Buffs, x => x.ConfigId == 1201501);
        await Loop.InvokeAsync(() => { target.Buffs.Clear(); target.SceneGeneration++; return Task.CompletedTask; });
        await VictimPassiveHitAsync(source, target);
        Assert.DoesNotContain(target.Buffs, x => x.ConfigId == 1201501);
        now += 10_000;
        await VictimPassiveHitAsync(source, target);
        Assert.Contains(target.Buffs, x => x.ConfigId == 1201501);
    }

    [ClientDataTheory]
    [InlineData(1, 160000, 60000)]
    [InlineData(5, 200000, 100000)]
    public async Task VictimEmergencyTreatmentUsesRealPvpHpShieldAndOriginalCooldown(int level, long shield, long heal)
    {
        await using var a = await InWorld("VictimTreatA");
        await using var b = await InWorld("VictimTreatB");
        await Gm("victimtreata", "!job 6400");
        await Gm("victimtreatb", "!job 6400");
        var source = Session("victimtreata"); var target = Session("victimtreatb");
        await CardOffenceRoom((a, source), (b, target));
        await PrepareVictimPassiveAsync(source, target, 16303, level);
        var passive = _app.Services.GetRequiredService<PassiveSkills>();
        long now = 100_000; passive.VictimNow = () => now;
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(target, 400000, null));
        await VictimPassiveHitAsync(source, target);
        Assert.Equal(399000 + heal, target.CurrentHp);
        Assert.Equal(shield, Assert.Single(target.Buffs, x => x.ConfigId == 1630800 + level).ShieldRemaining);
        await Loop.InvokeAsync(() => { target.Buffs.Clear(); return Task.CompletedTask; });
        await VictimPassiveHitAsync(source, target);
        Assert.Equal(398000 + heal, target.CurrentHp);
        Assert.DoesNotContain(target.Buffs, x => x.ConfigId == 1630800 + level);
    }

    [ClientDataTheory]
    [InlineData(1, 60000)]
    [InlineData(3, 100000)]
    public async Task VictimPrayerQuietTimerIsResetByPvpAndOldSceneCannotRestoreShield(int level, long shield)
    {
        await using var a = await InWorld("VictimPrayA");
        await using var b = await InWorld("VictimPrayB");
        await Gm("victimpraya", "!job 4400");
        await Gm("victimprayb", "!job 4400");
        var source = Session("victimpraya"); var target = Session("victimprayb");
        await CardOffenceRoom((a, source), (b, target));
        await PrepareVictimPassiveAsync(source, target, 14306, level);
        var passive = _app.Services.GetRequiredService<PassiveSkills>();
        long now = 100_000; passive.VictimNow = () => now;
        var callbacks = new List<Func<Task>>();
        passive.ScheduleVictimQuiet = (delay, callback) => { Assert.Equal(30050, delay.TotalMilliseconds); callbacks.Add(callback); };
        await VictimPassiveHitAsync(source, target);
        now += 20_000;
        await VictimPassiveHitAsync(source, target);
        Assert.Equal(2, callbacks.Count);
        now += 10_050;
        await Loop.InvokeAsync(callbacks[0]);
        Assert.DoesNotContain(target.Buffs, x => x.ConfigId == 1430600 + level);
        now += 20_000;
        await Loop.InvokeAsync(callbacks[1]);
        Assert.Equal(shield, Assert.Single(target.Buffs, x => x.ConfigId == 1430600 + level).ShieldRemaining);
        await Loop.InvokeAsync(() => { target.Buffs.Clear(); return Task.CompletedTask; });
        await VictimPassiveHitAsync(source, target);
        await Loop.InvokeAsync(() => { target.SceneGeneration++; return Task.CompletedTask; });
        now += 30_050;
        await Loop.InvokeAsync(callbacks[2]);
        Assert.DoesNotContain(target.Buffs, x => x.ConfigId == 1430600 + level);
    }

    [ClientDataFact]
    public async Task VictimEmergencyHealingDoesNotRestoreDamageCommittedDuringShieldPublication()
    {
        await using var a = await InWorld("VictimAwaitA");
        await using var b = await InWorld("VictimAwaitB");
        await Gm("victimawaita", "!job 6400");
        await Gm("victimawaitb", "!job 6400");
        var source = Session("victimawaita"); var target = Session("victimawaitb");
        await CardOffenceRoom((a, source), (b, target));
        await PrepareVictimPassiveAsync(source, target, 16303, 1);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(target, 400000, null));
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var data = _app.Services.GetRequiredService<GameData>();
        bool injected = false;
        buffs.Changed.Add(async player =>
        {
            if (injected || player != target || !target.Buffs.Any(x => x.ConfigId == 1630801)) return;
            injected = true;
            var extra = new SkillConfig(SkillConfig.Get(data, 1100101)!.Value.AsDescribedHit(0, 167000, 1));
            await combat.CastExtraOnPlayerAsync(source, extra, target); // 160000盾＋7000真HP傷害。
            Assert.Equal(392000, target.CurrentHp);
        });
        await VictimPassiveHitAsync(source, target);
        Assert.True(injected);
        Assert.Equal(452000, target.CurrentHp); // 最新392000＋60000；不可回舊399000再補血。
    }

    [ClientDataTheory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public async Task VictimReactionUsesPreHitVictimEpochIndependentlyOfAttacker(int invalidation, bool expected)
    {
        await using var a = await InWorld("VictimEpochA");
        await using var b = await InWorld("VictimEpochB");
        await Gm("victimepocha", "!job 2200");
        await Gm("victimepochb", "!job 2200");
        var source = Session("victimepocha"); var target = Session("victimepochb");
        await CardOffenceRoom((a, source), (b, target));
        await PrepareVictimPassiveAsync(source, target, 12003, 1);
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.PvpDealtAsync.Insert(0, (_, _, _, _) =>
        {
            if (invalidation == 0) source.SceneGeneration++;
            else if (invalidation == 1) target.SceneGeneration++;
            else target.SkillProtocol = new();
            return Task.CompletedTask;
        });
        await VictimPassiveHitAsync(source, target);
        Assert.Equal(999000, target.CurrentHp); // 已提交的真傷害不因來源之後失效回復。
        Assert.Equal(expected, target.Buffs.Any(x => x.ConfigId == 1201501));
    }
}
