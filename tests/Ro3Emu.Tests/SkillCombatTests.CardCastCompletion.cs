using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task CardCooldownResetRollsOnlyOnceForAMultiWaveCastEvenWhenTheRollFails()
    {
        await using var bot = await InWorld("CardMultiWave");
        await Gm("cardmultiwave", "!job 2200");
        await Teach("cardmultiwave", 12106);
        var source = Session("cardmultiwave");
        var target = await Target("cardmultiwave", stationary: true, immobile: true);
        var effects = _app.Services.GetRequiredService<CardEffects>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        int rolls = 0, completions = 0;
        ActiveSkillCast? completed = null;
        await Loop.InvokeAsync(async () =>
        {
            await EquipEventCard(source, 2052901);
            target.Hp = 1_000_000; target.StaysOutOfBattle = true;
            effects.Roll = _ => { rolls++; return 9999; };
            combat.SkillCastCompleted.Add((s, cast, _) =>
            { if (s == source) { completions++; completed = cast; } return Task.CompletedTask; });
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210601, TargetPos = target.Pos });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        for (int i = 0; i < 2; i++) await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1210601);
        var skill = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1210601)!.Value;
        await Task.Delay((int)skill.LogicData[2] + 200);
        Assert.Equal(1, completions);
        Assert.Equal(1, rolls);
        Assert.NotNull(completed);
        await Loop.InvokeAsync(() => effects.ResetAfterCastAsync(source, completed, skill.Row));
        Assert.Equal(1, rolls); // 同SessionId的重送不再抽籤，失敗也不可再抽。
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CardCooldownResetIncludesCompletedResurrectionButNotAnInterruptedChant(bool interrupt)
    {
        await using var caster = await InWorld("CardRevive");
        await using var other = await InWorld("CardReviveDead");
        var source = Session("cardrevive"); var target = Session("cardrevivedead");
        var combat = _app.Services.GetRequiredService<CombatService>();
        int offers = 0, completions = 0;
        await Loop.InvokeAsync(async () =>
        {
            source.Progress!.Level = 50; source.Progress.Skills[14112] = 1;
            target.Dead = true;
            await Character.SetHpSpAsync(source, null, long.MaxValue);
            await Character.SetHpSpAsync(target, 0, 0);
            await EquipEventCard(source, 2052901);
            _app.Services.GetRequiredService<CardEffects>().Roll = _ => 0;
            combat.ReviveOffer = (_, _, _) => { offers++; return Task.CompletedTask; };
            combat.SkillCastCompleted.Add((s, _, _) => { if (s == source) completions++; return Task.CompletedTask; });
        });
        await caster.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1411201, TargetId = target.Role!.Id });
        Assert.Equal(0, (await Next(caster, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        if (interrupt)
        {
            await caster.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq());
            Assert.True((await Next(caster, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        }
        await Task.Delay(1800);
        Assert.Equal(interrupt ? 0 : 1, offers);
        Assert.Equal(interrupt ? 0 : 1, completions);
        Assert.Equal(interrupt, Math.Max(source.SkillCds.GetValueOrDefault(14112), source.SkillModeCds.GetValueOrDefault((14112, 0))) > MonsterService.NowMs);
    }

    [ClientDataFact]
    public async Task CardCooldownResetUsesThePrimaryGuardianWrapperAfterItsEffectCompletes()
    {
        await using var bot = await InWorld("Alice");
        await EquipGuardian(bot, 11001, 1611001);
        var source = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        int completions = 0;
        var completionSeen = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            await EquipEventCard(source, 2052901);
            await Character.SetHpSpAsync(source, 1, null);
            _app.Services.GetRequiredService<CardEffects>().Roll = _ => 0;
            combat.SkillCastCompleted.Add((s, _, row) =>
            {
                if (s == source) { completions++; completionSeen.TrySetResult(new SkillConfig(row).Id); }
                return Task.CompletedTask;
            });
        });
        using var reset = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(i => i.SkillId == 23562 && i.CdEndTimestampMs == i.CdStartTimestampMs));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2356201 });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2356101);
        await reset.WaitAsync(Wait);
        // 收到封包時，伺服器仍可能正在完成 SendGameAsync；另外等待後續完成事件，避免把網路接收當成所有回呼已完成。
        Assert.Equal(2356201, await completionSeen.Task.WaitAsync(Wait));
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(1, completions);
            Assert.True(Math.Max(source.SkillCds.GetValueOrDefault(23562), source.SkillModeCds.GetValueOrDefault((23562, 0))) <= MonsterService.NowMs);
            return Task.CompletedTask;
        });
    }
}
