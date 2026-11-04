using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's UnitConfig (data/config/UnitConfig.json): the unit an NPC is shown as.</summary>
[ConfigTable("UnitConfig")]
public readonly partial record struct UnitConfig(JsonElement Row)
{
    /// <summary>Walking speed in cm/s.</summary>
    [ConfigField("_fSpeed")] public partial double Speed { get; }
    [ConfigField("_iCollisionType")] public partial long CollisionType { get; }
    [ConfigField("_kCollisionSize")] public partial long[] CollisionSize { get; }
    [ConfigField("_iBulletBoxShape")] public partial long BulletBoxShape { get; }

    /// <summary>
    /// How far a hit reaches past this unit's centre: LC_UnitManager.GetNPCPrefabRadiusByUnitConfigId with its damage flag, which
    /// LC_Skill_ActionPreCheck adds to a skill's casting range (m_fModelDamageRadius). enum_ColliderType: 0 none, 1 box, 2 cylinder, 3 sphere.
    /// </summary>
    public float DamageRadius(NPCConfig npc, long minCircleRadius)
    {
        if (BulletBoxShape > 0) return 0;
        double ratio = npc.ModelRatio is > 0 and var r ? r : 1;
        if (CollisionType == 0) return npc.PrefabRadius * 0.01f;
        var size = CollisionSize;
        if (size.Length == 0) return 0;
        if (CollisionType == 1) return size.Length < 2 ? 0 : (float)(Math.Min(size[0], size[1]) * 0.5 * 0.01 * ratio);
        if (CollisionType is not (2 or 3)) return 0;
        float radius = (float)(size[0] * 0.01 * ratio);
        return radius <= minCircleRadius * 0.01f ? 0 : radius;
    }
}
