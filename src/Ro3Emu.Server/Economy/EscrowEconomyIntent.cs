using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ro3Emu.Server.Economy;

/// <summary>E03: resolved external changes authorized by the command; transfers require no external leg.</summary>
public sealed partial class EscrowEconomyIntent
{
    private readonly List<EscrowExternalChange> changes = [];
    private readonly List<EscrowMailRealization> realizations = [];
    private readonly List<EscrowEvidence> evidence = [];
    private EscrowRolePart[][]? childRoleWrites;
    private bool frozen;

    /// <summary>Persisted E03 command namespaces; never infer a family from a reason number or payload shape.</summary>
    internal static bool IsKind(string kind) =>
        kind.StartsWith("market/", StringComparison.Ordinal) ||
        kind.StartsWith("auction/", StringComparison.Ordinal) ||
        kind.StartsWith("mail/", StringComparison.Ordinal) ||
        kind.StartsWith("guild-market/", StringComparison.Ordinal) ||
        kind.StartsWith("activity/adventure/", StringComparison.Ordinal) ||
        kind.StartsWith("e03/", StringComparison.Ordinal) ||
        kind is "activity/fragment-transfer" or "activity/eve-settlement" or "activity/eve-reconcile" or
            "activity/treasure-map-use" or "activity/treasure-expire" or "activity/pass-gift";

    public string OperationId { get; }
    public string CommandKind { get; }
    public string ActorAccount { get; }
    public long ActorRoleId { get; }
    public string SourceKey { get; }
    public long EffectiveUtcMs { get; }
    public EconomyReason TransferReason { get; }

    public EscrowEconomyIntent(string commandKind, string actorAccount = "", long actorRoleId = 0,
        string? sourceKey = null, long effectiveUtcMs = 0,
        EconomyReason transferReason = EconomyReason.TradeEscrow)
        : this(Guid.NewGuid().ToString("N"), commandKind, actorAccount, actorRoleId, sourceKey,
            effectiveUtcMs, transferReason)
    { }

    private EscrowEconomyIntent(string operationId, string commandKind, string actorAccount, long actorRoleId,
        string? sourceKey, long effectiveUtcMs, EconomyReason transferReason)
    {
        if (string.IsNullOrWhiteSpace(commandKind) || commandKind.Length > 128 || !IsKind(commandKind))
            throw new ArgumentException("E03 command kind is required and bounded.", nameof(commandKind));
        if (actorAccount.Length > 512 || actorRoleId < 0 || actorRoleId > 0 && string.IsNullOrWhiteSpace(actorAccount))
            throw new ArgumentException("Invalid E03 actor identity.", nameof(actorAccount));
        if (!EconomyReasonCatalog.Allows(transferReason, EconomyLegKind.Transfer))
            throw new ArgumentOutOfRangeException(nameof(transferReason));
        OperationId = operationId;
        CommandKind = commandKind;
        ActorAccount = actorAccount;
        ActorRoleId = actorRoleId;
        SourceKey = sourceKey ?? "e03/" + operationId;
        if (SourceKey.Length is < 1 or > 512) throw new ArgumentException("Invalid E03 source key.", nameof(sourceKey));
        EffectiveUtcMs = effectiveUtcMs > 0 ? effectiveUtcMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        TransferReason = transferReason;
    }

    public EscrowEconomyIntent Source(EconomyAsset asset, long quantity, EconomyReason reason,
        EconomyCompartment? compartment = null, EconomyOwner? owner = null)
        => Add(asset, quantity, reason, EconomyLegKind.Source, compartment, owner);
    public EscrowEconomyIntent Sink(EconomyAsset asset, long quantity, EconomyReason reason,
        EconomyCompartment? compartment = null, EconomyOwner? owner = null)
        => Add(asset, quantity, reason, EconomyLegKind.Sink, compartment, owner);

    /// <summary>An abstract configured mail attachment may materialize with randomized item attributes on claim.</summary>
    public EscrowEconomyIntent RealizeMailAttachment(EconomyAsset asset, long quantity, long roleId,
        string letterId, int attachmentIndex, int bind)
    {
        if (frozen) throw new InvalidOperationException("E03 intent is already bound.");
        if (asset.Kind != EconomyAssetKind.Item || asset.Id <= 0 || quantity <= 0 || roleId <= 0 ||
            string.IsNullOrWhiteSpace(letterId) || letterId.Length > 128 || letterId.Contains('/') ||
            attachmentIndex < 0 || bind < 0 || realizations.Count >= 8192)
            throw new ArgumentException("Invalid E03 mail attachment realization.");
        var realization = new EscrowMailRealization(asset, quantity, roleId, letterId, attachmentIndex, bind);
        if (realizations.Any(x => x.SourceLocation == realization.SourceLocation))
            throw new ArgumentException("Duplicate E03 mail attachment realization.");
        realizations.Add(realization);
        return this;
    }

    private EscrowEconomyIntent Add(EconomyAsset asset, long quantity, EconomyReason reason, EconomyLegKind kind,
        EconomyCompartment? compartment, EconomyOwner? owner)
    {
        if (frozen) throw new InvalidOperationException("E03 intent is already bound.");
        if (asset.Id <= 0 || asset.Kind is < EconomyAssetKind.Item or > EconomyAssetKind.EventResource ||
            quantity <= 0 || !EconomyReasonCatalog.Allows(reason, kind) || changes.Count >= 8192)
            throw new ArgumentException("E03 external change must have an exact positive amount and catalogued reason.");
        changes.Add(new(kind, asset, quantity, reason, compartment, owner));
        return this;
    }

    public EscrowEconomyIntent Evidence(object value)
    {
        if (frozen) throw new InvalidOperationException("E03 intent is already bound.");
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType());
        if (bytes.Length > 16 * 1024 * 1024 || evidence.Count >= 256)
            throw new ArgumentException("E03 evidence is unbounded.", nameof(value));
        evidence.Add(new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length,
            bytes.Length <= 16 * 1024 ? Encoding.UTF8.GetString(bytes) : null));
        return this;
    }

    internal EscrowEconomyIntent BindCopy(IEnumerable<EscrowExternalChange>? deferred = null)
    {
        frozen = true;
        var copy = new EscrowEconomyIntent(OperationId, CommandKind, ActorAccount, ActorRoleId,
            SourceKey, EffectiveUtcMs, TransferReason);
        copy.changes.AddRange(changes);
        copy.realizations.AddRange(realizations);
        copy.evidence.AddRange(evidence);
        copy.VigorReward = VigorReward;
        if (deferred is not null)
            foreach (var change in deferred)
                copy.Add(change.Asset, change.Quantity, change.Reason, change.Kind,
                    change.Compartment, change.Owner);
        copy.frozen = true;
        return copy;
    }

    /// <summary>Bind the resolved child order to the same copied intent used by commit and replay.</summary>
    internal void BindChildRoleWrites(IEnumerable<IEnumerable<EscrowRolePart>> children)
    {
        if (!frozen) throw new InvalidOperationException("E03 child writes require a copied intent.");
        var resolved = children.Select(parts => parts.Distinct()
            .OrderBy(x => x.RoleId).ThenBy(x => x.Part, StringComparer.Ordinal).ToArray()).ToArray();
        if (resolved.Length is < 1 or > 16384 ||
            resolved.SelectMany(x => x).Any(x => x.RoleId <= 0 || string.IsNullOrWhiteSpace(x.Part)))
            throw new InvalidOperationException("Invalid E03 child role-write declaration.");
        if (childRoleWrites is not null)
        {
            if (childRoleWrites.Length != resolved.Length ||
                childRoleWrites.Where((parts, index) => !parts.SequenceEqual(resolved[index])).Any())
                throw new InvalidOperationException("E03 child role-write declaration changed after binding.");
            return;
        }
        childRoleWrites = resolved;
    }

    internal IReadOnlyList<EscrowExternalChange> Changes => changes;
    internal IReadOnlyList<EscrowMailRealization> Realizations => realizations;
    internal EscrowRolePart[][] ChildRoleWrites =>
        childRoleWrites ?? throw new InvalidOperationException("E03 child writes are not bound.");
    internal byte[] Payload => VigorReward is not null ? VigorPayload : Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        OperationId, CommandKind, ActorAccount, ActorRoleId, SourceKey, EffectiveUtcMs,
        TransferReason, Changes = changes, Realizations = realizations, Evidence = evidence,
        ChildRoleWrites = childRoleWrites ?? throw new InvalidOperationException("E03 child writes are not bound.")
    }));
    internal string Hash => Convert.ToHexString(SHA256.HashData(Payload));
}

internal readonly record struct EscrowExternalChange(EconomyLegKind Kind, EconomyAsset Asset,
    long Quantity, EconomyReason Reason, EconomyCompartment? Compartment, EconomyOwner? Owner);
internal readonly record struct EscrowRolePart(long RoleId, string Part);
internal readonly record struct EscrowMailRealization(EconomyAsset Asset, long Quantity, long RoleId,
    string LetterId, int AttachmentIndex, int Bind)
{
    internal EconomyLocation SourceLocation => new(new(EconomyOwnerKind.Role,
        RoleId.ToString(CultureInfo.InvariantCulture)), EconomyCompartment.Mail,
        $"role/{RoleId.ToString(CultureInfo.InvariantCulture)}/mailbox/{LetterId}/attachment-{AttachmentIndex}");
    internal string AbstractFingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"Attrs={{}}|StringAttrs={{}}|Enchantments={{}}|BindType={Bind}|BindExpire=0|IsLocked=0")));
}
internal sealed record EscrowEvidence(string Hash, int Bytes, string? Json);
