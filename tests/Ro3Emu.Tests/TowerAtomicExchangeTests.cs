using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Atomic item, role and Tower marker persistence for a cleared-floor reward.</summary>
public sealed class TowerAtomicExchangeTests : IAsyncLifetime
{
    private const int Token = 1250709, OtherReward = 1250701;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-tower-atomic-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
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

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private GameSession Session(BotClient bot) =>
        Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    private static byte[] FirstPass(int floor) =>
        new ClimbTowerFirstPassData { Layer = floor, HasFirstPass = true }.ToByteArray();

    private static int ExchangesSent(BotClient bot) =>
        bot.Inbox.Snapshot().Count(e => e.IsGame(CharacterService.ItemsSync) || e.IsGame(CharacterService.GetRewardNotify));

    private long Count(long role, int entry) =>
        Service<ItemStore>().Load(role).Where(i => i.Entry == entry).Sum(i => (long)i.Stack);

    [ClientDataFact]
    public async Task SuccessfulExchangeCommitsRewardsCurrencyAndBothTowerMarkers()
    {
        await using var alice = await InWorld("Alice");
        var session = Session(alice);
        long role = session.Role!.Id;
        var roles = Service<RoleStore>();
        var character = Service<CharacterService>();
        byte[] tower = new DBClimTower { LayerConfID = 10 }.ToByteArray();
        byte[] firstPass = FirstPass(10);
        long currencyBefore = session.Progress!.Currency[1];
        int messagesBefore = ExchangesSent(alice);

        await OnLoop(async () =>
        {
            var granted = await character.ExchangeAsync(session, [(1, 100)], [(Token, 2), (OtherReward, 3)],
                popup: false, sync: false, persist: tx =>
                {
                    roles.SaveTower(role, tower, tx);
                    roles.RecordTowerFirstPass(10, firstPass, tx);
                });
            Assert.NotNull(granted);
            Assert.Equal(2, granted!.Count);
        });

        Assert.Equal(2, Count(role, Token));
        Assert.Equal(3, Count(role, OtherReward));
        Assert.Equal(currencyBefore - 100, roles.LoadProgress(role).Currency[1]);
        Assert.Equal(currencyBefore - 100, session.Progress!.Currency[1]);
        Assert.Equal(10, DBClimTower.Parser.ParseFrom(roles.LoadTower(role)!).LayerConfID);
        Assert.Equal(firstPass, roles.LoadTowerFirstPasses(10, 10)[10]);
        Assert.Equal(messagesBefore, ExchangesSent(alice)); // Caller owns the postcommit 5009 and 5065 ordering.
    }

    [ClientDataFact]
    public async Task TowerRewardKeepsExistingInstanceMetadataAndUsesSeparateStack()
    {
        await using var alice = await InWorld("Alice");
        var session = Session(alice);
        long role = session.Role!.Id;
        var roles = Service<RoleStore>();
        var items = Service<ItemStore>();
        var character = Service<CharacterService>();
        await OnLoop(async () => Assert.NotNull(await character.ExchangeAsync(session, [], [(Token, 1)], popup: false, sync: false)));
        var existing = session.Items.Single(i => i.Entry == Token);
        existing.IsLocked = true;
        existing.Attrs = new Dictionary<int, long> { [777] = 9 };
        existing.StringAttrs = new Dictionary<int, string> { [510] = "recorded-cost" };
        existing.BindType = 2;
        existing.BindExpire = 123456789;
        existing.Enchantments[42] = new ItemEnchantment { Attr = new Dictionary<int, int> { [101] = 7 }, GetTime = 987654321 };
        items.Update(existing);
        byte[] tower = new DBClimTower { LayerConfID = 10 }.ToByteArray();

        await OnLoop(async () => Assert.NotNull(await character.ExchangeAsync(session, [(1, 100)], [(Token, 2)],
            popup: false, sync: false, persist: tx => roles.SaveTower(role, tower, tx))));

        var stored = items.Load(role).Where(i => i.Entry == Token).ToArray();
        Assert.Equal(2, stored.Length);
        var retained = Assert.Single(stored, i => i.Guid == existing.Guid);
        Assert.Equal(1, retained.Stack);
        Assert.True(retained.IsLocked);
        Assert.Equal(9, retained.Attrs![777]);
        Assert.Equal("recorded-cost", retained.StringAttrs![510]);
        Assert.Equal(2, retained.BindType);
        Assert.Equal(123456789, retained.BindExpire);
        Assert.Equal(7, retained.Enchantments[42].Attr[101]);
        Assert.Equal(987654321, retained.Enchantments[42].GetTime);
        var reward = Assert.Single(stored, i => i.Guid != existing.Guid);
        Assert.Equal(2, reward.Stack);
        Assert.False(reward.IsLocked);
        Assert.Equal(0, reward.BindType);
        Assert.Equal(10, DBClimTower.Parser.ParseFrom(roles.LoadTower(role)!).LayerConfID);
    }

    [ClientDataFact]
    public async Task FailedTowerUpdateRollsBackBothStacksCurrencyAndMarkerThenRetryGrantsOnce()
    {
        await using var alice = await InWorld("Alice");
        var session = Session(alice);
        long role = session.Role!.Id;
        var roles = Service<RoleStore>();
        var character = Service<CharacterService>();
        await OnLoop(async () => Assert.NotNull(await character.ExchangeAsync(session, [], [(Token, 1)], popup: false, sync: false)));
        var existing = session.Items.Single(i => i.Entry == Token);
        var originalItems = session.Items.ToArray();
        var originalNewItems = session.NewItems.ToHashSet();
        long currencyBefore = session.Progress!.Currency[1];
        byte[] towerBefore = roles.LoadTower(role)!;
        byte[] towerAfter = new DBClimTower { LayerConfID = 10 }.ToByteArray();
        byte[] firstPass = FirstPass(10);
        int messagesBefore = ExchangesSent(alice);

        FixtureSql.Execute(Service<AccountStore>(),
            "CREATE TRIGGER fail_tower BEFORE UPDATE OF tower ON role BEGIN SELECT RAISE(ABORT, 'tower test failure'); END");
        try
        {
            await OnLoop(async () =>
                await Assert.ThrowsAsync<SqliteException>(() => character.ExchangeAsync(session, [(1, 100)], [(Token, 2), (OtherReward, 3)],
                    persist: tx =>
                    {
                        roles.RecordTowerFirstPass(10, firstPass, tx);
                        roles.SaveTower(role, towerAfter, tx);
                    })));

            Assert.Equal(1, Count(role, Token));
            Assert.Equal(0, Count(role, OtherReward));
            Assert.Equal(currencyBefore, roles.LoadProgress(role).Currency[1]);
            Assert.Equal(towerBefore, roles.LoadTower(role));
            Assert.Empty(roles.LoadTowerFirstPasses(10, 10));
            Assert.Equal(originalItems, session.Items);
            Assert.Same(existing, session.Items.Single(i => i.Entry == Token));
            Assert.Equal(1, existing.Stack);
            Assert.Equal(currencyBefore, session.Progress!.Currency[1]);
            Assert.True(session.NewItems.SetEquals(originalNewItems));
            Assert.Equal(messagesBefore, ExchangesSent(alice)); // Default popup/sync were requested but nothing escaped precommit.
        }
        finally
        {
            FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_tower");
        }

        await OnLoop(async () => Assert.NotNull(await character.ExchangeAsync(session, [(1, 100)], [(Token, 2), (OtherReward, 3)],
            popup: false, sync: false, persist: tx =>
            {
                roles.RecordTowerFirstPass(10, firstPass, tx);
                roles.SaveTower(role, towerAfter, tx);
            })));
        Assert.Same(existing, session.Items.Single(i => i.Entry == Token));
        Assert.Equal(3, existing.Stack);
        Assert.Equal(3, Count(role, Token));
        Assert.Equal(3, Count(role, OtherReward));
        Assert.Equal(currencyBefore - 100, roles.LoadProgress(role).Currency[1]);
        Assert.Equal(towerAfter, roles.LoadTower(role));
        Assert.Equal(firstPass, roles.LoadTowerFirstPasses(10, 10)[10]);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
