using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// 31323 on award boxes (ItemConfig _iAward), _kUseTimes limits on the realm's 05:00 game day and Monday 05:00 week,
/// and the level/job/sex/map refusals, against the exported client tables.
/// </summary>
public sealed class ItemBoxTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3emu-boxes-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private readonly RealmClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    /// <summary>A realm at UTC-03 (unlike the test host): the periods must follow the realm's zone.</summary>
    private sealed class RealmClock : TimeProvider
    {
        private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Realm -03", TimeSpan.FromHours(-3), "Realm -03", "Realm -03");
        public DateTimeOffset Now = new(2026, 10, 13, 23, 30, 0, TimeSpan.FromHours(-3)); // Tuesday 23:30 realm time
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private async Task<BotClient> Enter(string name, int sex = 1, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name, sex)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private long RoleId(BotClient bot) => (long)bot.Self!.Uuid;

    private long Box(BotClient bot, int entry) =>
        Service<ItemStore>().Load(RoleId(bot)).First(i => i.Entry == entry && i.Grid == 0).Guid;

    private long Count(BotClient bot, int entry) => Service<GameData>().Row("ItemConfig", entry) is { } row
        && GameData.Int(row, "_iType") == 1 ? Service<RoleStore>().LoadProgress(RoleId(bot)).Currency.GetValueOrDefault(entry)
        : Service<ItemStore>().Load(RoleId(bot)).Where(i => i.Entry == entry && i.Grid == 0).Sum(i => (long)i.Stack);

    private DBItemsInfo SavedBag(BotClient bot) => DBItemsInfo.Parser.ParseFrom(Service<RoleStore>().LoadBagState(RoleId(bot))!);

    private async Task<ItemUseResp> Use(BotClient bot, int entry, int count = 1, ItemUseParamInfo? param = null)
    {
        var req = new ItemUseReq { ItemGuid = Box(bot, entry), ItemCount = count };
        if (param != null) req.ItemParam.Add(param);
        await bot.SendGameAsync(CharacterService.ItemUse, req);
        return await Next(bot, CharacterService.ItemUse, ItemUseResp.Parser);
    }

    private static ItemUseParamInfo Pick(params (int Option, int Units)[] picks)
    {
        var param = new ItemUseParamInfo { SelfChooseGift = new ItemUseParamInfo.Types.SelfChooseGift() };
        foreach (var (option, units) in picks) param.SelfChooseGift.ChooseGift[option] = units;
        return param;
    }

    /// <summary>The next 5009 that sets the item's useTimes to <paramref name="used"/> (null: deletes it).</summary>
    private static Task<DBItemsInfoIncrement> UseTimesSync(BotClient bot, int entry, int? used) =>
        Next(bot, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.UseTimes.TryGetValue(entry, out var v) && (used == null ? v.Del : !v.Del && v.All == used));

    [ClientDataFact]
    public async Task APlainBoxPaysItsAwardOnceAndConsumesTheBox()
    {
        // 1300008 _iAward 10040002 -> AwardConfig 10040002 [[1430400,1],[1200500,100],[1,500]], no _iGroupUse.
        await using var bot = await Enter("Opener");
        await Gm(bot, "!item 1300008 3");
        long zeny = Count(bot, 1);
        var used = await Use(bot, 1300008);
        Assert.Equal(0, used.Result);
        Assert.Equal((1300008, 1, 0), (used.ItemId, used.ItemCount, used.ItemTimes));
        var shown = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser, m => !m.RewardType.Contains(1));
        Assert.Equal(new[] { 2 }, shown.RewardType.ToArray());
        Assert.Equal(new[] { (1, 500), (1200500, 100), (1430400, 1) },
            shown.Rewards.Select(r => (r.ItemEntry, r.Stack)).Order().ToArray());
        Assert.Equal(2, Count(bot, 1300008));
        Assert.Equal(1, Count(bot, 1430400));
        Assert.Equal(100, Count(bot, 1200500));
        Assert.Equal(zeny + 500, Count(bot, 1));
        Assert.Equal(ErrorCodes.Parameter, (await Use(bot, 1300008, 2)).Result); // _iGroupUse 0: one at a time
        Assert.Equal(2, Count(bot, 1300008));
    }

    [ClientDataFact]
    public async Task AGroupUseBoxOpensSeveralAtOnce()
    {
        // 1300017 _iGroupUse 1, AwardConfig 10040011 [[1256000,200]].
        await using var bot = await Enter("Hoarder");
        await Gm(bot, "!item 1300017 5");
        Assert.Equal(0, (await Use(bot, 1300017, 3)).Result);
        Assert.Equal(2, Count(bot, 1300017));
        Assert.Equal(600, Count(bot, 1256000));
    }

    [ClientDataFact]
    public async Task ABoxWithAClientHostedEffectStillOpens()
    {
        // 1300103 _iUseEffect [[142,...]] (PathFindingNormal, _iEXECHost 0) besides _iAward 40087; level 59.
        await using var bot = await Enter("Walker");
        await Gm(bot, "!lvl 59");
        await Gm(bot, "!item 1300103 1");
        Assert.Equal(0, (await Use(bot, 1300103)).Result);
        Assert.Equal(0, Count(bot, 1300103));
    }

    [ClientDataFact]
    public async Task AChoiceBoxPaysThePickedOptionsAndRefusesInvalidPicks()
    {
        // 1300028 SelfChooseGift (303), level 10, AwardConfig 10040040: eight weapons, option 1 = 2010120, 3 = 2030120.
        await using var bot = await Enter("Chooser");
        await Gm(bot, "!lvl 10");
        await Gm(bot, "!item 1300028 3");
        Assert.Equal(155, (await Use(bot, 1300028, 1)).Result);              // no pick
        Assert.Equal(155, (await Use(bot, 1300028, 1, Pick((9, 1)))).Result); // there are eight options
        Assert.Equal(155, (await Use(bot, 1300028, 1, Pick((0, 1)))).Result); // options count from 1
        Assert.Equal(155, (await Use(bot, 1300028, 3, Pick((1, 2)))).Result); // units must add up to the count
        Assert.Equal(155, (await Use(bot, 1300028, 1, new ItemUseParamInfo { IsFirstHeartBox = true })).Result);
        Assert.Equal(3, Count(bot, 1300028));

        var used = await Use(bot, 1300028, 3, Pick((1, 2), (3, 1)));
        Assert.Equal(0, used.Result);
        Assert.Equal(0, Count(bot, 1300028));
        Assert.Equal(2, Count(bot, 2010120));
        Assert.Equal(1, Count(bot, 2030120));
        Assert.Equal(0, Count(bot, 2020120));
        var shown = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser, m => !m.RewardType.Contains(1));
        Assert.Equal(new[] { 2 }, shown.RewardType.ToArray());
        Assert.Equal(3, shown.Rewards.Count); // each weapon instance with its own roll
    }

    [ClientDataFact]
    public async Task AHeartBoxShowsTheNoviceChestPerformance()
    {
        // 1300018 Novice Chest Lv. 5: _kAwardperformance [5] (ManifestTypeNewer), level 5, AwardConfig 1001201.
        await using var bot = await Enter("Novice");
        await Gm(bot, "!item 1300018 1");
        Assert.Equal(CharacterService.ErrLevelNeed, (await Use(bot, 1300018, 1, new ItemUseParamInfo { IsFirstHeartBox = true })).Result);
        await Gm(bot, "!lvl 5");
        long potions = Count(bot, 1000000);
        Assert.Equal(0, (await Use(bot, 1300018, 1, new ItemUseParamInfo { IsFirstHeartBox = true })).Result);
        var shown = await Next(bot, CharacterService.GetRewardNotify, GetRewardNotify.Parser, m => !m.RewardType.Contains(1));
        Assert.Equal(new[] { 5 }, shown.RewardType.ToArray());
        Assert.Equal(0, Count(bot, 1300018));
        Assert.Equal(1, Count(bot, 1300019)); // the next chest
        Assert.Equal(potions + 5, Count(bot, 1000000));
    }

    [ClientDataFact]
    public async Task ADailyUseLimitResetsAtFiveInTheRealmZoneAndSurvivesRelog()
    {
        // 1300000 _kUseTimes [1,10]: ten a day (enum_ItemUseLimit.Day); level 10, sex 1, jobs [1000,5100,5200,5300].
        await using (var bot = await Enter("Daily"))
        {
            await Gm(bot, "!lvl 10");
            await Gm(bot, "!item 1300000 12");
            for (int n = 1; n <= 10; n++)
            {
                var used = await Use(bot, 1300000);
                Assert.Equal(0, used.Result);
                Assert.Equal(n, used.ItemTimes);
            }
            await UseTimesSync(bot, 1300000, 10);
            Assert.Equal(CharacterService.ErrUseTimes, (await Use(bot, 1300000)).Result);
            Assert.Equal(2, Count(bot, 1300000));
            Assert.Equal(10, SavedBag(bot).UseTimes[1300000]);
        }

        await using var again = await Enter("Daily", create: false);
        await UseTimesSync(again, 1300000, 10);
        Assert.Equal(CharacterService.ErrUseTimes, (await Use(again, 1300000)).Result);
        _clock.Now = _clock.Now.AddHours(5).AddMinutes(29); // Wednesday 04:59: still Tuesday's game day
        Assert.Equal(CharacterService.ErrUseTimes, (await Use(again, 1300000)).Result);

        _clock.Now = _clock.Now.AddMinutes(2); // 05:01: the online role is told the count is gone
        await UseTimesSync(again, 1300000, null);
        var fresh = await Use(again, 1300000);
        Assert.Equal(0, fresh.Result);
        Assert.Equal(1, fresh.ItemTimes);
        await UseTimesSync(again, 1300000, 1);
    }

    [ClientDataFact]
    public async Task AWeeklyUseLimitRunsFromMondayFive()
    {
        // 1302000 Standard Compensation Chest: _kUseTimes [2,20], level 30, _iGroupUse 1, _kAwardperformance [5].
        await using var bot = await Enter("Weekly");
        await Gm(bot, "!lvl 30");
        await Gm(bot, "!item 1302000 22");
        Assert.Equal(CharacterService.ErrUseTimes, (await Use(bot, 1302000, 21)).Result);
        var used = await Use(bot, 1302000, 20, new ItemUseParamInfo { IsFirstHeartBox = true });
        Assert.Equal(0, used.Result);
        Assert.Equal(20, used.ItemTimes);
        Assert.Equal(CharacterService.ErrUseTimes, (await Use(bot, 1302000)).Result);
        _clock.Now = new DateTimeOffset(2026, 10, 19, 4, 59, 0, TimeSpan.FromHours(-3)); // Monday before 05:00
        Assert.Equal(CharacterService.ErrUseTimes, (await Use(bot, 1302000)).Result);
        _clock.Now = _clock.Now.AddMinutes(2);
        Assert.Equal(0, (await Use(bot, 1302000)).Result);
        Assert.Equal(1, Count(bot, 1302000));
    }

    [ClientDataFact]
    public async Task LevelJobSexAndMapRestrictionsRefuseBeforeTheItemIsSpent()
    {
        await using var bot = await Enter("Restricted");
        await Gm(bot, "!item 1300007 1"); // Golden Thief Bug's Gift: _iLevelNeed 10
        Assert.Equal(CharacterService.ErrLevelNeed, (await Use(bot, 1300007)).Result);
        await Gm(bot, "!lvl 25");
        await Gm(bot, "!item 1300003 1"); // _kJobNeed [4200]
        Assert.Equal(CharacterService.ErrJobNeed, (await Use(bot, 1300003)).Result);
        Assert.Equal((1, 1), (Count(bot, 1300007), Count(bot, 1300003)));

        await using var other = await Enter("Lady", sex: 2);
        await Gm(other, "!lvl 10");
        await Gm(other, "!item 1300000 1"); // _iSexNeed 1
        Assert.Equal(CharacterService.ErrSexNeed, (await Use(other, 1300000)).Result);

        // Fly Wing 1002001 _kUseLimit [1001]: ItemUseLimitConfig 1001 _iLimitType 1, maps [10110,10111]. Not enforced: no client
        // code reads it, so the direction (forbidden or only allowed) is unknown; the wing works on the starting maps.
        await Gm(bot, "!item 1002001 1");
        using (var sw = bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine)))
        {
            await Gm(bot, "!warp 10110");
            await sw.WaitAsync(Wait);
        }
        using (var self = bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf)))
        {
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10110 });
            await self.WaitAsync(Wait);
        }
        Assert.Equal(10110, await Service<GameLoop>().InvokeAsync(() => Task.FromResult(
            Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null).SceneEntry)));
        Assert.Equal(0, (await Use(bot, 1002001)).Result);
        Assert.Equal(0, Count(bot, 1002001));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
