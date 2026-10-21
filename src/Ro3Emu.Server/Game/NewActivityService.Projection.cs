using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class NewActivityService
{
    /// <summary>
    /// LC_ActivityParser_PronteraDefense calls pairs on global/city guild maps, and WorldExploration on node items. LC_PbHelper only initializes
    /// an empty map through an increment; a new nested map entry needs an all wrapper. Like SeasonRefineService,
    /// prime empty nested maps on a wire clone and immediately delete the zero keys. These are (inferred)
    /// transport initialization markers, never guilds, entitlements or persisted state.
    /// </summary>
    private async Task SyncProjectionAsync(GameSession session, DBNewActivityInfo state, DeadlineLease lease)
    {
        if (!session.Synced.ContainsKey(ActivitySync))
        {
            var wire = state.Clone();
            bool changed = false;
            if (wire.ActivityInfo.TryGetValue(118, out var activity)
                && activity.ActivityParam?.PronteraDefense?.GlobalInfo is { } prepared)
            {
                if (prepared.GuildInfo.Count == 0) { prepared.GuildInfo[0] = new DBPronteraDefenseGuildInfo(); changed = true; }
                foreach (var city in prepared.MapInfo.Values)
                {
                    if (city.DefenseGuilds.Count == 0) { city.DefenseGuilds[0] = false; changed = true; }
                    if (city.GuildContributionBoard.Count == 0) { city.GuildContributionBoard[0] = new DBPronteraDefenseGuildBoardInfo(); changed = true; }
                }
            }
            foreach (var exploration in wire.ActivityInfo.Values.Select(a => a.ActivityParam?.WorldExploration).OfType<DBWorldExploration>())
                foreach (var node in exploration.PhaseInfos.Values.SelectMany(p => p.NodeInfos.Values))
                    if (node.Items.Count == 0) { node.Items[0] = 0; changed = true; }
            if (changed) await CharacterService.SyncAsync(session, ActivitySync, wire);
            if (!_deadlines.IsCurrent(session, lease)) return;
        }
        await CharacterService.SyncAsync(session, ActivitySync, state);
    }

    /// <summary>Compatibility endpoints in msgtable; the current sender uses the result-bearing 82976 envelope.</summary>
    public async Task LevelAwardAsync(GameSession session, GetThreeDayLevelAwardReq req)
    {
        var ownerScope = _loop.Owners != null ? await EnterLocalWithGateAsync(session, "activity/level-award") : null;
        if (ownerScope == null) await session.ActivityGate.WaitAsync();
        try
        {
            int result = await ClaimSimpleAsync(session, 103, req.LevelAwardCfgId, false);
            // (inferred) The legacy placeholder is the only status scalar. Modern clients use outer Common.result.
            await session.SendGameAsync(LevelAward, new GetThreeDayLevelAwardRsp { Placeholder = result });
        }
        finally { session.ActivityGate.Release(); ownerScope?.Dispose(); }
    }

    public async Task FunctionPreviewAsync(GameSession session, GetThreeDayFunctionPreviewAwardReq req)
    {
        var ownerScope = _loop.Owners != null ? await EnterLocalWithGateAsync(session, "activity/function-preview") : null;
        if (ownerScope == null) await session.ActivityGate.WaitAsync();
        try
        {
            int result = await ClaimSimpleAsync(session, 104, req.FunctionPreviewCfgId, true);
            await session.SendGameAsync(FunctionPreviewAward, new GetThreeDayFunctionPreviewAwardRsp { Placeholder = result });
        }
        finally { session.ActivityGate.Release(); ownerScope?.Dispose(); }
    }
}
