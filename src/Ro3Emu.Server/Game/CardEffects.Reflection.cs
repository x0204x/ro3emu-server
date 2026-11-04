using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CardEffects
{
    private const int MagicReflect = 20512, MagicReflectAdvanced = 20513,
        PhysicalReflect = 20516, PhysicalReflectAdvanced = 20517;
    private const int VitalityAttribute = 3;

    private void InitializeReflection() => _combat.PvpSkillHitPublished.Add(ReflectIncomingAsync);

    private async Task ReflectIncomingAsync(GameSession attacker, GameSession victim, JsonElement row,
        long effective, DamageHitResult result)
    {
        var incoming = new SkillConfig(row);
        if (effective <= 0 || result == DamageHitResult.Miss || row.TryGetProperty("_ro3CardReflection", out _)
            || incoming.IsNormalAttack(_data) || incoming.RangeType != SkillRelease.SingleTarget || victim.Dead || attacker.Dead) return;
        // 同族重複卡採最高星階、共用該族冷卻（inferred）；不同物理／魔法卡按各自描述篩選。
        var candidates = Passives(victim).Where(p => ReflectionFamily(p.Id / 100, incoming.DmgType))
            .GroupBy(p => p.Id / 100).Select(g => g.MaxBy(p => p.Id)!).ToArray();
        foreach (var passive in candidates)
        {
            int family = passive.Id / 100;
            var literals = Description(passive.Row);
            var logic = new BuffConfig(passive.Row).LogicData;
            if (logic.Length != 1 || SkillConfig.Get(_data, logic[0]) is not { } skill) continue;
            long amount = ReflectionAmount(literals, effective, _character.Stats(attacker)[AttributeId.MaxHp],
                _character.Stats(victim)[VitalityAttribute]);
            if (amount <= 0 || !Ready(victim, family, (long)(literals[^1] * 1000))) continue;
            bool Eligible() => Passives(victim).Any(p => p.Card.Guid == passive.Card.Guid && p.Id == passive.Id);
            await _combat.ReflectCardDamageAsync(victim, attacker, skill.Row, amount, Eligible);
        }
    }

    private static bool ReflectionFamily(int family, long type) => type switch
    {
        1 => family is PhysicalReflect or PhysicalReflectAdvanced,
        2 => family is MagicReflect or MagicReflectAdvanced,
        _ => false,
    };

    /// <summary>BuffConfig 20512/13/16/17 與繁中說明：反射受到傷害比例、攻擊者 MaxHP 上限；
    /// 進階版另以持卡者體質增加比例並封頂。向下取整與使用實際 HP 損失（不含吸收盾）為 inferred。</summary>
    internal static long ReflectionAmount(double[] description, long damage, long attackerMaxHp, long vitality)
    {
        if (description.Length is not (3 or 6) || damage <= 0 || attackerMaxHp <= 0) return 0;
        if (description.Any(v => !double.IsFinite(v))) return 0;
        decimal percent = (decimal)description[0];
        if (description.Length == 6)
        {
            if (description[2] <= 0) return 0;
            percent = Math.Min((decimal)description[4], percent + Math.Max(0, vitality) / (decimal)description[2] * (decimal)description[3]);
        }
        decimal result = Math.Min(damage * Math.Max(0, percent) / 100,
            attackerMaxHp * Math.Max(0, (decimal)description[1]) / 100);
        return (long)Math.Clamp(decimal.Floor(result), 0, long.MaxValue);
    }
}
