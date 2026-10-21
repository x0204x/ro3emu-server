using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Monster Hunt (MonsterHuntService): a field kill on a Hunt map counts for the map and the listed monster inside the
/// kill's own transaction and drops the area's region point; monster and area stage claims pay once.
/// Prontera South Gate field 10110 (AreaConcurMapConfig 10100101, area 101001, base level 30), monster row 173 = NPC 5000001.
/// </summary>
public sealed class MonsterHuntTests : IAsyncLifetime
{
    private const long Area = 101001, Map = 10100101, Row = 173, OtherRow = 174;
    private const int Npc = 5000001, RegionPoint = 501;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-hunt-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private async Task<BotClient> OnHuntMap(string name)
    {
        var bot = await InWorld(name);
        await Gm(bot, "!lvl 40");
        await Gm(bot, "!warp 10110");
        await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.SwitchSceneLine), Wait);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10110 });
        await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.SceneLoaded), Wait);
        return bot;
    }

    private MonsterUnit Monster(GameSession killer, long uid) => new()
    {
        Uid = uid, SceneEntry = killer.SceneEntry, RuntimeMap = killer.RuntimeMap,
        Template = Service<MonsterService>().Template(Npc)!, Hp = 0,
        Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, Npc)
    };

    [ClientDataFact]
    public async Task AFieldKillCountsInItsOwnTransactionAndDropsTheRegionPoint()
    {
        await using var alice = await OnHuntMap("Alice");
        var s = Session(alice);
        await OnLoop(() => Service<RewardService>().PrepareKilledAsync(s, Monster(s, 900001)));
        Assert.Equal(1, await Read(() => s.AreaConcur!.MonsterInfo[Row].CurScore));
        Assert.Equal(1, await Read(() => s.AreaConcur!.MapInfo[Map].CurScore));
        Assert.Equal(1, await Read(() => Service<CharacterService>().CountOf(s, RegionPoint))); // AwardConfig 100230001: 100% one point
        // The counters are stored with the kill, not by a separate save.
        var stored = DBAreaConcurInfo.Parser.ParseFrom(Service<RoleStore>().LoadAreaConcur(s.Role!.Id)!);
        Assert.Equal(1, stored.MonsterInfo[Row].CurScore);

        // Below the area's level gate (base 30) nothing counts.
        await using var bob = await InWorld("Bob");
        var b = Session(bob);
        await Gm(bob, "!warp 10110");
        await bob.Inbox.WaitAsync(e => e.IsGame(MsgId.SwitchSceneLine), Wait);
        await bob.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10110 });
        await bob.Inbox.WaitAsync(e => e.IsGame(MsgId.SceneLoaded), Wait);
        await OnLoop(() => Service<RewardService>().PrepareKilledAsync(b, Monster(b, 900002)));
        Assert.Empty(await Read(() => b.AreaConcur!.MonsterInfo.Keys.ToArray()));
    }

    [ClientDataFact]
    public async Task MonsterAndAreaStagesPayOnceWhenReached()
    {
        await using var alice = await OnHuntMap("Alice");
        var s = Session(alice);
        async Task ClaimMonster(long id, params long[] stages)
        {
            var req = new AreaConcurGainMonsterAwardReq { MonsterId = id };
            foreach (long stage in stages) req.StageIds[stage] = true;
            await alice.SendGameAsync(MonsterHuntService.GainMonsterAward, req);
            await Next(alice, MonsterHuntService.GainMonsterAward, AreaConcurGainMonsterAwardResp.Parser);
        }
        // Not reached: 0 kills.
        await ClaimMonster(Row, 1);
        Assert.False(await Read(() => s.AreaConcur!.MonsterInfo.GetValueOrDefault(Row)?.ClaimedAward.ContainsKey(1) == true));

        // 120 kills reach stages 1 (60) and 2 (120) of both Prontera field monsters: four claimed stages.
        await OnLoop(() =>
        {
            foreach (long id in new[] { Row, OtherRow }) s.AreaConcur!.MonsterInfo[id] = new AreaConcurMonsterInfo { CurScore = 120 };
            return Task.CompletedTask;
        });
        long items = await Read(() => s.Items.Sum(i => (long)i.Stack));
        await ClaimMonster(Row, 1, 2);
        await ClaimMonster(OtherRow, 1, 2);
        Assert.True(await Read(() => s.Items.Sum(i => (long)i.Stack)) > items, "the monster stage rewards arrived");
        Assert.Equal(4, await Read(() => s.AreaConcur!.AreaInfo[Area].CurScore));
        long afterMonsters = await Read(() => s.Items.Sum(i => (long)i.Stack));
        await ClaimMonster(Row, 1, 2); // already claimed
        Assert.Equal(afterMonsters, await Read(() => s.Items.Sum(i => (long)i.Stack)));

        // Area stage 1 needs 10 claimed monster stages: refused at 4, paid at 10.
        async Task ClaimArea(long stage)
        {
            var req = new AreaConcurGainAreaAwardReq { AreaId = Area };
            req.StageIds[stage] = true;
            await alice.SendGameAsync(MonsterHuntService.GainAreaAward, req);
            await Next(alice, MonsterHuntService.GainAreaAward, AreaConcurGainAreaAwardResp.Parser);
        }
        await ClaimArea(1);
        Assert.False(await Read(() => s.AreaConcur!.AreaInfo[Area].ClaimedAward.ContainsKey(1)));
        await OnLoop(() =>
        {
            foreach (long id in new[] { 175L, 176L, 177L })
                s.AreaConcur!.MonsterInfo[id] = new AreaConcurMonsterInfo { CurScore = 120, ClaimedAward = { [1] = true, [2] = true } };
            return Task.CompletedTask;
        });
        await ClaimArea(1);
        Assert.True(await Read(() => s.AreaConcur!.AreaInfo[Area].ClaimedAward[1]));
        Assert.Equal(1, await Read(() => Service<CharacterService>().CountOf(s, 1300067))); // Blue Cards Selection Chest
        await ClaimArea(1);
        Assert.Equal(1, await Read(() => Service<CharacterService>().CountOf(s, 1300067)));
        var stored = DBAreaConcurInfo.Parser.ParseFrom(Service<RoleStore>().LoadAreaConcur(s.Role!.Id)!);
        Assert.True(stored.AreaInfo[Area].ClaimedAward[1]);
    }
}
