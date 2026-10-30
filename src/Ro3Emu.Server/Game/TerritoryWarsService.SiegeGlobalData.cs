using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 30115 GLOBAL_OBJECT_DATA_NOTIFTY in the siege (romsg.GlobalObjectDataNotify, re/proto/MG_Dungeon.proto:330-354). The
/// siege UI knows a castle object only through it: LC_SiegeHelper.GetNpcUIData (Logic/Siege/LC_SiegeHelper.lua:300-357)
/// takes HP, max HP, position and camp from the GlobalObjectData at the object's point and counts the object dead when
/// there is none (l.246, LC_DunGeon_Helper.GetGlobalObjectDataByPointId); an entry with hp &lt;= 0 deletes it
/// (Logic/Scene/Component/LC_SceneCom_GlobalObjMgr.lua:82-138). LC_DunGeon_Siege._updateDestoryPerform (l.1280-1341)
/// shows the intact or the broken model of the DestoryModelPerformParams buildings (41-43 the bastions, 254-256 the
/// workshops, 273/274/299/308 the arrow towers) by whether an object stands at the point, so the markers 4903002 keep
/// the workshops and towers standing. Every spawned castle NPC has NPCConfig _iNeedNotifyGlobalData 1.
/// </summary>
public sealed partial class TerritoryWarsService
{
    private GlobalObjectData SiegeGlobalEntry(int point, MonsterUnit unit) => new()
    {
        GlobalObjId = unit.Uid, ConfigId = unit.Template.NpcId, Pos = new Vec3 { X = unit.X, Y = unit.Y, Z = unit.Z },
        Camp = NPCConfig.Get(_data, unit.Template.NpcId)?.Camp ?? 0, Hp = unit.Dead ? 0 : Math.Max(0, unit.Hp),
        MaxHp = unit.Template.MaxHp, DungeonPointId = point,
    };

    private bool SiegeGlobal(MonsterUnit unit) => NPCConfig.Get(_data, unit.Template.NpcId) is { NeedNotifyGlobalData: 1 };

    private static IEnumerable<GameSession> SiegeLoadedMembers(SiegeRun battle) => battle.Room.ActiveMembers
        .Select(m => m.Session).Where(s => ReferenceEquals(s.SiegeBattle, battle) && s.InstanceId == battle.Room.Id && !s.SceneLoading);

    /// <summary>The full set of standing objects on every load of the siege scene: the client clears its global objects
    /// with the scene.</summary>
    private Task SendSiegeObjectsAsync(GameSession s, SiegeRun battle)
    {
        var msg = new GlobalObjectDataNotify();
        foreach (var (point, unit) in battle.Objects)
            if (!unit.Dead && SiegeGlobal(unit)) msg.GlobalDatas[unit.Uid] = SiegeGlobalEntry(point, unit);
        return msg.GlobalDatas.Count == 0 ? Task.CompletedTask : Send(s, GlobalObjectNotify, msg);
    }

    /// <summary>
    /// The objects whose HP the loaded members have not seen yet: new ones, damaged or healed ones and dead ones (hp 0).
    /// Called by the siege clock (inferred: HP at most once a second, as the client's HUD refreshes it) and at once for
    /// a spawn or a death.
    /// </summary>
    private async Task SiegePublishObjectsAsync(SiegeRun battle)
    {
        var msg = new GlobalObjectDataNotify();
        foreach (var (point, unit) in battle.Objects)
        {
            if (!SiegeGlobal(unit)) continue;
            long hp = unit.Dead ? 0 : Math.Max(0, unit.Hp);
            if (battle.SentHp.TryGetValue(point, out long sent) && sent == hp) continue;
            battle.SentHp[point] = hp;
            msg.GlobalDatas[unit.Uid] = SiegeGlobalEntry(point, unit);
        }
        if (msg.GlobalDatas.Count > 0)
            await RoleFanout.EachAsync(SiegeLoadedMembers(battle), s => Send(s, GlobalObjectNotify, msg));
    }
}
