using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ro3Emu.Server.Economy;

/// <summary>Semantic authorization for an E05d value movement, before its concrete SQL locations are known.</summary>
public sealed record EconomyEffect(EconomyAsset Asset, EconomyOwner Owner, EconomyCompartment Compartment,
    long Delta, EconomyLegKind Kind, EconomyReason Reason, int EdgeNumber = 0);

/// <summary>Detached E05d receipt intent. An empty effect list explicitly authorizes zero economic movement.</summary>
public sealed class EconomyEnvelope
{
    public string OperationId { get; }
    public string ActorAccount { get; }
    public long ActorRoleId { get; }
    public string CommandKind { get; }
    public string RequestFingerprint { get; }
    public string SourceKey { get; }
    public long EffectiveUtcMs { get; }
    public IReadOnlyList<EconomyEffect> Effects { get; }
    private readonly byte[] _outcome;
    public byte[] Outcome => (byte[])_outcome.Clone();
    private readonly byte[]? childIntent;
    internal bool HasChildIntent => childIntent is not null;
    internal byte[] ChildIntent => childIntent is null ? throw new InvalidOperationException("E05d typed child intent is unbound.") :
        (byte[])childIntent.Clone();
    private readonly byte[] _intentPayload;
    public byte[] IntentPayload => (byte[])_intentPayload.Clone();
    public string IntentHash { get; }

    public EconomyEnvelope(string operationId, string actorAccount, long actorRoleId, string commandKind,
        string requestFingerprint, string sourceKey, long effectiveUtcMs,
        IReadOnlyList<EconomyEffect> effects, byte[] outcome)
        : this(operationId, actorAccount, actorRoleId, commandKind, requestFingerprint, sourceKey,
            effectiveUtcMs, effects, outcome, null)
    { }

    private EconomyEnvelope(string operationId, string actorAccount, long actorRoleId, string commandKind,
        string requestFingerprint, string sourceKey, long effectiveUtcMs,
        IReadOnlyList<EconomyEffect> effects, byte[] outcome, byte[]? stagedChildIntent)
    {
        static string Required(string value, int limit, string name) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= limit ? value :
                throw new ArgumentException(name + " is missing or too long.", name);
        OperationId = Required(operationId, 128, nameof(operationId));
        ActorAccount = Required(actorAccount, 512, nameof(actorAccount));
        if (actorRoleId <= 0) throw new ArgumentOutOfRangeException(nameof(actorRoleId));
        ActorRoleId = actorRoleId;
        CommandKind = Required(commandKind, 128, nameof(commandKind));
        RequestFingerprint = Required(requestFingerprint, 1024, nameof(requestFingerprint));
        SourceKey = Required(sourceKey, 512, nameof(sourceKey));
        if (effectiveUtcMs <= 0) throw new ArgumentOutOfRangeException(nameof(effectiveUtcMs));
        EffectiveUtcMs = effectiveUtcMs;
        Effects = Array.AsReadOnly((effects ?? throw new ArgumentNullException(nameof(effects))).ToArray());
        _outcome = (byte[])(outcome ?? throw new ArgumentNullException(nameof(outcome))).Clone();
        childIntent = stagedChildIntent is null ? null : (byte[])stagedChildIntent.Clone();
        if (childIntent is { Length: > 512 * 1024 }) throw new ArgumentException("E05d typed child intent exceeds receipt limit.");
        if (Effects.Count > 512 || _outcome.Length > 16 * 1024) throw new ArgumentException("E05d envelope exceeds its receipt limit.");
        foreach (var effect in Effects)
        {
            if (!Enum.IsDefined(effect.Asset.Kind) || effect.Asset.Id <= 0 || effect.Asset.Epoch is null ||
                !Enum.IsDefined(effect.Owner.Kind) || string.IsNullOrWhiteSpace(effect.Owner.Id) ||
                !Enum.IsDefined(effect.Compartment) || !EconomyReasonCatalog.Allows(effect.Reason, effect.Kind) ||
                effect.Delta == 0 || effect.Delta == long.MinValue ||
                (effect.Kind == EconomyLegKind.Source && effect.Delta < 0) ||
                (effect.Kind == EconomyLegKind.Sink && effect.Delta > 0) ||
                (effect.Kind == EconomyLegKind.Transfer && effect.EdgeNumber <= 0) ||
                (effect.Kind != EconomyLegKind.Transfer && effect.EdgeNumber != 0))
                throw new ArgumentException("Invalid E05d semantic economic effect.", nameof(effects));
        }
        foreach (var edge in Effects.Where(x => x.Kind == EconomyLegKind.Transfer).GroupBy(x => x.EdgeNumber))
        {
            var pair = edge.ToArray();
            if (pair.Length != 2 || pair[0].Asset != pair[1].Asset ||
                pair[0].Delta != -pair[1].Delta || pair[0].Owner == pair[1].Owner && pair[0].Compartment == pair[1].Compartment)
                throw new ArgumentException("Transfer edge must have opposite effects in distinct compartments.", nameof(effects));
        }
        _intentPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            OperationId, ActorAccount, ActorRoleId, CommandKind, RequestFingerprint, SourceKey, EffectiveUtcMs,
            Effects = Effects.OrderBy(x => x.EdgeNumber).ThenBy(x => (int)x.Asset.Kind).ThenBy(x => x.Asset.Id)
                .ThenBy(x => x.Owner.Id, StringComparer.Ordinal).ThenBy(x => (int)x.Compartment).ToArray(),
            Outcome = Convert.ToBase64String(_outcome),
            ChildIntent = childIntent is null ? null : Convert.ToBase64String(childIntent)
        }));
        if (_intentPayload.Length > 2 * 1024 * 1024) throw new ArgumentException("E05d canonical intent exceeds receipt limit.");
        IntentHash = Convert.ToHexString(SHA256.HashData(_intentPayload));
    }

    internal EconomyEnvelope BindChildIntent(byte[] stagedChildIntent) => new(OperationId, ActorAccount,
        ActorRoleId, CommandKind, RequestFingerprint, SourceKey, EffectiveUtcMs, Effects, _outcome,
        stagedChildIntent ?? throw new ArgumentNullException(nameof(stagedChildIntent)));
}
