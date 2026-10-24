using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

public sealed class S3EconomyDecoderTests
{
    [Fact]
    public void TaskFollowupSourcesAreRecoveryEvidenceWithoutDuplicateAssets()
    {
        using var f = new Fixture();
        f.Insert("INSERT INTO role(id,currency,task_followup) VALUES(101,'1:7',$0)", B("[\"101:1:300:0\"]"));
        var snapshot = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.DoesNotContain(snapshot.Issues, x => x.Kind == EconomyIssueKind.UnknownSchema);
        Assert.Contains(snapshot.Issues, x => x.Kind == EconomyIssueKind.RecoveryDebt && x.Key == "role/101/task_followup");
        Assert.Equal(7, Assert.Single(snapshot.Holdings).Quantity);
        Assert.Equal("", Assert.Single(snapshot.Holdings).Fingerprint);
        f.Insert("UPDATE role SET task_followup=$0 WHERE id=101", B("[123]"));
        Assert.Contains(EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture").Issues,
            x => x.Kind == EconomyIssueKind.Corruption && x.Key == "role/101/task_followup");
    }

    private static byte[] B(string value) => System.Text.Encoding.UTF8.GetBytes(value);
    private static readonly EconomyCatalog Catalog = new(new Dictionary<long, EconomyAssetKind>
    {
        [1] = EconomyAssetKind.Currency, [2] = EconomyAssetKind.Currency, [102] = EconomyAssetKind.Currency,
        [133] = EconomyAssetKind.Currency, [3] = EconomyAssetKind.Item, [4] = EconomyAssetKind.Item,
        [1258002] = EconomyAssetKind.Item
    });

    private sealed class Fixture : IDisposable
    {
        internal string PathName { get; } = Path.Combine(AppContext.BaseDirectory, "e00-decoder-" + Guid.NewGuid().ToString("N") + ".db");
        internal SqliteConnection Db { get; }
        internal Fixture()
        {
            Db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathName, Pooling = false }.ToString());
            Db.Open();
            Exec("""
                CREATE TABLE role(id INTEGER PRIMARY KEY,currency TEXT NOT NULL,mailbox BLOB,territory_wars BLOB,deleted_time INTEGER NOT NULL DEFAULT 0,
                    level INTEGER NOT NULL DEFAULT 1,exp INTEGER NOT NULL DEFAULT 0,
                    job_level INTEGER NOT NULL DEFAULT 1,job_exp INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE owner_revision(kind TEXT NOT NULL,id TEXT NOT NULL,part TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision >= 0),PRIMARY KEY(kind,id,part));
                CREATE TABLE item(guid INTEGER PRIMARY KEY,role_id INTEGER,entry INTEGER,stack INTEGER,pack INTEGER,sort INTEGER,grid INTEGER,attrs TEXT,enchantments TEXT,str_attrs TEXT,bind_type INTEGER,bind_expire INTEGER,is_locked INTEGER);
                CREATE TABLE dungeon_reward_obligation(source_key TEXT PRIMARY KEY,role_id INTEGER NOT NULL,room_id INTEGER NOT NULL,
                    activated INTEGER NOT NULL CHECK(activated IN (0,1)),completed INTEGER NOT NULL CHECK(completed IN (0,1)),state BLOB NOT NULL);
                CREATE TABLE market_record(kind TEXT,id TEXT,data TEXT,PRIMARY KEY(kind,id));
                CREATE TABLE auction_item(id INTEGER PRIMARY KEY,source_key TEXT,state BLOB);
                CREATE TABLE auction_prebuy(id INTEGER PRIMARY KEY,role_id INTEGER,quantity INTEGER,unit_price INTEGER,currency INTEGER,processed INTEGER,paused INTEGER);
                CREATE TABLE auction_bonus(role_id INTEGER,group_key TEXT,amount INTEGER,claimed INTEGER,guild_id INTEGER);
                CREATE TABLE auction_run(id INTEGER PRIMARY KEY,state BLOB);
                CREATE TABLE auction_source(source_key TEXT PRIMARY KEY);
                CREATE TABLE guild(id INTEGER PRIMARY KEY,state BLOB);
                CREATE TABLE guild_recovery(guild_id INTEGER PRIMARY KEY,state BLOB NOT NULL,created_ms INTEGER NOT NULL);
                CREATE TABLE guild_market(id INTEGER PRIMARY KEY,state BLOB);
                CREATE TABLE guild_caravan_member(role_id INTEGER PRIMARY KEY,state BLOB);
                CREATE TABLE guild_caravan_version(guild_id INTEGER PRIMARY KEY);
                CREATE TABLE guild_campfire(guild_id INTEGER PRIMARY KEY,state BLOB);
                CREATE TABLE guild_challenge(guild_id INTEGER PRIMARY KEY,state BLOB);
                CREATE TABLE territory_wars(id INTEGER PRIMARY KEY,state BLOB);
                CREATE TABLE territory_wars_seasons(epoch TEXT PRIMARY KEY,state BLOB);
                CREATE TABLE territory_escort(id INTEGER PRIMARY KEY,state BLOB);
                CREATE TABLE festival_reward(cycle INTEGER,role_id INTEGER,source INTEGER,award INTEGER,claimed INTEGER);
                CREATE TABLE guild_challenge_clear(id TEXT PRIMARY KEY,pending INTEGER,state BLOB);
                CREATE TABLE festival_round(cycle INTEGER PRIMARY KEY,state BLOB NOT NULL);
                CREATE TABLE activity_eve_global(activity_id INTEGER,cycle_start INTEGER,blob BLOB,PRIMARY KEY(activity_id,cycle_start));
                CREATE TABLE server_state(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                """);
            // Hand-authored E00 role blob inventory, based on CoreSchemaPlans rather than reader reflection.
            foreach (string column in ("settings skill_plans tasks task_runtime task_followup guides emoji_play achievements achievement_runtime adventure_group library card_plans shop trade_info stalls_info " +
                "auction guild_info guild_challenge pet_books daily new_activities activity_runtime timed_activity timed_battle " +
                "training lottery tower wild_afk dungeons territory_escort lifejob life_restrict life_runtime season_talent " +
                "season_page season_page_meta season_refine equip_levels equip_plans auto_decompose bag_state stat_plans " +
                "world_state emblems pets mounts wardrobe totems totem_refine_epoch genre career_special heroic_spirit paragon title area_concur growth_class first_heart personal personal_upvote popularity social_outfit emoji_info").Split(' '))
                Exec("ALTER TABLE role ADD COLUMN " + column + " BLOB");
        }
        internal void Exec(string sql)
        {
            using var cmd = Db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
        }
        internal void Insert(string sql, params object[] values)
        {
            using var cmd = Db.CreateCommand(); cmd.CommandText = sql;
            for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("$" + i, values[i]);
            cmd.ExecuteNonQuery();
        }
        public void Dispose() { Db.Dispose(); File.Delete(PathName); }
    }

    [Fact]
    public void RoleProgressionWithoutItsColumnsIsAnUnknownSchema()
    {
        using var f = new Fixture();
        f.Exec("ALTER TABLE role DROP COLUMN job_exp");
        f.Exec("DROP TABLE owner_revision; CREATE TABLE owner_revision(kind TEXT, id TEXT, part TEXT)");
        var issues = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture").Issues;
        Assert.Contains(issues, x => x.Kind == EconomyIssueKind.UnknownSchema && x.Key == "progress/schema");
        Assert.Contains(issues, x => x.Kind == EconomyIssueKind.UnknownSchema && x.Key == "progress/revision");
    }

    [Fact]
    public void AnUnknownJsonFieldIsAnUnknownSchemaAndAWrongValueIsCorruption()
    {
        using var f = new Fixture();
        f.Insert("INSERT INTO role(id,currency,world_state) VALUES(101,'1:7',$0)",
            B("{\"VisitedMaps\":[1],\"Extra\":1}"));
        Assert.Contains(EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture").Issues,
            x => x.Kind == EconomyIssueKind.UnknownSchema && x.Key == "role/101/world_state");
        f.Insert("UPDATE role SET world_state=$0 WHERE id=101", B("{\"VisitedMaps\":\"x\"}"));
        Assert.Contains(EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture").Issues,
            x => x.Kind == EconomyIssueKind.Corruption && x.Key == "role/101/world_state");
    }

    [Fact]
    public void HandAuthoredCompartmentsCountEachHoldingOnce()
    {
        using var f = new Fixture();
        f.Insert("INSERT INTO role(id,currency,mailbox,territory_wars) VALUES(101,$0,$1,$2)",
            "1:10,2:0",
            B("""{"Letters":[{"Id":"open","CreatedTime":1,"RemoveTime":2,"Claimed":false,"Attachments":[{"Entry":3,"Count":2,"Bind":1},{"Entry":1,"Count":5},{"Entry":100,"Count":999}],"Items":[{"Guid":41,"Entry":4,"Stack":1}],"Currency":{"2":7}},{"Id":"history","Claimed":true,"Attachments":[{"Entry":3,"Count":999}]}]}"""),
            B("""{"Epoch":"999:2","Pending":{"123:1:1:5":[{"Entry":3,"Count":1,"Bind":2}],"world-land:123:1:1000:9:1":[{"Entry":3,"Count":1}]},"Paid":["123:1:2:5"]}"""));
        f.Insert("INSERT INTO role(id,currency,deleted_time,mailbox) VALUES(102,$0,1,$1)", "1:20",
            B("""{"Letters":[{"Id":"deleted-mail","Claimed":false,"Attachments":[{"Entry":3,"Count":2}]}]}"""));
        f.Insert("UPDATE role SET task_followup=$0 WHERE id=101", B("""["mvp/kill/1/1/123/456"]"""));
        f.Insert("UPDATE role SET totem_refine_epoch=$0,activity_runtime=$1,pet_books=$2,season_page_meta=$3,equip_plans=$4,card_plans=$5 WHERE id=101",
            B("101:638000000000000000"),
            B("""{"AdventureTreasure":{"PointId":0,"GroupId":0,"ItemId":0,"EventId":0,"Began":0,"PendingMonsterUid":0,"PendingMonsterAward":0,"PendingMonsterId":0}}"""),
            B("""{"123":{"3":999}}"""), B("""{"SeasonId":1,"Baseline":{"11":500},"DungeonScore":800}"""),
            // DBEquipInfo.equipPlans[1].itemsEquipGrid[1]=inventory Guid 1: reference, not a second item.
            new byte[] { 0x3A, 0x0A, 0x08, 0x01, 0x12, 0x06, 0x0A, 0x04, 0x08, 0x01, 0x10, 0x01 },
            // DBStorageHoleInfo.storagePlans[1].cardInfo[1].cardSlotInfo[1].ItemId=1: another reference.
            new byte[] { 0x0A, 0x12, 0x08, 0x01, 0x12, 0x0E, 0x0A, 0x0C, 0x08, 0x01, 0x12, 0x08,
                0x0A, 0x06, 0x08, 0x01, 0x12, 0x02, 0x08, 0x01 });
        f.Exec("""INSERT INTO item VALUES(1,101,3,4,1,0,-2,'{"1":2}',NULL,NULL,0,0,0)""");
        f.Exec("INSERT INTO item VALUES(2,999,4,2,1,0,0,NULL,NULL,NULL,0,0,0)");
        f.Exec("INSERT INTO item VALUES(3,102,3,1,1,0,0,NULL,NULL,NULL,0,0,0)");
        f.Insert("INSERT INTO market_record VALUES('trade_listing','l1',$0)",
            """{"SellerId":101,"Entry":3,"Quantity":3,"Item":{"Guid":21,"Entry":3,"Stack":3}}""");
        f.Insert("INSERT INTO market_record VALUES('trade_preorder','p1',$0)",
            """{"BuyerId":101,"Currency":1,"Quantity":2,"Price":7,"Status":0,"Hidden":true,"Item":{"Entry":3,"Stack":999}}""");
        f.Insert("INSERT INTO market_record VALUES('trade_preorder','p2',$0)",
            """{"BuyerId":101,"Currency":1,"Quantity":100,"Price":999,"Status":3}""");
        f.Insert("INSERT INTO market_record VALUES('stall','s1',$0)",
            """{"Key":"s1","SystemId":20,"Timeout":1,"Stock":[{"Id":1,"Item":{"Guid":31,"Entry":4,"Stack":5}}],"Gains":{"1":999}}""");
        f.Insert("INSERT INTO market_record VALUES('stall','s2',$0)",
            """{"Key":"s2","Owner":101,"Stock":[{"Id":2,"Item":{"Guid":32,"Entry":3,"Stack":1}}],"Gains":{"1":999},"Records":["eyJFbnRyeSI6MywiU3RhY2siOjk5OX0="]}""");
        f.Insert("INSERT INTO market_record VALUES('mail','m1',$0)",
            """{"RoleId":102,"Claimed":false,"Items":[{"Entry":3,"Stack":2}],"Currency":{"1":6}}""");
        f.Insert("INSERT INTO market_record VALUES('trade_history','h1',$0)",
            """{"Item":{"Entry":3,"Stack":1000}}""");
        f.Exec("INSERT INTO market_record VALUES('trade_sequence','seq','1')");
        f.Insert("INSERT INTO auction_item VALUES(10,'source:10',$0)",
            B("""{"Id":10,"SourceKey":"source:10","Entry":4,"Currency":1,"State":1,"Owner":101,"Reserve":9,"Bind":0}"""));
        f.Insert("INSERT INTO auction_item VALUES(11,'source:11',$0)",
            B("""{"Id":11,"SourceKey":"source:11","Entry":4,"Currency":1,"State":3,"Winner":101,"Owner":101,"Reserve":9}"""));
        f.Insert("INSERT INTO auction_item VALUES(12,'source:12',$0)",
            B("""{"Id":12,"SourceKey":"source:12","Entry":4,"Currency":1,"State":3,"Winner":0}"""));
        f.Exec("INSERT INTO auction_prebuy VALUES(1,101,1,8,1,0,1)");
        f.Exec("INSERT INTO auction_prebuy VALUES(2,101,1,999,1,1,0)");
        f.Exec("INSERT INTO auction_bonus VALUES(101,'guild:10',4,0,7)");
        f.Exec("INSERT INTO auction_bonus VALUES(101,'guild:11',999,1,7)");
        f.Insert("INSERT INTO auction_run VALUES(1,$0)", B("""{"Id":1,"Ready":true,"Finalized":false}"""));
        f.Insert("INSERT INTO guild VALUES(7,$0)",
            B("""{"Id":7,"Money":100,"Contribute":8,"Materials":50,"Reputation":9,"AuctionMaterials":6,"Members":{"101":{"CurContribute":1000,"HistoryContribute":5000}}}"""));
        f.Insert("INSERT INTO guild_campfire VALUES(7,$0)",
            B("""{"Day":1,"Exp":500,"Seeds":{"101":[1]},"FuelCount":{"101":2},"DrawnStages":[1],"FruitKills":{"1":3}}"""));
        f.Insert("INSERT INTO guild_market VALUES(1,$0)",
            B("""{"Stalls":[{"Id":"g1","GuildId":7,"TotalCount":10,"RemainCount":7}],"Trades":[{"Count":99}]}"""));
        f.Insert("INSERT INTO guild_caravan_member VALUES(101,$0)",
            B("""{"PendingAwards":[{"GuildId":7,"GuildBooked":false,"Grants":[{"Entry":3,"Count":2},{"Entry":104,"Count":11}]},{"GuildId":7,"GuildBooked":true,"Grants":[{"Entry":102,"Count":3},{"Entry":104,"Count":12}]}]}"""));
        f.Exec("INSERT INTO guild_caravan_version VALUES(7)"); // Version metadata, no second resource holding.
        f.Insert("INSERT INTO territory_wars VALUES(1,$0)",
            B("""{"Epoch":"123:1","WorldDrops":{"1":{"Amount":13}},"Rounds":{"1":{"Settled":false,"Zones":{"5":{"Entries":[{"GuildId":7,"Amount":14,"Legacy":true},{"GuildId":7,"Amount":5,"Legacy":false},{"GuildId":7,"Amount":999,"Occupier":true}]}}},"2":{"Settled":true,"Zones":{"5":{"Entries":[{"GuildId":7,"Amount":999,"Legacy":true}]}}}}}"""));
        f.Insert("INSERT INTO territory_wars_seasons VALUES('122:1',$0)",
            B("""{"Epoch":"122:1","WorldDrops":{"2":{"Amount":2}}}"""));
        f.Insert("INSERT INTO territory_escort VALUES(1,$0)",
            B("""{"SeasonStart":123,"PlayBook":1,"Goods":{"7":20},"Listings":{"1":{"Seller":7,"Count":4,"State":0},"9":{"Seller":7,"Count":999,"State":1}},"Carts":{"1":{"Guild":7,"Count":5,"OriginCount":999}},"Cargo":{"1":{"Picked":false},"2":{"Picked":true}},"Play":{"7":{"ToTransport":999,"Transported":999,"Score":999}},"ScoresByRound":{"7":{"1":999}},"StockRounds":{"7":[1]},"Archives":{"122:1":{"Goods":{"7":3},"Listings":{"2":{"Seller":7,"Count":2,"State":0}}}}}"""));
        f.Exec("INSERT INTO festival_reward VALUES(1,101,9,900,0)");
        f.Insert("INSERT INTO festival_round VALUES(1,$0)",
            B("""{"Cycle":1,"AnswerStart":1,"End":2,"Phase":3,"Question":1,"Deadline":2,"Questions":[11],"Answers":[12],"Candidates":{"101":{"Entered":true,"Rank":1}},"Bets":{"102":999},"Complete":true}"""));
        f.Insert("INSERT INTO activity_eve_global VALUES(7,123,$0)", new byte[] { 0x08, 0x01 }); // DBSeasonEveInfo.progress=1.
        f.Exec("INSERT INTO server_state VALUES('season_start','2026-10-01')");
        f.Exec("INSERT INTO server_state VALUES('season_id','2')");
        f.Exec("INSERT INTO server_state VALUES('playbook','101')");
        f.Exec("INSERT INTO server_state VALUES('instance_sequence','1125899906842625')");
        f.Exec("INSERT INTO server_state VALUES('flying_dragon','GPbN7dYGKgoIrI0GEPbN7dYG')"); // DBFlyingDragonProduceInfo.
        f.Insert("INSERT INTO server_state VALUES('season_history',$0)",
            """{"1":{"Id":1,"PlayBook":101,"Start":"2026-10-01T00:00:00","End":"2026-10-09T00:00:00"}}""");
        f.Insert("INSERT INTO guild_challenge_clear VALUES('clear',1,$0)",
            B("""{"Id":"clear","Personal":{"101":[{"Entry":3,"Count":2,"Bind":1}],"102":[{"Entry":3,"Count":999}]},"Delivered":[102]}"""));

        var snapshot = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        long Total(EconomyAssetKind kind, long id) => snapshot.Holdings.Where(h => h.Asset.Kind == kind && h.Asset.Id == id).Sum(h => h.Quantity);
        Assert.Equal(21, Total(EconomyAssetKind.Item, 3));
        Assert.Equal(10, Total(EconomyAssetKind.Item, 4));
        Assert.Equal(1, Total(EconomyAssetKind.Item, 1258002));
        Assert.Equal(76, Total(EconomyAssetKind.Currency, 1));
        Assert.Equal(7, Total(EconomyAssetKind.Currency, 2));
        Assert.Equal(3, Total(EconomyAssetKind.Currency, 102));
        Assert.Equal(15, Total(EconomyAssetKind.Currency, 133));
        Assert.All(snapshot.Holdings.Where(h => h.Asset.Kind == EconomyAssetKind.Currency),
            holding => Assert.Equal("", holding.Fingerprint));
        Assert.Equal(111, Total(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMoney));
        Assert.Equal(8, Total(EconomyAssetKind.GuildResource, EconomyDenominations.GuildContribution));
        Assert.Equal(71, Total(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMaterials));
        Assert.Equal(9, Total(EconomyAssetKind.GuildResource, EconomyDenominations.GuildReputation));
        Assert.Equal(11, Total(EconomyAssetKind.GuildResource, EconomyDenominations.GuildAuctionMaterials));
        Assert.Equal(34, Total(EconomyAssetKind.EventResource, EconomyDenominations.EscortGoods));
        Assert.Equal(2, snapshot.Holdings.Count(h => h.Location.Compartment == EconomyCompartment.TerritoryBid));
        Assert.Contains(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.TerritoryBid
            && h.Asset.Id == EconomyDenominations.GuildMaterials && h.Quantity == 14);
        Assert.Contains(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.TerritoryBid
            && h.Asset.Id == EconomyDenominations.GuildAuctionMaterials && h.Quantity == 5);
        Assert.Equal(8, Assert.Single(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.AuctionPrebuy).Quantity);
        Assert.Equal(14, Assert.Single(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.TradePreorder).Quantity);
        Assert.Equal(3, Assert.Single(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.CaravanPending
            && h.Asset.Kind == EconomyAssetKind.Currency).Quantity);
        Assert.Single(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.EscortCargo);
        Assert.Equal(2, snapshot.Holdings.Count(h => h.Location.Compartment == EconomyCompartment.Stall));
        Assert.DoesNotContain(snapshot.Holdings, h => h.Quantity == 999 || h.Quantity == 1000 || h.Quantity == 5000);
        Assert.Contains(snapshot.Holdings, h => h.Location.Owner.Kind == EconomyOwnerKind.Recovery && h.Location.Key.StartsWith("item/2/"));
        Assert.Contains(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.Currency
            && h.Location.Owner.Kind == EconomyOwnerKind.Recovery && h.Location.Owner.Id == "role:102" && h.Quantity == 20);
        Assert.Contains(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.LegacyMail
            && h.Location.Owner.Kind == EconomyOwnerKind.Recovery && h.Location.Owner.Id == "role:102");
        Assert.Contains(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.Mail
            && h.Location.Owner.Kind == EconomyOwnerKind.Recovery && h.Location.Owner.Id == "role:102");
        Assert.Contains(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.Inventory
            && h.Location.Owner.Kind == EconomyOwnerKind.Recovery && h.Location.Owner.Id == "role:102"
            && h.Location.Instance == "3");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.RecoveryDebt && i.Key == "role/102/deleted_time");
        Assert.Contains(snapshot.Holdings, h => h.Location.Key.Contains("grid--2") && h.Location.Instance == "1");
        Assert.Equal(2, snapshot.Holdings.Count(h => h.Location.Compartment == EconomyCompartment.TerritoryPending
            && h.Asset.Epoch == "" && h.Location.Key.Contains("123:1")));
        Assert.All(snapshot.Holdings.Where(h => h.Asset.Kind == EconomyAssetKind.Item), h => Assert.NotEmpty(h.Fingerprint));
        Assert.Null(snapshot.Cut.CommitSequence);
        Assert.Equal(1, Assert.Single(snapshot.Metrics, m => m.Repository == "guild_caravan_version").Rows);
        Assert.Equal(1, Assert.Single(snapshot.Metrics, m => m.Repository == "activity_eve_global").Blobs);
        Assert.Equal(1, Assert.Single(snapshot.Metrics, m => m.Repository == "festival_round").Blobs);
        Assert.Equal(6, Assert.Single(snapshot.Metrics, m => m.Repository == "server_state").Rows);
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.RecoveryDebt && i.Key.StartsWith("festival_reward/"));
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.RecoveryDebt && i.Detail.Contains("EXP grant"));
        Assert.DoesNotContain(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption);
        Assert.DoesNotContain(snapshot.Issues, i => i.Kind is EconomyIssueKind.Coverage or EconomyIssueKind.UnknownSchema);
        Assert.DoesNotContain(snapshot.Issues, i => i.Key.Contains("totem_refine_epoch") || i.Key.Contains("AdventureTreasure"));
    }

    [Fact]
    public void CapturedJourneyActIsProgressionAndUnknownNestedBalanceStillFailsClosed()
    {
        using var f = new Fixture();
        const string state = """{"PlayBook":101,"PendingJourney":{"journey/dungeon/realm/room/101":{"Source":"journey/dungeon/realm/room/101","SceneEntry":17001,"ActId":10114,"NodeId":107,"ActivityStart":100,"ActivityEnd":200,"PlayBook":101}}}""";
        f.Insert("INSERT INTO role(id,currency,dungeons) VALUES(101,'1:7',$0)", B(state));
        var snapshot = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.DoesNotContain(snapshot.Issues, i => i.Kind is EconomyIssueKind.UnknownSchema or EconomyIssueKind.Corruption);
        Assert.Equal(7, Assert.Single(snapshot.Holdings).Quantity);
        Assert.Equal(EconomySnapshotReader.ExpectedFormatManifestHash, EconomySnapshotReader.FormatManifestHash);

        f.Insert("UPDATE role SET dungeons=$0 WHERE id=101", B(state.Replace("\"ActId\":10114", "\"ActId\":10114,\"FutureBalance\":999")));
        var unknown = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.Contains(unknown.Issues, i => i.Kind == EconomyIssueKind.UnknownSchema && i.Key == "role/101/dungeons");
    }

    [Fact]
    public void CorruptionAndUnknownCoverageSurviveIncompleteSchema()
    {
        using var f = new Fixture();
        f.Exec("ALTER TABLE role ADD COLUMN future_balance INTEGER");
        f.Insert("INSERT INTO role(id,currency,mailbox) VALUES(101,$0,$1)", "999:5", B("""{"Letters":null}"""));
        f.Insert("UPDATE role SET task_followup=$0 WHERE id=101", B("""{"unexpected_balance":100}"""));
        f.Exec("INSERT INTO item VALUES(1,101,3,-2,0,0,0,NULL,NULL,NULL,0,0,0)");
        f.Insert("INSERT INTO market_record VALUES('new_wallet','n1',$0)", """{"Balance":100}""");
        f.Insert("INSERT INTO market_record VALUES('trade_preorder','overflow',$0)",
            """{"BuyerId":101,"Quantity":9223372036854775807,"Price":2,"Currency":1,"Status":0}""");
        f.Insert("INSERT INTO activity_eve_global VALUES(7,123,$0)", new byte[] { 0xA0, 0x06, 0x01 });
        f.Exec("INSERT INTO server_state VALUES('future_paid_reserve','100')");
        var snapshot = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.UnknownSchema && i.Key == "schema/role.future_balance");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.UnknownSchema && i.Key == "market/new_wallet/n1");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key.StartsWith("item/1"));
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "market/trade_preorder/overflow");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Coverage && i.Key.Contains("currency/999"));
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.UnknownSchema && i.Key == "activity_eve_global/7/123");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.UnknownSchema && i.Key == "server_state/future_paid_reserve");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "role/101/task_followup");
    }

    [Fact]
    public void UnknownProtobufFieldAndLegacyDuplicateAreVisible()
    {
        using var f = new Fixture();
        f.Insert("INSERT INTO role(id,currency,mailbox,equip_plans) VALUES(101,'',$0,$1)",
            B("""{"Letters":[{"Id":"legacy-1","Claimed":true,"Items":[{"Entry":3,"Stack":99}]}]}"""),
            new byte[] { 0xA0, 0x06, 0x01 }); // Field 100, absent from DBEquipInfo.
        f.Insert("INSERT INTO market_record VALUES('mail','sql-row',$0)",
            """{"Id":"legacy-1","RoleId":101,"Claimed":false,"Items":[{"Entry":3,"Stack":2}]}""");
        var snapshot = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.UnknownSchema && i.Key == "role/101/equip_plans");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "market/mail/sql-row");
        Assert.Equal(2, snapshot.Holdings.Single(h => h.Location.Compartment == EconomyCompartment.LegacyMail).Quantity);
    }

    [Fact]
    public void EmptyCompleteBaselineIsMeasuredWithoutWritingDatabase()
    {
        using var f = new Fixture();
        f.Exec("INSERT INTO role(id,currency) VALUES(101,'')");
        f.Db.Close();
        byte[] before = File.ReadAllBytes(f.PathName);
        var first = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        var second = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.Equal(before, File.ReadAllBytes(f.PathName));
        Assert.Empty(first.Issues);
        Assert.Empty(first.Holdings);
        Assert.Equal(first.Cut.SchemaVersion, second.Cut.SchemaVersion);
        Assert.Null(first.Cut.CommitSequence);
        Assert.NotEmpty(first.UncoveredMutationFamilies);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void LegacyAuctionBonusUsesClaimConfigCurrency(int lotCurrency, int claimCurrency)
    {
        using var f = new Fixture();
        f.Exec("INSERT INTO role(id,currency) VALUES(101,'')");
        f.Insert("INSERT INTO auction_item VALUES(20,'lot:20',$0)", B($$"""{"Id":20,"SourceKey":"lot:20","Entry":4,"Currency":{{lotCurrency}},"State":3,"Winner":101}"""));
        f.Exec("INSERT INTO auction_bonus VALUES(101,'guild:20',7,0,7)");
        var catalog = new EconomyCatalog(new Dictionary<long, EconomyAssetKind>
        { [1] = EconomyAssetKind.Currency, [2] = EconomyAssetKind.Currency, [4] = EconomyAssetKind.Item }, claimCurrency);
        var snapshot = EconomySnapshotReader.Read(f.PathName, catalog, "realm-fixture");
        var bonus = Assert.Single(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.AuctionBonus);
        Assert.Equal(claimCurrency, bonus.Asset.Id);
        Assert.Equal(7, bonus.Quantity);
    }

    [Theory]
    [InlineData("auction:bonus:guild:20:101")]
    [InlineData("auction:20:dividend:101")]
    public void PaidLegacyBonusMailCannotAlsoRemainUnclaimed(string deliveryKey)
    {
        using var f = new Fixture();
        f.Insert("INSERT INTO role(id,currency,mailbox) VALUES(101,'',$0)",
            B($$"""{"Letters":[{"Id":"claimed","DeliveryKey":"{{deliveryKey}}","Claimed":true}]}"""));
        f.Insert("INSERT INTO auction_item VALUES(20,'lot:20',$0)",
            B("""{"Id":20,"SourceKey":"lot:20","Entry":4,"Currency":1,"State":3,"Winner":101}"""));
        f.Exec("INSERT INTO auction_bonus VALUES(101,'guild:20',7,0,7)");
        var snapshot = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "auction/bonus/101/guild:20");
    }

    [Fact]
    public void MalformedPackedProtobufAndSqlRowDoNotAbortTheSnapshot()
    {
        using var f = new Fixture();
        f.Insert("INSERT INTO role(id,currency,guides,library) VALUES(101,'',$0,$1)",
            new byte[] { 0x0A, 0x01, 0x80 }, // DBGuideInfo.finishedGuideIds, truncated packed varint.
            new byte[] { 0x22, 0x08, 0x08, 0x01, 0x12, 0x04, 0x08, 0x01, 0x10, 0x63 }); // LibraryPageInfo.state=99.
        f.Exec("INSERT INTO auction_bonus VALUES('bad','guild:20',7,0,7)");
        f.Insert("INSERT INTO auction_item VALUES(20,'lot:20',$0)",
            B("""{"Id":20,"SourceKey":"lot:20","Entry":4,"Currency":1,"State":1,"Owner":101}"""));
        f.Exec("INSERT INTO auction_bonus VALUES(101,'guild:20',7,0,7)");
        var snapshot = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "role/101/guides");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.UnknownSchema && i.Key == "role/101/library");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key.StartsWith("auction_bonus/row/"));
        Assert.Equal(7, Assert.Single(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.AuctionBonus).Quantity);
    }

    [Fact]
    public void ArchiveEpochAndSettledReserveContradictionsAreDiagnosed()
    {
        using var f = new Fixture();
        f.Exec("INSERT INTO role(id,currency) VALUES(101,'')");
        f.Insert("INSERT INTO territory_wars_seasons VALUES('122:1',$0)", B("""{"Epoch":"121:1"}"""));
        f.Insert("INSERT INTO festival_round VALUES(2,$0)", B("""{"Cycle":1,"Candidates":{"101":{"Entered":true}}}"""));
        f.Insert("INSERT INTO festival_round VALUES(3,$0)", B("""{"Cycle":3,"Phase":{},"Candidates":{}}"""));
        f.Insert("INSERT INTO festival_round VALUES(4,$0)", B("""{"Cycle":4,"Phase":1,"Candidates":{"101":{"Entered":{}}}}"""));
        f.Insert("INSERT INTO auction_item VALUES(20,'lot:20',$0)",
            B("""{"Id":20,"SourceKey":"lot:20","Entry":4,"Currency":1,"State":3,"Owner":101,"Reserve":9,"Winner":0}"""));
        f.Insert("INSERT INTO auction_item VALUES(21,'lot:21',$0)",
            B("""{"Id":21,"SourceKey":"lot:21","Entry":4,"Currency":1,"State":0,"Owner":101}"""));
        var snapshot = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "territory_wars_seasons/122:1");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "festival_round/2");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "festival_round/3");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "festival_round/4");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "auction/lot/20");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "auction/lot/21");
        Assert.Equal(9, Assert.Single(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.AuctionReserve
            && h.Location.Owner.Kind == EconomyOwnerKind.Recovery).Quantity);
    }

    [Fact]
    public void ConflictingItemExpiryAliasesAreCorruption()
    {
        using var f = new Fixture();
        f.Exec("INSERT INTO role(id,currency) VALUES(101,'')");
        f.Insert("INSERT INTO market_record VALUES('trade_listing','expiry',$0)",
            """{"SellerId":101,"Entry":3,"Quantity":1,"Item":{"Entry":3,"Stack":1,"BindExpire":100,"ExpireTimeStamp":200}}""");
        var snapshot = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "market/trade_listing/expiry");
    }

    [Fact]
    public void MalformedDeletionStateQuarantinesRoleAssets()
    {
        using var f = new Fixture();
        f.Insert("INSERT INTO role(id,currency,deleted_time,mailbox) VALUES(103,'1:5',$0,$1)", "broken",
            B("""{"Letters":[{"Id":"m","Claimed":false,"Attachments":[{"Entry":3,"Count":2}]}]}"""));
        var snapshot = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption && i.Key == "role/103/deleted_time");
        Assert.Equal(2, snapshot.Holdings.Count(h => h.Location.Owner.Kind == EconomyOwnerKind.Recovery
            && h.Location.Owner.Id == "role:103"));
        Assert.DoesNotContain(snapshot.Holdings, h => h.Location.Owner.Kind == EconomyOwnerKind.Role && h.Location.Owner.Id == "103");
    }

    [Fact]
    public void ContradictoryPaidAndPendingTerritoryGrantIsDiagnosedAndPreserved()
    {
        using var f = new Fixture();
        f.Insert("INSERT INTO role(id,currency,territory_wars) VALUES(101,'',$0)",
            B("""{"Epoch":"123:1","Pending":{"123:1:1:5":[{"Entry":3,"Count":999}]},"Paid":["123:1:1:5"]}"""));
        var snapshot = EconomySnapshotReader.Read(f.PathName, Catalog, "realm-fixture");
        Assert.Contains(snapshot.Issues, i => i.Kind == EconomyIssueKind.Corruption
            && i.Key == "role/101/territory_wars/pending/123:1:1:5");
        Assert.Equal(999, Assert.Single(snapshot.Holdings, h => h.Location.Compartment == EconomyCompartment.TerritoryPending).Quantity);
    }

    [Fact]
    public void CatalogPinsAllDenominationConfigBytes()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "e00-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "ItemConfig.json"), """{"1":{"_iType":1},"3":{"_iType":5}}""");
            foreach (string name in new[] { "AuctionConfig.json", "GuildSettingConfig.json", "GuildTransportConfig.json", "GVGConfig.json" })
                File.WriteAllText(Path.Combine(directory, name), "{}");
            File.WriteAllText(Path.Combine(directory, "PublicParamConfig.json"), """{"AUCTION_CURRENCY":1}""");
            var first = EconomyCatalog.Load(directory);
            Assert.True(first.TryAsset(1, out var money)); Assert.Equal(EconomyAssetKind.Currency, money.Kind);
            Assert.True(first.TryAsset(3, out var item)); Assert.Equal(EconomyAssetKind.Item, item.Kind);
            Assert.False(first.TryAsset(999, out _));
            Assert.Equal(first.Version, EconomyCatalog.Load(directory).Version);
            File.WriteAllText(Path.Combine(directory, "AuctionConfig.json"), "{\"1\":{}}");
            Assert.NotEqual(first.Version, EconomyCatalog.Load(directory).Version);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
