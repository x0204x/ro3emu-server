using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Walks the main story of chapter 1 (10101010 → 10102010) with a bot the way a player does, driven by TaskConfig: for
/// every running task it produces the client event its target waits for (area entry, cutscene end, dialog with the task
/// point or the server NPC on its map, client interaction, job change through 31176, skill points) and kills the
/// monsters itself, so the exp for the job change comes from play alone. Fails with the task that stopped moving.
/// </summary>
public sealed class QuestWalkTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-walk-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private Dictionary<long, JsonElement> _tasks = null!;

    private const long LastChapterOneTask = 10102010;

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        string gameData = Path.Combine(RepoRoot(), "data");
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = gameData;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(gameData, "config", "TaskConfig.json")));
        _tasks = doc.RootElement.EnumerateObject().ToDictionary(p => long.Parse(p.Name), p => p.Value.Clone());
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task Gm(string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account=walker", new StringContent(line))).EnsureSuccessStatusCode();
    }

    /// <summary>The server's view of the walker: running/finished task ids and the current map.</summary>
    private async Task<(List<long> Running, HashSet<long> Delivered, long Scene, int Career)> State()
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        return await loop.InvokeAsync(() =>
        {
            var s = sessions.All().Single(x => x.Role != null);
            var running = s.Tasks!.Infos.Values.Where(i => i.Status is TaskState.Running or TaskState.Finish).Select(i => i.TaskId).ToList();
            var delivered = s.Tasks.Infos.Values.Where(i => i.Status == TaskState.Delivery).Select(i => i.TaskId).ToHashSet();
            return Task.FromResult((running, delivered, s.SceneEntry, s.Progress!.Career));
        });
    }

    private async Task WarpAsync(BotClient bot, long scene, float? x = null, float? z = null)
    {
        await Gm(x is { } px && z is { } pz
            ? FormattableString.Invariant($"!warp {scene} {px:0.0} {pz:0.0}")
            : $"!warp {scene}");
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.SwitchSceneLine), Wait);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry });
        await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.EntitySelf), Wait);
    }

    [ClientDataFact, Trait("Category", "Slow")]
    public async Task ChapterOneCanBeWalkedToTheEnd()
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("walker", "pw");
        await bot.EnterGateAsync();
        long roleId = (await bot.CreateRoleAsync("Walker")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(roleId);
        var world = _app.Services.GetRequiredService<WorldData>();
        var log = new List<string>();

        for (int step = 0; step < 200; step++)
        {
            var (running, delivered, scene, career) = await State();
            if (delivered.Contains(LastChapterOneTask)) break;
            var main = running.Where(id => GameData(id, "_iTaskType") == 1).OrderBy(id => id).ToList();
            Assert.True(main.Count > 0, $"no main task running after: {string.Join(" / ", log.TakeLast(5))}");
            long task = main[0];
            var targets = _tasks[task].GetProperty("_kProgressTarget").EnumerateArray()
                .Select(t => t.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? e.GetInt64() : long.Parse(e.GetString()!)).ToArray()).ToList();
            foreach (var t in targets)
            {
                switch (t[0])
                {
                    case 104003: // reach area [map, area]
                        if (scene != t[1]) await WarpAsync(bot, t[1]);
                        await bot.SendGameAsync(SceneService.ClientEnterArea, new ClientEnterAreaReq { AreaId = t[2] });
                        break;
                    case 309002: // cutscene ended
                        await bot.SendGameAsync(TaskService.PlotActingEnd, new PlotActingEndReq { PlotId = t[1] });
                        break;
                    case 104001: // dialog [npcOrPoint, map, dialog]
                        if (scene != t[2]) await WarpAsync(bot, t[2]);
                        var npc = world.Map(t[2])?.Npcs.FirstOrDefault(n => n.NpcId == t[1]);
                        if (npc != null)
                        {
                            await WarpAsync(bot, t[2], npc.X + 1, npc.Z + 1);
                            await bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { NpcUid = npc.Uid, DialogID = (int)t[3] });
                        }
                        else
                        {
                            // client-side task-point NPC: the dialog reports the task point as areapointid
                            var point = world.Map(t[2])!.Points[t[1]];
                            await WarpAsync(bot, t[2], point.X + 1, point.Z + 1);
                            await bot.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq { DialogID = (int)t[3], Areapointid = t[1] });
                        }
                        break;
                    case 103046: // client interaction [interact, n]
                        await bot.SendGameAsync(TaskService.TriggerTaskEvent, new TriggerTaskEventReq { TaskId = (int)t[1], GoalCount = (int)t[2] });
                        break;
                    case 104005 when career == t[1]: // job change from Novice: the panel's 31176, with the levels the chapter gave
                        await bot.SendGameAsync(CharacterService.PlayerTransformJob, new PlayerTransformJobReq { JobId = 1100 });
                        var rsp = PlayerTransformJobResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.PlayerTransformJob), Wait)).Body);
                        Assert.True(rsp.Result == 0, $"job change refused {rsp.Result} after: {string.Join(" / ", log.TakeLast(6))}");
                        break;
                    case 104014 or 104002: // kill [npc, n]
                        for (int k = 0; k < t[2] && !await TargetDone(task); k++) await KillAsync(bot, scene, n => n == t[1]);
                        break;
                    case 310001: // kill [n, map]: any monster there (the passive Porings / Fabres)
                        if (scene != t[2]) await WarpAsync(bot, t[2]);
                        for (int k = 0; k < t[1] && !await TargetDone(task); k++) await KillAsync(bot, t[2], n => n is 5000001 or 5000003);
                        break;
                    case 104012: // spend skill points: Bash (11001) to 10
                        await bot.SendGameAsync(SkillService.PlanUpdate, new SkillGrowthSkillPlanUpdateReq { Plan = 0, LevelUpList = { [11001] = new SkillData { SkillLevel = 10 } } });
                        break;
                    default: // kills and the rest: GM completes the targets
                        await Gm($"!quest finish {task}");
                        break;
                }
            }
            // The task must move (delivered) within a moment.
            bool moved = false;
            for (int i = 0; i < 40 && !moved; i++)
            {
                await Task.Delay(50);
                moved = (await State()).Delivered.Contains(task);
            }
            log.Add($"{task} {(moved ? "ok" : "STUCK")} {string.Join(",", targets.Select(t => t[0]))}");
            Assert.True(moved, $"task {task} did not complete: {string.Join(" / ", log.TakeLast(6))}");
        }
        var level = await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            Task.FromResult(_app.Services.GetRequiredService<SessionRegistry>().All().Single(x => x.Role != null).Progress!));
        log.Add($"end: base {level.Level}, job {level.JobLevel}");
        var final = await State();
        Assert.True(final.Delivered.Contains(LastChapterOneTask), string.Join(" / ", log));
        Assert.Equal(1100, final.Career);
        Console.WriteLine(string.Join("\n", log));
    }

    private async Task<bool> TargetDone(long task) => (await State()).Delivered.Contains(task);

    /// <summary>Jumps next to the nearest living monster that matches and attacks it until it dies (server auto-attack).</summary>
    private async Task KillAsync(BotClient bot, long scene, Func<int, bool> npc)
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var world = _app.Services.GetRequiredService<World>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var skills = _app.Services.GetRequiredService<SkillService>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        // Like a player: a Beginner Potion (starter kit) below half HP, before the fight and during it.
        async Task<bool> SipAsync()
        {
            var (hp, max, potion) = await loop.InvokeAsync(() =>
            {
                var me = sessions.All().Single(x => x.Role != null);
                var stats = character.Stats(me);
                return Task.FromResult((stats[2000], stats[101], me.Items.FirstOrDefault(i => i.Entry == 1000000)?.Guid ?? 0));
            });
            if (hp * 2 >= max || potion == 0) return false;
            await bot.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion, ItemCount = 1 });
            return true;
        }
        for (int sip = 0; sip < 5 && await SipAsync(); sip++) await Task.Delay(1200); // item cooldown
        var (target, attack) = await loop.InvokeAsync(() =>
        {
            var me = sessions.All().Single(x => x.Role != null);
            var m = world.AllUnits().OfType<MonsterUnit>().Where(u => u.SceneEntry == scene && !u.Dead && npc(u.Template.NpcId))
                .OrderBy(u => (u.X - me.Position.X) * (u.X - me.Position.X) + (u.Z - me.Position.Z) * (u.Z - me.Position.Z)).First();
            return Task.FromResult((m, skills.NormalAttack(me)));
        });
        // Like the client: attack again only when the auto-attack stopped (a potion, a jump) and chase a target that
        // roamed out of reach. A new cast cancels the swing in progress, so attacking blindly on a timer starved the
        // fight under a loaded suite (a Poring left at 65 HP after 30 s). Under load a fight also lasts longer: the
        // bot died once with its potions unused.
        for (int i = 0, check = 0; i < 300 && !target.Dead; i++)
        {
            // At most every 1.2 s (the potion cooldown).
            if (i % 12 == 6) await SipAsync();
            if (i >= check)
            {
                check = i + 3;
                var (attacking, near) = await loop.InvokeAsync(() =>
                {
                    var me = sessions.All().Single(x => x.Role != null);
                    return Task.FromResult((ReferenceEquals(me.Attack?.Target, target),
                        MonsterService.Distance(target.X, target.Z, me.Position.X, me.Position.Z) <= 2f));
                });
                if (!attacking || !near)
                {
                    if (!near) await Gm(FormattableString.Invariant($"!jump {target.X:0.0} {target.Z + 1:0.0}"));
                    await bot.SendGameAsync(CombatService.LockEnemy, new LockEnemyReq { EnemyId = target.Uid });
                    await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = attack, TargetId = target.Uid });
                    check = i + 10; // give the cast a second to start before judging it again
                }
            }
            await Task.Delay(100);
        }
        if (!target.Dead)
        {
            // A diagnosis for a failure seen only under full-suite load: who could not finish the fight, and why.
            string state = await loop.InvokeAsync(() =>
            {
                var me = sessions.All().Single(x => x.Role != null);
                var stats = character.Stats(me);
                return Task.FromResult(
                    FormattableString.Invariant($"me hp {stats[2000]}/{stats[101]} dead {me.Dead} at ({me.Position.X:0.0},{me.Position.Z:0.0}) scene {me.SceneEntry}; ") +
                    FormattableString.Invariant($"monster hp {target.Hp} at ({target.X:0.0},{target.Z:0.0}) scene {target.SceneEntry}; ") +
                    FormattableString.Invariant($"potions {me.Items.Where(i => i.Entry == 1000000).Sum(i => i.Stack)}"));
            });
            Assert.Fail($"monster {target.Template.NpcId} did not die: {state}");
        }
    }

    private long GameData(long task, string field) =>
        _tasks.TryGetValue(task, out var row) && row.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
