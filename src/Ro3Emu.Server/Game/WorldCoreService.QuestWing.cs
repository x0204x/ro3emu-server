using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class WorldCoreService
{
    internal const int TaskJumpMapAction = 112;

    /// <summary>
    /// 112 TaskJumpMap [112, map, point] with the client's [104] RebuildAutoMovePath and [106] UseWingEnd: the Quest Fly
    /// Wings 1002005..1002009 (PublicParamConfig.FlyWinTypeItem) jump to a scene-editor point, the format of a quest's
    /// _kTrack 142 PathFindingNormal [map, target] (1002006 [112,10220,1030301] is quest 103030's own track). The client
    /// sends them as a Fly Wing (LC_ItemHelper.UseItemForFly: no parameter, refused there when the current map's
    /// MapConfig._iFly is not 1) and, after the reply, rebuilds the cached auto-move path and ends the wing. Refusals
    /// before the item is spent are the Fly Wing's (WingError: an instance, a scene load, a map without _iFly) and a
    /// destination the map does not have. The jump follows the reply, as a wing's does, through the quest teleport
    /// (SceneService.TaskTrackTeleportAsync: another map's scene entry, or a teleport in this one).
    /// (inferred) ItemUseLimitConfig 1001 (_kUseLimit, maps 10110/10111) is read by neither the client nor this server.
    /// </summary>
    private async Task<int> UseQuestWingAsync(ItemActionUse use)
    {
        var s = use.Session;
        long map = use.Arg(1), point = use.Arg(2);
        if (s.InstanceId != 0 || s.SceneLoading) return ErrorCodes.FeatureUnavailable;
        if (_data.Row("MapConfig", s.SceneEntry) is not { } here || GameData.Int(here, "_iFly") != 1) return ErrorCodes.FeatureUnavailable;
        if (_data.Row("MapConfig", map) == null || _worldData.Map(map)?.Points.ContainsKey(point) != true) return ErrorCodes.ItemNoConfig;
        if (!await _character.CommitItemActionAsync(use, null, null, new { Map = map, Point = point })) return ErrorCodes.ItemsShort;
        use.AfterReply = () => _scene.TaskTrackTeleportAsync(s, map, point);
        return 0;
    }
}
