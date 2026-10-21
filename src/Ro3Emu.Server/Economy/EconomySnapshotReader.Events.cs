using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Economy;

public static partial class EconomySnapshotReader
{
    /// <summary>E05e reads only captured participants on the writer's existing transaction, using the audit decoders.</summary>
    internal static Dictionary<(EconomyAsset Asset, EconomyLocation Location), EconomyHolding> ReadEventHoldings(
        SqliteConnection db, SqliteTransaction tx, EconomyCatalog catalog, IReadOnlyList<RoleRevisionKey> roleParts,
        IReadOnlyList<OwnerRevisionKey> entities)
    {
        var state = new State(db, tx, catalog, []);
        state.ReadEventParticipants(roleParts, entities);
        if (state.Issues.Count != 0)
            throw new InvalidDataException("Event holdings cannot be accounted: " +
                string.Join("; ", state.Issues.Select(issue => issue.Key + ": " + issue.Detail)));
        return state.Holdings.ToDictionary(h => (h.Asset, h.Location));
    }

    private sealed partial class State
    {
        internal void ReadEventParticipants(IReadOnlyList<RoleRevisionKey> roleParts, IReadOnlyList<OwnerRevisionKey> entities)
        {
            // Identity is not a holding; load it independently so realm blobs can identify offline guild beneficiaries.
            if (entities.Any(e => e.Kind is "guild" or "realm" or "territory_season"))
            {
                using var identities = Command("SELECT id FROM guild");
                using var row = identities.ExecuteReader();
                while (row.Read()) _guilds.Add(row.GetInt64(0));
            }
            foreach (var group in roleParts.GroupBy(p => p.RoleId))
            {
                long role = group.Key;
                _roles.Add(role);
                using (var identity = Command("SELECT deleted_time FROM role WHERE id=$role"))
                {
                    identity.Parameters.AddWithValue("$role", role);
                    if (identity.ExecuteScalar() is not long deleted || deleted != 0)
                        throw new InvalidDataException("Missing/deleted event beneficiary " + role);
                }
                Holdings.AddRange(EconomyRoleAssetRead.Read(db, tx, role).Values);
                foreach (string column in group.Select(p => p.Part).Distinct().Where(p => p is "mailbox" or "territory_wars"))
                {
                    using var read = Command($"SELECT {column} FROM role WHERE id=$role");
                    read.Parameters.AddWithValue("$role", role);
                    if (read.ExecuteScalar() is byte[] bytes && bytes.Length != 0)
                        Guard($"role/{role}/{column}", () => DecodeRoleBlob(column, bytes, role, $"role/{role}/{column}"));
                }
                if (group.Any(p => p.Part == RevisionStore.FestivalReward)) ReadEventFestival(role);
                if (group.Any(p => p.Part == RevisionStore.MvpCounter) || entities.Any(e => e.Kind == "mvp_obligation")) ReadEventMvp(role);
                if (entities.Any(e => e.Kind == "timed_battle_obligation")) ReadEventTimedBattle(role);
            }
            foreach (long guild in entities.Where(e => e.Kind == "guild").Select(e => long.Parse(e.Id)).Distinct())
            {
                using var read = Command("SELECT state FROM guild WHERE id=$guild");
                read.Parameters.AddWithValue("$guild", guild);
                if (read.ExecuteScalar() is not byte[] bytes) throw new InvalidDataException("Missing event guild " + guild);
                using var doc = Json(bytes);
                var value = doc.RootElement;
                foreach (var field in new[] { ("Money", 1L), ("Contribute", 2L), ("Materials", 3L),
                    ("Reputation", 4L), ("AuctionMaterials", 5L) })
                {
                    long amount = Long(value, field.Item1);
                    if (amount < 0) throw new InvalidDataException("Negative guild event purse.");
                    if (amount > 0) Resource(EconomyAssetKind.GuildResource, field.Item2, amount,
                        Guild(guild, "guild/" + guild), EconomyCompartment.GuildTreasury, $"guild/{guild}/{field.Item1}");
                }
            }
            bool wars = entities.Any(e => e.Kind == "realm" && e.Id == "TerritoryWars" || e.Kind == "territory_season");
            if (wars)
            {
                ReadEventRealm("territory_wars", (key, value) => DecodeWarsWorld(value, key, false));
                using var archives = Command("SELECT epoch,state FROM territory_wars_seasons ORDER BY epoch");
                using var row = archives.ExecuteReader();
                while (row.Read())
                {
                    string epoch = row.GetString(0);
                    using var doc = Json((byte[])row.GetValue(1));
                    DecodeWarsWorld(doc.RootElement, "territory_wars_seasons/" + epoch, true);
                }
            }
            if (entities.Any(e => e.Kind == "realm" && e.Id == "TerritoryEscort"))
                ReadEventRealm("territory_escort", DecodeEscort);
        }

        private void ReadEventRealm(string table, Action<string, System.Text.Json.JsonElement> decode)
        {
            using var read = Command($"SELECT state FROM {table} WHERE id=1");
            if (read.ExecuteScalar() is not byte[] bytes) return;
            using var doc = Json(bytes);
            decode(table + "/1", doc.RootElement);
        }

        private void ReadEventFestival(long role)
        {
            using var read = Command("SELECT cycle,source,award,grants FROM festival_reward WHERE role_id=$role AND claimed=0");
            read.Parameters.AddWithValue("$role", role);
            using var row = read.ExecuteReader();
            while (row.Read())
            {
                string key = $"festival_reward/{row.GetInt64(0)}/{role}/{row.GetInt64(1)}";
                if (row.IsDBNull(3))
                { Issue(EconomyIssueKind.RecoveryDebt, key, "Legacy festival award has no frozen result."); continue; }
                ReadFrozenGrants((byte[])row.GetValue(3), role, key);
            }
        }

        private void ReadEventMvp(long? role)
        {
            using var read = Command("SELECT source_key,player_id,awards,delivered FROM mvp_award_obligation" +
                (role == null ? "" : " WHERE player_id=$role"));
            if (role != null) read.Parameters.AddWithValue("$role", role.Value);
            using var row = read.ExecuteReader();
            while (row.Read())
            {
                string key = "mvp_award_obligation/" + row.GetString(0);
                if (row.GetInt64(3) is not (0 or 1)) throw new InvalidDataException("Invalid MVP delivery marker.");
                if (row.GetInt64(3) == 0) ReadFrozenGrants((byte[])row.GetValue(2), row.GetInt64(1), key);
            }
        }

        private void ReadFrozenGrants(byte[] bytes, long role, string key)
        {
            using var doc = Json(bytes);
            int index = 0;
            foreach (var grant in Elements(doc.RootElement))
            {
                Fields(grant, "Entry Count BindType ExpireTimeStamp");
                long entry = Long(grant, "Entry");
                if (entry is not (100 or 101)) Entry(entry, Long(grant, "Count"), Role(role, key),
                    EconomyCompartment.Pending, key + "/" + index,
                    GrantFingerprint(Long(grant, "BindType"), Long(grant, "ExpireTimeStamp")));
                index++;
            }
        }

        /// <summary>Mock Battle obligations hold a frozen result and the DropPools to roll, never goods: those arrive
        /// with the role's event/casual-battle receipt. An unpaid row with pools is an earned but unrolled reward.</summary>
        private void ReadCasualBattle()
        {
            using var read = Command("SELECT source_key,role_id,payout,delivered FROM casual_battle_obligation ORDER BY source_key");
            using var row = read.ExecuteReader();
            int rowNumber = 0;
            while (row.Read()) Guard("casual_battle_obligation/row/" + rowNumber++, () =>
            {
                string key = "casual_battle_obligation/" + SqlText(row, 0);
                byte[] payout = SqlBlob(row, 2);
                Measure("casual_battle_obligation", payout.Length, true);
                long delivered = SqlLong(row, 3);
                if (delivered is not (0 or 1)) throw new InvalidDataException("Invalid Mock Battle delivery marker.");
                using var doc = Json(payout);
                var value = doc.RootElement;
                Fields(value, "MatchId RoleId BattleTime ResultId Won Outcome StartMmr MmrDelta Stats Pools");
                Fields(Prop(value, "Stats"), "PersonalScore Damage Kills Assists DamageTaken Healing");
                if (Long(value, "RoleId") != SqlLong(row, 1)) throw new InvalidDataException("Mock Battle payout role mismatch.");
                if (delivered == 0 && Elements(Prop(value, "Pools")).Any())
                    Issue(EconomyIssueKind.RecoveryDebt, key,
                        "Unpaid Mock Battle DropPools; random contents cannot be resolved at read time.");
            });
        }

        private void ReadEventTimedBattle(long? role)
        {
            using var read = Command("SELECT source_key,role_id,grants,delivered FROM timed_battle_obligation" +
                (role == null ? "" : " WHERE role_id=$role"));
            if (role != null) read.Parameters.AddWithValue("$role", role.Value);
            using var row = read.ExecuteReader();
            while (row.Read())
            {
                string key = "timed_battle_obligation/" + row.GetString(0);
                if (row.GetInt64(3) is not (0 or 1)) throw new InvalidDataException("Invalid timed battle delivery marker.");
                if (row.GetInt64(3) == 0) ReadFrozenGrants((byte[])row.GetValue(2), row.GetInt64(1), key);
            }
        }
    }
}
