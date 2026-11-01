using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private sealed class DeathBoundWindow
    {
        public long BuffGuid;
        public long Next;
        public long RoleId;
    }
    private readonly ConditionalWeakTable<GameSession, DeathBoundWindow> _deathBoundWindows = new();

    private void WireFormulaEvents()
    {
        PvpSkillHitPublished.Add(DeathBoundPublishedAsync);
        SkillCastCompleted.Add(ApplyExplosionAftercostAsync);
    }

    /// <summary>主技11311說明限敵對玩家近戰命中；Buff._kLogicData指向11381。
    /// 主技12..20%與child1200..2000一致，Buff提示的60..100%是不同數值，故不採用。
    /// 使用實際HP損失、禁止反射互觸與取整沿卡片反射入口（inferred）。</summary>
    private async Task DeathBoundPublishedAsync(GameSession attacker, GameSession victim, JsonElement incoming, long effective, DamageHitResult result)
    {
        if (effective <= 0 || result == DamageHitResult.Miss || victim.Role == null || victim.Dead || attacker.Dead
            || new SkillConfig(incoming).DistanceType != 1 || incoming.TryGetProperty("_ro3CardReflection", out _)
            || !CanPlayerHit(attacker, victim)) return;
        var active = victim.Buffs.FirstOrDefault(b => b.ConfigId / 100 == 11311 && b.ExpireMs > Now);
        if (active == null || BuffConfig.Get(_data, active.ConfigId) is not { LogicData: [var child] }
            || SkillConfig.Get(_data, child) is not { ScriptLogic: "DeathBound2" } skill
            || SkillConfig.Get(_data, active.ConfigId) is not { ScriptLogic: "DeathBound" } primary
            || skill.DamageParamExtra is not [[1, 3, 0, 1, > 0, 1, > 0]]) return;
        var tuple = skill.DamageParamExtra[0]; var description = Numbers(primary.Row, "_kDescData");
        if (tuple[4] != tuple[6] || description.Length < 5 || description[3] * 100 != tuple[4]) return;
        var state = _deathBoundWindows.GetOrCreateValue(victim);
        if (state.RoleId == victim.Role.Id && state.BuffGuid == active.Guid && state.Next > Now) return;
        long amount = (long)Math.Max(0, Math.Floor(effective * (double)tuple[4] / 10000));
        if (amount <= 0) return;
        state.RoleId = victim.Role.Id; state.BuffGuid = active.Guid; state.Next = Now + (long)(description[4] * 1000);
        await PublishDeathBoundAsync(victim, skill, amount);
    }

    private async Task PublishDeathBoundAsync(GameSession source, SkillConfig skill, long amount)
    {
        var role = source.Role; var attachment = source.Attachment;
        long scene = source.WorldScene, generation = source.SceneGeneration;
        bool Current() => role != null && ReferenceEquals(source.Role, role) && !source.Dead && !source.SceneLoading
            && source.WorldScene == scene && source.SceneGeneration == generation
            && ReferenceEquals(source.Attachment, attachment) && attachment?.Cancelled != true;
        // child的600cm執行範圍與主文字5m不一致；採SkillConfig，最近者優先（inferred）。
        var targets = CardSplashTargets(source, source.Position, 0, (float)skill.Range.FirstOrDefault() / 100,
            (int)skill.TargetMax);
        var node = JsonNode.Parse(skill.Row.GetRawText())?.AsObject();
        if (node == null || !Current()) return;
        node["_kDamageParam1"] = new JsonArray(0); node["_kDamageParam2"] = new JsonArray(1);
        node["_ro3GuardianFixedHit"] = amount; node["_ro3CardReflection"] = true;
        // 子技11381沿用主技的自身視覺選擇器；事件owner已選敵人，不能把它解讀成自我治療。
        node["_ro3HostileOnly"] = true;
        node["_iRangeType"] = SkillRelease.SingleTarget; // 已共同選好五目標，不得再自動選第二批玩家。
        var row = JsonSerializer.SerializeToElement(node);
        if (targets.Monsters.Length > 0) await CastExtraAsync(source, row, targets.Monsters);
        if (Current()) await CastCardSplashPlayersAsync(source, row, targets.Players, amount, Current);
    }
}
