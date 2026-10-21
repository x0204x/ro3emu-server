using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The login 5009 card pack must be sortable by the client. LC_DAL_Knapsack's 5009 handler sorts pack 3 with
/// LC_ItemHelper.SortFuncs[4] (and the tab's alternative [5]); both compare kCardInfo.iCardStar, which
/// LC_ItemProtoParse.ParseItemBaseInfoByPre fills only for enum_ItemType.Card (ParseCardLevelInfo defaults a missing
/// 502 to 0). A non-card in pack 3 has iCardStar nil, so with a same-quality card the sort throws and the bag is lost.
/// </summary>
public sealed class CardPackSyncTests : IAsyncLifetime
{
    private const int Lunatic = 1420100, Mimic = 1430606, Fragment = 1200500;
    private const int ItemTypeCard = 4;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-cardpack-" + Guid.NewGuid().ToString("N"));
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

    [ClientDataFact]
    public async Task LoginCardPackHoldsOnlyCardsWhenFragmentAndUnupgradedCardShareAQuality()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        // The failing in-game bag: an upgraded Q2 card, a fresh Q3 card and the Q3 Blue-Green Card Fragment.
        Assert.Equal(GameData.Int(data.Row("ItemConfig", Mimic)!.Value, "_iQuality"), GameData.Int(data.Row("ItemConfig", Fragment)!.Value, "_iQuality"));
        Assert.Equal(3, GameData.Int(data.Row("ItemConfig", Fragment)!.Value, "_iBagTab"));
        await using (var alice = await InWorld("Alice"))
        {
            await Gm("alice", $"!item {Lunatic} 1");
            await Gm("alice", $"!item {Fragment} 249");
            await Gm("alice", $"!item {Mimic} 1");
            await alice.Inbox.WaitAsync(e => e.IsGame(CharacterService.ItemsSync)
                && DBItemsInfoIncrement.Parser.ParseFrom(e.Body).ToString().Contains(Mimic.ToString()), Wait);
        }
        await using var again = await InWorld("Alice", create: false);
        var full = DBItemsInfoIncrement.Parser.ParseFrom((await again.Inbox.WaitAsync(e => e.IsGame(CharacterService.ItemsSync), Wait)).Body);

        AssertCardPackSortable(data, full);
        var pack3 = full.PackInfo[3].All.Items.Values;
        Assert.Contains(pack3, i => i.ItemEntry == Mimic);
        Assert.Contains(pack3, i => i.ItemEntry == Lunatic);
        Assert.Equal(249, full.PackInfo[1].All.Items.Values.Where(i => i.ItemEntry == Fragment).Sum(i => i.Stack));
    }

    [ClientDataFact]
    public async Task SavedNonCardInCardPackMovesToItemPackBeforeLoginSync()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        var items = _app.Services.GetRequiredService<ItemStore>();
        long role;
        await using (var alice = await InWorld("Alice"))
            role = (long)alice.Self!.Uuid;
        // Rows saved before the fix: the fragment followed _iBagTab=3 into the card pack beside a same-quality card.
        items.Insert(role, new InvItem { Entry = Mimic, Stack = 1, Pack = 3, Sort = 1, Attrs = new() { [501] = 0, [502] = 0, [530] = 0 } });
        items.Insert(role, new InvItem { Entry = Fragment, Stack = 40, Pack = 3, Sort = 2 });

        await using var again = await InWorld("Alice", create: false);
        var full = DBItemsInfoIncrement.Parser.ParseFrom((await again.Inbox.WaitAsync(e => e.IsGame(CharacterService.ItemsSync), Wait)).Body);

        AssertCardPackSortable(data, full);
        Assert.Contains(full.PackInfo[3].All.Items.Values, i => i.ItemEntry == Mimic);
        Assert.Equal(40, full.PackInfo[1].All.Items.Values.Where(i => i.ItemEntry == Fragment).Sum(i => i.Stack));
        var saved = items.Load(role).Single(i => i.Entry == Fragment);
        Assert.Equal(1, saved.Pack);
        Assert.Equal(1, full.PackInfo[1].All.Items.Values.Count(i => i.ItemEntry == Fragment));
        Assert.Equal(saved.Sort, full.PackInfo[1].All.SortedIndex[saved.Guid]);
        Assert.Equal(full.PackInfo[1].All.SortedIndex.Count, full.PackInfo[1].All.SortedIndex.Values.Distinct().Count());
    }

    /// <summary>
    /// What the client derives from pack 3: kCardInfo.iCardStar = itemInfos[502].longVal or 0 for a Card, nil for
    /// anything else. Every pair the sort may compare must yield two non-nil stars, so every item must be a Card,
    /// and every card's star (when sent) must be a longVal so lua-protobuf yields a number.
    /// </summary>
    private static void AssertCardPackSortable(GameData data, DBItemsInfoIncrement full)
    {
        var pack3 = full.PackInfo[3].All.Items.Values.ToList();
        Assert.NotEmpty(pack3);
        foreach (var item in pack3)
        {
            Assert.True(GameData.Int(data.Row("ItemConfig", item.ItemEntry)!.Value, "_iType") == ItemTypeCard,
                $"pack 3 holds non-card {item.ItemEntry}: the client's card sort would read a nil iCardStar");
            foreach (int key in new[] { 501, 502, 530 })
                if (item.ItemInfos.TryGetValue(key, out var attr))
                    Assert.Equal(ItemAttrValue.ValOneofCase.LongVal, attr.ValCase);
        }
        foreach (var (pack, info) in full.PackInfo)
            if (pack != 3)
                Assert.DoesNotContain(info.All?.Items.Values ?? [], i =>
                    GameData.Int(data.Row("ItemConfig", i.ItemEntry)!.Value, "_iType") == ItemTypeCard);
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task Gm(string account, string command)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_httpUrl}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo data");
    }
}
