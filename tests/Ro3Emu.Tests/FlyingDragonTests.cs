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
/// The Starshatter ritual (FlyingDragonService): one per server, started at NPC 107 in Prontera with a Lord's Mane,
/// helped by others for Minted Coins, finished by its owner, cheered near NPC 108, and the dragon mailed at the end.
/// </summary>
public sealed class FlyingDragonTests : IAsyncLifetime
{
    private const int LordsMane = 1009015, MintedCoin = 2, DragonItem = 1501004, Prontera = 10100;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-dragon-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

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

    /// <summary>Into Prontera beside the world NPC <paramref name="npc"/> (107 the administrator, 108 the ceremony spot).</summary>
    private async Task Beside(BotClient bot, int npc)
    {
        var at = Service<WorldData>().Map(Prontera)!.Npcs.First(n => n.NpcId == npc);
        Assert.True(await Service<GameLoop>().InvokeAsync(() =>
            Service<SceneService>().EnterSceneAsync(Session(bot), Prontera, new MapPoint(at.X + 1, at.Y, at.Z, 0))));
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, p => p.SceneEntry == Prontera);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = Prontera });
        await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, p => p.SceneId == Prontera && p.Result == 0);
    }

    private static async Task<int> Ask(BotClient bot, uint id, IMessage req)
    {
        await bot.SendGameAsync(id, req);
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait);
        return FlyingDragonStartProduceRes.Parser.ParseFrom(e.Body).Result; // every reply is {result = 1}
    }

    private long Count(BotClient bot, int entry) => Session(bot).Items.Where(i => i.Entry == entry && i.Grid == 0).Sum(i => (long)i.Stack)
        + Session(bot).Progress!.Currency.GetValueOrDefault(entry);

    private MailService.Mailbox Box(BotClient bot) =>
        System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(Service<RoleStore>().LoadMailbox(Session(bot).Role!.Id) ?? "{}"u8.ToArray())
        ?? new MailService.Mailbox();

    [ClientDataFact]
    public async Task ARitualRunsFromTheManeThroughHelpAndTheCeremonyToTheDragon()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        // Away from the administrator, then without the mane.
        Assert.Equal(ErrorCodes.DragonPlace, await Ask(alice, FlyingDragonService.Start, new FlyingDragonStartProduceReq()));
        await Beside(alice, 107);
        Assert.Equal(ErrorCodes.DragonItems, await Ask(alice, FlyingDragonService.Start, new FlyingDragonStartProduceReq()));
        await Gm(alice, $"!item {LordsMane} 1");
        Assert.Equal(0, await Ask(alice, FlyingDragonService.Start, new FlyingDragonStartProduceReq()));
        Assert.Equal(0, Count(alice, LordsMane));
        long owner = Session(alice).Role!.Id;
        // Everyone online is told (the top button), Bob included.
        var told = await Next(bob, FlyingDragonService.Notify, FlyingDragonProductNotify.Parser,
            n => n.Info.Status == FlyingDragonProductStatus.Product);
        Assert.Equal(owner, told.Info.OwnerRoleId);
        Assert.False(told.Info.ProductInfo.HasAssisted);
        // One ritual per server.
        await Gm(bob, $"!item {LordsMane} 1");
        await Beside(bob, 107);
        Assert.Equal(ErrorCodes.DragonBusy, await Ask(bob, FlyingDragonService.Start, new FlyingDragonStartProduceReq()));

        // Help: never the owner, once per helper, one Minted Coin each.
        Assert.Equal(ErrorCodes.DragonSelf, await Ask(alice, FlyingDragonService.Assist, new FlyingDragonAssistProduceReq()));
        long coins = Count(bob, MintedCoin);
        Assert.Equal(0, await Ask(bob, FlyingDragonService.Assist, new FlyingDragonAssistProduceReq()));
        Assert.Equal(coins + 1, Count(bob, MintedCoin));
        Assert.Equal(ErrorCodes.DragonAssisted, await Ask(bob, FlyingDragonService.Assist, new FlyingDragonAssistProduceReq()));

        // The ceremony only once the progress is full, and only by its owner.
        Assert.Equal(ErrorCodes.DragonDone, await Ask(alice, FlyingDragonService.Complete, new FlyingDragonCompleteProduceReq()));
        await Gm(alice, "!dragon progress 10000");
        Assert.Equal(ErrorCodes.DragonSelf, await Ask(bob, FlyingDragonService.Complete, new FlyingDragonCompleteProduceReq()));
        Assert.Equal(0, await Ask(alice, FlyingDragonService.Complete, new FlyingDragonCompleteProduceReq()));
        await Next(bob, FlyingDragonService.Notify, FlyingDragonProductNotify.Parser,
            n => n.Info.Status == FlyingDragonProductStatus.Ceremony && n.Info.CeremonyInfo.CanThumbsUp);

        // Cheers: near the ceremony spot, once, never the owner.
        Assert.Equal(ErrorCodes.DragonSelf, await Ask(alice, FlyingDragonService.ThumbsUp, new FlyingDragonCeremonyThumbsUpReq()));
        await Beside(bob, 108);
        Assert.Equal(0, await Ask(bob, FlyingDragonService.ThumbsUp, new FlyingDragonCeremonyThumbsUpReq()));
        Assert.Equal(ErrorCodes.DragonLiked, await Ask(bob, FlyingDragonService.ThumbsUp, new FlyingDragonCeremonyThumbsUpReq()));

        // The end: the dragon for the owner by mail, everyone back to Idle.
        await Gm(alice, "!dragon end");
        await Next(bob, FlyingDragonService.Notify, FlyingDragonProductNotify.Parser, n => n.Info.Status == FlyingDragonProductStatus.Idle);
        Assert.Contains(Box(alice).Letters, l => l.Attachments.Any(a => a.Entry == DragonItem));
        // The same role waits DragonTaskWaitTime before its next ritual.
        await Gm(alice, $"!item {LordsMane} 1");
        Assert.Equal(ErrorCodes.DragonCooldown, await Ask(alice, FlyingDragonService.Start, new FlyingDragonStartProduceReq()));
    }

    [ClientDataFact]
    public async Task ARitualThatRunsOutReturnsTheManeByMail()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, $"!item {LordsMane} 1");
        await Beside(alice, 107);
        Assert.Equal(0, await Ask(alice, FlyingDragonService.Start, new FlyingDragonStartProduceReq()));
        await Gm(alice, "!dragon timeout");
        await Next(alice, FlyingDragonService.Notify, FlyingDragonProductNotify.Parser, n => n.Info.Status == FlyingDragonProductStatus.Idle);
        Assert.Contains(Box(alice).Letters, l => l.TemplateId == 1114 && l.Attachments.Any(a => a.Entry == LordsMane));
    }
}
