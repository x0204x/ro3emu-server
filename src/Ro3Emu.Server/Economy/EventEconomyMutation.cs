using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Economy;

/// <summary>E05e semantic change per beneficiary, independently checked against the persisted holdings.</summary>
public readonly record struct EventEconomyOwnerDelta(EconomyOwner Owner, EconomyAsset Asset, long Delta);

/// <summary>A frozen event result and its authorized faucets, sinks and beneficiaries. No gameplay callback enters SQL.</summary>
public sealed class EventEconomyMutation
{
    public EconomyCatalog Catalog { get; }
    public string OperationId { get; }
    public string ActorAccount { get; }
    public long ActorRoleId { get; }
    public string CommandKind { get; }
    public string SourceKey { get; }
    public long EffectiveUtcMs { get; }
    public IReadOnlyList<EconomyQuantity> Costs { get; }
    public IReadOnlyList<EconomyQuantity> Goods { get; }
    public IReadOnlyList<EventEconomyOwnerDelta> OwnerDeltas { get; }
    public EconomyReason SourceReason { get; }
    public EconomyReason SinkReason { get; }
    public EconomyReason TransferReason { get; }
    private readonly byte[] outcome, payload;
    public byte[] Outcome => (byte[])outcome.Clone();
    public byte[] IntentPayload => (byte[])payload.Clone();
    public string IntentHash { get; }
    public string RequestFingerprint { get; }
    public string? ChildPlanHash { get; }

    public EventEconomyMutation(EconomyCatalog catalog, string operationId, string actorAccount, long actorRoleId,
        string commandKind, string sourceKey, long effectiveUtcMs, IReadOnlyList<EconomyQuantity> costs,
        IReadOnlyList<EconomyQuantity> goods, IReadOnlyList<EventEconomyOwnerDelta> ownerDeltas, byte[] outcome,
        EconomyReason sourceReason = EconomyReason.ActivityReward, EconomyReason sinkReason = EconomyReason.ItemUse,
        EconomyReason transferReason = EconomyReason.MailDelivery, string? childPlanHash = null)
    {
        static string Required(string text, int limit) => !string.IsNullOrWhiteSpace(text) && text.Length <= limit
            ? text : throw new ArgumentException("Missing or oversized event identity.");
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        OperationId = Required(operationId, 128); ActorAccount = Required(actorAccount, 512);
        CommandKind = Required(commandKind, 128); SourceKey = Required(sourceKey, 512);
        if (!CommandKind.StartsWith("event/", StringComparison.Ordinal) || actorRoleId < 0 || effectiveUtcMs <= 0)
            throw new ArgumentException("Invalid event family, actor or effective time.");
        ActorRoleId = actorRoleId; EffectiveUtcMs = effectiveUtcMs;
        Costs = Array.AsReadOnly(costs.ToArray()); Goods = Array.AsReadOnly(goods.ToArray());
        OwnerDeltas = Array.AsReadOnly(ownerDeltas.ToArray());
        if (Costs.Concat(Goods).Any(q => q.Quantity <= 0 || !ValidAsset(q.Asset)) ||
            OwnerDeltas.Any(q => !ValidAsset(q.Asset) || !Enum.IsDefined(q.Owner.Kind) || string.IsNullOrWhiteSpace(q.Owner.Id)))
            throw new ArgumentException("Invalid event amounts or beneficiary identity.");
        if (!EconomyReasonCatalog.Allows(sourceReason, EconomyLegKind.Source) ||
            !EconomyReasonCatalog.Allows(sinkReason, EconomyLegKind.Sink) ||
            !EconomyReasonCatalog.Allows(transferReason, EconomyLegKind.Transfer))
            throw new ArgumentException("Invalid event accounting reasons.");
        SourceReason = sourceReason; SinkReason = sinkReason; TransferReason = transferReason;
        if (childPlanHash is not null && (childPlanHash.Length != 64 || childPlanHash.Any(c => !char.IsAsciiHexDigit(c))))
            throw new ArgumentException("Invalid detached event plan hash.");
        ChildPlanHash = childPlanHash;
        this.outcome = (byte[])outcome.Clone();
        if (this.outcome.Length is 0 or > 262144) throw new ArgumentException("Event result exceeds receipt bound.");
        RequestFingerprint = Convert.ToHexString(SHA256.HashData(this.outcome));
        payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, OperationId, ActorAccount, ActorRoleId,
            CommandKind, RequestFingerprint, SourceKey, EffectiveUtcMs, CatalogVersion = Catalog.DenominationsVersion,
            Costs, Goods, OwnerDeltas, SourceReason, SinkReason, TransferReason, ChildPlanHash, Outcome = Convert.ToBase64String(this.outcome)
        });
        if (payload.Length > 1048576) throw new ArgumentException("Event intent exceeds receipt bound.");
        IntentHash = Convert.ToHexString(SHA256.HashData(payload));
    }

    internal EventEconomyMutation BindPlan(string hash) => new(Catalog, OperationId, ActorAccount, ActorRoleId,
        CommandKind, SourceKey, EffectiveUtcMs, Costs, Goods, OwnerDeltas, outcome, SourceReason, SinkReason, TransferReason, hash);

    internal static bool ValidAsset(EconomyAsset asset) => Enum.IsDefined(asset.Kind) && asset.Id > 0 &&
        asset.Epoch != null && (asset.Kind == EconomyAssetKind.EventResource ? asset.Epoch.Length > 0 : asset.Epoch == "");
}

/// <summary>Family-local semantic capture; EXP is result progression rather than a spendable ItemConfig purse.</summary>
public static class EventEconomy
{
    private static readonly ConditionalWeakTable<GameData, EconomyCatalog> Catalogs = new();
    public static EconomyCatalog Catalog(GameData data) => Catalogs.GetValue(data, value =>
        new EconomyCatalog(value.Table("ItemConfig").ToDictionary(row => long.Parse(row.Key, CultureInfo.InvariantCulture),
            row => GameData.Int(row.Value, "_iType") == 1 ? EconomyAssetKind.Currency : EconomyAssetKind.Item),
            value.Table("PublicParamConfig").TryGetValue("AUCTION_CURRENCY", out var bonus) ? bonus.GetInt64() : 1));

    public static EventEconomyMutation ForRole(GameData data, GameSession session, string kind, string source,
        IEnumerable<(int Entry, long Count)> costs, IEnumerable<AwardGrant> grants, object outcome)
    {
        var catalog = Catalog(data);
        EconomyAsset Asset(int entry) => catalog.TryAsset(entry, out var asset) ? asset
            : throw new ArgumentException("Uncatalogued event item " + entry);
        var debit = costs.Where(q => q.Count > 0 && q.Entry is not (100 or 101))
            .Select(q => new EconomyQuantity(Asset(q.Entry), q.Count)).ToArray();
        var credit = grants.Where(q => q.Count > 0 && q.Entry is not (100 or 101))
            .Select(q => new EconomyQuantity(Asset(q.Entry), q.Count)).ToArray();
        var owner = new EconomyOwner(EconomyOwnerKind.Role, session.Role!.Id.ToString(CultureInfo.InvariantCulture));
        var deltas = debit.Select(q => new EventEconomyOwnerDelta(owner, q.Asset, -q.Quantity))
            .Concat(credit.Select(q => new EventEconomyOwnerDelta(owner, q.Asset, q.Quantity))).ToArray();
        return new(catalog, Guid.NewGuid().ToString("N"), session.Account!, session.Role.Id,
            kind.StartsWith("event/", StringComparison.Ordinal) ? kind : "event/" + kind, source,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), debit, credit, deltas, JsonSerializer.SerializeToUtf8Bytes(outcome));
    }
}
