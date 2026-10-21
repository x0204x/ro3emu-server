using System.Text;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

public sealed partial class EmblemService
{
    public const uint SwitchPlan = 82425, RenamePlan = 82426;
    // PublicParamConfig.SchemeNum / SchemeNameNumMax / SchemeCD; server enforcement is (inferred).
    private const int PlanCount = 5, PlanNameMax = 8;

    internal int PlanSwitchCooldownError(GameSession session) =>
        DateTime.UtcNow - session.EmblemPlanSwitchUtc < TimeSpan.FromSeconds(3) ? ErrorCodes.Cooldown : 0;

    /// <summary>Project the selected 5005 plan onto detached 5009 item instances before committing either owner.</summary>
    internal void ApplyGenreMarkers(IEnumerable<InvItem> inventory, EmblemState state)
    {
        var slots = state.Plans[state.CurrentPlan].Slots;
        foreach (var item in inventory.Where(i => IsEmblem(_data, i.Entry)))
        {
            int slot = slots.FirstOrDefault(s => s.Value == item.Guid).Key;
            item.Attrs ??= new();
            item.Attrs[Source] = slot == 0 ? 0 : 2;
            item.Attrs[Position] = slot;
        }
    }

    /// <summary>
    /// LC_SchemeServerHelper:82-138: select an existing saved plan and project its
    /// GUIDs through 5005 and itemInfos 10001/10002. Commit item projections and selected plan together.
    /// </summary>
    public async Task SwitchPlanAsync(GameSession session, EmbleSwitchPlanReq req)
    {
        if (session.Progress == null || session.Role == null)
        {
            await session.SendGameAsync(SwitchPlan, new EmbleSwitchPlanResp { Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        using var gate = await CharacterService.Lock(session);
        int code = Gate(session);
        var state = session.Emblems;
        if (code == 0 && (req.TargetIndex is < 0 or >= PlanCount || !state.Plans.ContainsKey(req.TargetIndex)))
            code = ErrorCodes.Parameter;
        if (code == 0 && req.TargetIndex != state.CurrentPlan)
        {
            var target = state.Plans[req.TargetIndex];
            code = PlanSwitchCooldownError(session);
            if (code == 0 && (target.Slots.Keys.Any(slot => slot is < FirstSlot or > LastSlot)
                || target.Slots.Values.Distinct().Count() != target.Slots.Count)) code = ErrorCodes.EmblemSlot;
            else if (code == 0)
                foreach (long guid in target.Slots.Values)
                {
                    var item = session.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0 && IsEmblem(_data, i.Entry));
                    if (item == null) { code = ErrorCodes.EmblemInvalid; break; }
                    if (!SeasonValid(item.Entry)) { code = ErrorCodes.EmblemOtherSeason; break; }
                }
            if (code == 0)
            {
                var next = CopyState(state);
                next.CurrentPlan = req.TargetIndex;
                var beforeItems = session.Items.Select(i => i.Clone()).ToArray();
                var afterItems = beforeItems.Select(i => i.Clone()).ToList();
                ApplyGenreMarkers(afterItems, next);
                long previous = session.EmblemPower;
                try
                {
                    await CommitEmblemsAsync(session, "emblem-plan-switch", next, beforeItems, afterItems, [],
                        new { From = state.CurrentPlan, To = req.TargetIndex },
                        () => session.EmblemPlanSwitchUtc = DateTime.UtcNow);
                }
                catch (SqliteException e)
                {
                    _log.LogWarning(e, "Soul Echo plan switch rolled back for {Role}", session.Role!.Id);
                    code = ErrorCodes.Parameter;
                }
                if (code == 0)
                {
                    await ChangedAsync(session, previous, inventory: true);
                }
            }
        }
        await session.SendGameAsync(SwitchPlan, new EmbleSwitchPlanResp { Result = code, CurrentPlan = session.Emblems.CurrentPlan });
    }

    /// <summary>82426 uses 5005 planName (LC_SchemeServerHelper:260-310); validation follows shared scheme limits.</summary>
    public async Task RenamePlanAsync(GameSession session, EmblemPlanChangeNameReq req)
    {
        if (session.Progress == null || session.Role == null)
        {
            await session.SendGameAsync(RenamePlan, new EmblemPlanChangeNameResp { Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        using var gate = await CharacterService.Lock(session);
        int code = Gate(session);
        string name = req.Name.Trim();
        if (code == 0 && (req.TargetIndex is < 0 or >= PlanCount || !session.Emblems.Plans.ContainsKey(req.TargetIndex)
            || name.Length == 0 || name.EnumerateRunes().Count() > PlanNameMax || name.Any(char.IsControl))) code = ErrorCodes.Parameter;
        if (code == 0)
        {
            var next = CopyState(session.Emblems);
            next.Plans[req.TargetIndex].Name = name;
            var beforeItems = session.Items.Select(i => i.Clone()).ToArray();
            var afterItems = beforeItems.Select(i => i.Clone()).ToArray();
            try
            {
                await CommitEmblemsAsync(session, "emblem-plan-rename", next, beforeItems, afterItems, [],
                    new { req.TargetIndex, Name = name });
            }
            catch (SqliteException e)
            {
                _log.LogWarning(e, "Soul Echo plan rename rolled back for {Role}", session.Role!.Id);
                code = ErrorCodes.Parameter;
            }
            if (code == 0) await SyncAsync(session);
        }
        await session.SendGameAsync(RenamePlan, new EmblemPlanChangeNameResp { Result = code });
    }
}
