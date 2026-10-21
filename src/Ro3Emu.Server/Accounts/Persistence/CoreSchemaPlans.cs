using Microsoft.Data.Sqlite;
using static Ro3Emu.Server.Accounts.RoleStore;

namespace Ro3Emu.Server.Accounts.Persistence;

internal sealed class RoleSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "schema/role";
    internal override bool AllowsUnversionedRoleMutation => true;
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS role (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                account         TEXT NOT NULL,
                area            INTEGER NOT NULL,
                name            TEXT NOT NULL UNIQUE COLLATE NOCASE,
                sex             INTEGER NOT NULL,
                career          INTEGER NOT NULL,
                level           INTEGER NOT NULL,
                hair            INTEGER NOT NULL,
                create_time     INTEGER NOT NULL,
                last_login_time INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS role_account ON role(account);
            CREATE TABLE IF NOT EXISTS server_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
        SchemaSql.AddColumn(db, tx, "role", "scene_entry", $"INTEGER NOT NULL DEFAULT {SpawnSceneEntry}");
        SchemaSql.AddColumn(db, tx, "role", "pos_x", $"REAL NOT NULL DEFAULT {SpawnX}");
        SchemaSql.AddColumn(db, tx, "role", "pos_y", "REAL NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "role", "pos_z", $"REAL NOT NULL DEFAULT {SpawnZ}");
        SchemaSql.AddColumn(db, tx, "role", "face", "REAL NOT NULL DEFAULT 0");
        // Serialized romsg.DBSettingInfo (client settings; SETTING_SAVE_SETTING_INFO / AUTO_SYNC 5061).
        SchemaSql.AddColumn(db, tx, "role", "settings", "BLOB");
        // Progression (see RoleProgress); level and career are the columns above.
        SchemaSql.AddColumn(db, tx, "role", "job_level", "INTEGER NOT NULL DEFAULT 1");
        SchemaSql.AddColumn(db, tx, "role", "exp", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "role", "job_exp", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "role", "stats", "TEXT NOT NULL DEFAULT '1,1,1,1,1,1'");
        SchemaSql.AddColumn(db, tx, "role", "bonus_points", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "role", "currency", "TEXT NOT NULL DEFAULT ''");
        SchemaSql.AddColumn(db, tx, "role", "starter_kit", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "role", "skills", "TEXT NOT NULL DEFAULT ''");
        SchemaSql.AddColumn(db, tx, "role", "skill_bar", "TEXT NOT NULL DEFAULT ''");
        // The five Build-window skill selections; progress keeps the selected effective values.
        SchemaSql.AddColumn(db, tx, "role", "skill_plans", "BLOB");
        // Progression (stage 3): see RoleProgress.
        SchemaSql.AddColumn(db, tx, "role", "job_level", "INTEGER NOT NULL DEFAULT 1");
        SchemaSql.AddColumn(db, tx, "role", "exp", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "role", "job_exp", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "role", "stats", "TEXT NOT NULL DEFAULT '1,1,1,1,1,1'");
        SchemaSql.AddColumn(db, tx, "role", "bonus_points", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "role", "currency", "TEXT NOT NULL DEFAULT ''");
        SchemaSql.AddColumn(db, tx, "role", "starter_kit", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "role", "skills", "TEXT NOT NULL DEFAULT ''");
        SchemaSql.AddColumn(db, tx, "role", "skill_bar", "TEXT NOT NULL DEFAULT ''");
        // Serialized romsg.DBTaskInfo (quests, AUTO_SYNC 5027); NULL = the role predates quests.
        SchemaSql.AddColumn(db, tx, "role", "tasks", "BLOB");
        // Server-owned quest action/guide completion boundary, separate from client DBTaskInfo.
        SchemaSql.AddColumn(db, tx, "role", "task_runtime", "BLOB");
        // Natural card-unlock sources awaiting mandatory task settlement after the library commit.
        SchemaSql.AddColumn(db, tx, "role", "task_followup", "BLOB");
        // Serialized romsg.DBGuideInfo (finished tutorial guides, AUTO_SYNC 5055).
        SchemaSql.AddColumn(db, tx, "role", "guides", "BLOB");
        // Serialized romsg.DBEmojiPlayInfo (emote favourites, unlocks, idle emote; AUTO_SYNC 5006).
        SchemaSql.AddColumn(db, tx, "role", "emoji_play", "BLOB");
        // Serialized romsg.DBRoleAchievementInfo (achievement statuses, scores, reward/hint levels; AUTO_SYNC 5019)
        // and its server-only JSON runtime (AchievementService.RuntimeState: the login-day counter's last day).
        SchemaSql.AddColumn(db, tx, "role", "achievements", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "achievement_runtime", "BLOB");
        // Serialized romsg.DBAdventureGroupRelationData (AUTO_SYNC 5068): the role's own Adventure Group values (claimed
        // level rewards, leave cooldown); the group fields are rebuilt from AdventureGroupStore on every sync.
        SchemaSql.AddColumn(db, tx, "role", "adventure_group", "BLOB");
        // Serialized romsg.DBLibraryInfo (card collection, AUTO_SYNC 5057).
        SchemaSql.AddColumn(db, tx, "role", "library", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "card_plans", "BLOB");
        // JSON ShopService.ShopState (purchase counters with their period, favourites; AUTO_SYNC 5024).
        SchemaSql.AddColumn(db, tx, "role", "shop", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "trade_info", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "stalls_info", "BLOB");
        // MailService's durable per-role mailbox (micro list/read/delete and game claim 31602).
        SchemaSql.AddColumn(db, tx, "role", "mailbox", "BLOB");
        // 5063 DBAuction: personal prebuy counters and joined guild history.
        SchemaSql.AddColumn(db, tx, "role", "auction", "BLOB");
        // 5047 personal guild values/prefs; GuildStore.guild_member is the authoritative membership index.
        SchemaSql.AddColumn(db, tx, "role", "guild_info", "BLOB");
        // Guild Challenge personal stage entitlements; the guild and clear journals have separate owners.
        SchemaSql.AddColumn(db, tx, "role", "guild_challenge", "BLOB");
        // Server-owned EXP-book spend ledger for exact 33310 pet rebirth refunds.
        SchemaSql.AddColumn(db, tx, "role", "pet_books", "BLOB");
        // DailyActivityService: 5004 rows, shared 5059 claims and reset epochs.
        SchemaSql.AddColumn(db, tx, "role", "daily", "BLOB");
        // MG_NewActivity personal state (5013) and server-owned rollover/entitlement watermarks.
        SchemaSql.AddColumn(db, tx, "role", "new_activities", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "activity_runtime", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "timed_activity", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "timed_battle", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "training", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "lottery", "BLOB");
        // Serialized romsg.DBClimTower (5065); config ids and floor-number honor watermark are distinct.
        SchemaSql.AddColumn(db, tx, "role", "tower", "BLOB");
        // JSON WildAfkService.State: 5035 plus daily reset and consumed-time metadata.
        SchemaSql.AddColumn(db, tx, "role", "wild_afk", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "dungeons", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "territory_wars", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "territory_escort", "BLOB");
        // Life professions (5010), vigor (5018), and daily consumption/season metadata.
        SchemaSql.AddColumn(db, tx, "role", "lifejob", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "life_restrict", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "life_runtime", "BLOB");
        // Server-wide first-pass records survive restarts. No unproven seasonal reset.
        SchemaSql.Execute(db, tx, "CREATE TABLE IF NOT EXISTS tower_first_pass (floor INTEGER PRIMARY KEY, record BLOB NOT NULL)");
        // Serialized romsg.DBSeasonTalent (AUTO_SYNC 5050); plans survive playbook changes.
        SchemaSql.AddColumn(db, tx, "role", "season_talent", "BLOB");
        // Serialized season page (5022) and equipment-grid season refinement (5023).
        SchemaSql.AddColumn(db, tx, "role", "season_page", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "season_page_meta", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "season_refine", "BLOB");
        // JSON Game.EquipLevels (strengthen / refine / refine luck per equipment grid, AUTO_SYNC 5007).
        SchemaSql.AddColumn(db, tx, "role", "equip_levels", "BLOB");
        // Serialized DBEquipInfo plan fields (5007) and AutoDecomposeSetting (5009).
        SchemaSql.AddColumn(db, tx, "role", "equip_plans", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "auto_decompose", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "bag_state", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "stat_plans", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "world_state", "BLOB");
        // JSON EmblemService.EmblemState (Soul Echo plans, AUTO_SYNC 5005, attained power).
        SchemaSql.AddColumn(db, tx, "role", "emblems", "BLOB");
        // Serialized romsg.DBPetInfo (roster, lineup and incubator; AUTO_SYNC 5017).
        SchemaSql.AddColumn(db, tx, "role", "pets", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "mounts", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "wardrobe", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "totems", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "totem_refine_epoch", "BLOB");
        SchemaSql.AddColumn(db, tx, "role", "genre", "BLOB");
        // Serialized romsg.DBCareerSpecial (leased Special Class Items: pendant and mount slot; AUTO_SYNC 5002).
        SchemaSql.AddColumn(db, tx, "role", "career_special", "BLOB");
        // Serialized romsg.DBOdinStatue (Valhalla Trials Blessing statue; part of AUTO_SYNC 5046, ValhallaService).
        SchemaSql.AddColumn(db, tx, "role", "heroic_spirit", "BLOB");
        // Serialized romsg.DBAreaConcurInfo (Monster Hunt kill counters and claimed stages; AUTO_SYNC 5053).
        SchemaSql.AddColumn(db, tx, "role", "area_concur", "BLOB");
        // Serialized romsg.DBGrowthClassInfo (Growth Classroom claimed rank rewards; AUTO_SYNC 5045, GrowthClassService).
        SchemaSql.AddColumn(db, tx, "role", "growth_class", "BLOB");
        // Serialized romsg.DBFirstHeartTaskActivityData (Seven-Day Goal tasks, score and claims; AUTO_SYNC 5049).
        SchemaSql.AddColumn(db, tx, "role", "first_heart", "BLOB");
        // Serialized romsg.DBPersonalInfo (profile card presets and the current one; AUTO_SYNC 5016, PersonalService).
        SchemaSql.AddColumn(db, tx, "role", "personal", "BLOB");
        // Serialized romsg.PersonalUpvoteList (who liked this role's profile card and when; PersonalService).
        SchemaSql.AddColumn(db, tx, "role", "personal_upvote", "BLOB");
        // Serialized romsg.DBPopularityStar (popularity from received friend gifts, affection from sent ones).
        SchemaSql.AddColumn(db, tx, "role", "popularity", "BLOB");
        // Serialized romsg.DBSocialOutfit (chat cosmetics unlocked by items and the worn ones; AUTO_SYNC 5044, SocialOutfitService).
        SchemaSql.AddColumn(db, tx, "role", "social_outfit", "BLOB");
        // Serialized romsg.DBEmojiInfo (chat emoticons unlocked by items; AUTO_SYNC 5041, EmojiService).
        SchemaSql.AddColumn(db, tx, "role", "emoji_info", "BLOB");
        // Unix seconds of a deletion; deleted roles are kept (items, progress) but no longer listed or selectable.
        SchemaSql.AddColumn(db, tx, "role", "deleted_time", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.Execute(db, tx, """
            CREATE TABLE IF NOT EXISTS role_lifecycle (
                role_id INTEGER PRIMARY KEY,
                epoch INTEGER NOT NULL CHECK(epoch > 0),
                deleted INTEGER NOT NULL CHECK(deleted IN (0,1))
            );
            CREATE TABLE IF NOT EXISTS owner_revision (
                kind TEXT NOT NULL,
                id TEXT NOT NULL,
                part TEXT NOT NULL,
                revision INTEGER NOT NULL CHECK(revision >= 0),
                PRIMARY KEY(kind,id,part)
            );
            INSERT OR IGNORE INTO role_lifecycle(role_id,epoch,deleted)
                SELECT id,1,CASE WHEN deleted_time=0 THEN 0 ELSE 1 END FROM role;
            """);
        // Backfill every existing role before the server admits connections. Each blob is
        // a separate storage part; progress deliberately excludes settings and position.
        foreach (string part in RoleColumns.Blobs.Concat(RevisionStore.AdditionalParts))
        {
            using var revision = DbSql.Command(db, tx, """
                INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'role',id,$part,0 FROM role
                """);
            revision.Parameters.AddWithValue("$part", part);
            revision.ExecuteNonQuery();
        }
        // Role ids show up as uuids in the client; start well above small entity ids.
        SchemaSql.Execute(db, tx, "INSERT OR IGNORE INTO sqlite_sequence (name, seq) SELECT 'role', 100000 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'role')");
        // E01 shares this schema transaction; legacy balances become the opening cut at sequence zero.
        new EconomySchemaPlan().Execute(db, tx);
        return 1;
    }
}

internal sealed class ItemSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "schema/item";
    internal override bool AllowsUnversionedRoleMutation => true;
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();

        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS item (
                guid    INTEGER PRIMARY KEY AUTOINCREMENT,
                role_id INTEGER NOT NULL,
                entry   INTEGER NOT NULL,
                stack   INTEGER NOT NULL,
                pack    INTEGER NOT NULL,
                sort    INTEGER NOT NULL,
                grid    INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS item_role ON item(role_id);
            INSERT OR IGNORE INTO sqlite_sequence (name, seq) SELECT 'item', 1000000 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'item');
            """;
        cmd.ExecuteNonQuery();
        foreach (var column in new[] { "attrs", "enchantments" })
        {
            using var check = db.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('item') WHERE name = $name";
            check.Parameters.AddWithValue("$name", column);
            if ((long)check.ExecuteScalar()! != 0) continue;
            using var alter = db.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = $"ALTER TABLE item ADD COLUMN {column} TEXT";
            alter.ExecuteNonQuery();
        }
        using var lockCheck = db.CreateCommand();
        lockCheck.Transaction = tx;
        lockCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('item') WHERE name = 'is_locked'";
        if ((long)lockCheck.ExecuteScalar()! == 0)
        {
            using var alter = db.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = "ALTER TABLE item ADD COLUMN is_locked INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        SchemaSql.AddColumn(db, tx, "item", "str_attrs", "TEXT");
        using var expireCheck = db.CreateCommand();
        expireCheck.Transaction = tx;
        expireCheck.CommandText = "SELECT COUNT(*) FROM pragma_table_info('item') WHERE name = 'expire_time'";
        bool legacyExpire = (long)expireCheck.ExecuteScalar()! != 0;
        if (SchemaSql.AddColumn(db, tx, "item", "bind_type", "INTEGER NOT NULL DEFAULT 0"))
        {
            // Daily's earlier schema called this field "bind". Migrate it once so existing bound rewards
            // keep their ownership semantics without overwriting later card binding changes on startup.
            using var legacy = db.CreateCommand();
            legacy.Transaction = tx;
            legacy.CommandText = "SELECT COUNT(*) FROM pragma_table_info('item') WHERE name = 'bind'";
            if ((long)legacy.ExecuteScalar()! != 0)
            {
                using var migrate = db.CreateCommand();
                migrate.Transaction = tx;
                migrate.CommandText = "UPDATE item SET bind_type = bind";
                migrate.ExecuteNonQuery();
            }
        }
        if (SchemaSql.AddColumn(db, tx, "item", "bind_expire", "INTEGER NOT NULL DEFAULT 0") && legacyExpire)
        {
            // The quest branch stored ItemBindInfo expiry in expire_time; move it once to its dedicated column.
            using var migrate = db.CreateCommand();
            migrate.Transaction = tx;
            migrate.CommandText = "UPDATE item SET bind_expire = expire_time WHERE expire_time != 0";
            migrate.ExecuteNonQuery();
        }
        return 1;
    }
}

internal sealed class FriendSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "schema/friend";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();

        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS friend (owner INTEGER NOT NULL, other INTEGER NOT NULL, since INTEGER NOT NULL, intimacy INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (owner, other));
            CREATE TABLE IF NOT EXISTS friend_apply (target INTEGER NOT NULL, applicant INTEGER NOT NULL, time INTEGER NOT NULL, PRIMARY KEY (target, applicant));
            CREATE TABLE IF NOT EXISTS blacklist (owner INTEGER NOT NULL, other INTEGER NOT NULL, PRIMARY KEY (owner, other));
            """;
        cmd.ExecuteNonQuery();
        EnsureExtra(db, tx);
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'friend_group',id,'state',0 FROM friend_chat_group
            """);
        return 1;
    }

    private static void EnsureExtra(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS friend_metadata (role INTEGER PRIMARY KEY, data BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS friend_chat_group (id INTEGER PRIMARY KEY AUTOINCREMENT, owner INTEGER NOT NULL, name TEXT NOT NULL, created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS friend_chat_member (group_id INTEGER NOT NULL, role INTEGER NOT NULL, dnd INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (group_id, role), FOREIGN KEY (group_id) REFERENCES friend_chat_group(id));
            CREATE TABLE IF NOT EXISTS recent_team_peer (owner INTEGER NOT NULL, other INTEGER NOT NULL, last_seen INTEGER NOT NULL,
              PRIMARY KEY(owner, other));
            """;
        cmd.ExecuteNonQuery();
    }
}

internal sealed class AuctionSchemaPlan : DbPlan
{
    internal override int Bytes => 128;
    internal override string Label => "schema/auction";
    internal override object Execute(SqliteConnection db, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();

        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS auction_item (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_key TEXT NOT NULL UNIQUE,
                state BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS auction_run (
                id INTEGER PRIMARY KEY,
                state BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS auction_bonus (
                role_id INTEGER NOT NULL,
                group_key TEXT NOT NULL,
                amount INTEGER NOT NULL,
                claimed INTEGER NOT NULL DEFAULT 0,
                guild_id INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(role_id, group_key)
            );
            CREATE TABLE IF NOT EXISTS auction_source (
                source_key TEXT PRIMARY KEY
            );
            CREATE TABLE IF NOT EXISTS auction_prebuy (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                role_id INTEGER NOT NULL,
                auction_type INTEGER NOT NULL,
                src_type INTEGER NOT NULL,
                item_id INTEGER NOT NULL,
                quantity INTEGER NOT NULL,
                unit_price INTEGER NOT NULL,
                currency INTEGER NOT NULL,
                revision INTEGER NOT NULL DEFAULT 0,
                processed INTEGER NOT NULL DEFAULT 0,
                requested_id INTEGER NOT NULL DEFAULT 0,
                paused INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS auction_prebuy_role ON auction_prebuy(role_id);
            """;
        cmd.ExecuteNonQuery();
        SchemaSql.AddColumn(db, tx, "auction_prebuy", "requested_id", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.AddColumn(db, tx, "auction_prebuy", "paused", "INTEGER NOT NULL DEFAULT 0");
        SchemaSql.Execute(db, tx, """
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'auction_item',source_key,'state',0 FROM auction_item;
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'auction_run',id,'state',0 FROM auction_run;
            INSERT OR IGNORE INTO owner_revision(kind,id,part,revision)
                SELECT 'auction_prebuy',id,'state',0 FROM auction_prebuy;
            """);
        return 1;
    }
}

internal static class SchemaSql
{
    internal static void Execute(SqliteConnection db, SqliteTransaction tx, string sql)
    { using var cmd = DbSql.Command(db, tx, sql); cmd.ExecuteNonQuery(); }

    internal static bool AddColumn(SqliteConnection db, SqliteTransaction tx, string table, string name, string definition)
    {
        using var check = DbSql.Command(db, tx, "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name=$name");
        check.Parameters.AddWithValue("$table", table); check.Parameters.AddWithValue("$name", name);
        if ((long)check.ExecuteScalar()! != 0) return false;
        Execute(db, tx, $"ALTER TABLE {table} ADD COLUMN {name} {definition}");
        return true;
    }
}
