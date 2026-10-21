using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ro3Emu.Server.Economy;

/// <summary>Independent, read-only baseline decoder for the E00 persisted compartments.</summary>
public static partial class EconomySnapshotReader
{
    public const string CoverageVersion = "e03-e05e-reader-1";
    private static readonly string[] RequiredTables = ["role", "item", "market_record", "auction_item",
        "auction_prebuy", "auction_bonus", "auction_run", "auction_source", "guild", "guild_market",
        "guild_caravan_member", "guild_caravan_version", "guild_campfire", "guild_recovery", "territory_wars",
        "territory_wars_seasons", "territory_escort", "festival_reward", "guild_challenge_clear",
        "dungeon_reward_obligation"];

    // Every persisted role BLOB must be classified. Presence of a new BLOB column is never silently ignored.
    private static readonly Dictionary<string, BlobFormat> RoleBlobs = new(StringComparer.Ordinal)
    {
        ["settings"] = BlobFormat.Protobuf, ["skill_plans"] = BlobFormat.Protobuf,
        ["tasks"] = BlobFormat.Protobuf, ["task_runtime"] = BlobFormat.Json,
        ["task_followup"] = BlobFormat.StringSet,
        ["guides"] = BlobFormat.Protobuf, ["emoji_play"] = BlobFormat.Protobuf,
        ["achievements"] = BlobFormat.Protobuf, ["achievement_runtime"] = BlobFormat.Json,
        ["adventure_group"] = BlobFormat.Protobuf, ["library"] = BlobFormat.Protobuf,
        ["card_plans"] = BlobFormat.Protobuf, ["shop"] = BlobFormat.Json,
        ["trade_info"] = BlobFormat.Protobuf, ["stalls_info"] = BlobFormat.Protobuf,
        ["mailbox"] = BlobFormat.Mailbox, ["auction"] = BlobFormat.Protobuf,
        ["guild_info"] = BlobFormat.Protobuf, ["guild_challenge"] = BlobFormat.Json,
        ["pet_books"] = BlobFormat.Json, ["daily"] = BlobFormat.Json,
        ["new_activities"] = BlobFormat.Protobuf, ["activity_runtime"] = BlobFormat.Json,
        ["timed_activity"] = BlobFormat.Json, ["timed_battle"] = BlobFormat.Json,
        ["training"] = BlobFormat.Protobuf, ["lottery"] = BlobFormat.Json,
        ["tower"] = BlobFormat.Protobuf, ["wild_afk"] = BlobFormat.Json,
        ["dungeons"] = BlobFormat.Json, ["territory_wars"] = BlobFormat.TerritoryRole,
        ["territory_escort"] = BlobFormat.Json, ["lifejob"] = BlobFormat.Protobuf,
        ["life_restrict"] = BlobFormat.Protobuf, ["life_runtime"] = BlobFormat.Json,
        ["season_talent"] = BlobFormat.Protobuf, ["season_page"] = BlobFormat.Protobuf,
        ["season_page_meta"] = BlobFormat.Json, ["season_refine"] = BlobFormat.Protobuf,
        ["equip_levels"] = BlobFormat.Json, ["equip_plans"] = BlobFormat.Protobuf,
        ["auto_decompose"] = BlobFormat.Protobuf, ["bag_state"] = BlobFormat.Protobuf,
        ["stat_plans"] = BlobFormat.Protobuf, ["world_state"] = BlobFormat.Json,
        ["emblems"] = BlobFormat.Json, ["pets"] = BlobFormat.Protobuf,
        ["mounts"] = BlobFormat.Json, ["wardrobe"] = BlobFormat.Protobuf,
        ["totems"] = BlobFormat.Protobuf, ["totem_refine_epoch"] = BlobFormat.RawEpoch,
        ["genre"] = BlobFormat.Protobuf, ["career_special"] = BlobFormat.Protobuf, ["heroic_spirit"] = BlobFormat.Protobuf, ["area_concur"] = BlobFormat.Protobuf,
        ["growth_class"] = BlobFormat.Protobuf, ["first_heart"] = BlobFormat.Protobuf,
        ["personal"] = BlobFormat.Protobuf, ["personal_upvote"] = BlobFormat.Protobuf, ["popularity"] = BlobFormat.Protobuf,
        ["social_outfit"] = BlobFormat.Protobuf, ["emoji_info"] = BlobFormat.Protobuf
    };

    private enum BlobFormat { Json, Protobuf, Mailbox, TerritoryRole, RawEpoch, StringSet }
    private sealed class UnknownPersistedFieldException(string message) : Exception(message);

    /// <summary>Remaining unqualified families. E05e event envelopes do not imply whole-realm coverage.</summary>
    public static IReadOnlyList<string> UncoveredFamilies { get; } = [
        "role/item identity, reset, migration and snapshot replacement", "character exchange, direct grants and GM",
        "monster drops, gathering, static pickup and world interaction", "shop, card, equipment, appraisal and enchant",
        "pet, mount, wardrobe, library, skill, talent, refine and life", "daily, task and activity families outside C/D13f",
        "source-owned financial mail in unconverted parent families", "dungeon parent auction clear journal",
        "guild treasury, membership and campfire outside converted commands",
        "guild caravan and guild challenge", "earned obligations outside C/D13f"
    ];

    public static EconomySnapshot Read(string databasePath, EconomyCatalog catalog, string realmId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrWhiteSpace(realmId)) throw new ArgumentException("Realm id is required.", nameof(realmId));
        if (!File.Exists(databasePath)) throw new FileNotFoundException("Economy database does not exist.", databasePath);
        var timer = Stopwatch.StartNew();
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        db.Open();
        using (var only = db.CreateCommand()) { only.CommandText = "PRAGMA query_only=ON"; only.ExecuteNonQuery(); }
        using var tx = db.BeginTransaction(deferred: true);
        // The first read (this schema check) pins the WAL view; the persisted watermark is read from that same view.
        // A legacy baseline without E01 has no cut, and none is fabricated.
        string? persistedRealm = null;
        long? commitSequence = null;
        if (EconomySchema.Has(db, tx, "economy_meta"))
        {
            using var cutCommand = db.CreateCommand();
            cutCommand.Transaction = tx;
            cutCommand.CommandText = "SELECT realm_id,next_sequence FROM economy_meta WHERE singleton=1";
            using var cutRow = cutCommand.ExecuteReader();
            if (cutRow.Read()) { persistedRealm = cutRow.GetString(0); commitSequence = cutRow.GetInt64(1); }
        }
        var schema = new List<(string Name, string Sql)>();
        using (var metadata = db.CreateCommand())
        {
            metadata.Transaction = tx;
            metadata.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var r = metadata.ExecuteReader();
            while (r.Read()) schema.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        }
        string schemaVersion = Hash(string.Join("\n", schema.Select(x => x.Name + ":" + x.Sql)));
        var state = new State(db, tx, catalog, schema.Select(x => x.Name).ToHashSet(StringComparer.Ordinal));
        if (persistedRealm is null && state.Tables.Contains("economy_meta"))
            state.Issue(EconomyIssueKind.Corruption, "cut/economy_meta", "Economy metadata singleton is missing.");
        if (persistedRealm is not null && realmId != "auto" && realmId != persistedRealm)
            state.Issue(EconomyIssueKind.Corruption, "cut/realm", "Expected realm ID differs from durable database identity.");
        if (persistedRealm is null && realmId == "auto")
            state.Issue(EconomyIssueKind.Coverage, "cut/realm", "Legacy database has no durable realm identity.");
        if (FormatManifestHash != ExpectedFormatManifestHash)
            state.Issue(EconomyIssueKind.UnknownSchema, "schema/persisted-formats",
                "Persisted DTO/protobuf shape changed since its economic classification: " + FormatManifestHash);
        foreach (string table in RequiredTables)
            if (!state.Tables.Contains(table)) state.Issue(EconomyIssueKind.Coverage, "schema/" + table, "Required persisted compartment table is missing.");
        state.CheckSchema();
        state.ReadRoles();
        var progress = ReadProgress(db, tx, state.Tables, state.Issues);
        state.ReadDungeonPending();
        state.ReadItems();
        state.ReadMarkets();
        state.ReadAuctions();
        state.ReadGuilds();
        state.ReadTerritory();
        state.ReadObligations();
        var progressRevisions = ReadProgressRevisions(db, tx, state.Tables, state.Issues);
        tx.Commit();
        timer.Stop();
        var cut = new EconomyCut(persistedRealm ?? realmId, schemaVersion, catalog.Version, CoverageVersion,
            commitSequence, DateTimeOffset.UtcNow);
        return new EconomySnapshot(cut, state.Holdings, state.Issues, UncoveredFamilies,
            state.Metrics.Select(m => new EconomyDecodeMetric(m.Key, m.Value.Rows, m.Value.Blobs, m.Value.Bytes)).ToArray(), timer.Elapsed.TotalMilliseconds)
        { Progress = progress, ProgressRevisions = progressRevisions };
    }

    private static IReadOnlyDictionary<long, long> ReadProgressRevisions(SqliteConnection db, SqliteTransaction tx,
        HashSet<string> tables, List<EconomyIssue> issues)
    {
        var result = new Dictionary<long, long>();
        if (!tables.Contains("owner_revision"))
        {
            issues.Add(new(EconomyIssueKind.Coverage, "progress/revision", "Role progress revision table is missing."));
            return result;
        }
        if (!EconomySchema.Has(db, tx, "owner_revision", "id", "revision", "kind", "part"))
        {
            issues.Add(new(EconomyIssueKind.UnknownSchema, "progress/revision", "Role progress revision columns are missing."));
            return result;
        }
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT id,revision FROM owner_revision WHERE kind='role' AND part='progress'";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            if (!long.TryParse(rows.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture, out long role) ||
                role <= 0 || rows.GetInt64(1) < 0 || !result.TryAdd(role, rows.GetInt64(1)))
                issues.Add(new(EconomyIssueKind.Corruption, "progress/revision", "Invalid or duplicate role progress revision."));
        }
        return result;
    }

    private static IReadOnlyDictionary<long, EconomyProgress> ReadProgress(SqliteConnection db, SqliteTransaction tx,
        HashSet<string> tables, List<EconomyIssue> issues)
    {
        var result = new Dictionary<long, EconomyProgress>();
        if (!tables.Contains("role")) return result;
        if (!EconomySchema.Has(db, tx, "role", "id", "level", "exp", "job_level", "job_exp"))
        {
            issues.Add(new(EconomyIssueKind.UnknownSchema, "progress/schema", "Role progression columns are missing."));
            return result;
        }
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT id,level,exp,job_level,job_exp FROM role ORDER BY id";
        using var row = command.ExecuteReader();
        while (row.Read())
        {
            long role = row.GetInt64(0);
            var value = new EconomyProgress(row.GetInt32(1), row.GetInt64(2), row.GetInt32(3), row.GetInt64(4));
            if (role <= 0 || value.BaseLevel <= 0 || value.JobLevel <= 0 || value.BaseExp < 0 || value.JobExp < 0 ||
                !result.TryAdd(role, value))
                issues.Add(new(EconomyIssueKind.Corruption, "progress/" + role, "Invalid or duplicate role progression."));
        }
        return result;
    }

    /// <summary>Read the same persisted compartments inside the writer's transaction for E05d delta verification.</summary>
    internal static (IReadOnlyList<EconomyHolding> Holdings, IReadOnlyList<EconomyIssue> Issues) ReadTransaction(
        SqliteConnection db, SqliteTransaction tx, EconomyCatalog catalog)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        using (var command = db.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var rows = command.ExecuteReader();
            while (rows.Read()) tables.Add(rows.GetString(0));
        }
        var state = new State(db, tx, catalog, tables);
        state.ReadRoles();
        state.ReadDungeonPending();
        state.ReadItems();
        state.ReadMarkets();
        state.ReadAuctions();
        state.ReadGuilds();
        state.ReadTerritory();
        state.ReadObligations();
        return (state.Holdings, state.Issues);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed partial class State(SqliteConnection db, SqliteTransaction tx, EconomyCatalog catalog, HashSet<string> tables)
    {
        internal HashSet<string> Tables => tables;
        internal List<EconomyHolding> Holdings { get; } = [];
        internal List<EconomyIssue> Issues { get; } = [];
        internal Dictionary<string, (long Rows, long Blobs, long Bytes)> Metrics { get; } = new(StringComparer.Ordinal);
        private readonly HashSet<long> _roles = [];
        private readonly HashSet<long> _deletedRoles = [];
        private readonly HashSet<long> _unknownRoleDeletion = [];
        private readonly HashSet<long> _deletionRecoveryNoted = [];
        private readonly HashSet<long> _guilds = [];
        private readonly HashSet<long> _recoveredGuilds = [];
        private readonly HashSet<string> _mailDeliveryKeys = [];

        internal void Issue(EconomyIssueKind kind, string key, string detail) => Issues.Add(new(kind, key, detail));
        internal void ReadDungeonPending()
        {
            if (!Has("dungeon_reward_obligation")) return;
            Guard("dungeon/pending", () =>
            {
                foreach (var holding in EconomyDungeonPending.Read(db, tx))
                {
                    string key = holding.Location.Key;
                    if (!catalog.TryAsset(holding.Asset.Id, out var classified) || classified != holding.Asset)
                    {
                        Issue(EconomyIssueKind.Coverage, key, "Frozen dungeon denomination is not catalogued.");
                        continue;
                    }
                    long role = long.Parse(holding.Location.Owner.Id, CultureInfo.InvariantCulture);
                    Hold(holding.Asset, Role(role, key), EconomyCompartment.Pending, key,
                        holding.Quantity, holding.Fingerprint);
                }
            });
        }
        // Bytes is the exact UTF-8/BLOB payload bytes read, excluding SQLite's internal scalar storage.
        private void Measure(string repo, long bytes, bool blob = false)
        {
            var m = Metrics.GetValueOrDefault(repo);
            Metrics[repo] = (m.Rows + 1, m.Blobs + (blob ? 1 : 0), checked(m.Bytes + bytes));
        }
        private void PayloadBytes(string repo, long bytes)
        {
            var m = Metrics.GetValueOrDefault(repo);
            Metrics[repo] = (m.Rows, m.Blobs, checked(m.Bytes + bytes));
        }
        private SqliteCommand Command(string sql) { var c = db.CreateCommand(); c.Transaction = tx; c.CommandText = sql; return c; }
        private static long SqlLong(SqliteDataReader row, int index)
            => row.GetValue(index) is long value ? value : throw new InvalidCastException("Expected persisted INTEGER column.");
        private static string SqlText(SqliteDataReader row, int index)
            => row.GetValue(index) is string value ? value : throw new InvalidCastException("Expected persisted TEXT column.");
        private static byte[] SqlBlob(SqliteDataReader row, int index)
            => row.GetValue(index) is byte[] value ? value : throw new InvalidCastException("Expected persisted BLOB column.");
        private bool Has(string table) => tables.Contains(table);
        private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
        private EconomyOwner Role(long role, string key)
        {
            if (_roles.Contains(role))
            {
                if (_deletedRoles.Contains(role) || _unknownRoleDeletion.Contains(role))
                {
                    if (_deletedRoles.Contains(role) && _deletionRecoveryNoted.Add(role))
                        Issue(EconomyIssueKind.RecoveryDebt, "role/" + Id(role) + "/deleted_time",
                            "Soft-deleted role retains assets; recovery ownership requires policy.");
                    return new(EconomyOwnerKind.Recovery, "role:" + Id(role));
                }
                return new(EconomyOwnerKind.Role, Id(role));
            }
            Issue(EconomyIssueKind.RecoveryDebt, key, $"Role {role} has persisted assets but no role row.");
            return new(EconomyOwnerKind.Recovery, Id(role));
        }
        private EconomyOwner Guild(long guild, string key)
        {
            if (_guilds.Contains(guild) || _recoveredGuilds.Contains(guild)) return new(EconomyOwnerKind.Guild, Id(guild));
            Issue(EconomyIssueKind.RecoveryDebt, key, $"Guild {guild} has persisted assets but no guild row.");
            return new(EconomyOwnerKind.Recovery, "guild:" + Id(guild));
        }
        private void Hold(EconomyAsset asset, EconomyOwner owner, EconomyCompartment part, string key,
            long quantity, string fingerprint = "", string epoch = "", string instance = "")
        {
            if (quantity <= 0) { Issue(EconomyIssueKind.Corruption, key, "Held quantity must be positive."); return; }
            // Ordinary catalogue items/currencies and guild resources keep their denomination
            // across pickup, refund and delivery. Their source epoch lives in the location key.
            Holdings.Add(new(asset with { Epoch = asset.Kind == EconomyAssetKind.EventResource ? epoch : "" },
                new(owner, part, key, instance), quantity, fingerprint));
        }
        private void Entry(long entry, long count, EconomyOwner owner, EconomyCompartment part, string key,
            string fingerprint = "", string epoch = "", string instance = "")
        {
            if (count <= 0) { Issue(EconomyIssueKind.Corruption, key, "Concrete grant/stack quantity must be positive."); return; }
            if (entry is 100 or 101) { Issue(EconomyIssueKind.RecoveryDebt, key, "EXP grant is an entitlement, not an item balance."); return; }
            if (!catalog.TryAsset(entry, out var asset)) { Issue(EconomyIssueKind.Coverage, key, $"ItemConfig entry {entry} is not catalogued."); return; }
            Hold(asset, owner, part, key, count, asset.Kind == EconomyAssetKind.Item ? fingerprint : "", epoch, instance);
        }
        private void Currency(long entry, long count, EconomyOwner owner, EconomyCompartment part, string key, string epoch = "")
        {
            if (count < 0) { Issue(EconomyIssueKind.Corruption, key, "Negative currency balance."); return; }
            if (entry is 100 or 101)
            { Issue(EconomyIssueKind.Corruption, key, "EXP pseudo-entry is not a spendable currency purse."); return; }
            if (!catalog.TryAsset(entry, out var asset) || asset.Kind != EconomyAssetKind.Currency)
            { Issue(EconomyIssueKind.Coverage, key, $"Currency {entry} is not catalogued as currency."); return; }
            if (count > 0) Hold(asset, owner, part, key, count, epoch: epoch);
        }
        private void Resource(EconomyAssetKind kind, long id, long count, EconomyOwner owner,
            EconomyCompartment part, string key, string epoch = "") => Hold(new(kind, id, epoch), owner, part, key, count, epoch: epoch);
        private void Guard(string key, Action decode)
        {
            try { decode(); }
            catch (UnknownPersistedFieldException e) { Issue(EconomyIssueKind.UnknownSchema, key, e.Message); }
            catch (Exception e) when (e is JsonException or FormatException or InvalidDataException or OverflowException or InvalidOperationException
                or ArgumentException or SqliteException or IndexOutOfRangeException or InvalidCastException
                or DecoderFallbackException)
            { Issue(EconomyIssueKind.Corruption, key, e.GetType().Name + ": " + e.Message); }
        }
        private static JsonDocument Json(byte[] data) => CheckedJson(JsonDocument.Parse(data));
        private static JsonDocument Json(string data) => CheckedJson(JsonDocument.Parse(data));
        private static JsonDocument CheckedJson(JsonDocument document)
        {
            try { CheckDuplicates(document.RootElement, 0); return document; }
            catch { document.Dispose(); throw; }
        }
        private static void CheckDuplicates(JsonElement value, int depth)
        {
            if (depth > 64) throw new JsonException("Persisted JSON nesting exceeds decoder bound.");
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in value.EnumerateObject())
                {
                    if (!names.Add(p.Name)) throw new JsonException("Duplicate persisted JSON field " + p.Name);
                    CheckDuplicates(p.Value, depth + 1);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var e in value.EnumerateArray()) CheckDuplicates(e, depth + 1);
        }
        private static JsonElement Prop(JsonElement value, string name) => value.GetProperty(name);
        private static bool Try(JsonElement value, string name, out JsonElement result)
        {
            result = default;
            return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out result);
        }
        private static long Long(JsonElement value, string name, long fallback = 0)
            => Try(value, name, out var p) ? p.GetInt64() : fallback;
        private static string String(JsonElement value, string name, string fallback = "")
            => Try(value, name, out var p) ? p.GetString() ?? fallback : fallback;
        private static bool Bool(JsonElement value, string name, bool fallback = false)
            => Try(value, name, out var p) ? p.GetBoolean() : fallback;
        private static IEnumerable<JsonProperty> Properties(JsonElement value)
            => value.ValueKind == JsonValueKind.Object ? value.EnumerateObject() : throw new JsonException("Expected JSON object.");
        private static IEnumerable<JsonElement> Elements(JsonElement value)
            => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw new JsonException("Expected JSON array.");
        private static void Fields(JsonElement value, string allowed)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected JSON object.");
            var names = allowed.Split(' ').ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject())
            {
                if (!seen.Add(p.Name)) throw new JsonException("Duplicate persisted JSON field " + p.Name);
                if (!names.Contains(p.Name)) throw new UnknownPersistedFieldException("Unclassified JSON field " + p.Name);
            }
        }
        private static string Fingerprint(JsonElement item)
        {
            // Canonical semantic item state, independent of source JSON property order and excluding stack/location.
            return Hash($"Attrs={OptionalObject(item, "Attrs")}|StringAttrs={OptionalObject(item, "StringAttrs")}" +
                $"|Enchantments={OptionalObject(item, "Enchantments")}|BindType={Long(item, "BindType")}" +
                $"|BindExpire={Long(item, "BindExpire", Long(item, "ExpireTimeStamp"))}|IsLocked={(Bool(item, "IsLocked") ? 1 : 0)}");
        }
        private static string GrantFingerprint(long bindType, long bindExpire = 0)
        {
            if (bindType < 0 || bindExpire < 0) throw new FormatException("Invalid grant binding or expiry.");
            return Hash($"Attrs={{}}|StringAttrs={{}}|Enchantments={{}}|BindType={bindType}|BindExpire={bindExpire}|IsLocked=0");
        }
        private static string OptionalObject(JsonElement item, string name)
            => Try(item, name, out var value) && value.ValueKind != JsonValueKind.Null ? Canon(value) : "{}";
        private static string Canon(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canon(p.Value))) + "}",
            JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Canon)) + "]",
            _ => e.GetRawText()
        };
        private void Item(JsonElement item, EconomyOwner owner, EconomyCompartment part, string key, string epoch = "")
        {
            Fields(item, "Guid Entry Stack Pack Sort Grid IsLocked Attrs StringAttrs BindType BindExpire ExpireTimeStamp Enchantments");
            long entry = Long(item, "Entry"), stack = Long(item, "Stack");
            if (entry <= 0) throw new JsonException("Item entry is absent or invalid.");
            if (!Try(item, "Stack", out _) || (Try(item, "BindExpire", out _) && Try(item, "ExpireTimeStamp", out _)
                && Long(item, "BindExpire") != Long(item, "ExpireTimeStamp")))
                throw new JsonException("Missing item stack or conflicting expiry aliases.");
            if (!catalog.TryAsset(entry, out var asset) || asset.Kind != EconomyAssetKind.Item)
            { Issue(EconomyIssueKind.Coverage, key, "Item instance entry is not catalogued as item."); return; }
            Entry(entry, stack, owner, part, key, Fingerprint(item), epoch,
                Long(item, "Guid") > 0 ? Long(item, "Guid").ToString(CultureInfo.InvariantCulture) : "");
        }
        internal void CheckSchema()
        {
            CheckAllColumns();
            if (Has("role"))
            {
                var cols = Columns("role");
                foreach (var needed in new[] { "id", "deleted_time", "currency", "mailbox", "territory_wars" })
                    if (!cols.ContainsKey(needed)) Issue(EconomyIssueKind.Coverage, "schema/role." + needed, "Required role column is missing.");
                foreach (var col in cols)
                    if (col.Value.Contains("BLOB", StringComparison.OrdinalIgnoreCase) && !RoleBlobs.ContainsKey(col.Key))
                        Issue(EconomyIssueKind.UnknownSchema, "schema/role." + col.Key, "Unclassified role BLOB column.");
                foreach (var known in RoleBlobs.Keys)
                    if (!cols.ContainsKey(known)) Issue(EconomyIssueKind.Coverage, "schema/role." + known, "Expected role BLOB column is missing.");
            }
            foreach (var pair in new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["item"] = ["guid", "role_id", "entry", "stack", "pack", "sort", "grid", "attrs", "enchantments", "str_attrs", "bind_type", "bind_expire", "is_locked"],
                ["market_record"] = ["kind", "id", "data"], ["auction_item"] = ["id", "source_key", "state"],
                ["auction_prebuy"] = ["id", "role_id", "quantity", "unit_price", "currency", "processed", "paused"],
                ["auction_bonus"] = ["role_id", "group_key", "amount", "claimed", "guild_id"],
                ["guild"] = ["id", "state"], ["guild_market"] = ["id", "state"],
                ["guild_caravan_member"] = ["role_id", "state"],
                ["territory_wars"] = ["id", "state"], ["territory_wars_seasons"] = ["epoch", "state"],
                ["territory_escort"] = ["id", "state"]
            })
                if (Has(pair.Key))
                {
                    var cols = Columns(pair.Key);
                    foreach (string c in pair.Value) if (!cols.ContainsKey(c))
                        Issue(EconomyIssueKind.Coverage, "schema/" + pair.Key + "." + c, "Required column is missing.");
                }
        }
        private Dictionary<string, string> Columns(string table)
        {
            using var cmd = Command("PRAGMA table_info(\"" + table + "\")");
            using var r = cmd.ExecuteReader();
            var cols = new Dictionary<string, string>(StringComparer.Ordinal);
            while (r.Read()) cols.Add(r.GetString(1), r.GetString(2));
            return cols;
        }
    }
}
