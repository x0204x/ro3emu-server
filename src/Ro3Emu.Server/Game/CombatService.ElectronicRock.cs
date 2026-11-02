using System.Runtime.CompilerServices;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private readonly ConditionalWeakTable<ActiveSkillCast, object> _precisionEnergyApplied = [];

    private void WireElectronicRock() => CastResolved.Add(PrecisionEnergyAsync);

    // 必須由死亡發布入口於第一個await前捕獲，不能在後繼PlayerDied observer才捕獲新角色。
    internal Func<Task> CapturePrecisionDeath(GameSession source)
    {
        var role = source.Role; var attachment = source.Attachment; var epoch = source.SkillProtocol;
        long scene = source.WorldScene, generation = source.SceneGeneration;
        bool Current() => role != null && ReferenceEquals(source.Role, role)
            && ReferenceEquals(source.Attachment, attachment) && attachment?.Cancelled != true
            && ReferenceEquals(source.SkillProtocol, epoch) && source.WorldScene == scene
            && source.SceneGeneration == generation && !source.SceneLoading && source.Dead;
        return () => _buffs.ResetPrecisionEnergyOnDeathAsync(source, Current);
    }

    // 11315說明每命中6次得1層；11130701明確改為4次，並非11315 mode1的授權。
    // 原單體只有一個AtkPoint，2／5段飄字不是兩次／五次能量事件。
    internal async Task PrecisionEnergyAsync(GameSession source, ActiveSkillCast cast,
        IReadOnlyList<Ro3Emu.Server.Maps.MonsterUnit> monsters, SkillHitInfo hit)
    {
        var skill = new SkillConfig(cast.Row);
        if (skill.SkillId != 11315 || cast.Request.IsExtraSkill || !PassiveCastCurrent(source, cast)
            || _precisionEnergyApplied.TryGetValue(cast, out _)
            || !hit.Dsts.Any(d => d.HitResult != DamageHitResult.Miss && monsters.Any(m => m.Uid == d.DstID))
                && !cast.PublishedPlayerHits.Any(h => h.Result != DamageHitResult.Miss)) return;
        if (source.Progress is not { } progress || !_skills.InTree(progress.Career, 11315)
            || _skills.Level(progress, 11315) <= 0 || _skills.Level(progress, 11310) <= 0) return;
        var original = SkillConfig.Get(_data, 1131500 + skill.Level);
        if (original == null) return;
        if (original.Value.Description is not { } description) return;
        // Description: [文字ID,色碼四欄,每N命中,每次充能]。
        int required = description.GetArrayLength() > 5 ? (int)(GameData.Number(description[5]) ?? 0) : 0;
        int reward = description.GetArrayLength() > 6 ? (int)(GameData.Number(description[6]) ?? 0) : 0;
        if (EmblemService.ResonanceSkills(source, _data).Contains(11130701)
            && SkillConfig.Get(_data, 11130701) is { } grant)
            required = int.TryParse(grant.DescData.ElementAtOrDefault(3), out int enhanced) ? enhanced : required;
        if (required <= 0 || reward <= 0) return;
        _precisionEnergyApplied.Add(cast, new object());
        await _buffs.AddPrecisionHitAsync(source, required, reward, () => PassiveCastCurrent(source, cast));
    }
}
