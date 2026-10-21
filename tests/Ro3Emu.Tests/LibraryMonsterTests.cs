using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The Monster Codex (CardService.Monsters.cs): a kill of a Codex monster (NPCConfig._iWildMonsters 1) counts in 5057
/// monsters inside the kill's own transaction; 82407 unlocks a counted entry once and pays 10 Collection Points (118).
/// NPC 5000001 is a Codex monster, 400096 a monster that is not; function 6001 opens at base level 14.
/// </summary>
public sealed class LibraryMonsterTests : IAsyncLifetime
{
    private const int Codex = 5000001, NotCodex = 400096, Points = 118;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-codex-" + Guid.NewGuid().ToString("N"));
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

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
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

    private EconomySnapshot Cut() => EconomySnapshotReader.Read(Service<AccountStore>().DatabasePath,
        EconomyCatalog.Load(Path.Combine(ClientData.RepoRoot, "data")), "auto");

    /// <summary>The receipts between two cuts balance exactly.</summary>
    private EconomyLedgerWindow Balanced(EconomySnapshot opening, EconomySnapshot closing)
    {
        var ledger = EconomyLedgerReader.Read(Service<AccountStore>().DatabasePath,
            EconomyCatalog.Load(Path.Combine(ClientData.RepoRoot, "data")), "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        Assert.DoesNotContain(closing.Issues, i => i.Kind is EconomyIssueKind.UnknownSchema or EconomyIssueKind.Corruption);
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Assets, asset => Assert.Equal("0", asset.Residual));
        return ledger;
    }

    private async Task LoggedOut(string account)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (Service<SessionRegistry>().All().Any(s => s.Account == account && s.Role != null) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(200);
    }

    private MonsterUnit Monster(GameSession killer, long uid, int npc) => new()
    {
        Uid = uid, SceneEntry = killer.SceneEntry, RuntimeMap = killer.RuntimeMap,
        Template = Service<MonsterService>().Template(npc)!, Hp = 0,
        Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, npc)
    };

    private Task Kill(GameSession s, long uid, int npc) =>
        OnLoop(() => Service<RewardService>().PrepareKilledAsync(s, Monster(s, uid, npc)));

    private static async Task<int> Unlock(BotClient bot, long monster)
    {
        await bot.SendGameAsync(CardService.LibraryMonsterUnlock, new LibraryMonsterUnlockReq { MonsterId = monster });
        return (await Next(bot, CardService.LibraryMonsterUnlock, LibraryMonsterUnlockResp.Parser)).Result;
    }

    private Task<long> PointsOf(GameSession s) => Read(() => s.Progress!.Currency.GetValueOrDefault(Points));

    [ClientDataFact]
    public async Task AKillCountsInItsOwnTransactionAndAnUnlockPaysOnce()
    {
        long role;
        await using (var alice = await InWorld("Alice"))
        {
            var s = Session(alice);
            role = s.Role!.Id;
            var opening = Cut();
            await Kill(s, 900001, Codex);
            await Kill(s, 900002, Codex);
            await Kill(s, 900003, NotCodex);
            var kills = Balanced(opening, Cut()).Receipts;
            Assert.Equal(3, kills.Count(r => r.CommandKind == "monster-kill")); // besides any quest the kills complete
            Assert.Equal(2, await Read(() => s.Library!.Monsters[Codex].KillCount));
            Assert.False(await Read(() => s.Library!.Monsters[Codex].Unlock));
            Assert.False(await Read(() => s.Library!.Monsters.ContainsKey(NotCodex)));
            // Stored with the kill, not by a separate save; the client is told by a 5057 change.
            var stored = DBLibraryInfo.Parser.ParseFrom(Service<RoleStore>().LoadLibrary(role)!);
            Assert.Equal(2, stored.Monsters[Codex].KillCount);
            await Next(alice, CardService.LibrarySync, DBLibraryInfoIncrement.Parser, m => m.Monsters.ContainsKey(Codex));

            Assert.Equal(ErrorCodes.FunctionLocked, await Unlock(alice, Codex)); // function 6001: level 14
            await Gm(alice, "!lvl 20");
            Assert.Equal(ErrorCodes.CodexUnavailable, await Unlock(alice, NotCodex)); // never counted
            long before = await PointsOf(s);
            opening = Cut();
            Assert.Equal(0, await Unlock(alice, Codex));
            var ledger = Balanced(opening, Cut());
            Assert.Equal("cd13e/library-award", Assert.Single(ledger.Receipts).CommandKind);
            Assert.All(ledger.Legs.Where(l => l.Kind == EconomyLegKind.Source), l => Assert.Equal(EconomyReason.LibraryReward, l.Reason));
            Assert.True(await Read(() => s.Library!.Monsters[Codex].Unlock));
            Assert.Equal(before + 10, await PointsOf(s)); // PublicParamConfig.LibraryMonsterUnlockReward
            var change = await Next(alice, CardService.LibrarySync, DBLibraryInfoIncrement.Parser,
                m => m.Monsters.TryGetValue(Codex, out var f) && (f.All?.Unlock == true || f.Inc?.Unlock == true));
            Assert.False(change.SyncAllMark);
            Assert.Equal(ErrorCodes.AlreadyUnlocked, await Unlock(alice, Codex));
            Assert.Equal(before + 10, await PointsOf(s));

            await Kill(s, 900004, Codex); // counting goes on after the unlock
            Assert.Equal(3, await Read(() => s.Library!.Monsters[Codex].KillCount));
            Assert.True(await Read(() => s.Library!.Monsters[Codex].Unlock));
        }
        await LoggedOut("alice");
        await using var again = await InWorld("Alice", create: false);
        var full = await Next(again, CardService.LibrarySync, DBLibraryInfoIncrement.Parser, m => m.SyncAllMark);
        Assert.Equal(3, full.Monsters[Codex].All.KillCount);
        Assert.True(full.Monsters[Codex].All.Unlock);
        Assert.Equal(ErrorCodes.AlreadyUnlocked, await Unlock(again, Codex));
        Assert.Equal(10, await PointsOf(Session(again)));
    }

    /// <summary>
    /// 82405: LC_DAL_Library._processLibraryChangeUI sets iSelectUI to the reply's themeId, so the reply echoes it.
    /// LibraryThemeConfig has themes 1 and 2; theme 2 needs condition 15000000 (item 118 >= 1000).
    /// </summary>
    [ClientDataFact]
    public async Task ThemeSwitchChecksItsUnlockConditionAndPersistsTheTheme()
    {
        await using var reader = await InWorld("Reader");
        async Task<LibrarySwitchThemeResp> Switch(BotClient bot, int theme)
        {
            await bot.SendGameAsync(CardService.LibrarySwitchTheme, new LibrarySwitchThemeReq { ThemeId = theme });
            return await Next(bot, CardService.LibrarySwitchTheme, LibrarySwitchThemeResp.Parser);
        }
        var locked = await Switch(reader, 2);
        Assert.Equal(ErrorCodes.UnlockRequirement, locked.Result);
        Assert.Equal(0, locked.ThemeId);
        Assert.Equal(ErrorCodes.Parameter, (await Switch(reader, 3)).Result);
        Assert.Equal(ErrorCodes.Parameter, (await Switch(reader, 0)).Result);

        await Gm(reader, $"!item {Points} 1000");
        var chosen = await Switch(reader, 2);
        Assert.Equal(0, chosen.Result);
        Assert.Equal(2, chosen.ThemeId);
        var sync = await Next(reader, CardService.LibrarySync, DBLibraryInfoIncrement.Parser, m => m.HasOverviewTheme);
        Assert.Equal(2, sync.OverviewTheme);
        long role = Session(reader).Role!.Id;
        Assert.Equal(2, DBLibraryInfo.Parser.ParseFrom(Service<RoleStore>().LoadLibrary(role)!).OverviewTheme);

        var back = await Switch(reader, 1);
        Assert.Equal((0, 1), (back.Result, back.ThemeId));
        Assert.Equal(1, DBLibraryInfo.Parser.ParseFrom(Service<RoleStore>().LoadLibrary(role)!).OverviewTheme);
    }
}
