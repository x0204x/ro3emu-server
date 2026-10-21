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
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>One covered pet command: staged state stays private until the ledger and blob commit together.</summary>
public sealed class PetFamilyOwnerEconomyTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-pet-family-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
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
        long role = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role); await WorldSettle.StallsAsync(_app.Services);
        await Gm("!quest done 10104330");
        await Gm("!lvl 1");
        await Gm("!item 1610001 20");
        return bot;
    }

    private async Task Gm(string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync(_http + "/ro3emu/gm?account=alice", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private EconomySnapshot Cut() => S3EconomyReader.Read(_directory, EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")));
    private EconomyLedgerWindow Window(EconomySnapshot a, EconomySnapshot b) =>
        S3EconomyReader.ReadLedger(_directory, EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")), a, b)!;
    private static async Task<PetCombineResp> Combine(BotClient bot)
    {
        await bot.SendGameAsync(PetService.Combine, new PetCombineReq { PetId = 10001 });
        return PetCombineResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(PetService.Combine),
            TimeSpan.FromSeconds(8))).Body);
    }

    [ClientDataFact]
    public async Task StagedCommitKeepsPetAndFragmentsUnpublishedUntilCoveredCommit()
    {
        await using var bot = await Enter();
        var s = Session;
        var opening = Cut();
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ArmStageBarrier(staged, release);
        var command = Combine(bot);
        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Service<GameLoop>().InvokeAsync(() =>
            {
                Assert.False(s.Pets!.ListPet.ContainsKey(10001));
                Assert.Equal(20, s.Items.Where(i => i.Entry == 1610001).Sum(i => i.Stack));
                return Task.CompletedTask;
            });
            Assert.False(command.IsCompleted);
        }
        finally { release.TrySetResult(); await ClearStageBarrier(); }
        Assert.Equal(0, (await command.WaitAsync(TimeSpan.FromSeconds(8))).Result);
        var closing = Cut();
        var receipt = Assert.Single(Window(opening, closing).Receipts);
        Assert.Equal(1, receipt.LegCount);
        Assert.Equal("cd13b/pet-combine", EconomyReceiptReader.Read(Path.Combine(_directory, "ro3emu.db"), receipt.OperationId)!.CommandKind);
        Assert.True(s.Pets!.ListPet.ContainsKey(10001));
        Assert.Equal(0, s.Items.Where(i => i.Entry == 1610001).Sum(i => i.Stack));
    }

    [ClientDataFact]
    public async Task GmItemWaitsForSuspendedPetCommitAndSeesItsPublishedInventory()
    {
        await using var bot = await Enter();
        var session = Session;
        var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ArmStageBarrier(staged, release, deferred);
        var command = Combine(bot);
        Task? gm = null;
        try
        {
            await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gm = Gm("!item 1250701 1");
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(gm.IsCompleted);
            Assert.Equal(20, session.Items.Where(i => i.Entry == 1610001).Sum(i => i.Stack));
            Assert.DoesNotContain(session.Items, i => i.Entry == 1250701);
        }
        finally
        {
            release.TrySetResult();
            await ClearStageBarrier();
        }
        Assert.Equal(0, (await command.WaitAsync(TimeSpan.FromSeconds(10))).Result);
        await gm!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, session.Items.Where(i => i.Entry == 1610001).Sum(i => i.Stack));
        Assert.Equal(1, session.Items.Where(i => i.Entry == 1250701).Sum(i => i.Stack));
    }

    [ClientDataFact]
    public async Task LedgerFailureRollsBackPetBlobAndMaterialDebit()
    {
        await using var bot = await Enter();
        var s = Session;
        var opening = Cut();
        FixtureSql.Execute(Service<AccountStore>(), """
            CREATE TRIGGER fail_pet_economy BEFORE INSERT ON economy_entry
            BEGIN SELECT RAISE(ABORT,'pet economy fault'); END
            """);
        try
        {
            var failure = await Assert.ThrowsAsync<SqliteException>(() => Service<GameLoop>().InvokeAsync(() =>
                Service<OwnerRegistry>().RunSessionAsync("dispatcher", [ReservationKey.Role(s.Role!.Id)], s,
                    () => Service<PetService>().CombineAsync(s, new PetCombineReq { PetId = 10001 }))));
            Assert.Contains("pet economy fault", failure.Message);
            Assert.False(s.Pets!.ListPet.ContainsKey(10001));
            Assert.Equal(20, s.Items.Where(i => i.Entry == 1610001).Sum(i => i.Stack));
            Assert.False(DBPetInfo.Parser.ParseFrom(Service<RoleStore>().LoadPets(s.Role!.Id)!).ListPet.ContainsKey(10001));
            Assert.Empty(Window(opening, Cut()).Receipts);
        }
        finally { FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_pet_economy"); }
        Assert.Equal(0, (await Combine(bot)).Result);
        Assert.Single(Window(opening, Cut()).Receipts);
    }

    [ClientDataFact]
    public async Task RebirthRefundHasOneExpectedBookSourceAndCannotRepeat()
    {
        await using var bot = await Enter();
        Assert.Equal(0, (await Combine(bot)).Result);
        await Gm("!lvl 33"); await Gm("!item 1250701 2");
        await bot.SendGameAsync(PetService.LevelUp, new PetLvUpReq { PetId = 10001, EType = (EPetLvUpType)1 });
        var level = PetLvUpResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(PetService.LevelUp),
            TimeSpan.FromSeconds(8))).Body);
        Assert.Equal(0, level.Result);
        var opening = Cut();
        await bot.SendGameAsync(PetService.Rebirth, new PetRebirthReq { PetId = 10001 });
        var rebirth = PetRebirthResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(PetService.Rebirth),
            TimeSpan.FromSeconds(8))).Body);
        Assert.Equal(0, rebirth.Result);
        var closing = Cut();
        var window = Window(opening, closing);
        var receipt = Assert.Single(window.Receipts);
        Assert.Equal("cd13b/pet-rebirth", EconomyReceiptReader.Read(Path.Combine(_directory, "ro3emu.db"), receipt.OperationId)!.CommandKind);
        var source = Assert.Single(window.Legs, leg => leg.Kind == EconomyLegKind.Source);
        Assert.Equal((1250701L, 2L), (source.Asset.Id, source.Delta));
        Assert.Equal(2, Session.Items.Where(i => i.Entry == 1250701).Sum(i => i.Stack));
        await bot.SendGameAsync(PetService.Rebirth, new PetRebirthReq { PetId = 10001 });
        var refused = PetRebirthResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(PetService.Rebirth),
            TimeSpan.FromSeconds(8))).Body);
        Assert.Equal(ErrorCodes.PetOperation, refused.Result);
        Assert.Equal(closing.Cut.CommitSequence, Cut().Cut.CommitSequence);
    }

    private Task ArmStageBarrier(TaskCompletionSource staged, TaskCompletionSource release,
        TaskCompletionSource? gmDeferred = null) => Service<GameLoop>().InvokeAsync(() =>
    {
        Service<AccountStore>().BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/cd13b/pet-combine") return Task.CompletedTask;
            staged.TrySetResult();
            return release.Task;
        };
        Service<OwnerRegistry>().DeferredForTest = origin =>
        { if (origin == "GM") gmDeferred?.TrySetResult(); };
        return Task.CompletedTask;
    });

    private Task ClearStageBarrier() => Service<GameLoop>().InvokeAsync(() =>
    {
        Service<AccountStore>().BeforeOwnedCommitForTest = null;
        Service<OwnerRegistry>().DeferredForTest = null;
        return Task.CompletedTask;
    });

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
