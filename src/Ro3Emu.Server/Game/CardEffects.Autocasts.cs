using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CardEffects
{
    private const int BashProc = 20190, ThunderProc = 20200, DoubleAttackProc = 20210;

    /// <summary>
    /// BuffConfig 20191..20194／20201..20202：物理攻擊觸發；20211..20214：只限普通攻擊。
    /// 基底 _kLogicData[1] 是額外施放的 SkillConfig，機率來自同族 _kBuffAttributeList，
    /// _kDescData[1] 為秒數冷卻。時點沿用已提交傷害事件；卡片連鎖不再觸發卡片自動施放（inferred）。
    /// 不扣主動技能成本、不佔用或打斷原本施放，沿用 CastExtraAsync 的生命週期。
    /// </summary>
    private async Task AutocastOnDamageAsync(GameSession player, MonsterUnit target, JsonElement source,
        IReadOnlyList<Passive> passives)
    {
        var state = RuntimeFor(player);
        if (state.ResolvingAutocast || target.Dead || target.Hp <= 0 || new SkillConfig(source).DmgType != 1) return;
        state.ResolvingAutocast = true;
        try
        {
            foreach (var (basis, group, chance, desc) in Procs(passives))
            {
                if (group is not (BashProc or ThunderProc or DoubleAttackProc) || desc.Length < 2) continue;
                if (group == DoubleAttackProc && !new SkillConfig(source).IsNormalAttack(_data)) continue;
                var logic = new BuffConfig(basis).LogicData;
                if (logic is not [0, var skillId] || SkillConfig.Get(_data, skillId) is not { } row) continue;
                if (target.Dead || target.Hp <= 0 || Roll(10000) >= chance || !Ready(player, group, (long)(desc[1] * 1000))) continue;
                await _combat.CastExtraAsync(player, row.Row, [target]);
            }
        }
        finally { state.ResolvingAutocast = false; }
    }
}
