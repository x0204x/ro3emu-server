using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Game.Ownership;

/// <summary>One detach and durable fence for each role attachment, shared by every exit path.</summary>
public sealed class RoleHandover(GameLoop loop, OwnerRegistry owners, RoleStore roles, Deadlines deadlines,
    ILogger<RoleHandover> log)
{
    private Func<GameSession, Task>? _cleanup;
    private Func<GameSession, ReservationKey[]>? _cleanupKeys;
    internal Action<string, GameSession>? PhaseForTest { get; set; }
    internal Func<GameSession, Task>? BeforeCleanupForTest { get; set; }
    internal Func<GameSession, Task>? AfterCleanupAdmissionForTest { get; set; }

    /// <summary>Runs first on every Begin, attached role or not: the reconnect ticket of the session ends here
    /// (logout, role switch, replacement, grace expiry), so a stale ReconnectInfo is refused.</summary>
    internal Action<GameSession>? Beginning { get; set; }

    /// <summary>Configured after construction to keep the ownership registry independent of game services. The keys
    /// are what the cleanup writes (C/D 15: the role's own keys plus the shared state its exit publishes, not Realm).</summary>
    public void Configure(Func<GameSession, Task> cleanup, Func<GameSession, ReservationKey[]> keys)
    {
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _cleanupKeys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    public Task Begin(GameSession session)
    {
        owners.AssertLoop();
        Beginning?.Invoke(session);
        session.CancelPendingCharacterIntents();
        deadlines.Detach(session);
        var owner = session.Owner;
        var lease = session.Attachment;
        if (owner == null || lease == null || !ReferenceEquals(owner.Attachment, lease))
            return Task.CompletedTask;
        if (owner.Detaching is { } existing) return existing;
        lease.Cancelled = true;
        owners.BeginQuiescing(owner, lease);
        owners.HandoverCount++;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.Detaching = completion.Task;
        // Fresh loop permit: the caller may still own Realm and must release it before awaiting this task.
        loop.TrackDetached("role handover", async () =>
        {
            try { await FinishAsync(session, owner, lease); completion.TrySetResult(); }
            catch (Exception e) { completion.TrySetException(e); }
        });
        return completion.Task;
    }

    /// <summary>The cleanup keys follow shared membership (the role's guild), which may change while the cleanup
    /// waits: re-admit until the admitted reservation still covers them.</summary>
    private async Task<LegacyOwnerScope> EnterCleanupAsync(GameSession session)
    {
        var keys = _cleanupKeys ?? throw new InvalidOperationException("Role handover cleanup is not configured.");
        while (true)
        {
            var scope = await owners.EnterAsync("handover cleanup", keys(session));
            if (owners.Reservations.Covers(scope.Context.Operation, keys(session))) return scope;
            scope.Dispose();
        }
    }

    /// <summary>How long the revision fence waits for room in a full reader queue.</summary>
    internal TimeSpan FenceQueuePatience { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The fence's read stays in one step with what follows it; only a refused read waits and asks again.</summary>
    private async Task<RoleRevisionVector> ReadFenceAsync(long role)
    {
        var patience = new QueueFullPatience(FenceQueuePatience);
        while (true)
        {
            try { return roles.ReadRevisionVector(role); }
            catch (Exception e) when (patience.MayRetry(e)) { }
            await patience.WaitAsync();
        }
    }

    private async Task FinishAsync(GameSession session, RoleOwner owner, AttachmentLease lease)
    {
        try
        {
            PhaseForTest?.Invoke("Quiescing", session);
            while (owner.Operations.Count != 0)
            {
                var changed = owner.OperationsChanged.Task;
                await changed;
            }
            PhaseForTest?.Invoke("BeforeCleanup", session);
            if (BeforeCleanupForTest is { } beforeCleanup) await beforeCleanup(session);
            using (var scope = await EnterCleanupAsync(session))
            {
                scope.Context.PermitCleanup(owner, lease);
                scope.Context.Pin(owner);
                if (AfterCleanupAdmissionForTest is { } admitted) await admitted(session);
                if (_cleanup == null) throw new InvalidOperationException("Role handover cleanup is not configured.");
                await _cleanup(session);
            }
            PhaseForTest?.Invoke("BeforeFence", session);
            var durable = await ReadFenceAsync(owner.RoleId);
            if (durable.Deleted || durable.Epoch != owner.LifecycleEpoch ||
                owner.Revisions.Any(part => durable.Revision(part.Key) < part.Value))
                throw new InvalidOperationException($"Role {owner.RoleId} handover revision fence failed.");
            owner.Revisions = durable.Parts;
            if (!ReferenceEquals(owner.Attachment, lease))
                throw new InvalidOperationException("Role attachment changed before its handover fence.");
            owner.Attachment = null;
            owner.State = RoleOwnerState.Detached;
            owner.Detaching = null;
            if (ReferenceEquals(session.Attachment, lease))
            {
                session.Attachment = null;
                session.Owner = null;
                session.Role = null;
            }
            PhaseForTest?.Invoke("Detached", session);
        }
        catch (Exception e)
        {
            owner.State = RoleOwnerState.RecoveryRequired;
            log.LogError(e, "Role {Role} handover failed; attachment retained for recovery", owner.RoleId);
            throw;
        }
    }
}
