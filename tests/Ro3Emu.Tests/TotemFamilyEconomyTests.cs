using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Neutral setup/down and bound instance transformation retain one covered source each.</summary>
public sealed class TotemFamilyEconomyTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-totem-family-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);
    private string Database => Path.Combine(_directory, "ro3emu.db");

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await _app.StartAsync(); _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id); await WorldSettle.StallsAsync(_app.Services);
        await Gm("!lvl 60");
        return bot;
    }

    private async Task Gm(string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync(_http + "/ro3emu/gm?account=alice", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private async Task<long> Give(int entry)
    {
        await Gm($"!item {entry} 1");
        return Session.Items.Last(i => i.Entry == entry).Guid;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(10))).Body);
    private const string QuestAward = "cd13e/quest-award";
    private EconomySnapshot Cut() => S3EconomyReader.Read(_directory, EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")));
    private EconomyLedgerWindow Window(EconomySnapshot a, EconomySnapshot b) =>
        S3EconomyReader.ReadLedger(_directory, EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")), a, b)!;

    [ClientDataFact]
    public async Task SetupAndDownCommitMarkersAndBlobWithVerifiableMetadataReceipts()
    {
        await using var bot = await Enter();
        long guid = await Give(17300001);
        var opening = Cut();
        await bot.SendGameAsync(82362, new SetUpTotemReq { TotemPos = 1, TotemGuid = guid });
        Assert.Equal(0, (await Next(bot, 82362, SetUpTotemResp.Parser)).Result);
        Assert.Equal(guid, Session.Totems!.TotemPlans[0].SetUpTotem[1]);
        Assert.Equal(1, Session.Items.Single(i => i.Guid == guid).Attrs![10001]);
        var up = Cut();
        // Growth Classroom quests (1700500 deploy, 1700518 combine) deliver their zero-leg awards beside these.
        var first = Assert.Single(Window(opening, up).Receipts, r => r.CommandKind != QuestAward);
        Assert.Equal(1, first.LegCount);
        var upLeg = Assert.Single(Window(opening, up).Legs);
        Assert.Equal(EconomyLegKind.Metadata, upLeg.Kind);
        Assert.Equal(guid.ToString(), upLeg.Location.Instance);
        Assert.NotEqual(upLeg.BeforeFingerprint, upLeg.AfterFingerprint);
        var receipt = EconomyReceiptReader.Read(Database, first.OperationId)!;
        Assert.Equal("cd13b/totem-state", receipt.CommandKind);
        Assert.Equal(receipt.RequestFingerprint, Convert.ToHexString(SHA256.HashData(receipt.Outcome)));
        Assert.Equal(Session.Totems.ToByteArray(), Service<RoleStore>().LoadTotems(Session.Role!.Id));
        Assert.Equal(1, Service<ItemStore>().Load(Session.Role.Id).Single(i => i.Guid == guid).Attrs![10001]);

        await bot.SendGameAsync(82362, new SetUpTotemReq { TotemPos = 1, TotemGuid = 0 });
        Assert.Equal(0, (await Next(bot, 82362, SetUpTotemResp.Parser)).Result);
        var down = Cut();
        var second = Assert.Single(Window(up, down).Receipts, r => r.CommandKind != QuestAward);
        Assert.Equal(1, second.LegCount);
        var downLeg = Assert.Single(Window(up, down).Legs);
        Assert.Equal(EconomyLegKind.Metadata, downLeg.Kind);
        Assert.Equal(guid.ToString(), downLeg.Location.Instance);
        Assert.Equal(upLeg.AfterFingerprint, downLeg.BeforeFingerprint);
        Assert.NotEqual(downLeg.BeforeFingerprint, downLeg.AfterFingerprint);
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(0, Session.Items.Single(i => i.Guid == guid).Attrs![10001]);
        Assert.Equal(0, Service<ItemStore>().Load(Session.Role.Id).Single(i => i.Guid == guid).Attrs![10001]);
        Assert.Equal(2, Window(opening, down).Legs.Count);
    }

    [ClientDataFact]
    public async Task BoundCombineConsumesTwoInstancesAndPublishesOneMatchingReceipt()
    {
        await using var bot = await Enter();
        long leftGuid = await Give(17400001), rightGuid = await Give(17400001);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            var left = Session.Items.Single(i => i.Guid == leftGuid);
            var right = Session.Items.Single(i => i.Guid == rightGuid);
            left.BindType = 2; left.BindExpire = 123456;
            right.BindType = 1; right.BindExpire = 0;
            Service<ItemStore>().Update(left); Service<ItemStore>().Update(right);
            return Task.CompletedTask;
        });
        var opening = Cut();
        await bot.SendGameAsync(82366, new CombineTotemReq { LeftTotemGuid = leftGuid, RightTotemGuid = rightGuid });
        var combined = await Next(bot, 82366, CombineTotemResp.Parser);
        Assert.Equal(0, combined.Result);
        var item = Session.Items.Single(i => i.Guid == combined.NewTotemGuid);
        Assert.Equal((1, 0L), (item.BindType, item.BindExpire));
        Assert.DoesNotContain(Session.Items, i => i.Guid == leftGuid || i.Guid == rightGuid);
        var durable = Service<ItemStore>().Load(Session.Role!.Id);
        Assert.DoesNotContain(durable, i => i.Guid == leftGuid || i.Guid == rightGuid);
        Assert.Equal((1, 0L), (durable.Single(i => i.Guid == item.Guid).BindType, durable.Single(i => i.Guid == item.Guid).BindExpire));
        var window = Window(opening, Cut());
        var receipt = Assert.Single(window.Receipts, r => r.CommandKind != QuestAward);
        Assert.Equal(3, receipt.LegCount);
        var source = Assert.Single(window.Legs, leg => leg.Kind == EconomyLegKind.Source);
        Assert.Equal((17400001L, 1L, item.Guid.ToString()),
            (source.Asset.Id, source.Delta, source.Location.Instance));
        var detail = EconomyReceiptReader.Read(Database, receipt.OperationId)!;
        Assert.Equal("cd13b/totem-combine", detail.CommandKind);
        Assert.Equal(detail.RequestFingerprint, Convert.ToHexString(SHA256.HashData(detail.Outcome)));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
