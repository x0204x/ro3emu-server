using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Diagnostics;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.LoadTest;

public sealed partial class ScenarioRunner(RunnerOptions options)
{
    private static readonly Lazy<ServerMetrics> SharedBotMetrics = new(() => new ServerMetrics(observeRuntime: false));
    private readonly ActionCounters _counts = new();
    private readonly ActionCounters _setupCounts = new();
    private readonly MovementCorrelation _movement = new();
    private readonly FixedLatency _scheduleLag = new();
    private readonly ConcurrentBag<BotActor> _actors = new();
    private readonly ConcurrentDictionary<int, string> _failures = new();
    private readonly ConcurrentQueue<double> _queueSamples = new();
    private string _pem = "";
    private volatile bool _measureOffers;
    private HttpClient _http = null!;
    private string? persistenceEvidence;
    private string _prefix = "";
    private WorldData _world = null!;
    private LineMeasurements? _lines;
    private long _movementWithoutViewer, _chatWithoutPeer;

    public async Task<RunReport> RunAsync(CancellationToken ct)
    {
        var report = new RunReport
        {
            Scenario = options.Scenario, TargetBots = options.TargetBots,
            RequestedBots = options.Bots, RequestedSeconds = options.DurationSeconds, StartedUtc = DateTimeOffset.UtcNow,
            WalkPhase = options.WalkPhase
        };
        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPrefix)!);
        LocalServer? server = null;
        Dictionary<int, (string Name, double Cpu)>? competitorsBefore = null;
        SystemTimes? systemCpuBefore = null;
        try
        {
            if (options.ServerLocal) server = await LocalServer.StartAsync(options.RepositoryRoot,
                Path.GetDirectoryName(options.OutputPrefix)!, ct);
            var url = server?.Url ?? options.ServerUrl!;
            report.ServerUrl = url;
            report.ServerDataDir = server?.DataDir;
            report.ServerVersion = server == null ? "UNKNOWN (remote process binary unavailable)" :
                await LocalBuildBindingAsync(ct);
            _http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
            _pem = options.PemPath != null ? await File.ReadAllTextAsync(options.PemPath, ct) :
                await _http.GetStringAsync("/ro3emu/metrics/public-key", ct);
            _prefix = "lt" + Guid.NewGuid().ToString("N")[..9];
            var gameOptions = Options.Create(new ServerOptions { GameDataDir = Path.Combine(options.RepositoryRoot, "data") });
            var data = new GameData(gameOptions, NullLogger<GameData>.Instance);
            _world = new WorldData(gameOptions, data, NullLogger<WorldData>.Instance);
            _lines = new LineMeasurements();
            report.ServerBefore = await SnapshotAsync(ct);
            competitorsBefore = ProcessCpuSnapshot();
            systemCpuBefore = SystemTimes.Read();
            if (options.Scenario == "S1") await SeedReturningRolesAsync(ct);
            else await PrepareActorsAsync(report, ct);
            if (options.Scenario == "S3") await PrepareS3Async(report, ct);
            if (options.Scenario == "S5") await PrepareSiegeAsync(report, ct);
            if (server != null) await ResetLocalMetricsAsync(ct);
            report.ServerBefore = await SnapshotAsync(ct);
            foreach (var actor in _actors) actor.BeginMeasurement();
            _measureOffers = true;
            report.MeasurementPhase = "scenario";
            competitorsBefore = ProcessCpuSnapshot();
            systemCpuBefore = SystemTimes.Read();
            switch (options.Scenario)
            {
                case "S1": await RunS1Async(report, ct); break;
                case "S2": await RunS2Async(report, ct); break;
                case "S3": await RunS3Async(report, ct); break;
                case "S4": await RunS4Async(report, ct); break;
                case "S5": await RunSiegeAsync(_actors.ToArray(), options.DurationSeconds, ct); break;
            }
            if (options.Scenario == "S3") await CloseS3Async(report, ct);
            report.ServerAfter = await SnapshotAsync(ct);
            // Persistence evidence is read now: the client and the local server are disposed before the files are
            // written.
            if (options.ServerLocal)
                try { persistenceEvidence = await _http.GetStringAsync("/ro3emu/metrics/persistence", ct); }
                catch (HttpRequestException) { }
            report.SystemCpuPercent = SystemTimes.Percent(systemCpuBefore, SystemTimes.Read());
            report.HeavyProcesses = HeavyProcesses(competitorsBefore, ProcessCpuSnapshot(), server?.ProcessId);
        }
        catch (Exception e)
        {
            report.AbortReason = $"{e.GetType().Name}: {e.Message}";
            if (_http != null)
                try { report.ServerAfter = await SnapshotAsync(CancellationToken.None); } catch { }
        }
        finally
        {
            _measureOffers = false;
            if (competitorsBefore != null)
            {
                report.SystemCpuPercent = SystemTimes.Percent(systemCpuBefore, SystemTimes.Read());
                report.HeavyProcesses = HeavyProcesses(competitorsBefore, ProcessCpuSnapshot(), server?.ProcessId);
            }
            // Freeze before client teardown changes the incoming byte count or disconnect state.
            foreach (var actor in _actors) actor.Freeze();
            report.Inbound = _actors.GroupBy(x => x.Account).ToDictionary(x => x.Key,
                x => x.OrderByDescending(a => a.CreatedAt).First().FrozenReducer!);
            var wireBytes = _actors.GroupBy(x => x.Account).ToDictionary(x => x.Key,
                x => x.Sum(a => a.FrozenWireBytes));
            foreach (var actor in _actors) try { await actor.DisposeAsync(); } catch { }
            _http?.Dispose();
            if (server != null)
            {
                await server.DisposeAsync();
                if (server.CaptureFailure != null && report.AbortReason == null)
                    report.AbortReason = $"Local server log capture failed: {server.CaptureFailure}";
            }
            report.EndedUtc = DateTimeOffset.UtcNow;
            report.Actions = _counts.Actions();
            report.BotFailures = _failures.ToDictionary();
            report.Messages = (report.MeasurementPhase == "setup" ? _setupCounts : _counts).Messages().Where(x => x.Value.Offered > 0 || x.Value.Planned > 0)
                .ToDictionary(x => x.Key, x => x.Value);
            report.QueueLengthSamples = _queueSamples.ToArray();
            report.SchedulingLagP99Ms = _scheduleLag.Snapshot().P99;
            var motion = _movement.Snapshot();
            report.MovementPeerP99Ms = motion.Latencies.P99;
            report.MovementPeerLatency = motion.Latencies;
            report.MovementUnmatched = motion.Unmatched;
            report.MovementNeverSeen = motion.NeverSeen;
            report.MovementOffered = motion.Offers;
            report.MovementObservedOffers = motion.ObservedOffers;
            report.MovementAmbiguous = motion.Ambiguous;
            if (motion.Offers > 0) report.Actions["movement-seen"] = new(motion.Offers, motion.Offers,
                motion.ObservedOffers, 0, motion.NeverSeen, 0, motion.Latencies);
            report.MovementWithoutViewer = Interlocked.Read(ref _movementWithoutViewer);
            report.ChatWithoutPeer = Interlocked.Read(ref _chatWithoutPeer);
            report.LineSamples = _lines?.Snapshot() ?? [];
            if (report.ServerBefore != null && report.ServerAfter != null)
            {
                var pair = new MetricsPair(report.ServerBefore, report.ServerAfter);
                if (pair.SameEpoch) report.ServerDelta = pair.StructuredDelta();
                if (pair.Seconds > 0)
                {
                    report.InboundBytesPerSecond = wireBytes.ToDictionary(x => x.Key, x => x.Value / pair.Seconds);
                    report.DecodedPayloadBytesPerSecond = report.Inbound.ToDictionary(x => x.Key, x => x.Value.Bytes / pair.Seconds);
                }
                report.Admissions = pair.ServerAdmissions();
                foreach (var (id, value) in report.Messages.ToArray())
                {
                    long admitted = report.Admissions.GetValueOrDefault(id.ToString());
                    long completed = (long)Math.Max(0, pair.Delta($"inbound.{id}.completed"));
                    report.Messages[id] = value with { Admitted = admitted, Completed = completed };
                }
                var actionIds = new Dictionary<string, uint>
                {
                    ["local-chat"] = ChatService.TunnelUp,
                    ["fighter-cast"] = CombatService.UseSkill,
                    ["map-hop"] = SceneService.NpcInteraction,
                    ["guild-menu"] = GuildService.Recommend,
                    ["auction-menu"] = AuctionService.Query,
                    ["party-menu"] = TeamService.TeamInviteList,
                    ["equipment-menu"] = 31470,
                    ["login"] = MsgId.LoginRoleSelect,
                    ["stage.role-select"] = MsgId.LoginRoleSelect,
                    ["stage.create-role"] = MsgId.LoginRoleCreate,
                    ["stage.scene-ready"] = MsgId.SceneLoaded,
                };
                actionIds["walk"] = MsgId.Walk;
                if (report.MeasurementPhase == "setup")
                {
                    actionIds["setup-login"] = MsgId.LoginRoleSelect;
                    actionIds["seed-login"] = MsgId.LoginRoleSelect;
                    actionIds["setup-map"] = SceneService.SceneTeleport;
                }
                foreach (var (name, id) in actionIds)
                    if (report.Actions.TryGetValue(name, out var action) && report.Messages.TryGetValue(id, out var message))
                        report.Actions[name] = action with
                        {
                            // These IDs are shared by distinct cohorts; aggregate dispatch counts cannot
                            // identify which action was admitted. Keep the per-message totals as evidence.
                            Admitted = options.Scenario == "S3" && name == "walk" ||
                                options.Scenario == "S4" && name == "stage.scene-ready" ? null : message.Admitted,
                        };
            }
            if (_failures.Count > 0 && report.AbortReason == null)
                report.AbortReason = $"{_failures.Count} bot failures (first: {_failures.OrderBy(x => x.Key).First().Value})";
            if (report.Actions.Values.Any(x => x.Planned > x.Offered) && report.AbortReason == null)
                report.AbortReason = "Planned/offered discrepancy";
            if (report.SchedulingLagP99Ms > 100 && report.AbortReason == null)
                report.AbortReason = $"Scheduling lag p99 {report.SchedulingLagP99Ms:F1} ms > 100 ms";
            report.Targets = TargetEvaluation.Evaluate(report);
            report.Verdict = report.AbortReason == null && report.Targets.All(x => x.Status == "PASS") ? "PASS" : "NOT QUALIFIED";
            // The report is written even when the run was cancelled: that is when its abort reason matters most.
            await File.WriteAllTextAsync(options.OutputPrefix + ".json", report.ToJson(), CancellationToken.None);
            await File.WriteAllTextAsync(options.OutputPrefix + ".md", report.ToMarkdown(), CancellationToken.None);
            // Persistence evidence beside the report: the loop's synchronous DB waits by label (C/D 16 work list),
            // lazy parts, snapshots and backups. Cumulative since server start (not reset with the epoch).
            if (options.ServerLocal)
            {
                if (persistenceEvidence != null)
                    await File.WriteAllTextAsync(options.OutputPrefix + ".persistence.json", persistenceEvidence, CancellationToken.None);
                // Ownership evidence (C/D 15): Realm admissions by origin with queued and held time - every other
                // reservation waits while one is held. Cumulative since server start.
                if (report.ServerAfter?["ownership"] is { } ownership)
                    await File.WriteAllTextAsync(options.OutputPrefix + ".ownership.json",
                        ownership.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            }
        }
        return report;
    }

    private async Task<JsonNode> SnapshotAsync(CancellationToken ct)
    {
        var text = await _http.GetStringAsync("/ro3emu/metrics", ct);
        var node = JsonNode.Parse(text) ?? throw new InvalidDataException("Empty metrics JSON");
        if (node["schemaVersion"]?.GetValue<int>() != 1) throw new InvalidDataException("Unsupported metrics schema");
        _lines?.Observe(node);
        return node;
    }

    private async Task ResetLocalMetricsAsync(CancellationToken ct)
    {
        using var response = await _http.PostAsync("/ro3emu/metrics/reset", null, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> LocalBuildBindingAsync(CancellationToken ct)
    {
        var dll = Path.Combine(options.RepositoryRoot, "src", "Ro3Emu.Server", "bin", "Release", "net10.0", "Ro3Emu.Server.dll");
        await using var stream = File.OpenRead(dll);
        string sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        await using var runner = File.OpenRead(typeof(ScenarioRunner).Assembly.Location);
        string runnerSha = Convert.ToHexString(await SHA256.HashDataAsync(runner, ct)).ToLowerInvariant();
        string head = await GitOutputAsync("rev-parse", "HEAD");
        string dirty = string.IsNullOrWhiteSpace(await GitOutputAsync("status", "--porcelain")) ? "clean" : "dirty";
        return $"Ro3Emu.Server.dll SHA256 {sha}; Ro3Emu.LoadTest.dll SHA256 {runnerSha}; git HEAD {head.Trim()}; {dirty}";
    }
    private async Task<string> GitOutputAsync(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = options.RepositoryRoot, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("git failed while binding report");
        return output;
    }

    private string AccountName(int index) => $"{_prefix}{index:D4}";
    private static string RoleName(string account) => "L" + account;

    private async Task SeedReturningRolesAsync(CancellationToken ct)
    {
        int returning = options.Bots / 2;
        using var gate = new SemaphoreSlim(12);
        var tasks = Enumerable.Range(0, returning).Select(async i =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await using var actor = await CreateActorAsync(i, expectExisting: false, countAction: "seed-login", ct);
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    private async Task PrepareActorsAsync(RunReport report, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(12);
        var tasks = Enumerable.Range(0, options.Bots).Select(async i =>
        {
            await gate.WaitAsync(ct);
            try
            {
                // Stop ramping after concrete admission failures. Unattempted accounts remain planned.
                if (_failures.Count >= Math.Max(3, options.Bots / 10)) return;
                var actor = await CreateActorAsync(i, expectExisting: false, countAction: "setup-login", ct);
                _actors.Add(actor);
                int s3Town = options.Bots * 200 / 500;
                int s3Menu = options.Bots * 50 / 500;
                if (options.Scenario is "S2" or "S4" or "S5" || options.Scenario == "S3" && (i < s3Town || i >= options.Bots - s3Menu))
                    await TravelAsync(actor, 10100, "setup-map", ct);
                if (options.Scenario is "S2" or "S4" || options.Scenario == "S3" && i < s3Town)
                    await actor.Bot.JoinChatAsync($"4_{actor.Scene}").WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            catch (Exception e) { _failures[i] = $"setup: {e.GetType().Name}: {e.Message}"; }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        if (_failures.Count > 0) throw new InvalidOperationException($"Setup failed for {_failures.Count} of {options.Bots} bots");
        if (_actors.Count != options.Bots) throw new InvalidOperationException("Setup bot count mismatch");
    }

    private async Task<BotActor> CreateActorAsync(int index, bool expectExisting, string countAction, CancellationToken ct)
    {
        string account = AccountName(index);
        var reducer = new WorldReducer();
        var bot = new BotClient(_http.BaseAddress!.ToString(), _pem) { Timeout = TimeSpan.FromSeconds(45) };
        bot.EnableLoadMode(512, reducer.Observe, SharedBotMetrics.Value);
        bot.OnGameMessageOffered = id => (_measureOffers ? _counts : _setupCounts).Message(id).Offer();
        var activeStages = new ConcurrentDictionary<string, ActionTally>();
        bot.OnStageStarted = name =>
        {
            if (!_measureOffers) return;
            var stage = _counts.Action("stage." + name); stage.Plan(); stage.Offer();
            activeStages[name] = stage;
        };
        bot.OnStageFinished = (name, ms, error) =>
        {
            if (!activeStages.TryRemove(name, out var stage)) return;
            if (error == null) stage.Complete(ms);
            else if (error is BotRefusedException) stage.Refuse();
            else if (error is TimeoutException or OperationCanceledException) stage.Timeout(); else stage.Fail();
        };
        bot.Inbox.OnExpectedReply = (reply, ms) =>
        {
            reducer.RecordReply(reply, ms);
            uint id = reply.MsgId;
            if (id == ChatService.TunnelDown)
            {
                // Only command 3 acknowledges this bot's request; command 4 is someone else's fan-out.
                if (reply.Body.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(reply.Body) != 3) return;
                id = ChatService.TunnelUp;
            }
            if (reply.Kind == BotEventKind.Game && (_measureOffers ? _counts : _setupCounts).TryMessage(id, out var message))
                message!.RecordLatency(ms);
        };
        var actor = new BotActor(account, bot, reducer);
        var tally = _counts.Action(countAction);
        tally.Plan(); tally.Offer();
        var category = _measureOffers ? _counts.Action(expectExisting ? "returning-role-login" : "new-account-login") : null;
        category?.Plan(); category?.Offer();
        var sw = Stopwatch.StartNew();
        try
        {
            await bot.LoginAsync(account, "bot").WaitAsync(TimeSpan.FromSeconds(60), ct);
            await bot.EnterGateAsync().WaitAsync(TimeSpan.FromSeconds(60), ct);
            if (expectExisting && bot.Roles.Count == 0) throw new InvalidOperationException("Returning role missing");
            long role = bot.Roles.FirstOrDefault()?.Id ?? (await bot.CreateRoleAsync(RoleName(account)).WaitAsync(TimeSpan.FromSeconds(60), ct)).RoleBaseInfo.Id;
            actor.RoleId = role;
            var self = await bot.EnterWorldAsync(role).WaitAsync(TimeSpan.FromSeconds(60), ct);
            actor.HomeX = actor.X = self.MovementInfo.Pos.X;
            actor.Y = self.MovementInfo.Pos.Y;
            actor.HomeZ = actor.Z = self.MovementInfo.Pos.Z;
            actor.Scene = reducer.Location.SceneEntry;
            actor.Sender = new BotSendQueue(bot.DisposeAsync);
            reducer.OnMovement = m => { if (m.Uid != actor.RoleId) _movement.Seen(m, actor.RoleId, reducer.Location); };
            tally.Complete(sw.Elapsed.TotalMilliseconds);
            category?.Complete(sw.Elapsed.TotalMilliseconds);
            actor.LoginMs = sw.Elapsed.TotalMilliseconds;
            return actor;
        }
        catch (Exception e)
        {
            if (e is BotRefusedException) { tally.Refuse(); category?.Refuse(); }
            else if (e is TimeoutException or OperationCanceledException) { tally.Timeout(); category?.Timeout(); }
            else { tally.Fail(); category?.Fail(); }
            await actor.DisposeAsync();
            throw;
        }
    }

    private async Task RunS1Async(RunReport report, CancellationToken ct)
    {
        using var ramp = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int rateSeconds = 60;
        var clock = Stopwatch.StartNew();
        var tally = _counts.Action("login");
        for (int i = 0; i < options.Bots; i++) tally.Plan();
        var tasks = Enumerable.Range(0, options.Bots).Select(async i =>
        {
            ActionAttempt? attempt = null;
            try
            {
                var due = TimeSpan.FromSeconds(i * rateSeconds / (double)options.Bots);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ramp.Token);
                ramp.Token.ThrowIfCancellationRequested();
                _scheduleLag.Add(Math.Max(0, (clock.Elapsed - due).TotalMilliseconds));
                tally.Offer();
                attempt = new ActionAttempt(tally);
                var actor = await CreateActorAsync(i, expectExisting: i < options.Bots / 2, countAction: "login-protocol", ramp.Token);
                _actors.Add(actor);
                attempt.Complete();
            }
            catch (OperationCanceledException) when (ramp.IsCancellationRequested) { }
            catch (Exception e)
            {
                attempt?.Fail(e);
                _failures[i] = $"login: {e.GetType().Name}: {e.Message}";
            }
            finally { attempt?.Dispose(); }
        }).ToArray();
        var monitor = MonitorS1Async(report, ramp, ct);
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromMinutes(3), ct); }
        finally { ramp.Cancel(); try { await monitor; } catch (OperationCanceledException) { } }
        if (_actors.Count != options.Bots && report.AbortReason == null)
            report.AbortReason = $"Only {_actors.Count}/{options.Bots} reached full scene-ready marker";
    }

    private async Task MonitorS1Async(RunReport report, CancellationTokenSource ramp, CancellationToken outer)
    {
        int stalled = 0;
        long initialSelect = report.ServerBefore?["inbound"]?[MsgId.LoginRoleSelect.ToString()]?["count"]?.GetValue<long>() ?? 0;
        while (!ramp.IsCancellationRequested)
        {
            await Task.Delay(5000, ramp.Token);
            var status = _counts.Action("login").Snapshot();
            if (status.Offered < 20) continue;
            long admitted = (await SnapshotAsync(outer))["inbound"]?[MsgId.LoginRoleSelect.ToString()]?["count"]?.GetValue<long>() ?? 0;
            var queue = (await SnapshotAsync(outer))["loop"]?["queueLengthCurrent"]?.GetValue<double>() ?? 0;
            _queueSamples.Enqueue(queue);
            bool severe = status.TimedOut >= 10 || status.Failed >= Math.Max(10, status.Offered / 5) ||
                status.Completed >= 20 && status.Latency.P99 > 30000 ||
                status.Offered >= 100 && admitted - initialSelect < status.Offered / 10;
            stalled = severe ? stalled + 1 : 0;
            if (stalled < 2) continue;
            report.AbortReason = $"S1 ramp aborted: offered={status.Offered}, completed={status.Completed}, timed out={status.TimedOut}, select admitted={admitted - initialSelect}, login p99≤{status.Latency.P99} ms";
            ramp.Cancel();
            return;
        }
    }

    private async Task RunS2Async(RunReport report, CancellationToken ct)
    {
        await RunTrafficAsync(_actors.ToArray(), options.DurationSeconds, chat: true, ct);
    }

    private async Task RunS4Async(RunReport report, CancellationToken ct)
    {
        var duration = Stopwatch.StartNew();
        var actors = _actors.OrderBy(x => x.Account).ToArray();
        int mapCount = Math.Min(100, options.Bots / 2);
        int relogCount = Math.Min(100, options.Bots - mapCount);
        var kafra = _world.Map(10100)?.Npcs.FirstOrDefault(x => x.Kind == WorldNpcKind.Kafra && x.NpcId == 100001)
            ?? throw new InvalidOperationException("Prontera Kafra 100001 missing; map spike NOT RUN");
        // Positioning is setup, outside the measured spike.
        _measureOffers = false;
        await Task.WhenAll(actors.Take(mapCount).Select(actor => WalkNearAsync(actor, kafra, ct)));
        if (options.ServerLocal) await ResetLocalMetricsAsync(ct);
        report.ServerBefore = await SnapshotAsync(ct);
        foreach (var actor in _actors) actor.BeginMeasurement();
        _measureOffers = true;
        duration.Restart(); // S4's requested duration excludes the positioning setup above.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mapTasks = actors.Take(mapCount).Select(async (actor, i) =>
        {
            await start.Task;
            try { await KafraHopAsync(actor, kafra, 10111, ct); }
            catch (Exception e) { _failures[i] = $"map-hop: {e.GetType().Name}: {e.Message}"; }
        });
        var relogTasks = actors.Skip(mapCount).Take(relogCount).Select(async (actor, i) =>
        {
            await start.Task;
            var tally = _counts.Action("relog"); tally.Plan(); tally.Offer();
            var sw = Stopwatch.StartNew();
            try
            {
                await actor.DisposeAsync();
                int accountIndex = int.Parse(actor.Account[^4..]);
                var replacement = await CreateActorAsync(accountIndex, expectExisting: true, countAction: "relog-protocol", ct);
                _actors.Add(replacement);
                tally.Complete(sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception e) { if (e is BotRefusedException) tally.Refuse(); else if (e is TimeoutException or OperationCanceledException) tally.Timeout(); else tally.Fail(); _failures[mapCount + i] = $"relog: {e.Message}"; }
        });
        start.SetResult();
        var background = RunTrafficAsync(actors.Skip(mapCount + relogCount).ToArray(), options.DurationSeconds, chat: true, ct);
        await Task.WhenAll(mapTasks.Concat(relogTasks).Append(background)).WaitAsync(
            TimeSpan.FromSeconds(Math.Max(90, options.DurationSeconds + 30)), ct);
        var remaining = TimeSpan.FromSeconds(options.DurationSeconds) - duration.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
    }

    private async Task RunTrafficAsync(BotActor[] actors, int seconds, bool chat, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        var clock = Stopwatch.StartNew();
        var schedules = actors.Select((_, index) => WalkSchedule.DueSeconds(seconds, index, options.WalkPhase)).ToArray();
        int chatSlots = chat ? 1 + (seconds - 1) / 30 : 0;
        for (int index = 0; index < actors.Length; index++)
        {
            for (int i = 0; i < schedules[index].Length; i++) { _counts.Action("walk").Plan(); _counts.Message(MsgId.Walk).Plan(); }
            for (int i = 0; i < chatSlots; i++) { _counts.Action("local-chat").Plan(); _counts.Message(ChatService.TunnelUp).Plan(); }
        }
        var tasks = actors.Select(async (actor, index) =>
        {
            var random = new Random(index + 1729);
            try
            {
                for (int slot = 0; slot < schedules[index].Length && !stop.IsCancellationRequested; slot++)
                {
                    var due = TimeSpan.FromSeconds(schedules[index][slot]);
                    var wait = due - clock.Elapsed;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, stop.Token);
                    _scheduleLag.Add(Math.Max(0, (clock.Elapsed - due).TotalMilliseconds));
                    bool isStop = slot % 5 == 4;
                    float x = isStop ? actor.X : actor.HomeX + (float)(random.NextDouble() * 4 - 2);
                    float z = isStop ? actor.Z : actor.HomeZ + (float)(random.NextDouble() * 4 - 2);
                    if (options.WalkPhase == "realistic")
                    {
                        float dx = x - actor.X, dz = z - actor.Z, distance = MathF.Sqrt(dx * dx + dz * dz);
                        // A short walk at the minimum 0.5 s interval; keep the legacy sync trajectory intact.
                        if (distance > 1.5f) { x = actor.X + dx / distance * 1.5f; z = actor.Z + dz / distance * 1.5f; }
                    }
                    _counts.Action("walk").Offer();
                    using var attempt = new ActionAttempt(_counts.Action("walk"));
                    var viewer = PeerPairing.Select(actor, actors, requireVisibility: true);
                    if (viewer != null) _movement.Offer(actor.RoleId, x, z, isStop, viewer.RoleId, actor.Location);
                    else Interlocked.Increment(ref _movementWithoutViewer);
                    try
                    {
                        await actor.Sender!.SendAsync(() => actor.Bot.WalkAsync(x, actor.Y, z, isStop), stop.Token);
                        attempt.Complete(); // transport completion; separate movement-seen tally proves reception.
                    }
                    catch (Exception e) { attempt.Fail(e); throw; }
                    actor.X = x; actor.Z = z;
                    if (slot % 7 == 0) await actor.Sender.SendAsync(() => actor.Bot.SendGameAsync(MsgId.Heartbeat, new Ping()), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { _failures[index] = $"traffic: {e.GetType().Name}: {e.Message}"; }
        }).ToArray();
        var chats = chat ? actors.Select(async (actor, index) =>
        {
            try
            {
                // Realistic: each bot keeps its own phase inside the 30 s window. All bots used to talk within 150 ms
                // of every slot (a 300-message burst every 30 s, the S2 queue p99 spike); sync keeps that worst case.
                double phase = options.WalkPhase == "sync" ? index * .0005 : new Random(index + 4099).NextDouble() * 30;
                for (int slot = 0; slot < chatSlots && !stop.IsCancellationRequested; slot++)
                {
                    var due = TimeSpan.FromSeconds(Math.Min(slot * 30 + phase, Math.Max(0, seconds - 1)));
                    var wait = due - clock.Elapsed;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, stop.Token);
                    _scheduleLag.Add(Math.Max(0, (clock.Elapsed - due).TotalMilliseconds));
                    var peer = PeerPairing.Select(actor, actors, requireVisibility: false);
                    await SendLocalChatAsync(actor, peer, slot, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { _failures[400000 + index] = $"chat: {e.GetType().Name}: {e.Message}"; }
        }).ToArray() : [];
        var monitor = MonitorPressureAsync(clock, seconds, stop, ct);
        await Task.WhenAll(tasks.Concat(chats));
        var remaining = TimeSpan.FromSeconds(seconds) - clock.Elapsed;
        if (remaining > TimeSpan.Zero) try { await Task.Delay(remaining, stop.Token); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        stop.Cancel();
        try { await monitor; } catch (OperationCanceledException) { }
        // Stop offering first, then allow already-sent fan-out to reach the observers before freezing evidence.
        var drain = Stopwatch.StartNew();
        while (_movement.Snapshot().NeverSeen > 0 && drain.Elapsed < TimeSpan.FromSeconds(2))
            await Task.Delay(50, ct);
    }

    private async Task SendLocalChatAsync(BotActor actor, BotActor? peer, int slot, CancellationToken ct)
    {
        var tally = _counts.Action("local-chat"); tally.Offer();
        using var attempt = new ActionAttempt(tally);
        string content = $"{actor.Account}:{slot}";
        static bool Command(BotEvent e, uint cmd) => e.IsGame(ChatService.TunnelDown) && e.Body.Length >= 8 &&
            BinaryPrimitives.ReadUInt32LittleEndian(e.Body) == cmd;
        try
        {
            using var reply = actor.Bot.Inbox.Expect(e => Command(e, 3));
            using var seen = peer?.Bot.Inbox.Expect(e => Command(e, 4) &&
                Tencent.IM.NotifyNewTextChatMessage.Parser.ParseFrom(e.Body.AsSpan(8)).Msg?.Content == content);
            await actor.Sender!.SendAsync(() => actor.Bot.SendChatAsync($"4_{actor.Scene}", 2, content, channelIndex: 4), ct);
            var response = Tencent.IM.SendTextChatMessageResponse.Parser.ParseFrom(
                (await reply.WaitAsync(TimeSpan.FromSeconds(5), ct)).Body.AsSpan(8));
            if (!response.ChatResult) { attempt.Refuse(); return; }
            if (seen == null) { Interlocked.Increment(ref _chatWithoutPeer); attempt.Refuse(); return; }
            await seen.WaitAsync(TimeSpan.FromSeconds(5), ct);
            attempt.Complete();
        }
        catch (Exception e) { attempt.Fail(e); throw; }
    }

    private async Task RunFighterTrafficAsync(BotActor[] actors, int seconds, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        var clock = Stopwatch.StartNew();
        int slots = seconds / 2;
        var tasks = actors.Select(async (actor, index) =>
        {
            try
            {
                for (int slot = 0; slot < slots && !stop.IsCancellationRequested; slot++)
                {
                    var due = TimeSpan.FromSeconds(slot * 2 + index * .001);
                    if (due > clock.Elapsed) await Task.Delay(due - clock.Elapsed, stop.Token);
                    if (slot % 3 == 0) await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(MsgId.Heartbeat, new Ping()), stop.Token);
                    var monsters = actor.Reducer.Monsters();
                    var target = monsters.OrderBy(x => Distance(actor, x)).FirstOrDefault();
                    if (target == null)
                    {
                        var search = _counts.Action("fighter-search"); search.Plan(); search.Offer(); search.Complete(0);
                        continue;
                    }
                    float distance = Distance(actor, target);
                    if (distance > 3.5f)
                    {
                        var pos = target.MovementInfo.Pos;
                        float step = MathF.Min(2.5f, distance - 2.5f);
                        float x = actor.X + (pos.X - actor.X) / distance * step;
                        float z = actor.Z + (pos.Z - actor.Z) / distance * step;
                        var walk = _counts.Action("fighter-walk"); walk.Plan(); walk.Offer();
                        using var walkAttempt = new ActionAttempt(walk);
                        try { await actor.Sender!.SendAsync(() => actor.Bot.WalkAsync(x, actor.Y, z, stop: true), stop.Token); walkAttempt.Complete(); }
                        catch (Exception e) { walkAttempt.Fail(e); throw; }
                        actor.X = x; actor.Z = z;
                        continue;
                    }
                    await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(CombatService.LockEnemy,
                        new LockEnemyReq { EnemyId = (long)target.Uuid }), stop.Token);
                    var tally = _counts.Action("fighter-cast"); tally.Plan(); tally.Offer();
                    using var attempt = new ActionAttempt(tally);
                    try
                    {
                        using var reply = actor.Bot.Inbox.Expect(e => e.IsGame(CombatService.UseSkill));
                        using var hit = actor.Bot.Inbox.Expect(e => e.IsGame(CombatService.SkillHit) &&
                            SkillHitInfo.Parser.ParseFrom(e.Body) is { SrcID: var source, SkillId: 1000001 } packetHit &&
                            source == actor.RoleId && packetHit.Dsts.Any(x => x.DstID == (long)target.Uuid));
                        using var loot = actor.Bot.Inbox.Expect(e => e.IsGame(CharacterService.ItemsSync));
                        // Another fighter may kill the target before this cast's hit moment; the server then (rightly)
                        // leaves it out of the hit. That is a refused swing, not a bot failure.
                        using var died = actor.Bot.Inbox.Expect(e => e.IsGame(CombatService.SkillHit) &&
                            SkillHitInfo.Parser.ParseFrom(e.Body).Dsts.Any(x => x.DstID == (long)target.Uuid && x.IsDead));
                        var sw = Stopwatch.StartNew();
                        await actor.Sender.SendAsync(() => actor.Bot.SendGameAsync(CombatService.UseSkill,
                            new CastSkillReq { SkillId = 1000001, TargetId = (long)target.Uuid }), stop.Token);
                        var response = CastSkillRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(4), stop.Token)).Body);
                        if (response.Result != 0) { attempt.Refuse(); continue; }
                        SkillHitInfo strike;
                        try { strike = SkillHitInfo.Parser.ParseFrom((await hit.WaitAsync(TimeSpan.FromSeconds(4), stop.Token)).Body); }
                        catch (TimeoutException) when (died.IsCompleted ||
                            actor.Reducer.Monsters().All(m => m.Uuid != target.Uuid))
                        { attempt.Refuse(); continue; }
                        if (strike.Dsts.Any(x => x.Damage > 0)) attempt.Complete();
                        else attempt.Refuse();
                        // A received item sync proves observation only, not item/currency conservation.
                        try
                        {
                            await loot.WaitAsync(TimeSpan.FromMilliseconds(200), stop.Token);
                            var observedLoot = _counts.Action("observed-loot-sync");
                            observedLoot.Plan(); observedLoot.Offer(); observedLoot.Complete(sw.Elapsed.TotalMilliseconds);
                        }
                        catch (TimeoutException) { }
                    }
                    catch (Exception error) { attempt.Fail(error); throw; }
                    await actor.Sender.SendAsync(() => actor.Bot.SendGameAsync(MsgId.Heartbeat, new Ping()), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { _failures[200000 + index] = $"fighter: {e.GetType().Name}: {e.Message}"; }
        });
        await Task.WhenAll(tasks);
        var remaining = TimeSpan.FromSeconds(seconds) - clock.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
    }

    private static float Distance(BotActor actor, EntityBase target)
    {
        var pos = target.MovementInfo.Pos;
        float dx = pos.X - actor.X, dz = pos.Z - actor.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private async Task RunMenuTrafficAsync(BotActor[] actors, int seconds, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        int slots = Math.Max(1, seconds / 10);
        var tasks = actors.Select(async (actor, index) =>
        {
            try
            {
                // These are actual read/menu routes. Paid refine needs real equipment and is NOT RUN.
                var requests = new (string Name, uint Id, Google.Protobuf.IMessage Request)[]
                {
                    ("guild-menu", GuildService.Recommend, new GuildRecommendListReq { Count = 10 }),
                    ("auction-menu", AuctionService.Query, new QueryAuctionReq { AuctionType = AuctionType.World, StartIndex = 0, EndIndex = 10 }),
                    ("party-menu", TeamService.TeamInviteList, new TeamInviteListReq { Guid = actor.RoleId }),
                    ("equipment-menu", 31470, new QueryEquipRankReq { Rankid = 0, Itemuid = 0 }),
                };
                foreach (var request in requests) for (int i = 0; i < slots; i++) _counts.Action(request.Name).Plan();
                for (int slot = 0; slot < slots && !stop.IsCancellationRequested; slot++)
                {
                    foreach (var (name, id, request) in requests)
                    {
                        var tally = _counts.Action(name); tally.Offer();
                        using var attempt = new ActionAttempt(tally);
                        try
                        {
                            using var expected = actor.Bot.Inbox.Expect(e => e.IsGame(id));
                            var sw = Stopwatch.StartNew();
                            await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(id, request), stop.Token);
                            var received = await expected.WaitAsync(TimeSpan.FromSeconds(5), stop.Token);
                            bool accepted = id switch
                            {
                                GuildService.Recommend => GuildRecommendListResp.Parser.ParseFrom(received.Body).Result == 0,
                                AuctionService.Query => QueryAuctionRes.Parser.ParseFrom(received.Body).AuctionType == AuctionType.World,
                                TeamService.TeamInviteList => TeamInviteListResp.Parser.ParseFrom(received.Body).Result == 0,
                                31470 => QueryEquipRankRes.Parser.ParseFrom(received.Body) is { Rankid: 0, Itemuid: 0 },
                                _ => false,
                            };
                            if (accepted) attempt.Complete(); else attempt.Refuse();
                        }
                        catch (Exception error) { attempt.Fail(error); throw; }
                    }
                    await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(MsgId.Heartbeat, new Ping()), stop.Token);
                    await Task.Delay(TimeSpan.FromSeconds(10), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { _failures[300000 + index] = $"menu: {e.GetType().Name}: {e.Message}"; }
        });
        await Task.WhenAll(tasks);
    }

    private async Task RunHeartbeatsAsync(BotActor[] actors, int seconds, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        var tasks = actors.Select(async actor =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(MsgId.Heartbeat, new Ping()), stop.Token);
                    await Task.Delay(5000, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        await Task.WhenAll(tasks);
    }

    private async Task MonitorPressureAsync(Stopwatch clock, int seconds, CancellationTokenSource stop, CancellationToken outer)
    {
        int severe = 0;
        double initialAdmission = (await SnapshotAsync(outer))["inbound"]?[MsgId.Walk.ToString()]?["count"]?.GetValue<double>() ?? 0;
        while (!stop.IsCancellationRequested && clock.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(5000, stop.Token);
            var metric = await SnapshotAsync(outer);
            double queue = metric["loop"]?["queueLengthCurrent"]?.GetValue<double>() ?? 0;
            _queueSamples.Enqueue(queue);
            double admission = (metric["inbound"]?[MsgId.Walk.ToString()]?["count"]?.GetValue<double>() ?? 0) - initialAdmission;
            double offered = _counts.Message(MsgId.Walk).Snapshot().Offered;
            var movement = _movement.Snapshot();
            bool latencyExplosion = movement.Latencies.Count > 100 && movement.Latencies.P99 > 5000;
            if (clock.Elapsed.TotalSeconds > 15 && offered > 100 && (queue > 10000 || admission < offered * .1 || latencyExplosion)) severe++;
            else severe = 0;
            if (severe >= 2) { stop.Cancel(); throw new InvalidOperationException($"Ramp aborted: queue={queue:F0}, walk admission={admission:F0}/{offered}, movement p99={movement.Latencies.P99:F0} ms"); }
        }
    }

    private async Task TravelAsync(BotActor actor, long target, string action, CancellationToken ct)
    {
        var tally = _counts.Action(action); var msg = _counts.Message(SceneService.SceneTeleport);
        tally.Plan(); tally.Offer(); if (_measureOffers) msg.Plan();
        using var attempt = new ActionAttempt(tally);
        try
        {
            using var reply = actor.Bot.Inbox.Expect(e => e.IsGame(SceneService.SceneTeleport));
            using var switchScene = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
            using var self = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
            using var end = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.SelfInfoEndFrameNotify));
            var sw = Stopwatch.StartNew();
            await actor.Bot.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = target }).WaitAsync(TimeSpan.FromSeconds(10), ct);
            var response = SceneTeleportRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
            if (response.Result != 0) { attempt.Refuse(); throw new InvalidOperationException($"Travel refused {response.Result}"); }
            var scene = SwitchSceneLineResp.Parser.ParseFrom((await switchScene.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
            await LoadSceneAsync(actor, scene.SceneEntry, self, end, ct);
            attempt.Complete();
        }
        catch (Exception error) { attempt.Fail(error); throw; }
    }

    private static async Task LoadSceneAsync(BotActor actor, long scene,
        BotExpectation self, BotExpectation end, CancellationToken ct)
    {
        using var ack = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.SceneLoaded));
        await actor.Bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene, ViewMaxCount = 149 });
        var response = SceneLoadedResp.Parser.ParseFrom((await ack.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
        if (response.Result != 0) throw new InvalidOperationException($"Scene load refused {response.Result}");
        var baseInfo = EntitySelf.Parser.ParseFrom((await self.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body).Base;
        await end.WaitAsync(TimeSpan.FromSeconds(30), ct);
        actor.Scene = scene;
        actor.HomeX = actor.X = baseInfo.MovementInfo.Pos.X;
        actor.Y = baseInfo.MovementInfo.Pos.Y;
        actor.HomeZ = actor.Z = baseInfo.MovementInfo.Pos.Z;
    }

    private async Task WalkNearAsync(BotActor actor, WorldNpc npc, CancellationToken ct)
    {
        float tx = npc.X - 2, tz = npc.Z - 2;
        for (int i = 0; i < 30; i++)
        {
            float dx = tx - actor.X, dz = tz - actor.Z, dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < 3) return;
            float step = MathF.Min(dist, 2.5f), x = actor.X + dx / dist * step, z = actor.Z + dz / dist * step;
            await actor.Sender!.SendAsync(() => actor.Bot.WalkAsync(x, actor.Y, z, stop: i == 29), ct);
            actor.X = x; actor.Z = z;
            await Task.Delay(500, ct);
        }
        throw new InvalidOperationException("Could not position bot near Kafra for S4");
    }

    private async Task KafraHopAsync(BotActor actor, WorldNpc npc, long target, CancellationToken ct)
    {
        var tally = _counts.Action("map-hop"); var msg = _counts.Message(SceneService.NpcInteraction);
        tally.Plan(); tally.Offer(); msg.Plan();
        using var attempt = new ActionAttempt(tally);
        try
        {
            using var reply = actor.Bot.Inbox.Expect(e => e.IsGame(SceneService.NpcInteraction));
            using var switchScene = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
            using var self = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
            using var end = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.SelfInfoEndFrameNotify));
            var sw = Stopwatch.StartNew();
            await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(SceneService.NpcInteraction,
                new NpcInteractionReq { NpcUid = npc.Uid, InteractionId = 9, SceneConfigId = 10100, TargetSceneEntry = target }), ct);
            var response = NpcInteractionRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
            if (response.Result != 0) { attempt.Refuse(); throw new InvalidOperationException($"Kafra refused {response.Result}"); }
            var scene = SwitchSceneLineResp.Parser.ParseFrom((await switchScene.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
            await LoadSceneAsync(actor, scene.SceneEntry, self, end, ct);
            attempt.Complete();
        }
        catch (Exception error) { attempt.Fail(error); throw; }
    }

    private static Dictionary<int, (string Name, double Cpu)> ProcessCpuSnapshot()
    {
        var result = new Dictionary<int, (string, double)>();
        foreach (var p in Process.GetProcesses())
        {
            try { result[p.Id] = (p.ProcessName, p.TotalProcessorTime.TotalSeconds); }
            catch { }
            finally { p.Dispose(); }
        }
        return result;
    }
    private static string[] HeavyProcesses(Dictionary<int, (string Name, double Cpu)> before,
        Dictionary<int, (string Name, double Cpu)> after, int? localServerPid) =>
        after.Where(x => x.Key != Environment.ProcessId && x.Key != localServerPid &&
            x.Value.Cpu - (before.TryGetValue(x.Key, out var start) ? start.Cpu : 0) > 5)
            .Select(x => $"{x.Value.Name}[{x.Key}] CPU +{x.Value.Cpu - (before.TryGetValue(x.Key, out var start) ? start.Cpu : 0):F1}s").ToArray();
}

internal readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User)
{
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low; public uint High; public ulong Value => ((ulong)High << 32) | Low; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    public static SystemTimes? Read() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
        GetSystemTimes(out var idle, out var kernel, out var user) ? new(idle.Value, kernel.Value, user.Value) : null;
    public static double? Percent(SystemTimes? a, SystemTimes? b)
    {
        if (a is null || b is null) return null;
        double total = b.Value.Kernel - a.Value.Kernel + b.Value.User - a.Value.User;
        double idle = b.Value.Idle - a.Value.Idle;
        return total > 0 ? Math.Clamp(100 * (total - idle) / total, 0, 100) : null;
    }
}

public sealed class BotActor(string account, BotClient bot, WorldReducer reducer) : IAsyncDisposable
{
    public long CreatedAt { get; } = Stopwatch.GetTimestamp();
    public string Account { get; } = account;
    public BotClient Bot { get; } = bot;
    public WorldReducer Reducer { get; } = reducer;
    public BotSendQueue? Sender { get; set; }
    public long RoleId { get; set; }
    public long Scene { get; set; }
    public SceneLocation Location => Reducer.Location;
    public int LineNumber => Location.LineNumber;
    public float HomeX { get; set; }
    public float HomeZ { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public double LoginMs { get; set; }
    private long _wireStart;
    public long FrozenWireBytes { get; private set; }
    public ReducerSnapshot? FrozenReducer { get; private set; }
    public void BeginMeasurement() { Reducer.BeginMeasurement(); _wireStart = Bot.WireInboundBytes; }
    public void Freeze()
    {
        if (FrozenReducer != null) return;
        FrozenWireBytes = Math.Max(0, Bot.WireInboundBytes - _wireStart);
        FrozenReducer = Reducer.Snapshot();
    }
    private int _disposed;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Freeze();
        if (Sender != null) await Sender.DisposeAsync();
        await Bot.DisposeAsync();
    }
}
