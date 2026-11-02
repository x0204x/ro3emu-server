using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>
    /// Skills that keep dealing damage while their caster walks: the description's total is the row's ratio times the show's
    /// attack points after the first. Myriad Stars Chase the Moon (Lv. 5): [20000], points 0, 0.5, 0.7 ... 1.7, "PATK * 1400%".
    /// Aerial Bombardment (Lv. 3): [30000], points 0.017, 1.65, 1.83 ... 2.67, "totaling PATK * 2100%". Other multi-point
    /// shows are single hits as far as their descriptions tell, so this is by name, as GuardianPetRules.Pulses (inferred).
    /// </summary>
    private static readonly HashSet<string> ChannelScripts = ["VolleyArrows", "HighAltitudeBombing"];

    /// <summary>
    /// A channelled row's hits: the show's point and the row it deals. The first point only opens the channel (inferred: no
    /// total above counts it). A second ratio is the last point's: Myriad Stars' talent row [20000,15000] has 11 hits, ten
    /// of 200% for its "2000" and "When the skill ends, it deals one instance of 150%" (SkillConfig 11336301).
    /// </summary>
    internal static (double Point, JsonElement Row)[] ChannelHits(JsonElement row, JsonElement? show)
    {
        if (!ChannelScripts.Contains(Script(row))) return [];
        double[] points = [.. SkillShow.AttackPoints(show).Skip(1)];
        if (points.Length == 0) return [];
        var skill = new SkillConfig(row);
        var hit = skill.WithoutBuff1();
        var last = skill.DamageParam1 is [_, var final, ..] ? new SkillConfig(hit).WithDamage([final], skill.DamageParam2) : hit;
        return [.. points.Select((point, index) => (point, index == points.Length - 1 ? last : hit))];
    }

    /// <summary>
    /// The first attack point of a channelled cast: its caster buffs, then one effect pass per later point while the cast
    /// lives (moving is allowed, _bMovement 1; an interrupt or the next cast ends it). The area follows the caster and keeps
    /// the cast's aim (inferred: the stretched main effect 1332003 is bound to the moving DRoot). Procs of "after using" a
    /// skill see the first hit only, so they fire once per cast.
    /// </summary>
    private async Task<bool> StartChannelAsync(GameSession s, ActiveSkillCast action)
    {
        var show = _data.SkillTemplate(new SkillConfig(action.Row).ScriptShow);
        var hits = ChannelHits(action.Row, show);
        if (hits.Length == 0) return false;
        action.AuthoredStageCount = hits.Length; // 保留施放生命期，直到最後一段與完成事件結束。
        foreach (var buff in new SkillConfig(action.Row).Buff1)
            if (buff is [var id, var chance, var duration, ..] && id > 0 && Rolls.Proc(10000) < chance
                && SkillBuffAllowed?.Invoke(s, s, (int)id) != false)
                await _buffs.AddAsync(s, (int)id, (int)duration, s.Role!.Id);
        // 沿上游的各攻擊點與傷害列排程；逐段串接可避免等待交易時同一施放重入。
        void Schedule(int index) => ScheduleCastDeadline(action,
            ImpactOffset(show, hits[index].Point, action.ChantMs, action.ActionMs), async () =>
            {
                if (!Live(s, action)) return;
                TraceImpact(action, $"channel-{index}-resolve-entered");
                await ChannelHitAsync(s, action, hits[index].Row, index == 0);
                if (!Live(s, action)) return;
                if (index + 1 < hits.Length) { Schedule(index + 1); return; }
                action.AuthoredStageIndex = index;
                action.AuthoredStagesFinished = true;
                await CompleteCastOnceAsync(s, action);
                if (Now >= action.StartMs + action.ChantMs + action.ActionMs && ReferenceEquals(s.SkillCast, action))
                    s.SkillCast = null;
            });
        Schedule(0);
        return true;
    }

    private Task ChannelHitAsync(GameSession s, ActiveSkillCast action, JsonElement row, bool first)
    {
        if (!Live(s, action)) return Task.CompletedTask;
        var request = action.Request.Clone();
        request.IsExtraSkill |= !first;
        var hit = new ActiveSkillCast
        {
            Id = action.Id, OwnerId = action.OwnerId, WorldScene = action.WorldScene, SceneGeneration = action.SceneGeneration,
            Attachment = action.Attachment, Request = request, Row = row, Origin = s.Position.Clone(),
            StartMs = action.StartMs, ChantMs = action.ChantMs, ActionMs = action.ActionMs, Operator = action.Operator,
            SourceEligible = () => Live(s, action),
            CompletionPublished = true, // 子命中不發布本地卡片完成事件；由原施放最後一段統一發布。
        };
        return ApplyEffectsAsync(s, hit, request.TargetPos ?? hit.Origin);
    }
}
