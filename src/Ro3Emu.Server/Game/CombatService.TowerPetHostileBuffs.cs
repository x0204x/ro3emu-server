using System.Text.Json;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>塔寵使用同一SkillConfig._kBuff2[狀態,機率,毫秒]，來源為實際type7寵物UUID。
    /// 命中結算後只對仍存活且留在同一副本的目標套用；tag抵抗與免疫沿共用BuffService。</summary>
    private async Task TowerPetHostileBuffsAsync(TowerPetUnit pet, MonsterUnit target, JsonElement row, Func<bool> current)
    {
        bool Valid() => current() && !target.Removed && !target.Dead && target.Hp > 0
            && target.WorldScene == pet.WorldScene && target.InstanceId == pet.InstanceId;
        foreach (var tuple in Tuples(row, "_kBuff2"))
        {
            if (!Valid()) return;
            if (RollHostileBuff(tuple, null, pet, null, target, out int id, out int duration))
                await _buffs.AddSupportAsync(target, id, duration, pet.Uid);
        }
    }
}
