using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    internal List<Func<GameSession, GameSession, JsonElement, long, Task>> HealingApplied { get; } = [];
    internal List<Func<GameSession, ActiveSkillCast, JsonElement, Task>> SkillCastCompleted { get; } = [];

    private async Task CompleteCastOnceAsync(GameSession source, ActiveSkillCast cast, JsonElement? cooldownRow = null, bool primaryGuardian = false)
    {
        if (cast.CompletionPublished || !LastAuthoredStage(cast) || !CurrentCastMap(source, cast)
            || cast.Request.IsExtraSkill && !primaryGuardian) return;
        cast.CompletionPublished = true; // 在任一 await 前，以這次 cast SessionId 的物件記錄一次完成。
        await CompleteReadingSpellbookAsync(source, cast);
        foreach (var observer in SkillCastCompleted)
        {
            if (!CurrentCastMap(source, cast)) break;
            await observer(source, cast, cooldownRow ?? cast.Row);
        }
    }

    /// <summary>卡片只重置施放的模式；保留其他模式、其他技能與共同冷卻下限。</summary>
    internal static async Task ResetOwnCooldownAsync(GameSession s, SkillConfig skill)
    {
        int group = (int)skill.SkillId, mode = (int)skill.Mode;
        long now = Now, floor = Math.Max(now, s.SkillPublicCds.GetValueOrDefault(group));
        if (floor > now) s.SkillModeCds[(group, mode)] = floor;
        else s.SkillModeCds.Remove((group, mode));
        if (skill.CdModeGroup < 0)
        {
            if (floor > now) s.SkillCds[group] = floor;
            else s.SkillCds.Remove(group);
        }
        await s.SendGameAsync(SkillCdUpdate, new SkillCdNotify
        {
            Uuid = s.Role!.Id,
            Infos = { new SkillCdInfo { SkillId = group, SkillMode = s.SkillCooldownShownModes.GetValueOrDefault((group, mode), skill.CdModeGroup >= 0 ? (int)skill.CdModeGroup : mode),
                CdStartTimestampMs = now, CdEndTimestampMs = floor } },
        });
    }
}
