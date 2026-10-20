using System.Globalization;
using System.Text;
using System.Text.Json;
using Romsg;

namespace Ro3Emu.Server.Economy;

public static partial class EconomySnapshotReader
{
    private sealed partial class State
    {
        private void ReadTerritoryRole(JsonElement state, long role, string key)
        {
            Fields(state, "Epoch Seasons Camp LastCampChange Honor Valor Fatigue FatigueKills LastFatigue Donated Donations WorldLandClaims Pending Paid");
            if (!Try(state, "Pending", out var pending)) return;
            foreach (var grantSet in Properties(pending))
            {
                string path = key + "/pending/" + grantSet.Name;
                string[] parts = grantSet.Name.Split(':');
                bool worldLand = parts.Length == 6 && parts[0] == "world-land";
                int start = worldLand ? 1 : 0;
                if (!(parts.Length == 4 || worldLand) || parts.Skip(start).Any(p => !long.TryParse(p, NumberStyles.None,
                    CultureInfo.InvariantCulture, out long value) || value <= 0))
                { Issue(EconomyIssueKind.Coverage, path, "Unparseable territory pending source epoch/round/zone."); continue; }
                string epoch = parts[start] + ":" + parts[start + 1];
                if (Try(state, "Paid", out var paid) && Elements(paid).Any(x => x.GetString() == grantSet.Name))
                    Issue(EconomyIssueKind.Corruption, path, "Territory grant is both pending and marked paid.");
                int n = 0;
                foreach (var grant in Elements(grantSet.Value))
                {
                    Fields(grant, "Entry Count Bind");
                    Entry(Long(grant, "Entry"), Long(grant, "Count"), Role(role, path),
                        EconomyCompartment.TerritoryPending, path + "/" + n++,
                        GrantFingerprint(Long(grant, "Bind")), epoch: epoch);
                }
            }
        }

        internal void ReadTerritory()
        {
            ScanJsonEvidence("territory_wars", "id", "state", (key, state) => DecodeWarsWorld(state, key, false));
            if (Has("territory_wars_seasons") && new[] { "epoch", "state" }.All(Columns("territory_wars_seasons").ContainsKey))
            {
                using var cmd = Command("SELECT epoch,state FROM territory_wars_seasons ORDER BY epoch");
                using var r = cmd.ExecuteReader();
                int rowNumber = 0;
                while (r.Read())
                {
                    Guard("territory_wars_seasons/row/" + rowNumber++, () =>
                    {
                        string epoch = SqlText(r, 0), key = "territory_wars_seasons/" + epoch;
                        byte[] bytes = SqlBlob(r, 1); Measure("territory_wars_seasons", bytes.Length, true);
                        Guard(key, () =>
                        {
                            using var doc = Json(bytes);
                            if (String(doc.RootElement, "Epoch") != epoch) throw new FormatException("Territory archive epoch mismatch.");
                            DecodeWarsWorld(doc.RootElement, key, true);
                        });
                    });
                }
            }
            ScanJsonEvidence("territory_escort", "id", "state", DecodeEscort);
        }

        private readonly HashSet<string> activeWarsEpochs = [];
        private void DecodeWarsWorld(JsonElement state, string key, bool archived)
        {
            Fields(state, "Epoch LastStage Owners Rounds Battles Results SiegeHistory SiegeRuns Donations RuleResults WorldDrops NextWorldDropId WorldActivityRespawns WorldActivityOccurrences WorldPickupOccurrences");
            string epoch = String(state, "Epoch");
            if (archived && (epoch.Length == 0 || activeWarsEpochs.Contains(epoch)))
            { Issue(EconomyIssueKind.Coverage, key, "Archive epoch absent or duplicates active state."); return; }
            if (!archived && epoch.Length > 0) activeWarsEpochs.Add(epoch);
            if (Try(state, "WorldActivityOccurrences", out var activityOccurrences))
                foreach (var occurrence in Properties(activityOccurrences))
                {
                    if (!int.TryParse(occurrence.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                        || id <= 0 || occurrence.Value.GetInt64() < 0)
                        throw new FormatException("Invalid territory land-actor occurrence.");
                }
            if (Try(state, "WorldPickupOccurrences", out var pickupOccurrences))
                foreach (var occurrence in Properties(pickupOccurrences))
                {
                    Fields(occurrence.Value, "Ordinal NextEligible");
                    var slot = occurrence.Name.Split(':');
                    if (slot.Length != 4 || slot.Any(part => !long.TryParse(part, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out _)))
                        throw new FormatException("Invalid territory pickup slot identity.");
                    if (Long(occurrence.Value, "Ordinal") < 0 || Long(occurrence.Value, "NextEligible") < 0)
                        throw new FormatException("Negative territory pickup occurrence state.");
                }
            if (Try(state, "WorldDrops", out var drops))
                foreach (var drop in Properties(drops))
                {
                    Fields(drop.Value, "Id Scene X Y Z Amount");
                    long amount = Long(drop.Value, "Amount");
                    Currency(133, amount, new(EconomyOwnerKind.Escrow, "territory-drop:" + epoch),
                        EconomyCompartment.TerritoryDrop, key + "/epoch/" + epoch + "/drop/" + drop.Name, epoch);
                }
            if (!Try(state, "Rounds", out var rounds)) return;
            foreach (var round in Properties(rounds))
            {
                Fields(round.Value, "Round FinalEnd Settled Zones Auto");
                if (Bool(round.Value, "Settled")) continue;
                if (!Try(round.Value, "Zones", out var zones)) continue;
                foreach (var zone in Properties(zones))
                {
                    Fields(zone.Value, "Id Seats Entries Bidded");
                    if (!Try(zone.Value, "Entries", out var entries)) continue;
                    int n = 0;
                    foreach (var bid in Elements(entries))
                    {
                        Fields(bid, "GuildId Amount Time Occupier Legacy EscortScore");
                        string path = key + "/epoch/" + epoch + "/round/" + round.Name + "/zone/" + zone.Name + "/bid/" + n++;
                        if (Bool(bid, "Occupier")) continue;
                        long amount = Long(bid, "Amount"), guild = Long(bid, "GuildId");
                        Resource(EconomyAssetKind.GuildResource, Bool(bid, "Legacy") ? 3 : 5,
                            amount, Guild(guild, path), EconomyCompartment.TerritoryBid, path, epoch);
                    }
                }
            }
        }

        private void DecodeEscort(string key, JsonElement state)
        {
            Fields(state, "SeasonStart PlayBook Cycle NextGoodsId NextCartId NextCargoId Listings Goods Carts Cargo Play ScoresByRound StockRounds SkillCooldowns Archives");
            // OpenContent: a Territory War cycle > 0 appends ":cycle" (TerritoryWarsClock.EpochOf).
            long cycle = Try(state, "Cycle", out _) ? Long(state, "Cycle") : 0;
            string epoch = Long(state, "SeasonStart") + ":" + Long(state, "PlayBook") + (cycle > 0 ? ":" + cycle : "");
            DecodeEscortGoods(state, key, epoch, false);
            if (Try(state, "Archives", out var archives)) foreach (var archive in Properties(archives))
            {
                Fields(archive.Value, "Goods Listings Play ScoresByRound StockRounds SkillCooldowns");
                string path = key + "/archive/" + archive.Name;
                string[] parts = archive.Name.Split(':');
                if (parts.Length is not (2 or 3) || parts.Any(p => !long.TryParse(p, NumberStyles.None,
                    CultureInfo.InvariantCulture, out long value) || value <= 0))
                { Issue(EconomyIssueKind.Coverage, path, "Unparseable escort archive epoch."); continue; }
                if (archive.Name == epoch) { Issue(EconomyIssueKind.Coverage, path, "Archive duplicates active escort epoch."); continue; }
                DecodeEscortGoods(archive.Value, path, archive.Name, true);
            }
            if (Try(state, "Carts", out var carts)) foreach (var cart in Properties(carts))
            {
                Fields(cart.Value, "Id Guild Owner Round Type Point Count OriginCount DamageTier CurrentHp Started Scene");
                string path = key + "/cart/" + cart.Name;
                long guild = Long(cart.Value, "Guild"), remaining = Long(cart.Value, "Count");
                if (remaining < 0) throw new FormatException("Negative cart cargo.");
                if (remaining > 0) Resource(EconomyAssetKind.EventResource, 1, remaining,
                    Guild(guild, path), EconomyCompartment.EscortCart, path, epoch);
            }
            if (Try(state, "Cargo", out var cargo)) foreach (var row in Properties(cargo))
            {
                Fields(row.Value, "Id Scene LooterGuild X Y Z FreeAfter Expires Picked");
                string path = key + "/cargo/" + row.Name;
                if (!Bool(row.Value, "Picked")) Entry(1258002, 1, new(EconomyOwnerKind.Escrow, "escort-cargo:" + row.Name),
                    EconomyCompartment.EscortCargo, path, GrantFingerprint(0), epoch: epoch);
            }
        }

        private void DecodeEscortGoods(JsonElement state, string key, string epoch, bool archive)
        {
            if (Try(state, "Goods", out var goods)) foreach (var row in Properties(goods))
            {
                string path = key + "/goods/" + row.Name;
                long guild = long.Parse(row.Name, CultureInfo.InvariantCulture), count = row.Value.GetInt64();
                if (count < 0) throw new FormatException("Negative escort goods.");
                if (count > 0) Resource(EconomyAssetKind.EventResource, 1, count,
                    Guild(guild, path), EconomyCompartment.EscortGoods, path, epoch);
            }
            if (Try(state, "Listings", out var listings)) foreach (var row in Properties(listings))
            {
                Fields(row.Value, "Id Seller Count Price Message State Time Buyer Applications Rejected");
                string path = key + "/listing/" + row.Name;
                long status = Long(row.Value, "State"), count = Long(row.Value, "Count");
                if (status is < 0 or > 2 || count < 0) throw new FormatException("Invalid escort listing status/count.");
                if (status == 0 && count > 0) Resource(EconomyAssetKind.EventResource, 1, count,
                    Guild(Long(row.Value, "Seller"), path), EconomyCompartment.EscortListing, path, epoch);
            }
        }

        internal void ReadObligations()
        {
            if (Has("festival_reward") && new[] { "cycle", "role_id", "source", "award", "claimed" }.All(Columns("festival_reward").ContainsKey))
            {
                bool frozen = Columns("festival_reward").ContainsKey("grants");
                using var cmd = Command("SELECT cycle,role_id,source,award,claimed" + (frozen ? ",grants" : "") +
                    " FROM festival_reward ORDER BY cycle,role_id,source");
                using var r = cmd.ExecuteReader();
                int rowNumber = 0;
                while (r.Read())
                {
                    Guard("festival_reward/row/" + rowNumber++, () =>
                    {
                        string key = $"festival_reward/{SqlLong(r, 0)}/{SqlLong(r, 1)}/{SqlLong(r, 2)}";
                        Measure("festival_reward", 0);
                        if (SqlLong(r, 4) == 0)
                        {
                            if (frozen && !r.IsDBNull(5)) ReadFrozenGrants(SqlBlob(r, 5), SqlLong(r, 1), key);
                            else Issue(EconomyIssueKind.RecoveryDebt, key,
                                $"Unclaimed earned AwardId {SqlLong(r, 3)}; random contents cannot be resolved at read time.");
                        }
                        else if (SqlLong(r, 4) != 1) Issue(EconomyIssueKind.Corruption, key, "Invalid claim status.");
                    });
                }
            }
            if (Has("mvp_award_obligation")) Guard("mvp_award_obligation", () => ReadEventMvp(null));
            if (Has("timed_battle_obligation")) Guard("timed_battle_obligation", () => ReadEventTimedBattle(null));
            if (Has("casual_battle_obligation")) Guard("casual_battle_obligation", ReadCasualBattle);
            if (Has("guild_challenge_clear") && new[] { "id", "pending", "state" }.All(Columns("guild_challenge_clear").ContainsKey))
            {
                using var cmd = Command("SELECT id,pending,state FROM guild_challenge_clear ORDER BY id");
                using var r = cmd.ExecuteReader();
                int rowNumber = 0;
                while (r.Read())
                {
                    Guard("guild_challenge_clear/row/" + rowNumber++, () =>
                    {
                        string key = "guild_challenge_clear/" + SqlText(r, 0); byte[] blob = SqlBlob(r, 2);
                        Measure("guild_challenge_clear", blob.Length, true);
                        Guard(key, () =>
                        {
                            using var doc = Json(blob);
                            var root = doc.RootElement;
                            Fields(root, "Id Guild Cycle Season PlayBook OpenDay Challenge Difficulty Stage StageTimeMs PassTimeMs NewRecord Personal Delivered AuctionPublished AuctionEntries");
                            if (String(root, "Id") != SqlText(r, 0)) throw new FormatException("Guild clear identity mismatch.");
                            long pending = SqlLong(r, 1);
                            if (pending is not (0 or 1)) throw new FormatException("Invalid guild challenge pending status.");
                            var delivered = Try(root, "Delivered", out var settled)
                                ? Elements(settled).Select(x => x.GetInt64()).ToHashSet() : new HashSet<long>();
                            if (Try(root, "Personal", out var personal)) foreach (var grant in Properties(personal))
                            {
                                long role = long.Parse(grant.Name, CultureInfo.InvariantCulture);
                                if (delivered.Contains(role)) continue;
                                if (pending == 0) Issue(EconomyIssueKind.Corruption, key + "/" + role,
                                "Undelivered personal clear has terminal pending marker.");
                                int index = 0;
                                foreach (var good in Elements(grant.Value))
                                {
                                    Fields(good, "Entry Count Bind");
                                    Entry(Long(good, "Entry"), Long(good, "Count"), Role(role, key),
                                    EconomyCompartment.Pending, key + "/" + role + "/" + index++,
                                    GrantFingerprint(Long(good, "Bind")));
                                }
                            }
                            if (pending == 1) Issue(EconomyIssueKind.RecoveryDebt, key,
                                "Earned guild challenge clear has undelivered goods or auction publication.");
                        });
                    });
                }
            }
            ReadFestivalRounds();
            ReadActivityEveGlobal();
            ReadServerState();
        }

        private void ReadFestivalRounds()
        {
            if (!Has("festival_round") || !new[] { "cycle", "state" }.All(Columns("festival_round").ContainsKey)) return;
            using var cmd = Command("SELECT cycle,state FROM festival_round ORDER BY cycle");
            using var r = cmd.ExecuteReader();
            int rowNumber = 0;
            while (r.Read()) Guard("festival_round/row/" + rowNumber++, () =>
            {
                long cycle = SqlLong(r, 0);
                string key = "festival_round/" + Id(cycle);
                byte[] blob = SqlBlob(r, 1);
                Measure("festival_round", blob.Length, true);
                Guard(key, () =>
                {
                    using var doc = Json(blob);
                    var round = doc.RootElement;
                    Fields(round, "Cycle AnswerStart End Phase Question Deadline Questions Answers Candidates Bets Complete");
                    if (cycle <= 0 || Long(round, "Cycle") != cycle) throw new FormatException("Festival round cycle mismatch.");
                    if (Try(round, "Questions", out var questions)) foreach (var q in Elements(questions)) q.GetInt64();
                    if (Try(round, "Answers", out var answers)) foreach (var a in Elements(answers)) a.GetInt64();
                    if (Try(round, "Candidates", out var candidates)) foreach (var candidate in Properties(candidates))
                    {
                        if (!long.TryParse(candidate.Name, NumberStyles.None, CultureInfo.InvariantCulture, out long role) || role <= 0)
                            throw new FormatException("Invalid festival candidate role key.");
                        Fields(candidate.Value, "Entered Eliminated GiveUp Rank");
                        if (Long(candidate.Value, "Rank") < 0) throw new FormatException("Negative festival rank.");
                    }
                    if (Try(round, "Bets", out var bets)) foreach (var bet in Properties(bets))
                    {
                        if (!long.TryParse(bet.Name, NumberStyles.None, CultureInfo.InvariantCulture, out long role) || role <= 0
                            || bet.Value.GetInt64() < 0) throw new FormatException("Invalid festival bet history.");
                    }
                    // The writer's pinned DTO validates all known scalar types, including nested Candidate flags.
                    _ = round.Deserialize<Ro3Emu.Server.Game.FestivalService.Round>(StrictJson)
                        ?? throw new JsonException("Null festival round.");
                });
            });
        }

        private void ReadActivityEveGlobal()
        {
            if (!Has("activity_eve_global") || !new[] { "activity_id", "cycle_start", "blob" }
                .All(Columns("activity_eve_global").ContainsKey)) return;
            using var cmd = Command("SELECT activity_id,cycle_start,blob FROM activity_eve_global ORDER BY activity_id,cycle_start");
            using var r = cmd.ExecuteReader();
            int rowNumber = 0;
            while (r.Read()) Guard("activity_eve_global/row/" + rowNumber++, () =>
            {
                long activity = SqlLong(r, 0), cycle = SqlLong(r, 1);
                if (activity <= 0 || cycle <= 0) throw new FormatException("Invalid season eve identity.");
                string key = $"activity_eve_global/{activity}/{cycle}";
                byte[] blob = SqlBlob(r, 2);
                Measure("activity_eve_global", blob.Length, true);
                Guard(key, () => ValidateProto(blob, DBSeasonEveInfo.Descriptor));
            });
        }

        private void ReadServerState()
        {
            if (!Has("server_state") || !new[] { "key", "value" }.All(Columns("server_state").ContainsKey)) return;
            using var cmd = Command("SELECT key,value FROM server_state ORDER BY key");
            using var r = cmd.ExecuteReader();
            int rowNumber = 0;
            while (r.Read()) Guard("server_state/row/" + rowNumber++, () =>
            {
                string stateKey = SqlText(r, 0), value = SqlText(r, 1), key = "server_state/" + stateKey;
                Measure("server_state", Encoding.UTF8.GetByteCount(stateKey) + Encoding.UTF8.GetByteCount(value));
                Guard(key, () =>
                {
                    switch (stateKey)
                    {
                        case "season_start":
                            if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out _)) throw new FormatException("Invalid season start date.");
                            break;
                        case "season_id":
                        case "playbook":
                        case "instance_sequence":
                            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number <= 0)
                                throw new FormatException("Invalid positive server state counter.");
                            break;
                        case "season_history":
                            using (var doc = Json(value))
                            {
                                foreach (var season in Properties(doc.RootElement))
                                {
                                    if (!long.TryParse(season.Name, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
                                        throw new FormatException("Invalid season history key.");
                                    Fields(season.Value, "Id PlayBook Start End");
                                    if (Long(season.Value, "Id") != id || Long(season.Value, "PlayBook") <= 0)
                                        throw new FormatException("Season history identity mismatch.");
                                    Prop(season.Value, "Start").GetDateTime();
                                    Prop(season.Value, "End").GetDateTime();
                                }
                            }
                            break;
                        case "multi_dungeon_server":
                            // Realm of the Gods first clears (hall of fame, Sacred Blessing unlocks): records, no balance.
                            ValidateProto(Convert.FromBase64String(value), DBMultiDungeonServerInfo.Descriptor);
                            break;
                        default:
                            Issue(EconomyIssueKind.UnknownSchema, key, "Unclassified server_state key.");
                            break;
                    }
                });
            });
        }
    }
}
