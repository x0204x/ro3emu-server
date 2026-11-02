using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class EmblemService
{
    internal List<Func<GameSession, Task>> EffectsChanged { get; } = [];

    private async Task PublishSkillEffectsAsync(GameSession session)
    {
        foreach (var observer in EffectsChanged) await observer(session);
    }

    /// <summary>只讀已穿戴共鳴階段及職業授權，不把共鳴技能塞入永久Skills。
    /// 12315模式1比模式0逐級多20000；15302模式1為[+1000,+1100]，保留原列及第二分支倍率。
    /// 13305的+300對應同族Buff11330101/type29/add/index0。授權到效果的接線與疊加順序inferred。</summary>
    internal static SkillParameterRules.Modifier[] SkillEffects(GameSession session, GameData data) =>
        [.. ResonanceSkills(session, data).SelectMany(id => id switch
        {
            11336001 => [new SkillParameterRules.Modifier(13305, 29, 1, 300, 0)],
            // 電子搖滾只加全部百矛係數；精準刺擊4次充能由原140001 meter owner處理，不切其mode。
            11130701 => [new SkillParameterRules.Modifier(11310, 29, 1, 2500, 0)],
            11130601 => NoteLeapSkillEffects(session, data),
            11230801 => [new SkillParameterRules.Modifier(12315, 15, 2, 1, 0)],
            // 終幕衝擊：原mode1整列含兩段+6000/+12000、5秒減速及讀書副本，不另重加倍率。
            11230401 => [new SkillParameterRules.Modifier(12342, 15, 2, 1, 0), new(12347, 15, 2, 1, 0)],
            // 11607模式1保留原Buff1的1160101/4000ms，再由該Buff授權11604模式4。
            // 主動列的601屬性適用時點、1160101首次使用消耗尚未取得原服證據，不額外猜算。
            11164201 => [new SkillParameterRules.Modifier(11607, 15, 2, 1, 0)],
            11164301 => OverBrandSkillEffects(session, data),
            11531801 => [new SkillParameterRules.Modifier(15302, 15, 2, 1, 0)],
            _ => AdditionalSkillEffects(id),
        })];

    /// <summary>盾牌格擋原buff1164501雙防各5000，對應共鳴變體11165801各10000；
    /// 602/604仍各3000。已是變體不再倍增；基礎雙防先加一次，才套其他獨立天賦（inferred）。</summary>
    internal static IReadOnlyList<(long Id, double Value)> BuffAttributes(GameSession session, GameData data,
        JsonElement row, IReadOnlyList<(long Id, double Value)> pairs)
    {
        if (new BuffConfig(row).BuffId != 11645 || !ResonanceSkills(session, data).Contains(11164701)) return pairs;
        return [.. pairs.Select(p => p.Id is 1052 or 1062 ? (p.Id, p.Value * 2) : p)];
    }
}
