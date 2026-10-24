using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class GuildService
{
    // Membership/name admission is serial within this family; guild resources still overlap the
    // exact guild key used by the already-converted market. Unrelated role work remains independent.
    private async Task RunGuildAsync(GameSession? actor, IEnumerable<long> guildIds,
        IEnumerable<long> roleIds, Func<Task> command, bool maintenance = false, bool disband = false, bool characterGate = false)
    {
        var owners = ownerRegistry;
        long[] requestedGuilds = guildIds.Where(id => id > 0).Distinct().ToArray();
        long[] requestedRoles = roleIds.Where(id => id > 0).Distinct().ToArray();
        var lease = actor?.Attachment;
        var role = actor?.Role;
        while (true)
        {
            ReservationKey[] Keys() => GuildKeys(actor, role?.Id, requestedGuilds, requestedRoles, maintenance, disband);
            var keys = Keys();
            // A rename waiting at the character gate must not reserve a guild that a leave or
            // disband can still change. Admit both together, then restage all participants.
            if (characterGate) await actor!.StateLock.WaitAsync();
            if (owners.Current is not { RuntimeOnly: false })
            {
                // A disconnected applicant can still be completing handover. Let its cleanup
                // finish without holding keys, then capture the durable offline membership view.
                var handovers = keys.Where(key => key.Kind == ReservationKind.RolePart)
                    .Select(key => long.TryParse(key.Id, out long id) ? owners.Cached(id) : null)
                    .Where(owner => owner?.State == RoleOwnerState.Quiescing)
                    .Select(owner => owner!.Detaching).OfType<Task>().Distinct().ToArray();
                if (handovers.Length != 0)
                {
                    if (characterGate) actor!.StateLock.Release();
                    await Task.WhenAll(handovers);
                    continue;
                }
            }
            LegacyOwnerScope? scope;
            try { scope = owners.TryEnter("guild/ordinary", keys); }
            catch { if (characterGate) actor!.StateLock.Release(); throw; }
            if (scope == null)
            {
                var changed = owners.Reservations.Changed;
                if (characterGate) actor!.StateLock.Release();
                await changed;
                continue;
            }
            OwnerNotices? notices = null;
            try
            {
                if (!owners.Reservations.Covers(scope.Context.Operation, Keys()) &&
                    !owners.AuditUncovered($"guild/ordinary under {scope.Context.Origin} needs {string.Join(",", keys)}")) continue;
                if (actor != null && (!ReferenceEquals(actor.Role, role) || !ReferenceEquals(actor.Attachment, lease)))
                    throw new OperationCanceledException("Guild requester changed attachment.");
                if (actor?.Owner is { } actorOwner) scope.Context.Pin(actorOwner);
                if (scope.Context.Notices == null) notices = new OwnerNotices(scope.Context);
                await command();
            }
            finally { notices?.Dispose(); scope.Dispose(); if (characterGate) actor!.StateLock.Release(); }
            if (notices != null) await notices.FlushAsync();
            return;
        }
    }

    private ReservationKey[] GuildKeys(GameSession? actor, long? roleId, long[] requestedGuilds, long[] requestedRoles,
        bool maintenance, bool disband)
    {
        var guilds = maintenance ? store.List() : requestedGuilds
            .Append(actor?.Role is { } r ? store.ForRole(r.Id)?.Id ?? 0 : 0)
            .Where(id => id > 0).Distinct().Select(store.Get).OfType<GuildStore.State>().ToArray();
        return guilds.Select(g => ReservationKey.Entity("guild", g.Id.ToString()))
            .Append(ReservationKey.Entity("guild-catalog", "ordinary"))
            .Concat(maintenance || disband ? new[] { ReservationKey.Entity("guild_market", "1") } : [])
            .Concat(guilds.SelectMany(g => g.Members.Keys).Concat(requestedRoles)
                .Concat(roleId is { } id ? new[] { id } : []).Distinct().Select(id => ReservationKey.Role(id)))
            .ToArray();
    }

    /// <summary>The keys of the actor's own ordinary guild operation (own guild, its members, the catalog and the
    /// actor's role): what <see cref="OnlineChangedAsync"/> and <see cref="SceneGuildIdAsync"/> admit.</summary>
    internal ReservationKey[] OwnGuildKeys(GameSession s) => GuildKeys(s, s.Role?.Id, [], [], false, false);

    public Task CreateAsync(GameSession s, GuildCreateReq req) => RunGuildAsync(s, [], [], () => CreateCoreAsync(s, req), characterGate: true);
    public Task ApplyAsync(GameSession s, GuildEnterApplyReq req) => RunGuildAsync(s, req.GuildIds, [], () => ApplyCoreAsync(s, req), true);
    public Task ApplyOpeAsync(GameSession s, GuildEnterApplyOpeReq req) => RunGuildAsync(s, [], req.PlayerIds, () => ApplyOpeCoreAsync(s, req), true);
    public Task InviteAsync(GameSession s, GuildInviteReq req) => RunGuildAsync(s, [], req.TarPlayerID, () => InviteCoreAsync(s, req));
    public Task InviteAckAsync(GameSession s, GuildInviteAckReq req) => RunGuildAsync(s, [req.GuildID], [], () => InviteAckCoreAsync(s, req));
    public Task ChangeOfficialAsync(GameSession s, GuildChangeOfficialReq req) => RunGuildAsync(s, [], [req.TarPlayerID], () => ChangeOfficialCoreAsync(s, req));
    public Task LeaveAsync(GameSession s) => RunGuildAsync(s, [], [], () => LeaveCoreAsync(s), disband: true);
    public Task KickAsync(GameSession s, GuildMemberKickReq req) => RunGuildAsync(s, [], req.TarPlayerIDs, () => KickCoreAsync(s, req));
    public Task EditAsync(GameSession s, GuildEditReq req) => RunGuildAsync(s, [], [], () => EditCoreAsync(s, req),
        characterGate: (int)req.Tp == 2 && !req.IsCheck);
    public Task BuildUpgradeAsync(GameSession s, GuildBuildLevelUpReq req) => RunGuildAsync(s, [], [], () => BuildUpgradeCoreAsync(s, req), true);
    public Task RecruitAsync(GameSession s, GuildRecruitReq req) => RunGuildAsync(s, [], [], () => RecruitCoreAsync(s, req));
    public Task CancelCreateAsync(GameSession s) => RunGuildAsync(s, [], [], () => CancelCreateCoreAsync(s), disband: true);
    public Task HelperOpeAsync(GameSession s, GuildCreateHelperOpeReq req) => RunGuildAsync(s, req.GuildIds, [], () => HelperOpeCoreAsync(s, req));
    public Task HelperLeaveAsync(GameSession s, GuildCreateHelperLeaveReq req) => RunGuildAsync(s, [], [], () => HelperLeaveCoreAsync(s, req));
    // C/D 15: realm-wide guild maintenance runs on the heartbeat (at most once a second), not in the login. Inside
    // the role select the login only reads guild state and writes the role's own guild blob, which the select's role
    // reservation covers; elsewhere it admits its own guild scope.
    public Task LoginAsync(GameSession s) => ownerRegistry.Current is { RuntimeOnly: false }
        ? LoginCoreAsync(s) : RunGuildAsync(s, [], [], () => LoginCoreAsync(s));
    public Task OnlineChangedAsync(GameSession s, bool online) => RunGuildAsync(s, [], [], () => OnlineChangedCoreAsync(s, online));
    public Task ReconcileAsync() => RunGuildAsync(null, [], [], ReconcileCoreAsync, true);
    public Task RecommendAsync(GameSession s, GuildRecommendListReq req) => RunGuildAsync(s, [], [], () => RecommendCoreAsync(s, req));
    public Task HelperListAsync(GameSession s, GuildCreateHelperListReq req) => RunGuildAsync(s, [], [], () => HelperListCoreAsync(s, req));
    public Task DetailAsync(GameSession s, GuildDetailInfoReq req) => RunGuildAsync(s, [req.GuildId], [], () => DetailCoreAsync(s, req));
    public Task SceneGuildIdAsync(GameSession s) => RunGuildAsync(s, [], [], () => SceneGuildIdCoreAsync(s));
    public Task SyncOnceAsync(GameSession s, SyncGuildDataOnceReq req) => RunGuildAsync(s, [], [], () => SyncOnceCoreAsync(s, req));
    public Task MemberIdsAsync(GameSession s, GuildMemberIDListReq req) => RunGuildAsync(s, [], [], () => MemberIdsCoreAsync(s, req));
    public Task LeagueMgrDataAsync(GameSession s, uint id) => RunGuildAsync(s, [], [], () => LeagueMgrDataCoreAsync(s, id));
    public Task LeagueInfoAsync(GameSession s, uint id) => RunGuildAsync(s, [], [], () => LeagueInfoCoreAsync(s, id));
    public Task LeagueGroupInfoAsync(GameSession s, uint id) => RunGuildAsync(s, [], [], () => LeagueGroupInfoCoreAsync(s, id));

    /// <summary>Stage roster and role projections together, including generated guild identities.
    /// The membership index stays authoritative; Apply publishes every live view
    /// before queued notices can run.</summary>
    private void EnlistViews(GuildStore.State guild, RoleItemPlanBuilder transaction, IEnumerable<long>? removed = null,
        bool deferUnavailable = false)
    {
        foreach (long id in guild.Members.Keys.Concat(removed ?? []).Distinct())
        {
            // Resource refresh follows the committed caravan payment in a fresh scope. A
            // member may have started handover since that payment was admitted. Its durable
            // guild member remains authoritative; cleanup/login rebuilds this derived view.
            // Ordinary membership commands must still admit every changed projection.
            if (deferUnavailable && ownerRegistry.Current is { } context
                && !context.CanPin(ownerRegistry.Get(id))) continue;
            var online = Online(id);
            var prior = online?.Guild ?? Personal(id);
            var view = guild.Members.TryGetValue(id, out var member)
                ? RoleInfo(guild, member, prior) : WithoutGuild(prior);
            if (member == null) view.LastGuildId = guild.Id;
            bool generated = guild.Id == 0 && member != null;
            byte[] bytes = view.ToByteArray();
            // Roster metadata such as online timestamps does not rewrite every member's identical
            // projection. In particular another member may already be in handover cleanup.
            if (!generated && roles.LoadGuildInfo(id) is { } saved && saved.AsSpan().SequenceEqual(bytes)
                && (online == null || Equals(online.Guild, view))) continue;
            transaction.Add(new GuildRoleViewWritePlan(id, view, generated), _ =>
            {
                if (generated) view.GuildId = guild.Id;
                if (online != null)
                {
                    long previous = online.Guild?.GuildId ?? 0, previousOfficial = online.Guild?.Official ?? 0;
                    online.Guild = view;
                    if (previous != view.GuildId) chat.GuildMembershipChanged(online, previous);
                    if (previous != view.GuildId || previousOfficial != view.Official)
                        foreach (var handler in MembershipChanged) handler(online);
                }
            });
        }
    }

    private async Task<bool> SaveGuildAsync(GuildStore.State guild, IEnumerable<long>? removed = null, EconomyEnvelope? economy = null)
    {
        var transaction = roles.NewTransaction();
        store.EnlistSave(guild, transaction);
        EnlistViews(guild, transaction, removed);
        if (economy != null) transaction.WithEconomy(economy);
        try { await roles.CommitAsync(transaction, "guild/ordinary-save"); return true; }
        catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteExtendedErrorCode is 1555 or 2067) { return false; }
    }

    private async Task SavePersonalAsync(GameSession session, DBRoleGuildInfo view)
    {
        var bytes = view.ToByteArray();
        if (roles.BlobUnchanged(session.Role!.Id, "guild_info", bytes)) { session.Guild = view; return; } // a login's normalization
        var transaction = roles.NewTransaction();
        transaction.RoleBlob(session.Role.Id, "guild_info", bytes);
        await roles.CommitAsync(transaction, "guild/personal", _ => session.Guild = view);
    }

    private async Task DeleteGuildAsync(GuildStore.State guild, IEnumerable<long>? removed = null)
    {
        var transaction = roles.NewTransaction();
        long[] prior = guild.Members.Keys.Concat(removed ?? []).Distinct().ToArray();
        // The last leaver may already be removed from the staged roster; resolve the durable actor.
        transaction.WithEconomy(EnlistDisband(guild, transaction, prior, prior.First()));
        await roles.CommitAsync(transaction, "guild/disband-recovery");
    }

    /// <summary>
    /// A guild's end, staged: its market stalls and treasury go to recovery and the given roles' views lose the guild.
    /// Shared by disbanding and by a merge, where only the members who stay behind lose it (GuildService.Merge). Returns the
    /// economy envelope, which the caller attaches last (it freezes the transaction).
    /// </summary>
    private EconomyEnvelope EnlistDisband(GuildStore.State guild, RoleItemPlanBuilder transaction, IEnumerable<long> clearViews, long actorId)
    {
        var market = store.LoadMarket();
        long escrow = market.Stalls.Where(stall => stall.GuildId == guild.Id).Sum(stall => stall.RemainCount);
        if (market.Stalls.RemoveAll(stall => stall.GuildId == guild.Id) > 0) store.EnlistMarket(market, transaction);
        store.EnlistRecoverDelete(guild, transaction, escrow);
        var empty = new GuildStore.State { Id = guild.Id };
        EnlistViews(empty, transaction, clearViews);
        var actor = roles.Get(actorId)!;
        var owner = new EconomyOwner(EconomyOwnerKind.Guild, guild.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var effects = new List<EconomyEffect>();
        if (escrow > 0)
        {
            effects.Add(new(new(EconomyAssetKind.GuildResource, 3), owner, EconomyCompartment.GuildMarket,
                -escrow, EconomyLegKind.Transfer, EconomyReason.GuildResourceTransfer, 6));
            effects.Add(new(new(EconomyAssetKind.GuildResource, 3), owner, EconomyCompartment.Recovery,
                escrow, EconomyLegKind.Transfer, EconomyReason.GuildResourceTransfer, 6));
        }
        var treasury = store.Get(guild.Id)!;
        foreach (var (asset, count) in new[] { (1L, treasury.Money), (2L, treasury.Contribute),
            (3L, treasury.Materials), (4L, treasury.Reputation), (5L, treasury.AuctionMaterials) })
        {
            if (count == 0) continue;
            effects.Add(new(new(EconomyAssetKind.GuildResource, asset), owner, EconomyCompartment.GuildTreasury,
                -count, EconomyLegKind.Transfer, EconomyReason.GuildResourceTransfer, (int)asset));
            effects.Add(new(new(EconomyAssetKind.GuildResource, asset), owner, EconomyCompartment.Recovery,
                count, EconomyLegKind.Transfer, EconomyReason.GuildResourceTransfer, (int)asset));
        }
        return new EconomyEnvelope(Guid.NewGuid().ToString("N"), actor.Account, actor.Id,
            "guild-disband", "guild:" + guild.Id, "guild-disband:" + guild.Id, NowMs(), effects, []);
    }

    private EconomyEnvelope GuildPayment(GameSession actor, string kind, int entry, long amount)
    {
        bool currency = data.Row("ItemConfig", entry) is { } row && Ro3Emu.Server.Data.GameData.Int(row, "_iType") == 1;
        var effect = new EconomyEffect(new(currency ? EconomyAssetKind.Currency : EconomyAssetKind.Item, entry),
            new(EconomyOwnerKind.Role, actor.Role!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            currency ? EconomyCompartment.Currency : EconomyCompartment.Inventory, -amount,
            EconomyLegKind.Sink, EconomyReason.GuildServiceCost);
        string operation = Guid.NewGuid().ToString("N");
        return new(operation, actor.Account!, actor.Role.Id, kind, kind, kind + ":" + operation, NowMs(), [effect], []);
    }
}
