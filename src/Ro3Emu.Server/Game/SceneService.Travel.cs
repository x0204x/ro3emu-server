using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What a client may ask for with 30012 and 30131 (ServerOptions.TravelCheck, docs/systems/anticheat.md). The client's
/// map panel and tracker walk through portals and Kafra, which the server checks at the NPC; a raw travel request has no
/// NPC, so it is held to what the client sends one for. (inferred: the original server is not known, only its client.)
/// <list type="bullet">
/// <item>The role's own map: a failed load's retry.</item>
/// <item>The main city, from anywhere: a failed load (LC_TransitionBase: 10100), leaving Territory Wars
/// (LC_TerritoryWarsServer: 10100), leaving auto battle (LC_DAL_AutoAttack: AutoBattleAutoBackMainCityID) and the idle
/// tip's Return (UI_Sys_Main_OnHookTipWidgetCtrl: OnHookReturnMap, outside a city).</item>
/// <item>A Territory Wars map (_iSceneType 8): EnterTerritoryWarsMap from the map panel, an NPC and a guild's escort
/// invite. The client gates those panels only, so a low level is not refused here.</item>
/// <item>The GvG transfer's 10110, from a dungeon or a Territory Wars map (UI_Sys_GVG_TransferNodeWidgetCtrl, LC_DunGeon_GVG).</item>
/// <item>Any map while flying: FindPathAcrossMapNew crosses scenes with 30012 only when the player m_bFlying, on foot it
/// walks MapLinkData and Kafra instead. The server knows the flight (31083, which needs the fly buff).</item>
/// </list>
/// SceneTeleportReason_TaskOnlyTemp comes from the JumpMap action (ActionConfig 14), which no table uses, and the other
/// reasons say nothing the client's target does not, so the reason is reported and never trusted.
/// </summary>
public sealed partial class SceneService
{
    private const long FailedLoadCity = 10100, GvgTransferTarget = 10110, TerritoryWarsSceneType = 8;
    private const string JumpMapArea = "141";

    private HashSet<(long Scene, long Point)>? _authoredJumps;

    /// <summary>The check's answer for this request: go on, or refuse it; a refusal is counted either way.</summary>
    private bool TravelPasses(GameSession s, bool allowed, string detail)
    {
        string mode = options.Value.TravelCheck;
        if (allowed || mode == "off") return true;
        cheats.Refused(s, CheatMonitor.Travel, detail);
        return mode != "enforce";
    }

    internal bool TravelAllowed(GameSession s, long target) =>
        target == s.SceneEntry || IsMainCity(target) || s.WorldState.Flying
        || MapConfig.Get(data, target)?.SceneType == TerritoryWarsSceneType
        || target == GvgTransferTarget && (MapConfig.Get(data, s.SceneEntry) is not { } here || here.SceneType == TerritoryWarsSceneType);

    private bool IsMainCity(long target) => target == FailedLoadCity
        || data.Table("PublicParamConfig") is var parameters
            && (parameters.TryGetValue("AutoBattleAutoBackMainCityID", out var city) && GameData.Number(city) == target
                || parameters.TryGetValue("OnHookReturnMap", out var idle) && idle.ValueKind == JsonValueKind.Array
                    && idle.GetArrayLength() > 0 && GameData.Number(idle[0]) == target);

    /// <summary>
    /// 30131 goes where an authored action 141 (JumpMapArea, LC_InteractActions) says: a task's completion or tracker, the
    /// end of a dialogue or of a typewriter text. Every target is in the tables, so a request for another is not the client's.
    /// </summary>
    internal HashSet<(long Scene, long Point)> AuthoredJumps() => _authoredJumps ??= [.. JumpActions()];

    private IEnumerable<(long Scene, long Point)> JumpActions()
    {
        var rows = TaskConfig.All(data).SelectMany(t => new[] { t.EXECAction, t.Track })
            .Concat(DialogueConfig.All(data).Select(d => d.EXECAction))
            .Concat(TypewriterConfig.All(data).Select(t => t.Action));
        foreach (var slots in rows)
        {
            if (slots is not { ValueKind: JsonValueKind.Array } list) continue;
            foreach (var slot in list.EnumerateArray())
                if (slot.ValueKind == JsonValueKind.Array && slot.GetArrayLength() >= 3 && Text(slot[0]) == JumpMapArea
                    && long.TryParse(Text(slot[1]), CultureInfo.InvariantCulture, out long scene)
                    && long.TryParse(Text(slot[2]), CultureInfo.InvariantCulture, out long point))
                    yield return (scene, point);
        }

        static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }
}
