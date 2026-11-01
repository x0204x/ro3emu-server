using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class PassiveSkills
{
    private void InitializeAssassinResonance(EmblemService emblems)
    {
        _combat.SkillCastCompleted.Add(AssassinResonanceCompletedAsync);
        emblems.EffectsChanged.Add(ReconcileAssassinResonanceAsync);
        _character.JobChanged.Add(ReconcileAssassinResonanceAsync);
    }

    // 原件115316.Logic→115317（移速10%、緩速抗性1000）；115319.Logic→115325（移速10%）。
    // 秒數取精確共鳴授技描述；不新增永久已學技能，也不借同名但不同用途的旗標Buff。
    // (inferred)「施放後」以現有完成事件為界；中斷、額外追擊與完成前撤掉stage均不觸發。
    private async Task AssassinResonanceCompletedAsync(GameSession source, ActiveSkillCast cast, JsonElement row)
    {
        if (cast.Request.IsExtraSkill || source.Role == null || source.Dead || source.SceneLoading) return;
        int group = (int)new SkillConfig(row).SkillId;
        int grant = group switch { 15301 => 11531601, 15303 => 11531901, _ => 0 };
        if (grant == 0 || LearnedCompanionSkill(source, group) == null
            || !EmblemService.ResonanceSkills(source, _data).Contains(grant)
            || SkillConfig.Get(_data, grant) is not { } description
            || BuffConfig.Get(_data, grant) is not { LogicData: [var effect, ..] }
            || BuffConfig.Get(_data, effect) == null) return;
        int duration = (int)(Value(description.DescData, group == 15301 ? 2 : 1) * 1000);
        if (duration > 0) await _buffs.AddSupportAsync(source, (int)effect, duration, source.Role.Id);
    }

    // (inferred) 撤除裝備／轉職即清除此共鳴短增益，避免已無資格仍保留效果；不延長剩餘時間。
    private async Task ReconcileAssassinResonanceAsync(GameSession source)
    {
        var current = CapturePassiveTarget(source);
        var grants = EmblemService.ResonanceSkills(source, _data);
        foreach (var buff in source.Buffs.Where(b => b.ConfigId / 100 is 115317 or 115325).ToArray())
        {
            int grant = buff.ConfigId / 100 == 115317 ? 11531601 : 11531901;
            if (!current()) return;
            if (!grants.Contains(grant)) await _buffs.RemoveAsync(source, buff);
        }
    }
}
