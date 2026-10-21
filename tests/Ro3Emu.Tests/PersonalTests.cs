using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Profile card over the wire: 5016 DBPersonalInfo, 82553 appearance, 82559 preset, 82560 name, others' view.</summary>
public sealed class PersonalTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-personal-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const int Sticker = 1008001;

    public async Task InitializeAsync()
    {
        var (h, m, g) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o => { o.DataDir = _dir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data"); o.HttpPort = h; o.MicroGatePort = m; o.GameGatePort = g; });
        await _app.StartAsync(); _http = $"http://127.0.0.1:{h}";
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
        await b.EnterWorldAsync(id); return b;
    }

    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> p, Func<T, bool>? match = null) where T : IMessage<T>
    { var e = await b.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(p.ParseFrom(e.Body))), Wait); return p.ParseFrom(e.Body); }

    private async Task Gm(string account, string command)
    { using var h = new HttpClient(); (await h.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode(); }

    private GameSession Session(string account) => _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);

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

    private static async Task<UpdatePersonalAppearanceResp> Save(BotClient b, int plan, PersonalInfo info)
    {
        var req = new UpdatePersonalAppearanceReq { PlanId = plan, Info = info };
        await b.SendGameAsync(PersonalService.UpdateAppearance, req);
        var resp = await Next(b, PersonalService.UpdateAppearance, UpdatePersonalAppearanceResp.Parser);
        Assert.Equal(req, resp.Req);
        return resp;
    }

    private static async Task<Dictionary<object, object?>> Card(BotClient reader, long role, params string[] types)
    {
        var response = await reader.GetRoleFieldsAsync([role], types);
        var entry = (Dictionary<object, object?>)((object?[])response["Results"]!).Single()!;
        return (Dictionary<object, object?>)entry["Data"]!;
    }

    private static async Task<UpvotePersonalResp> Like(BotClient b, long role)
    {
        await b.SendGameAsync(PersonalService.Upvote, new UpvotePersonalReq { RoleId = role });
        var resp = await Next(b, PersonalService.Upvote, UpvotePersonalResp.Parser);
        Assert.Equal(role, resp.Req.RoleId);
        Assert.False(resp.IsCancle);
        return resp;
    }

    private static async Task<(int Count, PersonalUpvoteList? List)> Likes(BotClient reader, long role)
    {
        var seen = await Card(reader, role, "Base", "PersonalUpvoteCount", "PersonalUpvoteList");
        int count = PersonalUpvoteCount.Parser.ParseFrom((byte[])seen["PersonalUpvoteCount"]!).UpvoteCount;
        return (count, seen.TryGetValue("PersonalUpvoteList", out var list) ? PersonalUpvoteList.Parser.ParseFrom((byte[])list!) : null);
    }

    private static async Task<PopularityStarData> Popularity(BotClient reader, long role)
    {
        var bytes = (byte[])(await Card(reader, role, "Base", "PopularityStar"))["PopularityStar"]!;
        Assert.Equal(new[] { 8, 16, 24, 32, 40 }, ReadTags(bytes)); // every field present, the gift panel needs updateTime
        return PopularityStarData.Parser.ParseFrom(bytes);
    }

    private static int[] ReadTags(byte[] bytes)
    {
        var input = new CodedInputStream(bytes);
        var tags = new List<int>();
        for (uint tag; (tag = input.ReadTag()) != 0; input.SkipLastField()) tags.Add((int)tag);
        return tags.ToArray();
    }

    [ClientDataFact]
    public async Task LikesAreKeptOncePerCardCountedListedAndPersisted()
    {
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        var alice = await InWorld("Alice");
        long aliceId = (long)alice.Self!.Uuid, bobId = (long)bob.Self!.Uuid, carolId = (long)carol.Self!.Uuid;
        await Next(alice, PersonalService.Sync, DBPersonalInfoIncrement.Parser, i => i.SyncAllMark);

        Assert.Equal(PersonalService.ErrFunction, (await Like(alice, bobId)).Result); // Profile opens at base level 20
        foreach (string account in new[] { "alice", "carol" }) await Gm(account, "!lvl 20");
        await WaitUntil(() => Session("alice").Progress!.Level == 20 && Session("carol").Progress!.Level == 20, "level 20 not applied");

        Assert.Equal(PersonalService.ErrUpvoteSelf, (await Like(alice, aliceId)).Result);
        Assert.Equal(PersonalService.ErrParameter, (await Like(alice, 987654321)).Result);
        Assert.Equal((0, null), await Likes(carol, bobId));

        var liked = await Like(alice, bobId);
        Assert.Equal(0, liked.Result);
        // The 5016 increment after the reply carries the like's time, which lights the heart.
        long at = (await Next(alice, PersonalService.Sync, DBPersonalInfoIncrement.Parser, i => i.UpvoteTimes.ContainsKey(bobId)))
            .UpvoteTimes[bobId].All;
        Assert.True(at > 0);
        Assert.Equal(PersonalService.ErrUpvoted, (await Like(alice, bobId)).Result); // a like is kept, never toggled
        // PersonalLikeCD (1000 ms) between two likes, then the next card can be liked.
        Assert.Equal(PersonalService.ErrUpvoteCooldown, (await Like(alice, carolId)).Result);
        await Task.Delay(1100);
        Assert.Equal(0, (await Like(alice, carolId)).Result);

        var (count, list) = await Likes(carol, bobId);
        Assert.Equal(1, count);
        Assert.Equal(at, list!.Infos[aliceId]); // Like History: liker -> unix ms
        Assert.Equal(0, (await Like(carol, bobId)).Result);
        (count, list) = await Likes(alice, bobId);
        Assert.Equal(2, count);
        Assert.Equal(new[] { aliceId, carolId }.Order(), list!.Infos.Keys.Order());
        Assert.Equal(1, (await Likes(bob, carolId)).Count);
        Assert.Equal((0, null), await Likes(bob, aliceId));

        // Persisted on both sides: the liked card offline, the liker's own likes after a relogin.
        await bob.DisposeAsync();
        await WaitUntil(() => !_app.Services.GetRequiredService<SessionRegistry>().All().Any(s => s.Account == "bob" && s.Role != null), "bob still online");
        Assert.Equal(2, (await Likes(carol, bobId)).Count);
        await alice.DisposeAsync();
        await WaitUntil(() => !_app.Services.GetRequiredService<SessionRegistry>().All().Any(s => s.Account == "alice" && s.Role != null), "alice still online");
        await using var again = await InWorld("Alice", create: false);
        var reloaded = await Next(again, PersonalService.Sync, DBPersonalInfoIncrement.Parser, i => i.SyncAllMark);
        Assert.Equal(at, reloaded.UpvoteTimes[bobId].All);
        Assert.True(reloaded.UpvoteTimes[carolId].All > at);
        Assert.False(reloaded.UpvoteTimes.ContainsKey(0)); // no seed once the role has liked someone
        await Task.Delay(1100);
        Assert.Equal(PersonalService.ErrUpvoted, (await Like(again, bobId)).Result);
    }

    [ClientDataFact]
    public async Task FriendGiftsFeedPopularityAndTheCardShowsTheFashionScore()
    {
        await using var bob = await InWorld("Bob");
        await using var alice = await InWorld("Alice");
        long a = (long)alice.Self!.Uuid, b = (long)bob.Self!.Uuid;
        Assert.Equal(0, (await Popularity(alice, b)).Popularity);
        await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = a.ToString(), ["Target"] = b.ToString() });
        await bob.MicroAsync("FriendService", "accept", new() { ["Uid"] = b.ToString(), ["Applicant"] = a.ToString() });
        await WaitUntil(() => _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.FriendStore>().AreFriends(a, b), "not friends");

        // 1004000: _iUseEffect [107,10] intimacy, [108,10] popularity, [109,10] charity, per unit.
        await Gm("alice", "!item 1004000 3");
        await alice.SendGameAsync(FriendExtraService.GiveGift, new GiveFriendGiftReq { RoleId = b, ItemId = 1004000, Count = 3 });
        var gift = await Next(alice, FriendExtraService.GiveGift, GiveFriendGiftResp.Parser);
        Assert.Equal(0, gift.Result);
        Assert.Equal((30, 30), (gift.FriendGiftRecord.Popularity, gift.FriendGiftRecord.Affection));

        var received = await Popularity(alice, b);
        Assert.Equal((30, 30, 0), (received.Popularity, received.PopularityWeekly, received.Affection));
        Assert.True(received.UpdateTime > 0);
        var sent = await Popularity(bob, a);
        Assert.Equal((0, 30), (sent.Popularity, sent.Affection));

        // Weekly popularity counts from Monday 05:00; an older update leaves only the total.
        var star = new DBPopularityStar { Popularity = 30, PopularityWeekly = 30, UpdateTime = 1000 };
        var stale = PersonalService.Popularity(star.ToByteArray(), PersonalService.WeekStartMs(TimeProvider.System));
        Assert.Equal((30, 0), (stale.Popularity, stale.PopularityWeekly));

        // Fashion: permanently lit appearances' _iFashionScore (WardRobeConfig 10030417 scores 10).
        var data = _app.Services.GetRequiredService<GameData>();
        int Fashion(Dictionary<object, object?> seen) => RoleAdditionBaseInfo.Parser.ParseFrom((byte[])seen["Base"]!).FashionScore;
        long before = WardrobeService.FashionScore(data, Session("bob").Wardrobe);
        Assert.Equal(before, Fashion(await Card(alice, b, "Base")));
        await WaitUntil(() =>
        {
            Session("bob").Wardrobe!.AllLightUpAppearances[10030417] = new DBAppearanceInfo { AppearanceId = 10030417 };
            Session("bob").Wardrobe!.AllLightUpAppearances[10030419] = new DBAppearanceInfo { AppearanceId = 10030419, OverdueTimeMs = 1 };
            return true;
        }, "wardrobe not changed");
        Assert.Equal(before + 10, Fashion(await Card(alice, b, "Base"))); // the leased one does not count

        // Persisted: bob offline still shows his popularity.
        await bob.DisposeAsync();
        await WaitUntil(() => !_app.Services.GetRequiredService<SessionRegistry>().All().Any(s => s.Account == "bob" && s.Role != null), "bob still online");
        Assert.Equal(30, (await Popularity(alice, b)).Popularity);
    }

    [ClientDataFact]
    public async Task LoginSendsThreePresetsUnlocksAndASeededUpvoteMap()
    {
        await using var alice = await InWorld("Alice");
        var full = await Next(alice, PersonalService.Sync, DBPersonalInfoIncrement.Parser, i => i.SyncAllMark);
        Assert.Equal(1, full.CurPlanId);
        Assert.Equal(new[] { 1, 2, 3 }, full.PlanInfos.Keys.Order());
        var plan = full.PlanInfos[1].All;
        Assert.Equal(1001, plan.ThemeId); // PublicParamConfig PersonalDefaultTheme
        Assert.Equal(2001, plan.BackgroundId); // PersonalDefaultBackground
        Assert.Equal(3001, plan.Mood); // PersonalEmoEmpty
        Assert.Equal(new[] { 1 }, plan.ShowcaseVisibleList); // PersonalShowcaseDefault [6301], the card showcase
        Assert.Equal("", plan.PlanName);
        // Theme, background and action 1001 open at base level 1; action 1002 (walk) waits for level 50.
        Assert.True(full.UnlockInfos[4].All.UnlockMap[1001]);
        Assert.True(full.UnlockInfos[5].All.UnlockMap[2001]);
        Assert.False(full.UnlockInfos[4].All.UnlockMap.ContainsKey(1003)); // Fashion Shop theme
        Assert.Equal(new[] { 1001 }, full.UnlockInfos[6].All.UnlockMap.Keys);
        // No upvotes yet: a wire-only key keeps the map, the next increment deletes it.
        Assert.Equal(0, full.UpvoteTimes[0].All);
        var seed = await Next(alice, PersonalService.Sync, DBPersonalInfoIncrement.Parser, i => !i.SyncAllMark);
        Assert.True(seed.UpvoteTimes[0].Del);
        Assert.Empty(Session("alice").Personal!.UpvoteTimes);

        // 5044: the own card reads the worn title and honor; the defaults are worn and owned.
        var outfit = await Next(alice, SocialOutfitService.Sync, DBSocialOutfitIncrement.Parser, i => i.SyncAllMark);
        Assert.Equal(Session("alice").Role!.Sex == 2 ? 99999 : 99998, outfit.SocialOutfitEquip[1].All.CfgId); // the Novice head
        Assert.Equal(400000, outfit.SocialOutfitEquip[4].All.CfgId);
        Assert.Equal(500000, outfit.SocialOutfitEquip[5].All.CfgId);
        Assert.Equal(3, outfit.SocialOutfitContainer[4].All.SocialOutfitInfos[400000].Status);
        Assert.True(outfit.SocialOutfitContainer[1].All.SocialOutfitInfos.ContainsKey(outfit.SocialOutfitEquip[1].All.CfgId));

        await Gm("alice", "!lvl 50");
        var walk = await Next(alice, PersonalService.Sync, DBPersonalInfoIncrement.Parser, i => i.UnlockInfos.ContainsKey(6));
        Assert.True(walk.UnlockInfos[6].Inc.UnlockMap[1002].All);
    }

    [ClientDataFact]
    public async Task EditsAreCheckedPersistedAndShownToOthers()
    {
        await using var bob = await InWorld("Bob");
        var alice = await InWorld("Alice");
        long aliceId = (long)alice.Self!.Uuid;
        var plan = (await Next(alice, PersonalService.Sync, DBPersonalInfoIncrement.Parser, i => i.SyncAllMark)).PlanInfos[1].All;
        var edit = plan.Clone();
        edit.Sign = "Hello there";
        edit.Mood = 3002;

        Assert.Equal(PersonalService.ErrFunction, (await Save(alice, 1, edit)).Result); // Profile opens at base level 20
        await Gm("alice", "!lvl 20");
        await WaitUntil(() => Session("alice").Progress!.Level == 20, "level 20 not applied");

        Assert.Equal(PersonalService.ErrPreset, (await Save(alice, 4, edit)).Result);
        var locked = edit.Clone(); locked.ThemeId = 1003;
        Assert.Equal(PersonalService.ErrLocked, (await Save(alice, 1, locked)).Result);
        var longSign = edit.Clone(); longSign.Sign = new string('a', 31);
        Assert.Equal(PersonalService.ErrSignLength, (await Save(alice, 1, longSign)).Result);
        var badShowcase = edit.Clone(); badShowcase.ShowcaseVisibleList.Add(1);
        Assert.Equal(PersonalService.ErrParameter, (await Save(alice, 1, badShowcase)).Result);
        var stuck = edit.Clone(); stuck.DecalInfos.Add(new PersonalDecalInfo { DecalId = Sticker, ClientEditorData = new EditorDecals { IDecalId = Sticker } });
        Assert.Equal(PersonalService.ErrStickers, (await Save(alice, 1, stuck)).Result); // none owned
        await Gm("alice", $"!item {Sticker} 1");
        await WaitUntil(() => Session("alice").Progress!.Currency.GetValueOrDefault(Sticker) == 1, "sticker not granted");
        var twice = stuck.Clone(); twice.DecalInfos.Add(stuck.DecalInfos[0].Clone());
        Assert.Equal(PersonalService.ErrStickers, (await Save(alice, 1, twice)).Result); // one owned, two placed

        // The pose is a PerformanceConfig emote the role has unlocked; the editor preselects one (50001) even for a sign edit.
        var unlockedPoses = Session("alice").EmojiPlay!.EmojiUnlocks.ToHashSet();
        int lockedPose = _app.Services.GetRequiredService<GameData>().Table("PerformanceConfig").Values
            .Select(r => (int)GameData.Int(r, "_iID")).First(id => id > 0 && !unlockedPoses.Contains(id));
        var posed = stuck.Clone(); posed.RoleInfos.Add(new PersonalRoleInfo { ActionId = lockedPose });
        Assert.Equal(PersonalService.ErrLocked, (await Save(alice, 1, posed)).Result);
        Assert.Contains(50001, unlockedPoses);
        stuck.RoleInfos.Add(new PersonalRoleInfo { ActionId = 50001 });

        // The editor sends its localized default name for an unnamed preset; the stored name stays.
        stuck.PlanName = "Template 1";
        Assert.Equal(0, (await Save(alice, 1, stuck)).Result);
        var changed = await Next(alice, PersonalService.Sync, DBPersonalInfoIncrement.Parser, i => i.PlanInfos.ContainsKey(1));
        Assert.Equal("Hello there", changed.PlanInfos[1].Inc.Sign);
        Assert.Equal("", Session("alice").Personal!.PlanInfos[1].PlanName);
        Assert.Equal(Sticker, Session("alice").Personal!.PlanInfos[1].DecalInfos.Single().DecalId);
        Assert.Equal(1, Session("alice").Progress!.Currency[Sticker]); // placed, not spent

        // 82560 rename: PersonalTemplateLimit 7, not empty.
        await alice.SendGameAsync(PersonalService.UpdatePlanName, new UpdatePersonalPlanNameReq { PlanId = 2, PlanName = "Too long" });
        Assert.Equal(PersonalService.ErrNameLength, (await Next(alice, PersonalService.UpdatePlanName, UpdatePersonalPlanNameResp.Parser)).Result);
        await alice.SendGameAsync(PersonalService.UpdatePlanName, new UpdatePersonalPlanNameReq { PlanId = 2, PlanName = " " });
        Assert.Equal(PersonalService.ErrNameLength, (await Next(alice, PersonalService.UpdatePlanName, UpdatePersonalPlanNameResp.Parser)).Result);
        await alice.SendGameAsync(PersonalService.UpdatePlanName, new UpdatePersonalPlanNameReq { PlanId = 2, PlanName = "Mine" });
        var renamed = await Next(alice, PersonalService.UpdatePlanName, UpdatePersonalPlanNameResp.Parser);
        Assert.Equal(0, renamed.Result);
        Assert.Equal("Mine", renamed.Req.PlanName);
        Assert.Equal("Mine", (await Next(alice, PersonalService.Sync, DBPersonalInfoIncrement.Parser,
            i => i.PlanInfos.ContainsKey(2))).PlanInfos[2].Inc.PlanName);

        // Others: Base carries the card's avatar fields; "Personal" only when asked for and only a card the client parses.
        var seen = await Card(bob, aliceId, "Base", "Personal");
        var card = PersonalInfo.Parser.ParseFrom((byte[])seen["Personal"]!);
        Assert.Equal("Hello there", card.Sign);
        Assert.Equal(3002, card.Mood);
        Assert.Equal(Sticker, card.DecalInfos.Single().DecalId);
        Assert.False((await Card(bob, aliceId, "Base", "Name")).ContainsKey("Personal"));
        // Popularity and likes, asked for by the card, are present (zeros, explicit) so the card stops asking.
        var social = await Card(bob, aliceId, "Base", "PopularityStar", "PersonalUpvoteCount", "PersonalUpvoteList");
        Assert.Equal(0, PopularityStarData.Parser.ParseFrom((byte[])social["PopularityStar"]!).Popularity);
        Assert.Equal(new byte[] { 8, 0 }, (byte[])social["PersonalUpvoteCount"]!);
        Assert.False(social.ContainsKey("PersonalUpvoteList")); // an empty list would reach pairs(nil)
        Assert.False((await Card(bob, aliceId, "Base")).ContainsKey("PopularityStar"));

        // 82559 to preset 2 (no stickers): the client could not parse it, so it is not published.
        await alice.SendGameAsync(PersonalService.UpdateCurPlan, new UpdateCurPersonalPlanReq { PlanId = 5 });
        Assert.Equal(PersonalService.ErrPreset, (await Next(alice, PersonalService.UpdateCurPlan, UpdateCurPersonalPlanResp.Parser)).Result);
        await alice.SendGameAsync(PersonalService.UpdateCurPlan, new UpdateCurPersonalPlanReq { PlanId = 2 });
        var current = await Next(alice, PersonalService.UpdateCurPlan, UpdateCurPersonalPlanResp.Parser);
        Assert.Equal((0, 2), (current.Result, current.PlanId));
        Assert.Equal(2, (await Next(alice, PersonalService.Sync, DBPersonalInfoIncrement.Parser, i => i.HasCurPlanId)).CurPlanId);
        Assert.False((await Card(bob, aliceId, "Base", "Personal")).ContainsKey("Personal"));
        await alice.SendGameAsync(PersonalService.UpdateCurPlan, new UpdateCurPersonalPlanReq { PlanId = 1 });
        Assert.Equal(0, (await Next(alice, PersonalService.UpdateCurPlan, UpdateCurPersonalPlanResp.Parser)).Result);

        // Persisted: offline alice is still seen, and her next login gets the same presets.
        await alice.DisposeAsync();
        await WaitUntil(() => !_app.Services.GetRequiredService<SessionRegistry>().All().Any(s => s.Account == "alice" && s.Role != null), "alice still online");
        Assert.Equal("Hello there", PersonalInfo.Parser.ParseFrom((byte[])(await Card(bob, aliceId, "Base", "Personal"))["Personal"]!).Sign);
        await using var again = await InWorld("Alice", create: false);
        var reloaded = await Next(again, PersonalService.Sync, DBPersonalInfoIncrement.Parser, i => i.SyncAllMark);
        Assert.Equal(1, reloaded.CurPlanId);
        Assert.Equal("Hello there", reloaded.PlanInfos[1].All.Sign);
        Assert.Equal("Mine", reloaded.PlanInfos[2].All.PlanName);
    }

    /// <summary>
    /// 106902 CheckPersonalHomepageLike counts committed likes; 103019 CheckFriend reads the friend list (after login
    /// and on every relation change). Both are re-read after a relogin.
    /// </summary>
    [ClientDataFact]
    public async Task LikesAndFriendsMoveTheirQuests()
    {
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        var alice = await InWorld("Alice");
        long a = (long)alice.Self!.Uuid, b = (long)bob.Self!.Uuid, c = (long)carol.Self!.Uuid;
        await Gm("alice", "!lvl 20");
        await WaitUntil(() => Session("alice").Progress!.Level == 20, "level 20 not applied");
        await QuestProbe.AcceptAsync(_app, _http, "alice", 500201); // [106902,1] "Like Temporary Activity Quests"
        await QuestProbe.AcceptAsync(_app, _http, "alice", 100022); // [103019,3] "Have 3 friends"
        await QuestProbe.AtAsync(_app, "alice", 100022, 0);
        await QuestProbe.AtAsync(_app, "alice", 500201, 0);

        Assert.Equal(0, (await Like(alice, b)).Result);
        await QuestProbe.DoneAsync(_app, "alice", 500201);

        foreach (var (bot, id) in new[] { (bob, b), (carol, c) })
        {
            await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = a.ToString(), ["Target"] = id.ToString() });
            await bot.MicroAsync("FriendService", "accept", new() { ["Uid"] = id.ToString(), ["Applicant"] = a.ToString() });
        }
        await QuestProbe.AtAsync(_app, "alice", 100022, 2);

        await alice.DisposeAsync();
        await QuestProbe.OfflineAsync(_app, "alice");
        await using var again = await InWorld("Alice", create: false);
        await QuestProbe.AtAsync(_app, "alice", 100022, 2);
        await QuestProbe.DoneAsync(_app, "alice", 500201);
    }
}
