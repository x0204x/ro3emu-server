using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>自造屬性走真GameLoop與31008；不代表原服戰鬥回復政策或取整方式。</summary>
public sealed partial class NaturalRegenerationTests : IAsyncLifetime
{
    private const long Battle = 1L << 15;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(12);
    private SyntheticHost _host = null!;
    private object? _regenWork;
    private bool _natural, _zero;
    private T Service<T>() where T : notnull => _host.Service<T>();
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    public async Task InitializeAsync()
    {
        _host = await SyntheticHost.StartAsync();
        var character = Service<CharacterService>(); var loop = Service<GameLoop>();
        character.MatchAttributes = _ => new (long, double)[] { (101, 200), (102, 100), (107, 5), (108, 2), (1081, 500) };
        character.DynamicProperties.Add(_ => _zero || !_natural && !ReferenceEquals(loop.CurrentWork, _regenWork)
            ? new (int, long)[] { (107, 0), (108, 0), (1071, 0), (1081, 0) } : []);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task RealTickerPublishesSpRecoveryDuringBattleWhileHpStaysUnchanged()
    {
        await using var bot = await _host.InWorldAsync("NaturalBattle");
        var s = Session(bot);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            s.CurrentHp = 50; s.CurrentSp = 20; s.StatusBits |= Battle;
            s.LastCombatMs = MonsterService.NowMs; _natural = true;
            return Task.CompletedTask;
        });
        var update = OnePropertyChange.Parser.ParseFrom((await bot.Inbox.WaitAsync(packet =>
            packet.IsGame(CharacterService.OnePropertyChange) && OnePropertyChange.Parser.ParseFrom(packet.Body)
                .Property.Propertys.Any(p => p.PropertyId == AttributeId.Sp && p.Data == 27), Wait)).Body);
        Assert.Equal(50, update.Property.Propertys.Single(p => p.PropertyId == AttributeId.Hp).Data);
        Assert.Equal(27, update.Property.Propertys.Single(p => p.PropertyId == AttributeId.Sp).Data);
        Assert.Equal(Battle, s.StatusBits & Battle);
        Assert.Equal(50, s.CurrentHp); Assert.Equal(27, s.CurrentSp);
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("stale")]
    [InlineData("zero")]
    [InlineData("full")]
    [InlineData("unset")]
    public async Task DeadStaleZeroAndFullStatesDoNotCreateRecoveryOrPackets(string state)
    {
        await using var bot = await _host.InWorldAsync("NaturalNoop"); var s = Session(bot);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            s.CurrentHp = state == "dead" ? 0 : state == "full" ? 200 : state == "unset" ? null : 50;
            s.CurrentSp = state == "full" ? 100 : state == "unset" ? null : 20;
            s.Dead = state == "dead"; _zero = state == "zero";
            return Task.CompletedTask;
        });
        await bot.HeartbeatAsync();
        long? hp = s.CurrentHp, sp = s.CurrentSp; int before = PropertyPackets(bot);
        await Service<GameLoop>().InvokeAsync(() =>
        { _regenWork = Service<GameLoop>().CurrentWork; return Service<CombatService>().RegenAsync(s, () => state != "stale"); });
        await bot.HeartbeatAsync();
        Assert.Equal(hp, s.CurrentHp); Assert.Equal(sp, s.CurrentSp); Assert.Equal(before, PropertyPackets(bot));
    }

    [Fact]
    public async Task BattleRecoveryCapsAtMaximumWithoutHealingHp()
    {
        await using var bot = await _host.InWorldAsync("NaturalCap"); var s = Session(bot);
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            s.CurrentHp = 50; s.CurrentSp = 99; s.StatusBits |= Battle; s.LastCombatMs = MonsterService.NowMs;
            _regenWork = Service<GameLoop>().CurrentWork;
            await Service<CombatService>().RegenAsync(s, () => true);
        });
        Assert.Equal(50, s.CurrentHp); Assert.Equal(100, s.CurrentSp);
        var update = OnePropertyChange.Parser.ParseFrom((await bot.Inbox.WaitAsync(packet =>
            packet.IsGame(CharacterService.OnePropertyChange) && OnePropertyChange.Parser.ParseFrom(packet.Body)
                .Property.Propertys.Any(p => p.PropertyId == AttributeId.Sp && p.Data == 100)
                && OnePropertyChange.Parser.ParseFrom(packet.Body).Property.Propertys.Any(p => p.PropertyId == AttributeId.Hp && p.Data == 50), Wait)).Body);
        Assert.Equal(50, update.Property.Propertys.Single(p => p.PropertyId == AttributeId.Hp).Data);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingRecoveryReadsManaExpenseAndRechecksStaleCallback(bool stale)
    {
        await using var bot = await _host.InWorldAsync("NaturalPending"); var s = Session(bot);
        var loop = Service<GameLoop>(); var owners = Service<OwnerRegistry>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool current = true;
        await loop.InvokeAsync(() =>
        {
            s.CurrentHp = 50; s.CurrentSp = 70; s.StatusBits |= Battle; s.LastCombatMs = MonsterService.NowMs;
            owners.DeferredForTest = origin =>
            { if (origin == "Character.StateLock" && _regenWork != null && ReferenceEquals(loop.CurrentWork, _regenWork)) deferred.TrySetResult(); };
            return Task.CompletedTask;
        });
        // 先只保留角色預約；若先拿StateLock，等待者會卡在semaphore而非owner，無法觀察deferred。
        var held = loop.InvokeAsync(() => owners.RunAsync("test/mana-expense", [ReservationKey.Role(s.Role!.Id)], async () =>
        {
            entered.TrySetResult(); await release.Task;
            using var gate = await CharacterService.Lock(s);
            await Service<CharacterService>().SetHpSpAsync(s, null, 50); // 真實耗魔更新先完成，恢復不得用等待前的70覆蓋它。
        }));
        Task? pending = null;
        try
        {
            await entered.Task.WaitAsync(Wait);
            pending = loop.InvokeAsync(() =>
            { _regenWork = loop.CurrentWork; return Service<CombatService>().RegenAsync(s, () => current); });
            await deferred.Task.WaitAsync(Wait);
            Assert.False(pending.IsCompleted); Assert.Equal(70, s.CurrentSp);
            current = !stale;
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await held.WaitAsync(Wait);
                if (pending != null) await pending.WaitAsync(Wait);
            }
            finally { await loop.InvokeAsync(() => { owners.DeferredForTest = null; return Task.CompletedTask; }); }
        }
        Assert.NotNull(pending);
        Assert.Equal(stale ? 50 : 57, s.CurrentSp); Assert.Equal(50, s.CurrentHp);
    }

    private static int PropertyPackets(BotClient bot) => bot.Inbox.Snapshot().Count(p => p.IsGame(CharacterService.OnePropertyChange));
}
