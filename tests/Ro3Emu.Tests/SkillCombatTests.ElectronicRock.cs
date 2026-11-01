using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task PreparePrecisionAsync(BotClient bot, string name, int level)
    {
        IsolateDirectHealing();
        await Gm(name, "!job 1400"); await Teach(name, 11315, level); await Teach(name, 11310, level);
        var source = Session(name);
        await EquipPixelSpearAsync(bot, source);
        await Loop.InvokeAsync(() =>
        {
            source.Energy[140001].Value = 0; source.Energy[140002].Value = 0;
            source.Energy[140002].NextRecoverMs = MonsterService.NowMs + 600000;
            return Task.CompletedTask;
        });
        _app.Services.GetRequiredService<CombatService>().Rolls = new CardOffenceRolls();
    }

    private async Task<SkillHitInfo> PrecisionWireAsync(BotClient bot, GameSession source, long target, int skill)
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Completed(GameSession s, ActiveSkillCast _, System.Text.Json.JsonElement row)
        {
            if (s == source && new SkillConfig(row).SkillId == skill / 100) { source.Attack = null; done.TrySetResult(); }
            return Task.CompletedTask;
        }
        combat.SkillCastCompleted.Add(Completed);
        try
        {
            await Loop.InvokeAsync(async () =>
            {
                await combat.ClearSkillsAsync(source);
                source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear();
            });
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
            await done.Task.WaitAsync(Wait);
            return hit;
        }
        finally { combat.SkillCastCompleted.Remove(Completed); }
    }

    [ClientDataTheory]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    public async Task ElectronicRockRealPrecisionConvertsSixOrFourHitsAndPaysForRealRain(int level, bool resonance)
    {
        await using var bot = await InWorld("RockPrecision");
        await PreparePrecisionAsync(bot, "rockprecision", level);
        var source = Session("rockprecision");
        await EmblemMarksAsync(source, 1004, resonance ? 6 : 5);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var target = await Target("rockprecision", stationary: true, immobile: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        int required = resonance ? 4 : 6;
        var precision = new SkillConfig(combat.EffectiveSkillRow(source, 1131500 + level)!.Value);
        Assert.Equal(0, precision.Mode); Assert.Equal(10000 + level * 1000, Assert.Single(precision.DamageParam1));
        combat.CastResolved.Add((s, cast, monsters, hit) => s == source
            ? combat.PrecisionEnergyAsync(s, cast, monsters, hit) : Task.CompletedTask); // 同一cast重送不再加一次。
        using var earned = bot.Inbox.Expect(e => e.IsGame(BuffService.EnergyNotify)
            && PlayerEnergyNotify.Parser.ParseFrom(e.Body) is var n && n.EnergyConfigId == 140002 && n.EnergyValue == 1);
        for (int i = 1; i <= required; i++)
        {
            var hit = await PrecisionWireAsync(bot, source, target.Uid, 1131500 + level);
            Assert.True(Assert.Single(hit.Dsts, d => d.DstID == target.Uid).Damage > 0);
            Assert.Equal(i % required, source.Energy[140001].Value);
            Assert.Equal(i / required, source.Energy[140002].Value);
        }
        await earned.WaitAsync(Wait);
        var rain = new SkillConfig(combat.EffectiveSkillRow(source, 1131000 + level)!.Value);
        Assert.Equal(10000 + level * 1000 + (resonance ? 2500 : 0), Assert.Single(rain.DamageParam1));
        long before = target.Hp;
        var rainHit = await PrecisionWireAsync(bot, source, target.Uid, 1131000 + level);
        Assert.Equal(Assert.Single(rainHit.Dsts, d => d.DstID == target.Uid).Damage, before - target.Hp);
        Assert.True(target.Hp < before); Assert.Equal(0, source.Energy[140002].Value);
    }

    [ClientDataFact]
    public async Task ElectronicRockPvpFullShieldCountsOneHitAndRevocationPreservesProgress()
    {
        await using var a = await InWorld("RockPvpA"); await using var b = await InWorld("RockPvpB");
        await PreparePrecisionAsync(a, "rockpvpa", 1); await Gm("rockpvpb", "!job 1400");
        var source = Session("rockpvpa"); var target = Session("rockpvpb");
        await CardOffenceRoom((a, source), (b, target));
        var combat = _app.Services.GetRequiredService<CombatService>();
        await EmblemMarksAsync(source, 1004, 6);
        await Loop.InvokeAsync(() =>
        {
            combat.SetEventPvPHandlers((s, t) => s == source && t == target, (_, _) => Task.CompletedTask);
            source.Energy[140001].Value = 2; source.Energy[140002].Value = 0;
            target.Position = source.Position.Clone();
            target.Buffs.Add(new ActiveBuff(889931, 0, MonsterService.NowMs + 60000, 60000, [], target.Role!.Id, ShieldRemaining: 1_000_000));
            return Task.CompletedTask;
        });
        var hit = await PrecisionWireAsync(a, source, target.Role!.Id, 1131501);
        var dst = Assert.Single(hit.Dsts, d => d.DstID == target.Role.Id);
        Assert.Equal(0, dst.Damage); Assert.True(dst.SheildDamage > 0);
        Assert.Equal(3, source.Energy[140001].Value);
        await EmblemMarksAsync(source, 1004, 0);
        Assert.Equal(3, source.Energy[140001].Value);
        await PrecisionWireAsync(a, source, target.Role.Id, 1131501);
        Assert.Equal(4, source.Energy[140001].Value); Assert.Equal(0, source.Energy[140002].Value);
        await PrecisionWireAsync(a, source, target.Role.Id, 1131501);
        await PrecisionWireAsync(a, source, target.Role.Id, 1131501);
        Assert.Equal(0, source.Energy[140001].Value); Assert.Equal(1, source.Energy[140002].Value);
    }

    [ClientDataFact]
    public async Task ElectronicRockMissAndExtraDoNotChargeAndFullChargesDoNotOverflow()
    {
        await using var bot = await InWorld("RockReject"); await PreparePrecisionAsync(bot, "rockreject", 1);
        var source = Session("rockreject"); var target = await Target("rockreject", stationary: true, immobile: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.CastDamageOverrides.Add((s, _, _, _) => s == source ? (0, DamageHitResult.Miss) : null);
        var hit = await PrecisionWireAsync(bot, source, target.Uid, 1131501);
        Assert.Equal(DamageHitResult.Miss, Assert.Single(hit.Dsts).HitResult); // 合成Miss，原表技能必中。
        Assert.Equal(0, source.Energy[140001].Value);
        combat.CastDamageOverrides.Clear();
        await Loop.InvokeAsync(async () =>
        {
            var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1131501)!.Value.Row;
            await combat.CastExtraAsync(source, row, [target]);
            Assert.True(target.Hp is > 0 and < 1_000_000); Assert.False(target.Dead);
            Assert.Equal(0, source.Energy[140001].Value);
            source.Energy[140001].Value = 5; source.Energy[140002].Value = 5;
        });
        await PrecisionWireAsync(bot, source, target.Uid, 1131501);
        Assert.Equal(0, source.Energy[140001].Value); Assert.Equal(5, source.Energy[140002].Value);
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ElectronicRockDeathResetsOnlyOriginalMetersAndOldEpochCannotClearNewState(bool invalidated)
    {
        await using var bot = await InWorld("RockDeath"); await PreparePrecisionAsync(bot, "rockdeath", 1);
        var source = Session("rockdeath"); var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(async () =>
        {
            source.Energy[140001].Value = 3; source.Energy[140002].Value = 4; source.Energy[140003].Value = 2;
            long next = source.Energy[140002].NextRecoverMs;
            var reset = combat.CapturePrecisionDeath(source);
            if (invalidated) source.SkillProtocol = new();
            source.Dead = true;
            await reset(); await reset();
            Assert.Equal(invalidated ? 3 : 0, source.Energy[140001].Value);
            Assert.Equal(invalidated ? 4 : 0, source.Energy[140002].Value);
            Assert.Equal(2, source.Energy[140003].Value);
            Assert.Equal(next, source.Energy[140002].NextRecoverMs);
            source.Dead = false;
        });
        if (!invalidated)
        {
            await Loop.InvokeAsync(async () =>
            {
                source.Energy[140001].Value = 2; source.Energy[140002].Value = 3;
                await combat.EnvironmentDamageAsync(source, 10_000_000);
            });
            Assert.True(source.Dead); Assert.Equal(0, source.Energy[140001].Value); Assert.Equal(0, source.Energy[140002].Value);
        }
    }

    [ClientDataFact]
    public async Task ElectronicRockAllAuthoredRainModesGainOnly2500AndCareerRevokesGrant()
    {
        await using var bot = await InWorld("RockModes"); await PreparePrecisionAsync(bot, "rockmodes", 1);
        var source = Session("rockmodes"); var data = _app.Services.GetRequiredService<GameData>();
        await EmblemMarksAsync(source, 1004, 6);
        var modifier = Assert.Single(EmblemService.SkillEffects(source, data), m => m.Group == 11310 && m.Type == 29);
        Assert.Equal(2500, modifier.Value);
        int rows = 0;
        foreach (var raw in data.Table("SkillConfig").Values.Where(r => new SkillConfig(r).SkillId == 11310))
        {
            var original = new SkillConfig(raw);
            var changed = new SkillConfig(SkillParameterRules.Effective(raw, [modifier], []));
            Assert.Equal(original.Mode, changed.Mode);
            Assert.Equal(original.DamageParam1[0] + 2500, changed.DamageParam1[0]);
            Assert.Equal(original.Cost.Select(x => string.Join(",", x)), changed.Cost.Select(x => string.Join(",", x)));
            rows++;
        }
        Assert.True(rows >= 20);
        await Gm("rockmodes", "!job 1000");
        Assert.DoesNotContain(EmblemService.SkillEffects(source, data), m => m.Group == 11310 && m.Type == 29);
        Assert.False(source.Energy.ContainsKey(140001)); Assert.False(source.Energy.ContainsKey(140002));
    }
}
