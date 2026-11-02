using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task CardOffenceRoom(params (BotClient Bot, GameSession Player)[] members)
    {
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(members[0].Player.SceneEntry,
            [.. members.Select(m => m.Player)])));
        foreach (var (bot, player) in members)
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, player, reason: 3)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        }
        IsolateDirectHealing();
        foreach (var (bot, player) in members)
        {
            foreach (var (id, value) in new[] { (101, 1_000_000), (102, 10000), (103, 10000), (105, 0), (106, 0), (111, 1000) })
                await HealingStat(bot, id, value);
            await Loop.InvokeAsync(() => Character.SetHpSpAsync(player, 1_000_000, 10000));
        }
    }

    private async Task<long> CardOffencePvpWireHit(BotClient bot, GameSession target, int skill)
    {
        await Task.Delay(1100); // 完成前次一秒動作，不竄改技能冷卻或施放狀態。
        long before = target.CurrentHp ?? throw new InvalidOperationException("PvP fixture HP was not initialized.");
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Role!.Id });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.SkillId == skill);
        long damage = Assert.Single(hit.Dsts, d => d.DstID == target.Role.Id).Damage;
        Assert.Equal(damage, before - target.CurrentHp);
        await bot.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq { InterruptSkillId = skill });
        await Next(bot, CombatService.SkillInterrupt, SkillInterruptResp.Parser);
        return damage;
    }

    [ClientDataTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CardCriticalNextDamageAlsoUsesRealPvpHitsAndClearsOnExpiryAndRemoval(int tier)
    {
        await using var alice = await InWorld("OffencePvpA");
        await using var bob = await InWorld("OffencePvpB");
        await Gm("offencepvpa", "!job 1100"); await Teach("offencepvpa", 11001);
        var source = Session("offencepvpa"); var target = Session("offencepvpb");
        await CardOffenceRoom((alice, source), (bob, target));
        var combat = _app.Services.GetRequiredService<CombatService>();
        var rolls = new CardOffenceRolls(); combat.Rolls = rolls;
        await Loop.InvokeAsync(() =>
        {
            combat.SetEventPvPHandlers((a, b) => a == source && b == target, (_, _) => Task.CompletedTask);
            return Task.CompletedTask;
        });
        await CardOffenceEquipAsync(source, 1460103, tier);
        long baseline = await CardOffencePvpWireHit(alice, target, 1100101);
        Assert.True(baseline > 0);
        rolls.Critical = true;
        Assert.True(await CardOffencePvpWireHit(alice, target, 1100101) > baseline);
        Assert.Contains(source.Buffs, b => b.ConfigId == 2091101);
        rolls.Critical = false;
        Assert.Equal(baseline + baseline * 500 / 10000, await CardOffencePvpWireHit(alice, target, 1100101));
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 2091101);
        Assert.Equal(baseline, await CardOffencePvpWireHit(alice, target, 1100101));
        rolls.Critical = true;
        await CardOffencePvpWireHit(alice, target, 1100101);
        rolls.Critical = false;
        await Task.Delay(2100);
        Assert.Equal(baseline, await CardOffencePvpWireHit(alice, target, 1100101));
        rolls.Critical = true;
        await CardOffencePvpWireHit(alice, target, 1100101);
        await CardOffenceEquipAsync(source, 0, 0);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 2091101);
        await CardOffenceEquipAsync(source, 1460103, tier);
        rolls.Critical = false;
        Assert.Equal(baseline, await CardOffencePvpWireHit(alice, target, 1100101));
    }

    [ClientDataTheory]
    [InlineData(1450104, 2050901, 55)]
    [InlineData(1460104, 2051901, 25)]
    public async Task CardSplashSharesFiveTargetsAcrossMonstersAndHostilePlayersOnly(int card, int splashSkill, int percent)
    {
        await using var alice = await InWorld("SplashPvpA");
        await using var bob = await InWorld("SplashPvpB");
        await using var enemy = await InWorld("SplashEnemy");
        await using var friendly = await InWorld("SplashFriend");
        var source = Session("splashpvpa"); var target = Session("splashpvpb");
        var neighbor = Session("splashenemy"); var friend = Session("splashfriend");
        await CardOffenceRoom((alice, source), (bob, target), (enemy, neighbor), (friendly, friend));
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var effects = _app.Services.GetRequiredService<CardEffects>(); effects.Roll = _ => 0;
        await CardOffenceEquipAsync(source, card, 3);
        var monsters = new List<MonsterUnit>();
        for (int i = 0; i < 5; i++) monsters.Add(await Target("splashpvpa", stationary: true));
        foreach (var monster in monsters) await CardOffenceDummyAsync(monster);
        await Loop.InvokeAsync(async () =>
        {
            combat.SetEventPvPHandlers((a, b) => a == source && (b == target || b == neighbor), (_, _) => Task.CompletedTask);
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(source.WorldScene);
            foreach (var other in map.Units.OfType<MonsterUnit>().Except(monsters).ToArray()) await world.RemoveUnitAsync(other);
            source.Position = target.Position.Clone();
            neighbor.Position = target.Position.Clone(); neighbor.Position.X += 1;
            friend.Position = target.Position.Clone(); friend.Position.X += 0.1f;
            for (int i = 0; i < monsters.Count; i++)
            {
                monsters[i].X = target.Position.X + (i == 4 ? 1.5f : 0.2f * (i + 1));
                monsters[i].Y = target.Position.Y; monsters[i].Z = target.Position.Z; map.Moved(monsters[i]);
            }
        });
        long primary = await CardOffencePvpWireHit(alice, target, 1000001);
        long splash = primary * percent / 100;
        Assert.True(splash > 0);
        await Next(enemy, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == splashSkill && h.Dsts.Any(d => d.DstID == neighbor.Role!.Id));
        Assert.Equal(1_000_000 - splash, neighbor.CurrentHp);
        Assert.Equal(1_000_000, friend.CurrentHp);
        Assert.All(monsters.Take(4), m => Assert.Equal(1_000_000 - splash, m.Hp));
        Assert.Equal(1_000_000, monsters[4].Hp);
        var splashHits = alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).Where(h => h.SkillId == splashSkill)
            .SelectMany(h => h.Dsts).ToArray();
        Assert.Equal(5, splashHits.Length);
        Assert.All(splashHits, h => Assert.Equal(splash, h.Damage));
        Assert.DoesNotContain(splashHits, h => h.DstID == target.Role!.Id);
        Assert.Contains(splashHits, h => h.DstID == neighbor.Role!.Id);
        Assert.Equal(1_000_000 - primary, target.CurrentHp); // 原目標不算額外目標，也不吃自己的濺射。
        await Loop.InvokeAsync(() =>
        {
            combat.SetEventPvPHandlers((_, _) => false, (_, _) => Task.CompletedTask);
            var legal = combat.CardSplashTargets(source, target.Position, target.Role!.Id, 5, 5);
            Assert.Empty(legal.Players); // 和平狀態不因近距離被選中。
            return Task.CompletedTask;
        });
    }
}
