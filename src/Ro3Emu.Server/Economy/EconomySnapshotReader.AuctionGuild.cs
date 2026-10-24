using System.Globalization;
using System.Text;
using System.Text.Json;
using Romsg;

namespace Ro3Emu.Server.Economy;

public static partial class EconomySnapshotReader
{
    private sealed partial class State
    {
        private readonly Dictionary<long, long> _auctionCurrency = [];

        internal void ReadAuctions()
        {
            if (Has("auction_item") && new[] { "id", "source_key", "state" }.All(Columns("auction_item").ContainsKey))
            {
                using var cmd = Command("SELECT id,source_key,state FROM auction_item ORDER BY id");
                using var r = cmd.ExecuteReader();
                int rowNumber = 0;
                while (r.Read())
                {
                    Guard("auction_item/row/" + rowNumber++, () =>
                    {
                        long id = SqlLong(r, 0); string source = SqlText(r, 1), key = "auction/lot/" + id;
                        byte[] bytes = SqlBlob(r, 2); Measure("auction_item", bytes.Length, true);
                        Guard(key, () =>
                        {
                            using var doc = Json(bytes);
                            var row = doc.RootElement;
                            Fields(row, "Id SourceKey Type SrcType GuildId Entry Bind Start End State Owner Price Reserve Currency Raise FixedPrice MaxPrice TypeAttr LastBid Revision Auto AutoPaused RunId Eligible GiveUp Joined Winner RandomAward SettlementRecoveryReason");
                            if (Long(row, "Id") != id || String(row, "SourceKey") != source || source.Length == 0)
                                throw new FormatException("Auction lot identity mismatch.");
                            long entry = Long(row, "Entry"), currency = Long(row, "Currency"), owner = Long(row, "Owner"),
                                reserve = Long(row, "Reserve"), winner = Long(row, "Winner"), status = Long(row, "State");
                            if (entry <= 0 || !catalog.TryAsset(entry, out var asset) || asset.Kind != EconomyAssetKind.Item)
                            { Issue(EconomyIssueKind.Coverage, key, "Auction item is absent from item catalogue."); return; }
                            if (!catalog.TryAsset(currency, out var money) || money.Kind != EconomyAssetKind.Currency)
                            { Issue(EconomyIssueKind.Coverage, key, "Auction currency is not catalogued."); return; }
                            if (owner < 0 || reserve < 0 || winner < 0 || status <= (int)AuctionState.Snone || status > (int)AuctionState.Settled)
                                throw new FormatException("Invalid auction state or reserve.");
                            _auctionCurrency[id] = currency;
                            if (status == (int)AuctionState.Settled)
                            {
                                if (winner == 0)
                                {
                                    Hold(asset, new(EconomyOwnerKind.Recovery, "auction:" + id), EconomyCompartment.AuctionLot,
                                        key, 1, GrantFingerprint(Long(row, "Bind")));
                                    if (reserve > 0)
                                    {
                                        Currency(currency, reserve, new(EconomyOwnerKind.Recovery, "auction-reserve:" + id),
                                            EconomyCompartment.AuctionReserve, key + "/reserve");
                                        Issue(EconomyIssueKind.Corruption, key, "Settled unsold lot retains a bidder reserve.");
                                    }
                                    Issue(EconomyIssueKind.RecoveryDebt, key, "Settled unsold lot retains ownership; retirement/return policy is unresolved.");
                                }
                                else if (!_mailDeliveryKeys.Contains(winner + "/auction:" + id + ":award")
                                    && !_mailDeliveryKeys.Contains(winner + "/auction:" + id + ":unsold"))
                                    Issue(EconomyIssueKind.RecoveryDebt, key, "Settled winner mail linkage is absent or already claimed/deleted.");
                                return;
                            }
                            Hold(asset, new(EconomyOwnerKind.Escrow, "auction:" + id), EconomyCompartment.AuctionLot,
                                key, 1, GrantFingerprint(Long(row, "Bind")));
                            if (reserve > 0)
                            {
                                if (owner <= 0) throw new FormatException("Auction reserve has no owner.");
                                Currency(currency, reserve, Role(owner, key), EconomyCompartment.AuctionReserve, key + "/reserve");
                            }
                        });
                    });
                }
            }
            if (Has("auction_prebuy") && new[] { "id", "role_id", "quantity", "unit_price", "currency", "processed", "paused" }
                .All(Columns("auction_prebuy").ContainsKey))
            {
                using var cmd = Command("SELECT id,role_id,quantity,unit_price,currency,processed,paused FROM auction_prebuy ORDER BY id");
                using var r = cmd.ExecuteReader();
                int rowNumber = 0;
                while (r.Read())
                {
                    Guard("auction_prebuy/row/" + rowNumber++, () =>
                    {
                        long id = SqlLong(r, 0); string key = "auction/prebuy/" + id; Measure("auction_prebuy", 0);
                        Guard(key, () =>
                        {
                            long role = SqlLong(r, 1), quantity = SqlLong(r, 2), price = SqlLong(r, 3), currency = SqlLong(r, 4), processed = SqlLong(r, 5);
                            if (quantity != 1 || price <= 0 || processed is not (0 or 1) || SqlLong(r, 6) is not (0 or 1))
                                throw new FormatException("Unknown prebuy quantity/status semantics.");
                            if (processed == 0) Currency(currency, price, Role(role, key), EconomyCompartment.AuctionPrebuy, key);
                        });
                    });
                }
            }
            if (Has("auction_bonus") && new[] { "role_id", "group_key", "amount", "claimed", "guild_id" }.All(Columns("auction_bonus").ContainsKey))
            {
                using var cmd = Command("SELECT role_id,group_key,amount,claimed,guild_id FROM auction_bonus ORDER BY role_id,group_key");
                using var r = cmd.ExecuteReader();
                int rowNumber = 0;
                while (r.Read())
                {
                    Guard("auction_bonus/row/" + rowNumber++, () =>
                    {
                        long role = SqlLong(r, 0), amount = SqlLong(r, 2), claimed = SqlLong(r, 3);
                        string group = SqlText(r, 1), key = "auction/bonus/" + role + "/" + group;
                        Measure("auction_bonus", Encoding.UTF8.GetByteCount(group));
                        Guard(key, () =>
                        {
                            if (amount <= 0 || claimed is not (0 or 1)) throw new FormatException("Invalid auction bonus amount/status.");
                            if (claimed == 1) return; // Current writer records delivered bonus with claimed=1.
                            string[] parts = group.Split(':');
                            if (parts.Length < 2 || !long.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out long lot)
                                || !_auctionCurrency.ContainsKey(lot))
                            { Issue(EconomyIssueKind.Coverage, key, "Legacy unclaimed bonus lot/source linkage is ambiguous."); return; }
                            if (_mailDeliveryKeys.Contains(role + "/auction:" + lot + ":dividend:" + role)
                                || _mailDeliveryKeys.Contains(role + "/auction:bonus:" + group + ":" + role))
                                Issue(EconomyIssueKind.Corruption, key, "Unclaimed bonus has an already-delivered mail entitlement.");
                            Currency(catalog.AuctionBonusCurrency, amount, Role(role, key), EconomyCompartment.AuctionBonus, key);
                        });
                    });
                }
            }
            ScanJsonEvidence("auction_run", "id", "state", (key, root) =>
            {
                Fields(root, "Id GuildId SrcType PlayBook TierGroups Eligible Finalized Ready");
                if (!Bool(root, "Finalized") && Bool(root, "Ready"))
                    Issue(EconomyIssueKind.RecoveryDebt, key, "Earned run has unresolved award outcome.");
            });
            if (Has("auction_source")) ScanTextKeys("auction_source", "source_key");
        }

        internal void ReadGuilds()
        {
            // Guild ids are collected before market/caravan ownership is checked.
            if (Has("guild") && new[] { "id", "state" }.All(Columns("guild").ContainsKey))
            {
                using var cmd = Command("SELECT id,state FROM guild ORDER BY id");
                using var r = cmd.ExecuteReader();
                int rowNumber = 0;
                while (r.Read())
                {
                    Guard("guild/row/" + rowNumber++, () =>
                    {
                        long id = SqlLong(r, 0); _guilds.Add(id); string key = "guild/" + id;
                        byte[] bytes = SqlBlob(r, 1); Measure("guild", bytes.Length, true);
                        Guard(key, () =>
                        {
                            using var doc = Json(bytes); var state = doc.RootElement;
                            Fields(state, "Id Name Announce Notice CreateTime Status HelperDeadlineMs Level Background Icon AutoAccept AutoCreateHelper JoinLevelLimit Money Contribute Materials Reputation AuctionMaterials LastChangeNameTime LastChangeNoticeTime LastRecruitMs HistoryNames Members Applications HelperApplies Rights Buildings BuildingEndTimes CurActivity LastWeekActivity ActivityWeek LeagueElites LeagueEliteVersion UnlockedHeadIcons ShopCommodityBuys ShopCommodityWeek NextBroadEmailMs BroadEmailCount BroadEmailDay ImpeachOwner ImpeachEndMs ImpeachNeed ImpeachResponders NextImpeachMs MergeInvites PendingMergeGuildId MergeAbsorbs MergeAtSec MergeMemberIds MergeCooldownSec");
                            if (Long(state, "Id") != id) throw new FormatException("Guild identity mismatch.");
                            var owner = new EconomyOwner(EconomyOwnerKind.Guild, Id(id));
                            foreach (var field in new[] { ("Money", 1L), ("Contribute", 2L), ("Materials", 3L),
                            ("Reputation", 4L), ("AuctionMaterials", 5L) })
                            {
                                long quantity = Long(state, field.Item1);
                                if (quantity < 0) throw new FormatException("Negative guild resource " + field.Item1);
                                if (quantity > 0) Resource(EconomyAssetKind.GuildResource, field.Item2, quantity,
                                    owner, EconomyCompartment.GuildTreasury, key + "/" + field.Item1);
                            }
                            // Member CurContribute/HistoryContribute are progression/history mirrors, not a purse; guild and
                            // member activity counters and a member's brought-in funds (ContributeMoney) are counters too.
                        });
                    });
                }
            }
            if (Has("guild_recovery") && new[] { "guild_id", "state", "created_ms" }.All(Columns("guild_recovery").ContainsKey))
            {
                using var cmd = Command("SELECT guild_id,state,created_ms FROM guild_recovery ORDER BY guild_id");
                using var r = cmd.ExecuteReader();
                int rowNumber = 0;
                while (r.Read())
                {
                    Guard("guild_recovery/row/" + rowNumber++, () =>
                    {
                        long id = SqlLong(r, 0); string key = "guild-recovery/" + id;
                        byte[] bytes = SqlBlob(r, 1); Measure("guild_recovery", bytes.Length, true);
                        Guard(key, () =>
                        {
                            if (id <= 0 || _guilds.Contains(id) || !_recoveredGuilds.Add(id) || SqlLong(r, 2) <= 0)
                                throw new FormatException("Invalid or live guild recovery identity.");
                            using var doc = Json(bytes);
                            var state = doc.RootElement;
                            Fields(state, "Id Name Announce Notice CreateTime Status HelperDeadlineMs Level Background Icon AutoAccept AutoCreateHelper JoinLevelLimit Money Contribute Materials Reputation AuctionMaterials LastChangeNameTime LastChangeNoticeTime LastRecruitMs HistoryNames Members Applications HelperApplies Rights Buildings BuildingEndTimes CurActivity LastWeekActivity ActivityWeek LeagueElites LeagueEliteVersion UnlockedHeadIcons ShopCommodityBuys ShopCommodityWeek NextBroadEmailMs BroadEmailCount BroadEmailDay ImpeachOwner ImpeachEndMs ImpeachNeed ImpeachResponders NextImpeachMs MergeInvites PendingMergeGuildId MergeAbsorbs MergeAtSec MergeMemberIds MergeCooldownSec");
                            if (Long(state, "Id") != id) throw new FormatException("Guild recovery snapshot identity mismatch.");
                            var owner = new EconomyOwner(EconomyOwnerKind.Guild, Id(id));
                            foreach (var field in new[] { ("Money", 1L), ("Contribute", 2L), ("Materials", 3L),
                            ("Reputation", 4L), ("AuctionMaterials", 5L) })
                            {
                                long quantity = Long(state, field.Item1);
                                if (quantity < 0) throw new FormatException("Negative recovered guild resource " + field.Item1);
                                if (quantity > 0) Resource(EconomyAssetKind.GuildResource, field.Item2, quantity,
                                    owner, EconomyCompartment.Recovery, key + "/" + field.Item1);
                            }
                            Issue(EconomyIssueKind.RecoveryDebt, key, "Disbanded guild retains assets pending an explicit settlement policy.");
                        });
                    });
                }
            }
            ScanJsonEvidence("guild_market", "id", "state", (key, state) =>
            {
                Fields(state, "NextId RecentAvgPrice RecentPrices PriceHistory Stalls RecoveryStalls Trades LastListedMs");
                if (!Try(state, "Stalls", out var stalls)) return;
                int n = 0;
                foreach (var row in Elements(stalls))
                {
                    Fields(row, "Id GuildId UnitPrice TotalCount RemainCount CreatedMs AvailableMs ExpiresMs");
                    string id = String(row, "Id", Id(n++)), path = key + "/" + id;
                    long guild = Long(row, "GuildId"), total = Long(row, "TotalCount"), remain = Long(row, "RemainCount");
                    if (guild <= 0 || remain < 0 || total < remain) throw new FormatException("Invalid guild market remaining material.");
                    if (remain > 0) Resource(EconomyAssetKind.GuildResource, 3, remain, Guild(guild, path),
                        EconomyCompartment.GuildMarket, path);
                }
                if (Try(state, "RecoveryStalls", out var recovery))
                    foreach (var row in Elements(recovery))
                    {
                        Fields(row, "Id GuildId UnitPrice TotalCount RemainCount CreatedMs AvailableMs ExpiresMs");
                        long guild = Long(row, "GuildId"), remain = Long(row, "RemainCount");
                        if (guild <= 0 || remain < 0) throw new FormatException("Invalid recovery guild stall.");
                        if (remain > 0)
                        {
                            string path = "guild_market/1/recovery/" + String(row, "Id");
                            Resource(EconomyAssetKind.GuildResource, 3, remain,
                                new EconomyOwner(EconomyOwnerKind.Recovery, "guild:" + Id(guild)),
                                EconomyCompartment.Recovery, path);
                            Issue(EconomyIssueKind.RecoveryDebt, path, "Orphaned guild-market stock needs return or retirement.");
                        }
                    }
            });
            if (Has("guild_caravan_member") && new[] { "role_id", "state" }.All(Columns("guild_caravan_member").ContainsKey))
            {
                using var cmd = Command("SELECT role_id,state FROM guild_caravan_member ORDER BY role_id");
                using var r = cmd.ExecuteReader();
                int rowNumber = 0;
                while (r.Read())
                {
                    Guard("guild_caravan_member/row/" + rowNumber++, () =>
                    {
                        long role = SqlLong(r, 0); string key = "caravan/" + role;
                        byte[] bytes = SqlBlob(r, 1); Measure("guild_caravan_member", bytes.Length, true);
                        Guard(key, () =>
                        {
                            using var doc = Json(bytes); var state = doc.RootElement;
                            Fields(state, "Revision GuildId Day CurRound RefreshTimes HelpOtherCount ReqHelpOtherCount IsAwardGot LastRefreshTime Skus PendingAwards");
                            if (!Try(state, "PendingAwards", out var awards)) return;
                            int index = 0;
                            foreach (var award in Elements(awards))
                            {
                                Fields(award, "GuildId GuildBooked Grants SourceKey");
                                string stable = String(award, "SourceKey", "");
                                string path = key + "/" + (stable.Length == 0 ? index.ToString(CultureInfo.InvariantCulture) : stable);
                                index++;
                                bool booked = Bool(award, "GuildBooked");
                                long guild = Long(award, "GuildId");
                                if (!Try(award, "Grants", out var grants)) throw new JsonException("Pending caravan grant list is missing.");
                                int n = 0;
                                foreach (var grant in Elements(grants))
                                {
                                    Fields(grant, "Entry Count BindType ExpireTimeStamp");
                                    long entry = Long(grant, "Entry"), count = Long(grant, "Count");
                                    string grantKey = path + "/" + n++;
                                    if (entry == 104)
                                    {
                                        if (!booked)
                                        {
                                            Resource(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMoney, count,
                                                Guild(guild, grantKey), EconomyCompartment.CaravanPending, grantKey);
                                            Issue(EconomyIssueKind.RecoveryDebt, grantKey, $"Guild {guild} has unbooked earned funds {count}.");
                                        }
                                        continue; // Already-booked guild Money exists in guild.state.
                                    }
                                    Entry(entry, count, Role(role, grantKey), EconomyCompartment.CaravanPending, grantKey,
                                        GrantFingerprint(Long(grant, "BindType"), Long(grant, "ExpireTimeStamp")));
                                }
                            }
                        });
                    });
                }
            }
            if (Has("guild_caravan_version")) ScanIntegerKeys("guild_caravan_version", "guild_id");
            ScanJsonEvidence("guild_campfire", "guild_id", "state", (key, root) =>
                Fields(root, "Day Exp Seeds FuelCount DrawnStages BossDead FruitKills"));
            ScanJsonEvidence("guild_challenge", "guild_id", "state", (key, root) =>
                Fields(root, "Season Cycle Activated Unlocked LastUnlocked PassedDifficulty Difficulty Stage Selected Complete Progress BestTimeMs AuctionEntries"));
        }

        private void ScanTextKeys(string table, string column)
        {
            if (!Has(table) || !Columns(table).ContainsKey(column)) return;
            using var cmd = Command($"SELECT \"{column}\" FROM \"{table}\" ORDER BY \"{column}\"");
            using var r = cmd.ExecuteReader();
            int rowNumber = 0;
            while (r.Read()) Guard(table + "/row/" + rowNumber++, () =>
            {
                if (r[0] is not string value) throw new InvalidCastException("Expected text key.");
                Measure(table, Encoding.UTF8.GetByteCount(value));
            });
        }

        private void ScanIntegerKeys(string table, string column)
        {
            if (!Has(table) || !Columns(table).ContainsKey(column)) return;
            using var cmd = Command($"SELECT \"{column}\" FROM \"{table}\" ORDER BY \"{column}\"");
            using var r = cmd.ExecuteReader();
            int rowNumber = 0;
            while (r.Read()) Guard(table + "/row/" + rowNumber++, () =>
            {
                if (SqlLong(r, 0) <= 0) throw new FormatException("Invalid persisted metadata key.");
                Measure(table, 0); // Exact payload bytes excludes SQLite INTEGER storage.
            });
        }

        private void ScanJsonEvidence(string table, string keyColumn, string blobColumn, Action<string, System.Text.Json.JsonElement> decode)
        {
            if (!Has(table) || !new[] { keyColumn, blobColumn }.All(Columns(table).ContainsKey)) return;
            using var cmd = Command($"SELECT \"{keyColumn}\",\"{blobColumn}\" FROM \"{table}\" ORDER BY \"{keyColumn}\"");
            using var r = cmd.ExecuteReader();
            int rowNumber = 0;
            while (r.Read())
            {
                Guard(table + "/row/" + rowNumber++, () =>
                {
                    string key = table + "/" + Convert.ToString(r[0], CultureInfo.InvariantCulture);
                    byte[] bytes = SqlBlob(r, 1); Measure(table, bytes.Length, true);
                    Guard(key, () =>
                    {
                        using var doc = Json(bytes); if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                            throw new System.Text.Json.JsonException("Expected JSON object."); decode(key, doc.RootElement);
                    });
                });
            }
        }
    }
}
