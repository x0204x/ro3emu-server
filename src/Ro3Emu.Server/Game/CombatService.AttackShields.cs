using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private static bool AttackShieldBuff(JsonElement row, int buffId) => new SkillConfig(row).SkillId switch
    {
        11301 => buffId == 1130505,
        11607 => buffId is >= 1160701 and <= 1160710,
        11610 => buffId is 1161101 or 11161101,
        _ => false,
    };

    private static void RecordAttackShieldTarget(ActiveSkillCast cast, long targetId, long damageWithShield, DamageHitResult result)
    {
        if (cast.Request.IsExtraSkill || damageWithShield <= 0 || result == DamageHitResult.Miss
            || new SkillConfig(cast.Row).SkillId is not (11301 or 11607)
            || cast.Row.TryGetProperty("_ro3CardReflection", out _)) return;
        (cast.AttackShieldTargets ??= []).Add(targetId);
    }

    private async Task ApplyAttackShieldsAsync(GameSession source, ActiveSkillCast cast, Func<bool> current)
    {
        if (cast.Request.IsExtraSkill || !current() || cast.Row.TryGetProperty("_ro3CardReflection", out _)) return;
        var skill = new SkillConfig(cast.Row);
        if (skill.SkillId is not (11301 or 11607 or 11610)) return;
        int added = skill.SkillId == 11610 ? (cast.AttackShieldApplied == 0 ? 1 : 0)
            : (cast.AttackShieldTargets?.Count ?? 0) - cast.AttackShieldApplied;
        if (added <= 0) return;
        cast.AttackShieldApplied += added; // 同一施放的重複階段／重送不可再加同一目標。
        foreach (var tuple in Tuples(cast.Row, "_kBuff1"))
        {
            if (!current() || !RollBuff(tuple, out int id, out int duration) || !AttackShieldBuff(cast.Row, id)
                || BuffConfig.Get(_data, id) is not
                {
                    DamageFormulaExtra: ["T_AXadd0_etc"],
                    DamageParamExtra: [[0, > 0, 101, 3]]
                } buff
                || !buff.BuffCategory.Contains(30)) continue;
            long perLayer = (long)Math.Clamp(Math.Floor(_character.Stats(source)[101] * buff.DamageParamExtra[0][1] / 10000d), 0, long.MaxValue);
            if (skill.SkillId == 11610)
                await _buffs.AddSupportAsync(source, id, duration, cast.OwnerId, perLayer);
            else
                await _buffs.AddHitShieldAsync(source, id, duration, cast.OwnerId, added, perLayer);
        }
    }
}
