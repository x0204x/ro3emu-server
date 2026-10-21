using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Focused, opt-in real-session idle attribution. Includes posted work, not just Tick's enqueue cost.</summary>
public sealed class IdleTickerBenchmarkTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 9, 10, 13, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerMillisecond * 50);
    }

    [Fact, Trait("Category", "Slow")]
    public async Task MeasureIdleTickers()
    {
        string? output = Environment.GetEnvironmentVariable("RO3_IDLE_BENCH_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Ro3Emu.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        int count = int.TryParse(Environment.GetEnvironmentVariable("RO3_IDLE_BENCH_N"), out int n) ? n : 50;
        int samples = 5;
        string directory = Path.Combine(root.FullName, "artifacts", "idle-benchmark-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var clock = new Clock();
        await using var app = ServerHost.Create([], o =>
        {
            o.DataDir = directory; o.GameDataDir = Path.Combine(root.FullName, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.PlayBook = 101; o.SeasonStart = "2026-10-05";
        }, clock);
        var bots = new List<BotClient>();
        await app.StartAsync();
        try
        {
            var loop = app.Services.GetRequiredService<GameLoop>();
            var registered = (List<IGameTicker>)typeof(GameLoop).GetField("_tickers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loop)!;
            IGameTicker[] tickers = [];
            await loop.InvokeAsync(() =>
            {
                tickers = registered.ToArray();
                foreach (var ticker in tickers) loop.Remove(ticker);
                return Task.CompletedTask;
            });
            string key = File.ReadAllText(Path.Combine(directory, "gate_rsa_public.pem"));
            for (int i = 0; i < count; i++)
            {
                var bot = new BotClient($"http://127.0.0.1:{httpPort}", key);
                bots.Add(bot);
                await bot.LoginAsync($"idle{i}", "pw"); await bot.EnterGateAsync();
                var role = await bot.CreateRoleAsync($"Idle{i}");
                Assert.Equal(0, role.Result);
                // The spawn map admits 150 roles. Keep that real admission limit intact;
                // the 300-session profile places the other 150 in Prontera (limit 300).
                if (i >= 150)
                    app.Services.GetRequiredService<RoleStore>().SavePosition(role.RoleBaseInfo.Id, 10100, 0, 0, 0, 0);
                await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
            }
            Assert.Equal(count, app.Services.GetRequiredService<SessionRegistry>().All().Count(s => s.Role != null && !s.SceneLoading));
            // Baseline owners expressly excluded by the task. All other registered tickers are measured.
            var excluded = new HashSet<string> { "CombatService", "FestivalService", "WildAfkService", "WorldCoreService" };
            var rows = new List<object>();
            TimeSpan now = TimeSpan.FromHours(1);
            string? only = Environment.GetEnvironmentVariable("RO3_IDLE_BENCH_SERVICE");
            foreach (var ticker in tickers.Where(t => !excluded.Contains(t.GetType().Name)
                && (string.IsNullOrEmpty(only) || t.GetType().Name == only)))
            {
                var elapsed = new List<double>();
                for (int sample = -1; sample < samples; sample++)
                {
                    double milliseconds = 0;
                    for (int step = 0; step < 20; step++)
                    {
                        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        await loop.InvokeAsync(() =>
                        {
                            clock.Advance(); now += TimeSpan.FromMilliseconds(50);
                            long start = Stopwatch.GetTimestamp();
                            ticker.Tick(new GameTime(now, TimeSpan.FromMilliseconds(50)));
                            // Two queue barriers include normal event-driven callbacks posted by reconciliation.
                            loop.Post(() =>
                            {
                                loop.Post(() =>
                                {
                                    milliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                                    done.SetResult(); return Task.CompletedTask;
                                });
                                return Task.CompletedTask;
                            });
                            return Task.CompletedTask;
                        });
                        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
                        // Several legacy tickers still use the static wall clock. Pace every sample so
                        // those services receive real seconds too; delay is outside measured loop work.
                        await Task.Delay(50);
                    }
                    if (sample >= 0) elapsed.Add(milliseconds);
                }
                // A five-second idle window cannot amortize the caravan's minute sweep.
                // Force five complete GameTime periods separately, keeping the original idle sample intact.
                var minuteSweeps = new List<double>();
                if (ticker is GuildCaravanService)
                    for (int sweep = 0; sweep < samples; sweep++)
                    {
                        var done = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await loop.InvokeAsync(() =>
                        {
                            now += TimeSpan.FromMinutes(1);
                            long start = Stopwatch.GetTimestamp();
                            ticker.Tick(new GameTime(now, TimeSpan.FromMinutes(1)));
                            loop.Post(() =>
                            {
                                loop.Post(() => { done.SetResult(Stopwatch.GetElapsedTime(start).TotalMilliseconds); return Task.CompletedTask; });
                                return Task.CompletedTask;
                            });
                            return Task.CompletedTask;
                        });
                        minuteSweeps.Add(await done.Task.WaitAsync(TimeSpan.FromSeconds(30)));
                    }
                rows.Add(new
                {
                    service = ticker.GetType().Name, sessions = count, samples = elapsed,
                    meanMsPerSessionPerSecond = elapsed.Average() / count,
                    medianMsPerSessionPerSecond = elapsed.Order().ElementAt(samples / 2) / count,
                    minuteSweepSamplesMs = minuteSweeps,
                    minuteSweepAmortizedMsPerSessionPerSecond = minuteSweeps.Count == 0 ? (double?)null : minuteSweeps.Average() / 60 / count
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            var scenes = app.Services.GetRequiredService<SessionRegistry>().All()
                .Where(s => s.Role != null && !s.SceneLoading).GroupBy(s => s.SceneEntry)
                .ToDictionary(g => g.Key, g => g.Count());
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { sessions = count, scenes, samples, rows }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (var bot in bots) await bot.DisposeAsync();
            await app.StopAsync();
        }
    }
}
