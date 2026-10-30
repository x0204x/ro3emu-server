using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The siege's damage rules by NPCConfig _kNpcTag (docs/systems/territory-wars.md "Damage by tag"). Two PublicParamConfig
/// rows the client never reads share one shape, [attacker tag, target tag, change / 10000] (inferred from both):
/// ArtilleryAttackCityGateReduceDamage [3002, 3003, -9000] and GuardAttackOtherReduceDamage [3004, 3005, -5000]. Tag 3002
/// is every siege cannon (4900013-4900016, 4900023-4900027) and its bullets (4900101-4900108, NPCType 10); 3003 the City
/// Gate, the ram, the ladders, the Obstacles and the Barrier Stele 4905012; 3004 the archers 4901001; 3005 the ladders.
/// A cannon shot on a gate, ram, ladder or barricade so deals a tenth. A player's cast carries a cannon's tags when its
/// skill is that cannon's (GVGCannonConfig _iSkillID); the war vehicles' seat skills carry none.
/// </summary>
public sealed partial class TerritoryWarsService
{
    private static readonly string[] SiegeTagDamageParams = ["ArtilleryAttackCityGateReduceDamage", "GuardAttackOtherReduceDamage"];
    private const long BulletNpcType = 10;
    /// <summary>Skill id -> the NPC tags its casts carry (the cannons of that skill and its bullet NPC); read once.</summary>
    private Dictionary<long, long[]>? _skillTags;

    private Dictionary<long, long[]> SkillTags()
    {
        if (_skillTags != null) return _skillTags;
        var tags = new Dictionary<long, HashSet<long>>();
        foreach (var cannon in GVGCannonConfig.All(_data))
        {
            if (!tags.TryGetValue(cannon.SkillId, out var set)) tags[cannon.SkillId] = set = [];
            if (NPCConfig.Get(_data, cannon.NpcId) is { } npc) set.UnionWith(npc.NpcTag);
            // SkillConfig _kLogicData[2] is the skill's bullet NPC (TerritoryWarsService.CannonShots.cs).
            if (SkillConfig.Get(_data, cannon.SkillId) is { LogicData: [_, _, var bullet, ..] }
                && NPCConfig.Get(_data, bullet) is { NPCType: BulletNpcType } shot) set.UnionWith(shot.NpcTag);
        }
        return _skillTags = tags.ToDictionary(t => t.Key, t => t.Value.ToArray());
    }

    /// <summary>The change of [attacker tag, target tag, change] rows that match, applied one after the other.</summary>
    private long TagDamage(long[] source, long[] target, long damage)
    {
        foreach (string name in SiegeTagDamageParams)
            if (ParamArray(name) is [var from, var to, var change] && source.Contains(from) && target.Contains(to))
                damage = damage * (10000 + change) / 10000;
        return damage;
    }

    /// <summary>CombatService.DamageAdjusters: a player's cast on a monster with the NPC tags of its source.</summary>
    private long SiegeTagDamage(MonsterUnit target, JsonElement row, long damage) =>
        SkillTags().GetValueOrDefault(new SkillConfig(row).Id) is { Length: > 0 } source
        && NPCConfig.Get(_data, target.Template.NpcId)?.NpcTag is { Length: > 0 } tags
            ? TagDamage(source, tags, damage) : damage;
}
