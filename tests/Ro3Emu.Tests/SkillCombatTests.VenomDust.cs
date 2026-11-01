using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1, 565)]
    [InlineData(10, 790)]
    public async Task VenomDustUsesTheAimedThreeMetreCircleAndFiveTargetCap(int level, long expected)
    {
        await using var bot = await InWorld("VenomArea");
        await Gm("venomarea", "!lvl 99"); await Gm("venomarea", "!job 5200");
        await Teach("venomarea", 15106, level); await HealingStat(bot, 103, 1000);
        var source = Session("venomarea");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var targets = new List<MonsterUnit>();
        for (int i = 0; i < 8; i++)
        { var monster = await Target("venomarea", stationary: true, immobile: true); await CardOffenceDummyAsync(monster); targets.Add(monster); }
        var center = source.Position.Clone(); center.X += 6;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            combat.Rolls = new CardOffenceRolls();
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(source.WorldScene);
            foreach (var ambient in map.Units.OfType<MonsterUnit>().Where(m => !targets.Contains(m)).ToArray())
            { ambient.Hp = 0; ambient.Dead = true; await world.RemoveUnitAsync(ambient); }
            for (int i = 0; i < targets.Count; i++)
            {
                var monster = targets[i]; monster.Y = center.Y; monster.Z = center.Z;
                monster.X = i == 7 ? source.Position.X + 1 : center.X + (i < 5 ? i * .5f : i == 5 ? 2.9f : 3.2f);
                map.Moved(monster);
            }
            combat.SkillCastCompleted.Add((s, _, _) =>
            { if (s == source) complete.TrySetResult(); return Task.CompletedTask; });
        });
        // 第五名距施放者8m，仍在目標點2m內；自身附近的怪物反而不在毒霧圓內。
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1510600 + level, TargetId = targets[0].Uid, TargetPos = center });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await complete.Task.WaitAsync(Wait);
        for (int i = 0; i < targets.Count; i++) Assert.Equal(i < 5 ? expected : 0, 1_000_000 - targets[i].Hp);
        // LogicData指向Skill15116，不是同號短劍精通Buff；本輪不製造未證實的中毒週期。
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId / 100 == 15116);
    }

    [ClientDataFact]
    public async Task VenomDustSharesFiveTargetsAcrossHostilePlayersAndMonstersAtTheAimPoint()
    {
        await using var bot = await InWorld("VenomMixed");
        await using var first = await InWorld("VenomFoeA");
        await using var second = await InWorld("VenomFoeB");
        await using var outside = await InWorld("VenomOutside");
        foreach (string name in new[] { "venommixed", "venomfoea", "venomfoeb", "venomoutside" })
        { await Gm(name, "!lvl 99"); await Gm(name, "!job 5200"); }
        await Teach("venommixed", 15106); await HealingStat(bot, 103, 1000);
        var source = Session("venommixed");
        var enemies = new[] { Session("venomfoea"), Session("venomfoeb"), Session("venomoutside") };
        var combat = _app.Services.GetRequiredService<CombatService>();
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(source.SceneEntry, [source, .. enemies])));
        foreach (var (client, player) in new[] { (bot, source), (first, enemies[0]), (second, enemies[1]), (outside, enemies[2]) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, player, reason: 3)));
            await Next(client, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await client.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(client, MsgId.EntitySelf, EntitySelf.Parser);
            await HealingStat(client, 101, 100_000); await HealingStat(client, 105, 0);
        }
        var monsters = new List<MonsterUnit>();
        for (int i = 0; i < 4; i++)
        { var monster = await Target("venommixed", stationary: true, immobile: true); await CardOffenceDummyAsync(monster); monsters.Add(monster); }
        var center = source.Position.Clone(); center.X += 6;
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            combat.Rolls = new CardOffenceRolls(); combat.SetEventPvPHandlers((a, b) => a != b, (_, _) => Task.CompletedTask);
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(source.WorldScene);
            foreach (var ambient in map.Units.OfType<MonsterUnit>().Where(m => !monsters.Contains(m)).ToArray())
            { ambient.Hp = 0; ambient.Dead = true; await world.RemoveUnitAsync(ambient); }
            for (int i = 0; i < monsters.Count; i++)
            {
                monsters[i].X = center.X + (i < 3 ? i * .6f : 2.8f);
                monsters[i].Y = center.Y; monsters[i].Z = center.Z; map.Moved(monsters[i]);
            }
            for (int i = 0; i < enemies.Length; i++)
            {
                enemies[i].Position = center.Clone(); enemies[i].Position.X += i == 0 ? .3f : i == 1 ? .9f : 3.3f;
                await Character.SetHpSpAsync(enemies[i], 100_000, null);
            }
            combat.SkillCastCompleted.Add((s, _, _) =>
            { if (s == source) complete.TrySetResult(); return Task.CompletedTask; });
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
        { SkillId = 1510601, TargetId = monsters[0].Uid, TargetPos = center });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await complete.Task.WaitAsync(Wait);
        for (int i = 0; i < monsters.Count; i++) Assert.Equal(i < 3 ? 565 : 0, 1_000_000 - monsters[i].Hp);
        Assert.Equal(565, 100_000 - Character.Stats(enemies[0])[2000]);
        Assert.Equal(565, 100_000 - Character.Stats(enemies[1])[2000]);
        Assert.Equal(100_000, Character.Stats(enemies[2])[2000]);
    }
}
