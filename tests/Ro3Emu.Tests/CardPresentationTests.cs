using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>卡片進階的封包順序；客戶端動畫由成功回應與卡片資料變動共同觸發。</summary>
public sealed class CardPresentationTests : IAsyncLifetime
{
    private const int Lunatic = 1420100, Fragment = 1200500, Dagger = 2010121;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-card-presentation-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _httpUrl = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
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

    private async Task<BotClient> InWorld()
    {
        var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task Gm(string command)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_httpUrl}/ro3emu/gm?account=alice", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static void AssertPresentationOrder(BotClient bot, int start, bool replyFirst)
    {
        var events = bot.Inbox.Snapshot().Skip(start).ToArray();
        int reply = Array.FindIndex(events, e => e.IsGame(CardService.StarUp));
        int items = Array.FindIndex(events, e => e.IsGame(CharacterService.ItemsSync));
        Assert.True(reply >= 0 && items >= 0, "進階成功回應與 5009 卡片資料變動都必須送達");
        Assert.Equal(replyFirst, reply < items);
    }

    [ClientDataFact]
    public async Task PackStarUpSynchronizesCardBeforeSuccessReply()
    {
        await using var alice = await InWorld();
        await Gm($"!item {Lunatic} 3");
        await Gm($"!item {Fragment} 170");
        await Gm("!item 2 10000");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
        long guid = session.Items.First(i => i.Grid == 0 && i.Entry == Lunatic).Guid;
        await alice.SendGameAsync(CardService.LevelUp, new CardLevelUpReq { SourceType = CardLevelSourceType.Pack, Guid = guid });
        var level = await Next(alice, CardService.LevelUp, CardLevelUpResp.Parser);
        Assert.Equal(0, level.Result);
        Assert.Equal(CardLevelSourceType.Pack, level.SourceType);
        Assert.Equal(guid, level.Guid);
        Assert.Equal(1, level.NewLevel);

        int start = alice.Inbox.Snapshot().Count;
        using var reply = alice.Inbox.Expect(e => e.IsGame(CardService.StarUp));
        using var items = alice.Inbox.Expect(e => e.IsGame(CharacterService.ItemsSync));
        await alice.SendGameAsync(CardService.StarUp, new CardStarUpReq { SourceType = CardLevelSourceType.Pack, Guid = guid });
        var response = CardStarUpResp.Parser.ParseFrom((await reply.WaitAsync(Wait)).Body);
        await items.WaitAsync(Wait);
        Assert.Equal(0, response.Result);
        Assert.Equal(CardLevelSourceType.Pack, response.SourceType);
        Assert.Equal(guid, response.Guid);
        Assert.Equal(1, response.NewStarNum);
        AssertPresentationOrder(alice, start, replyFirst: false);
    }

    [ClientDataFact]
    public async Task SocketedStarUpRepliesBeforeCardStorageUpdateForAnimation()
    {
        await using var alice = await InWorld();
        var first = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long dagger = first.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == Dagger).ItemGuid;
        await Gm("!lvl 10");
        await Gm($"!item {Lunatic} 2");
        await Gm($"!item {Fragment} 170");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
        long card = session.Items.First(i => i.Grid == 0 && i.Entry == Lunatic).Guid;
        await alice.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await alice.SendGameAsync(CardService.SlotInsert,
            new EquipmentCardInsertReq { EquipPos = (EquipGridType)1, SlotIndex = 1, CardGuid = card });
        long socketGuid = (await Next(alice, CardService.SlotInsert, EquipmentCardInsertResp.Parser)).CardGuid;
        await alice.SendGameAsync(CardService.LevelUp,
            new CardLevelUpReq { SourceType = CardLevelSourceType.Equip, Guid = socketGuid, GridType = (EquipGridType)1, SlotIndex = 1 });
        var level = await Next(alice, CardService.LevelUp, CardLevelUpResp.Parser);
        Assert.Equal(0, level.Result);
        Assert.Equal(CardLevelSourceType.Equip, level.SourceType);
        Assert.Equal(socketGuid, level.Guid);
        Assert.Equal(1, level.SlotIndex);
        Assert.Equal((EquipGridType)1, level.GridType);
        Assert.Equal(1, level.NewLevel);

        int start = alice.Inbox.Snapshot().Count;
        using var reply = alice.Inbox.Expect(e => e.IsGame(CardService.StarUp));
        using var items = alice.Inbox.Expect(e => e.IsGame(CharacterService.ItemsSync));
        await alice.SendGameAsync(CardService.StarUp,
            new CardStarUpReq { SourceType = CardLevelSourceType.Equip, Guid = socketGuid, GridType = (EquipGridType)1, SlotIndex = 1 });
        var response = CardStarUpResp.Parser.ParseFrom((await reply.WaitAsync(Wait)).Body);
        var cardUpdate = DBItemsInfoIncrement.Parser.ParseFrom((await items.WaitAsync(Wait)).Body);
        Assert.Equal(0, response.Result);
        Assert.Equal(CardLevelSourceType.Equip, response.SourceType);
        Assert.Equal(socketGuid, response.Guid);
        Assert.Equal(1, response.SlotIndex);
        Assert.Equal((EquipGridType)1, response.GridType);
        Assert.Equal(1, response.NewStarNum);
        Assert.True(cardUpdate.StoragePack?.All?.CardInfo.ContainsKey(1) == true
            || cardUpdate.StoragePack?.Inc?.CardInfo.ContainsKey(1) == true,
            "裝備卡片的 5009 必須含孔位資料，才能喚起客戶端綁定更新");
        AssertPresentationOrder(alice, start, replyFirst: true);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository data/config");
    }
}
