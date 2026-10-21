using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

public sealed partial class NewActivityService
{
    /// <summary>Multi-role activity requests enter with their complete participant set, outside the dispatcher bridge.</summary>
    internal bool IsOfflineOwnerRequest(uint message, ReadOnlyMemory<byte> body)
    {
        if (message is 82964 or 82967) return true;
        if (message != Common) return false;
        var request = CommonActivityReq.Parser.ParseFrom(body.Span);
        return request.FragmentGatherSend != null || request.FragmentGatherAskFinished != null
            || request.BattlePassGift != null;
    }

    /// <summary>Only requests whose service path reserves the role and its possible Eve settlement source.</summary>
    internal bool IsEventOwnerRequest(uint message, ReadOnlyMemory<byte> body)
    {
        if (message is GetList or GetReward or LevelAward or FunctionPreviewAward) return true;
        if (message != Common) return false;
        var request = CommonActivityReq.Parser.ParseFrom(body.Span);
        if (request.ActivityId == 118)
            return request.PronteraDefenseDonate != null || request.PronteraDefenseGuildDefense != null
                || request.ActivityCase == CommonActivityReq.ActivityOneofCase.None;
        return request.LevelAwardGetReward != null || request.FunctionPreviewGetReward != null
            || request.GetRewardCommon != null && _data.Row("NewActivityConfig", request.ActivityId) is { } row
                && Ro3Emu.Server.Data.GameData.Int(row, "_iActivityType") == 8
            || request.BattlePassClaimReward != null || request.BattlePassMarkUnlockViewed != null
            || request.BattlePassBuyLevel != null || request.GainMonthCardDailyAward != null;
    }

    /// <summary>C/D 15: the Eve cycle entity the login's activity admission needs, from the same saved blob the login
    /// loads, reserved up front by the role select.</summary>
    internal ReservationKey[] LoginKeys(long roleId)
    {
        var saved = _roles.LoadNewActivities(roleId);
        var loaded = saved == null ? new DBNewActivityInfo() : DBNewActivityInfo.Parser.ParseFrom(saved);
        return loaded.ActivityInfo.TryGetValue(117, out var eve) && eve.CycleStartTime > 0
            ? [EveKey(eve.CycleStartTime, EveSettlementNear(eve))] : [];
    }

    // C/D 16 (S4): the Season Eve global row is written only by its settlement (after the role's cycle ended) and by
    // Eve adventure requests. Everything else reads it, so it holds the row shared; as an exclusive key on every
    // activity operation it serialized all logins and activity requests of a cycle (S4: up to 3 s).
    // Every activity path settles an ended cycle before RefreshCalendar replaces its timestamps, and it reads the
    // clock for that after admission and after awaits. So the key is exclusive from EveWriteLeadSeconds before the
    // cycle end: an operation admitted with the shared key cannot reach a due settlement.
    private const long EveWriteLeadSeconds = 300;
    private bool EveSettlementNear(PlayerActivityInfo eve) => eve.CycleEndTime > 0 && Now >= eve.CycleEndTime - EveWriteLeadSeconds;
    private static ReservationKey EveKey(long cycle, bool writes) => writes
        ? ReservationKey.Entity("activity_eve_global", $"117:{cycle}")
        : ReservationKey.SharedEntity("activity_eve_global", $"117:{cycle}");
    private bool EveWrites(GameSession session, CommonActivityReq? request, bool? loadedEnded) =>
        request?.ActivityId == 117 || (loadedEnded ?? (session.NewActivities?.ActivityInfo.TryGetValue(117, out var eve) == true
            && EveSettlementNear(eve) && session.ActivityRuntime?.EveSettledCycleStart != eve.CycleStartTime));
    /// <summary>The Eve key an activity operation of this session needs now; null without a saved cycle.</summary>
    internal ReservationKey? EveKeyFor(GameSession session, CommonActivityReq? request = null, long? loadedCycle = null,
        bool? loadedEnded = null)
    {
        long cycle = loadedCycle ?? (session.NewActivities?.ActivityInfo.TryGetValue(117, out var eve) == true ? eve.CycleStartTime : 0);
        return cycle > 0 ? EveKey(cycle, EveWrites(session, request, loadedEnded)) : null;
    }

    private async Task<LegacyOwnerScope> EnterLocalWithGateAsync(GameSession session, string origin,
        CommonActivityReq? request = null, long? loadedEveCycle = null, bool? loadedEveEnded = null)
    {
        var owners = _loop.Owners ?? throw new InvalidOperationException("Activity owner registry is unavailable.");
        var role = session.Role ?? throw new OperationCanceledException("Activity role disappeared before admission.");
        var owner = session.Owner ?? throw new OperationCanceledException("Activity owner disappeared before admission.");
        var attachment = session.Attachment;
        while (true)
        {
            var keys = new List<ReservationKey> { ReservationKey.Role(role.Id), ReservationKey.Lifecycle(role.Id) };
            if (EveKeyFor(session, request, loadedEveCycle, loadedEveEnded) is { } eveKey) keys.Add(eveKey);
            if (request?.ActivityId == 118) keys.AddRange(_defense.OwnerKeys(session, request));
            var scope = await owners.EnterWithGateAsync(origin, session.ActivityGate, keys.ToArray());
            try
            {
                if (!ReferenceEquals(session.Role, role) || !ReferenceEquals(session.Owner, owner)
                    || !ReferenceEquals(session.Attachment, attachment) || attachment?.Cancelled == true)
                    throw new OperationCanceledException("Activity attachment changed before admission.");
                var currentKeys = new List<ReservationKey> { ReservationKey.Role(role.Id), ReservationKey.Lifecycle(role.Id) };
                if (EveKeyFor(session, request, loadedEveCycle, loadedEveEnded) is { } currentEveKey) currentKeys.Add(currentEveKey);
                if (request?.ActivityId == 118) currentKeys.AddRange(_defense.OwnerKeys(session, request));
                if (!keys.SequenceEqual(currentKeys))
                {
                    session.ActivityGate.Release();
                    scope.Dispose();
                    continue;
                }
                scope.Context.Pin(owner);
                return scope;
            }
            catch
            {
                session.ActivityGate.Release();
                scope.Dispose();
                throw;
            }
        }
    }

    private async Task<LegacyOwnerScope> EnterSocialWithGateAsync(GameSession sender, long recipient,
        string origin, long ask = 0)
    {
        var owners = _loop.Owners ?? throw new InvalidOperationException("Activity owner registry is unavailable.");
        var role = sender.Role ?? throw new OperationCanceledException("Activity sender has no role.");
        var owner = sender.Owner ?? throw new OperationCanceledException("Activity sender has no owner.");
        var attachment = sender.Attachment;
        while (true)
        {
            var keys = new List<ReservationKey> { ReservationKey.Role(role.Id), ReservationKey.Lifecycle(role.Id) };
            if (recipient > 0 && recipient != role.Id)
            {
                keys.Add(ReservationKey.Role(recipient));
                keys.Add(ReservationKey.Lifecycle(recipient));
            }
            if (ask > 0) keys.Add(ReservationKey.Entity("activity_fragment_ask",
                ask.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            // The social paths run the same settlement pre-step as every Common request, so they need the same key.
            var eveKey = EveKeyFor(sender);
            if (eveKey is { } key) keys.Add(key);
            var scope = await owners.EnterWithGateAsync(origin, sender.ActivityGate, keys.Distinct().ToArray());
            try
            {
                if (!ReferenceEquals(sender.Role, role) || !ReferenceEquals(sender.Owner, owner)
                    || !ReferenceEquals(sender.Attachment, attachment) || attachment?.Cancelled == true)
                    throw new OperationCanceledException("Activity sender attachment changed before admission.");
                if (EveKeyFor(sender) != eveKey)
                {
                    sender.ActivityGate.Release();
                    scope.Dispose();
                    continue;
                }
                scope.Context.Pin(owner);
                return scope;
            }
            catch
            {
                sender.ActivityGate.Release();
                scope.Dispose();
                throw;
            }
        }
    }

    /// <summary>SessionRegistry supplies only a presentation endpoint; RoleOwner remains the durable identity.</summary>
    private GameSession? AttachedSocialSession(RoleOwner owner) =>
        owner.State == RoleOwnerState.Attached && owner.Attachment is { } attachment
            ? _sessions.All().FirstOrDefault(session => ReferenceEquals(session.Owner, owner)
                && ReferenceEquals(session.Attachment, attachment) && session.Role?.Id == owner.RoleId)
            : null;

    private async Task<RoleOwner?> PinSocialRecipientAsync(long roleId)
    {
        var owners = _loop.Owners ?? throw new InvalidOperationException("Activity owner registry is unavailable.");
        var context = owners.Current ?? throw new InvalidOperationException("Activity gift needs an owner reservation.");
        if (await _roles.GetAsync(roleId) == null) return null;
        var vector = await _roles.ReadRevisionVectorAsync(roleId);
        if (vector.Deleted) return null;
        var owner = owners.GetOrRegister(vector);
        if (!context.CanPin(owner) || owner.State == RoleOwnerState.Loading) return null;
        context.Pin(owner);
        return owner;
    }
}
