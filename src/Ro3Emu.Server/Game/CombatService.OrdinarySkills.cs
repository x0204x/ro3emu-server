using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    // 僅由伺服器多段技能排程建立；不接受封包指定傷害分支。
    private static int? ScheduledDamageBranch(JsonElement row) =>
        row.TryGetProperty("_ro3DamageBranch", out var value) && value.TryGetInt32(out int branch)
        && branch >= 0 && branch < new SkillConfig(row).DamageParam1.Length ? branch : null;

    private int? AuthoredSourceDamageBranch(GameSession source, JsonElement row) =>
        ScheduledDamageBranch(row) ?? (Script(row) == "HolyCross"
            // ItemConfig 的 5 為雙手矛、裝備部位 1；單手矛（4）不符合。
            ? SkillParameterRules.Wields(source, _data, 5) ? 1 : 0 : null);

    private int OrdinaryMonsterDamageBranch(GameSession source, Ro3Emu.Server.Maps.MonsterUnit target, JsonElement row)
    {
        if (AuthoredSourceDamageBranch(source, row) is { } authored) return authored;
        if (IsDualBranchHeal(row)) return 1;
        var skill = new SkillConfig(row);
        if (skill.SkillId is 11608 or 11611 or 11636) return 1; // 原表第二列是對魔物增幅。
        if (skill.ScriptLogic is "ProtectionAttack" or "ProtectionAttack2") return 1;
        if (skill.ScriptLogic == "Soul_Strike") return target.Template.Element == 10 ? 1 : 0;
        if (skill.ScriptLogic is "Magnus_Exorcismus" or "Magnus_ExorcismusBD")
            return target.Template.Race is 3 or 5 || target.Template.Element is 8 or 10 ? 1 : 0;
        return SonicBlowBranch(source, target, row);
    }

    private void WireOrdinarySkills()
    {
        CastResolved.Add(async (source, cast, _, hit) =>
        {
            if (Script(cast.Row) == "Smite3" && GameData.Int(cast.Row, "_iMode") == 0
                && hit.Dsts.Any(d => d.HitResult != Romsg.DamageHitResult.Miss && d.Damage > 0))
                await ConvertShieldStanceAsync(source, cast.Row);
        });
        PvpSkillHitPublished.Add(async (source, _, row, damage, result) =>
        {
            if (Script(row) != "Smite3" || GameData.Int(row, "_iMode") != 0 || damage <= 0
                || result == Romsg.DamageHitResult.Miss) return;
            await _buffs.RecoverEnergyOnHitAsync(source, row);
            await ConvertShieldStanceAsync(source, row);
        });
        SkillCastCompleted.Add(async (source, cast, row) =>
        {
            if (Script(row) == "HorizonSlash")
            {
                // 描述20%移速／5秒，Buff1130201也正是1131+2000；不要把空helper11318當效果。
                int duration = (int)(Numbers(row, "_kDescData").ElementAtOrDefault(5) * 1000);
                if (duration > 0) await _buffs.AddAsync(source, 1130201, duration, cast.OwnerId);
            }
            if (GameData.Int(row, "_iSkillID") != 11636 || GameData.Int(row, "_iMode") != 1) return;
            var enhanced = source.Buffs.FirstOrDefault(b => b.ConfigId == 1163101 && b.ExpireMs > Now);
            if (enhanced == null) return;
            if (enhanced.PileCount <= 1) await _buffs.RemoveAsync(source, enhanced);
            else await _buffs.SetPileCountAsync(source, enhanced.ConfigId,
                (int)Math.Clamp(enhanced.ExpireMs - Now, 1, int.MaxValue), enhanced.SourceId, enhanced.PileCount - 1);
        });
    }

    internal async Task RecoverReflectedShieldStanceAsync(GameSession source)
    {
        if (source.Progress is not { } progress || !_skills.InTree(progress.Career, 11636)
            || _skills.Level(progress, 11636) is not (> 0 and var level)
            || SkillConfig.Get(_data, _skills.RealId(11636, level)) is not { } skill) return;
        var desc = Numbers(skill.Row, "_kDescData");
        if (Rolls.Proc(10000) >= desc.ElementAtOrDefault(4) * 100) return;
        var current = CapturePlayerSkillTarget(source);
        await _buffs.RecoverEnergyOnHitAsync(source, skill.Row);
        if (current()) await ConvertShieldStanceAsync(source, skill.Row);
    }

    internal async Task ConvertShieldStanceAsync(GameSession source, JsonElement row)
    {
        // 以現行父技能的20層/3次為準；舊helper11623仍寫10層，不能覆蓋新版父技能。
        var desc = Numbers(row, "_kDescData");
        int required = (int)desc.ElementAtOrDefault(5), charges = (int)desc.ElementAtOrDefault(6);
        if (required <= 0 || charges <= 0 || !source.Energy.TryGetValue(141002, out var energy) || energy.Value < required) return;
        var current = CapturePlayerSkillTarget(source);
        if (await _buffs.ConsumeEnergyAmountAsync(source, 141002, required) && current())
            // (inferred) 下一個三次技能的狀態不採零時長的60秒通用fallback；死亡/離場仍遵守原Buff清除旗標。
            await _buffs.SetPileCountAsync(source, 1163101, int.MaxValue, source.Role!.Id, charges);
    }

    // 傷害發布會 await；狀態只能回到同一角色、連線與場景世代。
    private static Func<bool> CapturePlayerSkillTarget(GameSession player)
    {
        var role = player.Role;
        var attachment = player.Attachment;
        long generation = player.SceneGeneration, scene = player.WorldScene;
        return () => role != null && ReferenceEquals(player.Role, role)
            && ReferenceEquals(player.Attachment, attachment) && attachment?.Cancelled != true
            && player.SceneGeneration == generation && player.WorldScene == scene
            && !player.SceneLoading && !player.Dead;
    }

    private async Task CleanseSkillEffectsAsync(GameSession source, GameSession recipient, JsonElement row,
        Func<bool> currentSource)
    {
        var skill = new SkillConfig(row);
        string script = skill.ScriptLogic;
        bool cleanse = script is "Cleanse" or "Cleanse2";
        bool release = skill.SkillId == BuffService.ControlReleaseSkillGroup;
        if (!cleanse && !release && script is not ("Detoxify" or "HealingSkills")) return;
        var currentTarget = CapturePlayerSkillTarget(recipient);
        var categories = cleanse ? Numbers(row, "_kLogicData").ToHashSet() : [];
        // Cleanse 的參數是分類（6 沉默、5 黑暗等），不是 Buff ID。
        foreach (var active in recipient.Buffs.ToArray())
        {
            if (!currentSource() || !currentTarget()) break;
            if (active.IsPassive || BuffConfig.Get(_data, active.ConfigId) is not { } buff
                || buff.Type != 2 || buff.Disperse == 0) continue;
            bool remove = cleanse && buff.BuffCategory.Any(c => categories.Contains(c))
                || release && buff.BuffCategory.Contains(37)
                || script == "Detoxify" && buff.BuffId == 9030
                || script == "HealingSkills" && buff.BuffId is 9023 or 9025;
            if (remove) await _buffs.RemoveAsync(recipient, active);
        }
    }
}
