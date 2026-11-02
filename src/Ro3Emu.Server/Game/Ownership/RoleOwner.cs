namespace Ro3Emu.Server.Game.Ownership;

public enum RoleOwnerState { Detached, Loading, Attached, Quiescing, Deleting, Deleted, RecoveryRequired }

/// <summary>Persistent role identity, independent of a socket (C/D design, steps 06–07).</summary>
public sealed class RoleOwner(long roleId, long lifecycleEpoch)
{
    public long RoleId { get; } = roleId;
    public long LifecycleEpoch { get; internal set; } = lifecycleEpoch;
    private RoleOwnerState _state = RoleOwnerState.Detached;
    public RoleOwnerState State
    {
        get => _state;
        internal set
        {
            var was = _state;
            _state = value;
            if (was != value && (was == RoleOwnerState.RecoveryRequired || value == RoleOwnerState.RecoveryRequired))
                RecoveryChanged?.Invoke(this, value == RoleOwnerState.RecoveryRequired);
        }
    }
    /// <summary>Tells the registry when the role enters or leaves RecoveryRequired, wherever that was decided.</summary>
    internal Action<RoleOwner, bool>? RecoveryChanged;
    public AttachmentLease? Attachment { get; internal set; }
    public IReadOnlyDictionary<string, long> Revisions { get; internal set; } = new Dictionary<string, long>();
    internal readonly HashSet<long> Operations = [];
    internal Task? Detaching;
    internal TaskCompletionSource OperationsChanged = NewSignal();
    internal long QuiesceAdmission;
    internal long NextGeneration;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void OperationFinished(long operation)
    {
        if (!Operations.Remove(operation)) return;
        var changed = OperationsChanged;
        OperationsChanged = NewSignal();
        changed.TrySetResult();
    }
}

/// <summary>One attachment. Account admission and role generation are intentionally separate.</summary>
public sealed record AttachmentLease(string AccountId, long RoleId, long Generation, long RequestId)
{
    public bool Cancelled { get; internal set; }
}
