using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    internal readonly record struct AuthoredDamageStage(double Point, int Branch, int Split);

    /// <summary>原件說明確立真正段數；ShowDmg僅分割飄字，不能再乘傷害。
    /// 模板攻擊點對應後端段落是 inferred：SoulExpansion 前兩點，VectorShock 首／末點；
    /// PinpointAttack3 的十次10%及末次250%與350%總傷害、十一個點相互吻合。</summary>
    internal static AuthoredDamageStage[] AuthoredDamageStages(SkillConfig row, JsonElement? template)
    {
        if (template is not { } value) return [];
        var points = Numbers(value, "m_kAtkPoints");
        if (row.DamageParam1.Length == 4 && points.Length >= 4 && row.ScriptLogic is
            "TetraVortex" or "TetraVortex2" or "TetraVortexcopy" or "TetraVortex2copy")
            return [.. points.Take(4).Select((point, index) => new AuthoredDamageStage(point, index, 1))];
        if (row.DamageParam1.Length != 2) return [];
        return row.ScriptLogic switch
        {
            "SoulExpansion" or "SoulExpansion1" or "SoulExpansion_Copy" or "SoulExpansion1_Copy"
                when points.Length >= 2 => [new(points[0], 0, 1), new(points[1], 1, 1)],
            "VectorShock" or "VectorShock1" or "VectorShock_Copy" or "VectorShock1_Copy"
                when points.Length >= 2 => [new(points[0], 0, 3), new(points[^1], 1, 1)],
            "PinpointAttack3" when points.Length == 11 =>
                [.. points.Select((point, index) => new AuthoredDamageStage(point, index == 10 ? 1 : 0, 1))],
            _ => [],
        };
    }

    private bool TryScheduleAuthoredStages(GameSession source, ActiveSkillCast cast, JsonElement? template,
        Func<bool> current, Func<Task> finished, IReadOnlyList<MonsterUnit>? chosenTargets = null, int travelMs = 0)
    {
        // 上游引導技能有自己的排程；禁止再由本地多段排程重複觸發。
        if (ChannelScripts.Contains(Script(cast.Row))) return false;
        var stages = AuthoredDamageStages(new SkillConfig(cast.Row), template);
        if (stages.Length == 0) return false;
        cast.AuthoredStageCount = stages.Length;
        // 逐段串接仍使用原始絕對deadline；前段await不讓後段重入並覆寫該次段落。
        void Schedule(int index) => ScheduleCastDeadline(cast,
            ImpactOffset(template, stages[index].Point, cast.ChantMs, cast.ActionMs) + travelMs, async () =>
            {
                if (!current()) { await finished(); return; }
                cast.AuthoredStageIndex = index;
                cast.AuthoredStage = stages[index];
                cast.Resolved = true;
                TraceImpact(cast, $"stage-{index}-resolve-entered");
                if (chosenTargets == null) await ResolveCastAsync(source, cast);
                else await ApplyEffectsAsync(source, cast, cast.Request.TargetPos ?? cast.Origin,
                    chosenTargets: [.. chosenTargets.Where(m => !m.Removed)]);
                if (index + 1 < stages.Length && current()) Schedule(index + 1);
                else { cast.AuthoredStagesFinished = true; await finished(); }
            });
        Schedule(0);
        return true;
    }

    private static JsonElement AuthoredStageRow(ActiveSkillCast cast)
    {
        if (cast.AuthoredStageIndex < 0) return cast.Row;
        var node = JsonNode.Parse(cast.Row.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("Authored skill stage needs a config object.");
        node["_ro3DamageBranch"] = cast.AuthoredStage.Branch;
        node["_kShowDmg"] = new JsonArray(cast.AuthoredStage.Split);
        if (IsTetraStage(cast))
        {
            node["_iEleType"] = TetraElements[cast.AuthoredStageIndex];
            node["_kBuff2"] = new JsonArray(); // 每段只由Tetra owner套該元素狀態／必要的保底。
        }
        if (cast.AuthoredStageIndex > 0 || new SkillConfig(cast.Row).ScriptLogic == "VolleyArrows")
            node["_kBuff1"] = new JsonArray();
        if (new SkillConfig(cast.Row).ScriptLogic == "PinpointAttack3" && !LastAuthoredStage(cast))
            node["_ro3SkipStageDisplacement"] = true;
        return JsonSerializer.SerializeToElement(node);
    }

    private static bool LastAuthoredStage(ActiveSkillCast cast) => cast.AuthoredStageCount == 0
        || cast.AuthoredStageIndex == cast.AuthoredStageCount - 1;
}
