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
    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CardCooldownResetUsesTheOriginalRequestedModeForTalentAndMountVariants(bool mount)
    {
        await using var bot = await InWorld("CardCdVariant");
        await Gm("cardcdvariant", "!lvl 99");
        await Gm("cardcdvariant", mount ? "!job 3400" : "!job 1200");
        int group = mount ? 13099 : 11110;
        await Teach("cardcdvariant", group);
        if (!mount) await EquipConditionFixtureAsync("cardcdvariant", 2050120);
        var source = Session("cardcdvariant");
        if (mount)
            await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(source, 1331001, 600000, source.Role!.Id));
        else await ChooseProfessionTalent(bot, "cardcdvariant", 10102114);
        var target = await Target("cardcdvariant", stationary: true, immobile: true);
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 1_000_000;
            await EquipEventCard(source, 2052901);
            _app.Services.GetRequiredService<CardEffects>().Roll = _ => 0;
        });
        using var reset = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(i => i.SkillId == group
                && i.CdEndTimestampMs - i.CdStartTimestampMs <= 0));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = group * 100 + 1, TargetId = target.Uid });
        var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.NotEqual(group * 100 + 1, hit.SkillId);
        var cd = SkillCdNotify.Parser.ParseFrom((await reset.WaitAsync(Wait)).Body);
        Assert.Equal(0, Assert.Single(cd.Infos, i => i.SkillId == group).SkillMode);
        Assert.Equal(0, source.SkillCooldownShownModes[(group, 1)]);
        Assert.True(source.SkillModeCds.GetValueOrDefault((group, 1)) <= MonsterService.NowMs);
        await Loop.InvokeAsync(() => { source.Attack = null; return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task CardCooldownResetPreservesThePublicCooldownCreatedByRealBackSlide()
    {
        await using var bot = await InWorld("CardCdPublic");
        await Gm("cardcdpublic", "!job 5100");
        await Teach("cardcdpublic", 15104);
        var source = Session("cardcdpublic");
        var effects = _app.Services.GetRequiredService<CardEffects>();
        await HealingStat(bot, 102, 10000);
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(source, null, 10000);
            await EquipEventCard(source, 2052901);
            effects.Roll = _ => 0;
        });
        using var reset = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(i => i.SkillId == 15104
                && i.CdEndTimestampMs - i.CdStartTimestampMs < 1500));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510401 });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var notice = SkillCdNotify.Parser.ParseFrom((await reset.WaitAsync(Wait)).Body);
        long end = Assert.Single(notice.Infos, i => i.SkillId == 15104).CdEndTimestampMs;
        Assert.Equal(source.SkillPublicCds[15104], end);
        Assert.Equal(end, source.SkillCds[15104]);
        Assert.True(source.SkillCds[15330] > end + 10000);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510401 });
        Assert.NotEqual(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task CardSpBurnOnPublishedPvpHitDeductsCurrentSpAndIgnoresReflections()
    {
        await using var alice = await InWorld("CardBurnSource");
        await using var bob = await InWorld("CardBurnTarget");
        await Gm("cardburnsource", "!job 2100");
        await Teach("cardburnsource", 12004);
        IsolateDirectHealing();
        var source = Session("cardburnsource"); var target = Session("cardburntarget");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var effects = _app.Services.GetRequiredService<CardEffects>();
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(source.SceneEntry, [source, target])));
        foreach (var (bot, session) in new[] { (alice, source), (bob, target) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, session, reason: 3)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        }
        await HealingStat(bob, 101, 100000);
        await HealingStat(bob, 102, 1000);
        await Loop.InvokeAsync(async () =>
        {
            await EquipEventCard(source, 2023001);
            combat.SetEventPvPHandlers((a, b) => a == source && b == target, (_, _) => Task.CompletedTask);
            effects.Roll = _ => 0;
            await Character.SetHpSpAsync(target, 100000, 1000);
            var reflected = JsonSerializer.SerializeToElement(new { _iID = 1200401, _ro3CardReflection = true });
            await effects.DrainPlayerSpAsync(source, target, reflected, 50, DamageHitResult.Normal);
            Assert.Equal(1000, Character.Stats(target)[2001]);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = target.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bob, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            n => n.EntityUuid == (ulong)target.Role.Id && n.Property.Any(p => p.Idx == 2001 && p.Data == 800));
        Assert.Equal(800, Character.Stats(target)[2001]);
        Assert.True(target.CurrentHp < 100000);
    }

    private async Task<InvItem> EquipEventCard(GameSession player, int buff, int tier = 0, int slot = 1)
    {
        var data = _app.Services.GetRequiredService<GameData>();
        var row = data.Table("CardConfig").Values.First(r => GameData.Int(r, "_iTier") == tier
            && EquipmentStunts.Ids(r, "_kSkillAttribute").SelectMany(id =>
                EquipmentAffixes.Rows(data.Row("SpecialEffectConfig", id)!.Value, "_kSpecialEffect"))
                .Any(op => op is [2, var id] && id == buff));
        var item = new InvItem
        {
            Guid = 891000 + slot, Entry = (int)GameData.Int(row, "_iCardID"), Stack = 1,
            Pack = 3, Grid = CharacterService.CardGrid(1, slot), Attrs = new() { [502] = tier }
        };
        EnsureCardFixtureGear(player, 1);
        player.Items.Add(item);
        Assert.Contains(item, EquipmentStunts.ActiveCards(player, data));
        await _app.Services.GetRequiredService<CardEffects>().RefreshAsync(player);
        return item;
    }

    [ClientDataFact]
    public async Task CardSpBurnStacksChanceCapsAtFourHundredAndPublishesCurrentMonsterSp()
    {
        await using var bot = await InWorld("CardSpBurn");
        var player = Session("cardspburn");
        var target = await Target("cardspburn", stationary: true, immobile: true);
        var effects = _app.Services.GetRequiredService<CardEffects>();
        var data = _app.Services.GetRequiredService<GameData>();
        var normal = data.Row("SkillConfig", 1000001)!.Value;
        await Loop.InvokeAsync(async () =>
        {
            target.Template = target.Template with { MaxSp = 5000 };
            target.Hp = 1_000_000; target.StaysOutOfBattle = true;
            await EquipEventCard(player, 2023001, slot: 1);
            await EquipEventCard(player, 2023001, slot: 2);
            long chance = EquipmentStunts.CardEffects(player, data).SelectMany(e =>
                EquipmentAffixes.Rows(data.Row("SpecialEffectConfig", e.Effect)!.Value, "_kSpecialEffect"))
                .Where(op => op is [2, _]).SelectMany(op => EquipmentAffixes.Rows(data.Row("BuffConfig", op[1])!.Value, "_kBuffAttributeList"))
                .Where(op => op.Length >= 5 && op[1] == 20230 && op[2] == 4 && op[3] == 1).Sum(op => op[4]);
            Assert.Equal(1600, chance);
            effects.Roll = _ => (int)chance;
            await effects.DrainMonsterSpAsync(player, target, normal, 100);
            Assert.Equal(5000, target.CurrentSp);
            effects.Roll = _ => (int)chance - 1;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var change = await Next(bot, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            n => n.EntityUuid == (ulong)target.Uid && n.Property.Any(p => p.Idx == 2001 && p.Data == 4600));
        Assert.Equal(4600, target.CurrentSp);
        Assert.Equal(4600, Assert.Single(target.Entity().Property, p => p.Idx == 2001).Data);
        await Loop.InvokeAsync(async () =>
        {
            player.Attack = null;
            await effects.DrainMonsterSpAsync(player, target, normal, 100);
            Assert.Equal(4600, target.CurrentSp); // 同一冷卻不重扣。
            player.Items.RemoveAll(i => i.Guid is 891001 or 891002);
            await effects.RefreshAsync(player);
        });
        var fresh = await Target("cardspburn", stationary: true);
        Assert.Equal(fresh.Template.MaxSp, fresh.CurrentSp); // 新出生個體不沿用舊 SP。
    }

    [ClientDataTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CardHealProcUsesTenPercentTwoSecondsAndTheOriginalRecipient(int tier)
    {
        await using var bot = await InWorld("CardHealSource");
        await using var other = await InWorld("CardHealTarget");
        await Gm("cardhealsource", "!job 4100");
        await Teach("cardhealsource", 14007);
        IsolateDirectHealing();
        await HealingStat(other, 101, 100000);
        var source = Session("cardhealsource"); var recipient = Session("cardhealtarget");
        var effects = _app.Services.GetRequiredService<CardEffects>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        int extras = 0;
        await Loop.InvokeAsync(async () =>
        {
            await EquipEventCard(source, 2053101, tier);
            await Character.SetHpSpAsync(recipient, 1, null);
            combat.HealingApplied.Add((s, t, row, _) =>
            { if (s == source && t == recipient && GameData.Int(row, "_iID") == 2003901) extras++; return Task.CompletedTask; });
            effects.Roll = _ => 1000;
            await effects.HealAfterHealingAsync(source, recipient, data.Row("SkillConfig", 1400701)!.Value, 100);
            Assert.Equal(1, recipient.CurrentHp);
            effects.Roll = _ => 999;
        });
        // 先穿裝啟用卡片，再固定這個精準公式案例的 MATK；否則新穿的裝備會加回屬性。
        await HealingStat(bot, 104, 1000);
        Assert.Equal(1000, Character.HealingStats(source)[104]);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400701, TargetId = recipient.Role!.Id });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var proc = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2003901);
        long baseHeal = (long)Math.Floor(320 * (1 + source.Progress!.Level / 1000d));
        long bonus = new[] { 550, 650, 750, 950 }[tier]; // CardConfig 965..968 本體療效，不可漏算。
        Assert.Equal(bonus, Character.HealingStats(source)[207]);
        long expected = (long)Math.Floor(baseHeal * (1 + bonus / 10000d));
        Assert.Equal(expected, Assert.Single(proc.Dsts).Damage);
        Assert.Equal(recipient.Role.Id, Assert.Single(proc.Dsts).DstID);
        Assert.Equal(1, extras);
        long once = recipient.CurrentHp!.Value;
        await Loop.InvokeAsync(async () =>
        {
            await effects.HealAfterHealingAsync(source, recipient, data.Row("SkillConfig", 1400701)!.Value, 100);
            Assert.Equal(once, recipient.CurrentHp);
        });
        await Task.Delay(2050);
        await Loop.InvokeAsync(async () =>
        {
            await effects.HealAfterHealingAsync(source, recipient, data.Row("SkillConfig", 1400701)!.Value, 100);
            Assert.Equal(once + expected, recipient.CurrentHp);
            Assert.Equal(2, extras); // 觸發治療不能遞迴再觸發。
            source.Items.RemoveAll(i => i.Guid == 891001);
            await effects.RefreshAsync(source);
        });
    }

    [ClientDataFact]
    public async Task CardCooldownResetKeepsPublicOtherModeAndOtherSkillCooldowns()
    {
        await using var bot = await InWorld("CardCdReset");
        await Gm("cardcdreset", "!job 1100");
        await Teach("cardcdreset", 11002);
        var source = Session("cardcdreset");
        var effects = _app.Services.GetRequiredService<CardEffects>();
        long shared = 0, otherMode = 0, otherSkill = 0;
        await Loop.InvokeAsync(async () =>
        {
            await EquipEventCard(source, 2052901);
            effects.Roll = _ => 599;
            long now = MonsterService.NowMs;
            shared = now + 3000; otherMode = now + 20000; otherSkill = now + 25000;
            // 合成其他技能留下的公共冷卻；本次 11002 自身 8 秒 CD 必須只降到這個下限。
            source.SkillPublicCds[11002] = shared;
            source.SkillModeCds[(11002, 1)] = otherMode;
            source.SkillCds[11003] = otherSkill;
        });
        using var resetWait = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(i => i.SkillId == 11002 && i.CdEndTimestampMs == shared));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100201 });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await resetWait.WaitAsync(Wait);
        Assert.Equal(shared, source.SkillCds[11002]);
        Assert.Equal(otherMode, source.SkillModeCds[(11002, 1)]);
        Assert.Equal(otherSkill, source.SkillCds[11003]);
        await Task.Delay(3100);
        using var secondCd = bot.Inbox.Expect(e => e.IsGame(CombatService.SkillCdUpdate)
            && SkillCdNotify.Parser.ParseFrom(e.Body).Infos.Any(i => i.SkillId == 11002 && i.CdEndTimestampMs > MonsterService.NowMs + 7000));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100201 });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await secondCd.WaitAsync(Wait);
        await Task.Delay(1200);
        Assert.True(source.SkillCds[11002] > MonsterService.NowMs + 5000); // 24 秒內同技能不能再重置。
    }
}
