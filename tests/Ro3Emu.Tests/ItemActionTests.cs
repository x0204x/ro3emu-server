using System.Text;
using System.Text.Json.Nodes;
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
/// Item use actions owned by their services (CharacterService.ItemActions): 207 library page, 212 chat emoticons,
/// 620 profile decoration, 349 social outfit (with 83480 and others' view), 603 hatchery EXP, 112 Quest Fly Wing,
/// 105 horn, 113 guild badge, 114 guild member pack with the 32301 Guild Store, 118 Adventure Group card, and the
/// client-only OpenUI / UseSkill items.
/// </summary>
public sealed class ItemActionTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-itemaction-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private readonly TestClock _clock = new();

    private sealed class TestClock : TimeProvider
    {
        private long _ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _ms));
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ms, (long)by.TotalMilliseconds);
    }

    public async Task InitializeAsync()
    {
        var (h, m, g) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = h; o.MicroGatePort = m; o.GameGatePort = g;
            o.GuildBlockedWords = ["ban"];
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{h}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await b.LoginAsync(name.ToLowerInvariant(), "pw"); await b.EnterGateAsync();
        long id = create ? (await b.CreateRoleAsync(name)).RoleBaseInfo.Id : b.Roles.Single().Id;
        await b.EnterWorldAsync(id); await WorldSettle.StallsAsync(_app.Services);
        return b;
    }

    private async Task<BotClient> Relogin(BotClient bot, string name)
    {
        await bot.DisposeAsync();
        await WaitUntil(() => !Sessions.All().Any(s => s.Account == name.ToLowerInvariant() && s.Role != null), name + " still online");
        return await InWorld(name, create: false);
    }

    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> p, Func<T, bool>? match = null) where T : IMessage<T>
    { var e = await b.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(p.ParseFrom(e.Body))), Wait); return p.ParseFrom(e.Body); }

    private async Task Gm(string account, string command)
    { using var h = new HttpClient(); (await h.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode(); }

    private SessionRegistry Sessions => _app.Services.GetRequiredService<SessionRegistry>();
    private RoleStore Roles => _app.Services.GetRequiredService<RoleStore>();
    private GameSession Session(string account) => Sessions.All().Single(s => s.Account == account && s.Role != null);
    private static long RoleId(BotClient bot) => (long)bot.Self!.Uuid;

    private async Task<T> Read<T>(string account, Func<GameSession, T> read)
    {
        T value = default!;
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => { value = read(Session(account)); return Task.CompletedTask; });
        return value;
    }

    private async Task WaitUntil(Func<bool> done, string what)
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        for (var until = DateTime.UtcNow + Wait; ; await Task.Delay(20))
        {
            bool ok = false;
            await loop.InvokeAsync(() => { ok = done(); return Task.CompletedTask; });
            if (ok) return;
            Assert.True(DateTime.UtcNow < until, what);
        }
    }

    private long Count(BotClient bot, int entry) =>
        _app.Services.GetRequiredService<ItemStore>().Load(RoleId(bot)).Where(i => i.Entry == entry && i.Grid == 0).Sum(i => (long)i.Stack);

    /// <summary>31323 of one unit of <paramref name="entry"/> from the bag; the reply's result.</summary>
    private async Task<int> Use(BotClient bot, int entry, params ItemUseParamInfo[] param)
    {
        long guid = _app.Services.GetRequiredService<ItemStore>().Load(RoleId(bot)).First(i => i.Entry == entry && i.Grid == 0).Guid;
        var req = new ItemUseReq { ItemGuid = guid, ItemCount = 1 };
        req.ItemParam.AddRange(param);
        using var reply = bot.Inbox.Expect(e => e.IsGame(CharacterService.ItemUse));
        await bot.SendGameAsync(CharacterService.ItemUse, req);
        return ItemUseResp.Parser.ParseFrom((await reply.WaitAsync(Wait)).Body).Result;
    }

    private static async Task<T> Request<T>(BotClient bot, uint id, IMessage body, MessageParser<T> parser) where T : IMessage<T>
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(id));
        await bot.SendGameAsync(id, body);
        return parser.ParseFrom((await reply.WaitAsync(Wait)).Body);
    }

    // ------------------------------------------------------------------ 207 library page

    [ClientDataFact]
    public async Task LibraryPageItemAddsItsChapterOnceAndKeepsItAfterRelogin()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 28"); // the page item's _iLevelNeed
        await Gm("alice", "!item 1007011 2");
        await Gm("alice", "!item 1007062 1");
        Assert.Equal(0, await Use(alice, 1007011));
        var page = await Next(alice, CardService.LibrarySync, DBLibraryInfoIncrement.Parser, m => m.Pages.ContainsKey(10011));
        Assert.Equal(LibraryPageState.Gain, page.Pages[10011].All.State);
        Assert.Equal(1, Count(alice, 1007011));
        Assert.Equal(LibraryPageState.Gain, DBLibraryInfo.Parser.ParseFrom(Roles.LoadLibrary(RoleId(alice))!).Pages[10011].State);
        Assert.Equal(ErrorCodes.AlreadyUnlocked, await Use(alice, 1007011)); // the page is held: the item stays
        Assert.Equal(1, Count(alice, 1007011));
        Assert.Equal(ErrorCodes.ItemNoConfig, await Use(alice, 1007062)); // LibraryPageConfig has no page 10062
        Assert.Equal(1, Count(alice, 1007062));

        await using var again = await Relogin(alice, "Alice");
        var full = await Next(again, CardService.LibrarySync, DBLibraryInfoIncrement.Parser, m => m.SyncAllMark);
        Assert.Equal(LibraryPageState.Gain, full.Pages[10011].All.State);
    }

    // ------------------------------------------------------------------ 212 chat emoticons

    [ClientDataFact]
    public async Task EmoticonItemUnlocksItsChatTabOnce()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!item 1220003 2");
        Assert.Equal(0, await Use(alice, 1220003));
        var full = await Next(alice, EmojiService.EmoticonSync, DBEmojiInfoIncrement.Parser, m => m.SyncAllMark);
        Assert.Equal(Enumerable.Range(50, 18).Select(i => (long)i), full.EmojiInfo.All.Value.Order()); // EmoticonTabConfig 2
        Assert.True(full.DungeonEmojiInstalls.ContainsKey(0)); // the full handler walks it unguarded
        Assert.Equal(1, Count(alice, 1220003));
        Assert.Equal(18, DBEmojiInfo.Parser.ParseFrom(Roles.LoadEmojiInfo(RoleId(alice))!).EmojiInfo.Count);
        Assert.Equal(EmojiService_ErrUnlocked, await Use(alice, 1220003));
        Assert.Equal(1, Count(alice, 1220003));

        await Gm("alice", "!item 1220004 1");
        Assert.Equal(0, await Use(alice, 1220004));
        var more = await Next(alice, EmojiService.EmoticonSync, DBEmojiInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.NotNull(more.EmojiInfo);

        await using var again = await Relogin(alice, "Alice");
        var reloaded = await Next(again, EmojiService.EmoticonSync, DBEmojiInfoIncrement.Parser, m => m.SyncAllMark);
        Assert.Equal(36, reloaded.EmojiInfo.All.Value.Count);
    }

    private const int EmojiService_ErrUnlocked = 2001;

    // ------------------------------------------------------------------ 620 profile decoration

    [ClientDataFact]
    public async Task DecorationItemUnlocksAProfileThemeTheEditorThenAccepts()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 20");
        await Gm("alice", "!item 2120001 2");
        await Gm("alice", "!item 1960003 1");
        Assert.Equal(0, await Use(alice, 2120001)); // [620,4,1003] Bouquet theme
        var unlock = await Next(alice, PersonalService.Sync, DBPersonalInfoIncrement.Parser,
            m => m.UnlockInfos.TryGetValue(4, out var theme) && (theme.All?.UnlockMap.ContainsKey(1003) == true
                || theme.Inc?.UnlockMap.ContainsKey(1003) == true));
        Assert.NotNull(unlock);
        Assert.Equal(1, Count(alice, 2120001));
        Assert.Equal(PersonalService.ErrUnlocked, await Use(alice, 2120001));
        Assert.Equal(1, Count(alice, 2120001));
        Assert.Equal(PersonalService.ErrConfig, await Use(alice, 1960003)); // action 50004 has no row
        Assert.Equal(1, Count(alice, 1960003));

        var plan = (await Read("alice", s => PersonalService.Current(s.Personal)!)).Clone();
        plan.ThemeId = 1003;
        var save = new UpdatePersonalAppearanceReq { PlanId = 1, Info = plan };
        Assert.Equal(0, (await Request(alice, PersonalService.UpdateAppearance, save, UpdatePersonalAppearanceResp.Parser)).Result);

        await using var again = await Relogin(alice, "Alice");
        var full = await Next(again, PersonalService.Sync, DBPersonalInfoIncrement.Parser, m => m.SyncAllMark);
        Assert.True(full.UnlockInfos[4].All.UnlockMap[1003]);
    }

    // ------------------------------------------------------------------ 349 social outfit

    [ClientDataFact]
    public async Task SocialOutfitItemsUnlockAreWornSeenByOthersAndRunOut()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Next(alice, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser, m => m.SyncAllMark);
        await Gm("alice", "!item 1910002 2");
        await Gm("alice", "!item 1910003 1");
        await Gm("alice", "!item 1950015 2");

        Assert.Equal(0, await Use(alice, 1910002)); // [349,10002]: head 100002, permanent
        var head = await Next(alice, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser, m => m.SocialOutfitContainer.ContainsKey(1));
        Assert.Equal(1, Count(alice, 1910002));
        Assert.Equal(3, (await Read("alice", s => s.SocialOutfit!)).SocialOutfitContainer[1].SocialOutfitInfos[100002].Status);
        Assert.NotNull(head);
        Assert.Equal(SocialOutfitService.ErrAlreadyUnlocked, await Use(alice, 1910002));
        Assert.Equal(1, Count(alice, 1910002));

        // 1910003 [[349,10003],[349,10004]]: 100003 for a male role, 100004 for a female one.
        int sex = await Read("alice", s => s.Role!.Sex);
        Assert.Equal(0, await Use(alice, 1910003));
        var owned = (await Read("alice", s => s.SocialOutfit!)).SocialOutfitContainer[1].SocialOutfitInfos;
        Assert.True(owned.ContainsKey(sex == 2 ? 100004 : 100003));
        Assert.False(owned.ContainsKey(sex == 2 ? 100003 : 100004));

        // 1950015 [349,50015]: honor 500015 for 86400 s; a second use extends it.
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        Assert.Equal(0, await Use(alice, 1950015));
        var timed = (await Read("alice", s => s.SocialOutfit!)).SocialOutfitContainer[5].SocialOutfitInfos[500015];
        Assert.Equal(2, timed.Status);
        Assert.Equal(now + 86400, timed.ExpireTimestamp);

        var equip = new EquipSocialOutfitReq { CfgId = 500015, Type = 5 };
        Assert.Equal(0, (await Request(alice, SocialOutfitService.Equip, equip, EquipSocialOutfitRes.Parser)).Err);
        await Next(alice, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser,
            m => m.SocialOutfitEquip.TryGetValue(5, out var worn) && (worn.All?.CfgId == 500015 || worn.Inc?.CfgId == 500015));
        var wrong = new EquipSocialOutfitReq { CfgId = 500013, Type = 5 }; // not owned
        Assert.Equal(ErrorCodes.Parameter, (await Request(alice, SocialOutfitService.Equip, wrong, EquipSocialOutfitRes.Parser)).Err);

        // Others see the worn outfits in the profile Base (RoleAdditionBaseInfo.socialOutfitData).
        var card = await bob.GetRoleFieldsAsync([RoleId(alice)], ["Base"]);
        var data = (Dictionary<object, object?>)((Dictionary<object, object?>)((object?[])card["Results"]!).Single()!)["Data"]!;
        var basic = RoleAdditionBaseInfo.Parser.ParseFrom((byte[])data["Base"]!);
        Assert.Equal(500015, basic.SocialOutfitData.SocialOutfit[5]);
        Assert.Equal(400000, basic.SocialOutfitData.SocialOutfit[4]);

        // A day later the honor runs out: the server drops it and the slot falls back to the default.
        _clock.Advance(TimeSpan.FromSeconds(86401));
        await Next(alice, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser,
            m => m.SocialOutfitEquip.TryGetValue(5, out var worn) && (worn.All?.CfgId == 500000 || worn.Inc?.CfgId == 500000));
        Assert.False((await Read("alice", s => s.SocialOutfit!)).SocialOutfitContainer.ContainsKey(5));

        await using var again = await Relogin(alice, "Alice");
        var full = await Next(again, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser, m => m.SyncAllMark);
        Assert.Equal(3, full.SocialOutfitContainer[1].All.SocialOutfitInfos[100002].Status);
        Assert.False(full.SocialOutfitContainer[5].All.SocialOutfitInfos.ContainsKey(500015));
    }

    // ------------------------------------------------------------------ 603 hatchery EXP

    [ClientDataFact]
    public async Task HatcheryByproductAddsRoomExpAndLevelsTheRoom()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest done 10104330");
        await Gm("alice", "!lvl 1");
        await Gm("alice", "!quest done 10105010");
        await Gm("alice", "!item 1250725 2"); // Pale Sediment [603,5]
        long exp = await Read("alice", s => s.Pets!.IncubationExp);
        Assert.Equal(1, await Read("alice", s => s.Pets!.IncubationLv));
        Assert.Equal(0, await Use(alice, 1250725));
        await Next(alice, PetService.PetSync, DBPetInfoIncrement.Parser, m => m.IncubationExp == exp + 5);
        Assert.Equal(1, Count(alice, 1250725));
        Assert.Equal(exp + 5, DBPetInfo.Parser.ParseFrom(Roles.LoadPets(RoleId(alice))!).IncubationExp);
        // Room 1 needs 10 EXP and costs nothing: the second use levels it (as a collect would).
        Assert.Equal(0, await Use(alice, 1250725));
        await WaitUntil(() => Session("alice").Pets!.IncubationLv == 2, "room level 2");
        Assert.Equal(2, DBPetInfo.Parser.ParseFrom(Roles.LoadPets(RoleId(alice))!).IncubationLv);
        Assert.Equal(0, Count(alice, 1250725));
    }

    // ------------------------------------------------------------------ 112 Quest Fly Wing

    [ClientDataFact]
    public async Task QuestFlyWingJumpsToItsPointAfterTheReply()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!item 1002005 1"); // [112,10111,2895]: this map
        await Gm("alice", "!item 1002006 1"); // [112,10220,1030301]: Payon Forest
        Assert.Equal(10111, await Read("alice", s => s.SceneEntry));
        var target = _app.Services.GetRequiredService<WorldData>().Map(10111)!.Points[2895];
        Assert.Equal(0, await Use(alice, 1002005));
        await WaitUntil(() => Math.Abs(Session("alice").Position.X - target.X) < 0.5f
            && Math.Abs(Session("alice").Position.Z - target.Z) < 0.5f, "teleported to point 2895");
        Assert.Equal(0, Count(alice, 1002005));

        await Task.Delay(200); // _kCDGroup [3,100]: the wings share a 100 ms group cooldown
        using var sw = alice.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine) && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == 10220);
        Assert.Equal(0, await Use(alice, 1002006));
        await sw.WaitAsync(Wait);
        Assert.Equal(0, Count(alice, 1002006));
    }

    // ------------------------------------------------------------------ 105 horn

    [ClientDataFact]
    public async Task HornShoutsToEveryoneOnline()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!lvl 10"); // the horn's _iLevelNeed
        await Gm("alice", "!item 1003101 2");
        ItemUseParamInfo Words(string words) => new()
        {
            ChatCustomizeData = new ChatCustomizeDada { SoundTransmission = new SoundTransmission { PlayerName = "x:y", Words = words } },
        };
        Assert.Equal(ErrorCodes.Parameter, await Use(alice, 1003101)); // the panel always sends the words
        Assert.Equal(ErrorCodes.Parameter, await Use(alice, 1003101, Words(" ")));
        Assert.Equal(ErrorCodes.Parameter, await Use(alice, 1003101, Words(new string('a', 51))));
        Assert.Equal(ErrorCodes.TextSensitive, await Use(alice, 1003101, Words("ban this")));
        Assert.Equal(2, Count(alice, 1003101));

        Assert.Equal(0, await Use(alice, 1003101, Words("Hello Prontera")));
        var shout = await Next(bob, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == 2);
        Assert.Equal(MsgSource.Player, shout.Source);
        Assert.Equal(RoleId(alice), shout.PlayerId);
        Assert.Equal($"{RoleId(alice)}:Alice", shout.ParamList[1].Params.Single());
        Assert.Equal("Hello Prontera", shout.ParamList[2].Params.Single());
        await Next(alice, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == 2);
        Assert.Equal(1, Count(alice, 1003101));
    }

    // ------------------------------------------------------------------ 113 / 114 / 32301 guild

    private static GuildCreateReq Guild(string name) => new()
    {
        Name = name, Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
    };

    /// <summary>Alice founds a guild, Bob joins as a Member (20); the guild holds <paramref name="funds"/> Guild Funds.</summary>
    private async Task<long> GuildOfTwo(BotClient alice, BotClient bob, long funds)
    {
        await Gm("alice", "!lvl 21");
        await Gm("bob", "!lvl 21");
        Assert.Equal(0, (await Request(alice, GuildService.Create, Guild("Astra"), GuildCreateResp.Parser)).Result);
        long guild = (await Next(alice, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid > 0)).Guid;
        var apply = await Request(bob, GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guild } }, GuildEnterApplyResp.Parser);
        Assert.Equal(0, Assert.Single(apply.ApplyResult).Result);
        var agree = new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { RoleId(bob) } };
        Assert.Equal(0, (await Request(alice, GuildService.ApplyOpe, agree, GuildEnterApplyOpeResp.Parser)).Result);
        var guilds = _app.Services.GetRequiredService<GuildStore>();
        if (funds > 0) await Gm("alice", $"!item 104 {funds}");
        for (int i = 0; i < 250 && guilds.Get(guild)!.Money < funds; i++) await Task.Delay(20);
        Assert.Equal(funds, guilds.Get(guild)!.Money);
        return guild;
    }

    [ClientDataFact]
    public async Task GuildBadgeItemUnlocksADesignTheGuildCanThenPick()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long guild = await GuildOfTwo(alice, bob, 0);
        await Gm("alice", "!item 1210901 2"); // [113,200201] God of Nature
        await Gm("bob", "!item 1210901 1");
        var locked = new GuildEditReq { Tp = (GuildEditReq.Types.EditType)3, Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200201 } };
        Assert.Equal(ErrorCodes.GuildIconLocked, (await Request(alice, GuildService.Edit, locked, GuildEditResp.Parser)).Result);

        Assert.Equal(GuildService.ErrStorePosition, await Use(bob, 1210901)); // a Member may not change the badge
        Assert.Equal(1, Count(bob, 1210901));
        Assert.Equal(0, await Use(alice, 1210901));
        var push = await Next(bob, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            m => m.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangeUnLockHeadIcon);
        Assert.Equal(200201, Assert.Single(push.UnLockHeadIconData).Id);
        Assert.Equal(0, Assert.Single(push.UnLockHeadIconData).Removetime);
        Assert.Equal(1, Count(alice, 1210901));
        Assert.Equal(ErrorCodes.AlreadyUnlocked, await Use(alice, 1210901));
        Assert.Equal(1, Count(alice, 1210901));

        Assert.Equal(0, (await Request(alice, GuildService.Edit, locked, GuildEditResp.Parser)).Result);
        Assert.Equal(200201, _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Icon);

        await using var again = await Relogin(alice, "Alice");
        var full = await Next(again, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid == guild);
        Assert.Equal(200201, Assert.Single(full.UnLockHeadIconData).Id);
    }

    [ClientDataFact]
    public async Task GuildStoreSellsMemberPacksWhichMailEveryMember()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long guild = await GuildOfTwo(alice, bob, 20000);
        var guilds = _app.Services.GetRequiredService<GuildStore>();
        Task<BuyGuildCommodityRes> Buy(BotClient who, int commodity, int count) => Request(who, GuildService.BuyCommodity,
            new BuyGuildCommodityReq { CommodityId = commodity, Count = count }, BuyGuildCommodityRes.Parser);

        Assert.Equal(GuildService.ErrStorePosition, (await Buy(bob, 27201, 1)).Result); // [9007,6,3]: GM to Vice GM
        Assert.Equal(GuildService.ErrStoreFunds, (await Buy(alice, 27203, 1)).Result); // 100000 funds
        var bought = await Buy(alice, 27201, 2);
        Assert.Equal((0, 27201, 2), (bought.Result, bought.CommodityId, bought.Count));
        Assert.Equal(20000 - 2 * 5000, guilds.Get(guild)!.Money);
        Assert.Equal(2, Count(alice, 1211101));
        var counts = await Next(bob, GuildService.BaseChange, GuildBaseInfoChangeNtf.Parser,
            m => m.Tp == GuildBaseInfoChangeNtf.Types.ChangeType.ChangeShopCommodityInfo);
        Assert.Equal(2, counts.ShopCommodityData.Single(c => c.Commodityid == 27201).Buycount);
        Assert.Equal(0, counts.ShopCommodityData.Single(c => c.Commodityid == 27202).Buycount);
        Assert.Equal(GuildService.ErrStoreLimit, (await Buy(alice, 27201, 2)).Result); // 3 a week, guild-wide
        Assert.Equal(0, (await Buy(alice, 27201, 1)).Result);
        Assert.Equal(GuildService.ErrStoreLimit, (await Buy(alice, 27201, 1)).Result);
        Assert.Equal(5000, guilds.Get(guild)!.Money);

        // [114,2004,1120,6,20]: MailConfig 1120 with MultiverseGroupConfig 2004 (one 1211104) to positions <= 20.
        Assert.Equal(0, await Use(alice, 1211101));
        Assert.Equal(2, Count(alice, 1211101));
        foreach (var member in new[] { alice, bob })
        {
            long role = RoleId(member);
            await WaitUntil(() => Letters(role).Any(), "pack letter");
            var letter = Assert.Single(Letters(role));
            Assert.Equal(1211104, (int)letter["Attachments"]![0]!["Entry"]!);
        }
        Assert.Equal(0, await Use(alice, 1211101));
        await WaitUntil(() => Letters(RoleId(bob)).Count() == 2, "second pack letter");

        await using var again = await Relogin(alice, "Alice");
        var full = await Next(again, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid == guild);
        Assert.Equal(3, full.ShopCommodityData.Single(c => c.Commodityid == 27201).Buycount);
    }

    private IEnumerable<JsonNode> Letters(long role) =>
        Roles.LoadMailbox(role) is { Length: > 0 } box
            ? JsonNode.Parse(Encoding.UTF8.GetString(box))!["Letters"]!.AsArray().OfType<JsonNode>()
                .Where(l => (int?)l["TemplateId"] == 1120)
            : [];

    // ------------------------------------------------------------------ 118 Adventure Group card

    [ClientDataFact]
    public async Task AdventureGroupCardItemUnlocksTheGroupsBackground()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!item 1200900 2");
        Assert.Equal(AdventureGroupService.ErrNotInGroup, await Use(alice, 1200900));
        Assert.Equal(2, Count(alice, 1200900));
        await Gm("alice", "!advguild create Wanderers");
        var groups = _app.Services.GetRequiredService<AdventureGroupStore>();
        await WaitUntil(() => groups.ForRole(RoleId(alice)) != null, "group created");

        Assert.Equal(0, await Use(alice, 1200900));
        var change = await Next(alice, AdventureGroupService.ChangeNotify, AdventureGroupDataChangeNotify.Parser,
            m => m.ChangeBaseData?.Availablebackground.Contains(101) == true);
        Assert.NotNull(change);
        Assert.Contains(101, groups.ForRole(RoleId(alice))!.AvailableBackgrounds);
        Assert.Equal(1, Count(alice, 1200900));
        Assert.Equal(AdventureGroupService.ErrCardUnlocked, await Use(alice, 1200900));
        Assert.Equal(1, Count(alice, 1200900));
    }

    // ------------------------------------------------------------------ client-only actions

    [ClientDataFact]
    public async Task ClientOnlyItemsAnswerAndOnlyTheManualIsSpent()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!item 1113008 1"); // [1,TOPBAR,UI_Sys_ShopPanel,...] the conch opens its own shop
        await Gm("alice", "!item 1201034 1"); // [2,5701601] UseSkill
        Assert.Equal(0, await Use(alice, 1113008));
        Assert.Equal(1, Count(alice, 1113008));
        Assert.Equal(0, await Use(alice, 1201034));
        Assert.Equal(0, Count(alice, 1201034));
    }
}
