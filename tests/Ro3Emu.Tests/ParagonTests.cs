using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Astral Level (ParagonService, CharacterService.Paragon): Base EXP past level 99 feeds it, stars take points along
/// _iPostTalentID inside an active constellation, and Star Cores move between the bag and their slot under one balanced
/// receipt. Constellation 1 is nodes 1-12: 1 -> 2 -> 3 -> 5 -> {4, 6, 9}; node 6 is a Prism slot (_iType 920).
/// </summary>
public sealed class ParagonTests : IAsyncLifetime
{
    private const int PrismGray = 1201001, SunGray = 1201007;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-paragon-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.StarCoreDrops = false; // a lucky kill must not add a core to the counted bag
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

    /// <summary>What the client was last sent on 5015.</summary>
    private Task<DBParagonLevel?> Sent(BotClient bot) =>
        Read(() => (Session(bot).Synced.GetValueOrDefault(ParagonService.Sync) as DBParagonLevel)?.Clone());

    private static async Task<int> Allocate(BotClient bot, int slot)
    {
        await bot.SendGameAsync(ParagonService.Allocate, new ParagonLevelAllocatePointReq { Slot = slot, Points = 1 });
        return (await Next(bot, ParagonService.Allocate, ParagonLevelAllocatePointResp.Parser)).Result;
    }

    private static async Task<int> Install(BotClient bot, int slot, long guid)
    {
        await bot.SendGameAsync(ParagonService.Install, new ParagonLevelRuneInstallReq { Slot = slot, RuneGuid = guid });
        return (await Next(bot, ParagonService.Install, ParagonLevelRuneInstallResp.Parser)).Result;
    }

    private async Task MaxOut(BotClient bot, params int[] slots)
    {
        foreach (int slot in slots)
            while (Session(bot).Paragon!.Runegroup.GetValueOrDefault(slot)?.Points is not { } p || p < MaxLevel(slot))
                Assert.Equal(0, await Allocate(bot, slot));
    }

    private int MaxLevel(int slot) => (int)Ro3Emu.Server.Data.GameData.Int(Service<Ro3Emu.Server.Data.GameData>().Row("PatronTalentConfig", slot)!.Value, "_iMaxLevel");

    private EconomySnapshot Cut() => EconomySnapshotReader.Read(Service<AccountStore>().DatabasePath,
        EconomyCatalog.Load(Path.Combine(ClientData.RepoRoot, "data")), "auto");

    private void Balanced(EconomySnapshot opening, EconomySnapshot closing)
    {
        var ledger = EconomyLedgerReader.Read(Service<AccountStore>().DatabasePath,
            EconomyCatalog.Load(Path.Combine(ClientData.RepoRoot, "data")), "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
        Assert.DoesNotContain(closing.Issues, i => i.Kind is EconomyIssueKind.UnknownSchema or EconomyIssueKind.Corruption);
        var check = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(check.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Assets, asset => Assert.Equal("0", asset.Residual));
    }

    [ClientDataFact]
    public async Task BaseExpPastTheLevelCapFeedsTheAstralLevel()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        // 5000 Base EXP at the cap, +20% (PatronExpAddConfig: 10 or more levels under the server's Astral Level 100):
        // 6000, of which level 1 takes its 3000 and 3000 stays toward level 2's 4200.
        await Gm(alice, "!paragon exp 5000");
        var p = Session(alice).Progress!;
        Assert.Equal((99, 0L, 2, 3000L), (p.Level, p.Exp, p.ParagonLevel, p.ParagonExp));
        var sent = (await Sent(alice))!;
        Assert.Equal((2, 3000L, 2L, 200000d), (sent.Lv, sent.Exp, sent.LeftPoints, sent.ExtraAdd));
        Assert.Equal([1], sent.ActiveGroup.Keys.Order());
        // Every node travels, at 0 points too, and the missing ids between them as well.
        Assert.Equal(Enumerable.Range(1, 40), sent.Runegroup.Keys.Order());
        Assert.All(sent.Runegroup.Values, r => Assert.Equal(0, r.Points));
    }

    [ClientDataFact]
    public async Task StarsTakePointsAlongTheirChainInsideAnActiveConstellation()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!paragon level 40"); // 40 points
        Assert.Equal(ErrorCodes.Parameter, await Allocate(alice, 2)); // node 1 (its predecessor) is not full
        await MaxOut(alice, 1);
        Assert.Equal(ErrorCodes.Parameter, await Allocate(alice, 1)); // capped at 3
        Assert.Equal(0, await Allocate(alice, 2));
        Assert.Equal(ErrorCodes.Parameter, await Allocate(alice, 13)); // constellation 2 is not active yet
        Assert.Equal(36L, (await Sent(alice))!.LeftPoints);
        // Completing constellation 1 (32 points) activates constellation 2.
        await MaxOut(alice, 2, 3, 5, 4, 6, 9, 8, 7, 11, 12, 10);
        var sent = (await Sent(alice))!;
        Assert.Equal([1, 2], sent.ActiveGroup.Keys.Order());
        Assert.Equal(8L, sent.LeftPoints);
        Assert.Equal(0, await Allocate(alice, 13));
        Assert.True(sent.LvPower > 0);
    }

    [ClientDataFact]
    public async Task AStarCoreLeavesTheBagForItsSlotAndComesBackOnRemovalAndReset()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!paragon level 20");
        await MaxOut(alice, 1, 2, 3, 5, 6); // node 6, the Prism slot, needs its one point first
        await Gm(alice, $"!item {PrismGray} 1");
        await Gm(alice, $"!item {SunGray} 1");
        var opening = Cut();
        long prism = Session(alice).Items.Single(i => i.Entry == PrismGray).Guid;
        long sun = Session(alice).Items.Single(i => i.Entry == SunGray).Guid;
        Assert.Equal(ErrorCodes.Parameter, await Install(alice, 6, sun)); // a Sun core fits only a 921 star
        Assert.Equal(ErrorCodes.Parameter, await Install(alice, 5, prism)); // not a slot
        Assert.Equal(0, await Install(alice, 6, prism));
        Assert.DoesNotContain(Session(alice).Items, i => i.Entry == PrismGray);
        Assert.Equal(PrismGray, (await Sent(alice))!.Runegroup[6].Rune.ItemEntry);

        await alice.SendGameAsync(ParagonService.Remove, new ParagonLevelRuneRemoveReq { SlotId = 6 });
        Assert.Equal(0, (await Next(alice, ParagonService.Remove, ParagonLevelRuneRemoveResp.Parser)).Result);
        Assert.Single(Session(alice).Items, i => i.Entry == PrismGray);
        Assert.Equal(0, (await Sent(alice))!.Runegroup[6].Rune.ItemEntry); // an empty slot's rune, so the client drops the core
        Assert.Equal(1, Session(alice).Paragon!.Runegroup[6].Points); // its point stays

        // Back in, then a reset of star 3: 3, 5 and everything after them (4, 6, 9 ...) are emptied, the core returns.
        Assert.Equal(0, await Install(alice, 6, Session(alice).Items.Single(i => i.Entry == PrismGray).Guid));
        await alice.SendGameAsync(ParagonService.ResetSingle, new ParagonLevelResetSingleReq { SlotId = 3 });
        Assert.Equal(0, (await Next(alice, ParagonService.ResetSingle, ParagonLevelResetSingleResp.Parser)).Result);
        var sent = (await Sent(alice))!;
        Assert.Equal((3, 3, 0, 0, 0), (sent.Runegroup[1].Points, sent.Runegroup[2].Points, sent.Runegroup[3].Points,
            sent.Runegroup[5].Points, sent.Runegroup[6].Points));
        Assert.Single(Session(alice).Items, i => i.Entry == PrismGray);

        await alice.SendGameAsync(ParagonService.ResetAll, new ParagonLevelResetAllReq());
        Assert.Equal(0, (await Next(alice, ParagonService.ResetAll, ParagonLevelResetAllResp.Parser)).Result);
        Assert.Equal(20L, (await Sent(alice))!.LeftPoints);
        Balanced(opening, Cut());
    }
}
