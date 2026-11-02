using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private sealed class CardOffenceRolls : CombatRolls
    {
        public bool Critical;
        public int ProcValue = 9999;
        public override double Hit() => 0;
        public override double Crit() => Critical ? 0 : 1;
        public override double DamageFloat() => 0.5;
        public override int Proc(int max) => Math.Min(ProcValue, max - 1);
    }

    /// <summary>卡片效果測試先建立同部位的實際裝備；不可再依靠舊版空裝備格也生效的行為。
    /// 僅供已在遊戲迴圈內的隔離戰鬥 fixture，真實插卡／穿戴請求另由 CardRulesTests 驗證。</summary>
    private void EnsureCardFixtureGear(GameSession player, int grid)
    {
        if (player.Items.Any(i => i.Grid == grid)) return;
        var data = _app.Services.GetRequiredService<GameData>();
        int part = grid switch { 1 => 2, 2 => 3, 3 => 4, 4 => 5, 5 => 6, 6 or 7 => 7, _ => grid };
        var gear = data.Table("ItemConfig").Values.Select(row => new ItemConfig(row))
            .Where(item => (grid == 1 ? item.EquipPart is 1 or 2 : item.EquipPart == part) && item.LevelNeed <= 1
                && (item.JobNeed.Length == 0 || item.JobNeed.Contains(0) || item.JobNeed.Contains(player.Progress!.Career)))
            .OrderBy(item => item.Id).First();
        player.Items.Add(new InvItem { Guid = 9850000 + grid, Entry = (int)gear.Id, Stack = 1, Pack = 2, Grid = grid });
    }

    private async Task CardOffenceEquipAsync(GameSession player, int entry, int tier)
    {
        await Loop.InvokeAsync(async () =>
        {
            player.Items.RemoveAll(i => i.Guid == 9930001);
            if (entry != 0) EnsureCardFixtureGear(player, 1);
            if (entry != 0) player.Items.Add(new InvItem
            {
                Guid = 9930001, Entry = entry, Stack = 1, Pack = 3,
                Grid = CharacterService.CardGrid(1, 1), Attrs = new() { [502] = tier },
            });
            foreach (var handler in _app.Services.GetRequiredService<CardService>().SocketsChanged) await handler(player);
        });
    }

    private Task CardOffenceDummyAsync(MonsterUnit target) => Loop.InvokeAsync(() =>
    {
        target.Template = target.Template with { MaxHp = 1_000_000, Def = 0, Mdef = 0, Element = 1, Skills = null };
        target.Hp = 1_000_000; target.StaysOutOfBattle = true; target.KeepsDamage = true;
        return Task.CompletedTask;
    });

    private async Task<long> CardOffenceHitAsync(Ro3Emu.Bot.BotClient bot, GameSession player, MonsterUnit target, JsonElement row)
    {
        long before = target.Hp;
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<CombatService>().CastExtraAsync(player, row, [target]));
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == GameData.Int(row, "_iID") && h.Dsts.Any(d => d.DstID == target.Uid));
        long amount = hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
        Assert.Equal(amount, before - target.Hp);
        return amount;
    }

    [ClientDataTheory]
    [InlineData(1430108, 5000098, 5020004, 1000, 180, 320, 460, 600)]
    [InlineData(1440111, 5000160, 5000001, 1001, 360, 540, 720, 900)]
    [InlineData(1430124, 5000141, 5000144, 1004, 180, 320, 460, 600)]
    [InlineData(1430125, 5000144, 5000145, 1005, 180, 320, 460, 600)]
    [InlineData(1430126, 5000145, 5000141, 1003, 180, 320, 460, 600)]
    [InlineData(1450105, 5020007, 5000098, 1002, 500, 500, 500, 500)]
    [InlineData(1460105, 5000105, 5000160, 1002, 1000, 1000, 1000, 1000)]
    public async Task CardFamilyDamageUsesAuthoredNpcTagsAndEveryActualStar(int card, int targetId, int excludedId,
        int tag, int tier0, int tier1, int tier2, int tier3)
    {
        await using var bot = await InWorld("Cardfamily");
        var player = Session("cardfamily");
        var data = _app.Services.GetRequiredService<GameData>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CardOffenceRolls();
        var row = SkillConfig.Get(data, 1100101)!.Value.AsDescribedHit(0, 10000, 1);
        var target = await Target("cardfamily", stationary: true, npcId: targetId);
        var excluded = await Target("cardfamily", stationary: true, npcId: excludedId);
        await CardOffenceDummyAsync(target); await CardOffenceDummyAsync(excluded);
        Assert.Contains(tag, data.Row("NPCConfig", targetId)!.Value.GetProperty("_kNpcTag").EnumerateArray().Select(v => v.GetInt32()));
        var expected = new[] { tier0, tier1, tier2, tier3 };
        for (int tier = 0; tier < 4; tier++)
        {
            await CardOffenceEquipAsync(player, card, tier);
            Assert.Equal(10000 + expected[tier], await CardOffenceHitAsync(bot, player, target, row));
            Assert.Equal(10000, await CardOffenceHitAsync(bot, player, excluded, row));
        }
        if (tag == 1000)
        {
            var chieftain = await Target("cardfamily", stationary: true, npcId: 5020006);
            await CardOffenceDummyAsync(chieftain);
            Assert.Equal(10000, await CardOffenceHitAsync(bot, player, chieftain, row));
        }
        await CardOffenceEquipAsync(player, 0, 0);
        Assert.Equal(10000, await CardOffenceHitAsync(bot, player, target, row));
    }

    [ClientDataTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CardCriticalArmsOnlyTheNextDamageAndExpiresOrClearsOnRemoval(int tier)
    {
        await using var bot = await InWorld("Cardcrit");
        var player = Session("cardcrit");
        var data = _app.Services.GetRequiredService<GameData>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var rolls = new CardOffenceRolls(); combat.Rolls = rolls;
        await bot.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq
        { GmType = (GmOpEnum)33, ParamString = $"{bot.Self!.Uuid},111,1000" });
        Assert.Equal(0, (await Next(bot, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser)).Result);
        Assert.Equal(1000, Character.Stats(player)[111]);
        await CardOffenceEquipAsync(player, 1460103, tier);
        Assert.Equal(tier < 2 ? 1400 : 1700, Character.Stats(player)[111]); // 真卡另加 400/700，不能用100%基底再假設 roll=1 不暴擊。
        var target = await Target("cardcrit", stationary: true);
        await CardOffenceDummyAsync(target);
        var row = SkillConfig.Get(data, 1100101)!.Value.AsDescribedHit(0, 10000, 1);
        rolls.Critical = true;
        Assert.Equal(15000, await CardOffenceHitAsync(bot, player, target, row));
        Assert.Contains(player.Buffs, b => b.ConfigId == 2091101);
        rolls.Critical = false;
        Assert.Equal(10500, await CardOffenceHitAsync(bot, player, target, row));
        Assert.DoesNotContain(player.Buffs, b => b.ConfigId == 2091101);
        Assert.Equal(10000, await CardOffenceHitAsync(bot, player, target, row));
        rolls.Critical = true;
        Assert.Equal(15000, await CardOffenceHitAsync(bot, player, target, row));
        rolls.Critical = false;
        await Task.Delay(2100);
        Assert.Equal(10000, await CardOffenceHitAsync(bot, player, target, row));
        rolls.Critical = true;
        Assert.Equal(15000, await CardOffenceHitAsync(bot, player, target, row));
        await CardOffenceEquipAsync(player, 0, 0);
        Assert.DoesNotContain(player.Buffs, b => b.ConfigId == 2091101);
        await CardOffenceEquipAsync(player, 1460103, tier);
        rolls.Critical = false;
        Assert.Equal(10000, await CardOffenceHitAsync(bot, player, target, row));
    }

    [ClientDataTheory]
    [InlineData(1450104, 0, 2050801, 2500, 3, 50, 3000)]
    [InlineData(1450104, 1, 2050801, 4000, 3, 50, 3000)]
    [InlineData(1450104, 2, 2050801, 4000, 3, 50, 2500)]
    [InlineData(1450104, 3, 2050901, 4000, 3, 55, 2500)]
    [InlineData(1460104, 0, 2051001, 10000, 3, 15, 200)]
    [InlineData(1460104, 1, 2051101, 10000, 3, 20, 200)]
    [InlineData(1460104, 2, 2051801, 10000, 3, 25, 200)]
    [InlineData(1460104, 3, 2051901, 10000, 5, 25, 200)]
    public async Task CardSplashUsesEveryStarChanceRangeFiveExtraTargetsAndCooldown(int card, int tier,
        int splashId, int chance, int radius, int percent, int cooldown)
    {
        await using var bot = await InWorld("Cardsplash");
        var player = Session("cardsplash");
        var data = _app.Services.GetRequiredService<GameData>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new CardOffenceRolls();
        var effects = _app.Services.GetRequiredService<CardEffects>();
        await CardOffenceEquipAsync(player, card, tier);
        var primary = await Target("cardsplash", stationary: true);
        var extras = new List<MonsterUnit>();
        for (int i = 0; i < 7; i++) extras.Add(await Target("cardsplash", stationary: true));
        foreach (var target in extras.Prepend(primary)) await CardOffenceDummyAsync(target);
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(player.WorldScene);
            foreach (var other in map.Units.OfType<MonsterUnit>().Except(extras).Where(m => m != primary).ToArray())
                await world.RemoveUnitAsync(other);
            for (int i = 0; i < extras.Count; i++)
            {
                extras[i].X = primary.X + (i == 6 ? radius + 0.2f : 0.2f * (i + 1));
                extras[i].Z = primary.Z; extras[i].Y = primary.Y; map.Moved(extras[i]);
            }
        });
        var normal = SkillConfig.Get(data, 1000001)!.Value.WithDamage([0], [10000]);
        var ranged = JsonNode.Parse(normal.GetRawText())!.AsObject(); ranged["_iDistanceType"] = 2;
        effects.Roll = _ => 0;
        await CardOffenceHitAsync(bot, player, primary, JsonSerializer.SerializeToElement(ranged));
        Assert.All(extras, m => Assert.Equal(1_000_000, m.Hp));
        var special = SkillConfig.Get(data, 1100101)!.Value.AsDescribedHit(0, 10000, 1);
        await CardOffenceHitAsync(bot, player, primary, special);
        Assert.All(extras, m => Assert.Equal(1_000_000, m.Hp));
        if (chance < 10000)
        {
            effects.Roll = _ => chance;
            await CardOffenceHitAsync(bot, player, primary, normal);
            Assert.All(extras, m => Assert.Equal(1_000_000, m.Hp));
        }
        effects.Roll = _ => chance - 1;
        Assert.Equal(10000, await CardOffenceHitAsync(bot, player, primary, normal));
        var splash = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == splashId);
        Assert.Equal(5, splash.Dsts.Count);
        Assert.All(splash.Dsts, d => Assert.Equal(100 * percent, d.Damage));
        Assert.DoesNotContain(splash.Dsts, d => d.DstID == primary.Uid);
        Assert.All(extras.Take(5), m => Assert.Equal(1_000_000 - 100 * percent, m.Hp));
        Assert.All(extras.Skip(5), m => Assert.Equal(1_000_000, m.Hp));
        Assert.Equal(10000, await CardOffenceHitAsync(bot, player, primary, normal));
        Assert.All(extras.Take(5), m => Assert.Equal(1_000_000 - 100 * percent, m.Hp));
        await Task.Delay(cooldown + 100);
        Assert.Equal(10000, await CardOffenceHitAsync(bot, player, primary, normal));
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == splashId);
        Assert.All(extras.Take(5), m => Assert.Equal(1_000_000 - 200 * percent, m.Hp));
        await CardOffenceEquipAsync(player, 0, 0);
        await Task.Delay(cooldown + 100);
        Assert.Equal(10000, await CardOffenceHitAsync(bot, player, primary, normal));
        Assert.All(extras.Take(5), m => Assert.Equal(1_000_000 - 200 * percent, m.Hp));
    }

    [ClientDataTheory]
    [InlineData(1450401, 0, false)]
    [InlineData(1450401, 1, false)]
    [InlineData(1450401, 2, false)]
    [InlineData(1450401, 3, false)]
    [InlineData(1460401, 0, true)]
    [InlineData(1460401, 1, true)]
    [InlineData(1460401, 2, true)]
    [InlineData(1460401, 3, true)]
    public async Task CardSightlessMindHasAuthoredGuaranteedStunAndBlindNotInventedBleeding(int card, int tier, bool guaranteed)
    {
        await using var bot = await InWorld("Cardsight");
        var player = Session("cardsight");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var rolls = new CardOffenceRolls { ProcValue = 2000 }; combat.Rolls = rolls;
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await CardOffenceEquipAsync(player, card, tier);
        Assert.Equal(1, player.Progress!.GrantedSkills[20040]);
        Assert.Equal(1, player.Progress.GrantedSkills[20045]);
        var row = combat.EffectiveSkillRow(player, 2004501)!.Value;
        Assert.Equal(guaranteed ? 10000 : 2000, row.GetProperty("_kBuff2")[1][1].GetInt32());
        Assert.Equal(902501, row.GetProperty("_kBuff2")[0][0].GetInt32());
        var target = await Target("cardsight", stationary: true); await CardOffenceDummyAsync(target);
        Assert.True(await CardOffenceHitAsync(bot, player, target, row) > 0);
        Assert.Equal(guaranteed, buffs.UnitBuffs(target).Any(b => b.ConfigId == 901601));
        Assert.DoesNotContain(buffs.UnitBuffs(target), b => b.ConfigId == 902501);
        var other = await Target("cardsight", stationary: true); await CardOffenceDummyAsync(other);
        rolls.ProcValue = 1999;
        Assert.True(await CardOffenceHitAsync(bot, player, other, row) > 0);
        Assert.Contains(buffs.UnitBuffs(other), b => b.ConfigId == 901601);
        Assert.Contains(buffs.UnitBuffs(other), b => b.ConfigId == 902501);
        await CardOffenceEquipAsync(player, 0, 0);
        Assert.DoesNotContain(20040, player.Progress.GrantedSkills.Keys);
        Assert.DoesNotContain(20045, player.Progress.GrantedSkills.Keys);
    }

    [ClientDataFact]
    public async Task CardAutocastCannotChainIntoSplashEvenWhenItsRowIsANormalAttack()
    {
        await using var bot = await InWorld("Cardchain");
        var player = Session("cardchain");
        var data = _app.Services.GetRequiredService<GameData>();
        var combat = _app.Services.GetRequiredService<CombatService>(); combat.Rolls = new CardOffenceRolls();
        var effects = _app.Services.GetRequiredService<CardEffects>(); effects.Roll = _ => 0;
        await CardOffenceEquipAsync(player, 1450104, 0);
        var doubleCard = data.Table("CardConfig").Values.First(r => GameData.Int(r, "_iTier") == 0
            && EquipmentStunts.Ids(r, "_kSkillAttribute").Any(id =>
                EquipmentAffixes.Rows(data.Row("SpecialEffectConfig", id)!.Value, "_kSpecialEffect")
                    .Any(op => op is [2, 2021001])));
        await Loop.InvokeAsync(async () =>
        {
            player.Items.Add(new InvItem
            {
                Guid = 9930002, Entry = (int)GameData.Int(doubleCard, "_iCardID"),
                Stack = 1, Pack = 3, Grid = CharacterService.CardGrid(1, 2)
            });
            foreach (var handler in _app.Services.GetRequiredService<CardService>().SocketsChanged) await handler(player);
        });
        var target = await Target("cardchain", stationary: true);
        var neighbor = await Target("cardchain", stationary: true);
        await CardOffenceDummyAsync(target); await CardOffenceDummyAsync(neighbor);
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(player.WorldScene);
            foreach (var other in map.Units.OfType<MonsterUnit>().Where(m => m != target && m != neighbor).ToArray())
                await world.RemoveUnitAsync(other);
            neighbor.X = target.X + 0.5f; neighbor.Z = target.Z; neighbor.Y = target.Y;
            map.Moved(neighbor);
        });
        var normal = SkillConfig.Get(data, 1000001)!.Value.WithDamage([0], [10000]);
        await Loop.InvokeAsync(() => effects.OnDealtAsync(player, target, normal, 10000));
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2003001);
        Assert.True(target.Hp < 1_000_000);
        Assert.Equal(1_000_000, neighbor.Hp); // 追加二刀連擊不能再啟動巴風特濺射。
        await CardOffenceHitAsync(bot, player, target, normal);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2050801);
        Assert.Equal(995000, neighbor.Hp); // 自己的普通命中仍可正常觸發。
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CardOffenceStopsAcrossSceneEpochChangesInsideBuffPublication(bool duringRemoval)
    {
        await using var bot = await InWorld("CardEpoch");
        var player = Session("cardepoch");
        var data = _app.Services.GetRequiredService<GameData>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var rolls = new CardOffenceRolls { Critical = true }; combat.Rolls = rolls;
        var effects = _app.Services.GetRequiredService<CardEffects>(); effects.Roll = _ => 0;
        await CardOffenceEquipAsync(player, 1460103, 0);
        var target = await Target("cardepoch", stationary: true);
        var neighbor = await Target("cardepoch", stationary: true);
        await CardOffenceDummyAsync(target); await CardOffenceDummyAsync(neighbor);
        await Loop.InvokeAsync(async () =>
        {
            var world = _app.Services.GetRequiredService<World>(); var map = world.Map(player.WorldScene);
            foreach (var other in map.Units.OfType<MonsterUnit>().Where(m => m != target && m != neighbor).ToArray())
                await world.RemoveUnitAsync(other);
            neighbor.X = target.X + 0.5f; neighbor.Y = target.Y; neighbor.Z = target.Z; map.Moved(neighbor);
        });
        var normal = SkillConfig.Get(data, 1000001)!.Value.WithDamage([0], [10000]);
        if (duringRemoval) await CardOffenceHitAsync(bot, player, target, normal);
        await Loop.InvokeAsync(async () =>
        {
            player.Items.Add(new InvItem
            {
                Guid = 9930002, Entry = 1450104, Stack = 1,
                Pack = 3, Grid = CharacterService.CardGrid(1, 2)
            });
            foreach (var handler in _app.Services.GetRequiredService<CardService>().SocketsChanged) await handler(player);
        });
        bool changed = false;
        long generation = player.SceneGeneration;
        _app.Services.GetRequiredService<BuffService>().Changed.Add(session =>
        {
            if (session == player && !changed && session.Buffs.Any(b => b.ConfigId == 2091101) != duringRemoval)
            {
                changed = true;
                session.SceneGeneration++; // 可控制的生命期失效點：就在 Add/Remove 的 Changed await 邊界，沒有 sleep 競賽。
            }
            return Task.CompletedTask;
        });
        Assert.Equal(duringRemoval ? 15750 : 15000, await CardOffenceHitAsync(bot, player, target, normal));
        Assert.True(changed);
        Assert.Equal(generation + 1, player.SceneGeneration);
        Assert.Equal(1_000_000, neighbor.Hp); // 舊命中的 await 不能在新 epoch 重新授權濺射。
        rolls.Critical = false;
        Assert.Equal(10000, await CardOffenceHitAsync(bot, player, target, normal));
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2050801);
        Assert.Equal(995000, neighbor.Hp); // 新 epoch 的新命中仍可運作；舊暴擊不留下下一擊加成。
    }
}
