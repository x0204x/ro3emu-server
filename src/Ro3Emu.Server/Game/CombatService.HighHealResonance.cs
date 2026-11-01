using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private sealed record PoemShieldCooldown(long Role, object Epoch, long Ready);
    private readonly ConditionalWeakTable<GameSession, PoemShieldCooldown> _poemShields = new();

    internal async Task ReconcileHighHealResonanceAsync(GameSession source)
    {
        var current = CapturePlayerSkillTarget(source);
        if (EmblemService.ResonanceSkills(source, _data).Contains(11430401)) return;
        foreach (var buff in source.Buffs.Where(b => b.ConfigId == 11430501).ToArray())
        {
            if (!current() || EmblemService.ResonanceSkills(source, _data).Contains(11430401)) return;
            await _buffs.RemoveAsync(source, buff);
        }
    }

    private static bool IsHighHealFamily(JsonElement row) => Script(row) is "HighHeal" or "S1_Poem" or "Clarify";
    private bool HighHealAlly(GameSession source, GameSession target) => target.Role != null && !target.Dead
        && !target.SceneLoading && target.WorldScene == source.WorldScene && (target == source || !CanPlayerHit(source, target));

    // HighHeal與S1_Poem原文均指定「自身與指定單一我方」。自己已是目標時不能再加一次。
    private static void AddHighHealCaster(GameSession source, JsonElement row, List<GameSession> allies)
    {
        if (Script(row) is "HighHeal" or "S1_Poem" && allies.Count > 0 && !allies.Contains(source))
            allies.Insert(0, source);
    }

    private bool ReservePoemShield(GameSession source, ActiveSkillCast cast, bool hasRecipient)
    {
        if (Script(cast.Row) != "S1_Poem" || !hasRecipient || !CurrentCastMap(source, cast)) return false;
        // Buff11430601 LogicData=[5000,5000]與技能描述一致：只限制盾，不限制每次的治療。
        long cooldown = BuffConfig.Get(_data, 11430601)?.LogicData.FirstOrDefault() ?? 0;
        if (cooldown <= 0) return false;
        if (_poemShields.TryGetValue(source, out var old) && old.Role == source.Role!.Id
            && ReferenceEquals(old.Epoch, source.SkillProtocol) && old.Ready > Now) return false;
        _poemShields.AddOrUpdate(source, new(source.Role!.Id, source.SkillProtocol, Now + cooldown));
        return true;
    }

    private async Task HighHealResonanceAsync(GameSession source, ActiveSkillCast cast, SkillHitInfo hit)
    {
        if (cast.Request.IsExtraSkill || !LastAuthoredStage(cast) || new SkillConfig(cast.Row).SkillId != 14301
            || !hit.Dsts.Any(d => d.DamageType == DamageType.Heal)
            || !EmblemService.ResonanceSkills(source, _data).Contains(11430401)
            || SkillConfig.Get(_data, 11430401) is not { DescData: [var seconds, var stacks] }
            || !int.TryParse(seconds, out int duration) || !int.TryParse(stacks, out int maximum)) return;
        // (inferred) 一次高階治癒術的自身＋指定目標是同一次命中事件，只取得一層，避免自己也被治療時翻倍。
        var previous = source.Buffs.FirstOrDefault(b => b.ConfigId == 11430501 && b.SourceId == cast.OwnerId && b.ExpireMs > Now);
        int count = Math.Min(maximum, (previous?.PileCount ?? 0) + 1);
        await _buffs.SetPileCountAsync(source, 11430501, duration * 1000, cast.OwnerId, count);
        if (!CurrentCastMap(source, cast) || !EmblemService.ResonanceSkills(source, _data).Contains(11430401)
            || count < maximum) return;
        var rhythm = source.Buffs.FirstOrDefault(b => b.ConfigId == 11430501 && b.SourceId == cast.OwnerId);
        if (rhythm != null) await _buffs.RemoveAsync(source, rhythm);
        if (!CurrentCastMap(source, cast) || _data.Row("SkillConfig", 11430501) is not { } echo) return;
        var skill = new SkillConfig(echo);
        var allies = _world.PlayersIn(source.WorldScene).Where(p => HighHealAlly(source, p)
            && Relation(echo, true, p == source) && Distance(p.Position, source.Position) <= skill.Range.FirstOrDefault() / 100d)
            .OrderByDescending(p => p == source).ThenBy(p => Distance(p.Position, source.Position))
            .Take((int)skill.TargetMax).ToArray();
        await CastExtraOnAlliesAsync(source, echo, allies);
    }
}
