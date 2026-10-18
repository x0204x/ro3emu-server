using System.Globalization;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>
/// Runs only with RO3EMU_STORYWALK=1 (several minutes; kept out of the default suite):
/// <c>$env:RO3EMU_STORYWALK=1; dotnet test -c Release --no-build --filter Category=StoryWalk</c>.
/// </summary>
public sealed class StoryWalkFactAttribute : FactAttribute
{
    public StoryWalkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RO3EMU_STORYWALK") != "1")
            Skip = "Story walk: set RO3EMU_STORYWALK=1 to run it (several minutes).";
    }
}

/// <summary>
/// Pre-alpha item 5: a bot follows the whole connected main story (the 101xxxxx chain linked by _kPreTask from
/// 10101010) the way a player does and reports, per task, what it did and whether the server moved. For every task it
/// sends what the client sends for that target: 31209 area entry, 31208 dialog with the server NPC or the client's
/// task point, 31214 client interaction, 32709 + 84011 plot end, 31323 item use, 31205 NPC turn-in, 83651 cargo
/// placement, 31176 job change, 31151 skill learning, 30131 for a delivery's 141 JumpMapArea, and the client's own way
/// into a story dungeon (accept or track action 3000 → 30400, a scene-editor region whose server-host action 613
/// enters it on 31209, or an interaction's after-action 3000) followed by clearing the dungeon's waves, or for a scripted
/// scene without a fight playing each step with the event its client sends (32700 on the step's scene actor, a walk into
/// its server region or to its point, 84011 when its story ends). For the growth
/// manual the story waits on (104004 chapter tasks) it plays every child task through its panel (31409 craft, 83550
/// talents, 31500 stat points, 33308/33311 pets, 32801 Spirit Tower floors) and claims each with 31205.
///
/// What the bot fakes, and why (each use is written into the report):
/// <list type="bullet">
/// <item>Travel: <c>!warp</c> / <c>!jump</c> to the target map and point. Instead the walk checks that the map is
/// reachable from where the story left the player through portals and Kafras.</item>
/// <item>Fights: next to the monster, its HP is set to 1 before a real normal attack lands (the same shortcut as
/// DungeonTests.Kill); the kill, its exp, drops and quest credit go through the server's real settlement.</item>
/// <item>Grind: where a target waits on a level (101002) or a job change needs Base/Job levels the story has not
/// given, <c>!lvl</c> supplies exactly the missing levels; where a growth-manual craft or talent costs materials the
/// bag lacks, <c>!item</c> supplies exactly the missing cost (totals per item in the report). A real player farms
/// these. Quest items, quest rewards and quest state are never supplied.</item>
/// </list>
/// A task that does not move after the player's action is a blocker: it is reported, then pushed with
/// <c>!quest done</c> (or, when even that delivery is refused, marked delivered without its award) so the rest of the
/// chain is still walked. The test fails on any blocker that is not a known, documented open one
/// (<see cref="KnownOpen"/>), and on a known open one that no longer blocks (so the list stays true).
/// </summary>
public sealed class StoryWalkTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-storywalk-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private WebApplication _app = null!;
    private string _http = "";
    private string _gameData = "";
    private Dictionary<long, JsonElement> _tasks = null!;
    private BotClient _bot = null!;
    private readonly List<Row> _rows = new();

    /// <summary>
    /// Blockers that stay open on purpose, with the reason (content the client does not ship, or a client-side matter).
    /// The walk still reports them; it fails on any blocker not listed here.
    /// </summary>
    private static readonly Dictionary<long, string> KnownOpen = new()
    {
        // None at present. The scripted scenes without a fight (1000201, 1000301, 3120402) are reconstructed
        // (Reconstructed/scripted-scenes.json) and played through the events their client sends (ClearDungeonAsync).
    };

    private sealed record Row(long Task, string Name, string Targets, string Result, string Detail);

    private sealed record Region(long Map, long Id, float X, float Z, string Actions);

    private Dictionary<long, Region> _regions = null!;

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _gameData = Path.Combine(RepoRoot(), "data");
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = _gameData;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_gameData, "config", "TaskConfig.json")));
        _tasks = doc.RootElement.EnumerateObject().ToDictionary(p => long.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.Clone());
        _regions = LoadRegions();
    }

    public async Task DisposeAsync()
    {
        if (_bot != null) await _bot.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    // ---- the chain ----

    private long[] Pre(long id) => _tasks[id].TryGetProperty("_kPreTask", out var p) && p.ValueKind == JsonValueKind.Array
        ? p.EnumerateArray().Select(e => e.GetInt64()).ToArray() : [];

    /// <summary>10101010 and then, task by task, the 101xxxxx child whose _kPreTask is the previous one.</summary>
    private List<long> MainChain()
    {
        var children = new Dictionary<long, List<long>>();
        foreach (var id in _tasks.Keys)
            foreach (var pre in Pre(id))
                (children.TryGetValue(pre, out var list) ? list : children[pre] = new()).Add(id);
        var chain = new List<long>();
        for (long id = TaskService.FirstTask; id != 0;)
        {
            chain.Add(id);
            id = children.GetValueOrDefault(id)?.Where(c => c / 100000 == 101).Order().FirstOrDefault() ?? 0;
        }
        return chain;
    }

    private long[][] Targets(long id) => _tasks[id].GetProperty("_kProgressTarget").EnumerateArray()
        .Select(t => t.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? e.GetInt64() : long.Parse(e.GetString()!, CultureInfo.InvariantCulture)).ToArray())
        .Where(t => t.Length > 0).ToArray();

    private string[][] Actions(long id, string field) => _tasks[id].TryGetProperty(field, out var a) && a.ValueKind == JsonValueKind.Array
        ? a.EnumerateArray().Select(x => x.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()).ToArray()).ToArray() : [];

    private long[] Longs(long id, string field) => _tasks[id].TryGetProperty(field, out var a) && a.ValueKind == JsonValueKind.Array
        ? a.EnumerateArray().Select(e => e.GetInt64()).ToArray() : [];

    private string Name(long id) => Service<GameData>().TextOf(_tasks[id], "_iName");

    /// <summary>The map/point a task tracks to (_kTrack 142/143 [map, point]).</summary>
    private (long Map, long Point)? Track(long id) => Actions(id, "_kTrack")
        .Where(a => a.Length >= 3 && a[0] is "142" or "143")
        .Select(a => ((long Map, long Point)?)(long.Parse(a[1], CultureInfo.InvariantCulture), long.Parse(a[2], CultureInfo.InvariantCulture)))
        .FirstOrDefault();

    // ---- the walk ----

    [StoryWalkFact, Trait("Category", "StoryWalk")]
    public async Task TheMainStoryCanBeWalkedToItsLastReachableTask()
    {
        var chain = MainChain();
        int maxLevel = Service<CharacterService>().MaxLevel;
        // The chain ends in a task nobody can finish (e.g. 10108262 "reach Lv. 999"): accepting it is the end.
        int endIndex = chain.FindIndex(id => Targets(id).Any(t => t is [101002, var lv] && lv > maxLevel));
        if (endIndex < 0) endIndex = chain.Count;
        _bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await _bot.LoginAsync("walker", "pw");
        await _bot.EnterGateAsync();
        long roleId = (await _bot.CreateRoleAsync("Walker")).RoleBaseInfo.Id;
        await _bot.EnterWorldAsync(roleId);
        var started = DateTime.UtcNow;

        for (int index = 0; index < endIndex; index++)
        {
            long task = chain[index];
            var detail = new List<string>();
            string targets = string.Join(" ", Targets(task).Select(t => "[" + string.Join(',', t) + "]"));
            await EnsureAliveAsync();
            if (!Active(task) && !Delivered(task))
            {
                // Give the accept a moment (the previous delivery's settlement may still be running).
                for (int i = 0; i < 40 && !Active(task) && !Delivered(task); i++) await Task.Delay(50);
            }
            if (!Active(task) && !Delivered(task))
            {
                Block(task, targets, $"never accepted (pre {string.Join(',', Pre(task))} delivered: "
                    + $"{Pre(task).All(Delivered)}, condition {_tasks[task].GetProperty("_iCondition")})", detail);
                await Gm($"!quest accept {task}");
            }
            _nextMap = index + 1 < chain.Count ? MapOf(chain[index + 1]) : null;
            // Travel is faked with !warp, so check that a player could make it: from where the story left them to
            // this task's map through portals and Kafras (or the story's own jump, 141 JumpMapArea).
            if (MapOf(task) is { } map && _where is { } from && from != map && !Reachable(from).Contains(map))
                Block(task, targets, $"map {map} cannot be reached from {from} by portal or Kafra", detail);
            if (MapOf(task) is { } here) _where = here;
            if (!Delivered(task))
            {
                try
                {
                    await DoTaskAsync(task, detail);
                }
                catch (Exception e) when (e is TimeoutException or WalkBlocked)
                {
                    detail.Add(e.Message);
                }
            }
            bool moved = await WaitDeliveredAsync(task, TimeSpan.FromSeconds(3));
            if (!moved && Status(task) == TaskState.Finish && Longs(task, "_kDeliverTask") is [2, ..])
            {
                await DeliverAtNpcAsync(task, detail);
                moved = await WaitDeliveredAsync(task, TimeSpan.FromSeconds(3));
            }
            if (moved)
            {
                await StoryJumpAsync(task, detail);
                _rows.Add(new Row(task, Name(task), targets, detail.Any(d => d.StartsWith("GM", StringComparison.Ordinal)) ? "ok (GM aid)" : "ok", string.Join("; ", detail)));
                continue;
            }
            detail.Add($"server state {Describe(task)}");
            Block(task, targets, "did not complete", detail);
            await Gm($"!quest done {task}");
            if (!await WaitDeliveredAsync(task, TimeSpan.FromSeconds(3)))
            {
                // The delivery itself is refused (e.g. a cost the player cannot have): mark it delivered without its
                // award, only so the rest of the chain can still be walked. The blocker above is already recorded.
                await Service<GameLoop>().InvokeAsync(async () =>
                {
                    var s = Session();
                    s.Tasks!.Infos[task].Status = TaskState.Delivery;
                    s.Tasks.CompleteTaskNum[task] = 1;
                    await Service<TaskService>().RefreshAsync(s);
                });
                if (!await WaitDeliveredAsync(task, TimeSpan.FromSeconds(3)))
                {
                    _rows.Add(new Row(task, Name(task), targets, "ABORT", "could not even be forced"));
                    break;
                }
            }
        }
        long last = endIndex < chain.Count ? chain[endIndex] : chain[^1];
        bool reachedEnd = endIndex < chain.Count ? Active(last) : Delivered(last);
        var (level, jobLevel, career) = await OnLoop(s => (s.Progress!.Level, s.Progress.JobLevel, s.Progress.Career));
        string report = Report(chain, endIndex, reachedEnd, $"end: base {level}, job {jobLevel}, career {career}, {(DateTime.UtcNow - started).TotalSeconds:0} s");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "storywalk-report.md"), report);
        output.WriteLine(report);
        Console.WriteLine(report);
        var blocked = _rows.Where(r => r.Result.StartsWith("BLOCKED", StringComparison.Ordinal)).ToList();
        var unexpected = blocked.Where(r => !KnownOpen.ContainsKey(r.Task)).ToList();
        var stale = KnownOpen.Keys.Where(id => blocked.All(r => r.Task != id)).ToList();
        Assert.True(reachedEnd && unexpected.Count == 0 && stale.Count == 0 && _rows.All(r => r.Result != "ABORT"),
            $"{unexpected.Count} unexpected blocker(s): {string.Join(" | ", unexpected.Select(r => $"{r.Task} {r.Detail}"))}; "
            + $"known open but no longer blocked (remove from KnownOpen): {string.Join(", ", stale)}");
    }

    private void Block(long task, string targets, string why, List<string> detail)
    {
        string known = KnownOpen.TryGetValue(task, out var reason) ? $" [known open: {reason}]" : "";
        _rows.Add(new Row(task, Name(task), targets, "BLOCKED: " + why + known, string.Join("; ", detail)));
        detail.Clear();
    }

    private string Report(List<long> chain, int endIndex, bool reachedEnd, string summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Story walk: {chain.Count} chained main tasks, {endIndex} to walk, end task {(endIndex < chain.Count ? chain[endIndex] : 0)} {(reachedEnd ? "reached" : "NOT reached")}");
        sb.AppendLine(summary);
        sb.AppendLine(CultureInfo.InvariantCulture, $"blockers: {_rows.Count(r => r.Result.StartsWith("BLOCKED", StringComparison.Ordinal))}, GM aids: {_rows.Count(r => r.Result == "ok (GM aid)")}");
        if (_materialGrind.Count > 0)
            sb.AppendLine("GM grind materials: " + string.Join(", ", _materialGrind.Select(kv =>
                $"{kv.Key} {Service<GameData>().ItemName((int)kv.Key)} x{kv.Value}")));
        sb.AppendLine();
        sb.AppendLine("| task | name | targets | result | detail |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in _rows)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.Task} | {r.Name} | {r.Targets} | {r.Result} | {r.Detail.Replace('|', '/')} |");
        return sb.ToString();
    }

    private sealed class WalkBlocked(string message) : Exception(message);

    /// <summary>Does, target by target, what a player does for this task.</summary>
    private async Task DoTaskAsync(long task, List<string> detail)
    {
        var targets = Targets(task);
        for (int i = 0; i < targets.Length; i++)
        {
            if (Delivered(task) || TargetDone(task, i)) continue;
            var t = targets[i];
            switch (t[0])
            {
                case 104003: // reach area [map, area]: stand in the region, the client reports 31209
                    await GoToRegionAsync(t[1], t[2], detail);
                    await _bot.SendGameAsync(SceneService.ClientEnterArea, new ClientEnterAreaReq { AreaId = t[2] });
                    break;
                case 104001: // dialog [npcOrPoint, map, dialog]
                    await TalkAsync(t[1], t[2], t[3], detail);
                    break;
                case 309002: // plot [plotId]: the cutscene ends (32709) and the video's end action reports 84011
                    await _bot.SendGameAsync(TaskService.ExitCutSceneState, new ExitCutSceneStateReq());
                    await _bot.SendGameAsync(TaskService.PlotActingEnd, new PlotActingEndReq { PlotId = t[1] });
                    break;
                case 103046: // client interaction [interactId, n]: at the tracked point, 31214 with the interaction id
                    if (Track(task) is { } at) await GoToPointAsync(at.Map, at.Point, detail);
                    var reply = await RequestAsync(TaskService.TriggerTaskEvent, new TriggerTaskEventReq { TaskId = (int)t[1], GoalCount = (int)t[2] },
                        TriggerTaskEventResp.Parser);
                    if (reply.Result != 0) detail.Add($"31214 refused {reply.Result}");
                    break;
                case 104014 or 104002: // kill [npc..., n]
                    await KillTargetAsync(task, i, t[1..^1].Select(n => (int)n).ToHashSet(), detail);
                    break;
                case 310001: // kill [n, map]: any monster on that map
                    await KillOnMapAsync(task, i, t[2], detail);
                    break;
                case 101018: // dungeon pass [scene, n]
                    await ClearDungeonAsync(task, t[1], detail);
                    break;
                case 103042: // use item [item, n]
                    await UseItemAsync((int)t[1], (int)t[2], detail);
                    break;
                case 103051: // item submit [item, n, mode]: the items come from play; the turn-in is 31205 at the NPC
                    long have = await OnLoop(s => s.Items.Where(x => x.Entry == t[1]).Sum(x => (long)x.Stack));
                    if (have < t[2]) throw new WalkBlocked($"owns {have}/{t[2]} of item {t[1]} to submit");
                    break;
                case 103053: // cargo placement [n]: the mini-game's clear sends 83651, once per clear
                    if (Track(task) is { } cargo) await GoToPointAsync(cargo.Map, cargo.Point, detail);
                    for (int k = 0; k < t[1] && !TargetDone(task, i); k++)
                    {
                        var r = await RequestAsync(TaskService.GoodsStack, new TaskGoodsStackReq(), TaskGoodsStackResp.Parser);
                        if (r.Result != 0) { detail.Add($"83651 refused {r.Result}"); break; }
                    }
                    break;
                case 101002: // base level [n]
                    await GrindToAsync((int)t[1], null, detail);
                    break;
                case 104005: // job change from [career]
                case 101019: // job rank [rank]
                    await ChangeJobAsync(t, detail);
                    break;
                case 104012: // spend skill points [n]
                    await LearnSkillsAsync(task, i, null, (int)t[1], detail);
                    break;
                case 311001: // spend skill points on rank [rank, n]
                    await LearnSkillsAsync(task, i, (int)t[1], (int)t[2], detail);
                    break;
                case 104004: // another task delivered [taskId]
                    await CompleteSideTaskAsync(t[1], detail);
                    break;
                default:
                    throw new WalkBlocked($"walker has no handler for target {t[0]}");
            }
        }
    }

    // ---- server state ----

    private Task<T> OnLoop<T>(Func<GameSession, T> read) => Service<GameLoop>().InvokeAsync(() =>
        Task.FromResult(read(Service<SessionRegistry>().All().Single(x => x.Role != null))));

    private GameSession Session() => Service<SessionRegistry>().All().Single(x => x.Role != null);

    private TaskInfo? Info(long task) => Session().Tasks?.Infos.TryGetValue(task, out var info) == true ? info : null;
    private TaskState Status(long task) => Info(task)?.Status ?? TaskState.None;
    private bool Active(long task) => Status(task) is TaskState.Running or TaskState.Finish;
    private bool Delivered(long task) => TaskService.IsDelivered(Session(), task) && Session().Tasks != null;

    private bool TargetDone(long task, int index)
    {
        if (Info(task) is not { } info) return false;
        if (info.Status is TaskState.Delivery or TaskState.Finish) return true;
        return index < info.CompleteNum.Count && info.CompleteNum[index] >= Required(Targets(task)[index]);
    }

    /// <summary>The client's display count per target type, for the types on the main chain.</summary>
    private static long Required(long[] t) => t[0] switch
    {
        104014 or 104002 => t[^1],
        310001 or 309002 or 104012 or 101002 or 103053 or 326002 or 103034 or 101014 or 104013 => t[1],
        103024 => t[3],
        101018 or 103046 or 103042 or 311001 => t[2],
        103051 => t[2],
        _ => 1,
    };

    private string Describe(long task) => Info(task) is { } i ? $"{i.Status} [{string.Join('/', i.CompleteNum)}]" : "absent";

    private async Task<bool> WaitDeliveredAsync(long task, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!Delivered(task))
        {
            if (DateTime.UtcNow > until) return false;
            await Task.Delay(50);
        }
        return true;
    }

    private async Task Gm(string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account=walker", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private async Task<T> RequestAsync<T>(uint id, IMessage body, MessageParser<T> parser) where T : IMessage<T>
    {
        using var reply = _bot.Inbox.Expect(e => e.IsGame(id));
        await _bot.SendGameAsync(id, body);
        return parser.ParseFrom((await reply.WaitAsync(Wait)).Body);
    }

    // ---- travel (GM shortcut) ----

    private async Task WarpAsync(long scene, float? x = null, float? z = null)
    {
        if (x is { } px && z is { } pz && await OnLoop(s => s.SceneEntry == scene && s.InstanceId == 0))
        {
            await Gm(FormattableString.Invariant($"!jump {px:0.0} {pz:0.0}"));
            for (int i = 0; i < 40; i++)
            {
                var (sx, sz) = await OnLoop(s => (s.Position.X, s.Position.Z));
                if (Math.Abs(sx - px) < 2 && Math.Abs(sz - pz) < 2) return;
                await Task.Delay(25);
            }
            return;
        }
        using var sw = _bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
        await Gm(x is { } wx && z is { } wz ? FormattableString.Invariant($"!warp {scene} {wx:0.0} {wz:0.0}") : $"!warp {scene}");
        await LoadSceneAsync(SwitchSceneLineResp.Parser.ParseFrom((await sw.WaitAsync(Wait)).Body).SceneEntry);
    }

    private async Task LoadSceneAsync(long scene)
    {
        using var self = _bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
        await _bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene });
        await self.WaitAsync(Wait);
    }

    private async Task GoToPointAsync(long map, long point, List<string> detail)
    {
        if (Service<WorldData>().Map(map)?.Points.TryGetValue(point, out var p) == true) await WarpAsync(map, p.X + 1, p.Z + 1);
        else { detail.Add($"point {point} not on map {map}"); await WarpAsync(map); }
    }

    private async Task GoToRegionAsync(long map, long region, List<string> detail)
    {
        if (_regions.TryGetValue(region, out var r) && r.Map == map) await WarpAsync(map, r.X, r.Z);
        else { detail.Add($"region {region} not in map {map}'s scene editor"); await WarpAsync(map); }
    }

    private async Task EnsureAliveAsync()
    {
        if (!await OnLoop(s => s.Dead)) return;
        await _bot.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        for (int i = 0; i < 100 && await OnLoop(s => s.Dead); i++) await Task.Delay(50);
    }

    // ---- targets ----

    private async Task TalkAsync(long npcOrPoint, long map, long dialog, List<string> detail)
    {
        var world = Service<WorldData>();
        var npc = world.Map(map)?.Npcs.FirstOrDefault(n => n.NpcId == npcOrPoint);
        if (npc != null)
        {
            await WarpAsync(map, npc.X + 1, npc.Z + 1);
            await _bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { NpcUid = npc.Uid, DialogID = (int)dialog });
        }
        else if (world.Map(map)?.Points.TryGetValue(npcOrPoint, out var point) == true)
        {
            // client-side task-point NPC: the dialog reports the task point as areapointid
            await WarpAsync(map, point.X + 1, point.Z + 1);
            await _bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { DialogID = (int)dialog, Areapointid = npcOrPoint });
        }
        else throw new WalkBlocked($"no NPC or task point {npcOrPoint} on map {map}");
    }

    /// <summary>31205 at the configured NPC: _kDeliverTask [2, npc, map, point, dialog].</summary>
    private async Task DeliverAtNpcAsync(long task, List<string> detail)
    {
        var args = Longs(task, "_kDeliverTask");
        await GoToPointAsync(args[2], args[3], detail);
        var reply = await RequestAsync(TaskService.TaskDelivery, new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = task } } },
            TaskDeliveryResp.Parser);
        if (reply.Data.Count == 0) detail.Add("31205 refused");
    }

    private long? _nextMap;

    private async Task UseItemAsync(int item, int count, List<string> detail)
    {
        var guid = await OnLoop(s => s.Items.FirstOrDefault(x => x.Entry == item && x.Stack >= count)?.Guid ?? 0);
        if (guid == 0) throw new WalkBlocked($"does not own item {item} to use");
        var use = new ItemUseReq { ItemGuid = guid, ItemCount = count };
        if (Service<GameData>().Row("ItemConfig", item) is { } row && row.GetProperty("_iUseEffect").ToString().Contains("\"102\"", StringComparison.Ordinal))
        {
            // The Butterfly Wing opens the map panel; the player picks where the story goes next.
            long destination = _nextMap ?? 10100;
            detail.Add($"butterfly wing to {destination}");
            use.ItemParam.Add(new ItemUseParamInfo { ButterflyWings = new ItemUseParamInfo.Types.ButterflyWings { MapId = (int)destination } });
        }
        var reply = await RequestAsync(CharacterService.ItemUse, use, ItemUseResp.Parser);
        if (reply.Result != 0) throw new WalkBlocked($"31323 item {item} refused {reply.Result}");
        // A wing may move the player (use effects 101/102); let that scene change finish.
        await Task.Delay(300);
        var pending = _bot.Inbox.Snapshot().LastOrDefault(e => e.IsGame(MsgId.SwitchSceneLine));
        if (pending != null && await OnLoop(s => s.SceneLoading))
            await LoadSceneAsync(SwitchSceneLineResp.Parser.ParseFrom(pending.Body).SceneEntry);
    }

    private async Task KillTargetAsync(long task, int index, HashSet<int> npcs, List<string> detail)
    {
        var track = Track(task);
        long map = track?.Map ?? await OnLoop(s => s.SceneEntry);
        if (track is { } at) await GoToPointAsync(at.Map, at.Point, detail);
        int kills = 0;
        while (!TargetDone(task, index) && kills < 200)
        {
            if (!await KillOneAsync(map, m => npcs.Contains(m.Template.NpcId)))
                throw new WalkBlocked($"no killable {string.Join('/', npcs)} on map {map} after {kills} kills: {_killProblem}");
            kills++;
        }
        detail.Add($"{kills} kills (fast)");
    }

    private async Task KillOnMapAsync(long task, int index, long map, List<string> detail)
    {
        var track = Track(task);
        if (track is { } at && at.Map == map) await GoToPointAsync(at.Map, at.Point, detail);
        else await WarpAsync(map);
        int kills = 0;
        while (!TargetDone(task, index) && kills < 200)
        {
            if (!await KillOneAsync(map, _ => true)) throw new WalkBlocked($"no killable monster on map {map} after {kills} kills: {_killProblem}");
            kills++;
        }
        detail.Add($"{kills} kills (fast)");
    }

    private readonly HashSet<long> _unkillable = new();
    private string _killProblem = "";

    /// <summary>
    /// Fast kill (a test shortcut, not the fight): beside the nearest matching living monster, set its HP to 1, then a
    /// real normal attack over the wire kills it, so the server's own settlement (exp, drops, quest credit) runs. A
    /// monster the attack cannot kill is skipped (and named in the blocker if no other one dies).
    /// </summary>

    private async Task<bool> KillOneAsync(long scene, Func<MonsterUnit, bool> match, long instance = 0, int waitTenths = 600)
    {
        var world = Service<World>();
        for (int skip = 0; skip < 5; skip++)
        {
            MonsterUnit? target = null;
            for (int i = 0; i < waitTenths && target == null; i++)
            {
                target = await OnLoop(me => world.AllUnits().OfType<MonsterUnit>()
                    .Where(u => u.InstanceId == instance && (instance != 0 || u.SceneEntry == scene) && !u.Dead && u.Hp > 0
                        && !_unkillable.Contains(u.Uid) && match(u))
                    .OrderBy(u => (u.X - me.Position.X) * (u.X - me.Position.X) + (u.Z - me.Position.Z) * (u.Z - me.Position.Z))
                    .FirstOrDefault());
                if (target == null) await Task.Delay(100); // respawn
            }
            if (target == null) { _killProblem = "none alive"; return false; }
            await EnsureAliveAsync();
            int attack = await OnLoop(s => Service<SkillService>().NormalAttack(s));
            int lastResult = 0;
            for (int attempt = 0; attempt < 20 && !target.Dead; attempt++)
            {
                await Service<GameLoop>().InvokeAsync(async () =>
                {
                    if (target.Dead) return;
                    var me = Session();
                    await Service<SceneService>().TeleportInMapAsync(me, new MapPoint(target.X + 1, target.Y, target.Z, target.Face));
                    Service<MonsterService>().Engage(target, me);
                    target.Hp = 1;
                });
                await _bot.SendGameAsync(CombatService.LockEnemy, new LockEnemyReq { EnemyId = target.Uid });
                lastResult = (await RequestAsync(CombatService.UseSkill, new CastSkillReq { SkillId = attack, TargetId = target.Uid }, CastSkillRsp.Parser)).Result;
                for (int i = 0; i < 20 && !target.Dead; i++) await Task.Delay(50);
                await EnsureAliveAsync();
            }
            if (target.Dead) return true;
            // A monster the attack cannot kill (refused casts, out of reach): try another one, and say which.
            _unkillable.Add(target.Uid);
            var (mx, mz, px, pz) = await OnLoop(me => (target.X, target.Z, me.Position.X, me.Position.Z));
            _killProblem = FormattableString.Invariant($"{target.Template.NpcId} uid {target.Uid} at ({mx:0.0},{mz:0.0}) not killed, player at ({px:0.0},{pz:0.0}), last cast result {lastResult}");
        }
        return false;
    }

    /// <summary>
    /// Enters a story dungeon the way the client does and clears it. Routes: the task's accept or track action 3000
    /// (LC_InteractActions.EnterDungeon → 30400), a scene-editor region whose server-host action 613 names the scene
    /// (LC_System_Task.OnEnterArea returns false for a server action, so the client reports 31209), an interaction
    /// whose after-action 3000 names it (the client then sends 30400). Without any of them the walk says so.
    /// </summary>
    private async Task ClearDungeonAsync(long task, long scene, List<string> detail)
    {
        using var sw = _bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine) && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == scene);
        using var refused = _bot.Inbox.Expect(e => e.IsGame(DungeonService.Enter) && EnterDungeonRsp.Parser.ParseFrom(e.Body).Result != 0);
        var region = _regions.Values.FirstOrDefault(r => r.Actions.StartsWith($"613|{scene}|", StringComparison.Ordinal));
        string sceneText = scene.ToString(CultureInfo.InvariantCulture);
        if (Actions(task, "_kAcceptAction").Concat(Actions(task, "_kTrack")).Any(a => a is ["3000", var s, ..] && s == sceneText))
        {
            if (Track(task) is { } at) await GoToPointAsync(at.Map, at.Point, detail);
            // Accept action 3000 runs on acceptance; a track action 3000 runs when the player clicks the quest tracker.
            detail.Add("entry: accept/track action 3000 → 30400");
            await _bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = scene });
        }
        else if (region != null)
        {
            detail.Add($"entry: region {region.Id} (613) on map {region.Map} → 31209");
            await WarpAsync(region.Map, region.X, region.Z);
            await _bot.SendGameAsync(SceneService.ClientEnterArea, new ClientEnterAreaReq { AreaId = region.Id });
        }
        else if (InteractionEntering(scene) is { } interaction)
        {
            // The interaction's after-actions ran in the client right after its 31214: 3000 → 30400.
            detail.Add($"entry: interaction {interaction} after-action 3000 → 30400");
            await _bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = scene });
        }
        else
        {
            detail.Add("entry: no client route found, sending 30400");
            await _bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = scene });
        }
        var entered = await Task.WhenAny(sw.WaitAsync(Wait), refused.WaitAsync(Wait));
        if (entered.IsFaulted) throw new WalkBlocked($"dungeon {scene}: no scene change after the entry request");
        var e = await entered;
        if (e.IsGame(DungeonService.Enter))
            throw new WalkBlocked($"dungeon {scene}: 30400 refused {EnterDungeonRsp.Parser.ParseFrom(e.Body).Result}");
        using var result = _bot.Inbox.Expect(x => x.IsGame(DungeonService.Result));
        await LoadSceneAsync(scene);
        long instance = await OnLoop(s => s.InstanceId);
        int kills = 0, steps = 0;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (!result.IsCompleted && DateTime.UtcNow < until)
        {
            // A scripted scene without a fight: play its current step the way the client does (see PlayStepAsync).
            var (wave, step) = await OnLoop(s => s.DungeonAttempt is { Settled: false } a && a.Wave < a.Plan.Waves.Count
                ? (a.Wave, a.Plan.Waves[a.Wave].Step) : (-1, null));
            if (step != null)
            {
                await PlayStepAsync(scene, wave, step, detail);
                for (int i = 0; i < 100 && !result.IsCompleted && await OnLoop(s => s.DungeonAttempt?.Wave) == wave; i++)
                    await Task.Delay(50);
                steps++;
                continue;
            }
            if (await KillOneAsync(scene, _ => true, instance, waitTenths: 20)) kills++;
            else await Task.Delay(100);
        }
        if (!result.IsCompleted) throw new WalkBlocked($"dungeon {scene}: no result after {kills} kills, {steps} scripted steps");
        var res = DungeonResNotify.Parser.ParseFrom((await result.WaitAsync(Wait)).Body);
        detail.Add(steps > 0 ? $"dungeon {scene}: {steps} scripted steps, result {res.Res}" : $"dungeon {scene}: {kills} kills (fast), result {res.Res}");
        await EnsureAliveAsync();
        using var back = _bot.Inbox.Expect(x => x.IsGame(MsgId.SwitchSceneLine));
        await _bot.SendGameAsync(DungeonService.Leave, new LeaveDungeonReq());
        await LoadSceneAsync(SwitchSceneLineResp.Parser.ParseFrom((await back.WaitAsync(Wait)).Body).SceneEntry);
    }

    /// <summary>
    /// One step of a scripted scene (Reconstructed/scripted-scenes.json): the bot sends what the player's client sends
    /// for the step's first completing event. For an interaction (or the dungeon message an interaction sends) it stands
    /// beside the step's scene actor offering it and sends 32700 as the client does when the player presses the
    /// interaction (or walks into its range, or picks the dialogue option that triggers it); for a region or a point it
    /// reports a walk into it (31082); for a story it sends what the client sends when the story ends (32709, 84011).
    /// Travel to the actor or region is the walk's usual GM teleport.
    /// </summary>
    private async Task PlayStepAsync(long scene, int wave, ReconstructedDungeons.ScriptedStep step, List<string> detail)
    {
        var catalog = Service<DungeonCatalog>();
        var t = step.Complete[0];
        switch (t.Kind)
        {
            case ReconstructedDungeons.TriggerKind.Plot:
                await _bot.SendGameAsync(TaskService.ExitCutSceneState, new ExitCutSceneStateReq());
                await _bot.SendGameAsync(TaskService.PlotActingEnd, new PlotActingEndReq { PlotId = t.Id });
                detail.Add($"step {wave + 1}: story {t.Id} ended (84011)");
                break;
            case ReconstructedDungeons.TriggerKind.Interaction or ReconstructedDungeons.TriggerKind.Message:
                var (actor, interaction) = await OnLoop(s => (s.DungeonAttempt?.SceneActors ?? []).SelectMany(a => catalog.Interactions(a.NpcId)
                    .Where(i => t.Kind == ReconstructedDungeons.TriggerKind.Interaction ? i == t.Id : catalog.DungeonMessage(i) == t.Id)
                    .Select(i => (a, i))).FirstOrDefault());
                if (actor == null) throw new WalkBlocked($"dungeon {scene} step {wave + 1}: no scene actor offers {t.Kind} {t.Id}");
                await Service<GameLoop>().InvokeAsync(() => Service<SceneService>().TeleportInMapAsync(Session(),
                    new MapPoint(actor.X + 1, actor.Y, actor.Z, actor.Face)));
                var reply = await RequestAsync(SceneService.NpcInteraction, new NpcInteractionReq
                    { NpcUid = actor.Uid, InteractionId = (int)interaction, SceneConfigId = scene }, NpcInteractionRsp.Parser);
                detail.Add($"step {wave + 1}: 32700 interaction {interaction} on {actor.NpcId}" + (reply.Result != 0 ? $" refused {reply.Result}" : ""));
                break;
            default:
                MapPoint at = t.Kind == ReconstructedDungeons.TriggerKind.Region && catalog.SceneRegion(scene, t.Id) is { } region
                    ? new MapPoint(region.X, region.Y, region.Z, 0)
                    : catalog.ScenePoint(scene, t.Id) ?? throw new WalkBlocked($"dungeon {scene}: no {t.Kind} {t.Id}");
                await Service<GameLoop>().InvokeAsync(() => Service<SceneService>().TeleportInMapAsync(Session(),
                    new MapPoint(at.X - 0.5f, at.Y, at.Z, 0)));
                await _bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = new Vec3 { X = at.X, Y = at.Y, Z = at.Z }, BStop = true, SpeedRate = 1 });
                detail.Add($"step {wave + 1}: walked into {t.Kind.ToString().ToLowerInvariant()} {t.Id}");
                break;
        }
    }

    /// <summary>Grind stand-in: the levels a real player would gain by fighting, supplied by !lvl and reported.</summary>
    private async Task GrindToAsync(int baseLevel, int? jobLevel, List<string> detail)
    {
        var (level, job) = await OnLoop(s => (s.Progress!.Level, s.Progress.JobLevel));
        if (level >= baseLevel && (jobLevel == null || job >= jobLevel)) return;
        int toBase = Math.Max(level, baseLevel), toJob = Math.Max(job, jobLevel ?? 0);
        detail.Add($"GM grind: base {level}→{toBase}" + (jobLevel != null ? $", job {job}→{toJob}" : ""));
        await Gm(jobLevel != null ? $"!lvl {toBase} {toJob}" : $"!lvl {toBase}");
        for (int i = 0; i < 40 && await OnLoop(s => s.Progress!.Level) < toBase; i++) await Task.Delay(25);
    }

    private static readonly Dictionary<int, (int Base, int Job)> JobLevelLimit = new() { [2] = (10, 10), [3] = (30, 40), [4] = (40, 40), [5] = (50, 40) };

    /// <summary>The change-job panel's 31176 for the first job of the next rank the current career can take.</summary>
    private async Task ChangeJobAsync(long[] target, List<string> detail)
    {
        var data = Service<GameData>();
        var (career, sex) = await OnLoop(s => (s.Progress!.Career, s.Role!.Sex));
        if (target[0] == 104005 && career != target[1]) return; // already changed
        var next = data.Table("JobConfig").Values
            .Where(j => GameData.Int(j, "_iParentId") == career && GameData.Int(j, "_iNotTransformJob") == 1
                && GameData.Int(j, "_iGenderLimit") is var g && (g == 0 || g == sex))
            .OrderBy(j => GameData.Int(j, "_iID")).FirstOrDefault();
        if (next.ValueKind == JsonValueKind.Undefined) throw new WalkBlocked($"no job to advance to from {career}");
        int jobId = (int)GameData.Int(next, "_iID");
        if (JobLevelLimit.TryGetValue((int)GameData.Int(next, "_iJobRank"), out var limit)) await GrindToAsync(limit.Base, limit.Job, detail);
        var reply = await RequestAsync(CharacterService.PlayerTransformJob, new PlayerTransformJobReq { JobId = jobId }, PlayerTransformJobResp.Parser);
        if (reply.Result != 0) throw new WalkBlocked($"31176 {career}→{jobId} refused {reply.Result}");
        detail.Add($"job {career}→{jobId}");
    }

    /// <summary>The skill panel: raise skills of the current tree (of one rank) one level at a time until the target is met.</summary>
    private async Task LearnSkillsAsync(long task, int index, int? rank, int points, List<string> detail)
    {
        var skills = Service<SkillService>();
        int learned = 0;
        for (int round = 0; round < 200 && !TargetDone(task, index); round++)
        {
            var groups = await OnLoop(s => skills.Tree(s.Progress!.Career)
                .Where(g => rank == null || skills.Rank(g.Career) == rank).Select(g => g.Group).Distinct().ToList());
            bool any = false;
            foreach (int group in groups)
            {
                if (TargetDone(task, index)) break;
                int level = await OnLoop(s => s.Progress!.Skills.GetValueOrDefault(group));
                var reply = await RequestAsync(SkillService.PlanUpdate, new SkillGrowthSkillPlanUpdateReq
                    { Plan = 0, LevelUpList = { [group] = new SkillData { SkillId = group, SkillLevel = level + 1 } } },
                    SkillGrowthSkillPlanUpdateResp.Parser);
                if (reply.Result == 0) { learned++; any = true; }
            }
            if (!any) break;
            await Task.Delay(50);
        }
        detail.Add($"{learned} skill levels learned");
        if (!TargetDone(task, index)) await Task.Delay(300);
    }

    /// <summary>
    /// 104004: the named task must be delivered. For a growth-manual chapter (the panel the task tracks) the bot plays
    /// every child task, claims each one (31205), then claims the chapter.
    /// </summary>
    private async Task CompleteSideTaskAsync(long side, List<string> detail)
    {
        if (Delivered(side)) return;
        for (int i = 0; i < 40 && !Active(side); i++) await Task.Delay(50);
        if (!Active(side)) throw new WalkBlocked($"required task {side} was never offered");
        foreach (var t in Targets(side).Where(t => t[0] == 104004))
        {
            long child = t[1];
            if (Delivered(child)) continue;
            if (!Active(child)) throw new WalkBlocked($"child task {child} of {side} was never offered");
            await PlayChildAsync(child, detail);
            await ClaimAsync(child, detail);
        }
        await ClaimAsync(side, detail);
    }

    /// <summary>The panel's claim: 31205 for a finished UI-delivered task.</summary>
    private async Task ClaimAsync(long task, List<string> detail)
    {
        for (int i = 0; i < 60 && Status(task) == TaskState.Running; i++) await Task.Delay(50);
        if (Status(task) != TaskState.Finish) throw new WalkBlocked($"task {task} is {Describe(task)}, not finished");
        var reply = await RequestAsync(TaskService.TaskDelivery, new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = task } } }, TaskDeliveryResp.Parser);
        if (reply.Data.Count == 0 || !await WaitDeliveredAsync(task, TimeSpan.FromSeconds(3)))
            throw new WalkBlocked($"claim of task {task} refused ({Describe(task)})");
        detail.Add($"claimed {task}");
    }

    /// <summary>A growth-manual child task: crafting, talents, stat points, level.</summary>
    private async Task PlayChildAsync(long child, List<string> detail)
    {
        var targets = Targets(child);
        for (int i = 0; i < targets.Length; i++)
        {
            if (TargetDone(child, i)) continue;
            var t = targets[i];
            switch (t[0])
            {
                case 103024: // craft [quality, rank, n, subtype]
                    await CraftAsync(child, i, (int)t[2], (int)t[4], detail);
                    break;
                case 326002: // talent levels [n]
                    await LearnTalentsAsync(child, i, detail);
                    break;
                case 104013: // stat points spent [n]
                    await AllocateStatsAsync(child, i, detail);
                    break;
                case 101002:
                    await GrindToAsync((int)t[1], null, detail);
                    break;
                case 103034: // deploy [n] pets
                    await DeployPetsAsync((int)t[1], detail);
                    break;
                case 101014: // Spirit Tower floor [n]
                    await ClimbTowerAsync(child, i, (int)t[1], detail);
                    break;
                default:
                    throw new WalkBlocked($"walker cannot play growth target {t[0]} of {child} yet");
            }
            for (int w = 0; w < 40 && !TargetDone(child, i); w++) await Task.Delay(50);
            if (!TargetDone(child, i)) throw new WalkBlocked($"growth task {child} target [{string.Join(',', t)}] stuck at {Describe(child)}");
        }
    }

    /// <summary>
    /// Grind stand-in for materials: what a recipe or upgrade costs and the bag lacks is supplied by !item, counted per
    /// item in the report (a real player farms or buys it).
    /// </summary>
    private readonly Dictionary<long, long> _materialGrind = new();

    private async Task SupplyAsync(IEnumerable<(long Id, double Value)> cost)
    {
        foreach (var (id, value) in cost)
        {
            long have = await OnLoop(s => Service<CharacterService>().CountOf(s, (int)id));
            long need = (long)value - have;
            if (need <= 0) continue;
            _materialGrind[id] = _materialGrind.GetValueOrDefault(id) + need;
            await Gm(FormattableString.Invariant($"!item {id} {need}"));
            for (int i = 0; i < 40 && await OnLoop(s => Service<CharacterService>().CountOf(s, (int)id)) < (long)value; i++) await Task.Delay(25);
        }
    }

    /// <summary>The craft panel's 31409 until a piece of the asked quality comes out (quality is a weighted draw).</summary>
    private async Task CraftAsync(long task, int index, int rank, int part, List<string> detail)
    {
        // The target names the made item's _iEquipPart; the request names the recipe's _iEquipType.
        var data = Service<GameData>();
        var recipe = data.Table("EquipCraftConfig").Values.First(r => GameData.Int(r, "_iRank") == rank
            && GameData.Pairs(r, "_kQualityEquipID").Any(o => data.Row("ItemConfig", (long)o.Value) is { } item
                && GameData.Int(item, "_iEquipPart") is var made && (part == 0 || made == part || (part == 2 && made == 1))));
        int subtype = (int)GameData.Int(recipe, "_iEquipType");
        int crafts = 0;
        for (; crafts < 100 && !TargetDone(task, index); crafts++)
        {
            await SupplyAsync(GameData.Pairs(recipe, "_kCost"));
            var reply = await RequestAsync(EquipmentService.Craft, new EquipCraftReq { Rank = rank, Subtype = subtype }, EquipCraftResp.Parser);
            if (reply.Result != 0) throw new WalkBlocked($"31409 craft rank {rank} subtype {subtype} refused {reply.Result}");
            await Task.Delay(30);
        }
        detail.Add($"GM grind: {crafts} crafts of rank {rank} subtype {subtype} with supplied materials");
    }

    /// <summary>The talent panel's 83550 on open nodes until the level total is met.</summary>
    private async Task LearnTalentsAsync(long task, int index, List<string> detail)
    {
        var data = Service<GameData>();
        int learned = 0;
        var refusals = new Dictionary<long, int>();
        for (int round = 0; round < 400 && !TargetDone(task, index); round++)
        {
            var nodes = await OnLoop(s => s.SeasonTalent!.UnlockedNode.Where(kv => kv.Value).Select(kv => kv.Key)
                .Select(id => (Id: id, Level: s.SeasonTalent.Plan[s.SeasonTalent.CurPlanId].TalentNodes.GetValueOrDefault(id)?.CurrentLevel ?? 0))
                .ToList());
            bool any = false;
            foreach (var (id, level) in nodes)
            {
                if (TargetDone(task, index)) break;
                if (data.Row("SeasonTalentConfig", id) is not { } node || GameData.Int(node, "_iType") == 4) continue; // Battle Oath: PvP track
                if (level >= GameData.Int(node, "_iMaxLevel")) continue;
                long group = GameData.Int(node, "_iLevelGroupId");
                var effect = data.Table("SeasonTalentEffectConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iGroup") == group
                    && GameData.Int(r, "_iLevel") == level + 1);
                if (effect.ValueKind != JsonValueKind.Object) continue;
                await SupplyAsync(GameData.Pairs(effect, "_kCosts"));
                var reply = await RequestAsync(SeasonTalentService.Upgrade, new SeasonTalentUpgradeReq { TalentId = (int)id }, SeasonTalentUpgradeResp.Parser);
                if (reply.Result == 0) { learned++; any = true; break; }
                refusals[id] = reply.Result;
            }
            // The quest refresh after an upgrade is queued: give it a moment before calling the tree exhausted.
            for (int w = 0; w < 40 && !any && !TargetDone(task, index); w++) await Task.Delay(50);
            if (!any && !TargetDone(task, index)) throw new WalkBlocked($"no talent node could be raised after {learned} levels ({Describe(task)}); refusals " + string.Join(",", refusals.Select(kv => $"{kv.Key}:{kv.Value}")));
        }
        detail.Add($"GM grind: {learned} talent levels with supplied materials");
    }

    /// <summary>The pet panel's 33311: owned pets into the battle slots (source 0 roster, target 1 set-up).</summary>
    private async Task DeployPetsAsync(int count, List<string> detail)
    {
        var (owned, deployed) = await OnLoop(s => (s.Pets?.ListPet.Keys.ToList() ?? [],
            s.Pets?.SetupPlans.GetValueOrDefault(s.Pets.CurrentPlan)?.SetUpPets.Values.Where(id => id != 0).ToHashSet() ?? []));
        if (deployed.Count + owned.Count(id => !deployed.Contains(id)) < count)
        {
            // The pet panel's 33308: a pet whose shards (PetBaseConfig _kDecompose [shard, n]) the bag holds, e.g. the
            // 20 Poring shards of 10104330's award.
            foreach (var (key, row) in Service<GameData>().Table("PetBaseConfig"))
            {
                if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("_kDecompose", out var shard) || shard.ValueKind != JsonValueKind.Array
                    || shard.GetArrayLength() != 2) continue;
                int pet = int.Parse(key, CultureInfo.InvariantCulture);
                if (owned.Contains(pet) || await OnLoop(s => Service<CharacterService>().CountOf(s, shard[0].GetInt32())) < shard[1].GetInt64()) continue;
                var combined = await RequestAsync(PetService.Combine, new PetCombineReq { PetId = pet }, PetCombineResp.Parser);
                if (combined.Result == 0) { owned.Add(pet); detail.Add($"pet {pet} combined from shards"); }
                if (deployed.Count + owned.Count(id => !deployed.Contains(id)) >= count) break;
            }
        }
        var free = owned.Where(id => !deployed.Contains(id)).ToList();
        if (deployed.Count + free.Count < count) throw new WalkBlocked($"owns {owned.Count} pet(s), {count} to deploy");
        for (int slot = 1; deployed.Count < count && free.Count > 0; slot++)
        {
            int pet = free[0];
            var reply = await RequestAsync(PetService.StateUpdate, new PetStateUpdateReq
                { PetId = pet, Source = 0, Target = (PetStateSource_Type)1, TargetSlotIndex = slot }, PetStateUpdateResp.Parser);
            if (reply.Result != 0) throw new WalkBlocked($"33311 deploy pet {pet} to slot {slot} refused {reply.Result}");
            free.RemoveAt(0);
            deployed.Add(pet);
            detail.Add($"pet {pet} deployed");
        }
    }

    /// <summary>
    /// The Spirit Tower (32801 start, then each next floor from inside, 32802 settles): floors are cleared with the
    /// same fast kills, the deployed pets in the formation.
    /// </summary>
    private async Task ClimbTowerAsync(long task, int index, int floor, List<string> detail)
    {
        var floors = Service<GameData>().Table("PetFloorConfig").Values
            .Where(r => TowerService.FitsPlayBook(r, Service<SeasonService>().PlayBook)).OrderBy(r => GameData.Int(r, "_iFloor")).ToList();
        var pets = await OnLoop(s => s.Pets?.SetupPlans.GetValueOrDefault(s.Pets.CurrentPlan)?.SetUpPets.Values.Where(id => id != 0).ToList() ?? []);
        var formation = new SetClimTowerPetsReq { Pets = { pets.Take(4) } };
        while (formation.Pets.Count < 4) formation.Pets.Add(0);
        var set = await RequestAsync(TowerService.SetPets, formation, SetClimTowerPetsRes.Parser);
        if (set.Result != 0) detail.Add($"32803 formation refused {set.Result}");
        bool inside = false;
        int cleared = 0;
        for (int attempt = 0; attempt < floor + 3 && !TargetDone(task, index); attempt++)
        {
            int current = await OnLoop(s => s.Tower is { LayerConfID: > 0 } t
                ? (int)GameData.Int(floors.First(r => GameData.Int(r, "_iID") == t.LayerConfID), "_iFloor") : 0);
            var next = floors.FirstOrDefault(r => GameData.Int(r, "_iFloor") == current + 1);
            if (next.ValueKind != JsonValueKind.Object) throw new WalkBlocked($"no tower floor after {current}");
            using var end = _bot.Inbox.Expect(e => e.IsGame(TowerService.End));
            using var sw = _bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
            var start = await RequestAsync(TowerService.Start, new ClimTowerStartReq { Layer = (int)GameData.Int(next, "_iID"), IsInClimTower = inside },
                ClimTowerStartRes.Parser);
            if (start.Result != 0) throw new WalkBlocked($"32801 tower floor {current + 1} refused {start.Result}");
            long scene = SwitchSceneLineResp.Parser.ParseFrom((await sw.WaitAsync(Wait)).Body).SceneEntry;
            await LoadSceneAsync(scene);
            inside = true;
            long instance = await OnLoop(s => s.InstanceId);
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!end.IsCompleted && DateTime.UtcNow < until)
                if (!await KillOneAsync(scene, _ => true, instance, waitTenths: 20)) await Task.Delay(100);
            if (!end.IsCompleted) throw new WalkBlocked($"tower floor {current + 1}: no result ({_killProblem})");
            var result = ClimTowerEndNotify.Parser.ParseFrom((await end.WaitAsync(Wait)).Body);
            if (!result.ClimTowerResult) throw new WalkBlocked($"tower floor {current + 1} lost (result {result.Result})");
            cleared++;
        }
        detail.Add($"tower: {cleared} floor(s) cleared (fast kills)");
        using var back = _bot.Inbox.Expect(x => x.IsGame(MsgId.SwitchSceneLine));
        await _bot.SendGameAsync(TowerService.LeaveDungeon, new LeaveDungeonReq());
        await LoadSceneAsync(SwitchSceneLineResp.Parser.ParseFrom((await back.WaitAsync(Wait)).Body).SceneEntry);
    }

    /// <summary>The stat panel's 31500: one more STR at a time; levels for the points are a grind stand-in.</summary>
    private async Task AllocateStatsAsync(long task, int index, List<string> detail)
    {
        int raised = 0;
        for (int i = 0; i < 400 && !TargetDone(task, index); i++)
        {
            var (plan, str) = await OnLoop(s => (s.StatPlans.Info.CurPlanIndex,
                s.StatPlans.Info.AllPlanBaseProperty[s.StatPlans.Info.CurPlanIndex].BaseProperty.GetValueOrDefault(1, 1)));
            var reply = await RequestAsync(CharacterService.AllocateAttrPoints,
                new AllocateAttrPointReq { PlanIndex = plan, Points = { [1] = str + 1 } }, AllocateAttrPointRsp.Parser);
            if (reply.Result == 0) { raised++; continue; }
            int level = await OnLoop(s => s.Progress!.Level);
            await GrindToAsync(level + 1, null, detail); // more points come with levels
        }
        detail.Add($"STR +{raised}");
    }

    // ---- data ----

    private long? _where;
    private readonly Dictionary<long, HashSet<long>> _reach = new();

    /// <summary>Maps reachable on foot from a map: portals (scene-editor NpcTag 26) and Kafras (tag 28) to any Capra map.</summary>
    private HashSet<long> Reachable(long from)
    {
        if (_reach.TryGetValue(from, out var known)) return known;
        var world = Service<WorldData>();
        var kafraTargets = Service<GameData>().Table("MapConfig").Keys.Select(k => long.Parse(k, CultureInfo.InvariantCulture))
            .Where(world.IsKafraDestination).ToList();
        var seen = new HashSet<long> { from };
        var queue = new Queue<long>([from]);
        while (queue.Count > 0)
        {
            var map = world.Map(queue.Dequeue());
            if (map == null) continue;
            var next = map.Npcs.Where(n => n.Target != null).Select(n => n.Target!.Value.Map).ToList();
            if (map.Npcs.Any(n => n.Kind == WorldNpcKind.Kafra)) next.AddRange(kafraTargets);
            foreach (long m in next) if (seen.Add(m)) queue.Enqueue(m);
        }
        return _reach[from] = seen;
    }

    /// <summary>
    /// The delivery's client action 141 JumpMapArea [map, point, ...] (LC_InteractActions.lua JumpMapArea) sends 30131;
    /// the bot does the same, so the story's own jumps go through the server's handler.
    /// </summary>
    private async Task StoryJumpAsync(long task, List<string> detail)
    {
        if (Actions(task, "_kEXECAction").FirstOrDefault(a => a is ["141", _, _, ..]) is not { } jump) return;
        long map = long.Parse(jump[1], CultureInfo.InvariantCulture), point = long.Parse(jump[2], CultureInfo.InvariantCulture);
        using var sw = _bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
        var reply = await RequestAsync(SceneService.CustomJumpMap, new ClientCustomJumpMapReq { SceneId = map, PointId = point }, ClientCustomJumpMapResp.Parser);
        if (reply.Result != 0) { detail.Add($"30131 jump to {map}/{point} refused {reply.Result}"); return; }
        if (await OnLoop(s => s.SceneLoading)) await LoadSceneAsync(SwitchSceneLineResp.Parser.ParseFrom((await sw.WaitAsync(Wait)).Body).SceneEntry);
        _where = map;
        detail.Add($"jump 141 to {map}");
    }

    /// <summary>NPCInteractConfig rows whose _iAfterAction holds ["3000", scene]: the interaction sends the player in.</summary>
    private long? InteractionEntering(long scene)
    {
        foreach (var (key, row) in Service<GameData>().Table("NPCInteractConfig"))
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("_iAfterAction", out var after) && after.ValueKind == JsonValueKind.Array
                && after.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= 2
                    && a[0].ToString() == "3000" && a[1].ToString() == scene.ToString(CultureInfo.InvariantCulture)))
                return long.Parse(key, CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>The map a task plays on: its track, else the map in its first target.</summary>
    private long? MapOf(long task) => Track(task)?.Map ?? Targets(task).Select(t => t switch
    {
        [104001, _, var map, _] => (long?)map,
        [104003, var map, _] => map,
        [310001, _, var map] => map,
        _ => null,
    }).FirstOrDefault(m => m != null);

    private Dictionary<long, Region> LoadRegions()
    {
        var regions = new Dictionary<long, Region>();
        var data = Service<GameData>();
        foreach (var (key, row) in data.Table("MapConfig"))
        {
            if (!long.TryParse(key, out long map) || !row.TryGetProperty("_kResSceneEditorName", out var n) || n.GetString() is not { Length: > 0 } name) continue;
            var path = Path.Combine(_gameData, "sceneeditor", name.ToLowerInvariant() + ".json");
            if (!File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("m_kRegins", out var rs) || rs.ValueKind != JsonValueKind.Object) continue;
            foreach (var r in rs.EnumerateObject())
            {
                if (!long.TryParse(r.Name, out long id) || !r.Value.TryGetProperty("center", out var c)) continue;
                string actions = r.Value.TryGetProperty("actions", out var a) ? a.GetString() ?? "" : "";
                regions.TryAdd(id, new Region(map, id, c.GetProperty("x").GetSingle(), c.GetProperty("z").GetSingle(), actions));
            }
        }
        return regions;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
