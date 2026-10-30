using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Accounts;

/// <summary>
/// Accounts and login tokens in SQLite. An unknown identity is registered on its first login (research server), or on
/// the web panel's sign-up page through the same code (<see cref="RegisterAsync"/>).
/// </summary>
public sealed partial class AccountStore : IDisposable
{
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly Lazy<EconomyCatalog> _escrowCatalog;
    internal EconomyCatalog EscrowCatalog => _escrowCatalog.Value;
    private readonly Lazy<EconomyCatalog> _economyCatalog;
    private readonly Lazy<DbWriter> _writer;

    /// <summary>Generations of the stores' caches over this database, bumped by its writer.</summary>
    internal CacheGenerations Caches { get; } = new();
    private readonly Lazy<DbReadPool> _readers;
    private readonly Game.GameLoop? _loop;
    private readonly ILogger<AccountStore>? _log;
    private readonly object _runtimeGate = new();
    private bool _disposed;
    private int _failNextEconomyAcknowledgementForTest;
    private int _failNextEconomyResolutionForTest;
    public DbAccessMetrics AccessMetrics { get; } = new();
    /// <summary>Role snapshots installed for logins (C/D 14); every committed role write invalidates its parts.</summary>
    public RoleSnapshots Snapshots { get; } = new();

    private readonly Diagnostics.ServerMetrics? _metrics;

    public AccountStore(IOptions<ServerOptions> options, Diagnostics.ServerMetrics? metrics = null, Game.GameLoop? loop = null,
        ILogger<AccountStore>? log = null)
    {
        _metrics = metrics;
        if (metrics != null) metrics.DbAccess = AccessMetrics;
        _loop = loop;
        _log = log;
        // A half-done restore may have moved the live file away; opening now would create an empty realm (C/D 19).
        DatabaseRestore.ThrowIfUnfinished(options.Value.DataDir);
        Directory.CreateDirectory(options.Value.DataDir);
        _databasePath = Path.Combine(options.Value.DataDir, "ro3emu.db");
        _escrowCatalog = new Lazy<EconomyCatalog>(() =>
        {
            if (options.Value.GameDataDir is { } configured) return EconomyCatalog.Load(configured);
            for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
                if (File.Exists(Path.Combine(parent.FullName, "data", "config", "ItemConfig.json")))
                    return EconomyCatalog.Load(Path.Combine(parent.FullName, "data"));
            return EconomyCatalog.Load(options.Value.DataDir);
        });
        _economyCatalog = new(() => EconomyCatalog.Load(options.Value.GameDataDir ?? options.Value.DataDir));
        _connectionString = $"Data Source={_databasePath}";
        using var db = OpenBootstrap();
        using (var wal = db.CreateCommand())
        {
            // WAL (persistent in the file): readers stop blocking the writer, and a commit is an append instead of a
            // rollback-journal round trip (2.9-3.1 ms -> 0.5-0.8 ms per write in the audit's benchmark). synchronous
            // stays FULL (the bundled SQLite default, asserted in the tests): commits survive an OS crash / power loss,
            // which the economy's commit-before-reply rule relies on.
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS account (
                name          TEXT PRIMARY KEY,
                password_hash BLOB NOT NULL,
                salt          BLOB NOT NULL,
                created_utc   TEXT NOT NULL
            );
            -- Login finds an account in any letter case (AccountCredentialsRead); not unique, legacy spellings may coexist.
            CREATE INDEX IF NOT EXISTS account_name_nocase ON account(name COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS auth_token (
                token       TEXT PRIMARY KEY,
                kind        TEXT NOT NULL,
                account     TEXT NOT NULL,
                expires_utc INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS owner_revision (
                kind TEXT NOT NULL,
                id TEXT NOT NULL,
                part TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision >= 0),
                PRIMARY KEY(kind,id,part)
            );
            CREATE TABLE IF NOT EXISTS owner_operation_receipt (
                operation_id TEXT PRIMARY KEY,
                family TEXT NOT NULL,
                result BLOB NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        _writer = new(() => new DbWriter(_connectionString, AccessMetrics, _metrics, _log)
        { RoleWritesCommitted = Snapshots.Committed, Caches = Caches });
        _readers = new(() => new DbReadPool(_databasePath, AccessMetrics, _metrics, _log));
        foreach (var plan in DatabaseSchema.Plans()) WriteSync<int>(plan);
    }

    internal string DatabasePath => _databasePath;
    internal EconomyCatalog EconomyCatalog => _economyCatalog.Value;
    /// <summary>Instance-scoped test-only lost-ack seam; no runtime or network surface.</summary>
    internal void FailNextEconomyAcknowledgementForTest() =>
        Interlocked.Exchange(ref _failNextEconomyAcknowledgementForTest, 1);
    internal bool ConsumeEconomyAcknowledgementFaultForTest() =>
        Interlocked.Exchange(ref _failNextEconomyAcknowledgementForTest, 0) != 0;
    internal void FailNextEconomyResolutionForTest() =>
        Interlocked.Exchange(ref _failNextEconomyResolutionForTest, 1);
    internal bool ConsumeEconomyResolutionFaultForTest() =>
        Interlocked.Exchange(ref _failNextEconomyResolutionForTest, 0) != 0;
    internal RoleItemPlanBuilder NewTransaction() => new(this);

    /// <summary>Compatibility reads and online backup only. Runtime writes must enter DbWriter.</summary>
    internal SqliteConnection Open([System.Runtime.CompilerServices.CallerMemberName] string source = "read")
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        using var measurement = _metrics?.MeasureDatabaseOpen();
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            db.Open();
            Diagnostics.SqliteMetrics.Attach(db, _metrics);
            using var queryOnly = db.CreateCommand();
            queryOnly.CommandText = "PRAGMA query_only=ON";
            queryOnly.ExecuteNonQuery();
            DbWriteGuard.AttachRead(db, AccessMetrics, source);
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private SqliteConnection OpenBootstrap()
    {
        using var measurement = _metrics?.MeasureDatabaseOpen();
        var db = new SqliteConnection(new SqliteConnectionStringBuilder(_connectionString) { Pooling = false }.ToString());
        try
        {
            db.Open();
            Diagnostics.SqliteMetrics.Attach(db, _metrics);
            DbWriteGuard.AttachBootstrap(db, AccessMetrics);
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    /// <summary>Banned: the credential matched (or registered the name) but the account has an active ban.</summary>
    public enum LoginResult { Ok, Registered, BadPassword, Banned }

    private bool OnLoop => _loop?.IsOnLoop == true || _metrics != null && ReferenceEquals(ServerMetrics.LoopOwner, _metrics);
    private DbWriter Writer { get { lock (_runtimeGate) { ObjectDisposedException.ThrowIf(_disposed, this); return _writer.Value; } } }
    internal Action<string>? BeforeCommitForTest { get => Writer.BeforeCommitForTest; set => Writer.BeforeCommitForTest = value; }
    private DbReadPool Readers { get { lock (_runtimeGate) { ObjectDisposedException.ThrowIf(_disposed, this); return _readers.Value; } } }
    internal async Task<T> WriteAsync<T>(DbPlan plan)
    {
        plan = Prepare(plan);
        T result = await Writer.ExecuteAsync<T>(plan);
        RecordResolvedCommit(plan, result);
        return result;
    }
    /// <summary>Test barrier: a lazy write's capture is taken, its submission to the writer has not happened yet.</summary>
    internal Func<string, Task>? BeforeLazyWriteForTest { get; set; }
    /// <summary>The writer's lazy lane for a plan whose expectations the caller captured (C/D 18 position).</summary>
    internal async Task<T> WriteLazyAsync<T>(DbPlan plan)
    {
        if (BeforeLazyWriteForTest is { } beforeLazy) await beforeLazy(plan.Label);
        plan = Prepare(plan);
        T result = await Writer.ExecuteAsync<T>(plan, lazy: true);
        RecordResolvedCommit(plan, result);
        return result;
    }
    internal Func<string, Task>? BeforeOwnedCommitForTest { get; set; }
    internal Func<string, Task>? BeforeOwnedApplyForTest { get; set; }
    internal Action<string>? AfterWriterCommitForTest { set => Writer.AfterCommitForTest = value; }
    /// <summary>Converted owner families retain their full capability through receipt/apply.
    /// This does not enable async writes for legacy families.</summary>
    internal async Task<T> WriteOwnedAsync<T>(DbPlan plan)
    {
        plan = Prepare(plan);
        if (_loop?.IsOnLoop == true && _loop.Owners is { } owners)
        {
            var context = owners.Current ?? throw new InvalidOperationException("Async owner commit needs a reservation.");
            var keys = plan.EntityWrites.Concat(plan.EntityDeltaWrites).Distinct()
                .Select(key => Game.Ownership.ReservationKey.Entity(key.Kind, key.Id, key.Part));
            if (!owners.Reservations.Covers(context.Operation, keys) &&
                !owners.AuditUncovered($"{context.Origin} commits {plan.Label} with unreserved entities"))
                throw new InvalidOperationException("Async owner commit did not reserve every entity.");
        }
        if (BeforeOwnedCommitForTest is { } beforeCommit) await beforeCommit(plan.Label);
        T result;
        try { result = await Writer.ExecuteAsync<T>(plan); }
        catch (OwnerOutcomeUnknownException) { RequireOwnerRecovery(plan); throw; }
        try
        {
            if (BeforeOwnedApplyForTest is { } beforeApply) await beforeApply(plan.Label);
            RecordResolvedCommit(plan, result);
        }
        catch { RequireOwnerRecovery(plan); throw; }
        return result;
    }
    /// <summary>Family envelopes resolve their own retained receipt after an ambiguous writer ACK.</summary>
    internal Func<string, Task>? BeforeEconomyResolveForTest { get; set; }
    /// <summary>Resolve E03's retained typed result before owner Apply when the writer ACK is lost.</summary>
    internal async Task<object?[]> WriteEscrowEconomyAsync(DbPlan plan, EscrowEconomyIntent intent, RoleItemPlan child)
    {
        plan = Prepare(plan);
        if (_loop?.IsOnLoop == true && _loop.Owners is { } owners)
        {
            var context = owners.Current ?? throw new InvalidOperationException("Async E03 commit needs a reservation.");
            var keys = plan.EntityWrites.Concat(plan.EntityDeltaWrites).Distinct()
                .Select(key => Game.Ownership.ReservationKey.Entity(key.Kind, key.Id, key.Part));
            if (!owners.Reservations.Covers(context.Operation, keys))
                throw new InvalidOperationException("Async E03 commit did not reserve every entity.");
        }
        if (BeforeOwnedCommitForTest is { } beforeCommit) await beforeCommit(plan.Label);
        object?[] result;
        try { result = await Writer.ExecuteAsync<object?[]>(plan); }
        catch (EconomyOperationConflictException) { throw; }
        catch (Exception original)
        {
            object?[]? resolved;
            try
            {
                if (ConsumeEconomyResolutionFaultForTest())
                    throw new IOException("Test-only E03 receipt resolution failure.");
                if (BeforeEconomyResolveForTest is { } beforeResolve) await beforeResolve(intent.OperationId);
                resolved = await ReadAsync<object?[]?>(new EscrowEconomyReceiptResolveReadPlan(intent, child));
            }
            catch (EconomyOperationConflictException) { throw; }
            catch (Exception readError)
            {
                RequireOwnerRecovery(plan, intent.OperationId);
                throw new EconomyOutcomeUnknownException(intent.OperationId, original, readError);
            }
            if (resolved is null) throw;
            result = resolved;
        }
        try
        {
            if (BeforeOwnedApplyForTest is { } beforeApply) await beforeApply(plan.Label);
            RecordResolvedCommit(plan, result);
        }
        catch { RequireOwnerRecovery(plan, intent.OperationId); throw; }
        return result;
    }
    internal async Task<object?[]> WriteFamilyEconomyAsync(DbPlan plan, EconomyMutation mutation, int expectedIds,
        int expectedChildren)
    {
        plan = Prepare(plan);
        if (_loop?.IsOnLoop == true && _loop.Owners is { } owners)
        {
            var context = owners.Current ?? throw new InvalidOperationException("Async family commit needs a reservation.");
            var keys = plan.EntityWrites.Concat(plan.EntityDeltaWrites).Distinct()
                .Select(key => Game.Ownership.ReservationKey.Entity(key.Kind, key.Id, key.Part));
            if (!owners.Reservations.Covers(context.Operation, keys) &&
                !owners.AuditUncovered($"{context.Origin} commits {plan.Label} with unreserved entities"))
                throw new InvalidOperationException("Async family commit did not reserve every entity.");
        }
        if (BeforeOwnedCommitForTest is { } beforeCommit) await beforeCommit(plan.Label);
        object?[] result;
        try { result = await Writer.ExecuteAsync<object?[]>(plan); }
        catch (EconomyOperationConflictException) { throw; }
        catch (Exception original)
        {
            object?[]? resolved;
            try
            {
                if (ConsumeEconomyResolutionFaultForTest())
                    throw new IOException("Test-only economy receipt resolution failure.");
                if (BeforeEconomyResolveForTest is { } beforeResolve)
                    await beforeResolve(mutation.OperationId);
                resolved = await ReadAsync<object?[]?>(new EconomyReceiptResolveReadPlan(mutation, expectedIds,
                    expectedChildren));
            }
            catch (EconomyOperationConflictException) { throw; }
            catch (Exception readError)
            {
                RequireOwnerRecovery(plan, mutation.OperationId);
                throw new EconomyOutcomeUnknownException(mutation.OperationId, original, readError);
            }
            if (resolved is null) throw;
            result = resolved;
        }
        try
        {
            if (BeforeOwnedApplyForTest is { } beforeApply) await beforeApply(plan.Label);
            RecordResolvedCommit(plan, result);
        }
        catch { RequireOwnerRecovery(plan, mutation.OperationId); throw; }
        return result;
    }
    internal void RequireOwnerRecovery(DbPlan plan, string? operationId = null)
    {
        if (_loop?.IsOnLoop != true || _loop.Owners is not { } owners) return;
        foreach (long role in plan.RolePins)
            if (owners.Cached(role) is { } owner) owner.State = Game.Ownership.RoleOwnerState.RecoveryRequired;
        var keys = plan.RolePins.Select(role => Game.Ownership.ReservationKey.Role(role))
            .Concat(plan.EntityWrites.Concat(plan.EntityDeltaWrites).Select(key => Game.Ownership.ReservationKey.Entity(key.Kind, key.Id, key.Part)));
        owners.Reservations.Quarantine(operationId ?? plan.OperationId!, keys);
        _log?.LogError("Owner operation {Operation} quarantined its participants for recovery", operationId ?? plan.OperationId);
    }
    internal Task<T> ReadAsync<T>(DbReadPlan plan) => Readers.ExecuteAsync<T>(plan);
    /// <summary>Test/diagnostic observer of synchronous writes (plan label, on loop).</summary>
    internal Action<string, bool>? WriteSyncObserved { get; set; }
    internal Action<DbPlan, bool>? WriteSyncPlanObserved { get; set; }
    internal T WriteSync<T>(DbPlan plan)
    {
        WriteSyncObserved?.Invoke(plan.Label, OnLoop);
        WriteSyncPlanObserved?.Invoke(plan, OnLoop);
        plan = Prepare(plan);
        T result = Writer.ExecuteLegacySync<T>(plan, OnLoop);
        RecordResolvedCommit(plan, result);
        return result;
    }
    /// <summary>Keep the owners' committed vectors exact: a commit resolved on the loop is recorded; one resolved
    /// elsewhere (a test, an HTTP endpoint, a micro service thread) marks its roles, whose owner vectors are then not
    /// served until a SQL read resynchronizes them (C/D 16).</summary>
    internal void RecordResolvedCommit(DbPlan plan, object? result)
    {
        if (plan.IsReplay) return;
        if (_loop?.IsOnLoop == true && _loop.Owners is { } owners)
            foreach (var group in plan.MutatedRoleWrites(result).Distinct().GroupBy(key => key.RoleId))
                owners.RecordCommittedRole(plan.ExpectedRevisions![group.Key], group.Select(key => key.Part));
        else
            foreach (long role in plan.MutatedRoleWrites(result).Select(key => key.RoleId).Distinct())
                _unrecordedCommits.AddOrUpdate(role, 1, (_, count) => count + 1);
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _unrecordedCommits = new();

    /// <summary>C/D 16: a role's expected vector for a plan - the owner's when it is exact, else SQL (which also
    /// resynchronizes an attached owner whose commits were made off the loop).</summary>
    internal RoleRevisionVector RoleVector(long role, bool sqlOnly = false)
    {
        if (!sqlOnly && OwnerVector(role) is { } cached) return cached;
        return ReadVector(role, overtaken: false);
    }
    /// <summary>The role's durable vector from SQL. An owner behind it is resynchronized: after off-loop commits
    /// (marked roles, attached or loading owners) or after an overtaken capture (<paramref name="overtaken"/>). The
    /// marks taken before the read are then cleared, so the owner vector is served again.</summary>
    private RoleRevisionVector ReadVector(long role, bool overtaken)
    {
        long marks = _unrecordedCommits.GetValueOrDefault(role);
        var read = ReadSync<RoleRevisionVector>(new RoleRevisionReadPlan(role));
        if ((overtaken || marks != 0) && _loop?.IsOnLoop == true && _loop.Owners?.Cached(role) is { } owner
            && (overtaken || owner.State is Game.Ownership.RoleOwnerState.Attached or Game.Ownership.RoleOwnerState.Loading)
            && owner.LifecycleEpoch == read.Epoch && !read.Deleted)
        {
            owner.Revisions = read.Parts;
            if (marks != 0) _unrecordedCommits.TryRemove(new KeyValuePair<long, long>(role, marks));
        }
        return read;
    }
    /// <summary>Compatibility path only: direct store methods capture and submit in one call.</summary>
    internal T WriteLegacySync<T>(DbPlan plan)
    {
        bool cached = CaptureLegacy(plan);
        try { return WriteSync<T>(plan); }
        catch (OwnerRevisionConflictException) when (cached ||
            plan is RoleIdentityWritePlan && plan.RoleWrites.Count == 1 &&
            plan.RoleWrites[0].Part == RevisionStore.Position &&
            plan.EntityWrites.Count == 0 && plan.EntityDeltaWrites.Count == 0)
        {
            // The position barrier's fresh SQL capture can be overtaken by an in-flight lazy write during
            // handover. Other fresh plans may contain mixed entity/role state and must keep their conflict.
            var initial = plan.ExpectedRevisions!;
            if (cached) VectorCacheRetries++;
            plan.ResetExpectations();
            CaptureLegacy(plan, fresh: true);
            foreach (var (role, vector) in initial)
                if (!plan.ExpectedRevisions!.TryGetValue(role, out var fresh) || fresh.Deleted ||
                    fresh.Epoch != vector.Epoch)
                    throw;
            return WriteSync<T>(plan);
        }
    }
    internal async Task<T> WriteLegacyAsync<T>(DbPlan plan)
    {
        if (!CaptureLegacy(plan)) return await WriteAsync<T>(plan);
        try { return await WriteAsync<T>(plan); }
        catch (OwnerRevisionConflictException)
        {
            VectorCacheRetries++;
            plan.ResetExpectations();
            CaptureLegacy(plan, fresh: true);
            return await WriteAsync<T>(plan);
        }
    }
    internal long VectorCacheHits, VectorCacheRetries;

    /// <summary>
    /// C/D 16: an attached role's committed vector from its owner, or null (then read it from SQL). A legacy write of
    /// an attached role captures its expected vector here instead of a synchronous SQL read on the loop. The owner
    /// records every commit made on the loop; should the cache ever be behind, the writer refuses the capture and
    /// the legacy write recaptures from SQL and retries once, which is exactly the old read-then-write behaviour
    /// (<see cref="WriteLegacySync"/> also checks the epoch; <see cref="WriteLegacyAsync"/> does not).
    /// </summary>
    private RoleRevisionVector? OwnerVector(long role)
    {
        if (_loop?.IsOnLoop != true || _loop.Owners?.Cached(role) is not { } owner) return null;
        if (_unrecordedCommits.ContainsKey(role)) return null;
        if (owner.State is not (Game.Ownership.RoleOwnerState.Attached or Game.Ownership.RoleOwnerState.Loading)
            || owner.Revisions.Count == 0) return null;
        VectorCacheHits++;
        return new RoleRevisionVector(role, owner.LifecycleEpoch, false, owner.Revisions);
    }

    /// <summary>Returns true when any role vector came from the owner cache.</summary>
    private bool CaptureLegacy(DbPlan plan, bool fresh = false)
    {
        plan.ResolveRoleWrites(this);
        FlushStagedFor(plan);
        bool cached = false;
        if (plan.RolePins.Count != 0)
            plan.Expect(plan.RolePins.Distinct().Select(role =>
            {
                if (!fresh && OwnerVector(role) is { } vector) { cached = true; return vector; }
                return ReadVector(role, overtaken: fresh);
            }).ToArray());
        if (plan.EntityWrites.Count != 0)
            plan.ExpectEntities(plan.EntityWrites.Distinct()
                .Select(key => ReadSync<OwnerRevisionValue>(new OwnerRevisionReadPlan(key))).ToArray());
        return cached;
    }
    private DbPlan Prepare(DbPlan plan)
    {
        plan.ResolveRoleWrites(this);
        FlushStagedFor(plan);
        var roles = plan.RolePins.Concat(plan.RoleWrites.Select(key => key.RoleId)).Distinct().ToArray();
        if (roles.Length != 0 && plan.ExpectedRevisions is null)
            throw new InvalidOperationException($"Role-writing plan {plan.Label} has no captured lifecycle vector.");
        foreach (long role in roles)
            if (!plan.ExpectedRevisions!.ContainsKey(role))
                throw new InvalidOperationException($"Missing captured role {role} for {plan.Label}.");
        var entities = plan.EntityWrites.Distinct().ToArray();
        if (entities.Length != 0 && plan.ExpectedEntities is null)
            throw new InvalidOperationException($"Entity-writing plan {plan.Label} has no captured revision vector.");
        foreach (var key in entities)
            if (!plan.ExpectedEntities!.ContainsKey(key))
                throw new InvalidOperationException($"Missing captured entity {key} for {plan.Label}.");
        if (_loop?.IsOnLoop == true && _loop.Owners?.Current is { } context)
        {
            if (context.RuntimeOnly && (plan.RoleWrites.Count != 0 || plan.EntityWrites.Count != 0 || plan.EntityDeltaWrites.Count != 0)
                && !_loop.Owners!.AuditUncovered($"{context.Origin} runtime writes {plan.Label}"))
                throw new InvalidOperationException($"Runtime intent cannot authorize persistence mutation in {plan.Label}.");
            var coveredByPart = plan.RoleWrites.Select(key => key.RoleId).ToHashSet();
            foreach (var key in plan.RoleWrites.Distinct()) context.AssertPart(key.RoleId, key.Part);
            foreach (long role in roles)
            {
                if (!coveredByPart.Contains(role)) context.AssertRole(role);
                if (context.ExpectedRoleEpoch(role) is long epoch && plan.ExpectedRevisions![role].Epoch != epoch)
                    throw new RolePlanConflictException();
                context.Pin(_loop.Owners!.Get(role));
            }
        }
        return plan;
    }
    /// <summary>Test/diagnostic observer of synchronous reads (plan label, on loop).</summary>
    internal Action<string, bool>? ReadSyncObserved { get; set; }
    internal T ReadSync<T>(DbReadPlan plan)
    {
        ReadSyncObserved?.Invoke(plan.Label, OnLoop);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return Readers.ExecuteLegacySync<T>(plan); }
        finally { AccessMetrics.LegacyWait(started, OnLoop, label: plan.Label); }
    }

    /// <summary>True when the account exists and the password matches (no registration, unlike Login).</summary>
    public bool Verify(string name, string password)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return VerifyAsync(name, password).GetAwaiter().GetResult(); }
        finally { AccessMetrics.LegacyWait(started, OnLoop, label: "account/verify"); }
    }
    public async Task<bool> VerifyAsync(string name, string password) => await VerifiedAccountAsync(name, password).ConfigureAwait(false) != null;

    /// <summary>The account's stored name when the password matches and the account is not banned, else null.</summary>
    public async Task<string?> VerifiedAccountAsync(string name, string password)
    {
        var credentials = await ReadAsync<AccountCredentials?>(new AccountCredentialsRead(name)).ConfigureAwait(false);
        return credentials != null && await MatchesAsync(password, credentials).ConfigureAwait(false)
            ? await UnlessBannedAsync(credentials.Name, "password").ConfigureAwait(false) : null;
    }

    public LoginResult Login(string name, string password)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return LoginAsync(name, password).GetAwaiter().GetResult(); }
        finally { AccessMetrics.LegacyWait(started, OnLoop, label: "account/login"); }
    }
    public async Task<LoginResult> LoginAsync(string name, string password) => (await SignInAsync(name, password).ConfigureAwait(false)).Result;

    /// <summary>
    /// Login or first-time registration. <see cref="SignIn.Account"/> is the account's stored name, which can differ
    /// from <paramref name="name"/> in letter case; tokens and sessions must carry it, never the typed spelling. The ban
    /// is checked only once the password matched, so a wrong password answers the same whether or not the account is
    /// banned; a name that has no account but a ban row is refused before it is registered.
    /// </summary>
    public async Task<SignIn> SignInAsync(string name, string password)
    {
        var signIn = await CheckOrRegisterAsync(name, password).ConfigureAwait(false);
        if (signIn.Result is LoginResult.BadPassword or LoginResult.Banned) return signIn;
        return await UnlessBannedAsync(signIn.Account, "sign-in").ConfigureAwait(false) == null
            ? signIn with { Result = LoginResult.Banned } : signIn;
    }

    private async Task<SignIn> CheckOrRegisterAsync(string name, string password)
    {
        var existing = await ReadAsync<AccountCredentials?>(new AccountCredentialsRead(name)).ConfigureAwait(false);
        if (existing != null) return await Check(existing).ConfigureAwait(false);
        switch (await TryRegisterAsync(name, password).ConfigureAwait(false))
        {
            case Registration.Banned: return new(LoginResult.Banned, name);
            case Registration.Created: return new(LoginResult.Registered, name);
        }
        // A concurrent registration won the unique-name constraint; compare against its committed credentials.
        existing = await ReadAsync<AccountCredentials?>(new AccountCredentialsRead(name)).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Account registration collision did not leave a visible account.");
        return await Check(existing).ConfigureAwait(false);

        async Task<SignIn> Check(AccountCredentials credentials) =>
            new(await MatchesAsync(password, credentials).ConfigureAwait(false) ? LoginResult.Ok : LoginResult.BadPassword, credentials.Name);
    }

    public readonly record struct SignIn(LoginResult Result, string Account);

    public enum Registration { Created, Taken, Banned }

    /// <summary>
    /// Creates the account the game's first login would create for this name and password (the panel's sign-up):
    /// the same row, the same hash, the same ban check. Taken when the name exists in any letter case.
    /// </summary>
    public async Task<Registration> RegisterAsync(string name, string password) =>
        await ReadAsync<AccountCredentials?>(new AccountCredentialsRead(name)).ConfigureAwait(false) != null
            ? Registration.Taken : await TryRegisterAsync(name, password).ConfigureAwait(false);

    private async Task<Registration> TryRegisterAsync(string name, string password)
    {
        // A ban outlives its account (deleting an account keeps its ban row), so a banned name is refused before it is
        // registered again.
        if (await UnlessBannedForRegistrationAsync(name).ConfigureAwait(false) == null) return Registration.Banned;
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = await Task.Run(() => AccountHash.Hash(password, salt)).ConfigureAwait(false);
        return await WriteAsync<bool>(new AccountRegisterPlan(name, hash, salt, DateTime.UtcNow.ToString("O"))).ConfigureAwait(false)
            ? Registration.Created : Registration.Taken;
    }

    /// <summary>
    /// Sets <paramref name="replacement"/> as the password once <paramref name="current"/> matched (a banned account cannot),
    /// and drops the account's login tokens, so a client still holding one signs in again with the new password.
    /// </summary>
    public async Task<bool> ChangePasswordAsync(string name, string current, string replacement)
    {
        if (await VerifiedAccountAsync(name, current).ConfigureAwait(false) is not { } account) return false;
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = await Task.Run(() => AccountHash.Hash(replacement, salt)).ConfigureAwait(false);
        return await WriteAsync<bool>(new AccountPasswordPlan(account, hash, salt)).ConfigureAwait(false);
    }
    private static Task<bool> MatchesAsync(string password, AccountCredentials existing)
        => Task.Run(() => CryptographicOperations.FixedTimeEquals(existing.Hash, AccountHash.Hash(password, existing.Salt)));

    /// <summary>Token returned by /sdk/login, presented as the verifySdk ticket.</summary>
    public string IssueSdkToken(string account) => IssueToken("sdk", account, 24);
    public Task<string> IssueSdkTokenAsync(string account) => IssueTokenAsync("sdk", account, 24);

    public string? ResolveSdkToken(string? token) => ResolveToken("sdk", token);
    public Task<string?> ResolveSdkTokenAsync(string? token) => ResolveTokenAsync("sdk", token);

    /// <summary>Sign of the engine token (MixedLoginToken) handed out by LoginService.verifySdk.</summary>
    public string IssueEngineSign(string account) => IssueToken("engine", account, 16);
    public Task<string> IssueEngineSignAsync(string account) => IssueTokenAsync("engine", account, 16);

    public string? ResolveEngineSign(string? sign) => ResolveToken("engine", sign);
    public Task<string?> ResolveEngineSignAsync(string? sign) => ResolveTokenAsync("engine", sign);

    /// <summary>
    /// Tokens live in the database so a server restart does not invalidate sessions of running clients
    /// (they reconnect with the token they already hold). Valid for a day.
    /// </summary>
    private string IssueToken(string kind, string account, int bytes)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return WriteSync<string>(new AccountTokenPlan(kind, account, token, now, now + 86400));
    }
    private Task<string> IssueTokenAsync(string kind, string account, int bytes)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return WriteAsync<string>(new AccountTokenPlan(kind, account, token, now, now + 86400));
    }

    /// <summary>The token's account, or null when the token is unknown or expired or the account is banned.</summary>
    private string? ResolveToken(string kind, string? token)
        => string.IsNullOrEmpty(token) ? null
            : UnlessBanned(ReadSync<string?>(new AccountTokenRead(kind, token, DateTimeOffset.UtcNow.ToUnixTimeSeconds())), kind + " token");
    private async Task<string?> ResolveTokenAsync(string kind, string? token)
        => string.IsNullOrEmpty(token) ? null
            : await UnlessBannedAsync(await ReadAsync<string?>(new AccountTokenRead(kind, token, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                .ConfigureAwait(false), kind + " token").ConfigureAwait(false);

    public void Dispose()
    {
        void StopWorkers()
        {
            lock (_runtimeGate)
            {
                if (_disposed) return;
                _disposed = true;
                try { if (_writer.IsValueCreated) _writer.Value.Dispose(); }
                finally { if (_readers.IsValueCreated) _readers.Value.Dispose(); }
            }
        }
        if (_loop?.IsWorkerActive == true)
        {
            var debt = _loop.DrainStatus;
            _log?.LogWarning("Database worker shutdown deferred until game loop exits: phase={Phase}, outstanding={Outstanding}, queued={Queued}",
                debt.Phase, debt.Outstanding, debt.Queued);
            _ = _loop.WorkerExited.ContinueWith(_ => StopWorkers(), TaskScheduler.Default);
        }
        else StopWorkers();
    }

    internal Task PersistenceWorkersExited
    {
        get
        {
            lock (_runtimeGate)
                return Task.WhenAll(_writer.IsValueCreated ? _writer.Value.WorkerExited : Task.CompletedTask,
                    _readers.IsValueCreated ? _readers.Value.WorkersExited : Task.CompletedTask);
        }
    }
}
