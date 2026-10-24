using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ro3Emu.Server.Economy;

/// <summary>A caller-authorized, positive change to one denomination.</summary>
public readonly record struct EconomyQuantity(EconomyAsset Asset, long Quantity);

/// <summary>Frozen progression and conversion result for an E04 reward.</summary>
public sealed record EconomyRewardEvidence(
    int BeforeBaseLevel, long BeforeBaseExp, int AfterBaseLevel, long AfterBaseExp,
    int BeforeJobLevel, long BeforeJobExp, int AfterJobLevel, long AfterJobExp,
    long ResolvedBaseExp, long ResolvedJobExp,
    IReadOnlyList<long>? BaseLevelCosts = null, IReadOnlyList<long>? JobLevelCosts = null,
    long BaseCapDiscard = 0, long JobCapDiscard = 0,
    IReadOnlyList<EconomyQuantity>? OriginalGoods = null,
    IReadOnlyList<EconomyQuantity>? ConvertedInputs = null,
    IReadOnlyList<EconomyQuantity>? ConvertedOutputs = null);

/// <summary>Detached semantic intent for one admitted transaction. IDs are stable across internal retry.</summary>
public sealed class EconomyMutation
{
    // Stage G: one options instance; a new one per mutation rebuilt the JSON type metadata every time.
    private static readonly JsonSerializerOptions CanonicalJson =
        new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    // The shipped item catalog has 3,190 denominations. Allow both cost and goods for
    // every configured entry, with room for currency, while retaining a finite intent bound.
    public const int MaxFamilyQuantities = 8192;
    public const int MaxFamilyIntentBytes = 1024 * 1024;
    public bool IsFamily => IsFamilyKind(CommandKind);
    public bool IsProgression => IsProgressionKind(CommandKind);
    public static bool IsProgressionKind(string kind) => kind.StartsWith("cd13c/", StringComparison.Ordinal) &&
        new[] { "life", "season", "skill", "cast" }.Any(family =>
            kind[6..] == family || kind[6..].StartsWith(family + "-", StringComparison.Ordinal));
    public static bool IsFamilyKind(string kind)
    {
        if (CharacterFamilyEconomy.IsKind(kind)) return true;
        if (!kind.StartsWith("cd13b/", StringComparison.Ordinal)) return false;
        string name = kind[6..];
        return new[] { "pet", "mount", "wardrobe", "totem", "emblem", "genre", "valhalla", "paragon" }
            .Any(family => name == family || name.StartsWith(family + "-", StringComparison.Ordinal));
    }
    public string OperationId { get; }
    internal EconomyCatalog? ProgressionCatalog { get; }
    public string? PlanFingerprint { get; }
    internal EconomyMutation BindProgressionPlan(string fingerprint) => !IsProgression
        ? throw new InvalidOperationException("Only C/D 13c binds progression plans.")
        : new(OperationId, ActorAccount, ActorRoleId, CommandKind, RequestFingerprint, SourceKey,
            EffectiveUtcMs, Costs, Goods, Outcome, progressionCatalog: ProgressionCatalog, planFingerprint: fingerprint);
    public string ActorAccount { get; }
    public long ActorRoleId { get; }
    public string CommandKind { get; }
    public string RequestFingerprint { get; }
    public string IntentHash { get; }
    private readonly byte[] intentPayload;
    public byte[] IntentPayload => (byte[])intentPayload.Clone();
    public string SourceKey { get; }
    public long EffectiveUtcMs { get; }
    public IReadOnlyList<EconomyQuantity> Costs { get; }
    public IReadOnlyList<EconomyQuantity> Goods { get; }
    public EconomyRewardEvidence? RewardEvidence { get; }
    private readonly byte[] outcome;
    public byte[] Outcome => (byte[])outcome.Clone();

    public EconomyMutation(string operationId, string actorAccount, long actorRoleId, string commandKind,
        string requestFingerprint, string sourceKey, long effectiveUtcMs,
        IReadOnlyList<EconomyQuantity> costs, IReadOnlyList<EconomyQuantity> goods, byte[] outcome,
        EconomyRewardEvidence? rewardEvidence = null, EconomyCatalog? progressionCatalog = null,
        string? planFingerprint = null)
    {
        static string Required(string value, int limit, string name) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= limit ? value : throw new ArgumentException(name + " is missing or too long.", name);
        OperationId = Required(operationId, 128, nameof(operationId));
        ActorAccount = Required(actorAccount, 512, nameof(actorAccount));
        if (actorRoleId <= 0) throw new ArgumentOutOfRangeException(nameof(actorRoleId));
        ActorRoleId = actorRoleId;
        CommandKind = Required(commandKind, 128, nameof(commandKind));
        if (!IsProgression && (progressionCatalog != null || planFingerprint != null))
            throw new ArgumentException("Progression evidence cannot authorize another family.");
        ProgressionCatalog = progressionCatalog;
        PlanFingerprint = planFingerprint;
        RequestFingerprint = Required(requestFingerprint, 1024, nameof(requestFingerprint));
        SourceKey = Required(sourceKey, 512, nameof(sourceKey));
        if (effectiveUtcMs <= 0) throw new ArgumentOutOfRangeException(nameof(effectiveUtcMs));
        EffectiveUtcMs = effectiveUtcMs;
        Costs = Array.AsReadOnly((costs ?? throw new ArgumentNullException(nameof(costs))).ToArray());
        Goods = Array.AsReadOnly((goods ?? throw new ArgumentNullException(nameof(goods))).ToArray());
        bool reward = IsRewardCommand(commandKind);
        if (reward != (rewardEvidence is not null))
            throw new ArgumentException("E04 reward commands require progression evidence; other commands cannot carry it.");
        if ((!IsFamily && !IsProgression && !reward && commandKind != "cd13e/daily-reset" && Costs.Count + Goods.Count == 0) ||
            Costs.Count + Goods.Count > (IsFamily || IsProgression ? MaxFamilyQuantities : 32) ||
            Costs.Concat(Goods).Any(q => q.Quantity <= 0 || q.Asset.Id <= 0 || q.Asset.Epoch != "" ||
                q.Asset.Kind is not (EconomyAssetKind.Item or EconomyAssetKind.Currency)))
            throw new ArgumentException("Economy quantities must be bounded, positive catalog item/currency amounts.");
        RewardEvidence = rewardEvidence is null ? null : Freeze(rewardEvidence);
        if (RewardEvidence is not null) ValidateRewardEvidence(RewardEvidence, Goods);
        this.outcome = (byte[])(outcome ?? throw new ArgumentNullException(nameof(outcome))).Clone();
        if (this.outcome.Length > 16 * 1024) throw new ArgumentException("Resolved outcome exceeds E01 receipt limit.", nameof(outcome));
        // A caller-provided request fingerprint is useful evidence but cannot authorize altered resolved costs/goods.
        // JSON's fixed property order and sorted denominations make this stable across an internal retry.
        string canonical = JsonSerializer.Serialize(new
        {
            OperationId, ActorAccount, ActorRoleId, CommandKind, RequestFingerprint, SourceKey, EffectiveUtcMs,
            Costs = Costs.OrderBy(q => (int)q.Asset.Kind).ThenBy(q => q.Asset.Id).ThenBy(q => q.Asset.Epoch, StringComparer.Ordinal).ToArray(),
            Goods = Goods.OrderBy(q => (int)q.Asset.Kind).ThenBy(q => q.Asset.Id).ThenBy(q => q.Asset.Epoch, StringComparer.Ordinal).ToArray(),
            Outcome = Convert.ToBase64String(this.outcome),
            RewardEvidence, PlanFingerprint,
            ProgressionCatalogVersion = progressionCatalog?.DenominationsVersion
        }, CanonicalJson);
        intentPayload = Encoding.UTF8.GetBytes(canonical);
        if (intentPayload.Length > (IsFamily || IsProgression ? MaxFamilyIntentBytes : 64 * 1024))
            throw new ArgumentException("Canonical intent exceeds receipt limit.");
        IntentHash = Convert.ToHexString(SHA256.HashData(intentPayload));
    }

    public static bool IsRewardCommand(string command) => command is
        "monster-kill" or "gather-yield" or "dungeon-reward" or "tower-reward" or "world-revive" or
        "cd13e/quest-award" or "cd13e/achievement-award" or "cd13e/advguild-award" or "cd13e/monsterhunt-award" or "cd13e/guild-award" or "cd13e/growthclass-award" or "cd13e/library-award" or "cd13e/firstheart-award" or "cd13e/daily-stage" or "cd13e/journey-victory" or
        "cd13e/character-exp" or "cd13e/grant" or "cd13e/gm-grant";

    public static bool IsOwnedExchangeCommand(string command) => command is
        "cd13e/tower-milestone" or "cd13e/grant" or "cd13e/character-exp" or
        "cd13e/quest-award" or "cd13e/achievement-award" or "cd13e/advguild-award" or "cd13e/monsterhunt-award" or "cd13e/guild-award" or "cd13e/growthclass-award" or "cd13e/library-award" or "cd13e/firstheart-award" or "cd13e/daily-stage" or "cd13e/journey-victory" or
        "cd13e/gm-grant" or "cd13e/daily-reset";

    private static EconomyRewardEvidence Freeze(EconomyRewardEvidence value) => value with
    {
        BaseLevelCosts = Array.AsReadOnly((value.BaseLevelCosts ?? []).ToArray()),
        JobLevelCosts = Array.AsReadOnly((value.JobLevelCosts ?? []).ToArray()),
        OriginalGoods = Array.AsReadOnly((value.OriginalGoods ?? []).ToArray()),
        ConvertedInputs = Array.AsReadOnly((value.ConvertedInputs ?? []).ToArray()),
        ConvertedOutputs = Array.AsReadOnly((value.ConvertedOutputs ?? []).ToArray())
    };

    public static void ValidateRewardEvidence(EconomyRewardEvidence e, IReadOnlyList<EconomyQuantity> finalGoods)
    {
        var baseCosts = e.BaseLevelCosts ?? [];
        var jobCosts = e.JobLevelCosts ?? [];
        if (e.BeforeBaseLevel <= 0 || e.BeforeJobLevel <= 0 || e.AfterBaseLevel < e.BeforeBaseLevel ||
            e.AfterJobLevel < e.BeforeJobLevel || e.BeforeBaseExp < 0 || e.BeforeJobExp < 0 ||
            e.AfterBaseExp < 0 || e.AfterJobExp < 0 || e.ResolvedBaseExp < 0 || e.ResolvedJobExp < 0 ||
            e.BaseCapDiscard < 0 || e.JobCapDiscard < 0 ||
            baseCosts.Count != e.AfterBaseLevel - e.BeforeBaseLevel ||
            jobCosts.Count != e.AfterJobLevel - e.BeforeJobLevel ||
            baseCosts.Any(x => x <= 0) || jobCosts.Any(x => x < 0))
            throw new ArgumentException("Invalid E04 progression transition evidence.");
        if ((System.Numerics.BigInteger)e.BeforeBaseExp + e.ResolvedBaseExp -
                baseCosts.Aggregate(System.Numerics.BigInteger.Zero, (sum, x) => sum + x) - e.BaseCapDiscard != e.AfterBaseExp ||
            (System.Numerics.BigInteger)e.BeforeJobExp + e.ResolvedJobExp -
                jobCosts.Aggregate(System.Numerics.BigInteger.Zero, (sum, x) => sum + x) - e.JobCapDiscard != e.AfterJobExp)
            throw new ArgumentException("Resolved EXP does not explain committed level and remainder transitions.");
        var original = e.OriginalGoods ?? [];
        var inputs = e.ConvertedInputs ?? [];
        var outputs = e.ConvertedOutputs ?? [];
        if (original.Count + inputs.Count + outputs.Count > 64 ||
            original.Concat(inputs).Concat(outputs).Any(q => q.Quantity <= 0 || q.Asset.Id <= 0 ||
                q.Asset.Epoch != "" || q.Asset.Kind is not (EconomyAssetKind.Item or EconomyAssetKind.Currency)))
            throw new ArgumentException("Invalid E04 conversion evidence.");
        if (original.Count == 0 && inputs.Count == 0 && outputs.Count == 0) return;
        var amounts = new Dictionary<EconomyAsset, System.Numerics.BigInteger>();
        foreach (var q in original) amounts[q.Asset] = amounts.GetValueOrDefault(q.Asset) + q.Quantity;
        foreach (var q in inputs) amounts[q.Asset] = amounts.GetValueOrDefault(q.Asset) - q.Quantity;
        foreach (var q in outputs) amounts[q.Asset] = amounts.GetValueOrDefault(q.Asset) + q.Quantity;
        foreach (var q in finalGoods) amounts[q.Asset] = amounts.GetValueOrDefault(q.Asset) - q.Quantity;
        if (amounts.Values.Any(x => x != 0))
            throw new ArgumentException("Original reward and conversion do not explain final goods.");
    }
}

public sealed class EconomyOperationConflictException(string message) : InvalidOperationException(message);
public sealed class EconomyOutcomeUnknownException(string operationId, Exception writeFailure, Exception resolutionFailure) : IOException("Economic operation " + operationId + " has an unresolved commit outcome; owner reload is required.",
        new AggregateException(writeFailure, resolutionFailure))
{
    public string OperationId { get; } = operationId;
}
