using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class PassiveSkills
{
    private const int BlitzBeat = 13102, BlitzBeatFollowUp = 13110, FocusedArrowStrike = 13305,
        WargStrike = 13312, SwiftWarg = 13326;
    private const int FalconLease = 1500100, WolfLease = 1500400, BattleMountSlot = 2;
    private SkillService? _companionSkills;
    private readonly ConditionalWeakTable<GameSession, CompanionProcState> _companionProcs = [];

    private sealed class CompanionProcState(long role, long generation, object? attachment)
    {
        public readonly long Role = role, Generation = generation;
        public readonly object? Attachment = attachment;
        public long ReadyMs;
    }

    private void InitializeCompanionPassives(SkillService skills)
    {
        _companionSkills = skills;
        _combat.MonsterSkillHitPublished.Add(CompanionMonsterHitAsync);
        _combat.PvpSkillHitPublished.Add(CompanionPlayerHitAsync);
        _combat.SkillCastCompleted.Add(SwiftWargCompletedAsync);
    }

    private SkillConfig? LearnedCompanionSkill(GameSession s, int group) => s.Progress is { } p
        && _companionSkills is { } skills && skills.InTree(p.Career, group) && skills.Level(p, group) is > 0 and var level
            ? SkillConfig.Get(_data, skills.RealId(group, level)) : null;

    private static bool RidesWolf(GameSession s) =>
        s.Mounts?.MountIds.GetValueOrDefault(BattleMountSlot) is { MountId: WolfLease, IsRiding: true };

    // 配對 LC_ConditionList 86000361/86000415：需獵鷹跟隨且未騎狼；13102 AutoLearn
    // 明確指向 13110 的 5% 追擊傷害列及 13111 空監看列。不可用主動列打全額傷害。
    // 13312 mode0 已是 Focused Arrow Strike 追擊列；mode1 才是騎乘主動攻擊。
    private SkillConfig? CompanionFollowUp(GameSession s, JsonElement row, long damage)
    {
        if (damage <= 0 || s.Role == null || s.Dead || row.TryGetProperty("_ro3CardReflection", out _)) return null;
        var trigger = new SkillConfig(row);
        if (trigger.IsNormalAttack(_data) && !RidesWolf(s)
            && s.CareerSpecial?.PendantSlot?.CartRemouldCfg == FalconLease
            && LearnedCompanionSkill(s, BlitzBeat) is { } falcon
            && Roll(10000) < Value(falcon.DescData, 7) * 100)
            return falcon.AutoLearnSkill.Select(id => SkillConfig.Get(_data, id))
                .FirstOrDefault(r => r?.SkillId == BlitzBeatFollowUp);
        if (trigger.SkillId != FocusedArrowStrike || !CareerSpecialService.IsLeasedMount(s, WolfLease)
            || LearnedCompanionSkill(s, WargStrike) is not { } wolf) return null;
        if (!_companionProcs.TryGetValue(s, out var state) || state.Role != s.Role.Id
            || state.Generation != s.SceneGeneration || !ReferenceEquals(state.Attachment, s.Attachment))
        {
            _companionProcs.Remove(s);
            state = new CompanionProcState(s.Role.Id, s.SceneGeneration, s.Attachment);
            _companionProcs.Add(s, state);
        }
        long now = MonsterService.NowMs;
        if (now < state.ReadyMs) return null;
        state.ReadyMs = now + (long)(Value(wolf.DescData, 6) * 1000);
        return wolf;
    }

    private async Task CompanionMonsterHitAsync(GameSession s, MonsterUnit target, JsonElement row, long damage, DamageHitResult result)
    {
        if (target.Hp <= 0 || CompanionFollowUp(s, row, damage) is not { } extra) return;
        var targets = new List<MonsterUnit> { target };
        if (extra.SkillId == BlitzBeatFollowUp)
            targets.AddRange(_combat.MonstersNear(s, target.Pos, (float)Value(extra.DescData, 1))
                .Where(m => m != target).Take(Math.Max(0, (int)extra.TargetMax - 1)));
        await _combat.CastExtraAsync(s, extra.Row, targets);
    }

    private async Task CompanionPlayerHitAsync(GameSession s, GameSession target, JsonElement row, long damage, DamageHitResult result)
    {
        if (target.Dead || CompanionFollowUp(s, row, damage) is not { } extra) return;
        long generation = s.SceneGeneration, scene = s.WorldScene;
        var attachment = s.Attachment;
        var targets = new List<GameSession> { target };
        if (extra.SkillId == BlitzBeatFollowUp)
            targets.AddRange(_sessions.All().Where(p => p != target && p.WorldScene == scene && _combat.Hostile(s, p)
                && MonsterService.Distance(p.Position.X, p.Position.Z, target.Position.X, target.Position.Z) <= Value(extra.DescData, 1))
                .Take(Math.Max(0, (int)extra.TargetMax - 1)));
        foreach (var victim in targets)
        {
            if (s.Dead || s.SceneGeneration != generation || s.WorldScene != scene
                || !ReferenceEquals(s.Attachment, attachment) || attachment?.Cancelled == true) break;
            await _combat.CastExtraOnPlayerAsync(s, extra, victim);
        }
    }

    // Swift Warg 的父技能描述：Warg Strike / Warg Hunt 施放完成後重置自身冷卻。
    // 使用既有一次完成事件，追加追擊不觸發；保留共用CD下限。滿級移動替代的
    // WolfAttackPlus / 1333101 生命期未由客戶端資料定義，本候選不自行改寫步行。
    private Task SwiftWargCompletedAsync(GameSession s, ActiveSkillCast cast, JsonElement row)
    {
        if (cast.Request.IsExtraSkill || !RidesWolf(s) || !CareerSpecialService.IsLeasedMount(s, WolfLease)
            || new SkillConfig(row).SkillId is not (WargStrike or 13323 or 13329 or 13330)
            || LearnedCompanionSkill(s, SwiftWarg) is not { } dash) return Task.CompletedTask;
        return CombatService.ResetOwnCooldownAsync(s, dash);
    }
}
