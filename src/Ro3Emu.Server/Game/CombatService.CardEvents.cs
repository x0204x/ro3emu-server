using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>原命中的傷害、HP 與死亡發布後才執行卡片追擊，避免追擊搶在來源命中之前。</summary>
    internal List<Func<GameSession, GameSession, JsonElement, long, DamageHitResult, Task>> PvpSkillHitPublished { get; } = [];
    internal List<Func<GameSession, MonsterUnit, JsonElement, long, DamageHitResult, Task>> MonsterSkillHitPublished { get; } = [];
    internal List<Func<MonsterUnit, GameSession, JsonElement, long, DamageHitResult, Task>> MonsterAttackHitPublished { get; } = [];

    /// <summary>卡片反射以已結算傷害為基底，不再次計算攻防；盾、保命、死亡與 PvP 計分仍走共用提交入口。
    /// 原服未揭露捨入及反射遇反射順序，本候選禁止反射再次觸發卡片反射（inferred）。</summary>
    internal Task ReflectCardDamageAsync(GameSession source, GameSession target, JsonElement skill, long amount, Func<bool> eligible)
    {
        if (amount <= 0 || !CanPlayerHit(source, target) || !eligible()) return Task.CompletedTask;
        var sourceRole = source.Role!;
        var targetRole = target.Role!;
        var sourceAttachment = source.Attachment;
        var targetAttachment = target.Attachment;
        long scene = source.WorldScene;
        long sourceGeneration = source.SceneGeneration, targetGeneration = target.SceneGeneration;
        bool Current() => CurrentPlayerHit(source, target, sourceRole, targetRole, scene)
            && ReferenceEquals(source.Attachment, sourceAttachment) && sourceAttachment?.Cancelled != true
            && ReferenceEquals(target.Attachment, targetAttachment) && targetAttachment?.Cancelled != true
            && source.SceneGeneration == sourceGeneration && target.SceneGeneration == targetGeneration
            && eligible();
        var node = JsonNode.Parse(skill.GetRawText())!.AsObject();
        node["_kDamageParam1"] = new JsonArray(0);
        node["_kDamageParam2"] = new JsonArray(1);
        node["_ro3GuardianFixedHit"] = amount; // 既有固定追加傷害入口；不是另一輪攻擊力計算。
        node["_ro3CardReflection"] = true;
        var row = JsonSerializer.SerializeToElement(node);
        return HitEventPlayersAsync(source, [target], row, (int)new SkillConfig(skill).Id,
            HitEffect(new SkillConfig(skill).ScriptShow), ++_sessionIds, Current);
    }
}
