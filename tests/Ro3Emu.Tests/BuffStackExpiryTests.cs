using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>合成Buff資料：同一game-loop callback內使舊層過期後立即重加，不靠ticker清理。</summary>
public sealed class BuffStackExpiryTests
{
    private const int Buff = 99081;

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(7, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(7, true)]
    public async Task ExpiredStackRestartsAtOneBeforeTickerCleanupAndLiveStacksKeepTheirCap(int stackType, bool worldUnit)
    {
        await using var f = new Fixture(stackType);
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("StackExpiry");
        var source = f.Host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        var buffs = f.Host.Service<BuffService>();
        var unit = new StackTarget
        {
            Uid = 0x600000000111, SceneEntry = source.SceneEntry, InstanceId = source.InstanceId,
            X = source.Position.X, Y = source.Position.Y, Z = source.Position.Z
        };
        await f.Host.Service<GameLoop>().InvokeAsync(async () =>
        {
            if (worldUnit) await f.Host.Service<World>().AddUnitAsync(unit);
            Task Add() => worldUnit ? buffs.AddSupportAsync(unit, Buff, 60000, unit.Uid)
                : buffs.AddAsync(source, Buff, 60000, source.Role!.Id);
            List<ActiveBuff> Active() => worldUnit ? Assert.IsType<List<ActiveBuff>>(buffs.UnitBuffs(unit)) : source.Buffs;
            ActiveBuff Row() => Assert.Single(Active(), b => b.ConfigId == Buff);
            for (int expected = 1; expected <= 3; expected++)
            {
                await Add(); Assert.Equal(expected, Row().PileCount);
                Assert.Equal(expected * 100, Assert.Single(Row().Changes).Value);
            }
            await Add(); Assert.Equal(3, Row().PileCount);
            var old = Row();
            int index = Active().IndexOf(old);
            Active()[index] = old with { ExpireMs = MonsterService.NowMs - 1 };
            // 此處沒有await：確認舊3層仍在容器內後直接呼叫真正Add路徑。
            Assert.Equal(3, Row().PileCount); Assert.True(Row().ExpireMs <= MonsterService.NowMs);
            await Add();
            Assert.Equal(1, Row().PileCount); Assert.Equal(100, Assert.Single(Row().Changes).Value);
            Assert.True(Row().ExpireMs > MonsterService.NowMs);
            Assert.Equal(old.Guid, Row().Guid); // 既有refresh的wire身分與容器替換語義維持，不留下第二個同群Buff。
            await Add(); Assert.Equal(2, Row().PileCount);
            await Add(); await Add(); Assert.Equal(3, Row().PileCount);
            Assert.Equal(300, Assert.Single(Row().Changes).Value);
        });
    }

    private sealed class StackTarget : WorldUnit
    {
        public override EntityBase Entity() => new() { Uuid = (ulong)Uid, EntityType = 1, State = EntityState.Alive };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredPeriodicReapplyStartsNewCadenceAndKeepsTheFinalTick(bool worldUnit)
    {
        await using var f = new Fixture(7, periodic: true); await f.Start();
        await using var bot = await f.Host.InWorldAsync("PeriodicReset");
        var source = f.Host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        var buffs = f.Host.Service<BuffService>(); var loop = f.Host.Service<GameLoop>();
        var unit = new StackTarget { Uid = 0x600000000112, SceneEntry = source.SceneEntry, InstanceId = source.InstanceId };
        BuffService.PeriodicEffect? observed = null;
        int ticks = 0;
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Add(int duration) => worldUnit ? buffs.AddSupportAsync(unit, Buff, duration, unit.Uid)
            : buffs.AddAsync(source, Buff, duration, source.Role!.Id);
        List<ActiveBuff> Active() => worldUnit ? Assert.IsType<List<ActiveBuff>>(buffs.UnitBuffs(unit)) : source.Buffs;
        ActiveBuff Row() => Assert.Single(Active(), b => b.ConfigId == Buff);
        await loop.InvokeAsync(async () =>
        {
            if (worldUnit) await f.Host.Service<World>().AddUnitAsync(unit);
            buffs.PeriodicTick = (effect, _) =>
            { observed = effect; ticks++; signal.TrySetResult(); return Task.CompletedTask; };
            await Add(60000);
            buffs.TickPeriodic(Row().ExpireMs - Row().DurationMs + 1000);
        });
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var original = Assert.IsType<BuffService.PeriodicEffect>(observed);
        long restart = 0;
        await loop.InvokeAsync(async () =>
        {
            long unchanged = original.NextMs;
            await Add(60000);
            Assert.True(buffs.PeriodicCurrent(original)); Assert.Equal(unchanged, original.NextMs);
            var old = Row(); Active()[Active().IndexOf(old)] = old with { ExpireMs = MonsterService.NowMs - 1 };
            original.NextMs = MonsterService.NowMs - 1; // 合成已逾期排程，不能沿用到新Buff。
            await Add(3000); restart = Row().ExpireMs - Row().DurationMs;
            Assert.False(buffs.PeriodicCurrent(original)); Assert.Equal(1, buffs.PeriodicCount);
            ticks = 0; signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            buffs.TickPeriodic(restart + 999); Assert.Equal(0, ticks);
        });
        for (int tick = 1; tick <= 3; tick++)
        {
            signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await loop.InvokeAsync(() => { buffs.TickPeriodic(restart + tick * 1000); return Task.CompletedTask; });
            await signal.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(tick, ticks);
        }
        await loop.InvokeAsync(() =>
        {
            buffs.TickPeriodic(restart + 4000);
            Assert.Equal(3, ticks); Assert.Equal(0, buffs.PeriodicCount);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredPeriodicBusyOldCallbackCannotCommitAgainstReappliedSameGuid(bool worldUnit)
    {
        await using var f = new Fixture(7, periodic: true); await f.Start();
        await using var bot = await f.Host.InWorldAsync("PeriodicBusy");
        var source = f.Host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        var buffs = f.Host.Service<BuffService>(); var loop = f.Host.Service<GameLoop>();
        var unit = new StackTarget { Uid = 0x600000000113, SceneEntry = source.SceneEntry, InstanceId = source.InstanceId };
        var entered = new TaskCompletionSource<BuffService.PeriodicEffect>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var realTick = buffs.PeriodicTick ?? throw new InvalidOperationException("Expected real Combat periodic consumer.");
        bool couldCommit = true;
        Task Add() => worldUnit ? buffs.AddSupportAsync(unit, Buff, 60000, unit.Uid)
            : buffs.AddAsync(source, Buff, 60000, source.Role!.Id);
        List<ActiveBuff> Active() => worldUnit ? Assert.IsType<List<ActiveBuff>>(buffs.UnitBuffs(unit)) : source.Buffs;
        await loop.InvokeAsync(async () =>
        {
            if (worldUnit) await f.Host.Service<World>().AddUnitAsync(unit);
            var character = f.Host.Service<CharacterService>();
            // 最小合成角色沒有MaxHP成長表；先建立有效生命值，否則SetHpSp(1)會被上限0截成0。
            character.MatchAttributes = _ => new (long, double)[] { (101, 1000), (102, 100) };
            character.DynamicProperties.Add(_ => new (int, long)[] { (107, 0), (108, 0), (1071, 0), (1081, 0) });
            Assert.Equal(1000, character.Stats(source)[101]);
            await character.SetHpSpAsync(source, 1, null);
            Assert.Equal(1, source.CurrentHp);
            buffs.PeriodicTick = async (effect, buff) =>
            {
                entered.TrySetResult(effect); await resume.Task;
                couldCommit = buffs.PeriodicCurrent(effect);
                await realTick(effect, buff); // 玩家路徑是實際HP consumer，不能讓舊工作恢復HP。
                done.TrySetResult();
            };
            await Add(); var row = Assert.Single(Active(), b => b.ConfigId == Buff);
            buffs.TickPeriodic(row.ExpireMs - row.DurationMs + 1000);
        });
        var oldEffect = await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await loop.InvokeAsync(async () =>
        {
            Assert.True(oldEffect.Busy);
            var old = Assert.Single(Active(), b => b.ConfigId == Buff);
            Active()[Active().IndexOf(old)] = old with { ExpireMs = MonsterService.NowMs - 1 };
            buffs.Tick(default);
            Assert.Single(Active(), b => b.ConfigId == Buff); // 正在處理末跳時，自然到期不能先移除來源。
            await Add(); Assert.False(buffs.PeriodicCurrent(oldEffect));
            Assert.Equal(old.Guid, Assert.Single(Active(), b => b.ConfigId == Buff).Guid);
            resume.TrySetResult();
        });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(couldCommit); Assert.Equal(1, source.CurrentHp);
    }

    private sealed class Fixture(int stackType, bool periodic = false) : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-stack-expiry-" + Guid.NewGuid().ToString("N"));
        public SyntheticHost Host { get; private set; } = null!;

        public async Task Start()
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest);
            }
            var rows = new Dictionary<string, object>
            {
                [Buff.ToString(CultureInfo.InvariantCulture)] = new
                {
                    _iID = Buff, _iBuffID = 99081, _iType = 1, _iTime = 60000, _iStackType = stackType, _iMaxStack = 3,
                    _kChangeAttri = new[] { new[] { 104, 100 } },
                    _kDamageFormulaExtra = new[] { periodic ? "T_AXaddBadd3_etc" : "" },
                    _kDamageParamExtra = new[] { new[] { 0, 0, 0, 100 } },
                },
            };
            File.WriteAllText(Path.Combine(_root, "config", "BuffConfig.json"), JsonSerializer.Serialize(rows));
            Host = await SyntheticHost.StartAsync(o => o.GameDataDir = _root);
        }

        public async ValueTask DisposeAsync()
        {
            if (Host != null) await Host.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
