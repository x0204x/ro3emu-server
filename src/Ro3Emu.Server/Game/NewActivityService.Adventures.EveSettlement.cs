using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class ActivityRuntimeState
{
    public long EveSettledCycleStart { get; set; }
}

public sealed partial class NewActivityService
{
    /// <summary>
    /// Settle the saved Season Eve cycle before RefreshCalendar replaces its timestamps.
    /// ActivityGate is held, including on login before ActivitiesReady becomes true.
    /// </summary>
    private async Task SettleEveExpiredAsync(GameSession session, DeadlineLease? scheduled = null)
    {
        // Settlement ownership survives a replacement of this service's timer; only attachment/role changes abort it.
        var lease = scheduled is { } due
            ? due with { ScheduleGeneration = 0, Key = null }
            : _deadlines.Capture(session);
        long roleId = session.Role?.Id ?? 0;
        const long activityId = 117;
        if (session.Role == null || session.NewActivities == null || session.ActivityRuntime == null
            || !session.NewActivities.ActivityInfo.TryGetValue(activityId, out var info)
            || info.CycleStartTime <= 0 || info.CycleEndTime <= 0 || Now < info.CycleEndTime
            || session.ActivityRuntime.EveSettledCycleStart == info.CycleStartTime) return;
        // EveWriteLeadSeconds makes this unreachable with the shared key unless an operation outlives the lead window;
        // fail before any write rather than at the commit's reservation check.
        if (_loop.Owners is { } owners && !owners.CurrentCovers(EveKey(info.CycleStartTime, writes: true)))
            throw new OperationCanceledException("Season Eve settlement needs the exclusive Eve key; the next admission settles it.");

        // No settlement plan has been admitted yet. A role-screen request may cancel this
        // speculative gate wait; once acquired, the complete transaction remains durable.
        if (!_deadlines.IsCurrent(session, lease) || session.Role?.Id != roleId) return;
        await _adventureSocialGate.WaitAsync(session.PendingCharacterIntent);
        try
        {
            if (!_deadlines.IsCurrent(session, lease) || session.Role?.Id != roleId) return;
            var global = ReadEveGlobal(session, activityId);
            AdvanceEveGlobal(session, activityId, global);
            var next = session.NewActivities.Clone();
            var runtime = CloneRuntime(session);
            runtime.EveSettledCycleStart = info.CycleStartTime;
            bool entered = next.ActivityTypeState.TryGetValue(19, out var type)
                && type.ActivityState.TryGetValue(activityId, out var personal) && personal.EnterTime > 0;
            var eve = next.ActivityInfo[activityId].ActivityParam.SeasonEve;
            var deliveries = new List<MailService.ActivityTemplateDelivery>();
            if (entered && eve != null)
            {
                long wishAward = eve.SelectWish > 0
                    ? _data.Row("SeasonEveWishConfig", eve.SelectWish) is { } row
                        ? GameData.Int(row, "_iRewardId") : 0
                    : EveConstant("SeasonEveNoWishAward");
                if (wishAward <= 0) throw new InvalidOperationException("Invalid Season Eve wish award.");
                deliveries.Add(new MailService.ActivityTemplateDelivery(
                    EveConstant("SeasonEveWishMailId"), ResolveEveMailAttachments(session, [wishAward])));
                var unclaimed = EveProgressAwards()
                    .Where(award => global.Progress >= award.Threshold && !eve.ClaimedReward.ContainsKey(award.Id))
                    .Select(award => award.Award).ToArray();
                if (unclaimed.Length > 0)
                    deliveries.Add(new MailService.ActivityTemplateDelivery(
                        EveConstant("SeasonEveProgressMailId"), ResolveEveMailAttachments(session, unclaimed)));
            }

            next.ActivityInfo[activityId].ActivityParam.SeasonEve = CreateSeasonEve();
            if (next.ActivityTypeState.TryGetValue(19, out type)
                && type.ActivityState.TryGetValue(activityId, out var reset)) reset.EnterTime = 0;
            byte[] activityBytes = next.ToByteArray();
            byte[] runtimeBytes = JsonSerializer.SerializeToUtf8Bytes(runtime);
            await _mail.DeliverActivityBatchAsync(session, deliveries, tx =>
            {
                if (!_deadlines.IsCurrent(session, lease) || session.Role?.Id != roleId)
                    throw new OperationCanceledException("Activity attachment changed before Eve settlement commit.");
                tx.RoleBlob(roleId, "new_activities", activityBytes);
                tx.RoleBlob(roleId, "activity_runtime", runtimeBytes);
                SaveEveGlobal(session, activityId, global, tx);
                var intent = new EscrowEconomyIntent("activity/eve-settlement", session.Account ?? "",
                    roleId, sourceKey: $"activity/eve-settlement/{roleId}/{info.CycleStartTime}",
                    transferReason: EconomyReason.MailDelivery);
                foreach (var group in deliveries.SelectMany(d => d.Attachments)
                    .Where(a => a.Entry is not (100 or 101)).GroupBy(a => a.Entry))
                    intent.Source(AdventureAsset(group.Key), group.Sum(a => (long)a.Count),
                        EconomyReason.ActivityReward);
                tx.WithEscrowEconomy(intent.Evidence(new
                {
                    Role = roleId, info.CycleStartTime,
                    Deliveries = deliveries.Select(d => new { d.TemplateId, d.Attachments }).ToArray()
                }));
            }, committed: () =>
            {
                // A cancelled attachment cannot undo a committed settlement; publish its source before notices.
                session.NewActivities = next;
                session.ActivityRuntime = runtime;
            });
        }
        finally { _adventureSocialGate.Release(); }
    }

    private IReadOnlyList<MailService.Attachment> ResolveEveMailAttachments(GameSession session,
        IReadOnlyList<long> awardGroups)
    {
        var grants = awardGroups.SelectMany(group => _awards.Resolve(session, group, Playbook(session)).Grants).ToArray();
        if (grants.Any(g => g.BindType != 0 || g.ExpireTimeStamp != 0))
            throw new InvalidOperationException("Season Eve mail requires plain configured item rewards.");
        return grants.GroupBy(g => g.Entry)
            .Select(g => new MailService.Attachment(g.Key, checked(g.Sum(item => item.Count)))).ToArray();
    }
}
