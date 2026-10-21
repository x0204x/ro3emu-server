using Microsoft.Data.Sqlite;
using System.Text.Json;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts;

/// <summary>A character ("role" in the client). Career 1000 = Novice; sex 1 = male, 2 = female.</summary>
public sealed record Role(
    long Id, string Account, int Area, string Name, int Sex, int Career, int Level, int Hair, long CreateTime, long LastLoginTime,
    long SceneEntry, float X, float Y, float Z, float Face);

/// <summary>A detached public return point captured before a private-room admission.</summary>
public readonly record struct RoleReturnPosition(long RoleId, long SceneEntry, float X, float Y, float Z, float Face);

/// <summary>
/// The changing part of a role: base/job level with the exp inside the current level (as the client shows it),
/// allocated base stats STR..LUK, extra stat points given by a GM, and currencies (ItemConfig id -> amount; 1 = Zeny).
/// </summary>
public sealed class RoleProgress
{
    internal RoleRevisionVector? SourceRevision { get; set; }
    public int Career { get; set; }
    public int Level { get; set; } = 1;
    public int JobLevel { get; set; } = 1;
    public long Exp { get; set; }
    public long JobExp { get; set; }
    public int[] Stats { get; } = [1, 1, 1, 1, 1, 1];
    public int BonusPoints { get; set; }
    public Dictionary<int, long> Currency { get; } = new();
    public bool StarterKit { get; set; }

    /// <summary>Learned skill levels by skill group id (SkillConfig._iSkillID).</summary>
    public Dictionary<int, int> Skills { get; } = new();

    /// <summary>Skill bar: slot (1..6) -> skill group id.</summary>
    public Dictionary<int, int> SkillBar { get; } = new();
}

/// <summary>Characters in the same SQLite file as the accounts.</summary>
public sealed class RoleStore(AccountStore accounts)
{
    /// <summary>PublicParamConfig.SpawnSceneEntry (Prontera field 08) and SpawnScenePos {x, z}.</summary>
    public const long SpawnSceneEntry = 10111;
    public const float SpawnX = 156f;
    public const float SpawnZ = 192f;

    private readonly AccountStore _accounts = accounts;
    private string? _rewardRealm;
    internal string RewardRealmId => _rewardRealm ??= _accounts.ReadSync<string>(new RewardSourceReadPlan());
    internal Task<bool> RewardSourceCommittedAsync(string source) => _accounts.ReadAsync<bool>(new RewardSourceReadPlan(source));

    public RoleItemPlanBuilder NewTransaction() => new(_accounts);

    /// <summary>Stage G: the selection of <paramref name="role"/> stages its login blobs (see AccountStore.Staging).</summary>
    internal void BeginLoginStaging(long role) => _accounts.BeginStaging(role);
    internal Task CommitLoginStagingAsync(long role) => _accounts.CommitStagedAsync(role);
    internal void FlushLoginStaging(long role) => _accounts.FlushStaged(role);
    internal bool IsLoginStaging(long role) => _accounts.StagedCount(role) >= 0;

    /// <summary>Stage G: true when the role's blob column already holds exactly <paramref name="value"/> (a valid
    /// snapshot column or the hash of this process's last save of it; any commit of the part invalidates both), so
    /// a transaction writing only it would change nothing. A staged column is never reported unchanged.</summary>
    internal bool BlobUnchanged(long id, string column, byte[] value)
    {
        if (_accounts.TryGetStaged(id, column, out _)) return false;
        if (!_accounts.Snapshots.Unchanged(id, column, value) && !_accounts.Snapshots.PersistedEqual(id, column, value)) return false;
        SnapshotVerify.Same(value, () => _accounts.ReadSync<byte[]?>(new RoleBlobReadPlan(id, column)), $"role {id} {column} unchanged");
        return true;
    }

    internal object?[] Commit(RoleItemPlanBuilder builder, string label) => builder.Commit(_accounts, label);
    internal Task<object?[]> CommitAsync(RoleItemPlanBuilder builder, string label, Action<object?[]>? publish = null)
        => builder.CommitAsync(_accounts, label, publish);

    /// <summary>Persistent private-room identities: stored dungeon reports must not alias a run after restart.</summary>
    public long AllocateInstanceId()
    {
        return _accounts.WriteLegacySync<long>(new RoleIdentityWritePlan(RoleIdentityWriteKind.AllocateInstance));
    }

    /// <summary>Durable lifecycle epoch and storage-part revisions for a staged role operation.</summary>
    public RoleRevisionVector ReadRevisionVector(long roleId) =>
        _accounts.ReadSync<RoleRevisionVector>(new RoleRevisionReadPlan(roleId));

    public Task<RoleRevisionVector> ReadRevisionVectorAsync(long roleId) =>
        _accounts.ReadAsync<RoleRevisionVector>(new RoleRevisionReadPlan(roleId));

    public List<Role> ForAccount(string account) =>
        _accounts.ReadSync<List<Role>>(new RoleProjectionRead(RoleProjectionKind.ForAccount, text: account));

    public Task<List<Role>> ForAccountAsync(string account) => _accounts.ReadAsync<List<Role>>(new RoleProjectionRead(RoleProjectionKind.ForAccount, text: account));

    public Role? Get(long id)
    {
        if (_accounts.Snapshots.TryIdentity(id, out var snapshot))
            return SnapshotVerify.Same(snapshot, () => _accounts.ReadSync<List<Role>>(new RoleProjectionRead(RoleProjectionKind.ById, id: id)).FirstOrDefault(), $"role {id} identity");
        return _accounts.ReadSync<List<Role>>(new RoleProjectionRead(RoleProjectionKind.ById, id: id)).FirstOrDefault();
    }
    public async Task<Role?> GetAsync(long id) => (await _accounts.ReadAsync<List<Role>>(new RoleProjectionRead(RoleProjectionKind.ById, id: id))).FirstOrDefault();

    public Role? FindByName(string name) => _accounts.ReadSync<List<Role>>(new RoleProjectionRead(RoleProjectionKind.ByName, text: name)).FirstOrDefault();
    public async Task<Role?> FindByNameAsync(string name) => (await _accounts.ReadAsync<List<Role>>(new RoleProjectionRead(RoleProjectionKind.ByName, text: name))).FirstOrDefault();

    /// <summary>
    /// Deletes a role of the account (the client has no delete: its role screen button is hidden and unbound). The row
    /// stays for support; the name is released by suffixing the id. Returns false when the role is not the account's.
    /// </summary>
    public bool Delete(string account, long id)
    {
        return _accounts.WriteLegacySync<bool>(new RoleIdentityWritePlan(RoleIdentityWriteKind.Delete, id: id,
            now: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), account: account));
    }

    /// <summary>Creates a role at the spawn point; returns null when the name is taken.</summary>
    public Role? Create(string account, int area, string name, int sex, int career, int hair)
    {
        return _accounts.WriteLegacySync<Role?>(new RoleIdentityWritePlan(RoleIdentityWriteKind.Create,
            now: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), account: account, area: area, name: name,
            sex: sex, career: career, hair: hair));
    }

    /// <summary>Create a role with its spawn position in one insert on the writer thread (the loop does not wait on
    /// it). A taken name returns null (unique index).</summary>
    public Task<Role?> CreateAsync(string account, int area, string name, int sex, int career, int hair,
        long scene, float x, float y, float z, float face) =>
        _accounts.WriteAsync<Role?>(new RoleIdentityWritePlan(RoleIdentityWriteKind.Create,
            now: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), account: account, area: area, name: name,
            sex: sex, career: career, hair: hair, scene: scene, x: x, y: y, z: z, face: face));

    /// <summary>The select's login stamp on the writer thread (the loop does not wait for the commit).</summary>
    public Task TouchLoginAsync(long id) => _accounts.WriteLegacyAsync<int>(new RoleIdentityWritePlan(RoleIdentityWriteKind.TouchLogin,
        id: id, now: DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    public void TouchLogin(long id)
    {
        _accounts.WriteLegacySync<int>(new RoleIdentityWritePlan(RoleIdentityWriteKind.TouchLogin,
            id: id, now: DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    public void SavePosition(long id, long sceneEntry, float x, float y, float z, float face)
    {
        _accounts.WriteLegacySync<int>(new RoleIdentityWritePlan(RoleIdentityWriteKind.Position,
            id: id, scene: sceneEntry, x: x, y: y, z: z, face: face));
    }

    /// <summary>
    /// C/D 18 lazy public position: written on the writer's lazy lane against the caller's expected revision of
    /// the position part (a CAS chain), so an overtaken capture is refused instead of overwriting a newer position.
    /// Returns false on that refusal. Never waits on the loop.
    /// </summary>
    public async Task<bool> SavePositionLazyAsync(long id, long epoch, long expectedRevision, long sceneEntry, float x, float y, float z, float face)
    {
        var plan = new RoleIdentityWritePlan(RoleIdentityWriteKind.Position, id: id, scene: sceneEntry, x: x, y: y, z: z, face: face);
        plan.Expect([new RoleRevisionVector(id, epoch, false, new Dictionary<string, long> { [RevisionStore.Position] = expectedRevision })]);
        try { await _accounts.WriteLazyAsync<int>(plan); return true; }
        catch (RolePlanConflictException) { return false; }
    }

    /// <summary>C/D 17 lazy blob part: the same CAS chain as the position, on the column's own revision part.</summary>
    public async Task<bool> SaveBlobLazyAsync(long id, long epoch, long expectedRevision, string column, byte[] value)
    {
        var plan = new RoleBlobWritePlan(id, column, value);
        plan.Expect([new RoleRevisionVector(id, epoch, false, new Dictionary<string, long> { [column] = expectedRevision })]);
        try { await _accounts.WriteLazyAsync<int>(plan); return true; }
        catch (RolePlanConflictException) { return false; }
    }

    /// <summary>Commit every entrant's outside position in one transaction before any private handoff.</summary>
    public void SaveReturnPositions(IReadOnlyList<RoleReturnPosition> positions)
    {
        if (positions.Count == 0) return;
        if (positions.Select(x => x.RoleId).Distinct().Count() != positions.Count)
            throw new ArgumentException("Return position roles must be distinct.", nameof(positions));
        for (int attempt = 0; ; attempt++)
        {
            var transaction = NewTransaction();
            foreach (var point in positions)
                transaction.Add(new RoleIdentityWritePlan(RoleIdentityWriteKind.Position,
                    id: point.RoleId, scene: point.SceneEntry, x: point.X, y: point.Y, z: point.Z, face: point.Face));
            try { Commit(transaction, "room/return-position-barrier"); return; }
            // Only position parts are written. A lazy position write that committed after the capture overtook it;
            // the barrier's value is the one to keep, so recapture once (as the single-role position barrier does).
            catch (OwnerRevisionConflictException) when (attempt == 0) { }
        }
    }

    public byte[]? LoadSettings(long id)
        => LoadBlob(id, "settings");

    /// <summary>A settings barrier (leave world, AFK-coupled change): always written. A lazy write of the part may be
    /// queued or in flight, and the snapshot that the unchanged-save checks compare against cannot see it.</summary>
    public void SaveSettings(long id, byte[] settings)
        => SaveBlob(id, "settings", settings, force: true);

    public byte[]? LoadGuides(long id) => LoadBlob(id, "guides");
    public byte[]? LoadEmojiPlay(long id) => LoadBlob(id, "emoji_play");
    public void SaveEmojiPlay(long id, byte[] value) => SaveBlob(id, "emoji_play", value);
    public byte[]? LoadAchievements(long id) => LoadBlob(id, "achievements");
    public void SaveAchievements(long id, byte[] value, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "achievements", value, transaction);
    public byte[]? LoadAchievementRuntime(long id) => LoadBlob(id, "achievement_runtime");
    public void SaveAchievementRuntime(long id, byte[] value) => SaveBlob(id, "achievement_runtime", value);
    public byte[]? LoadAdventureGroup(long id) => LoadBlob(id, "adventure_group");
    public void SaveAdventureGroup(long id, byte[] value, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "adventure_group", value, transaction);

    public byte[]? LoadLibrary(long id) => LoadBlob(id, "library");
    public byte[]? LoadCardPlans(long id) => LoadBlob(id, "card_plans");
    public void SaveCardPlans(long id, byte[] plans, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "card_plans", plans, transaction);

    public void SaveLibrary(long id, byte[] library, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "library", library, transaction);

    public byte[]? LoadShop(long id) => LoadBlob(id, "shop");

    public byte[]? LoadTrade(long id) => LoadBlob(id, "trade_info");
    public void SaveTrade(long id, byte[] value) => SaveBlob(id, "trade_info", value);
    public byte[]? LoadStalls(long id) => LoadBlob(id, "stalls_info");
    public void SaveStalls(long id, byte[] value) => SaveBlob(id, "stalls_info", value);

    public byte[]? LoadDaily(long id) => LoadBlob(id, "daily");

    public byte[]? LoadNewActivities(long id) => LoadBlob(id, "new_activities");
    public void SaveNewActivities(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "new_activities", state, transaction);
    public byte[]? LoadActivityRuntime(long id) => LoadBlob(id, "activity_runtime");
    public void SaveActivityRuntime(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "activity_runtime", state, transaction);
    public byte[]? LoadTimedActivity(long id) => LoadBlob(id, "timed_activity");
    public byte[]? LoadTimedBattle(long id) => LoadBlob(id, "timed_battle");
    public byte[]? LoadTraining(long id) => LoadBlob(id, "training");
    public byte[]? LoadGuildChallenge(long id) => LoadBlob(id, "guild_challenge");
    public void SaveGuildChallenge(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "guild_challenge", state, transaction);
    public void SaveTraining(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "training", state, transaction);
    public byte[]? LoadLottery(long id) => LoadBlob(id, "lottery");
    public void SaveLottery(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "lottery", state, transaction);
    public void SaveTimedBattle(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "timed_battle", state, transaction);
    public void SaveTimedActivity(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "timed_activity", state, transaction);

    public byte[]? LoadWildAfk(long id) => LoadBlob(id, "wild_afk");
    public void SaveWildAfk(long id, byte[] state) => SaveBlob(id, "wild_afk", state);

    public void SaveDaily(long id, byte[] daily) => SaveBlob(id, "daily", daily);

    /// <summary>A rollover writes quest progress, daily state and currency in one SQLite transaction.</summary>
    /// <summary>The daily period reset: daily rows, quests, liveness currency and (when given) the AFK allowance of
    /// the new day are one transaction, so no crash can leave a restored AFK bank beside an unreset day or the reverse.</summary>
    public void SaveDailyReset(long id, byte[] daily, byte[] tasks, string currency, RoleProgress progress,
        EconomyMutation economy, byte[]? wildAfk = null)
    {
        var builder = DailyResetBuilder(id, daily, tasks, currency, progress, economy, wildAfk);
        Commit(builder, "role/daily-reset");
        progress.SourceRevision = builder.CommittedVector(id, RevisionStore.Progress);
    }

    internal async Task SaveDailyResetAsync(long id, byte[] daily, byte[] tasks, string currency,
        RoleProgress progress, EconomyMutation economy, byte[]? wildAfk = null)
    {
        var builder = DailyResetBuilder(id, daily, tasks, currency, progress, economy, wildAfk);
        await CommitAsync(builder, "role/daily-reset", _ =>
            progress.SourceRevision = builder.CommittedVector(id, RevisionStore.Progress));
    }

    private RoleItemPlanBuilder DailyResetBuilder(long id, byte[] daily, byte[] tasks, string currency,
        RoleProgress progress, EconomyMutation economy, byte[]? wildAfk)
    {
        if (economy.CommandKind != "cd13e/daily-reset" || economy.ActorRoleId != id)
            throw new ArgumentException("Daily reset needs its own economic source.", nameof(economy));
        var builder = NewTransaction();
        builder.BindProgressSource(id, progress);
        builder.Add(new RoleDailyResetPlan(id, daily, tasks, currency));
        if (wildAfk is not null) builder.RoleBlob(id, "wild_afk", wildAfk);
        builder.WithEconomy(economy);
        return builder;
    }

    /// <summary>One durable daily settlement: compare prior blobs, write award/quest/daily state, then item rows.</summary>
    public bool CommitDailyAward(long id, byte[] previousDaily, byte[] daily, RoleProgress progress,
        IReadOnlyList<InvItem> touchedItems, byte[]? previousTasks = null, byte[]? tasks = null)
    {
        // One new stack may be touched by several grants; identity, not Guid=0, distinguishes stacks.
        InvItem[] uniqueItems = touchedItems.Distinct<InvItem>(ReferenceEqualityComparer.Instance).ToArray();
        var builder = new RoleItemPlanBuilder(_accounts);
        try { builder.BindProgressSource(id, progress); }
        catch (RolePlanConflictException) { return false; }
        builder.Add(new RoleDailyAwardPlan(id, previousDaily, daily, progress, previousTasks, tasks));
        int itemReceipt = builder.Add(new ItemAwardUpsertPlan(id, uniqueItems));
        try
        {
            var receipts = builder.Commit(_accounts, "role/daily-award");
            progress.SourceRevision = builder.CommittedVector(id, RevisionStore.Progress);
            ItemSnapshotWritePlan.ApplyGuids(uniqueItems, (long[])receipts[itemReceipt]!);
            return true;
        }
        catch (RolePlanConflictException) { return false; }
    }

    /// <summary>Ring acceptance changes ordinary 5027 quest state and the daily allowance in one write.</summary>
    public bool CommitRingAccept(long id, byte[] previousDaily, byte[] daily, byte[] tasks)
        => _accounts.WriteLegacySync<bool>(new RoleRingAcceptPlan(id, previousDaily, daily, tasks));

    public byte[]? LoadTower(long id) => LoadBlob(id, "tower");

    public byte[]? LoadDungeons(long id) => LoadBlob(id, "dungeons");
    public byte[]? LoadTerritoryWars(long id) => LoadBlob(id, "territory_wars");
    public void SaveTerritoryWars(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "territory_wars", state, transaction);
    public byte[]? LoadTerritoryEscort(long id) => LoadBlob(id, "territory_escort");
    public void SaveTerritoryEscort(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "territory_escort", state, transaction);
    public byte[]? LoadLifeJob(long id) => LoadBlob(id, "lifejob");
    public byte[]? LoadLifeRestrict(long id) => LoadBlob(id, "life_restrict");
    public byte[]? LoadLifeRuntime(long id) => LoadBlob(id, "life_runtime");
    public void SaveLifeJob(long id, byte[] info, byte[] restrict, byte[] runtime, RoleItemPlanBuilder? transaction = null)
    {
        if (transaction == null)
        {
            var builder = new RoleItemPlanBuilder(_accounts);
            builder.RoleBlob(id, "lifejob", info);
            builder.RoleBlob(id, "life_restrict", restrict);
            builder.RoleBlob(id, "life_runtime", runtime);
            builder.Commit(_accounts, "role/lifejob");
            return;
        }
        SaveBlob(id, "lifejob", info, transaction);
        SaveBlob(id, "life_restrict", restrict, transaction);
        SaveBlob(id, "life_runtime", runtime, transaction);
    }
    public void SaveDungeons(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "dungeons", state, transaction);

    public void SaveTower(long id, byte[] tower, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "tower", tower, transaction);

    /// <summary>Captures a complete transaction on the caller and waits for its durable commit.</summary>
    public void WithTransaction(Action<RoleItemPlanBuilder> action)
    {
        var transaction = new RoleItemPlanBuilder(_accounts);
        action(transaction); // Capture on the caller; no live state or callback enters the writer.
        transaction.Commit(_accounts, "role/transaction");
    }

    /// <summary>First committed winner owns the floor (inferred); INSERT OR IGNORE protects concurrent finishes.</summary>
    public void RecordTowerFirstPass(long floor, byte[] record, RoleItemPlanBuilder? transaction = null)
    {
        var plan = new TowerFirstPassWritePlan(floor, record);
        if (transaction == null) _accounts.WriteLegacySync<int>(plan);
        else transaction.Add(plan);
    }

    public Dictionary<long, byte[]> LoadTowerFirstPasses(long min, long max)
        => _accounts.ReadSync<Dictionary<long, byte[]>>(new TowerFirstPassReadPlan(min, max));

    public byte[]? LoadSeasonTalent(long id) => LoadBlob(id, "season_talent");

    public byte[]? LoadSeasonPage(long id) => LoadBlob(id, "season_page");

    public void SaveSeasonPage(long id, byte[] page, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "season_page", page, transaction);

    public byte[]? LoadSeasonPageMeta(long id) => LoadBlob(id, "season_page_meta");

    public void SaveSeasonPageMeta(long id, byte[] meta, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "season_page_meta", meta, transaction);

    public byte[]? LoadSeasonRefine(long id) => LoadBlob(id, "season_refine");

    public void SaveSeasonRefine(long id, byte[] refine, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "season_refine", refine, transaction);

    public void SaveSeasonTalent(long id, byte[] talent, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "season_talent", talent, transaction);

    /// <summary>One SQLite commit for 83550's bag stacks, currency and durable plan/oath state.</summary>
    public void CommitTalentPurchase(long id, byte[] talent, IReadOnlyDictionary<int, long> currency,
        IReadOnlyDictionary<long, int> stacks, IReadOnlyCollection<long> removed, ItemStore items,
        RoleProgress? progress = null)
    {
        var builder = new RoleItemPlanBuilder(_accounts);
        if (progress is not null) builder.BindProgressSource(id, progress);
        builder.Add(new ItemTalentCostPlan(id, stacks, removed));
        builder.Add(new RoleCurrencyBlobPlan(id, currency, "season_talent", talent, requireRole: true));
        builder.Commit(_accounts, "role/talent-purchase");
        if (progress is not null) progress.SourceRevision = builder.CommittedVector(id, RevisionStore.Progress);
    }

    public byte[]? LoadPets(long id) => LoadBlob(id, "pets");

    public byte[]? LoadCareerSpecial(long id) => LoadBlob(id, "career_special");
    public void SaveCareerSpecial(long id, byte[] value) => SaveBlob(id, "career_special", value);
    public byte[]? LoadHeroicSpirit(long id) => LoadBlob(id, "heroic_spirit");
    public byte[]? LoadAreaConcur(long id) => LoadBlob(id, "area_concur");
    public void SaveAreaConcur(long id, byte[] value, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "area_concur", value, transaction);
    public byte[]? LoadGrowthClass(long id) => LoadBlob(id, "growth_class");
    public void SaveGrowthClass(long id, byte[] value, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "growth_class", value, transaction);
    public byte[]? LoadFirstHeart(long id) => LoadBlob(id, "first_heart");
    public void SaveFirstHeart(long id, byte[] value, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "first_heart", value, transaction);
    public byte[]? LoadPersonal(long id) => LoadBlob(id, "personal");
    public byte[]? LoadSocialOutfit(long id) => LoadBlob(id, "social_outfit");
    public byte[]? LoadEmojiInfo(long id) => LoadBlob(id, "emoji_info");
    public byte[]? LoadPersonalUpvotes(long id, RoleItemPlanBuilder? transaction = null) => LoadBlob(id, "personal_upvote", transaction);
    public void SavePersonalUpvotes(long id, byte[] value, RoleItemPlanBuilder transaction) => SaveBlob(id, "personal_upvote", value, transaction);
    public byte[]? LoadPopularity(long id, RoleItemPlanBuilder? transaction = null) => LoadBlob(id, "popularity", transaction);
    public void SavePopularity(long id, byte[] value, RoleItemPlanBuilder transaction) => SaveBlob(id, "popularity", value, transaction);
    public byte[]? LoadMounts(long id) => LoadBlob(id, "mounts");
    public void SaveMounts(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "mounts", state, transaction);
    public byte[]? LoadWardrobe(long id) => LoadBlob(id, "wardrobe");
    public Task<byte[]?> LoadWardrobeAsync(long id) => _accounts.ReadAsync<byte[]?>(new RoleProjectionRead(RoleProjectionKind.Wardrobe, id: id));
    public void SaveWardrobe(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "wardrobe", state, transaction);
    public byte[]? LoadTotems(long id) => LoadBlob(id, "totems");
    public byte[]? LoadTotemRefineEpoch(long id) => LoadBlob(id, "totem_refine_epoch");
    public void SaveTotemRefineEpoch(long id, byte[] epoch, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "totem_refine_epoch", epoch, transaction);
    public void SaveTotems(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "totems", state, transaction);
    public byte[]? LoadGenre(long id) => LoadBlob(id, "genre");
    public void SaveGenre(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "genre", state, transaction);

    /// <summary>Resolve a persisted Genre sharing code, including an offline seeker's code.</summary>
    public long? FindGenreCode(string code, long now)
        => _accounts.ReadSync<long?>(new RoleGenreCodeReadPlan(code, now));

    public byte[]? LoadSkillPlans(long id) => LoadBlob(id, "skill_plans");
    public void SaveSkillPlans(long id, byte[] state, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "skill_plans", state, transaction);

    public void SavePets(long id, byte[] pets, RoleItemPlanBuilder? tx = null) => SaveBlob(id, "pets", pets, tx);

    public byte[]? LoadPetBooks(long id) => LoadBlob(id, "pet_books");

    public void SavePetBooks(long id, byte[] books, RoleItemPlanBuilder? tx = null) => SaveBlob(id, "pet_books", books, tx);

    /// <summary>
    /// Pet payment/reward and its 5017 state commit together. A crash cannot leave a paid
    /// egg without a slot, or a collected reward with a still-collectible egg (inferred transaction invariant).
    /// </summary>
    internal void SavePetExchange(long id, byte[] pets, IReadOnlyDictionary<int, long> currency,
        IReadOnlyList<InvItem> before, IReadOnlyList<InvItem> after, RoleProgress? progress = null)
    {
        var builder = new RoleItemPlanBuilder(_accounts);
        if (progress is not null) builder.BindProgressSource(id, progress);
        int itemReceipt = builder.Add(new ItemExchangeWritePlan(id, before, after));
        builder.Add(new RoleCurrencyBlobPlan(id, currency, "pets", pets, requireRole: true));
        var receipts = builder.Commit(_accounts, "role/pet-exchange");
        if (progress is not null) progress.SourceRevision = builder.CommittedVector(id, RevisionStore.Progress);
        ItemSnapshotWritePlan.ApplyGuids(after, (long[])receipts[itemReceipt]!);
    }

    public byte[]? LoadEquipLevels(long id) => LoadBlob(id, "equip_levels");

    public void SaveEquipLevels(long id, byte[] levels) => SaveBlob(id, "equip_levels", levels);

    public byte[]? LoadEquipPlans(long id) => LoadBlob(id, "equip_plans");

    public void SaveEquipPlans(long id, byte[] plans, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "equip_plans", plans, transaction);

    public byte[]? LoadAutoDecompose(long id) => LoadBlob(id, "auto_decompose");

    public byte[]? LoadBagState(long id) => LoadBlob(id, "bag_state");
    public void SaveBagState(long id, byte[] state) => SaveBlob(id, "bag_state", state);
    public void SaveBagState(long id, byte[] state, RoleItemPlanBuilder transaction) => SaveBlob(id, "bag_state", state, transaction);
    public byte[]? LoadStatPlans(long id) => LoadBlob(id, "stat_plans");
    public void SaveStatPlans(long id, byte[] state, RoleItemPlanBuilder? tx = null) => SaveBlob(id, "stat_plans", state, tx);
    /// <summary>The selected allocation and its plan snapshot are one durable transition.</summary>
    public void SaveStatPlanProgress(long id, RoleProgress progress, byte[] plans)
    {
        var builder = new RoleItemPlanBuilder(_accounts);
        builder.Progress(id, progress);
        builder.RoleBlob(id, "stat_plans", plans);
        builder.Commit(_accounts, "role/stat-plan-progress");
    }
    public byte[]? LoadWorldState(long id) => LoadBlob(id, "world_state");
    public void SaveWorldState(long id, byte[] state) => SaveBlob(id, "world_state", state);

    public void SaveAutoDecompose(long id, byte[] setting) => SaveBlob(id, "auto_decompose", setting);

    public byte[]? LoadEmblems(long id) => LoadBlob(id, "emblems");

    public void SaveEmblems(long id, byte[] emblems, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "emblems", emblems, transaction);

    public void SaveShop(long id, byte[] shop, RoleItemPlanBuilder? tx = null) => SaveBlob(id, "shop", shop, tx);
    public byte[]? LoadGuildInfo(long id) => LoadBlob(id, "guild_info");
    public void SaveGuildInfo(long id, byte[] info, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "guild_info", info, transaction);

    public byte[]? LoadMailbox(long id, RoleItemPlanBuilder? transaction = null) => LoadBlob(id, "mailbox", transaction);

    public void SaveMailbox(long id, byte[] mailbox, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "mailbox", mailbox, transaction);

    public byte[]? LoadAuction(long id) => LoadBlob(id, "auction");
    public byte[]? LoadAuction(long id, RoleItemPlanBuilder transaction) => LoadBlob(id, "auction", transaction);
    public void SaveAuction(long id, byte[] auction, RoleItemPlanBuilder? transaction = null) => SaveBlob(id, "auction", auction, transaction);

    public void SaveGuides(long id, byte[] guides) => SaveBlob(id, "guides", guides);

    private byte[]? LoadBlob(long id, string column, RoleItemPlanBuilder? transaction = null)
    {
        if (transaction == null && _accounts.TryGetStaged(id, column, out var staged)) return staged;
        if (transaction == null && _accounts.Snapshots.TryBlob(id, column, out var snapshot))
            return SnapshotVerify.Same(snapshot, () => _accounts.ReadSync<byte[]?>(new RoleBlobReadPlan(id, column)), $"role {id} {column}");
        return transaction == null ? _accounts.ReadSync<byte[]?>(new RoleBlobReadPlan(id, column))
            : transaction.ReadRoleBlob(_accounts, id, column);
    }

    private void SaveBlob(long id, string column, byte[] value, RoleItemPlanBuilder? transaction = null, bool force = false)
    {
        // Stage G: during the role's selection its default blobs are staged and committed together at the end.
        if (transaction == null && !force && _accounts.TryStage(id, column, value,
            () => _accounts.Snapshots.Unchanged(id, column, value) || _accounts.Snapshots.PersistedEqual(id, column, value)))
            return;
        // A login's Load/normalize often saves back exactly what its snapshot read: nothing to write.
        if (transaction == null && !force && _accounts.Snapshots.Unchanged(id, column, value))
        {
            SnapshotVerify.Same(value, () => _accounts.ReadSync<byte[]?>(new RoleBlobReadPlan(id, column)), $"role {id} {column} skipped write");
            return;
        }
        if (transaction != null) { transaction.RoleBlob(id, column, value); return; }
        // C/D 16 part 3: a runtime save of the bytes this process last persisted for the part (no other commit since).
        if (!force && _accounts.Snapshots.PersistedEqual(id, column, value))
        {
            SnapshotVerify.Same(value, () => _accounts.ReadSync<byte[]?>(new RoleBlobReadPlan(id, column)), $"role {id} {column} unchanged save");
            return;
        }
        var save = _accounts.Snapshots.BeginSave(id, column);
        bool committed = false;
        try { committed = _accounts.WriteLegacySync<int>(new RoleBlobWritePlan(id, column, value)) > 0; }
        finally { _accounts.Snapshots.EndSave(id, column, save, value, committed); }
    }

    public byte[]? LoadTasks(long id)
        => LoadBlob(id, "tasks");

    public void SaveTasks(long id, byte[] tasks, RoleItemPlanBuilder? transaction = null)
        => SaveBlob(id, "tasks", tasks, transaction);

    public byte[]? LoadTaskRuntime(long id) => LoadBlob(id, "task_runtime");

    public void SaveTaskRuntime(long id, byte[] runtime) => SaveBlob(id, "task_runtime", runtime);

    /// <summary>The library and its mandatory quest debt share one durable boundary. The source is
    /// the role lifecycle, card id and unlock variant; a replay cannot create another obligation.</summary>
    internal async Task CommitCardUnlockAsync(long role, long epoch, int card, bool treasure, byte[] library,
        IReadOnlyList<InvItem>? inventory = null, Ro3Emu.Server.Economy.EconomyMutation? economy = null, Action? publish = null)
    {
        var builder = NewTransaction();
        if (economy != null)
        {
            if (inventory == null || economy.CommandKind != "cd13a/card-unlock")
                throw new ArgumentException("Card unlock envelope requires the owned inventory and command kind.");
            builder.ItemDelta(role, inventory, inventory);
            builder.WithEconomy(economy);
        }
        StageCardUnlock(builder, role, epoch, card, treasure, library);
        await CommitAsync(builder, "card/unlock-task-followup", _ => publish?.Invoke());
    }

    /// <summary>The unlocked library and its pending quest follow-up, staged into a transaction that also pays the unlock.</summary>
    internal void StageCardUnlock(RoleItemPlanBuilder builder, long role, long epoch, int card, bool treasure, byte[] library)
    {
        byte[]? old = builder.ReadRoleBlob(_accounts, role, "task_followup");
        var pending = old is { Length: > 0 }
            ? JsonSerializer.Deserialize<HashSet<string>>(old) ?? new HashSet<string>() : new HashSet<string>();
        pending.Add($"{role}:{epoch}:{card}:{(treasure ? 1 : 0)}");
        builder.RoleBlob(role, "library", library);
        builder.RoleBlob(role, "task_followup", JsonSerializer.SerializeToUtf8Bytes(pending));
    }

    internal bool HasTaskFollowup(long role) => LoadBlob(role, "task_followup") is { Length: > 2 };

    internal byte[]? LoadTaskFollowup(long role) => LoadBlob(role, "task_followup");
    internal byte[]? ReadTaskFollowup(RoleItemPlanBuilder builder, long role) =>
        builder.ReadRoleBlob(_accounts, role, "task_followup");
    internal byte[]? ReadWildAfk(RoleItemPlanBuilder builder, long role) =>
        builder.ReadRoleBlob(_accounts, role, "wild_afk");
    internal byte[]? ReadAreaConcur(RoleItemPlanBuilder builder, long role) =>
        builder.ReadRoleBlob(_accounts, role, "area_concur");

    /// <summary>Only a fully settled task pass may erase the debt; its tasks/runtime snapshot and
    /// debt clear commit together. Award failures leave the original source for reconnect recovery.</summary>
    internal async Task SaveSettledTasksAsync(long role, byte[] tasks, byte[] runtime)
    {
        var builder = NewTransaction();
        builder.ReadRoleBlob(_accounts, role, "task_followup");
        builder.RoleBlob(role, "tasks", tasks);
        builder.RoleBlob(role, "task_runtime", runtime);
        builder.RoleBlob(role, "task_followup", JsonSerializer.SerializeToUtf8Bytes(Array.Empty<string>()));
        await CommitAsync(builder, "task/settled-followup");
    }

    /// <summary>One durable boundary for a source's award, costs, progression and optional quest completion.</summary>
    public bool SaveAwardState(long id, RoleProgress p, byte[]? deliveredTaskState, byte[]? taskRuntimeState,
        IReadOnlyList<InvItem> items, Action<RoleItemPlanBuilder>? persist = null,
        byte[]? expectedDaily = null, byte[]? nextDaily = null,
        byte[]? expectedTasks = null, bool checkTasks = false)
    {
        if ((expectedDaily == null) != (nextDaily == null))
            throw new ArgumentException("Daily award requires both expected and next daily state");
        if (checkTasks && deliveredTaskState == null)
            throw new ArgumentException("Task comparison requires a next task state");
        var builder = new RoleItemPlanBuilder(_accounts);
        try { builder.BindProgressSource(id, p); }
        catch (RolePlanConflictException) { return false; }
        builder.Add(new RoleAwardStatePlan(id, p, deliveredTaskState, taskRuntimeState,
            expectedDaily, nextDaily, expectedTasks, checkTasks));
        int itemReceipt = builder.Items(id, items);
        persist?.Invoke(builder);
        try
        {
            var receipts = builder.Commit(_accounts, "role/award-state");
            p.SourceRevision = builder.CommittedVector(id, RevisionStore.Progress);
            ItemSnapshotWritePlan.ApplyGuids(items, (long[])receipts[itemReceipt]!);
            return true;
        }
        catch (RolePlanConflictException) { return false; }
    }

    /// <summary>Owned award commit. Capture occurs before admission; the writer receives only copied values.</summary>
    internal async Task<bool> SaveAwardStateAsync(long id, RoleProgress p, byte[]? deliveredTaskState, byte[]? taskRuntimeState,
        IReadOnlyList<InvItem> items, Action<RoleItemPlanBuilder>? persist = null,
        byte[]? expectedDaily = null, byte[]? nextDaily = null,
        byte[]? expectedTasks = null, bool checkTasks = false)
    {
        if ((expectedDaily == null) != (nextDaily == null))
            throw new ArgumentException("Daily award requires both expected and next daily state");
        if (checkTasks && deliveredTaskState == null)
            throw new ArgumentException("Task comparison requires a next task state");
        var builder = NewTransaction();
        try { builder.BindProgressSource(id, p); }
        catch (RolePlanConflictException) { return false; }
        builder.Add(new RoleAwardStatePlan(id, p, deliveredTaskState, taskRuntimeState,
            expectedDaily, nextDaily, expectedTasks, checkTasks));
        int itemReceipt = builder.Items(id, items);
        persist?.Invoke(builder);
        try
        {
            var receipts = await CommitAsync(builder, "role/award-state");
            p.SourceRevision = builder.CommittedVector(id, RevisionStore.Progress);
            ItemSnapshotWritePlan.ApplyGuids(items, (long[])receipts[itemReceipt]!);
            return true;
        }
        catch (RolePlanConflictException) { return false; }
    }

    /// <summary>
    /// C/D 14: reads the role at one read snapshot on a reader worker and installs it for this login (only for the
    /// owning account). Returns false when nothing was installed; the store reads then simply go to SQL.
    /// </summary>
    public async Task<bool> PrepareLoginSnapshotAsync(long id, string account)
    {
        if (!LoginSnapshots) return false;
        long started = _accounts.Snapshots.Sequence;
        RoleSnapshot snapshot;
        try { snapshot = await _accounts.ReadAsync<RoleSnapshot>(new RoleSnapshotReadPlan(id)); }
        catch (DbQueueFullException) { return false; }
        if (snapshot.Identity?.Account != account) return false;
        _accounts.Snapshots.Install(snapshot, started);
        LastLoginSnapshot = (snapshot.Bytes, snapshot.ReadMs);
        return true;
    }

    public void ReleaseLoginSnapshot(long id) => _accounts.Snapshots.Remove(id);
    /// <summary>Measurement/rollback switch: false = every login read goes to SQL as before C/D 14.</summary>
    internal bool LoginSnapshots { get; set; } = true;
    internal (long Bytes, double ReadMs) LastLoginSnapshot { get; private set; }

    public RoleProgress LoadProgress(long id)
    {
        if (_accounts.Snapshots.TryProgress(id, out var snapshot) && snapshot != null)
            return SnapshotVerify.Same(snapshot, () => _accounts.ReadSync<RoleProgress>(new RoleProgressReadPlan(id)), $"role {id} progress")!;
        return _accounts.ReadSync<RoleProgress>(new RoleProgressReadPlan(id));
    }

    internal static RoleProgress ReadProgress(SqliteDataReader r)
    {
        var p = new RoleProgress
        {
            Career = r.GetInt32(0),
            Level = r.GetInt32(1),
            JobLevel = r.GetInt32(2),
            Exp = r.GetInt64(3),
            JobExp = r.GetInt64(4),
            BonusPoints = r.GetInt32(6),
            StarterKit = r.GetInt32(8) != 0,
        };
        var stats = r.GetString(5).Split(',');
        for (int i = 0; i < p.Stats.Length && i < stats.Length; i++) p.Stats[i] = int.TryParse(stats[i], out var v) ? v : 1;
        foreach (var pair in r.GetString(7).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split(':');
            if (kv.Length == 2 && int.TryParse(kv[0], out var k) && long.TryParse(kv[1], out var amount)) p.Currency[k] = amount;
        }
        ReadIntMap(r.GetString(9), p.Skills);
        ReadIntMap(r.GetString(10), p.SkillBar);
        return p;
    }

    private static void ReadIntMap(string text, Dictionary<int, int> into)
    {
        foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split(':');
            if (kv.Length == 2 && int.TryParse(kv[0], out var k) && int.TryParse(kv[1], out var v)) into[k] = v;
        }
    }

    public void SaveProgress(long id, RoleProgress p, RoleItemPlanBuilder? transaction = null)
    {
        if (transaction is not null) { transaction.Progress(id, p); return; }
        var plan = new RoleProgressWritePlan(id, p);
        if (p.SourceRevision is { } source)
        {
            if (source.RoleId != id) throw new InvalidOperationException("Progress source role does not match save role.");
            plan.Expect([source]);
            _accounts.WriteSync<int>(plan);
            p.SourceRevision = source.Advance(RevisionStore.Progress);
        }
        else
        {
            _accounts.WriteLegacySync<int>(plan);
            p.SourceRevision = plan.ExpectedRevisions![id].Advance(RevisionStore.Progress);
        }
    }

    internal static List<Role> Read(SqliteCommand cmd)
    {
        var roles = new List<Role>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            roles.Add(new Role(
                r.GetInt64(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("account")), r.GetInt32(r.GetOrdinal("area")),
                r.GetString(r.GetOrdinal("name")), r.GetInt32(r.GetOrdinal("sex")), r.GetInt32(r.GetOrdinal("career")),
                r.GetInt32(r.GetOrdinal("level")), r.GetInt32(r.GetOrdinal("hair")),
                r.GetInt64(r.GetOrdinal("create_time")), r.GetInt64(r.GetOrdinal("last_login_time")),
                r.GetInt64(r.GetOrdinal("scene_entry")),
                r.GetFloat(r.GetOrdinal("pos_x")), r.GetFloat(r.GetOrdinal("pos_y")), r.GetFloat(r.GetOrdinal("pos_z")),
                r.GetFloat(r.GetOrdinal("face"))));
        }
        return roles;
    }

}
