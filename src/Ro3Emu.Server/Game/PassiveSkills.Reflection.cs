using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class PassiveSkills
{
    private const int ReflectDamageGroup = 11609, ReflectDamageBuff = 11610;
    private readonly ConditionalWeakTable<GameSession, CompanionProcState> _shieldProcs = [];

    private void InitializeReactiveShield()
    {
        _combat.MonsterAttackHitPublished.Add((_, defender, row, amount, result) =>
            ReactiveShieldAsync(defender, row, amount, result));
        _combat.PvpSkillHitPublished.Add((_, defender, row, amount, result) =>
            ReactiveShieldAsync(defender, row, amount, result));
    }

    // 11609 父說明、AutoLearn11618 與 Buff11610.Logic[11618,11404]：任何正傷害命中觸發，
    // 0.5秒一次、6公尺最多5敵。用真11618列計算自身PATK，不套卡片「已受傷害百分比」。
    // (inferred) PvE/PvP 混合目標共用五人上限，距離由近到遠；反射不再觸發反射或卡片追擊。
    private async Task ReactiveShieldAsync(GameSession defender, JsonElement incoming, long amount, DamageHitResult result)
    {
        if (amount <= 0 || result == DamageHitResult.Miss || defender.Dead || defender.SceneLoading
            || incoming.TryGetProperty("_ro3CardReflection", out _) || defender.Role == null
            || LearnedCompanionSkill(defender, ReflectDamageGroup) is not { } parent
            || !defender.Buffs.Any(b => b.ConfigId / 100 == ReflectDamageBuff && b.ExpireMs > MonsterService.NowMs)
            || parent.AutoLearnSkill.Select(id => SkillConfig.Get(_data, id)).FirstOrDefault(r => r?.SkillId == 11618) is not { } baseChild)
            return;
        // 共鳴111645由已穿戴stage授權11618 mode1；每次命中重新解析，卸除即回原列。
        // mode1原表倍率是基礎×1.5、半徑7m、11163101慢5%最多5層，不再手工加一次50%。
        var child = _combat.EffectiveSkillRow(defender, (int)baseChild.Id) is { } effective
            ? new SkillConfig(effective) : baseChild;
        if (!_shieldProcs.TryGetValue(defender, out var state) || state.Role != defender.Role.Id
            || state.Generation != defender.SceneGeneration || !ReferenceEquals(state.Attachment, defender.Attachment))
        {
            _shieldProcs.Remove(defender);
            state = new CompanionProcState(defender.Role.Id, defender.SceneGeneration, defender.Attachment);
            _shieldProcs.Add(defender, state);
        }
        long now = MonsterService.NowMs;
        if (state.ReadyMs > now) return;
        state.ReadyMs = now + (long)(Value(parent.DescData, 3) * 1000);
        var role = defender.Role; var attachment = defender.Attachment;
        long generation = defender.SceneGeneration, scene = defender.WorldScene;
        bool Current() => ReferenceEquals(defender.Role, role) && ReferenceEquals(defender.Attachment, attachment)
            && attachment?.Cancelled != true && defender.SceneGeneration == generation && defender.WorldScene == scene
            && !defender.Dead && !defender.SceneLoading
            && defender.Buffs.Any(b => b.ConfigId / 100 == ReflectDamageBuff && b.ExpireMs > MonsterService.NowMs);
        var node = JsonNode.Parse(child.Row.GetRawText())!.AsObject();
        node["_ro3CardReflection"] = true; // 共用不遞迴標記；沒有固定傷害欄位，仍以真子列計算攻防。
        var reflected = new SkillConfig(JsonSerializer.SerializeToElement(node));
        float radius = child.Range.Length > 0 ? (float)child.Range[0] / 100 : (float)Value(parent.DescData, 6);
        var targets = _combat.MonstersNear(defender, defender.Position, radius)
            .Select(m => (Monster: (MonsterUnit?)m, Player: (GameSession?)null,
                Distance: MonsterService.Distance(m.X, m.Z, defender.Position.X, defender.Position.Z)))
            .Concat(_sessions.All().Where(p => p.WorldScene == scene && _combat.Hostile(defender, p))
                .Select(p => (Monster: (MonsterUnit?)null, Player: (GameSession?)p,
                    Distance: MonsterService.Distance(p.Position.X, p.Position.Z, defender.Position.X, defender.Position.Z))))
            .Where(t => t.Distance <= radius).OrderBy(t => t.Distance).Take((int)child.TargetMax).ToArray();
        var playerLeases = targets.Select(t => t.Player).OfType<GameSession>().ToDictionary(p => p, CapturePassiveTarget);
        bool hit = false;
        foreach (var target in targets)
        {
            if (!Current()) break;
            if (target.Monster is { Hp: > 0, Removed: false } monster && monster.WorldScene == scene)
            {
                long before = monster.Hp;
                await _combat.CastExtraAsync(defender, reflected.Row, [monster], parametersApplied: true);
                hit |= monster.Hp < before;
            }
            else if (target.Player is { } player && playerLeases[player]() && _combat.Hostile(defender, player))
            {
                long before = _character.Stats(player)[2000];
                await _combat.CastExtraOnPlayerAsync(defender, reflected, player, applyHostileBuffs: true);
                hit |= _character.Stats(player)[2000] < before;
            }
        }
        // (inferred) 一波有效反傷只擲一次盾勢機率，不把五個AoE目標當成五次獨立觸發。
        if (hit && Current()) await _combat.RecoverReflectedShieldStanceAsync(defender);
    }

    private static Func<bool> CapturePassiveTarget(GameSession target)
    {
        var role = target.Role; var attachment = target.Attachment;
        long scene = target.WorldScene, generation = target.SceneGeneration;
        return () => role != null && ReferenceEquals(target.Role, role)
            && ReferenceEquals(target.Attachment, attachment) && attachment?.Cancelled != true
            && target.WorldScene == scene && target.SceneGeneration == generation
            && !target.SceneLoading && !target.Dead;
    }

}
