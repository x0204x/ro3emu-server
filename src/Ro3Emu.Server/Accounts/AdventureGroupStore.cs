using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Durable Adventure Group aggregates (AdventureGroupService) and the exclusive role membership index, modelled on
/// GuildStore: table adventure_group (id, unique name, JSON state) and adventure_group_member (role_id primary key),
/// entity revision "adventure_group". Committed aggregates are served from a generation cache that every writer job
/// touching the tables invalidates; callers always receive their own copy.
/// </summary>
public sealed class AdventureGroupStore(AccountStore accounts)
{
    /// <summary>The aggregate's table, as <see cref="RowMissingException"/> names it.</summary>
    internal const string Table = "adventure_group";

    private readonly AccountStore _accounts = accounts;

    public sealed class State
    {
        internal OwnerRevisionValue? CapturedRevision { get; set; }
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Announce { get; set; } = "";
        public List<int> Labels { get; set; } = new();
        public int Background { get; set; } = 1;
        /// <summary>romsg.AdventureGroupStatus: 1 Helper (support recruitment), 2 Temp (creation ritual), 3 Normal.</summary>
        public int Status { get; set; } = 1;
        /// <summary>romsg.AdventureGroupCreateType: 1 recruit board, 2 NPC (party at the mentor).</summary>
        public int CreateType { get; set; } = 1;
        /// <summary>Unix seconds: the recruit post for a support group, the founding for a normal one.</summary>
        public long CreateTime { get; set; }
        /// <summary>Unix seconds of a normal group's open recruitment, 0 when it is not recruiting.</summary>
        public long RecruitTime { get; set; }
        public long Owner { get; set; }
        public bool AutoAgreeApply { get; set; }
        public bool AutoAgreeHelperApply { get; set; }
        public bool CanSeeMemberAnnounce { get; set; } = true;
        public long ChangeOwnerTime { get; set; }
        public int Level { get; set; } = 1;
        /// <summary>Group EXP (Resource.adventGroupMoney), cumulative; AdventureGroupLevelAwardConfig._iNum ends each level.</summary>
        public long Money { get; set; }
        /// <summary>Group EXP gained this week (Resource.weekMoney), capped by AdventureGroupMoneyWeeklyLimit.</summary>
        public int WeekMoney { get; set; }
        public long WeekResetTime { get; set; }
        public long DayResetTime { get; set; }
        /// <summary>The chosen sapling, an NPCConfig id 200032..200041 (0 = none).</summary>
        public int SaplingId { get; set; }
        public int SaplingWatering { get; set; }
        public int SaplingUpvote { get; set; }
        public int ConveneUseTime { get; set; }
        public long LastConveneTime { get; set; }
        public List<int> AvailableBackgrounds { get; set; } = new();
        public long LastUpdateTime { get; set; }
        public Dictionary<long, Member> Members { get; set; } = new();
        /// <summary>Support applications (status 1) or join applications (status 3), by role.</summary>
        public Dictionary<long, Applicant> Applications { get; set; } = new();
        public List<GroupEvent> Events { get; set; } = new();
    }

    public sealed class Member
    {
        public long RoleId { get; set; }
        public string Name { get; set; } = "";
        public long EnterTime { get; set; }
        public string Nickname { get; set; } = "";
        public Declaration? Declaration { get; set; }
        /// <summary>Waterings on <see cref="WaterDay"/> (AdventureGroupWaterSaplingLimit per day).</summary>
        public int WateringCount { get; set; }
        public long WaterDay { get; set; }
    }

    public sealed class Declaration
    {
        public string Content { get; set; } = "";
        /// <summary>romsg.DeclarationType: 1 text, 2 voice.</summary>
        public int Type { get; set; }
        public byte[]? Voice { get; set; }
        public int VoiceDuration { get; set; }
    }

    public sealed class Applicant
    {
        public long RoleId { get; set; }
        public string Name { get; set; } = "";
        public long Time { get; set; }
    }

    public sealed class GroupEvent
    {
        public int Type { get; set; }
        public long Time { get; set; }
        public long First { get; set; }
        public long Second { get; set; }
        public string Third { get; set; } = "";
    }

    // ---- generation cache (as GuildStore, C/D 16; static generation for the same reason) ----

    private static long _cacheGeneration;
    private readonly object _cacheLock = new();
    private long _cachedGeneration = -1;
    private Dictionary<long, (byte[] Json, OwnerRevisionValue? Revision)> _byId = new();
    private Dictionary<long, long> _groupOfRole = new();

    /// <summary>Called by the writer after a job that wrote adventure_group or adventure_group_member rows.</summary>
    internal static void Invalidate() => Interlocked.Increment(ref _cacheGeneration);

    private void Refresh()
    {
        long generation = Volatile.Read(ref _cacheGeneration);
        lock (_cacheLock) if (_cachedGeneration == generation) return;
        var rows = _accounts.ReadSync<List<State>>(new AdventureGroupReadPlan(AdventureGroupReadKind.List));
        var ids = new Dictionary<long, (byte[], OwnerRevisionValue?)>();
        var roles = new Dictionary<long, long>();
        foreach (var row in rows)
        {
            ids[row.Id] = (JsonSerializer.SerializeToUtf8Bytes(row), row.CapturedRevision);
            foreach (long role in row.Members.Keys) roles[role] = row.Id;
        }
        lock (_cacheLock)
        {
            // A read during which a commit landed is returned below but not kept.
            if (Volatile.Read(ref _cacheGeneration) != generation) { _byId = ids; _groupOfRole = roles; _cachedGeneration = -1; return; }
            _byId = ids; _groupOfRole = roles; _cachedGeneration = generation;
        }
    }

    private static State Copy((byte[] Json, OwnerRevisionValue? Revision) cached)
    {
        var state = JsonSerializer.Deserialize<State>(cached.Json)!;
        state.CapturedRevision = cached.Revision;
        return state;
    }

    public State? Get(long id)
    {
        Refresh();
        lock (_cacheLock) return _byId.TryGetValue(id, out var hit) ? Copy(hit) : null;
    }

    public State? ForRole(long roleId)
    {
        Refresh();
        lock (_cacheLock) return _groupOfRole.TryGetValue(roleId, out long id) && _byId.TryGetValue(id, out var hit) ? Copy(hit) : null;
    }

    public IReadOnlyList<State> List()
    {
        Refresh();
        lock (_cacheLock) return _byId.OrderBy(p => p.Key).Select(p => Copy(p.Value)).ToList();
    }

    public bool NameExists(string name, long exceptId = 0) =>
        _accounts.ReadSync<bool>(new AdventureGroupReadPlan(AdventureGroupReadKind.NameExists, exceptId, name));

    /// <summary>Capture the aggregate's revision and stage its save; a generated id is published after the commit.</summary>
    internal void EnlistSave(State state, RoleItemPlanBuilder transaction)
    {
        var revision = state.Id == 0 ? (OwnerRevisionValue?)null :
            transaction.CaptureEntity(RevisionStore.Entity("adventure_group", state.Id), state.CapturedRevision);
        if (state.Id != 0 && state.CapturedRevision is null && revision is { Exists: true })
            throw new InvalidOperationException("Adventure group state lacks a source revision.");
        transaction.Add(new AdventureGroupSavePlan(state), receipt =>
        {
            state.Id = (long)receipt!;
            state.CapturedRevision = revision is { } current
                ? new OwnerRevisionValue(current.Key, current.Revision + 1, true)
                : new OwnerRevisionValue(RevisionStore.Entity("adventure_group", state.Id), 1, true);
        });
    }

    internal void EnlistDelete(State state, RoleItemPlanBuilder transaction)
    {
        transaction.CaptureEntity(RevisionStore.Entity("adventure_group", state.Id), state.CapturedRevision);
        transaction.Add(new AdventureGroupDeletePlan(state.Id));
    }

    /// <summary>True when a SQLite failure is the name or membership uniqueness (constraint 19).</summary>
    internal static bool IsConflict(Exception e) => e is SqliteException { SqliteErrorCode: 19 }
        || e is RowMissingException { Table: Table }
        || e is OwnerRevisionConflictException;
}
