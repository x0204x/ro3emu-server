using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private Task OverBrandMarksAsync(GameSession player, bool hesperus, bool gold) => EmblemOwnedAsync(player, async () =>
    {
        player.Items.RemoveAll(i => i.Guid is >= 9871000 and < 9871010);
        var plan = new EmblemService.Plan();
        int slot = 0;
        foreach (var (mark, count, entry) in new[] { (1001, hesperus ? 10 : 0, 1890112), (1002, gold ? 6 : 0, 1890122) })
            for (int left = count; left > 0;)
            {
                int lit = Math.Min(left, 4); left -= lit; slot++;
                var item = new InvItem
                {
                    Guid = 9871000 + slot, Entry = entry, Stack = 1, Pack = 5,
                    Attrs = new() { [1001] = lit * 5, [10001] = 2, [10002] = slot }
                };
                for (int index = 0; index < lit; index++) item.Attrs[1021 + index] = mark;
                player.Items.Add(item); plan.Slots[slot] = item.Guid;
            }
        player.Emblems.Plans[0] = plan; player.Emblems.CurrentPlan = 0;
        await _app.Services.GetRequiredService<EmblemService>().SyncGenrePlanAsync(player, player.EmblemPower);
    });

    [ClientDataTheory]
    [InlineData(1, 3500)]
    [InlineData(5, 5500)]
    public async Task EmblemOverBrandUsesExactModesDamageCooldownAndFourSecondWindow(int level, int baseRatio)
    {
        await using var bot = await InWorld("EchoGold");
        IsolateDirectHealing();
        await Gm("echogold", "!lvl 99"); await Gm("echogold", "!job 1410");
        await Teach("echogold", 11604, level); await Teach("echogold", 11607);
        await HealingStat(bot, 103, 1000); await HealingStat(bot, 102, 10000);
        var source = Session("echogold");
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var target = Assert.Single(await AttackShieldTargets("echogold", 1));
        await Loop.InvokeAsync(() => { target.Template = target.Template with { SpeedCm = 400 }; return Task.CompletedTask; });
        int baseSpeed = target.SpeedCm(MonsterService.NowMs); Assert.True(baseSpeed > 0);
        SkillConfig Effective() => new(combat.EffectiveSkillRow(source, 1160400 + level)!.Value);

        async Task<SkillHitInfo> Cast(int group)
        {
            await Loop.InvokeAsync(async () =>
            {
                await combat.InterruptAsync(source);
                source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear();
                await Character.SetHpSpAsync(source, null, 10000);
            });
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
            { SkillId = group * 100 + (group == 11604 ? level : 1), TargetPos = source.Position.Clone(), Face = source.Face });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, accepted.Result);
            return await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.SessionId == accepted.SessionId && h.Dsts.Any(d => d.DstID == target.Uid && d.Damage > 0));
        }
        async Task Check(int mode, int ratio)
        {
            var row = Effective(); Assert.Equal(mode, row.Mode); Assert.Equal(ratio, row.DamageParam1[0]);
            var authored = SkillConfig.All(data).Single(r => r.SkillId == 11604 && r.Level == level && r.Mode == mode);
            Assert.Equal(authored.Cd, row.Cd); Assert.Equal(authored.CdModeGroup, row.CdModeGroup);
            long hp = target.Hp;
            long expected = (long)Math.Round(Character.Stats(source)[103] * ratio / 10000d
                * (1 + Math.Min(Character.Stats(source)[115], 500) * 30 / 10000d));
            var hit = await Cast(11604);
            Assert.Equal(expected, Assert.Single(hit.Dsts).Damage); Assert.Equal(hp - expected, target.Hp);
            Assert.Equal(mode is 4 or 5 ? 2 : 1, hit.Split);
            if (row.Cd > 0) Assert.True(Math.Max(source.SkillCds.GetValueOrDefault(11604),
                source.SkillModeCds.GetValueOrDefault((11604, mode))) > MonsterService.NowMs);
        }

        await EmblemMarksAsync(source, 1002, 5); await Check(0, baseRatio);
        await EmblemMarksAsync(source, 1002, 6); await Check(2, baseRatio + 400);
        Assert.False(buffs.UnitHasGroup(target, 111652));
        await OverBrandMarksAsync(source, true, true);
        await Cast(11607);
        var boost = Assert.Single(source.Buffs, b => b.ConfigId == 1160101);
        Assert.Equal(4000, boost.DurationMs);
        await Check(5, (baseRatio + 400) * 3 / 2);
        var slow = await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.UnitId == target.Uid && n.Buffs.Any(b => b.ConfigId == 11165201));
        Assert.Equal(3000, Assert.Single(slow.Buffs, b => b.ConfigId == 11165201).Duration);
        Assert.Equal((int)Math.Ceiling(baseSpeed * .8 - 1e-6), target.SpeedCm(MonsterService.NowMs));
        await Task.Delay((int)Math.Max(0, boost.ExpireMs - MonsterService.NowMs) + 150);
        Assert.False(buffs.UnitHasGroup(target, 111652)); Assert.Equal(baseSpeed, target.SpeedCm(MonsterService.NowMs));
        await Check(2, baseRatio + 400);
        await Cast(11607);
        var secondBoost = Assert.Single(source.Buffs, b => b.ConfigId == 1160101);
        long expiry = secondBoost.ExpireMs;
        await OverBrandMarksAsync(source, true, false);
        Assert.Equal(expiry, secondBoost.ExpireMs); await Check(4, baseRatio * 3 / 2);
        Assert.False(buffs.UnitHasGroup(target, 111652));
        await Task.Delay((int)Math.Max(0, expiry - MonsterService.NowMs) + 150);
        await Check(0, baseRatio);
        await OverBrandMarksAsync(source, false, true); Assert.Equal(2, Effective().Mode);
        await Gm("echogold", "!job 1000"); Assert.Equal(0, Effective().Mode);
        Assert.DoesNotContain(111643, source.Progress!.Skills.Keys);
    }

    [ClientDataFact]
    public async Task EmblemOverBrandOnlySlowsHostilePlayersInsideTheAuthoredOverbrandWindow()
    {
        await using var bot = await InWorld("GoldPvp"); await using var foe = await InWorld("GoldFoe");
        IsolateDirectHealing();
        foreach (var name in new[] { "goldpvp", "goldfoe" })
        { await Gm(name, "!lvl 99"); await Gm(name, "!job 1410"); }
        await Teach("goldpvp", 11604, 5); await Teach("goldpvp", 11607);
        var source = Session("goldpvp"); var target = Session("goldfoe");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(source.SceneEntry, [source, target])));
        foreach (var (client, player) in new[] { (bot, source), (foe, target) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, player, reason: 3)));
            await Next(client, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await client.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(client, MsgId.EntitySelf, EntitySelf.Parser);
            await HealingStat(client, 101, 100000); await HealingStat(client, 105, 0);
        }
        await HealingStat(bot, 103, 1000); await HealingStat(bot, 102, 10000);
        await AttackShieldTargets("goldpvp", 0);
        await Loop.InvokeAsync(async () =>
        {
            combat.Rolls = new CardOffenceRolls(); combat.SetEventPvPHandlers((a, b) => a != b, (_, _) => Task.CompletedTask);
            target.Position = source.Position.Clone(); target.Position.X += 1;
            await Character.SetHpSpAsync(source, 100000, 10000); await Character.SetHpSpAsync(target, 100000, null);
        });
        async Task Cast(int group)
        {
            await Loop.InvokeAsync(async () =>
            { await combat.InterruptAsync(source); source.SkillCds.Clear(); source.SkillModeCds.Clear(); source.SkillPublicCds.Clear(); });
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq
            { SkillId = group * 100 + (group == 11604 ? 5 : 1), TargetPos = source.Position.Clone(), Face = source.Face });
            var response = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser); Assert.Equal(0, response.Result);
            await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.SessionId == response.SessionId && h.Dsts.Any(d => d.DstID == target.Role!.Id && d.Damage > 0));
        }
        await OverBrandMarksAsync(source, false, true); await Cast(11604);
        Assert.DoesNotContain(target.Buffs, b => b.ConfigId == 11165201);
        await OverBrandMarksAsync(source, true, true); await Cast(11607);
        await Next(bot, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.UnitId == source.Role!.Id && n.Buffs.Any(b => b.ConfigId == 1160101));
        var boost = Assert.Single(source.Buffs, b => b.ConfigId == 1160101); Assert.Equal(4000, boost.DurationMs);
        long speed = target.Speed; long hp = target.CurrentHp ?? throw new InvalidOperationException("PvP target HP was not initialized.");
        long expected = (long)Math.Round(1000 * .885 * (1 + Math.Min(Character.Stats(source)[115], 500) * .003));
        await Cast(11604);
        await Next(foe, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.UnitId == target.Role!.Id && n.Buffs.Any(b => b.ConfigId == 11165201));
        Assert.Equal(expected, hp - target.CurrentHp);
        var slow = Assert.Single(target.Buffs, b => b.ConfigId == 11165201);
        Assert.Equal(3000, slow.DurationMs); Assert.Equal(1, slow.PileCount);
        Assert.Equal((long)Math.Ceiling(speed * .8 - 1e-6), target.Speed);
        await Task.Delay((int)Math.Max(0, boost.ExpireMs - MonsterService.NowMs) + 150);
        Assert.DoesNotContain(target.Buffs, b => b.ConfigId == 11165201); Assert.Equal(speed, target.Speed);
        await Cast(11604); Assert.DoesNotContain(target.Buffs, b => b.ConfigId == 11165201);
    }
}
