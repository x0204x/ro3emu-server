using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Tutorial guides (LC_GuideManager): the client runs them and reports 84000 UPDATE_GUIDE {guideIds, records} and 31210
/// CLIENT_COMPLETE_GUIDE {guildId (sic)}; the server keeps the finished set in DBGuideInfo (AUTO_SYNC 5055), which the
/// client reads at login so a finished guide does not run again. Without it every login replays the guides.
/// </summary>
public sealed class GuideService(RoleStore roles, Data.GameData data, ILogger<GuideService> log)
{
    public const uint GuideInfoSync = 5055;
    public const uint UpdateGuide = 84000;
    public const uint CompleteGuide = 31210;

    /// <summary>
    /// LC_DAL_Guide._allDBGuideInfoProcess walks finishedGuideIds / guideFinishRecord without nil checks, and empty
    /// fields arrive as nil (inferred from the bag datasets): a fresh role carries this id, which no GuideConfig row uses.
    /// </summary>
    private const long Placeholder = 0;

    public async Task LoginAsync(GameSession session)
    {
        var saved = roles.LoadGuides(session.Role!.Id);
        session.Guides = saved != null ? DBGuideInfo.Parser.ParseFrom(saved) : new DBGuideInfo();
        await SyncAsync(session);
    }

    public async Task UpdateAsync(GameSession session, UpdateGuideReq req)
    {
        if (session.Guides == null) return;
        foreach (var id in req.GuideIds.Take(MaxPerUpdate)) Finish(session, id);
        foreach (var (id, value) in req.Records.Take(MaxPerUpdate))
            if (Known(id)) session.Guides.GuideFinishRecord[id] = value;
        await SaveAndSyncAsync(session);
    }

    public async Task CompleteAsync(GameSession session, ClientCompleteGuideReq req)
    {
        if (session.Guides == null) return;
        Finish(session, req.GuildId);
        await SaveAndSyncAsync(session);
    }

    /// <summary>
    /// Ids the client can report: GuideConfig rows (any id when the table is absent). Whatever else a modified client sends
    /// would be saved with the role and sent back at every login, so it is dropped, and one update is cut at
    /// <see cref="MaxPerUpdate"/> entries (inferred; the client reports one guide or a handful).
    /// </summary>
    private bool Known(long id) => data.Table("GuideConfig").Count == 0 || data.Row("GuideConfig", id) != null;

    private const int MaxPerUpdate = 256;

    private void Finish(GameSession session, long id)
    {
        if (id != Placeholder && Known(id) && !session.Guides!.FinishedGuideIds.Contains(id)) session.Guides.FinishedGuideIds.Add(id);
    }

    private async Task SaveAndSyncAsync(GameSession session)
    {
        var guides = session.Guides!;
        roles.SaveGuides(session.Role!.Id, guides.ToByteArray());
        log.LogInformation("{Name} guides done: {Guides}", session.Role.Name, string.Join(",", guides.FinishedGuideIds));
        await SyncAsync(session);
    }

    private static Task SyncAsync(GameSession session)
    {
        var now = session.Guides!.Clone();
        if (now.FinishedGuideIds.Count == 0) now.FinishedGuideIds.Add(Placeholder);
        if (now.GuideFinishRecord.Count == 0) now.GuideFinishRecord[Placeholder] = 0;
        return CharacterService.SyncAsync(session, GuideInfoSync, now);
    }
}
