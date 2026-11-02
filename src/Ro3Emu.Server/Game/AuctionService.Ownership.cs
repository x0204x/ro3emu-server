using System.Globalization;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

public sealed partial class AuctionService
{
    // Each attempt discovers the complete participant set before admission. A conflicting attempt
    // holds nothing while it waits; a changed set is restaged from current rows on wake-up.
    private async Task<LegacyOwnerScope> EnterAuctionAsync(string origin, Func<ReservationKey[]> stage)
    {
        var owners = loop.Owners ?? throw new InvalidOperationException("Auction owner registry is unavailable.");
        long queuedAt = 0;
        while (true)
        {
            var keys = stage().Distinct().ToArray();
            var scope = owners.TryEnter(origin, keys);
            if (scope == null)
            {
                if (queuedAt == 0) queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                await owners.Reservations.Changed;
                continue;
            }
            try
            {
                var current = stage().Distinct().ToArray();
                if (!keys.ToHashSet().SetEquals(current))
                {
                    scope.Dispose();
                    continue;
                }
                foreach (var key in keys.Where(key => key.Kind == ReservationKind.RolePart))
                    scope.Context.Pin(owners.Get(long.Parse(key.Id, CultureInfo.InvariantCulture)));
                if (queuedAt != 0) owners.RecordQueueAge(queuedAt);
                return scope;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }
    }

    private static bool CurrentSession(GameSession session, Role role, AttachmentLease? lease, RoleOwner? owner) =>
        ReferenceEquals(session.Role, role) && ReferenceEquals(session.Attachment, lease)
        && lease?.Cancelled != true && ReferenceEquals(session.Owner, owner)
        && (owner == null || ReferenceEquals(owner.Attachment, lease));

    private async Task<LegacyOwnerScope> EnterSessionAuctionAsync(GameSession session, string origin,
        Func<long, ReservationKey[]> stage)
    {
        var role = session.Role ?? throw new OperationCanceledException("Auction request has no role.");
        var lease = session.Attachment;
        var owner = session.Owner;
        var scope = await EnterAuctionAsync(origin, () =>
        {
            if (!CurrentSession(session, role, lease, owner))
                throw new OperationCanceledException("Auction role attachment changed while waiting.");
            return stage(role.Id);
        });
        if (!CurrentSession(session, role, lease, owner))
        {
            scope.Dispose();
            throw new OperationCanceledException("Auction role attachment changed after admission.");
        }
        return scope;
    }

    private async Task SendSessionAuctionAsync(GameSession session, long role, AttachmentLease? lease,
        uint message, IMessage payload)
    {
        if (AuctionRequestCurrent(session, role, lease))
            await session.SendGameAsync(message, payload);
    }

    private static bool AuctionRequestCurrent(GameSession session, long role, AttachmentLease? lease) =>
        session.Role?.Id == role && ReferenceEquals(session.Attachment, lease) && lease?.Cancelled != true
        && ReferenceEquals(session.Owner?.Attachment, lease);

    private static ReservationKey[] RoleKeys(IEnumerable<long> roles) => [.. roles.Where(role => role > 0)
        .Distinct().Select(role => ReservationKey.Role(role))];

    private ReservationKey[] RowKeys(AuctionStore.Item? row, IEnumerable<long>? extraRoles = null,
        IEnumerable<AuctionStore.Prebuy>? orders = null)
    {
        var keys = new List<ReservationKey>();
        if (row != null)
        {
            keys.Add(ReservationKey.Entity("auction_item", row.SourceKey));
            keys.AddRange(RoleKeys(row.Eligible.Concat(row.Joined).Append(row.Owner).Append(row.Winner)
                .Where(role => role > 0 && roles.Get(role) != null)));
        }
        keys.AddRange(RoleKeys(extraRoles ?? []));
        foreach (var order in orders ?? [])
        {
            keys.Add(ReservationKey.Role(order.Role));
            if (order.Id != 0) keys.Add(ReservationKey.Entity("auction_prebuy", order.Id.ToString(CultureInfo.InvariantCulture)));
        }
        return [.. keys.Distinct()];
    }

    // Query projections read one caller's live personal state alongside selected durable rows.
    // They need that caller and the rows, not every lot participant's inventory/progress.
    private static ReservationKey[] ReadKeys(long role, IEnumerable<AuctionStore.Item> rows,
        IEnumerable<AuctionStore.Prebuy>? orders = null) =>
        [.. RoleKeys([role]).Concat(rows.Select(row => ReservationKey.Entity("auction_item", row.SourceKey)))
            .Concat((orders ?? []).Where(order => order.Id != 0).Select(order =>
                ReservationKey.Entity("auction_prebuy", order.Id.ToString(CultureInfo.InvariantCulture))))
            .Distinct()];

    private static void RequireScope(LegacyOwnerScope scope, RoleItemPlanBuilder builder)
    {
        if (!scope.Context.Registry.Reservations.Covers(scope.Context.Operation, builder.ReservationKeys) &&
            !scope.Context.Registry.AuditUncovered($"auction plan under {scope.Context.Origin} grew beyond its admitted participants"))
            throw new InvalidOperationException("Auction plan grew beyond its admitted participant set.");
    }

    private void EnlistPersonal(long role, byte[]? before, Romsg.DBAuction state, RoleItemPlanBuilder tx)
    {
        tx.Add(new AuctionPersonalCasPlan(role, before, state.ToByteArray()), _ =>
        {
            if (Online(role) is { } session) session.Auction = state.Clone();
        });
    }
}
