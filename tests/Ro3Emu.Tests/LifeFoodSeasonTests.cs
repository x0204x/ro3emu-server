using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>自造料理驗證賽季身分與交易；不代表原服配方、取得方式或玩家畫面驗收。</summary>
public sealed partial class LifeFoodSeasonTests : IAsyncLifetime
{
    private const int Food = 906941, Book = 101, Attack = 103, PerUse = 17, Limit = 5;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private string _fixture = "";
    private SyntheticHost _host = null!;
    private T Service<T>() where T : notnull => _host.Service<T>();
    private GameSession Session(long role) => Service<SessionRegistry>().All().Single(s => s.Role?.Id == role);

    public async Task InitializeAsync()
    {
        _fixture = CreateFixture();
        _host = await SyntheticHost.StartAsync(o =>
        { o.GameDataDir = _fixture; o.PlayBook = Book; o.SeasonStart = "2026-01-01"; }, new FoodClock());
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_fixture, true);
    }

    [Fact]
    public async Task SameSeasonReloginPreservesFoodCountAndCommittedAttributes()
    {
        long role, attack;
        await using (var bot = await _host.InWorldAsync("FoodSame"))
        {
            role = (long)bot.Self!.Uuid; var s = Session(role); attack = Power(s);
            await Give(s, 3);
            Assert.Equal(0, await Eat(bot, s, 2));
            Assert.Equal(attack + 2 * PerUse, Power(s));
            Assert.Equal(2, SavedLife(role).MagicFoodTimes[Food]);
            AssertIdentity(SavedRuntime(role), 1);
            await bot.HeartbeatAsync();
            Assert.Contains(bot.Inbox.Snapshot().Where(p => p.IsGame(LifeJobService.LifeSync))
                .Select(p => DBLifeJobInfoIncrement.Parser.ParseFrom(p.Body)),
                m => m.MagicFoodTimes.TryGetValue(Food, out var used) && used.All == 2);
        }
        await _host.RestartAsync();
        await using var again = await _host.InWorldAsync("FoodSame", create: false);
        var reloaded = Session(role);
        Assert.Equal(2, reloaded.LifeJob!.MagicFoodTimes[Food]);
        Assert.Equal(attack + 2 * PerUse, Power(reloaded));
        AssertIdentity(reloaded.LifeRuntime, 1);
        Assert.Equal(1, Bag(role));
    }

    [Fact]
    public async Task SameBookNextSeasonKeepsCapturedQuotaUntilNextLoginThenClears()
    {
        long role, attack;
        await using (var bot = await _host.InWorldAsync("FoodCycle"))
        {
            role = (long)bot.Self!.Uuid; var s = Session(role); attack = Power(s);
            await Give(s, Limit + 1); Assert.Equal(0, await Eat(bot, s, Limit));
            await Advance();
            Assert.Equal(2, Service<SeasonService>().SeasonId);
            Assert.Equal(1, s.SeasonState!.SeasonId);
            Assert.Equal(ErrorCodes.AttemptsExhausted, await Eat(bot, s, 1));
            Assert.Equal(Limit, SavedLife(role).MagicFoodTimes[Food]);
            AssertIdentity(s.LifeRuntime, 1);
            Assert.Equal(attack + Limit * PerUse, Power(s));
            Assert.Equal(1, Bag(role));
        }
        await _host.RestartAsync();
        await using var again = await _host.InWorldAsync("FoodCycle", create: false);
        var reloaded = Session(role);
        Assert.Empty(reloaded.LifeJob!.MagicFoodTimes);
        Assert.Empty(SavedLife(role).MagicFoodTimes);
        AssertIdentity(reloaded.LifeRuntime, 2);
        Assert.Equal(attack, Power(reloaded));
        Assert.Equal(1, Bag(role));
        Assert.Equal(0, await Eat(again, reloaded, 1));
        Assert.Equal(1, SavedLife(role).MagicFoodTimes[Food]);
        Assert.Equal(attack + PerUse, Power(reloaded));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(Book, 2)]
    [InlineData(102, 0)]
    public async Task LegacyMissingSeasonBindsWithoutGuessingSameBookHistory(int oldBook, int expectedUses)
    {
        long role, attack;
        await using (var bot = await _host.InWorldAsync("FoodLegacy"))
        {
            role = (long)bot.Self!.Uuid; var s = Session(role); attack = Power(s);
            await Give(s, 2); Assert.Equal(0, await Eat(bot, s, 2));
        }
        // 只移除新增身分欄位，模擬舊 JSON；既有跨 PlayBook 重置政策仍須保留。
        var legacy = JsonNode.Parse(Service<RoleStore>().LoadLifeRuntime(role)!)!.AsObject();
        Assert.True(legacy.Remove(nameof(LifeJobService.RuntimeState.FoodSeasonId)));
        legacy[nameof(LifeJobService.RuntimeState.FoodBook)] = oldBook;
        FixtureSql.Execute(Service<AccountStore>(), "UPDATE role SET life_runtime=$runtime WHERE id=$id",
            ("$runtime", JsonSerializer.SerializeToUtf8Bytes(legacy)), ("$id", role));
        await _host.RestartAsync();
        await using var again = await _host.InWorldAsync("FoodLegacy", create: false);
        var reloaded = Session(role);
        Assert.Equal(expectedUses, reloaded.LifeJob!.MagicFoodTimes.GetValueOrDefault(Food));
        Assert.Equal(expectedUses, SavedLife(role).MagicFoodTimes.GetValueOrDefault(Food));
        AssertIdentity(SavedRuntime(role), 1);
        Assert.Equal(attack + expectedUses * PerUse, Power(reloaded));
        Assert.Equal(0, Bag(role));
    }

    private Task Give(GameSession s, int count) => Service<GameLoop>().InvokeAsync(() =>
        Service<CharacterService>().GrantAsync(s, [(Food, count)]));

    private async Task<int> Eat(BotClient bot, GameSession s, int count)
    {
        await bot.SendGameAsync(CharacterService.ItemUse,
            new ItemUseReq { ItemGuid = s.Items.Single(i => i.Entry == Food).Guid, ItemCount = count });
        return ItemUseResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(p => p.IsGame(CharacterService.ItemUse), Wait)).Body).Result;
    }

    private async Task Advance()
    {
        string result = await Service<SeasonService>().AdvanceCycleAsync(Book, new DateTime(2026, 1, 2));
        Assert.Equal("Season 2, playbook 101, from next login.", result);
    }

    private DBLifeJobInfo SavedLife(long role) => DBLifeJobInfo.Parser.ParseFrom(Service<RoleStore>().LoadLifeJob(role)!);
    private LifeJobService.RuntimeState SavedRuntime(long role) =>
        JsonSerializer.Deserialize<LifeJobService.RuntimeState>(Service<RoleStore>().LoadLifeRuntime(role)!)!;
    private long Bag(long role) => Service<ItemStore>().Load(role).Where(i => i.Entry == Food).Sum(i => (long)i.Stack);
    private static long Power(GameSession s) => s.EntityProperties.Single(p => p.Id == Attack).Value;
    private static void AssertIdentity(LifeJobService.RuntimeState runtime, long season)
    { Assert.Equal(Book, runtime.FoodBook); Assert.Equal(season, runtime.FoodSeasonId); }
}
