using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CardEffects
{
    private const int ArmourElementGroup = 9044, HolyArmourGroup = 20515, HolyOnHitGroup = 20514;
    private const int MagicNullGroup = 20510, MagicNullAdvancedGroup = 20511, SilenceReductionGroup = 20260;
    private const int PermanentEndureGroup = 20523, SlowImmunityGroup = 20525, StunImmunityGroup = 20527;

    private void InitializeDefence()
    {
        _combat.DefenceElementOverrides.Add(ArmourElement);
        _combat.IncomingSkillDamageAdjusters.Add(DefensiveDamage);
        _combat.DisplacementImmunities.Add(s => HasDefencePassive(s, PermanentEndureGroup));
        _buffs.PlayerBuffImmunities.Add(RejectCardControl);
        _combat.TakenAsync.Add((_, target, amount) => HolyArmourOnHitAsync(target, amount));
        _combat.PvpSkillHitPublished.Add((_, target, row, amount, _) =>
            row.TryGetProperty("_ro3CardReflection", out _) ? Task.CompletedTask : HolyArmourOnHitAsync(target, amount));
    }

    private IEnumerable<BuffConfig> LiveDefenceBuffs(GameSession target) => target.Buffs
        .Where(b => b.ExpireMs > MonsterService.NowMs)
        .Select(b => BuffConfig.Get(_data, b.ConfigId)).OfType<BuffConfig>();

    private bool HasDefencePassive(GameSession target, int group) => LiveDefenceBuffs(target).Any(b => b.BuffId == group);

    // 每張元素卡同時給說明 buff 與 9044 實效；鬼火 20110 的舊 logic=3 不可覆蓋真正 904403 的風=2。
    // (inferred) 暫時轉屬性優先於卡片常駐轉屬性；相同來源層使用最後加入的效果。
    private int? ArmourElement(GameSession target)
    {
        var timed = target.Buffs.LastOrDefault(b => !b.IsPassive && b.ExpireMs > MonsterService.NowMs
            && BuffConfig.Get(_data, b.ConfigId)?.BuffId == ArmourElementGroup);
        if (timed != null && BuffConfig.Get(_data, timed.ConfigId) is { LogicData: [var temporary, ..] }) return (int)temporary;
        return LiveDefenceBuffs(target).Where(b => b.BuffId is ArmourElementGroup or HolyArmourGroup)
            .Select(b => (int?)b.LogicData.FirstOrDefault()).LastOrDefault(e => e is >= 1 and <= 10);
    }

    private long DefensiveDamage(GameSession target, JsonElement row, DamageHitResult result, long amount)
    {
        if (amount <= 0 || result == DamageHitResult.Miss) return amount;
        var active = LiveDefenceBuffs(target).ToArray();
        if (new SkillConfig(row).DmgType == 2 && !row.TryGetProperty("_ro3CardReflection", out _))
        {
            // (inferred) 同族重複來源採最高機率並共用冷卻；不是把每張卡當成獨立免傷擲骰。
            var nullifier = active.Where(b => b.BuffId is MagicNullGroup or MagicNullAdvancedGroup)
                .OrderByDescending(b => b.LogicData.FirstOrDefault()).Select(b => (BuffConfig?)b).FirstOrDefault();
            if (nullifier is { LogicData: [var chance, ..] } effect && chance > 0 && Roll(10000) < chance
                && Ready(target, MagicNullGroup, DescriptionMs(effect.DescData, 1))) return 0;
        }
        if (active.Any(b => b.BuffId == BuffService.SilenceGroup))
        {
            long reduction = active.Where(b => b.BuffId == SilenceReductionGroup).Select(b => b.LogicData.FirstOrDefault()).DefaultIfEmpty().Max();
            amount = (long)Math.Floor(amount * (1 - Math.Clamp(reduction, 0, 10000) / 10000d));
        }
        return amount;
    }

    private async Task HolyArmourOnHitAsync(GameSession target, long amount)
    {
        if (amount <= 0 || target.Dead || target.Role == null) return;
        var trigger = LiveDefenceBuffs(target).Where(b => b.BuffId == HolyOnHitGroup && b.LogicData.Length >= 3)
            .OrderByDescending(b => b.LogicData[0]).ThenByDescending(b => b.LogicData[2]).Select(b => (BuffConfig?)b).FirstOrDefault();
        if (trigger is not { LogicData: [_, _, _, ..] } effect || Roll(10000) >= effect.LogicData[0]
            || !Ready(target, HolyOnHitGroup, DescriptionMs(effect.DescData, effect.DescData.Length - 1))) return;
        var logic = effect.LogicData;
        await _buffs.AddAsync(target, (int)logic[1], (int)logic[2], target.Role.Id);
    }

    private static long DescriptionMs(string[] description, int index) => index >= 0 && index < description.Length
        && double.TryParse(description[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? (long)Math.Round(seconds * 1000) : 0;

    private bool RejectCardControl(GameSession target, JsonElement incoming)
    {
        var buff = new BuffConfig(incoming);
        if (buff.Type != 2) return false;
        if (buff.BuffId == BuffService.StunGroup && HasDefencePassive(target, StunImmunityGroup)) return true;
        if (buff.BuffId is BuffService.KnockBackGroup or BuffService.KnockUpGroup
            && HasDefencePassive(target, PermanentEndureGroup)) return true;
        // BuffConfig 的 Slow 分類 24；同時確認負移速，避免把無減速的其他類型整包免疫。
        return buff.BuffCategory.Contains(24) && buff.ChangeAttri.Any(a => a is [var id, < 0]
            && id is AttributeId.Speed or AttributeId.PerSpeed or AttributeId.BuffPerSpeed)
            && HasDefencePassive(target, SlowImmunityGroup);
    }
}
