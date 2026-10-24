using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    /// <summary>Loop-only publication for converted economic owners. Finish auxiliary plan pruning
    /// and freeze packet deltas before releasing participants; transport cannot change owner state.</summary>
    internal Func<Task> CaptureEconomicItemsNotice(GameSession session, bool publishStateChanged = true)
    {
        var items = BuildItems(session);
        if (ResendItemsWholeIfNeeded(session, items)) items = BuildItems(session);
        var notices = new List<Func<Task>> { CaptureEconomicSync(session, ItemsSync, items) };
        if (PruneEquipmentPlans(session))
        {
            roles.SaveEquipPlans(session.Role!.Id, session.EquipmentPlans.ToByteArray());
            notices.Add(CaptureEconomicSync(session, EquipSync, BuildEquip(session)));
        }
        if (publishStateChanged) foreach (var handler in StateChanged) handler(session);
        return () => Task.WhenAll(notices.Select(notice => notice()));
    }

    internal static Func<Task> CaptureEconomicSync(GameSession session, uint id, IMessage snapshot)
    {
        var role = session.Role;
        var lease = session.Attachment;
        session.PlanPowers?.Invoke(id, snapshot);
        var increment = session.Synced.TryGetValue(id, out var before)
            ? AutoSync.Changes(before, snapshot, NextVersion(session, id))
            : AutoSync.Full(snapshot, NextVersion(session, id));
        session.Synced[id] = snapshot;
        return () => increment == null || !ReferenceEquals(session.Role, role) ||
            !ReferenceEquals(session.Attachment, lease) || lease?.Cancelled == true
                ? Task.CompletedTask : session.SendGameAsync(id, increment);
    }
}
