using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task LeaseCompanion(Ro3Emu.Bot.BotClient bot, int lease)
    {
        await bot.SendGameAsync(CareerSpecialService.Lease, new LeaseCareerSpecialItemReq { CartRemouldCfgId = lease });
        Assert.Equal(0, (await Next(bot, CareerSpecialService.Lease, LeaseCareerSpecialItemRsp.Parser)).ErrCode);
    }

    [ClientDataTheory]
    [InlineData(1, 300, 3)]
    [InlineData(5, 500, 5)]
    public async Task LeasedFalconNormalAttackUsesTheFivePercentFollowUpRowAtBothLevels(int level, int ratio, int flat)
    {
        await using var bot = await InWorld("FalconPassive");
        await Gm("falconpassive", "!lvl 99"); await Gm("falconpassive", "!job 3200");
        await Teach("falconpassive", 13101); await Teach("falconpassive", 13102, level);
        await LeaseCompanion(bot, 1500100);
        await HealingStat(bot, 103, 1000); await HealingStat(bot, 2, 0);
        var s = Session("falconpassive");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        _app.Services.GetRequiredService<PassiveSkills>().Roll = _ => 999; // 10%邊界內。
        var target = await Target("falconpassive", stationary: true, immobile: true);
        await Loop.InvokeAsync(() =>
        {
            target.Hp = 1_000_000; target.StaysOutOfBattle = true; target.KeepsDamage = true;
            target.Template = target.Template with { Def = 0, Element = 1 };
            return Task.CompletedTask;
        });
        int normal = _app.Services.GetRequiredService<SkillService>().NormalAttack(s);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = normal, TargetId = target.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var follow = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1311000 + level);
        Assert.Equal((long)Math.Round(1000d * ratio / 10000 + flat), Assert.Single(follow.Dsts, d => d.DstID == target.Uid).Damage);
        await Task.Delay(200);
        int Hits() => bot.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 1311000 + level);
        Assert.Equal(1, Hits());
        await Loop.InvokeAsync(async () =>
        {
            var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), normal)!.Value.Row;
            _app.Services.GetRequiredService<PassiveSkills>().Roll = _ => 1000; // 10%邊界外。
            foreach (var handler in combat.MonsterSkillHitPublished) await handler(s, target, row, 10, DamageHitResult.Normal);
        });
        Assert.Equal(1, Hits());
        await bot.SendGameAsync(CareerSpecialService.CancelLease, new CancelLeaseCareerSpecialItemReq { CartRemouldCfgId = 1500100 });
        Assert.Equal(0, (await Next(bot, CareerSpecialService.CancelLease, CancelLeaseCareerSpecialItemRsp.Parser)).ErrCode);
        await Loop.InvokeAsync(async () =>
        {
            _app.Services.GetRequiredService<PassiveSkills>().Roll = _ => 0;
            var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), normal)!.Value.Row;
            foreach (var handler in combat.MonsterSkillHitPublished) await handler(s, target, row, 10, DamageHitResult.Normal);
        });
        Assert.Equal(1, Hits());
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task LeasedWolfFocusedArrowTriggersTheRangedFollowUpOncePerAuthoredCooldown(int level)
    {
        await using var bot = await InWorld("WolfPassive");
        await Gm("wolfpassive", "!lvl 99"); await Gm("wolfpassive", "!job 3400");
        await Teach("wolfpassive", 13310); await Teach("wolfpassive", 13305); await Teach("wolfpassive", 13312, level);
        await EquipConditionFixtureAsync("wolfpassive", 2110120);
        await LeaseCompanion(bot, 1500400);
        await HealingStat(bot, 103, 1000);
        var s = Session("wolfpassive");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        var target = await Target("wolfpassive", stationary: true, immobile: true);
        await Loop.InvokeAsync(() => { target.Hp = 50_000_000; target.StaysOutOfBattle = true; return Task.CompletedTask; });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1330501, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1331200 + level);
        Assert.Contains(hit.Dsts, d => d.DstID == target.Uid && d.Damage > 0);
        int Hits() => bot.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 1331200 + level);
        await Loop.InvokeAsync(async () =>
        {
            var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1330501)!.Value.Row;
            foreach (var handler in combat.MonsterSkillHitPublished) await handler(s, target, row, 10, DamageHitResult.Normal);
        });
        Assert.Equal(1, Hits());
        await Task.Delay(2600);
        await Loop.InvokeAsync(async () =>
        {
            var row = SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), 1330501)!.Value.Row;
            foreach (var handler in combat.MonsterSkillHitPublished) await handler(s, target, row, 10, DamageHitResult.Normal);
        });
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1331200 + level);
        Assert.Equal(2, Hits());
        await bot.SendGameAsync(CareerSpecialService.CancelLease, new CancelLeaseCareerSpecialItemReq { CartRemouldCfgId = 1500400 });
        Assert.Equal(0, (await Next(bot, CareerSpecialService.CancelLease, CancelLeaseCareerSpecialItemRsp.Parser)).ErrCode);
    }

    [ClientDataTheory]
    [InlineData(true, 1)]
    [InlineData(true, 5)]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    public async Task LeasedCompanionPassivesAlsoHitOnlyHostilePlayers(bool falcon, int level)
    {
        await using var bot = await InWorld("CompanionPvp");
        await using var defender = await InWorld("CompanionTarget");
        await Gm("companionpvp", "!lvl 99"); await Gm("companionpvp", "!job 3400");
        await Teach("companionpvp", falcon ? 13101 : 13310);
        await Teach("companionpvp", falcon ? 13102 : 13312, level);
        if (!falcon) await Teach("companionpvp", 13305);
        if (!falcon) await EquipConditionFixtureAsync("companionpvp", 2110120);
        var s = Session("companionpvp"); var target = Session("companiontarget");
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(s.SceneEntry, [s, target])));
        foreach (var (client, player) in new[] { (bot, s), (defender, target) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, player, reason: 3)));
            await Next(client, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await client.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(client, MsgId.EntitySelf, EntitySelf.Parser);
            await HealingStat(client, 101, 100_000);
        }
        await LeaseCompanion(bot, falcon ? 1500100 : 1500400);
        await HealingStat(bot, 103, 1000);
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(async () =>
        {
            combat.Rolls = new GuardianStableDamageRolls();
            combat.SetEventPvPHandlers((a, b) => a == s && b == target, (_, _) => Task.CompletedTask);
            _app.Services.GetRequiredService<PassiveSkills>().Roll = _ => 0;
            await Character.SetHpSpAsync(target, 100_000, null);
        });
        int skill = falcon ? _app.Services.GetRequiredService<SkillService>().NormalAttack(s) : 1330501;
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Role!.Id, TargetPos = target.Position.Clone() });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        int followId = (falcon ? 1311000 : 1331200) + level;
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == followId);
        Assert.Contains(hit.Dsts, d => d.DstID == target.Role.Id && d.Damage > 0);
        await Task.Delay(200);
        Assert.Single(bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SkillId == followId);
        Assert.True(target.CurrentHp < 100_000);
    }

    [ClientDataTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RiddenWolfStrikeResetsSwiftWargCooldownAtBothLevels(int level)
    {
        await using var bot = await InWorld("SwiftPassive");
        await Gm("swiftpassive", "!lvl 99"); await Gm("swiftpassive", "!job 3400");
        await Teach("swiftpassive", 13310); await Teach("swiftpassive", 13312); await Teach("swiftpassive", 13326, level);
        await LeaseCompanion(bot, 1500400);
        await bot.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)3 });
        Assert.Equal(0, (await Next(bot, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        var s = Session("swiftpassive");
        var target = await Target("swiftpassive", stationary: true, immobile: true);
        await Loop.InvokeAsync(() =>
        {
            target.Hp = 50_000_000; target.StaysOutOfBattle = true;
            s.SkillCds[13326] = MonsterService.NowMs + 50_000;
            s.SkillModeCds[(13326, 0)] = MonsterService.NowMs + 50_000;
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1331211, TargetId = target.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var cooldown = await Next(bot, CombatService.SkillCdUpdate, SkillCdNotify.Parser, m => m.Infos.Any(i => i.SkillId == 13326));
        Assert.All(cooldown.Infos.Where(i => i.SkillId == 13326), i => Assert.True(i.CdEndTimestampMs <= MonsterService.NowMs));
        await Loop.InvokeAsync(() =>
        {
            Assert.False(s.SkillCds.ContainsKey(13326));
            Assert.False(s.SkillModeCds.ContainsKey((13326, 0)));
            return Task.CompletedTask;
        });
    }
}
