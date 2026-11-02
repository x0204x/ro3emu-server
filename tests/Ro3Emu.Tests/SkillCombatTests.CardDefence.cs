using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private async Task EquipDefenceCard(GameSession player, int group, int tier = 0)
    {
        var data = _app.Services.GetRequiredService<GameData>();
        var card = data.Table("CardConfig").Values.First(r => GameData.Int(r, "_iTier") == tier
            && EquipmentStunts.Ids(r, "_kSkillAttribute").SelectMany(id =>
                EquipmentAffixes.Rows(data.Row("SpecialEffectConfig", id)!.Value, "_kSpecialEffect"))
                .Any(op => op is [2, var id] && BuffConfig.Get(data, id)?.BuffId == group));
        EnsureCardFixtureGear(player, 1);
        player.Items.RemoveAll(i => i.Guid == 889901);
        player.Items.Add(new InvItem
        {
            Guid = 889901, Entry = (int)GameData.Int(card, "_iCardID"), Stack = 1,
            Pack = 3, Grid = CharacterService.CardGrid(1, 1), Attrs = new() { [502] = tier }
        });
        await _app.Services.GetRequiredService<CardEffects>().RefreshAsync(player);
    }

    private static JsonElement DefenceAttack(int id, int type = 2, int element = 1) => JsonSerializer.SerializeToElement(new
    {
        _iID = id, _iSkillHit = 10000, _iDmgType = type, _iIsNormalAtk = 0, _iEleType = element,
        _kDamageParam1 = DefenceZeroCoefficient, _kDamageParam2 = DefenceSmallFlatDamage,
    });

    [ClientDataTheory]
    [InlineData(20090, 3)]
    [InlineData(20100, 5)]
    [InlineData(20110, 2)]
    [InlineData(20120, 6)]
    [InlineData(20130, 4)]
    [InlineData(20140, 9)]
    [InlineData(20515, 7)]
    public async Task CardArmourElementChangesRealIncomingDamageAndPublishedElement(int group, int element)
    {
        await using var alice = await InWorld("Cardarmour");
        var player = Session("cardarmour");
        var enemy = await Target("cardarmour", stationary: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        combat.Rolls = new GuardianStableDamageRolls();
        long neutral = 0, changed = 0;
        await Loop.InvokeAsync(async () =>
        {
            enemy.StaysOutOfBattle = true;
            enemy.Template = enemy.Template with { Element = 1 };
            // 固定 10 點的獨立攻擊，原卡與原 EleConfig 提供待驗的轉屬性及倍率。
            await Character.SetHpSpAsync(player, long.MaxValue, null);
            long before = Character.Stats(player)[2000];
            await combat.MonsterSkillHitAsync(enemy, DefenceAttack(998881, element: 4), [player], enemy.Uid);
            neutral = before - Character.Stats(player)[2000];
            await EquipDefenceCard(player, group);
            Assert.Equal(element, combat.DefenceElement(player));
            await Character.SetHpSpAsync(player, long.MaxValue, null);
            before = Character.Stats(player)[2000];
            await combat.MonsterSkillHitAsync(enemy, DefenceAttack(998882, element: 4), [player], enemy.Uid);
            changed = before - Character.Stats(player)[2000];
            // 卡片本身可能加魔防，使用實際魔防獨立核對減傷及元素倍率。
            long mdef = Character.Stats(player)[AttributeId.Mdef];
            double reduction = 1 - mdef / (mdef + 20d * player.Progress!.Level);
            long expected = (long)Math.Round(10 * reduction * Math.Clamp(1 + GameData.Int(data.Row("EleConfig", element)!.Value, "_iFire") / 10000d, .1, 10));
            Assert.Equal(Math.Max(1, expected), changed);
            Assert.True(neutral > 0);
            player.Items.RemoveAll(i => i.Guid == 889901);
            await _app.Services.GetRequiredService<CardEffects>().RefreshAsync(player);
            Assert.Equal(1, combat.DefenceElement(player));
        });
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 998882);
        Assert.Equal(changed, Assert.Single(hit.Dsts).Damage);
        Assert.Equal(element, Assert.Single(hit.Dsts).DstElement);
    }

    [ClientDataTheory]
    [InlineData(20510, 0)]
    [InlineData(20510, 3)]
    [InlineData(20511, 0)]
    [InlineData(20511, 1)]
    [InlineData(20511, 2)]
    [InlineData(20511, 3)]
    public async Task CardMagicNullificationChecksChanceTypeAndSharedCooldownOnActualHits(int group, int tier)
    {
        await using var alice = await InWorld("Cardnull");
        var player = Session("cardnull");
        var enemy = await Target("cardnull", stationary: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var effects = _app.Services.GetRequiredService<CardEffects>();
        var data = _app.Services.GetRequiredService<GameData>();
        combat.Rolls = new GuardianStableDamageRolls();
        await Loop.InvokeAsync(async () =>
        {
            enemy.StaysOutOfBattle = true;
            await EquipDefenceCard(player, group, tier);
            long chance = player.Buffs.Select(b => BuffConfig.Get(data, b.ConfigId))
                .Where(b => b?.BuffId == group).Max(b => b!.Value.LogicData[0]);
            async Task<long> Hit(int id, int type)
            {
                await Character.SetHpSpAsync(player, long.MaxValue, null);
                long before = Character.Stats(player)[2000];
                await combat.MonsterSkillHitAsync(enemy, DefenceAttack(id, type), [player], enemy.Uid);
                return before - Character.Stats(player)[2000];
            }
            effects.Roll = _ => (int)chance;
            Assert.True(await Hit(998883, 2) > 0);
            effects.Roll = _ => (int)chance - 1;
            Assert.True(await Hit(998884, 1) > 0);
            Assert.Equal(0, await Hit(998885, 2));
            Assert.True(await Hit(998886, 2) > 0);
            player.Items.RemoveAll(i => i.Guid == 889901);
            await effects.RefreshAsync(player);
            Assert.True(await Hit(998887, 2) > 0);
        });
        Assert.Equal(0, Assert.Single((await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 998885)).Dsts).Damage);
    }

    [ClientDataFact]
    public async Task CardSilenceReductionChangesDamageOnlyWhileSilenced()
    {
        await using var alice = await InWorld("Cardsilent");
        var player = Session("cardsilent");
        var enemy = await Target("cardsilent", stationary: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        combat.Rolls = new GuardianStableDamageRolls();
        await Loop.InvokeAsync(async () =>
        {
            enemy.StaysOutOfBattle = true;
            await EquipDefenceCard(player, 20260);
            async Task<long> Hit(int id)
            {
                await Character.SetHpSpAsync(player, long.MaxValue, null);
                long before = Character.Stats(player)[2000];
                await combat.MonsterSkillHitAsync(enemy, DefenceAttack(id), [player], enemy.Uid);
                return before - Character.Stats(player)[2000];
            }
            long baseline = await Hit(998888);
            await buffs.AddAsync(player, 902301, 10000, enemy.Uid);
            Assert.True(buffs.HasForbidden(player, ForbiddenState.CastSkill));
            Assert.Equal((long)Math.Floor(baseline * .97), await Hit(998889));
            await buffs.RemoveAsync(player, Assert.Single(player.Buffs, b => b.ConfigId == 902301));
            Assert.Equal(baseline, await Hit(998890));
        });
    }

    [ClientDataTheory]
    [InlineData(20525, 1211101)]
    [InlineData(20527, 901601)]
    [InlineData(20523, 901701)]
    [InlineData(20523, 901801)]
    public async Task CardControlImmunityPreventsRealMovementOrCastRestrictionAndUnsocketRestoresIt(int group, int control)
    {
        await using var alice = await InWorld("Cardimmune");
        var player = Session("cardimmune");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(async () =>
        {
            await EquipDefenceCard(player, group);
            long speed = Character.Stats(player)[AttributeId.Speed];
            await buffs.AddAsync(player, control, 10000, 99999);
            Assert.DoesNotContain(player.Buffs, b => b.ConfigId == control);
            Assert.Equal(speed, Character.Stats(player)[AttributeId.Speed]);
            Assert.False(buffs.ActionBlocked(player, ForbiddenState.CastSkill));
            player.Items.RemoveAll(i => i.Guid == 889901);
            await _app.Services.GetRequiredService<CardEffects>().RefreshAsync(player);
            await buffs.AddAsync(player, control, 10000, 99999);
            Assert.Contains(player.Buffs, b => b.ConfigId == control);
            if (control == 1211101) Assert.True(Character.Stats(player)[AttributeId.Speed] < speed);
            if (control is 901601 or 901801) Assert.True(buffs.ActionBlocked(player, ForbiddenState.CastSkill));
        });
    }
    [ClientDataTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CardHolyArmourRequiresDamageAndChanceThenExpiresWithoutRefreshingDuringCooldown(int tier)
    {
        await using var alice = await InWorld("Cardholy");
        var player = Session("cardholy");
        var enemy = await Target("cardholy", stationary: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var effects = _app.Services.GetRequiredService<CardEffects>();
        var data = _app.Services.GetRequiredService<GameData>();
        combat.Rolls = new GuardianStableDamageRolls();
        int duration = 0;
        await Loop.InvokeAsync(async () =>
        {
            enemy.StaysOutOfBattle = true;
            await EquipDefenceCard(player, 20514, tier);
            var trigger = player.Buffs.Select(b => BuffConfig.Get(data, b.ConfigId)).Single(b => b?.BuffId == 20514)!.Value;
            duration = (int)trigger.LogicData[2];
            effects.Roll = _ => (int)trigger.LogicData[0];
            await combat.MonsterSkillHitAsync(enemy, DefenceAttack(998891), [player], enemy.Uid);
            Assert.Equal(1, combat.DefenceElement(player));
            effects.Roll = _ => 0;
            await Character.SetHpSpAsync(player, long.MaxValue, null);
            await combat.MonsterSkillHitAsync(enemy, DefenceAttack(998892), [player], enemy.Uid);
            var holy = Assert.Single(player.Buffs, b => b.ConfigId == 904407);
            Assert.False(holy.IsPassive);
            Assert.Equal(duration, holy.DurationMs);
            Assert.Equal(7, combat.DefenceElement(player));
            await Character.SetHpSpAsync(player, long.MaxValue, null);
            await combat.MonsterSkillHitAsync(enemy, DefenceAttack(998893), [player], enemy.Uid);
            Assert.Equal(holy.ExpireMs, Assert.Single(player.Buffs, b => b.ConfigId == 904407).ExpireMs);
        });
        await Task.Delay(duration + 150);
        await Loop.InvokeAsync(() => { Assert.Equal(1, combat.DefenceElement(player)); return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task CardEndurePreventsActualBossKnockbackAndRemovalRestoresMovement()
    {
        await using var alice = await InWorld("Cardendure");
        var player = Session("cardendure");
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(async () =>
        {
            var start = player.Position.Clone();
            // 測 shared boss/PvP 位移的實際位置變更，不只判斷圖示或 buff 是否存在。
            await EquipDefenceCard(player, 20523);
            await combat.KnockPlayerAsync(player, start.X - 1, start.Z, 1, 998894, 1, 300);
            Assert.Equal(start, player.Position);
            player.Items.RemoveAll(i => i.Guid == 889901);
            await _app.Services.GetRequiredService<CardEffects>().RefreshAsync(player);
            await combat.KnockPlayerAsync(player, start.X - 1, start.Z, 1, 998894, 1, 300);
            Assert.NotEqual(start, player.Position);
        });
        var move = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.SkillConfigId == 998894);
        Assert.NotEqual(move.StartPos, move.EndPos);
    }
    [ClientDataTheory]
    [InlineData(20512)]
    [InlineData(20513)]
    [InlineData(20516)]
    [InlineData(20517)]
    public async Task CardReflectionNeverReflectsMonsterNormalOrSkillDamage(int group)
    {
        await using var alice = await InWorld("Cardpveexclude");
        var player = Session("cardpveexclude");
        var enemy = await Target("cardpveexclude", stationary: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        combat.Rolls = new GuardianStableDamageRolls();
        long enemyHp = 0;
        await Loop.InvokeAsync(async () =>
        {
            enemy.StaysOutOfBattle = true; enemy.KeepsDamage = true;
            enemy.Hp = 10000; enemyHp = enemy.Hp;
            await EquipDefenceCard(player, group);
            await Character.SetHpSpAsync(player, long.MaxValue, null);
            await monsters.Attack!(enemy, player);
        });
        var normal = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == enemy.Uid && h.SkillId == enemy.Template.NormalSkill);
        Assert.True(Assert.Single(normal.Dsts).Damage > 0);
        await Loop.InvokeAsync(async () =>
        {
            Assert.Equal(enemyHp, enemy.Hp);
            await Character.SetHpSpAsync(player, long.MaxValue, null);
            long before = Character.Stats(player)[2000];
            await combat.MonsterSkillHitAsync(enemy, DefenceAttack(998895, group is 20512 or 20513 ? 2 : 1), [player], enemy.Uid);
            Assert.True(Character.Stats(player)[2000] < before);
            Assert.Equal(enemyHp, enemy.Hp);
        });
        Assert.True(Assert.Single((await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 998895)).Dsts).Damage > 0);
    }
    [ClientDataFact]
    public async Task PvpArrowElementReachesTheVictimsRealElementResistanceBeforeHpCommit()
    {
        await using var alice = await InWorld("ArrowPvPA");
        await using var bob = await InWorld("ArrowPvPB");
        var source = Session("arrowpvpa"); var target = Session("arrowpvpb");
        // 共用隔離房間，魔法反射卡不作用於本案的物理二連矢。
        await PrepareReflectionRoom(alice, bob, source, target, 1450201, 0);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var arrowSkill = SkillConfig.Get(data, 1300301)!.Value;
        long baseline = 0, resisted = 0;
        await Loop.InvokeAsync(async () =>
        {
            source.BagState.Info.CurrentArrowID = 1002013; // 配對水晶箭 ItemConfig._kParam1=5。
            Assert.Equal(1, arrowSkill.EleType);
            Assert.Equal(5, combat.AttackElement(source, arrowSkill.Row));
            await combat.CastExtraOnPlayerAsync(source, arrowSkill, target);
            baseline = 100_000 - target.CurrentHp!.Value;
            Assert.True(baseline > 0);
            await Character.SetHpSpAsync(target, 100_000, null);
            await buffs.AddGuardianAsync(target, 2349006, 10000, target.Role!.Id);
            Assert.Contains(target.Buffs, b => b.ConfigId == 2349006 && b.Changes.Contains((315L, 1000d)));
            await combat.CastExtraOnPlayerAsync(source, arrowSkill, target);
            resisted = 100_000 - target.CurrentHp!.Value;
            Assert.Equal((long)Math.Round(baseline * .9), resisted);
        });
        var first = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1300301);
        var second = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1300301);
        Assert.Equal(5, first.SrcElement);
        Assert.Equal(5, second.SrcElement);
        Assert.Equal(baseline, Assert.Single(first.Dsts).Damage);
        Assert.Equal(resisted, Assert.Single(second.Dsts).Damage);
    }
    [ClientDataFact]
    public async Task PublishedPvpHitCannotTriggerCardsInANewEpochButANewHitCan()
    {
        await using var alice = await InWorld("EpochCardsA");
        await using var bob = await InWorld("EpochCardsB");
        var source = Session("epochcardsa"); var target = Session("epochcardsb");
        await PrepareReflectionRoom(alice, bob, source, target, 1450301, 0);
        await HealingStat(bob, AttributeId.MaxSp, 10000);
        var enemy = await Target("epochcardsa", stationary: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        var effects = _app.Services.GetRequiredService<CardEffects>();
        var data = _app.Services.GetRequiredService<GameData>();
        var normal = SkillConfig.Get(data, 1000001)!.Value;
        bool changeEpoch = true;
        combat.PvpDealtAsync.Add((attacker, victim, _, _) =>
        {
            // 確定在原傷害已提交、卡片發布事件之前換生命期，不靠計時競速。
            if (attacker == source && victim == target && changeEpoch)
            { source.SceneGeneration++; changeEpoch = false; }
            return Task.CompletedTask;
        });
        await Loop.InvokeAsync(async () =>
        {
            enemy.StaysOutOfBattle = true; enemy.KeepsDamage = true; enemy.Hp = 100000;
            int socket = 1;
            foreach (int group in new[] { 20230, 20509 })
            {
                var card = data.Table("CardConfig").Values.First(r => GameData.Int(r, "_iTier") == 0
                    && EquipmentStunts.Ids(r, "_kSkillAttribute").SelectMany(id =>
                        EquipmentAffixes.Rows(data.Row("SpecialEffectConfig", id)!.Value, "_kSpecialEffect"))
                    .Any(op => op is [2, var id] && BuffConfig.Get(data, id)?.BuffId == group));
                EnsureCardFixtureGear(source, 1);
                source.Items.Add(new InvItem
                {
                    Guid = 889910 + socket, Entry = (int)GameData.Int(card, "_iCardID"), Stack = 1,
                    Pack = 3, Grid = CharacterService.CardGrid(1, socket++), Attrs = new() { [502] = 0 }
                });
            }
            await effects.RefreshAsync(source);
            effects.Roll = _ => 0;
            await Character.SetHpSpAsync(target, 100000, 10000);
            await combat.CastExtraOnPlayerAsync(source, normal, target);
            Assert.True(target.CurrentHp < 100000); // 原已提交命中不能被回滾。
            Assert.Equal(100000, source.CurrentHp); // 舊生命期命中不得反射。
            Assert.Equal(10000, Character.Stats(target)[AttributeId.Sp]);
            Assert.Equal(100000, enemy.Hp); // 也不得在新生命期濺射。
            await Character.SetHpSpAsync(target, 100000, 10000);
            await combat.CastExtraOnPlayerAsync(source, normal, target);
            Assert.True(source.CurrentHp < 100000);
            Assert.True(Character.Stats(target)[AttributeId.Sp] < 10000);
            Assert.True(enemy.Hp < 100000);
        });
    }
}
