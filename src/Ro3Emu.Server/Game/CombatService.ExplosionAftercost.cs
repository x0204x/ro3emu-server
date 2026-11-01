using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>能量釋放子技1635201：說明10110300091與extra[1,5000,101,3]為爆發後自身扣50%MaxHP，
    /// 最少保留1HP。它不是敵方傷害倍率，也不重付主技1631701已收取的type7初始成本。
    /// 本hook只跟成功子技完成；主技倒數、護盾來源及自動觸發的完整owner仍未重建。</summary>
    private async Task ApplyExplosionAftercostAsync(GameSession source, ActiveSkillCast cast, JsonElement row)
    {
        var skill = new SkillConfig(row);
        if (skill.ScriptLogic != "BombSuicide2" || skill.DamageFormulaExtra is not ["T_AXadd0_etc"]
            || skill.DamageParamExtra is not [[1, >= 0, AttributeId.MaxHp, 3]]) return;
        using var held = await CharacterService.Lock(source);
        if (!CurrentCastMap(source, cast) || source.Dead) return;
        var stats = _character.Stats(source);
        long cost = (long)Math.Floor(stats[AttributeId.MaxHp] * (double)skill.DamageParamExtra[0][1] / 10000);
        // (inferred) 明列的HP扣除不經護盾；取整後保留1HP，不產生怪物受擊或擊殺事件。
        await _character.SetHpSpAsync(source, Math.Max(1, stats[AttributeId.Hp] - cost), null);
    }
}
