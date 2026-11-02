using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class SceneService
{
    /// <summary>Custom data ScenePointId (Config/Define/Lua_CustomDataDefine.lua): LC_InteractCondition.CheckTreasureIsHunt
    /// offers the NPC's interaction only while it equals SeasonTreasureHunt.iPointId, the hunt's 5064 point.</summary>
    private const long ScenePointIdCustomData = 105;
    private const int TreasureNpcId = 21070002;
    /// <summary>Treasure hunt NPC UIDs: 2^46 + role id, below 2^53 (the client keeps unit ids in a double array, see
    /// DungeonService.SceneActorUidBase).</summary>
    internal const long TreasureUidPrefix = 1L << 46;
    internal Func<GameSession, Task<int>>? TreasureComplete { get; set; }

    private static long TreasureUid(GameSession session) => TreasureUidPrefix | session.Role!.Id;

    /// <summary>The hunt's configured map point must exist in the actual scene-editor map export.</summary>
    internal (long Scene, MapPoint Position)? TreasurePoint(long pointId)
    {
        if (data.Row("TreasureHuntMapPointConfig", pointId) is not { } row) return null;
        long mapId = Data.GameData.Int(row, "_iMapId"), mapPoint = Data.GameData.Int(row, "_iMapPointId");
        if (worldData.Map(mapId) is not { } map || !map.Points.TryGetValue(mapPoint, out var position)) return null;
        // A few authored points float above the ground (1033 by 3.5 m). The client measures the dig spot in three dimensions
        // against _iInteractDistance 2 (LC_InteractManager), so without this no Dig button ever shows there.
        return (mapId, position with { Y = map.Nav?.WalkHeight(position.X, position.Z, position.Y) ?? position.Y });
    }

    internal MapPoint? ConfiguredPoint(long sceneEntry, long pointId) =>
        worldData.Map(sceneEntry) is { } map && map.Points.TryGetValue(pointId, out var point) ? point : null;

    /// <summary>On scene load or map use, make this player's active hunt target visible as a real NPC entity.</summary>
    internal async Task SpawnTreasureNpcAsync(GameSession session)
    {
        // (inferred) 配置點都在公開野外，尋寶投影不得進入同圖的副本或活動房間。
        if (session.Role == null || session.SceneLoading || session.RuntimeMap.Kind != RuntimeMapKind.PublicLine) return;
        var hunt = session.ActivityRuntime.AdventureTreasure;
        if (hunt.PointId == 0 || hunt.Began + 10800 <= clock.GetUtcNow().ToUnixTimeSeconds()
            || TreasurePoint(hunt.PointId) is not { } target || target.Scene != session.SceneEntry
            || data.Row("NPCConfig", TreasureNpcId) is not { } npcRow) return;
        var npc = new WorldNpc(TreasureUid(session), target.Scene, WorldNpcKind.Npc, TreasureNpcId,
            data.ClientTextOf(npcRow, "_kName"), (int)Data.GameData.Int(npcRow, "_iCamp"),
            target.Position.X, target.Position.Y, target.Position.Z, target.Position.Face, default);
        var entity = NpcEntity(npc);
        entity.CustomDatas.Add(new CustomData
        {
            Id = ScenePointIdCustomData,
            CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = hunt.PointId },
        });
        var view = new AoIViewChangeData();
        view.Bases.Add(entity);
        await session.SendGameAsync(GameAoiSink.SyncAoiView, view);
    }

    internal Task RemoveTreasureNpcAsync(GameSession session)
    {
        var view = new AoIViewChangeData();
        view.LeaveAoi.Add((ulong)TreasureUid(session));
        return session.SendGameAsync(GameAoiSink.SyncAoiView, view);
    }

    internal Task<MonsterUnit?> SpawnTreasureMonsterAsync(GameSession session, int npcId, MapPoint at) =>
        // 沿用挖掘者的公開分線；只傳配置地圖 ID 會把所有怪物放到第 1 線。
        session.RuntimeMap.Kind == RuntimeMapKind.PublicLine
            ? monsters.SpawnSingleAsync(session.RuntimeMap, npcId, at)
            : Task.FromResult<MonsterUnit?>(null);

    internal Task RemoveTreasureMonsterAsync(MonsterUnit monster) => world.RemoveUnitAsync(monster);

    internal bool HasTreasureMonster(long uid) => world.AllUnits().Any(u => u.Uid == uid && u is MonsterUnit { Dead: false });

    /// <summary>The dig: the client sends the NPC's own NPCConfig._iInteractID. Only the owner's dynamic UID takes this
    /// branch, so the same interaction on a static NPC keeps its normal route.</summary>
    private async Task<bool> TryTreasureInteractAsync(GameSession session, NpcInteractionReq req)
    {
        if (session.Role == null || req.NpcUid != TreasureUid(session)
            || Data.Config.NPCConfig.Get(data, TreasureNpcId) is not { } npc || !npc.InteractId.Contains(req.InteractionId)) return false;
        var hunt = session.ActivityRuntime.AdventureTreasure;
        int result = hunt.PointId == 0 || session.RuntimeMap.Kind != RuntimeMapKind.PublicLine
            || TreasurePoint(hunt.PointId) is not { } target
            ? ErrorCodes.NpcMissing
            : session.SceneLoading || target.Scene != session.SceneEntry ? ErrorCodes.SwitchingScenes
            : MonsterService.Distance(session.Position.X, session.Position.Z, target.Position.X, target.Position.Z) > 4f + InteractSlack
                ? ErrorCodes.NpcTooFar : TreasureComplete == null ? ErrorCodes.FeatureUnavailable
                : await TreasureComplete(session);
        await session.SendGameAsync(NpcInteraction, new NpcInteractionRsp { Result = result, Req = req });
        return true;
    }
}
