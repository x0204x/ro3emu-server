using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class PassiveSkills
{
    private void InitializeWindCutter(EmblemService emblems)
    {
        _combat.CastLanded.Add(ClearPreviousWindCutterAsync);
        emblems.EffectsChanged.Add(ReconcileWindCutterAsync);
        _character.JobChanged.Add(ReconcileWindCutterAsync);
    }

    // 11303與11328是同一風刃技能的兩種Buff群。切換共鳴重施時不能讓15%與40%同時累加。
    private async Task ClearPreviousWindCutterAsync(GameSession source, JsonElement row)
    {
        if (new SkillConfig(row).SkillId != 11303) return;
        var current = CapturePassiveTarget(source);
        foreach (var buff in source.Buffs.Where(b => b.ConfigId / 100 is 11303 or 11328).ToArray())
        {
            if (!current()) return;
            await _buffs.RemoveAsync(source, buff);
        }
    }

    // (inferred) 撤除stage後不讓尚未到期的強化屬性殘留：降回該次施放level並保留剩餘時間。
    // 不因新穿戴stage替既有普通Buff補發增益；需要重新施放。錯職業/未學技能則只移除。
    private async Task ReconcileWindCutterAsync(GameSession source)
    {
        if (EmblemService.ResonanceSkills(source, _data).Contains(11130201)) return;
        var current = CapturePassiveTarget(source);
        foreach (var buff in source.Buffs.Where(b => b.ConfigId / 100 == 11328).ToArray())
        {
            if (!current()) return;
            long expires = buff.ExpireMs;
            int level = buff.ConfigId % 100;
            await _buffs.RemoveAsync(source, buff);
            if (!current() || expires <= MonsterService.NowMs || LearnedCompanionSkill(source, 11303) == null) continue;
            if (BuffConfig.Get(_data, 1130300 + level) is { } baseBuff)
                await _buffs.AddSupportAsync(source, (int)baseBuff.Id,
                    (int)Math.Clamp(expires - MonsterService.NowMs, 1, int.MaxValue), source.Role!.Id);
        }
    }
}
