using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task PrepareGloriaAsync(BotClient bot, string account, int level)
    {
        await Gm(account, "!job 1310");
        await Teach(account, 11501, level);
        await EquipPixelSpearAsync(bot, Session(account));
        await HealingStat(bot, 102, 10000);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session(account), null, 10000));
        _app.Services.GetRequiredService<CombatService>().Rolls = new CardOffenceRolls();
    }

    private TaskCompletionSource GloriaCompleted(GameSession source)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.CastResolved.Add(async (s, cast, monsters, hit) =>
        {
            if (s != source || new SkillConfig(cast.Row).SkillId != 11501) return;
            // 真施放已扣SP後重送同一cast/target，不得再次扣除。
            await combat.GloriaDominiSpAsync(s, cast, monsters, hit);
            await combat.GloriaDominiSpAsync(s, cast, monsters, hit);
        });
        combat.SkillCastCompleted.Add((s, _, row) =>
        {
            if (s == source && new SkillConfig(row).SkillId == 11501)
            { source.Attack = null; done.TrySetResult(); }
            return Task.CompletedTask;
        });
        return done;
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task GloriaDominiRealMonsterHitDeducts100SpOnlyOnceAtLowAndMaxLevel(int level)
    {
        await using var bot = await InWorld("GloriaMonster");
        IsolateDirectHealing();
        await PrepareGloriaAsync(bot, "gloriamonster", level);
        var source = Session("gloriamonster");
        var target = await Target("gloriamonster", stationary: true, immobile: true);
        await Loop.InvokeAsync(() =>
        {
            target.Template = target.Template with { MaxSp = 1000, MaxHp = 1_000_000 };
            target.Hp = 1_000_000; target.CurrentSp = 1000;
            return Task.CompletedTask;
        });
        var done = GloriaCompleted(source);
        using var resource = bot.Inbox.Expect(e => e.IsGame(CombatService.AoiAttrChange)
            && AOIAttrChange.Parser.ParseFrom(e.Body) is var n && n.EntityUuid == (ulong)target.Uid
            && n.Property.Any(p => p.Idx == 2001 && p.Data == 900));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1150100 + level, TargetId = target.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await done.Task.WaitAsync(Wait);
        await resource.WaitAsync(Wait);
        Assert.Equal(900, target.CurrentSp);
        Assert.True(target.Hp < 1_000_000);
    }

    [ClientDataTheory]
    [InlineData(1, 1000, false, false, 900)]
    [InlineData(10, 1000, true, false, 900)]
    [InlineData(1, 40, false, false, 0)]
    [InlineData(10, 1000, true, true, 1000)]
    public async Task GloriaDominiRealPvpHitUsesCurrentSpAndIncludesFullShield(int level, int sp,
        bool shield, bool immune, int expected)
    {
        await using var a = await InWorld("GloriaPvpA");
        await using var b = await InWorld("GloriaPvpB");
        await PrepareGloriaAsync(a, "gloriapvpa", level);
        await Gm("gloriapvpb", "!job 1310");
        var source = Session("gloriapvpa"); var target = Session("gloriapvpb");
        await CardOffenceRoom((a, source), (b, target));
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(async () =>
        {
            combat.SetEventPvPHandlers((s, t) => s == source && t == target, (_, _) => Task.CompletedTask);
            target.Buffs.Clear(); target.Position = source.Position.Clone();
            if (shield) target.Buffs.Add(new ActiveBuff(888815, 0, MonsterService.NowMs + 60000, 60000,
                [], target.Role!.Id, ShieldRemaining: 1_000_000, ShieldDebuffImmunity: immune));
            await Character.SetHpSpAsync(target, 1_000_000, sp);
        });
        var done = GloriaCompleted(source);
        using var resource = b.Inbox.Expect(e => e.IsGame(CombatService.AoiAttrChange)
            && AOIAttrChange.Parser.ParseFrom(e.Body) is var n && n.EntityUuid == (ulong)target.Role!.Id
            && n.Property.Any(p => p.Idx == 2001 && p.Data == expected));
        await a.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1150100 + level, TargetId = target.Role!.Id });
        var accepted = await Next(a, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
        var hit = await Next(a, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        var dst = Assert.Single(hit.Dsts, d => d.DstID == target.Role.Id);
        Assert.NotEqual(DamageHitResult.Miss, dst.HitResult);
        if (shield) { Assert.Equal(0, dst.Damage); Assert.True(dst.SheildDamage > 0); }
        else Assert.True(dst.Damage > 0);
        await done.Task.WaitAsync(Wait);
        if (expected != sp) await resource.WaitAsync(Wait);
        Assert.Equal(expected, Character.Stats(target)[2001]);
        Assert.DoesNotContain(target.Buffs, x => x.ConfigId is >= 1150801 and <= 1150810);
    }

    [ClientDataTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task GloriaDominiCapturedVictimRejectsMissAndObsoleteEpoch(int rejection)
    {
        await using var a = await InWorld("GloriaEpochA");
        await using var b = await InWorld("GloriaEpochB");
        var source = Session("gloriaepocha"); var target = Session("gloriaepochb");
        await CardOffenceRoom((a, source), (b, target));
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        await Loop.InvokeAsync(async () =>
        {
            combat.SetEventPvPHandlers((s, t) => s == source && t == target, (_, _) => Task.CompletedTask);
            await Character.SetHpSpAsync(target, null, 10000);
            var cast = new ActiveSkillCast
            {
                Id = 12345, OwnerId = source.Role!.Id, WorldScene = source.WorldScene,
                SceneGeneration = source.SceneGeneration, Attachment = source.Attachment,
                Origin = source.Position.Clone(), StartMs = MonsterService.NowMs, ChantMs = 0, ActionMs = 0,
                Request = new CastSkillReq { SkillId = 1150101, TargetId = target.Role!.Id },
                Row = SkillConfig.Get(data, 1150101)!.Value.Row,
            };
            combat.CaptureGloriaDominiTargets(source, cast, [], [target]);
            // 合成已發布結果：專門驗捕獲後世代失效與Miss；真施放由上面六案覆蓋。
            cast.PublishedPlayerHits.Add(new(target, target.Role.Id, 0,
                rejection == 0 ? DamageHitResult.Miss : DamageHitResult.Normal, () => true));
            if (rejection == 1) target.SkillProtocol = new();
            if (rejection == 2) target.SceneGeneration++;
            if (rejection == 3) source.SkillProtocol = new();
            await combat.GloriaDominiSpAsync(source, cast, [], new SkillHitInfo());
            Assert.Equal(10000, Character.Stats(target)[2001]);
        });
    }
}
