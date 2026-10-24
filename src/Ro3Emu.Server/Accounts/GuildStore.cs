using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>Durable guild aggregate and exclusive role membership.</summary>
public sealed class GuildStore(AccountStore accounts)
{
    /// <summary>The aggregate's table, as <see cref="RowMissingException"/> names it.</summary>
    internal const string Table = "guild";

    private readonly AccountStore _accounts = accounts;

    public sealed class State
    {
        internal OwnerRevisionValue? CapturedRevision { get; set; }
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Announce { get; set; } = "";
        public string Notice { get; set; } = "";
        public long CreateTime { get; set; }
        public int Status { get; set; } = 2;
        public long HelperDeadlineMs { get; set; }
        public int Level { get; set; } = 1;
        public long Background { get; set; }
        public long Icon { get; set; }
        public bool AutoAccept { get; set; }
        public bool AutoCreateHelper { get; set; }
        public int JoinLevelLimit { get; set; }
        public long Money { get; set; }
        public long Contribute { get; set; }
        public long Materials { get; set; }
        public long Reputation { get; set; }
        public long AuctionMaterials { get; set; }
        public long LastChangeNameTime { get; set; }
        public long LastChangeNoticeTime { get; set; }
        public long LastRecruitMs { get; set; }
        public List<string> HistoryNames { get; set; } = new();
        public Dictionary<long, Member> Members { get; set; } = new();
        public Dictionary<long, Application> Applications { get; set; } = new();
        /// <summary>Founding responses waiting for the founder's approval (ApplyTime in ms; GuildService.Responders).</summary>
        public Dictionary<long, Application> HelperApplies { get; set; } = new();
        public Dictionary<int, List<int>> Rights { get; set; } = new();
        public Dictionary<long, long> Buildings { get; set; } = new();
        public Dictionary<long, long> BuildingEndTimes { get; set; } = new();
        /// <summary>Guild Activity (item 106) earned this week and last week; ActivityWeek = the week's Monday 05:00 (unix s).</summary>
        public long CurActivity { get; set; }
        public long LastWeekActivity { get; set; }
        public long ActivityWeek { get; set; }
        /// <summary>Guild League elite roster (MG_Guild EliteGroup, label LeagueLabel): slot index -> role, and the version
        /// 32302 saves against (GuildService.LeagueElite).</summary>
        public Dictionary<int, long> LeagueElites { get; set; } = new();
        public int LeagueEliteVersion { get; set; }
        /// <summary>Badge frames and designs unlocked by 113 items: GuildIconConfig id to removetime (unix s, 0 permanent);
        /// DBGuildData unLockHeadIconData (GuildService.ItemActions).</summary>
        public Dictionary<long, long> UnlockedHeadIcons { get; set; } = new();
        /// <summary>Guild Store (ShopConfig 2702) purchases of the week ShopCommodityWeek (Monday 05:00, unix s), guild-wide:
        /// commodity to count; DBGuildData shopCommodityData.</summary>
        public Dictionary<int, int> ShopCommodityBuys { get; set; } = new();
        public long ShopCommodityWeek { get; set; }
        /// <summary>Guild group mail (32241, GuildService.BroadEmail): when the next letter may go (unix ms), and how many
        /// went on the day BroadEmailDay (05:00, unix s).</summary>
        public long NextBroadEmailMs { get; set; }
        public int BroadEmailCount { get; set; }
        public long BroadEmailDay { get; set; }
        /// <summary>The master's impeachment (32235/32236, GuildService.Impeach): its initiator (0 = none), deadline (unix
        /// ms), responses needed, who responded (the initiator first), and when the next may start (unix ms).</summary>
        public long ImpeachOwner { get; set; }
        public long ImpeachEndMs { get; set; }
        public int ImpeachNeed { get; set; }
        public List<long> ImpeachResponders { get; set; } = [];
        public long NextImpeachMs { get; set; }
        /// <summary>Merging guilds (32314-32319, GuildService.Merge): invitations received, the partner of an accepted
        /// merge and whether this guild absorbs it, when it runs (unix s, the next 05:00), the members the absorbed guild
        /// brings, and the end of the cooldown after a merge (unix s).</summary>
        public List<MergeInvite> MergeInvites { get; set; } = [];
        public long PendingMergeGuildId { get; set; }
        public bool MergeAbsorbs { get; set; }
        public long MergeAtSec { get; set; }
        public List<long> MergeMemberIds { get; set; } = [];
        public long MergeCooldownSec { get; set; }
        /// <summary>32228 journal rows (GuildEventData bytes) to append with this state's next save; not part of the state.</summary>
        internal List<byte[]> PendingEvents { get; } = new();
    }

    /// <summary>A merge invitation from guild <see cref="From"/> (received at unix s, with its announcement).</summary>
    public sealed class MergeInvite
    {
        public long From { get; set; }
        public long RecvSec { get; set; }
        public string Announce { get; set; } = "";
    }

    public sealed class Member
    {
        public long RoleId { get; set; }
        public string Name { get; set; } = "";
        public int Official { get; set; } = 20;
        public int Level { get; set; }
        public int Career { get; set; }
        public long EnterTime { get; set; }
        public long OfflineTime { get; set; }
        public long CurContribute { get; set; }
        public long HistoryContribute { get; set; }
        /// <summary>The member's Guild Activity this week and last week (GuildService.Activity).</summary>
        public long WeekActivity { get; set; }
        public long LastWeekActivity { get; set; }
        /// <summary>Guild Funds (item 104) the member brought in (32291 Fund Rankings).</summary>
        public long ContributeMoney { get; set; }
    }

    public sealed class Application
    {
        public long RoleId { get; set; }
        public string Name { get; set; } = "";
        public int Level { get; set; }
        public int Career { get; set; }
        public long ApplyTime { get; set; }
    }

    /// <summary>Global 839xx order book. IDs remain opaque strings on the wire.</summary>
    public sealed class MarketState
    {
        internal OwnerRevisionValue? CapturedRevision { get; set; }
        public long NextId { get; set; }
        public long RecentAvgPrice { get; set; } = 100;
        public List<long> RecentPrices { get; set; } = new();
        public List<MarketPrice> PriceHistory { get; set; } = new();
        public List<MarketStall> Stalls { get; set; } = new();
        /// <summary>E03: no refund/burn policy is implied by guild deletion; keep orphan stock out of the public book.</summary>
        public List<MarketStall> RecoveryStalls { get; set; } = new();
        public List<MarketTrade> Trades { get; set; } = new();
        public Dictionary<long, long> LastListedMs { get; set; } = new();
    }

    public sealed class MarketPrice
    {
        public long Average { get; set; }
        public int DateHour { get; set; }
    }

    public sealed class MarketStall
    {
        public string Id { get; set; } = "";
        public long GuildId { get; set; }
        public long UnitPrice { get; set; }
        public long TotalCount { get; set; }
        public long RemainCount { get; set; }
        public long CreatedMs { get; set; }
        public long AvailableMs { get; set; }
        public long ExpiresMs { get; set; }
    }

    public sealed class MarketTrade
    {
        public string Id { get; set; } = "";
        public long SellerGuildId { get; set; }
        public long BuyerGuildId { get; set; }
        public long UnitPrice { get; set; }
        public long Count { get; set; }
        public long Fee { get; set; }
        public long TimeMs { get; set; }
        public bool Read { get; set; }
    }

    public MarketState LoadMarket() => _accounts.ReadSync<MarketState>(new GuildReadPlan(GuildReadKind.Market));

    /// <summary>Market book and every affected guild balance commit together.</summary>
    public bool SaveMarket(MarketState market, IEnumerable<State> affected)
    {
        var plan = MarketPlan(market, affected);
        try { plan.Commit(_accounts, "guild/market"); return true; }
        catch (RowMissingException e) when (e.Table == Table) { return false; }
    }

    /// <summary>Commit the complete market book, guild balances, and membership indexes as one value-only writer plan.</summary>
    internal async Task<bool> SaveMarketAsync(MarketState market, IEnumerable<State> affected,
        Economy.EscrowEconomyIntent economy)
    {
        var plan = MarketPlan(market, affected);
        plan.WithEscrowEconomy(economy);
        try { await plan.CommitAsync(_accounts, "guild/market"); return true; }
        catch (RowMissingException e) when (e.Table == Table) { return false; }
    }

    private RoleItemPlanBuilder MarketPlan(MarketState market, IEnumerable<State> affected)
    {
        var plan = new RoleItemPlanBuilder(_accounts);
        foreach (var guild in affected.DistinctBy(g => g.Id))
            EnlistSave(guild, plan);
        EnlistMarket(market, plan);
        return plan;
    }

    internal void EnlistMarket(MarketState market, RoleItemPlanBuilder plan)
    {
        var marketRevision = plan.CaptureEntity(RevisionStore.Entity("guild_market", 1), market.CapturedRevision);
        if (market.CapturedRevision is null && marketRevision.Exists)
            throw new InvalidOperationException("Guild market state lacks a source revision.");
        plan.Add(new GuildMarketSavePlan(market), _ => market.CapturedRevision =
            new OwnerRevisionValue(marketRevision.Key, marketRevision.Revision + 1, true));
    }

    public State? Get(long id)
    {
        lock (_cacheLock)
        {
            Current();
            if (_byId.TryGetValue(id, out var cached)) return cached is { } hit ? Copy(hit) : null;
        }
        long generation = _accounts.Caches.Current(WrittenCache.Guild);
        var state = _accounts.ReadSync<State?>(new GuildReadPlan(GuildReadKind.Guild, id));
        lock (_cacheLock)
            if (Current() == generation) _byId[id] = state == null ? null : Cache(state);
        return state;
    }

    // C/D 16: committed guild aggregates and role membership served from memory instead of a synchronous SQL read on
    // the loop per call (S1: 18k ForRole reads, 2.9 s of loop waits). Every caller still gets its own copy to
    // mutate. Any writer job that touches the guild tables bumps the generation after it ends (DbWriter.AfterJob);
    // a read during which the generation changed is returned but not cached. A read that began before a commit and
    // ended before the bump is cached until the bump: callers may see pre-commit bytes in that window, and a save from
    // them then conflicts on the revision (never a silent overwrite). The generation belongs to this store's database
    // (AccountStore.Caches), so another host's writes leave this cache alone.
    private readonly object _cacheLock = new();
    private long _cachedGeneration = -1;
    private readonly Dictionary<long, (byte[] Json, OwnerRevisionValue? Revision)?> _byId = [];
    private readonly Dictionary<long, long> _guildOfRole = [];

    private long Current()
    {
        long generation = _accounts.Caches.Current(WrittenCache.Guild);
        if (_cachedGeneration != generation) { _byId.Clear(); _guildOfRole.Clear(); _cachedGeneration = generation; }
        return generation;
    }

    private static (byte[] Json, OwnerRevisionValue? Revision) Cache(State state) =>
        (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state), state.CapturedRevision);

    private static State Copy((byte[] Json, OwnerRevisionValue? Revision) cached)
    {
        var state = System.Text.Json.JsonSerializer.Deserialize<State>(cached.Json)!;
        state.CapturedRevision = cached.Revision;
        return state;
    }

    internal bool HasRecovery(long id) => _accounts.ReadSync<bool>(new GuildRecoveryExistsPlan(id));

    public State? ForRole(long roleId)
    {
        long? known;
        lock (_cacheLock) { Current(); known = _guildOfRole.TryGetValue(roleId, out long guildId) ? guildId : null; }
        if (known is { } member) return member == 0 ? null : Get(member);
        long generation = _accounts.Caches.Current(WrittenCache.Guild);
        var state = _accounts.ReadSync<State?>(new GuildReadPlan(GuildReadKind.ForRole, roleId));
        lock (_cacheLock)
            if (Current() == generation)
            {
                _guildOfRole[roleId] = state?.Id ?? 0;
                if (state != null) _byId[state.Id] = Cache(state);
            }
        return state;
    }

    /// <summary>GuildSetting GuildEventDateMaxNum: journal rows kept per guild.</summary>
    internal int JournalCap { get; set; } = 500;

    /// <summary>The guild's journal, newest first.</summary>
    internal List<(long Index, byte[] Data)> Journal(long guildId) =>
        _accounts.ReadSync<List<(long Index, byte[] Data)>>(new GuildEventReadPlan(guildId));

    /// <summary>Append journal rows in a transaction that does not save the guild itself (e.g. an auction settlement).</summary>
    internal void EnlistEvents(long guildId, byte[][] events, RoleItemPlanBuilder transaction)
    {
        if (events.Length > 0) transaction.Add(new GuildEventAppendPlan(guildId, "", events, JournalCap));
    }

    public IReadOnlyList<State> List() => _accounts.ReadSync<List<State>>(new GuildReadPlan(GuildReadKind.List));

    public bool NameExists(string name, long exceptId = 0) =>
        _accounts.ReadSync<bool>(new GuildReadPlan(GuildReadKind.NameExists, exceptId, name));

    /// <summary>Standalone aggregate save; generated identity is applied only after the writer commit.</summary>
    public bool Save(State state)
    {
        var plan = new RoleItemPlanBuilder(_accounts);
        EnlistSave(state, plan);
        try { plan.Commit(_accounts, "guild/save"); return true; }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { return false; }
        catch (RowMissingException e) when (e.Table == Table) { return false; }
    }

    /// <summary>Capture payment-enlisted guild state; publish a generated identity on the caller only after commit.</summary>
    internal void EnlistSave(State state, RoleItemPlanBuilder transaction, string missingMessage = "Guild disappeared during save.")
    {
        var revision = state.Id == 0 ? (OwnerRevisionValue?)null :
            transaction.CaptureEntity(RevisionStore.Entity("guild", state.Id), state.CapturedRevision);
        if (state.Id != 0 && state.CapturedRevision is null && revision is { Exists: true })
            throw new InvalidOperationException("Guild state lacks a source revision.");
        transaction.Add(new GuildSavePlan(state, missingMessage), receipt =>
        {
            state.Id = (long)receipt!;
            state.CapturedRevision = revision is { } current
                ? new OwnerRevisionValue(current.Key, current.Revision + 1, true)
                : new OwnerRevisionValue(RevisionStore.Entity("guild", state.Id), 1, true);
        });
        if (state.PendingEvents.Count > 0)
        {
            var events = state.PendingEvents.ToArray();
            transaction.Add(new GuildEventAppendPlan(state.Id, state.Name, events, JournalCap),
                _ => state.PendingEvents.RemoveAll(e => events.Contains(e)));
        }
    }

    internal void EnlistMaterialContribution(long role, long guild, long amount, RoleItemPlanBuilder transaction)
        => transaction.Add(new GuildMaterialContributionPlan(role, guild, amount));

    internal void EnlistCaravanReward(long guild, long role, long funds, long contribution, RoleItemPlanBuilder transaction)
        => transaction.Add(new GuildCaravanRewardPlan(guild, role, funds, contribution));

    public void Delete(long guildId)
    {
        _accounts.WriteLegacySync<int>(new GuildDeletePlan(guildId));
    }

    internal void EnlistRecoverDelete(State state, RoleItemPlanBuilder transaction, long escrowMaterials = 0)
    {
        transaction.CaptureEntity(RevisionStore.Entity("guild", state.Id), state.CapturedRevision);
        transaction.Add(new GuildRecoverDeletePlan(state.Id, escrowMaterials));
    }
}
