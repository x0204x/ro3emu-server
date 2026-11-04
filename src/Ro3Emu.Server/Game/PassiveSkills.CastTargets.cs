using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class PassiveSkills
{
    // 三族說明未限定魔物；沿用原 PvE「每次原施放只處理一次」政策（inferred）。
    // 不掛每個 PvP 命中事件：範圍、分段及追加攻擊不能倍增被動觸發次數。
    private async Task ResolveOwnCastPassivesAsync(GameSession s, ActiveSkillCast action,
        IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        bool Current() => _combat.PassiveCastCurrent(s, action);
        if (action.Request.IsExtraSkill || action.PassiveCastResolved || !Current()
            || processed.Count == 0 && action.PublishedPlayerHits.Count == 0) return;
        action.PassiveCastResolved = true;
        long owner = action.OwnerId;
        var cast = new SkillConfig(action.Row);
        // 明確指定的玩家優先於同次範圍內魔物；無指定時仍沿用舊 PvE 第一個目標政策。
        var monster = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId);
        var player = action.PublishedPlayerHits.FirstOrDefault(p => p.TargetId == action.Request.TargetId);
        if (monster == null && player == null)
        {
            monster = processed.Count > 0 ? processed[0] : null;
            if (monster == null) player = action.PublishedPlayerHits.FirstOrDefault();
        }
        bool TargetCurrent() => Current() && (monster != null
            ? !monster.Removed && !monster.Dead && monster.Hp > 0 && monster.WorldScene == action.WorldScene
            : player != null && player.Current() && _combat.Hostile(s, player.Target));
        bool Granted(long id, int group) => Current() && Learned(s, group)?.Skill.Id == id;
        Task Extra(JsonElement row, Func<bool> granted, bool parametersApplied = false)
        {
            if (!TargetCurrent() || !granted()) return Task.CompletedTask;
            if (monster != null) return _combat.CastExtraAsync(s, row, [monster], parametersApplied);
            return player is { } victim ? _combat.CastPassiveExtraOnPlayerAsync(s, row, victim.Target,
                () => TargetCurrent() && granted(), parametersApplied) : Task.CompletedTask;
        }

        // Buff 14311 的兩個門檻各擲一次；14312 Holy Spirit Surge 生效時兩段必發。
        // 14316 為 MATK 來源物理傷害，14317 為魔法傷害。
        if (cast.SkillId == Judex && Learned(s, DoubleHolyLight) is
            { Skill: var light, Own: { LogicData: [var firstChance, var first, var secondChance, var second, ..] } })
            foreach (var (chance, id) in new[] { (firstChance, first), (secondChance, second) })
                if (TargetCurrent() && Granted(light.Id, DoubleHolyLight)
                    && (s.Buffs.Any(b => b.ConfigId / 100 == HolySpiritSurgeGroup && b.ExpireMs > MonsterService.NowMs)
                        || Roll(10000) < chance)
                    && SkillConfig.Get(_data, id) is { } row)
                    await Extra(row.Row, () => Granted(light.Id, DoubleHolyLight));

        // 玩家以本次已提交且已發布的實際 HP 扣除判斷；全護盾吸收／miss 不當成傷害。
        bool dealt = hit.Dsts.Any(d => d.Damage > 0) || action.PublishedPlayerHits.Any(p => p.EffectiveDamage > 0);
        if (dealt && Current() && Learned(s, EnchantBlade) is { Own: { LogicData: [var defense, ..] } })
            await _buffs.AddAsync(s, (int)defense, 0, owner);

        if (cast.SkillId == SoulExpansion && dealt && TargetCurrent()
            && Learned(s, PsychokineticAstralBody) is { Skill: var astral } && AstralBodies(s) is { } bodies
            && s.Buffs.FirstOrDefault(b => b.ConfigId == bodies.Buff && b.ExpireMs > MonsterService.NowMs) is { } live
            && live.PileCount >= bodies.Launch && BuffConfig.Get(_data, bodies.Buff) is { LogicData: [var launch, ..] }
            && (_combat.EffectiveSkillRow(s, (int)launch) ?? SkillConfig.Get(_data, launch)?.Row) is { } body)
        {
            int count = live.PileCount;
            await _buffs.RemoveAsync(s, live);
            for (int i = 0; i < count && TargetCurrent() && Granted(astral.Id, PsychokineticAstralBody); i++)
                await Extra(body, () => Granted(astral.Id, PsychokineticAstralBody), parametersApplied: true);
        }
    }
}
