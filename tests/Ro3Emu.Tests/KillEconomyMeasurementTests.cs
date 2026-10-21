using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>Fixed, paired E04 test-only probe. Reproduces the original two-stack grant, currency save,
/// then EXP save boundary; no sockets, random rolls, hooks or load traffic participate.</summary>
public sealed class KillEconomyMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public void FixedKillBeforeAfterCommitCountLatencyAndQueueAge()
    {
        string root = RepoRoot();
        string directory = Path.Combine(root, ".loadtest-temp", "e04-measure-" + Guid.NewGuid().ToString("N"));
        using var before = new Fixture(Path.Combine(directory, "before"));
        using var after = new Fixture(Path.Combine(directory, "after"));
        const int warmup = 4, samples = 24;
        for (int i = 0; i < warmup; i++) { before.Kill(false); after.Kill(true); }
        long startBefore = before.Commits, startAfter = after.Commits;
        var oldMs = new List<double>(); var newMs = new List<double>();
        for (int i = 0; i < samples; i++)
            if (i % 2 == 0) { oldMs.Add(before.Kill(false)); newMs.Add(after.Kill(true)); }
            else { newMs.Add(after.Kill(true)); oldMs.Add(before.Kill(false)); }
        Assert.Equal(samples * 4, before.Commits - startBefore);
        Assert.Equal(samples, after.Commits - startAfter);
        Assert.Equal(before.Outcome(), after.Outcome());
        object Summary(List<double> values) => new
        {
            Mean = values.Average(), Median = values.Order().ElementAt(samples / 2),
            P95 = values.Order().ElementAt((int)Math.Ceiling(samples * .95) - 1), Max = values.Max()
        };
        var report = new
        {
            Fixture = "two existing stacks + 7 currency + 3 base EXP + 4 job EXP, base level 50/job level 15 (career 1100)",
            Samples = samples, Warmup = warmup, BeforeCommitsPerKill = 4, AfterCommitsPerKill = 1,
            BeforeDeathToDurableMs = Summary(oldMs), AfterDeathToDurableMs = Summary(newMs),
            BeforeMaxQueueAgeMs = before.QueueAge, AfterMaxQueueAgeMs = after.QueueAge,
            QueueAgeBoundary = "writer lifetime high-water mark, including fixture setup and warmup",
            Boundary = "start of frozen kill settlement through acknowledgement of final durable commit; no presentation",
            Runtime = Environment.Version.ToString(), Note = "Shared host microfixture, no performance assertion or load qualification"
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        string artifacts = Path.Combine(root, ".loadtest-temp", "e04-measurements"); Directory.CreateDirectory(artifacts);
        string path = Path.Combine(artifacts, "fixed-kill-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        File.WriteAllText(path, json); output.WriteLine(path); output.WriteLine(json);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly AccountStore _accounts;
        private readonly RoleStore _roles;
        private readonly ItemStore _items;
        private readonly long _role;
        internal Fixture(string directory)
        {
            _accounts = new AccountStore(Options.Create(new ServerOptions { DataDir = directory }));
            _roles = new RoleStore(_accounts); _items = new ItemStore(_accounts);
            _role = _roles.Create("fixture", 1, "Fixture", 1, 1000, 1)!.Id;
            var p = _roles.LoadProgress(_role); p.Level = 50; p.Career = 1100; p.JobLevel = 15;
            p.Currency[1] = 1000; _roles.SaveProgress(_role, p);
            _items.Insert(_role, new InvItem { Entry = 1000000, Stack = 20, Pack = 1, Sort = 1 });
            _items.Insert(_role, new InvItem { Entry = 1000001, Stack = 20, Pack = 1, Sort = 2 });
        }
        private JsonElement Metrics => JsonSerializer.SerializeToElement(_accounts.AccessMetrics.Snapshot());
        internal long Commits => Metrics.GetProperty("writerCompleted").GetInt64();
        internal double QueueAge => Metrics.GetProperty("maxWriterQueueAgeMs").GetDouble();
        internal string Outcome()
        {
            var p = _roles.LoadProgress(_role);
            return JsonSerializer.Serialize(new { p.Exp, p.JobExp, Currency = p.Currency[1], Stacks = _items.Load(_role).Select(x => x.Stack).ToArray() });
        }
        internal double Kill(bool atomic)
        {
            long start = Stopwatch.GetTimestamp();
            var p = _roles.LoadProgress(_role); var initial = _items.Load(_role); var next = initial.Select(i => i.Clone()).ToArray();
            next[0].Stack += 3; next[1].Stack += 2;
            long baseBefore = p.Exp, jobBefore = p.JobExp;
            p.Currency[1] += 7;
            if (atomic)
            {
                p.Exp += 3; p.JobExp += 4;
                var tx = _roles.NewTransaction(); tx.ItemDelta(_role, initial, next); tx.Progress(_role, p);
                string source = "kill/fixture/" + Guid.NewGuid().ToString("N");
                EconomyQuantity[] goods = [new(new(EconomyAssetKind.Item, 1000000), 3),
                    new(new(EconomyAssetKind.Item, 1000001), 2), new(new(EconomyAssetKind.Currency, 1), 7)];
                var evidence = new EconomyRewardEvidence(50, baseBefore, 50, p.Exp, 15, jobBefore, 15, p.JobExp,
                    3, 4, OriginalGoods: goods);
                tx.WithEconomy(new EconomyMutation(Guid.NewGuid().ToString("N"), "fixture", _role, "monster-kill", source,
                    source, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [], goods, [], evidence));
                _roles.Commit(tx, "test/kill-atomic");
            }
            else
            {
                // Exact pre-E04 storage sequence for these two existing item stacks and currency.
                _items.Update(next[0]); _items.Update(next[1]); _roles.SaveProgress(_role, p);
                p.Exp += 3; p.JobExp += 4; _roles.SaveProgress(_role, p);
            }
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        public void Dispose() => _accounts.Dispose();
    }
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException();
    }
}
