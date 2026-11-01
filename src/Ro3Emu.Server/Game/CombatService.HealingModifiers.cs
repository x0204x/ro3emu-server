using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    // 配對1411211～1411214是14112復活的mode1；1400711～1400720保留Heal友／敵兩組公式。
    private static bool IsResurrectionRow(JsonElement row) => Script(row) is "Resurrection" or "AcolyteBD4";
    private static bool IsDualBranchHeal(JsonElement row) => Script(row) is "Heal" or "AcolyteBD11";
    internal Func<GameSession, GameSession, bool>? SameHealingParty { get; set; }

    private double HealingRatio(JsonElement row, GameSession source, GameSession recipient)
    {
        var ratios = Numbers(row, "_kDamageParam1");
        // Harmony_Heal 的原始P1[0]/P1[1]與描述為非小隊30%／小隊60%，不以同場景冒充隊友。
        int branch = Script(row) == "Harmony_Heal" && SameHealingParty?.Invoke(source, recipient) == true ? 1 : 0;
        return ratios.ElementAtOrDefault(branch);
    }

    private bool PreferHealingParty(JsonElement row, GameSession source, GameSession recipient) =>
        Script(row) is "ColuseoHeal" or "Choral" && SameHealingParty?.Invoke(source, recipient) == true;

    /// <summary>治癒術描述10110300003：Base每級提高0.1%治療量。
    /// 僅接受配對的 T_AXadd0_Xisskilllv [1,10,0,1]，不把所有額外傷害公式當治療倍率。
    /// (inferred) 基礎治療先依既有四捨五入，再乘Base修正並向下取整。</summary>
    private static long LevelAdjustedHealing(JsonElement row, int baseLevel, long amount)
    {
        var skill = new SkillConfig(row);
        if (!IsDualBranchHeal(row) || skill.DamageFormulaExtra is not ["T_AXadd0_Xisskilllv"]
            || skill.DamageParamExtra is not [[1, 10, 0, 1]]) return amount;
        return (long)Math.Clamp(Math.Floor(amount * (1 + baseLevel * 10 / 10000d)), 0, long.MaxValue);
    }

    /// <summary>燦爛聖光一般模式：BuffConfig._kData1 與描述10110300036的MATK護盾比例相符；
    /// 護盾不是治療量，不能套207／208。Kyrie 同樣以施放者 MATK 計量，吸收限制由 BuffService 處理。
    /// Choral 採實際 _kBuff1 指向的 BuffConfig 執行值；與描述 100% 的差異保留於稽核報告。</summary>
    private long HealingSkillShield(JsonElement skill, int buffId, GameSession caster)
    {
        if (Script(skill) is not ("ColuseoHeal" or "Choral" or "Kyrie_Eleison" or "S1_Poem")
            || BuffConfig.Get(_data, buffId) is not { } buff) return 0;
        var source = _character.HealingStats(caster);
        return (long)Math.Max(0, Math.Floor(source[AttributeId.Matk] * buff.Data1.FirstOrDefault() / 10000d));
    }

    /// <summary>AttributeConfig 207/208 and 1207/1208: independent healing done/received inputs. Callers pass the
    /// full live-source attributes (<see cref="CharacterService.HealingStats"/>), including ordinary buffs and gear.
    /// (inferred) Multiply the two additive groups, floor once, then clamp to missing HP.</summary>
    internal static long ModifiedHealing(StatSheet source, StatSheet target, long amount) =>
        (long)Math.Clamp(Math.Floor(amount * Math.Max(0, 1 + (source[AttributeId.PerTreat] + source[AttributeId.PerTreat1]) / 10000.0)
            * Math.Max(0, 1 + (target[AttributeId.PerCure] + target[AttributeId.PerCure1]) / 10000.0)), 0, long.MaxValue);

    /// <summary>AttributeConfig sp_dedution/per_sp_dedution (1231/1232); Marionette increases the latter.
    /// (inferred) Apply the flat then percent change once to the summed SP cost, rounding up.</summary>
    internal static long ModifiedSpCost(StatSheet stats, long amount) => amount <= 0 ? 0 :
        (long)Math.Clamp(Math.Ceiling(Math.Max(0, amount + stats[AttributeId.SpDedution]) * Math.Max(0, 1 + stats[AttributeId.PerSpDedution] / 10000.0)), 0, long.MaxValue);
}
