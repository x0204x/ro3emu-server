using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(2019001, 2002801, 0)]
    [InlineData(2019001, 2002801, 1)]
    [InlineData(2019001, 2002801, 2)]
    [InlineData(2019001, 2002801, 3)]
    [InlineData(2020001, 2002901, 0)]
    [InlineData(2020001, 2002901, 1)]
    [InlineData(2020001, 2002901, 2)]
    [InlineData(2020001, 2002901, 3)]
    [InlineData(2021001, 2003001, 0)]
    [InlineData(2021001, 2003001, 1)]
    [InlineData(2021001, 2003001, 2)]
    [InlineData(2021001, 2003001, 3)]
    public async Task CardAutocastUsesTierChanceAndInternalCooldown(int basis, int extraSkill, int tier)
    {
        await using var alice = await InWorld("Cardproc");
        var player = Session("cardproc");
        var data = _app.Services.GetRequiredService<GameData>();
        var effects = _app.Services.GetRequiredService<CardEffects>();
        var card = data.Table("CardConfig").Values.First(r => GameData.Int(r, "_iTier") == tier
            && EquipmentStunts.Ids(r, "_kSkillAttribute").SelectMany(id =>
                EquipmentAffixes.Rows(data.Row("SpecialEffectConfig", id)!.Value, "_kSpecialEffect"))
                .Any(op => op is [2, var id] && id == basis));
        var target = await Target("cardproc", stationary: true);
        var normal = data.Row("SkillConfig", 1000001)!.Value;
        int casts = 0;
        _app.Services.GetRequiredService<CombatService>().DealtAsync.Add((s, m, row, _) =>
        {
            if (s == player && m == target && GameData.Int(row, "_iID") == extraSkill) casts++;
            return Task.CompletedTask;
        });
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 1_000_000; target.StaysOutOfBattle = true; target.KeepsDamage = true;
            player.Items.Add(new InvItem
            {
                Guid = 880001, Entry = (int)GameData.Int(card, "_iCardID"), Stack = 1,
                Pack = 3, Grid = CharacterService.CardGrid(1, 1), Attrs = new() { [502] = tier }
            });
            await effects.RefreshAsync(player);
            var ids = EquipmentStunts.CardEffects(player, data).SelectMany(e =>
                EquipmentAffixes.Rows(data.Row("SpecialEffectConfig", e.Effect)!.Value, "_kSpecialEffect"))
                .Where(op => op is [2, _]).Select(op => op[1]);
            long chance = ids.SelectMany(id => EquipmentAffixes.Rows(data.Row("BuffConfig", id)!.Value, "_kBuffAttributeList"))
                .Where(op => op.Length >= 5 && op[1] == basis / 100 && op[2] == 4 && op[3] == 1).Sum(op => op[4]);
            Assert.InRange(chance, 1, 10000);
            effects.Roll = _ => (int)chance;
            await effects.OnDealtAsync(player, target, normal, 100);
            Assert.Equal(1_000_000, target.Hp);
            effects.Roll = _ => (int)chance - 1;
            await effects.OnDealtAsync(player, target, data.Row("SkillConfig", 1200401)!.Value, 100);
            await effects.OnDealtAsync(player, target, normal, 0);
            if (basis == 2021001) await effects.OnDealtAsync(player, target, data.Row("SkillConfig", 1100101)!.Value, 100);
            Assert.Equal(1_000_000, target.Hp);
            await effects.OnDealtAsync(player, target, normal, 100);
            Assert.True(target.Hp < 1_000_000);
            Assert.Equal(1, casts); // 追加普攻不得再觸發卡片形成遞迴。
            long once = target.Hp;
            await effects.OnDealtAsync(player, target, normal, 100);
            Assert.Equal(once, target.Hp);
            Assert.Equal(1, casts);
            player.Items.RemoveAll(i => i.Guid == 880001);
            await effects.RefreshAsync(player);
        });
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            m => m.SkillId == extraSkill && m.Dsts.Any(d => d.DstID == target.Uid));
        Assert.Contains(hit.Dsts, d => d.Damage > 0);
    }
}
