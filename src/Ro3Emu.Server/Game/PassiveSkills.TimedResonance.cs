using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class PassiveSkills
{
    private const int DragonTransformationGrant = 11130301, DragonTransformationBuff = 1133001,
        ShadowChainGrant = 11532801, ShadowChainBuff = 11533101;

    private void InitializeTimedResonance(EmblemService emblems)
    {
        _combat.SkillCastCompleted.Add(TimedResonanceCompletedAsync);
        emblems.EffectsChanged.Add(ReconcileTimedResonanceAsync);
        _character.JobChanged.Add(ReconcileTimedResonanceAsync);
        _combat.DamageAdjusters.Add((s, _, _, _, damage) => ShadowChainDamage(s, damage));
        _combat.PvpOutgoingDamageAdjusters.Add((s, _, _, _, damage) => ShadowChainDamage(s, damage));
    }

    // SkillConfig 11130301／11532801 描述給觸發技能與持續秒數；Buff1133001 的 type15 切11302 mode1。
    // Buff11533101 原名「連韻」、上限3、每層1215+500，與11532801「三連韻」敘述相符。
    // (inferred) 兩個狀態的配對及觸發時點採成功完成一次施放；追加技及中斷不觸發。
    private async Task TimedResonanceCompletedAsync(GameSession source, ActiveSkillCast cast, JsonElement row)
    {
        if (cast.Request.IsExtraSkill || source.Role == null || source.Dead) return;
        int group = (int)new SkillConfig(row).SkillId;
        int grant = group == 11303 ? DragonTransformationGrant
            : group is 15320 or 15322 or 15323 ? ShadowChainGrant : 0;
        if (grant == 0 || !EmblemService.ResonanceSkills(source, _data).Contains(grant)
            || SkillConfig.Get(_data, grant) is not { } effect) return;
        int duration = (int)(Value(effect.DescData, 0) * 1000);
        if (duration > 0)
            await _buffs.AddAsync(source, grant == DragonTransformationGrant ? DragonTransformationBuff : ShadowChainBuff,
                duration, source.Role.Id);
    }

    // (inferred) 卸下精確共鳴或轉職即撤除其限時增益，不讓穿回後延續已撤銷的層數。
    private async Task ReconcileTimedResonanceAsync(GameSession source)
    {
        var current = CapturePassiveTarget(source);
        foreach (var (grant, buffId) in new[] { (DragonTransformationGrant, DragonTransformationBuff), (ShadowChainGrant, ShadowChainBuff) })
            if (!EmblemService.ResonanceSkills(source, _data).Contains(grant))
                foreach (var buff in source.Buffs.Where(b => b.ConfigId == buffId).ToArray())
                {
                    if (!current()) return;
                    await _buffs.RemoveAsync(source, buff);
                }
    }

    // 既有守護寵物增傷只讀 IsGuardianOwned；此普通職業Buff必須有自己的實際傷害處理。
    // 按原1215數值加總，與其他獨立增傷層乘算及向下取整的順序為 inferred。
    private long ShadowChainDamage(GameSession source, long damage)
    {
        if (damage <= 0 || !EmblemService.ResonanceSkills(source, _data).Contains(ShadowChainGrant)) return damage;
        double bonus = source.Buffs.Where(b => b.ConfigId == ShadowChainBuff && b.ExpireMs > MonsterService.NowMs)
            .SelectMany(b => b.Changes).Where(c => c.Attr == AttributeId.PerDmg1).Sum(c => c.Value);
        return (long)Math.Clamp(Math.Floor(damage * Math.Max(0, 1 + bonus / 10000)), 0, long.MaxValue);
    }
}
