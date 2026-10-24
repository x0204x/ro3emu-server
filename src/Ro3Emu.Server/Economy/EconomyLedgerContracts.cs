namespace Ro3Emu.Server.Economy;

public enum EconomyLegKind { Source = 1, Sink = 2, Transfer = 3, Metadata = 4 }

/// <summary>E00 catalogue v1. Unknown/correction/generic-save reasons cannot qualify.</summary>
public enum EconomyReason
{
    Unknown = 0, StarterGrant = 1, MonsterDrop = 2, QuestReward = 3, DailyReward = 4,
    ActivityReward = 5, GatherYield = 6, GmGrant = 7, GmRemove = 8,
    NpcShopGoods = 9, NpcShopPayment = 10, NpcShopBuyback = 11,
    CraftInput = 12, CraftOutput = 13, DecomposeInput = 14, DecomposeOutput = 15,
    ItemUse = 16, RefineCost = 17, SkillCost = 18, PetCost = 19, PetRefund = 20,
    MarketTax = 21, StallTax = 22, AuctionFee = 23,
    MailDelivery = 24, MailClaim = 25, MailReturn = 26, MailExpiry = 27,
    TradeEscrow = 28, AuctionReserve = 29, AuctionRefund = 30, AuctionSettlement = 31,
    GuildResourceGrant = 32, GuildResourceSpend = 33, GuildResourceTransfer = 34,
    GuildMarketCapDiscard = 35, SystemRestock = 36, SystemRetire = 37,
    SeasonReset = 38, ExplicitDestruction = 39, ItemMetadata = 40,
    InventoryRelocation = 41, MountCost = 42, MountGoods = 43,
    WardrobeCost = 44, WardrobeGoods = 45, TotemCost = 46, TotemGoods = 47,
    EmblemCost = 48, EmblemGoods = 49, GenreCost = 50, GenreGoods = 51,
    PetGoods = 52, TotemRefund = 53,
    DungeonRewardDelivery = 54, // E04 used 41 on its branch; 41 was already InventoryRelocation on master
    // E05a reserved persisted values; do not renumber or reuse sibling branch ranges.
    EquipmentCost = 140, AppraisalCost = 141, EnchantCost = 142, EnchantRefund = 143,
    CardInput = 144, CardOutput = 145, ShopPayment = 146, ShopGoods = 147,
    ItemBoxGoods = 148, // award box contents (cd13a/character-box)
    // C/D 13e + E02 reserved range 120-139.
    OwnedGrant = 120, OwnedAwardCost = 121, DailyReset = 122,
    AchievementReward = 123, // achievement / Trophy tier claims (cd13e/achievement-award)
    AdventureGroupReward = 124, // Adventure Group level reward claims (cd13e/advguild-award)
    MonsterHuntReward = 125, // Monster Hunt monster and area stage claims (cd13e/monsterhunt-award)
    GuildReward = 126, // guild weekly activity chests and activity dividends (cd13e/guild-award)
    GrowthClassReward = 127, // Growth Classroom rank rewards (cd13e/growthclass-award)
    LibraryReward = 128, // Codex monster unlock Collection Points (cd13e/library-award)
    FirstHeartReward = 129, // Seven-Day Goal task and score reward claims (cd13e/firstheart-award)
    // C/D 13d used 41-45 on its branch; renumbered at merge (the values are persisted).
    FriendGiftCost = 55, CaravanPendingDelivery = 56, GuildActivityCost = 57,
    GuildServiceCost = 58, ChallengePendingDelivery = 59,
    ValhallaCost = 60, // Valhalla Trials Blessing: Einherjar Coins spent on the Odin statue (cd13b/valhalla-pray)
    ParagonCost = 61, ParagonGoods = 62, // Astral Level Star Cores installed / returned (cd13b/paragon-*)
    // Persisted values 200..219 are reserved exclusively for C/D 13c / E05c.
    LifeCost = 200, LifeGoods = 201, SeasonCost = 202, SeasonGoods = 203,
    SeasonRefund = 204, SkillPlanCost = 205, SkillPlanRefund = 206,
    CastAdmissionCost = 207, CastAdmissionRefund = 208,
    // E03 escrow families.
    MarketSlotUnlock = 301, StallOpenFee = 302, GuildMarketFee = 303
}

public static class EconomyReasonCatalog
{
    public const string Version = "economy-reasons-v15"; // each merge of a branch's reasons gets a new version

    public static bool Allows(EconomyReason reason, EconomyLegKind kind) => reason switch
    {
        EconomyReason.StarterGrant or EconomyReason.MonsterDrop or EconomyReason.QuestReward or
        EconomyReason.DailyReward or EconomyReason.ActivityReward or EconomyReason.GatherYield or
        EconomyReason.GmGrant or EconomyReason.NpcShopGoods or EconomyReason.NpcShopBuyback or
        EconomyReason.CraftOutput or EconomyReason.DecomposeOutput or EconomyReason.PetRefund or
        EconomyReason.GuildResourceGrant or EconomyReason.SystemRestock or EconomyReason.MountGoods or
        EconomyReason.WardrobeGoods or EconomyReason.TotemGoods or EconomyReason.EmblemGoods or
        EconomyReason.GenreGoods or EconomyReason.PetGoods or EconomyReason.TotemRefund or
        EconomyReason.EnchantRefund or EconomyReason.CardOutput or EconomyReason.ShopGoods or EconomyReason.ItemBoxGoods or
        EconomyReason.OwnedGrant or EconomyReason.AchievementReward or EconomyReason.AdventureGroupReward or EconomyReason.MonsterHuntReward or EconomyReason.GuildReward or
        EconomyReason.GrowthClassReward or EconomyReason.LibraryReward or EconomyReason.FirstHeartReward or
        EconomyReason.LifeGoods or EconomyReason.SeasonGoods or EconomyReason.SeasonRefund or
        EconomyReason.SkillPlanRefund or EconomyReason.CastAdmissionRefund or EconomyReason.ParagonGoods => kind == EconomyLegKind.Source,
        EconomyReason.GmRemove or EconomyReason.NpcShopPayment or EconomyReason.CraftInput or
        EconomyReason.DecomposeInput or EconomyReason.ItemUse or EconomyReason.RefineCost or
        EconomyReason.SkillCost or EconomyReason.PetCost or EconomyReason.MarketTax or
        EconomyReason.StallTax or EconomyReason.AuctionFee or EconomyReason.MailExpiry or
        EconomyReason.GuildResourceSpend or EconomyReason.GuildMarketCapDiscard or
        EconomyReason.SystemRetire or EconomyReason.SeasonReset or EconomyReason.ExplicitDestruction or
        EconomyReason.MountCost or EconomyReason.WardrobeCost or EconomyReason.TotemCost or
        EconomyReason.EmblemCost or EconomyReason.GenreCost or EconomyReason.EquipmentCost or
        EconomyReason.AppraisalCost or EconomyReason.EnchantCost or EconomyReason.CardInput or EconomyReason.ShopPayment or
        EconomyReason.OwnedAwardCost or EconomyReason.DailyReset or
        EconomyReason.FriendGiftCost or EconomyReason.GuildActivityCost or EconomyReason.GuildServiceCost or
        EconomyReason.LifeCost or EconomyReason.SeasonCost or EconomyReason.SkillPlanCost or
        EconomyReason.CastAdmissionCost or EconomyReason.MarketSlotUnlock or
        EconomyReason.StallOpenFee or EconomyReason.GuildMarketFee or EconomyReason.ValhallaCost or
        EconomyReason.ParagonCost => kind == EconomyLegKind.Sink,
        EconomyReason.MailDelivery or EconomyReason.MailClaim or EconomyReason.MailReturn or
        EconomyReason.TradeEscrow or EconomyReason.AuctionReserve or EconomyReason.AuctionRefund or
        EconomyReason.AuctionSettlement or EconomyReason.GuildResourceTransfer or EconomyReason.InventoryRelocation or
        EconomyReason.DungeonRewardDelivery or
        EconomyReason.CaravanPendingDelivery or EconomyReason.ChallengePendingDelivery => kind == EconomyLegKind.Transfer,
        EconomyReason.ItemMetadata => kind == EconomyLegKind.Metadata,
        _ => false
    };
}

/// <summary>
/// Neutral/instance-changing legs carry before/after fingerprints; null denotes no holding. Transfer edges are
/// explicit pairs, including old/new identities, not an inferred net difference. A source/sink's external
/// counterpart is defined by its kind/reason and has no real-owner location.
/// </summary>
public sealed record EconomyLeg(long CommitSequence, string OperationId, int LegNumber, EconomyLegKind Kind,
    EconomyReason Reason, EconomyAsset Asset, EconomyLocation Location, long Delta, int EdgeNumber = 0,
    EconomyLocation? Counterparty = null, string? BeforeFingerprint = null, string? AfterFingerprint = null,
    string SourceKey = "");

public sealed record EconomyReceipt(long CommitSequence, string OperationId, int LegCount,
    IReadOnlyList<string>? SourceKeys = null, bool Committed = true, int CommitOrder = 0)
{
    public long ActorRoleId { get; init; }
    public string CommandKind { get; init; } = "";
    public EconomyRewardEvidence? RewardEvidence { get; init; }
    public long? BeforeProgressRevision { get; init; }
    public long? AfterProgressRevision { get; init; }
}
public sealed record EconomySourceClaim(string SourceKey, string OperationId);

/// <summary>
/// An adapter must certify the entire (FromExclusive, ThroughInclusive] range and retained source claims.
/// E00 defines this in-memory boundary only; E01 binds it to the merged C/D receipt/watermark and archive reader.
/// Empty legs do not certify absence of writes. CompleteHistory may only be true for an independently read,
/// continuous manifest. PriorSourceClaims include retained/tombstoned source keys before the opening cut.
/// </summary>
public sealed record EconomyLedgerWindow(string RealmId, string CatalogVersion, string CoverageVersion,
    long FromExclusive, long ThroughInclusive, bool CompleteHistory,
    IReadOnlyList<EconomyLeg> Legs, IReadOnlyList<EconomyReceipt> Receipts,
    IReadOnlyList<EconomySourceClaim> PriorSourceClaims,
    IReadOnlyList<EconomyIssue> Issues, IReadOnlyList<string> UncoveredMutationFamilies)
{
    public string ReasonVersion { get; init; } = EconomyReasonCatalog.Version;
}

public enum EconomyVerdict { Pass, Fail, NotMeasured }
// Decimal strings preserve exact wide-integer diagnostics in JSON consumers as well as .NET.
public sealed record EconomyResidual(EconomyAsset Asset, string Opening, string Closing, string Sources,
    string Sinks, string RealOwnerLegs, string Residual, string LegResidual);
public sealed record EconomyLocationResidual(EconomyAsset Asset, EconomyLocation Location, string Residual);
public sealed record EconomyCheckResult(EconomyVerdict Verdict, IReadOnlyList<EconomyResidual> Assets,
    IReadOnlyList<EconomyLocationResidual> Locations, IReadOnlyList<EconomyIssue> Issues,
    IReadOnlyList<string> UncoveredMutationFamilies);
