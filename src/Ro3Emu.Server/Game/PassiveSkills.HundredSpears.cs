using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class PassiveSkills
{
    private void InitializeHundredSpears() => _combat.SkillCastCompleted.Add(HundredSpearsCompletedAsync);

    // 11316 的 Buff1 指向 1132401；其描述給 5 次強化充能，mode modifier 將 11310 切為強化列。
    // 普通 140002 的 10 秒回充及兩種 type8 消耗已由通用 EnergyConfig 處理，不另扣一次。
    // 轉換採一次施放完成事件（原服時間點尚待擷取），中斷及追加技能不建立或補充能量。
    private async Task HundredSpearsCompletedAsync(GameSession source, ActiveSkillCast cast, JsonElement row)
    {
        if (cast.Request.IsExtraSkill || source.Role == null || source.Dead) return;
        int group = (int)new SkillConfig(row).SkillId;
        if (group is not (11310 or 11316)) return;
        if (LearnedCompanionSkill(source, 11310) == null
            || !source.Energy.ContainsKey(140002) || !source.Energy.ContainsKey(140003)) return;
        var role = source.Role; var attachment = source.Attachment; long generation = source.SceneGeneration;
        bool Current() => ReferenceEquals(source.Role, role) && ReferenceEquals(source.Attachment, attachment)
            && attachment?.Cancelled != true && source.SceneGeneration == generation && !source.SceneLoading && !source.Dead;
        if (group == 11316)
        {
            if (LearnedCompanionSkill(source, 11316) == null
                || BuffConfig.Get(_data, 1132401) is not { } enhanced
                || source.Buffs.FirstOrDefault(b => b.ConfigId == 1132401 && b.ExpireMs > MonsterService.NowMs) is not { } buff) return;
            long charges = (long)Value(enhanced.DescData, 0);
            if (charges <= 0) return;
            if (!await _buffs.SetExistingEnergyValueAsync(source, 140002, 0) || !Current()
                || !source.Buffs.Any(b => ReferenceEquals(b, buff)) || buff.ExpireMs <= MonsterService.NowMs) return;
            await _buffs.SetExistingEnergyValueAsync(source, 140003, charges);
        }
        else if (source.Energy[140003].Value == 0)
        {
            foreach (var buff in source.Buffs.Where(b => b.ConfigId == 1132401).ToArray())
            {
                if (!Current()) break;
                await _buffs.RemoveAsync(source, buff);
            }
        }
    }
}
