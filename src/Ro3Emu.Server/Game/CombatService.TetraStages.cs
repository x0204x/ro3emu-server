using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private static bool IsTetraStage(ActiveSkillCast cast) => cast.AuthoredStageCount == 4
        && new SkillConfig(cast.Row).ScriptLogic is "TetraVortex" or "TetraVortex2" or "TetraVortexcopy" or "TetraVortex2copy";

    // 說明依序水／火／地／風，EleConfig為5/4/3/2；原Buff2排列是暈／燃燒／石化／冰凍。
    // 元素對應狀態與最後冰凍保底的具體相位為inferred，不把五個tuple每段全部套上。
    private static readonly int[] TetraBuffIndices = [3, 1, 2, 0];

    private async Task ApplyTetraStageBuffAsync(GameSession source, ActiveSkillCast cast,
        GameSession? player, MonsterUnit? monster, Func<bool> current)
    {
        if (!IsTetraStage(cast) || !current() || player?.Dead == true || monster?.Dead == true) return;
        long targetId = player?.Role?.Id ?? monster?.Uid ?? 0;
        if (targetId == 0 || source.Role == null) return;
        var tuples = Tuples(cast.Row, "_kBuff2").ToArray();
        if (tuples.Length != 5) return;
        bool applied = await TryTetraBuffAsync(source, player, monster, tuples[TetraBuffIndices[cast.AuthoredStageIndex]], current);
        if (!current()) return;
        if (applied) cast.AuthoredStatusApplied.Add(targetId);
        if (LastAuthoredStage(cast) && !cast.AuthoredStatusApplied.Contains(targetId))
            if (await TryTetraBuffAsync(source, player, monster, tuples[4], current))
                cast.AuthoredStatusApplied.Add(targetId);
    }

    private async Task<bool> TryTetraBuffAsync(GameSession source, GameSession? player, MonsterUnit? monster,
        JsonElement tuple, Func<bool> current)
    {
        if (!current() || source.Role == null || player?.Dead == true || monster is { Hp: <= 0 }
            || !RollHostileBuff(tuple, source, null, player, monster, out int id, out int duration)) return false;
        long sourceId = source.Role.Id;
        ActiveBuff? Observe() => player != null
            ? player.Buffs.FirstOrDefault(b => b.ConfigId == id && b.SourceId == sourceId && b.ExpireMs > Now)
            : monster != null ? _buffs.UnitActiveBuff(monster, id, sourceId) : null;
        var before = Observe();
        if (player != null) await _buffs.AddSupportAsync(player, id, duration, sourceId);
        else if (monster != null) await _buffs.AddSupportAsync(monster, id, duration, sourceId);
        if (!current()) return false;
        var after = Observe();
        return after != null && !ReferenceEquals(before, after);
    }
}
