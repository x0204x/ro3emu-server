using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts.Persistence;
using Romsg;

namespace Ro3Emu.Server.Accounts;

/// <summary>Server-owned auction rows and idempotent dungeon clear journal (MG_Auction.proto).</summary>
public sealed class AuctionStore(AccountStore accounts)
{
    private readonly AccountStore _accounts = accounts;

    public sealed class Item
    {
        public long Id { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        internal byte[]? OriginalBytes { get; set; }
        internal OwnerRevisionValue? SourceRevision { get; set; }
        public string SourceKey { get; set; } = "";
        public AuctionType Type { get; set; }
        public int SrcType { get; set; }
        public long GuildId { get; set; }
        public int Entry { get; set; }
        public int Bind { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public AuctionState State { get; set; } = AuctionState.Bidding;
        public long Owner { get; set; }
        public int Price { get; set; }
        public int Reserve { get; set; }
        public int Currency { get; set; } = 1;
        public int Raise { get; set; }
        public int FixedPrice { get; set; }
        public int MaxPrice { get; set; }
        public int TypeAttr { get; set; }
        public long LastBid { get; set; }
        public long Revision { get; set; }
        public bool Auto { get; set; }
        public bool AutoPaused { get; set; }
        public long RunId { get; set; }
        public List<long> Eligible { get; set; } = new();
        public List<long> GiveUp { get; set; } = new();
        public List<long> Joined { get; set; } = new();
        public long Winner { get; set; }
        public bool RandomAward { get; set; }
        // Durable terminal routing obligation: escrow and lot remain intact for operator reconciliation.
        public string SettlementRecoveryReason { get; set; } = "";
    }

    public sealed class Run
    {
        internal OwnerRevisionValue? SourceRevision { get; set; }
        public long Id { get; set; }
        public long GuildId { get; set; }
        public int SrcType { get; set; }
        public int PlayBook { get; set; }
        public Dictionary<int, long> TierGroups { get; set; } = new();
        public List<long> Eligible { get; set; } = new();
        public bool Finalized { get; set; }
        public bool Ready { get; set; }
    }

    public IReadOnlyList<Item> Items()
        => _accounts.ReadSync<List<Item>>(new AuctionReadPlan(AuctionReadKind.Items));

    public Item? Get(long id)
        => _accounts.ReadSync<Item?>(new AuctionReadPlan(AuctionReadKind.Item, id));

    internal void EnlistSave(Item item, RoleItemPlanBuilder transaction)
    {
        var revision = transaction.CaptureEntity(RevisionStore.Entity("auction_item", item.SourceKey), item.SourceRevision);
        if (item.Id != 0 && item.SourceRevision is null && revision.Exists)
            throw new InvalidOperationException("Auction item lacks a source revision.");
        transaction.Add(new AuctionItemWritePlan(item), receipt =>
        {
            var saved = (AuctionItemWriteReceipt)receipt!;
            item.Id = saved.Id;
            item.OriginalBytes = (byte[])saved.State.Clone();
            item.SourceRevision = new OwnerRevisionValue(revision.Key, revision.Revision + 1, true);
        });
    }

    internal void EnlistPrebuy(Prebuy order, RoleItemPlanBuilder transaction)
    {
        if (order.Id != 0)
        {
            var revision = transaction.CaptureEntity(RevisionStore.Entity("auction_prebuy", order.Id), order.SourceRevision);
            if (order.SourceRevision is null && revision.Exists)
                throw new InvalidOperationException("Auction prebuy lacks a source revision.");
        }
        transaction.Add(new AuctionPrebuyWritePlan(order));
    }

    internal void EnlistBonus(long role, string key, int amount, long guild, RoleItemPlanBuilder transaction) =>
        transaction.Add(new AuctionBonusWritePlan(role, key, amount, guild));

    internal void EnlistSource(string key, RoleItemPlanBuilder transaction)
    {
        transaction.CaptureEntity(RevisionStore.Entity("auction_source", key));
        transaction.Add(new AuctionSourceClaimPlan(key));
    }

    internal bool HasSource(string key) =>
        _accounts.ReadSync<bool>(new AuctionReadPlan(AuctionReadKind.SourceExists, sourceKey: key));

    internal void EnlistRunSave(Run run, byte[]? expected, RoleItemPlanBuilder transaction)
    {
        var revision = transaction.CaptureEntity(RevisionStore.Entity("auction_run", run.Id), run.SourceRevision);
        if (expected is not null && run.SourceRevision is null && revision.Exists)
            throw new InvalidOperationException("Auction run lacks a source revision.");
        transaction.Add(new AuctionRunSavePlan(run, expected), _ =>
            run.SourceRevision = new OwnerRevisionValue(revision.Key, revision.Revision + 1, true));
    }

    internal void EnlistBonusClaim(long role, string key, RoleItemPlanBuilder transaction) =>
        transaction.Add(new AuctionBonusClaimPlan(role, key));

    internal void EnlistRunClear(long runId, long roleId, long guildId, int sourceType, int playBook,
        IReadOnlyDictionary<int, long> tiers, RoleItemPlanBuilder transaction) =>
        transaction.Add(new AuctionRunClearPlan(runId, roleId, guildId, sourceType, playBook, tiers));

    public sealed record Prebuy(long Id, long Role, AuctionType Type, int SrcType, int Entry,
        int Quantity, int UnitPrice, int Currency, long Revision, bool Processed, long RequestedId, bool Paused)
    {
        internal Prebuy? Original { get; init; }
        internal OwnerRevisionValue? SourceRevision { get; init; }
    }

    public IReadOnlyList<Prebuy> Prebuys(bool pendingOnly = true)
        => _accounts.ReadSync<List<Prebuy>>(new AuctionReadPlan(AuctionReadKind.Prebuys, pendingOnly: pendingOnly));

    public IReadOnlyList<Run> PendingRuns()
        => _accounts.ReadSync<List<Run>>(new AuctionReadPlan(AuctionReadKind.PendingRuns));

    internal AuctionRunSnapshot GetRunSnapshot(long id)
        => _accounts.ReadSync<AuctionRunSnapshot>(new AuctionReadPlan(AuctionReadKind.RunSnapshot, id));

    public Run? GetRun(long id) => GetRunSnapshot(id).Run;

    public sealed record Bonus(long Role, string Key, int Amount, bool Claimed, long Guild);

    public IReadOnlyList<Bonus> Bonuses(long role)
        => _accounts.ReadSync<List<Bonus>>(new AuctionReadPlan(AuctionReadKind.Bonuses, role));

}
