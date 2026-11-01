using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1450201, 0, 2051201, 1200401, 30)]
    [InlineData(1450201, 1, 2051202, 1200401, 35)]
    [InlineData(1450201, 2, 2051203, 1200401, 42)]
    [InlineData(1450201, 3, 2051204, 1200401, 50)]
    [InlineData(1460201, 0, 2051301, 1200401, 42)]
    [InlineData(1460201, 1, 2051302, 1200401, 50)]
    [InlineData(1460201, 2, 2051303, 1200401, 58)]
    [InlineData(1460201, 3, 2051304, 1200401, 60)]
    [InlineData(1450301, 0, 2051601, 1100101, 30)]
    [InlineData(1450301, 1, 2051602, 1100101, 35)]
    [InlineData(1450301, 2, 2051603, 1100101, 42)]
    [InlineData(1450301, 3, 2051604, 1100101, 50)]
    [InlineData(1460301, 0, 2051701, 1100101, 42)]
    [InlineData(1460301, 1, 2051702, 1100101, 50)]
    [InlineData(1460301, 2, 2051703, 1100101, 58)]
    [InlineData(1460301, 3, 2051704, 1100101, 60)]
    public async Task CardReflectionEveryTierReturnsTheConfiguredShareOfARealPvpHit(int card, int tier, int reflectedSkill, int attackSkill, int percent)
    {
        await using var alice = await InWorld("ReflectA");
        await using var bob = await InWorld("ReflectB");
        await Gm("reflecta", attackSkill == 1200401 ? "!job 2100" : "!job 1100");
        await Teach("reflecta", attackSkill / 100);
        var source = Session("reflecta"); var target = Session("reflectb");
        await PrepareReflectionRoom(alice, bob, source, target, card, tier);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = attackSkill, TargetId = target.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var primary = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == attackSkill);
        long dealt = Assert.Single(primary.Dsts, d => d.DstID == target.Role!.Id).Damage;
        var reflected = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == reflectedSkill && h.SrcID == target.Role!.Id);
        long expected = dealt * percent / 100;
        Assert.Equal(expected, Assert.Single(reflected.Dsts, d => d.DstID == source.Role!.Id).Damage);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(100_000 - expected, source.CurrentHp);
            Assert.Equal(100_000 - dealt, target.CurrentHp);
            return Task.CompletedTask;
        });
        var hits = alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).Where(h => h.SkillId == attackSkill || h.SkillId == reflectedSkill).ToArray();
        Assert.Equal(2, hits.Length);
        Assert.Equal(attackSkill, hits[0].SkillId); // 反射不能先於原攻擊畫面。
    }

    private async Task PrepareReflectionRoom(Ro3Emu.Bot.BotClient alice, Ro3Emu.Bot.BotClient bob,
        GameSession source, GameSession target, int card, int tier)
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(source.SceneEntry, [source, target])));
        foreach (var (bot, player) in new[] { (alice, source), (bob, target) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, player, reason: 3)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        }
        IsolateDirectHealing();
        foreach (var bot in new[] { alice, bob })
            foreach (var (id, value) in new[] { (101, 100_000), (103, 1000), (104, 1000), (105, 0), (106, 0), (3, 100) })
                await HealingStat(bot, id, value);
        await Loop.InvokeAsync(async () =>
        {
            combat.Rolls = new StormBoundaryRolls(9999);
            combat.SetEventPvPHandlers((a, b) => a == source && b == target || a == target && b == source, (_, _) => Task.CompletedTask);
            // 此案專驗真卡星階的戰鬥效果；卡片培養／插卡准入已有獨立真請求測試。
            using (await CharacterService.Lock(target))
                target.Items.Add(new InvItem
                {
                    Guid = 899101, Entry = card, Stack = 1, Pack = 3,
                    Grid = CharacterService.CardGrid(card % 1000 / 100, 1), Attrs = new() { [502] = tier }
                });
            using (await CharacterService.Lock(source))
                source.Items.Add(new InvItem
                {
                    Guid = 899102, Entry = 1450201, Stack = 1, Pack = 3,
                    Grid = CharacterService.CardGrid(2, 1), Attrs = new() { [502] = 3 }
                });
            var cards = _app.Services.GetRequiredService<CardEffects>();
            await cards.RefreshAsync(target);
            await cards.RefreshAsync(source); // 兩方都有魔法反射卡，反射不得互相無限連鎖。
            await Character.SetHpSpAsync(source, 100_000, null);
            await Character.SetHpSpAsync(target, 100_000, null);
        });
    }

    [ClientDataFact]
    public async Task CardProcCooldownSurvivesSceneGenerationButResetsWithSelectedRoleEpoch()
    {
        await using var alice = await InWorld("ReflectA");
        await using var bob = await InWorld("ReflectB");
        var source = Session("reflecta"); var target = Session("reflectb");
        await PrepareReflectionRoom(alice, bob, source, target, 1460201, 3);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", 1200401)!.Value;
        await Loop.InvokeAsync(async () =>
        {
            async Task Pulse()
            {
                foreach (var callback in combat.PvpSkillHitPublished)
                    await callback(source, target, row, 1000, DamageHitResult.Normal);
            }
            await Pulse();
            Assert.Equal(99_400, source.CurrentHp);
            // 生命期元件測試：切圖世代不等於重新選角，不能用切圖洗掉卡片共用 CD。
            target.SceneGeneration++;
            await Pulse();
            Assert.Equal(99_400, source.CurrentHp);
            // InitializeSelectedRoleAsync 的既有生命期：重建 SkillProtocol 並清技能 CD。
            target.SkillProtocol = new();
            await Pulse();
            Assert.Equal(98_800, source.CurrentHp);
        });
    }

    [ClientDataFact]
    public async Task CardReflectionRejectsAreaWrongTypeAndZeroDamageAndSharesItsCooldown()
    {
        await using var alice = await InWorld("ReflectA");
        await using var bob = await InWorld("ReflectB");
        var source = Session("reflecta"); var target = Session("reflectb");
        await PrepareReflectionRoom(alice, bob, source, target, 1460201, 3);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        // 獨立驗證已發布命中事件的篩選；上方16案另外走真正施放與PvP傷害。
        Task Pulse(int skill, long damage) => Loop.InvokeAsync(async () =>
        {
            foreach (var callback in combat.PvpSkillHitPublished)
                await callback(source, target, data.Row("SkillConfig", skill)!.Value, damage, DamageHitResult.Normal);
        });
        await Pulse(1100101, 1000);
        await Pulse(1210803, 1000);
        await Pulse(1200401, 0);
        Assert.Equal(100_000, source.CurrentHp);
        await Pulse(1200401, 1000);
        Assert.Equal(99_400, source.CurrentHp);
        await Pulse(1200401, 1000);
        Assert.Equal(99_400, source.CurrentHp);
        await Loop.InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(target);
            target.Items.RemoveAll(i => i.Guid == 899101);
            await _app.Services.GetRequiredService<CardEffects>().RefreshAsync(target);
        });
        await Task.Delay(3100);
        await Pulse(1200401, 1000);
        Assert.Equal(99_400, source.CurrentHp);
        Assert.Single(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 2051304);
    }
}
