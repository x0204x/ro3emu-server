using Romsg;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 31401 appraisal (LC_ItemProtoParse.lua:271-293): the same owned bag GUID becomes
/// appraised through itemInfos[3], persisted in InvItem.Attrs and sent in 5009 before the full response items.
/// </summary>
public sealed class AppraisalService(GameData data, CharacterService character, FunctionService functions, RoleStore roles, ILogger<AppraisalService> log)
{
    public const uint Appraise = 31401;

    /// <summary>After a committed appraisal and its reply: the appraised instances (achievements 302006/302008).
    /// Listeners must not take the character lock synchronously (it is held); they post their work.</summary>
    public List<Action<GameSession, IReadOnlyList<InvItem>>> Appraised { get; } = [];

    /// <summary>
    /// (inferred) Validate the whole distinct, nonempty batch before changing any item; no additional batch cap or
    /// appraisal fee is invented from absent server rules. Function 7 gates this equipment operation. Keep the base
    /// stats already rolled at creation; generate entries/stunts from the client pools on the detached candidates.
    /// </summary>
    public async Task AppraiseAsync(GameSession session, EquipAppraiseReq req)
    {
        using var _ = await CharacterService.Lock(session);
        session.Owners!.Current!.BindPresentation(session);
        int result = !functions.IsOpen(session, 7) ? ErrorCodes.FeatureUnavailable
            : req.ItemGuids.Count == 0 || req.ItemGuids.Distinct().Count() != req.ItemGuids.Count ? ErrorCodes.Parameter : 0;
        var targets = new List<InvItem>();
        if (result == 0)
            foreach (long guid in req.ItemGuids)
            {
                var item = session.Items.FirstOrDefault(i => i.Guid == guid);
                result = item == null ? ErrorCodes.ItemMissing
                    : data.Row("ItemConfig", item.Entry) is not { } row || GameData.Int(row, "_iType") != 5 ? ErrorCodes.NotEquipment
                    : EquipmentAffixes.Config(item.Entry, data) == null ? ErrorCodes.ItemNoConfig
                    : item.Grid != 0 ? ErrorCodes.EquipNotInBag
                    : item.Stack != 1 || CharacterService.IsAppraised(item) ? ErrorCodes.AppraiseParameter : 0;
                if (result != 0) break;
                targets.Add(item!);
            }

        var resp = new EquipAppraiseResp { Result = result };
        if (result == 0)
        {
            var candidates = targets.Select(i => i.Clone()).ToList();
            foreach (var item in candidates)
            {
                EquipmentAffixes.Appraise(item, data);
            }
            var after = session.Items.Select(i => i.Clone()).ToList();
            foreach (var candidate in candidates) after.Single(i => i.Guid == candidate.Guid).Attrs = candidate.Attrs;
            var tx = roles.NewTransaction();
            tx.ItemDelta(session.Role!.Id, session.Items, after);
            tx.WithEconomy(character.FamilyEconomy(session, "cd13a/appraisal-batch", [], [],
                new { Items = candidates.Select(i => new { i.Guid, i.Entry, i.Attrs }).ToArray() }));
            try
            {
                await roles.CommitAsync(tx, "equipment/appraisal", _ =>
                {
                    for (int i = 0; i < targets.Count; i++) targets[i].Attrs = candidates[i].Attrs;
                });
            }
            catch (SqliteException e)
            {
                log.LogError(e, "Appraisal persistence failed for {Name}", session.Role!.Name);
                result = resp.Result = ErrorCodes.Unknown;
            }
            if (result == 0)
            {
                foreach (var target in targets) resp.AppraiseResult.Add(character.ItemInfoOf(target));
                await character.ItemsChangedAsync(session, stats: false);
            }
        }
        log.LogInformation("{Name} appraises {Count} items: {Result}", session.Role!.Name, req.ItemGuids.Count, result);
        await session.SendGameAsync(Appraise, resp);
        if (result == 0)
            foreach (var handler in Appraised)
                try { handler(session, targets); }
                catch (Exception e) { log.LogError(e, "Appraised hook failed for {Name}", session.Role!.Name); }
    }
}
