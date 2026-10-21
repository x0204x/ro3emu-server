using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>C/D 13a: socket and plan state stay together across a held commit and a lost acknowledgement.</summary>
public sealed class CardAtomicityTests : IAsyncLifetime
{
    private const int Lunatic = 1420100, Dagger = 2010121;
    private readonly string _dataDir = Path.Combine(RepoRoot(), ".loadtest-temp", "cd13a-card-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _httpUrl = "";
    private static readonly Lazy<EconomyCatalog> Catalog = new(() =>
        EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")));
    private EconomySnapshot Cut() => S3EconomyReader.Read(_dataDir, Catalog.Value);

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
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
    public async Task HeldInsertKeepsBagAndPlanOldUntilOneCommitAndRemoveResolvesLostAck()
    {
        await using var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        var initial = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long dagger = initial.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == Dagger).ItemGuid;
        await Gm("!lvl 20");
        await Gm($"!item {Lunatic} 2");
        var cards = await Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.TryGetValue(3, out var p) && p.All?.Items.Values.Any(i => i.ItemEntry == Lunatic) == true);
        long guid = cards.PackInfo[3].All.Items.Values.Single(i => i.ItemEntry == Lunatic).ItemGuid;
        await bot.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        var opening = Cut();

        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var items = _app.Services.GetRequiredService<ItemStore>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        accounts.BeforeCommitForTest = label =>
        {
            if (label != "economy/cd13a/card-insert") return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("card insert commit barrier");
        };
        try
        {
            await bot.SendGameAsync(CardService.SlotInsert,
                new EquipmentCardInsertReq { EquipPos = (EquipGridType)1, SlotIndex = 1, CardGuid = guid });
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(10))));
            Assert.Equal(2, items.Load(role).Where(i => i.Entry == Lunatic && i.Grid == 0).Sum(i => i.Stack));
            Assert.DoesNotContain(items.Load(role), i => i.Entry == Lunatic && i.Grid < 0);
            Assert.DoesNotContain(await loop.InvokeAsync(() => Task.FromResult(session.Items.ToArray())),
                i => i.Entry == Lunatic && i.Grid < 0);
            Assert.Equal(1, DBStorageHoleInfo.Parser.ParseFrom(roles.LoadCardPlans(role)).CurrentPlan);
            release.Set();
            var reply = await Next(bot, CardService.SlotInsert, EquipmentCardInsertResp.Parser);
            Assert.Equal(0, reply.Result);
            Assert.Equal(1, items.Load(role).Where(i => i.Entry == Lunatic && i.Grid < 0).Sum(i => i.Stack));
            Assert.Equal(Lunatic, DBStorageHoleInfo.Parser.ParseFrom(roles.LoadCardPlans(role))
                .StoragePlans[1].CardInfo[1].CardSlotInfo[1].ItemId);
        }
        finally { release.Set(); accounts.BeforeCommitForTest = null; }

        accounts.FailNextEconomyAcknowledgementForTest();
        await bot.SendGameAsync(CardService.SlotRemove,
            new EquipmentCardRemoveReq { EquipPos = (EquipGridType)1, SlotIndex = 1 });
        Assert.Equal(0, (await Next(bot, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
        Assert.Equal(2, items.Load(role).Where(i => i.Entry == Lunatic && i.Grid == 0).Sum(i => i.Stack));
        Assert.DoesNotContain(items.Load(role), i => i.Entry == Lunatic && i.Grid < 0);
        Assert.Equal(0, DBStorageHoleInfo.Parser.ParseFrom(roles.LoadCardPlans(role))
            .StoragePlans[1].CardInfo[1].CardSlotInfo[1].ItemId);
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = accounts.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ToString()))
        {
            db.Open();
            foreach (string kind in new[] { "cd13a/card-insert", "cd13a/card-remove" })
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT COUNT(*),COUNT(DISTINCT source_key) FROM operation_receipt WHERE command_kind=$kind AND actor_role=$role";
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$role", role);
                using var row = command.ExecuteReader();
                Assert.True(row.Read());
                Assert.Equal(1L, row.GetInt64(0));
                Assert.Equal(1L, row.GetInt64(1));
            }
        }

        accounts.BeforeCommitForTest = label =>
        {
            if (label == "economy/cd13a/card-insert") throw new InvalidOperationException("card rollback probe");
        };
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => loop.InvokeAsync(() =>
                _app.Services.GetRequiredService<CardService>().InsertAsync(session,
                    new EquipmentCardInsertReq { EquipPos = (EquipGridType)1, SlotIndex = 1, CardGuid = guid })));
            Assert.Equal(2, items.Load(role).Where(i => i.Entry == Lunatic && i.Grid == 0).Sum(i => i.Stack));
            Assert.DoesNotContain(items.Load(role), i => i.Entry == Lunatic && i.Grid < 0);
            Assert.DoesNotContain(session.Items, i => i.Entry == Lunatic && i.Grid < 0);
            Assert.Equal(0, DBStorageHoleInfo.Parser.ParseFrom(roles.LoadCardPlans(role))
                .StoragePlans[1].CardInfo[1].CardSlotInfo[1].ItemId);
        }
        finally { accounts.BeforeCommitForTest = null; }

        var closing = Cut();
        var ledger = S3EconomyReader.ReadLedger(_dataDir, Catalog.Value, opening, closing)!;
        Assert.True(ledger.CompleteHistory, string.Join(';', ledger.Issues.Select(issue => issue.Detail)));
        var exact = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.DoesNotContain(exact.Issues, issue => issue.Kind == EconomyIssueKind.Corruption);
        Assert.All(exact.Assets, asset =>
        {
            Assert.Equal("0", asset.Residual);
            Assert.Equal("0", asset.LegResidual);
        });
        Assert.All(exact.Locations, location => Assert.Equal("0", location.Residual));
        Assert.Contains(ledger.Legs, leg => leg.Reason == EconomyReason.InventoryRelocation && leg.Asset.Id == Lunatic);
        Assert.DoesNotContain(ledger.Legs, leg => leg.Asset.Id == Lunatic &&
            leg.Kind is EconomyLegKind.Source or EconomyLegKind.Sink);
    }

    private async Task Gm(string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_httpUrl}/ro3emu/gm?account=alice", new StringContent(command)))
            .EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser,
        Func<T, bool>? match = null) where T : Google.Protobuf.IMessage<T>
    {
        var evt = await bot.Inbox.WaitAsync(e => e.IsGame(id) &&
            (match == null || match(parser.ParseFrom(e.Body))), TimeSpan.FromSeconds(10));
        return parser.ParseFrom(evt.Body);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo data");
    }
}
