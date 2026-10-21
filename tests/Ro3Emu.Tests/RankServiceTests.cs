using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Tests;

/// <summary>
/// RankService over the micro gate (LC_DAL_Rank.lua: rangeUserRank / queryUserRank / getUserRankSurrounding), fed by
/// the committed state of roles that are not online.
/// </summary>
public sealed class RankServiceTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-ranks-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _httpUrl = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = http;
            o.MicroGatePort = micro;
            o.GameGatePort = game;
        });
        await _app.StartAsync();
        _httpUrl = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [ClientDataFact]
    public async Task EmptyAndUnknownBoardsAnswerOkWithoutRows()
    {
        await using var reader = await Reader();
        foreach (var rankId in new[] { "1:1011", "1:9999", "1:1011:3", "1:1053" })
        {
            var reply = await reader.MicroPositionalAsync("RankService", "rangeUserRank", [rankId, 1, 200]);
            Assert.Equal(true, ((object?[])reply["Ret"]!)[0]);
            Assert.Empty(Rows(reply));
            Assert.Equal(0L, Convert.ToInt64(reply["Count"]));
        }
        var bad = await reader.MicroPositionalAsync("RankService", "rangeUserRank", ["1011", 1, 200]);
        Assert.Equal(false, ((object?[])bad["Ret"]!)[0]);
    }

    [ClientDataFact]
    public async Task RoleBoardsRankOfflineRolesFromCommittedState()
    {
        var roles = Service<RoleStore>();
        long alice = Role("Alice", 1100), bob = Role("Bob", 2100), carol = Role("Carol", 4200), dave = Role("Dave", 4210);
        // Spirit Tower: Bob and Alice both cleared floor 5, Bob faster; Carol floor 3; Dave never climbed.
        roles.SaveTower(alice, Tower(5, remain: 20, complete: 2000).ToByteArray());
        roles.SaveTower(bob, Tower(5, remain: 40, complete: 3000, pets: [7001, 0]).ToByteArray());
        roles.SaveTower(carol, Tower(3, remain: 0, complete: 1000).ToByteArray());
        var pets = new DBPetInfo();
        pets.ListPet[7001] = new PetData { PetId = 7001, Level = 12 };
        roles.SavePets(bob, pets.ToByteArray());
        roles.SaveTraining(alice, new DBTrainingGroundInfo { MaxDps = 500 }.ToByteArray());
        roles.SaveTraining(dave, new DBTrainingGroundInfo { MaxDps = 900 }.ToByteArray());
        var wardrobe = new DBWardRobeInfo();
        wardrobe.AllLightUpAppearances[10030417] = new DBAppearanceInfo { AppearanceId = 10030417 };
        wardrobe.AllLightUpAppearances[10030419] = new DBAppearanceInfo { AppearanceId = 10030419 };
        wardrobe.AllLightUpAppearances[10030425] = new DBAppearanceInfo { AppearanceId = 10030425, OverdueTimeMs = 1 };
        roles.SaveWardrobe(carol, wardrobe.ToByteArray());
        roles.SaveTimedBattle(bob, JsonSerializer.SerializeToUtf8Bytes(new TimedBattleService.State
        {
            ChickenMatches = 2, ChickenScore = 150, DragonSettled = [1], DragonRank = 2, DragonRankScore = 30,
        }));

        await using var reader = await Reader();
        var tower = await reader.MicroPositionalAsync("RankService", "rangeUserRank", ["1:1011", 1, 200]);
        Assert.Equal(3L, Convert.ToInt64(tower["Count"]));
        Assert.Equal(new[] { (bob, 1L), (alice, 2L), (carol, 3L) }, Rows(tower).Select(r => (Id(r), Convert.ToInt64(r["Rank"]))).ToArray());
        var top = Rows(tower)[0];
        Assert.Equal(new[] { 5L, 3000L }, Values(top));
        var climb = LeaderBoardUserDada.Parser.ParseFrom((byte[])top["GameData"]!).ClimbTowerData;
        Assert.Equal(5, climb.TowerLayer);
        Assert.Equal(20, climb.DurationTime);
        Assert.Equal(new[] { 7001L }, climb.PetIds.ToArray());
        Assert.Equal(12, Assert.Single(climb.PetDatas).Level);

        // queryUserRank for the local player (_proessLeaderBoardSync -> SendForGetUserRank).
        var self = await reader.MicroPositionalAsync("RankService", "queryUserRank", ["1:1011", new object[] { carol.ToString() }]);
        Assert.Equal(3L, Convert.ToInt64(self["Count"]));
        Assert.Equal(3L, Convert.ToInt64(Assert.Single(Rows(self))["Rank"]));
        var absent = await reader.MicroPositionalAsync("RankService", "queryUserRank", ["1:1011", new object[] { dave.ToString() }]);
        Assert.Empty(Rows(absent));

        // getUserRankSurrounding {RankId, UserId, Before, After}.
        var near = await reader.MicroAsync("RankService", "getUserRankSurrounding",
            new() { ["RankId"] = "1:1011", ["UserId"] = alice.ToString(), ["Before"] = 1, ["After"] = 5 });
        Assert.Equal(new[] { 1L, 2L, 3L }, Rows(near).Select(r => Convert.ToInt64(r["Rank"])).ToArray());
        var notRanked = await reader.MicroAsync("RankService", "getUserRankSurrounding",
            new() { ["RankId"] = "1:1011", ["UserId"] = dave.ToString(), ["Before"] = 2, ["After"] = 2 });
        Assert.Empty(Rows(notRanked));

        // Dummy Challenge: overall plus the root job's board (PublicParamConfig.TrainingRootJob2LdId).
        Assert.Equal(new[] { dave, alice }, Rows(await Range(reader, 1201)).Select(Id).ToArray());
        Assert.Equal(new[] { alice }, Rows(await Range(reader, 1202)).Select(Id).ToArray());
        var swordsman = Assert.Single(Rows(await Range(reader, 1204)));
        Assert.Equal(dave, Id(swordsman));
        var common = LeaderBoardUserDada.Parser.ParseFrom((byte[])swordsman["GameData"]!).UserCommonData;
        Assert.Equal(4210L, common.UserDefineData[(int)LeaderBoardSelfDefineDataType.LdCarrer]);

        // Fashion Rating: permanent appearances only (10 + 10).
        Assert.Equal(new[] { 20L, 0L }, Values(Assert.Single(Rows(await Range(reader, 1301)))));
        // Survivor total points and Dragon Coffin total rank points (level 1 limit 100 + 30).
        Assert.Equal(150L, Values(Assert.Single(Rows(await Range(reader, 1052))))[0]);
        Assert.Equal(130L, Values(Assert.Single(Rows(await Range(reader, 1601))))[0]);
    }

    [ClientDataFact]
    public async Task GuildPrestigeBoardCarriesGveDataAndGuildSnapByGuildId()
    {
        long alice = Role("Alice", 1100), carol = Role("Carol", 4200);
        var guilds = Service<GuildStore>();
        long asters = Guild(guilds, "Asters", alice, "Alice", 50, activity: 300);
        long blades = Guild(guilds, "Blades", carol, "Carol", 80);
        Guild(guilds, "Quiet", Role("Erin", 1100), "Erin", 0);

        await using var reader = await Reader();
        var board = await Range(reader, 1030);
        Assert.Equal(new[] { blades, asters }, Rows(board).Select(Id).ToArray());
        var gve = LeaderBoardUserDada.Parser.ParseFrom((byte[])Rows(board)[1]["GameData"]!).GveData;
        Assert.Equal(("Asters", "Alice", alice, 1L), (gve.GuildName, gve.GuildOwnerName, gve.GuildOwnerId, gve.MemberCount));

        // 1031 ranks this week's Guild Activity; a guild without any is not listed.
        Assert.Equal(new[] { asters }, Rows(await Range(reader, 1031)).Select(Id).ToArray());

        // The local player's query resolves to its guild's row (inferred footer mapping).
        var mine = await reader.MicroPositionalAsync("RankService", "queryUserRank", ["1:1030", new object[] { alice.ToString() }]);
        Assert.Equal(asters, Id(Assert.Single(Rows(mine))));

        // The cell binds GuildSnap by guild id through UserDataService.
        var attributes = await reader.GetRoleAttributesAsync(blades);
        var entry = (Dictionary<object, object?>)Assert.Single((object?[])attributes["Results"]!)!;
        var fields = (Dictionary<object, object?>)entry["Data"]!;
        Assert.False(fields.ContainsKey("Base"));
        var snap = GuildSnapInfo.Parser.ParseFrom((byte[])fields["GuildSnap"]!);
        Assert.Equal(("Blades", "Carol", 80L), (snap.Name, snap.LeaderName, snap.Reputation));
    }

    [ClientDataFact]
    public async Task EquipmentBoardRanksInstancesAndHeroBoardsRankPower()
    {
        long alice = Role("Alice", 1100), carol = Role("Carol", 4200), dave = Role("Dave", 4210);
        var items = Service<ItemStore>();
        var weak = Equip(alice, 10, appraised: true);
        var strong = Equip(carol, 20, appraised: true);
        var unappraised = Equip(dave, 99, appraised: false);
        Emblem(alice, 1830101);
        Emblem(carol, 1850101);

        await using var reader = await Reader();
        var board = await Range(reader, 1401);
        Assert.Equal(new[] { strong.Guid, weak.Guid }, Rows(board).Select(Id).ToArray());
        var meta = LeaderBoardUserDada.Parser.ParseFrom((byte[])Rows(board)[1]["GameData"]!).EquipMetaData;
        Assert.Equal(alice, meta.UserId);
        Assert.Equal(weak.Guid, ItemBaseInfo.Parser.ParseFrom(meta.ItemBaseInfo).ItemGuid);
        var ranks = Service<RankService>();
        Assert.Equal(2, await ranks.EquipRankAsync(RankService.Equipment, weak.Guid));
        Assert.Equal(0, await ranks.EquipRankAsync(RankService.Equipment, unappraised.Guid));
        Assert.Equal(0, await ranks.EquipRankAsync(1011, strong.Guid));

        // Hero Registry: 1101 all jobs, 1103 swordsman line (1100), 1104 (4100).
        Assert.Equal(new[] { carol, alice }, Rows(await Range(reader, 1101)).Select(Id).ToArray());
        Assert.Equal(new[] { alice }, Rows(await Range(reader, 1103)).Select(Id).ToArray());
        Assert.Equal(new[] { carol }, Rows(await Range(reader, 1104)).Select(Id).ToArray());

        InvItem Equip(long role, long value, bool appraised)
        {
            var item = new InvItem
            {
                Entry = 2010120, Stack = 1, Pack = 1, Sort = 1,
                Attrs = new() { [3] = appraised ? 1 : 0, [30] = 1, [60] = value }
            };
            items.Insert(role, item);
            return item;
        }

        void Emblem(long role, int entry)
        {
            var item = new InvItem { Entry = entry, Stack = 1, Pack = 5, Sort = 1, Attrs = new() { [1001] = 1 } };
            items.Insert(role, item);
            var state = new EmblemService.EmblemState { CurrentPlan = 0 };
            state.Plans[0] = new EmblemService.Plan { Slots = { [1] = item.Guid } };
            Service<RoleStore>().SaveEmblems(role, JsonSerializer.SerializeToUtf8Bytes(state));
        }
    }

    private static DBClimTower Tower(int floor, int remain, long complete, int[]? pets = null)
    {
        var tower = new DBClimTower { LayerConfID = floor, RemainDuration = remain, CompleteTime = complete };
        tower.PetIds.Add(pets ?? [0, 0, 0, 0]);
        return tower;
    }

    private long Role(string name, int career) => Service<RoleStore>().Create("rank" + name.ToLowerInvariant(), 1, name, 1, career, 1)!.Id;

    private static long Guild(GuildStore store, string name, long leader, string leaderName, long reputation, long activity = 0)
    {
        // Guild Activity counts for the week that began Monday 05:00 local (GuildService.Activity).
        var local = DateTimeOffset.Now;
        var day = local.AddHours(-5).Date;
        long week = new DateTimeOffset(day.AddDays(-(((int)day.DayOfWeek + 6) % 7)).AddHours(5), local.Offset).ToUnixTimeSeconds();
        var guild = new GuildStore.State
        {
            Name = name, Reputation = reputation, Announce = "Join " + name,
            CurActivity = activity, ActivityWeek = week
        };
        guild.Members[leader] = new GuildStore.Member { RoleId = leader, Name = leaderName, Official = 1, Career = 1100, Level = 30 };
        Assert.True(store.Save(guild));
        return guild.Id;
    }

    private static Task<Dictionary<object, object?>> Range(BotClient reader, int board) =>
        reader.MicroPositionalAsync("RankService", "rangeUserRank", [$"1:{board}", 1, 200]);

    private static List<Dictionary<object, object?>> Rows(Dictionary<object, object?> reply) =>
        ((object?[])reply["RankList"]!).Cast<Dictionary<object, object?>>().ToList();

    private static long Id(Dictionary<object, object?> row) => long.Parse((string)row["UserId"]!);

    private static long[] Values(Dictionary<object, object?> row) =>
        ((object?[])((Dictionary<object, object?>)row["Score"]!)["Values"]!).Select(Convert.ToInt64).ToArray();

    private async Task<BotClient> Reader()
    {
        var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("rankreader", "pw");
        return bot;
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
}
