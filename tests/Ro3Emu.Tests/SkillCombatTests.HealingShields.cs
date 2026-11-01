using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, 400)]
    [InlineData(2, 500)]
    [InlineData(3, 600)]
    [InlineData(4, 700)]
    [InlineData(5, 800)]
    public async Task HealingKyrieEveryLevelUsesPhysicalShieldAndHolyLightBreaksIt(int level, long expected)
    {
        await using var bot = await InWorld("KyrieShield");
        await Gm("kyrieshield", "!job 4200");
        await Teach("kyrieshield", 14117, level);
        await HealingStat(bot, 104, 1000);
        await HealingStat(bot, 102, 10000);
        var source = Session("kyrieshield");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var data = _app.Services.GetRequiredService<GameData>();
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(source, null, 10000));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1411700 + level, TargetId = source.Role!.Id });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser, n => n.Buffs.Any(b => b.ConfigId == 1411700 + level));
        await Loop.InvokeAsync(async () =>
        {
            var buff = Assert.Single(source.Buffs, b => b.ConfigId == 1411700 + level);
            Assert.Equal(10000, buff.DurationMs);
            Assert.Equal(1, buff.ShieldDamageKind);
            Assert.Equal(expected, buffs.ShieldAmount(source));
            Assert.Equal(expected, Character.Stats(source)[2006]);
            Assert.Equal(0, Character.Stats(source)[2008]);
            Assert.Equal(100, await buffs.AbsorbAsync(source, 100, data.Row("SkillConfig", 1200401)));
            Assert.Equal(expected, buffs.ShieldAmount(source));
            Assert.Equal(0, await buffs.AbsorbAsync(source, 100, data.Row("SkillConfig", 1000001)));
            Assert.Equal(expected - 100, buffs.ShieldAmount(source));
            // 重施同一群組只更新，不疊第二份；未知傷害種類不得花掉物理盾。
            await buffs.AddSupportAsync(source, 1411700 + level, 10000, source.Role!.Id, expected);
            Assert.Single(source.Buffs, b => b.ConfigId == 1411700 + level);
            Assert.Equal(expected, buffs.ShieldAmount(source));
            Assert.Equal(50, await buffs.AbsorbAsync(source, 50));
            Assert.Equal(50, await buffs.AbsorbAsync(source, 50, data.Row("SkillConfig", 1401101)));
            Assert.Equal(0, buffs.ShieldAmount(source));
            Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1411700 + level);
        });
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task HealingChoralEveryLevelUsesActualBuffCoefficientAndExpires(int level)
    {
        await using var bot = await InWorld("ChoralShield");
        IsolateDirectHealing();
        await Gm("choralshield", "!job 4400");
        await Teach("choralshield", 14303, level);
        await HealingStat(bot, 104, 1000);
        await HealingStat(bot, 101, 100000);
        await HealingStat(bot, 102, 10000);
        await HealingStat(bot, 5, 0);
        var source = Session("choralshield");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await buffs.AddAsync(source, 11430801, 60000, source.Role!.Id);
            await Character.SetHpSpAsync(source, 1, 10000);
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430300 + level });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(1430310 + level, hit.SkillId);
        Assert.Equal(11000 + level * 500, Assert.Single(hit.Dsts).Damage);
        await Loop.InvokeAsync(async () =>
        {
            long expected = 130 + level * 5;
            Assert.Equal(expected, buffs.ShieldAmount(source));
            Assert.Equal(expected, Character.Stats(source)[2008]);
            var index = source.Buffs.FindIndex(b => b.ConfigId == 11430900 + level);
            Assert.Equal(5000, source.Buffs[index].DurationMs);
            Assert.Equal(0, await buffs.AbsorbAsync(source, 20));
            Assert.Equal(expected - 20, buffs.ShieldAmount(source));
            source.Buffs[index] = source.Buffs[index] with { ExpireMs = MonsterService.NowMs - 1 };
            Assert.Equal(30, await buffs.AbsorbAsync(source, 30));
            Assert.Equal(0, buffs.ShieldAmount(source));
        });
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealingMonsterShieldAbsorbsOnlyItsDamageKindAndPublishesShieldDamage(bool magic)
    {
        await using var bot = await InWorld("MonsterShield");
        if (magic) { await Gm("monstershield", "!job 2100"); await Teach("monstershield", 12004); }
        var source = Session("monstershield");
        var target = await Target("monstershield", stationary: true, immobile: true);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 1000000;
            source.GmCombat |= GmCombatSwitches.FixedDamage;
            await buffs.AddSupportAsync(target, 1411701, 10000, source.Role!.Id, shieldAmount: 100000);
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = magic ? 1200401 : 1000001, TargetId = target.Uid });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        var damage = Assert.Single(hit.Dsts);
        await Loop.InvokeAsync(() =>
        {
            source.Attack = null;
            if (magic) { Assert.True(damage.Damage > 0); Assert.Equal(0, damage.SheildDamage); Assert.Equal(100000, buffs.ShieldAmount(target)); }
            else { Assert.Equal(0, damage.Damage); Assert.True(damage.SheildDamage > 0); Assert.Equal(100000 - damage.SheildDamage, buffs.ShieldAmount(target)); }
            Assert.Equal(1000000 - damage.Damage, target.Hp);
            return Task.CompletedTask;
        });
    }
}
