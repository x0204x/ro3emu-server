using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// A realm wiped for the closed alpha: a new DataDir that holds only the realm's own state (no msgtable.tsv, the client
/// export stays in GameDataDir), OpenContent on and the season starting today. Two things broke there on 12.10 while the
/// old realm (season day 13) was fine: the shop's Fashion tab stayed empty (82338 went unanswered) and the top bar's
/// Season button did nothing (the season's first week is a hidden stage; Ro3Emu.ClientHooks.ServerDayGates).
/// </summary>
public sealed class FreshRealmTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-fresh-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = RepoData();
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.OpenContent.Enabled = true;
            o.SeasonStart = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static string RepoData()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) return Path.Combine(d.FullName, "data");
        throw new DirectoryNotFoundException("repository root");
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", Operator.Command(line))).EnsureSuccessStatusCode();
    }

    [ClientDataFact]
    public async Task TheFashionTabIsAnsweredOnAFreshRealm()
    {
        Assert.False(File.Exists(Path.Combine(_dataDir, "msgtable.tsv")));
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 60");
        // UI_Sys_AppearanceShopWidgetCtrl.OnOpen sends 82338 for enum_ShopType.Appearance (7) and draws categories
        // and items only from the answer (LC_DAL_Shop.CommodityGetInfoResp -> CommodityTimeMap).
        await alice.SendGameAsync(ShopService.CommodityGetInfo, new CommodityGetInfoReq { CommodityType = 7 });
        var info = await Next(alice, ShopService.CommodityGetInfo, CommodityGetInfoResp.Parser);
        Assert.Empty(info.CommodityInfos);
        // The tab's categories need their ShopConfig functions open: Recommended 70501 and Costume 70502 (level 10).
        var open = await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser,
            m => m.FunctionOpenInfos.ContainsKey(70502));
        Assert.True(open.FunctionOpenInfos[70501].All);
        Assert.True(open.FunctionOpenInfos[70502].All);
    }

    [Fact]
    public void NoFashionCommodityNeedsASaleWindow()
    {
        // An empty 82338 map is right only while no commodity is dated: IsValidTime asks the map for rows with both
        // _kStartTime and _kEndTime, Lua_CFG_ShopCommodityConfig for rows with a _kTimeLimit.
        foreach (var row in Service<GameData>().Table("ShopCommodityConfig").Values)
            foreach (var field in new[] { "_kStartTime", "_kEndTime", "_kTimeLimit" })
                Assert.False(row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0,
                    $"commodity {GameData.Int(row, "_iID")} has {field}");
    }

    [ClientDataFact]
    public void TheMessageTableComesFromGameDataWhenTheRealmDirHasNone()
    {
        Assert.NotNull(Service<MsgCatalog>().EmptyResponse(ShopService.CommodityGetInfo));
        var options = new ServerOptions { DataDir = _dataDir, GameDataDir = RepoData() };
        var catalog = new MsgCatalog(Options.Create(options), NullLogger<MsgCatalog>.Instance);
        Assert.Equal("COMMODITY_GET_INFO", catalog.Name(ShopService.CommodityGetInfo));
        Assert.False(catalog.UnknownToClient(ShopService.CommodityGetInfo));
    }

    [ClientDataFact]
    public async Task OnDayOneTheSeasonIsInItsHiddenFirstStageAndTheServerSignalsOpenContent()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 60");
        var season = await Next(alice, SeasonService.SeasonSync, DBSeasonInfoIncrement.Parser);
        long start = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
        Assert.Equal(101, season.Current.All.PlayBookId);
        Assert.Equal(start, season.Current.All.StartTime);
        Assert.Equal(101001, season.Current.All.StageId);
        var time = await Next(alice, SeasonService.TimeInfoNotify, SeasonTimeInfoNotify.Parser);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // The client's stage list skips 101001 (_iHide 1), so on days 1-7 LC_SeasonHelper.GetCurrentStageId is nil and
        // the first visible stage, 101002, has not started: the case ClientHooks.ServerDayGates.CurrentStage answers.
        var data = Service<GameData>();
        Assert.Equal(1, GameData.Int(data.Row("MultiverseStage", 101001)!.Value, "_iHide"));
        Assert.Equal(0, GameData.Int(data.Row("MultiverseStage", 101002)!.Value, "_iHide"));
        Assert.InRange(now, time.StageTimeInfo[101001].StartTime, time.StageTimeInfo[101001].EndTime - 1);
        Assert.True(time.StageTimeInfo[101002].StartTime > now);
        // The season page 69 is open (OpenContent forces it since the 20 Oct 2026 client closed it at level 9999), and the
        // hook's "server opens content" signal holds from the first day: a function no level reaches (7201, [1002,9999])
        // is reported open; the original never reports one.
        var open = await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser,
            m => m.FunctionOpenInfos.ContainsKey(69));
        Assert.True(open.FunctionOpenInfos[69].All);
        var functions = Service<FunctionService>();
        var session = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
            Service<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null)));
        Assert.True(await Service<GameLoop>().InvokeAsync(() => Task.FromResult(functions.IsOpen(session, 7201))));
        long condition = GameData.Int(data.Row("FunctionConfig", 7201)!.Value, "_kOpenCondition");
        var sub = data.Row("LC_ConditionList", condition)!.Value.GetProperty("m_kSubConditionList")[0].GetProperty("m_kData");
        Assert.Equal(1002, (long)GameData.Number(sub[0])!);
        Assert.True((long)GameData.Number(sub[1])! >= 999);
    }

    private GameSession Session(string account) => Service<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);

    [ClientDataFact]
    public async Task OnDayOneTheSeasonPageActivitiesAreOpenBelowTheirLevel99Gate()
    {
        // The season page lists MultiverseStage._kStageActivity per chapter; each activity's own condition is [1002,99]
        // (the final level cap, day 198), so the client fell back to its config windows ("Opens in 13 days").
        Assert.Equal([105, 106, 107, 109, 110, 111, 115],
            Service<NewActivityService>().StageActivities(101).Order().ToArray());
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 60");
        var sync = await Next(alice, NewActivityService.ActivitySync, DBNewActivityInfoIncrement.Parser, m => m.SyncAllMark);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), open = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
        var infos = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
            Session("alice").NewActivities!.ActivityInfo.ToDictionary(k => k.Key, k => k.Value.Clone())));
        // LC_DAL_NewActivity keeps an activity for the page only while its state is a TimeLimitConfig state ("Start"),
        // and the chapter card is Ongoing when the server's window holds now (Ode to Echoes 110, Divine Realm
        // Speedrun 115, Genesis Movement 111 on chapter one).
        foreach (long id in new long[] { 105, 106, 109, 110, 111, 115 })
        {
            Assert.Contains(id, sync.ActivityInfo.Keys);
            Assert.False(infos[id].IsShielded, $"activity {id} shielded");
            Assert.Equal("Start", infos[id].State);
            Assert.Equal(open, infos[id].CycleStartTime);
            Assert.True(infos[id].CycleEndTime > now + 365L * 86400, $"activity {id} closes");
        }
        // 107 Middle Season keeps its settlement window (KeepTimeLimits 1021, from stage 101003).
        Assert.Equal("_close", infos[107].State);
        Assert.False(infos[107].IsShielded);
        // Not a season page activity: the Pre donation 102 keeps its level-99 gate.
        Assert.True(infos[102].IsShielded);
    }

    [ClientDataFact]
    public async Task OnDayOneEveryExplorationChapterAcceptsNotesAndTheStageClockIsUntouched()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 60");
        await Gm("alice", "!item 1113001 1");
        var time = await Next(alice, SeasonService.TimeInfoNotify, SeasonTimeInfoNotify.Parser);
        long start = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
        // The clock stays the season's own: chapter one (101002) starts on day 8, chapter two after it.
        Assert.Equal(start + 7 * 86400, time.StageTimeInfo[101002].StartTime);
        Assert.True(time.StageTimeInfo[101003].StartTime >= start + 14 * 86400);
        // ExplorePhaseConfig 101 runs from 101002 to 101003; its first node 1011 takes item 1113001 first.
        await alice.SendGameAsync(NewActivityService.Common, new CommonActivityReq
        {
            ActivityId = 111, RequestId = 1,
            FillPhaseNodeReq = new WorldExplorationFillPhaseNodeReq { PhaseCfgId = 101, NodeCfgId = 1011, ItemId = 1113001, Position = 1 },
        });
        var filled = await Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 1);
        Assert.Equal(0, filled.Result);
        Assert.Equal(0, filled.FillPhaseNodeRes.Err);
        // Phase 103 opens on day 12 by the config; under OpenContent it takes its first note today.
        var data = Service<GameData>();
        long node = (long)GameData.Number(data.Row("ExplorePhaseConfig", 103)!.Value.GetProperty("_kNodes")[0])!;
        long piece = (long)GameData.Number(data.Row("ExploreNodeConfig", node)!.Value.GetProperty("_kPiecesID")[0])!;
        await Gm("alice", $"!item {piece} 1");
        await alice.SendGameAsync(NewActivityService.Common, new CommonActivityReq
        {
            ActivityId = 111, RequestId = 2,
            FillPhaseNodeReq = new WorldExplorationFillPhaseNodeReq { PhaseCfgId = 103, NodeCfgId = node, ItemId = piece, Position = 1 },
        });
        Assert.Equal(0, (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 2)).Result);
    }
}
