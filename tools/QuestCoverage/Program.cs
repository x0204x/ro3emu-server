using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

// LAB 唯讀探針：執行既有判定與交付規劃，不啟動網路、資料庫或獎勵交易。
// 反射僅用於建立最小相依及讀取私有判定；OwnerContext 仍由正式保留流程取得。
if (args.Length != 3)
    throw new ArgumentException("Usage: QuestCoverage <export-root> <output-json> <repository-root>");
string export = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]), repo = Path.GetFullPath(args[2]);
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
var sourcePaths = Directory.GetFiles(Path.Combine(repo, "src/Ro3Emu.Server/Game"), "Task*.cs")
    .Concat(new[] { "DailyActivityService.cs", "MountService.cs", "CharacterService.Exchange.cs", "GameLoop.cs",
        "Ownership/OwnerRegistry.cs", "Ownership/LegacyOwnerScope.cs" }
        .Select(p => Path.Combine(repo, "src/Ro3Emu.Server/Game", p)))
    .Concat(new[] { "TaskConfig.cs", "ExploreNodeConfig.cs", "MultiverseSettlementTask.cs" }
        .Select(p => Path.Combine(repo, "src/Ro3Emu.Server/Data/Config/Tables", p)))
    .Append(Path.Combine(repo, "tools/QuestCoverage/Program.cs")).Order().ToArray();
var sourceHashes = sourcePaths.ToDictionary(p => Path.GetRelativePath(repo, p).Replace('\\', '/'), Hash);
var assembly = typeof(TaskService).Assembly;
string assemblyHash = Hash(assembly.Location);
var data = new GameData(Options.Create(new ServerOptions { GameDataDir = export }), NullLogger<GameData>.Instance);
var service = Empty<TaskService>();
Set(service, "_data", data);
Set(service, "_log", NullLogger<TaskService>.Instance);
Set(service, "_heldLogged", new HashSet<(long, long, string)>());
var mounts = Empty<MountService>();
Set(mounts, "_data", data);
Set(service, "_mounts", mounts);
var character = Empty<CharacterService>();
var characterData = typeof(CharacterService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
    .Single(f => f.FieldType == typeof(GameData));
characterData.SetValue(character, data);
Set(service, "_character", character);
var daily = Empty<DailyActivityService>();
Set(daily, "_data", data);
var definitions = (IDictionary)Call(service, "Defs")!;
var taskRows = TaskConfig.All(data).ToDictionary(row => row.TaskId);
var owners = new Dictionary<long, List<string>>();
void Own(long id, string owner)
{
    if (!owners.TryGetValue(id, out var list)) owners[id] = list = [];
    if (!list.Contains(owner)) list.Add(owner);
}
Own(TaskService.FirstTask, "TaskService.Login.FirstTask");
foreach (var def in (IEnumerable)Call(service, "RootTasks")!)
    Own((long)Property(def, "Id")!, "TaskService.AcceptRootTasks");
foreach (var title in (IEnumerable)Call(service, "GrowthTitles")!)
{
    Own((long)Field(title, "Item1")!, "TaskService.AcceptGrowthTitle.chapter");
    foreach (long child in (long[])Field(title, "Item2")!) Own(child, "TaskService.AcceptGrowthTitle.child");
}
foreach (DictionaryEntry entry in (IDictionary)Call(service, "GrowthClassTasks")!)
    Own((long)entry.Key, "TaskService.AcceptGrowthClass");
foreach (DictionaryEntry entry in (IDictionary)Call(service, "ActivityTasks")!)
    Own((long)entry.Key, "TaskService.AcceptActivityTasks");
foreach (long id in (IEnumerable<long>)Call(service, "ChickenTasks")!) Own(id, "TaskService.AcceptChickenTasks");
foreach (DictionaryEntry entry in (IDictionary)Call(daily, "Defs")!)
    if ((int)entry.Key != 3302 && (int)Property(entry.Value!, "Count")! > 0)
        foreach (long id in (long[])Property(entry.Value!, "Tasks")!) Own(id, $"DailyActivityService.TrackingTasks:{entry.Key}");
foreach (long root in new long[] { 3001, 3011, 3021 }) Own(root, "TaskService.AssignRing");
// 僅列設定上的接取入口；不呼叫 AcceptExploreTasks／AcceptSettlementTask 或變更角色狀態。
foreach (var node in ExploreNodeConfig.All(data))
    foreach (long id in new[] { node.QuestId, node.FinishQuestId }.Distinct())
        if (definitions.Contains(id)) Own(id, "TaskService.AcceptExploreTasks.configuration");
foreach (var settlement in MultiverseSettlementTask.All(data))
    if (definitions.Contains(settlement.TaskId)) Own(settlement.TaskId, "TaskService.AcceptSettlementTask.configuration");
// 建立儀式的入口由 AdventureGroupService 明確讀取 PublicParam，而非依任務型別猜測。
if (data.Table("PublicParamConfig").TryGetValue("AdventureGroupSwornTask", out var ritual)
    && ritual.ValueKind == JsonValueKind.Array)
    foreach (var id in ritual.EnumerateArray())
        if (GameData.Number(id) is > 0 and var number) Own((long)number, "AdventureGroupService.RitualTasks");

// DungeonTaskChain 只讀 Defs／TaskConfig 並建立該 service 的設定快取，不呼叫 OnDungeonRun，
// 不建立 attachment／假 producer，也不將這些 external tracker 改成一般任務可交付。
Dictionary<long, long> dungeonTrackerScenes = [];
foreach (long scene in taskRows.Values.Select(row => row.DungeonId).Where(scene => scene > 0).Distinct().Order())
    foreach (long id in (IEnumerable<long>)Call(service, "DungeonTaskChain", scene)!)
        if (!dungeonTrackerScenes.TryAdd(id, scene))
            throw new InvalidOperationException($"同一任務出現於多個副本追蹤鏈：{id}");

(string Kind, string Status, string[] ExternalOwners, string[] Evidence, long? Scene) OwnerInfo(long id, long[][] targets)
{
    if (dungeonTrackerScenes.TryGetValue(id, out long scene))
        return ("external_dungeon_tracker", "CONFIGURATION_MAPPED_RUNTIME_NOT_RUN", ["TaskService.DungeonRunTasks"],
            ["TaskService.DungeonTasks.DungeonTaskChain", "TaskService.ExtraTasks registration", "LC_TaskHelper.CheckShowMainTask"], scene);
    // G LC_OnePieceHelper.lua:14,134 決定1400010；LC_TaskHelper.lua:1114-1183以HiddenTreasureMapInfo建畫面任務。
    if (id == 1400010 && targets.Any(target => target is [103048, _]))
        return ("client_derived_tracker", "CLIENT_SOURCE_CONTRACT_RUNTIME_NOT_RUN", ["LC_TaskHelper._getOtherClientTask"],
            ["LC_OnePieceHelper.GetOnePieceTaskId", "HiddenTreasureMapInfo.iNpcCount/bIsOpen", "TaskConfig[1400010]"], null);
    // G9001501／9001502是2220102賽馬階段／GM窗口；目前沒有對應的正式server producer。
    if (id is 9001501 or 9001502 && taskRows[id].DungeonId == 2220102)
        return ("development_task", "NO_MAPPED_SERVER_PRODUCER_RUNTIME_NOT_RUN", [],
            ["TaskConfig[9001501/9001502]._iDungeonID/_iName", "ProgressConfig[200002/210001]"], 2220102);
    return owners.ContainsKey(id)
        ? ("stored_quest", "CONFIGURED_ACCEPT_OWNER_ONLY_RUNTIME_NOT_RUN", [], ["ConfiguredAcceptOwners"], null)
        : ("UNKNOWN", "ACCEPT_OWNER_UNKNOWN_RUNTIME_NOT_RUN", [], ["TaskDef alone does not establish an accept owner"], null);
}

using var loop = new GameLoop(NullLogger<GameLoop>.Instance);
var registry = new OwnerRegistry(loop, null!); // 此探針只保留 Role key，不呼叫角色載入或持久化 API。
await loop.StartAsync(CancellationToken.None);
var records = new List<object>();
try
{
    await loop.InvokeAsync(async () =>
    {
        using var scope = await registry.EnterAsync("QuestCoverage.read-only-plan", ReservationKey.Role(1));
        var session = new GameSession(null!, null!)
        {
            Role = new Role(1, "LAB", 1, "LAB", 1, 1000, 1, 0, 0, 0, 400003, 0, 0, 0, 0),
            Progress = new RoleProgress(),
        };
        foreach (DictionaryEntry pair in definitions)
        {
            var def = pair.Value!;
            long id = (long)pair.Key;
            var targets = (long[][])Property(def, "Targets")!;
            var accept = (string[][])Property(def, "AcceptActions")!;
            var actions = (string[][])Property(def, "ExecActions")!;
            var track = (string[][])Property(def, "Track")!;
            var owner = OwnerInfo(id, targets);
            var arguments = new object?[] { session, scope.Context, def, null, false, null, null };
            bool? delivery = null;
            string? deliveryError = null;
            try { delivery = (bool)Call(service, "TryDeliveryActions", arguments)!; }
            catch (Exception ex) { deliveryError = Unwrap(ex).ToString(); }
            records.Add(new
            {
                TaskId = id,
                Type = Property(def, "Type"),
                PreTasks = Property(def, "Pre"),
                Condition = Property(def, "Condition"),
                ParsedBaseExperience = Property(def, "Exp"),
                AwardGroup = Property(def, "Award"),
                AcceptMode = Property(def, "Accept"),
                DeliverMode = Property(def, "Deliver"),
                EngineTargetsSupported = Call(service, "TargetsSupported", id),
                Targets = targets.Select(t => new
                {
                    Values = t,
                    Required = Call(service, "Required", null, t),
                    Supported = Call(service, "Supported", t),
                }).ToArray(),
                EngineAcceptActionsSupported = Call(service, "AcceptActionsSupported", def),
                AcceptActions = accept.Select(a => new { Values = a, ServerOpcode = Call(service, "ServerOpcode", (object)a) }).ToArray(),
                DeliveryActions = actions.Select(a => new { Values = a, ServerOpcode = Call(service, "ServerOpcode", (object)a) }).ToArray(),
                TrackingActions = track.Select(a => new { Values = a, ServerOpcode = Call(service, "ServerOpcode", (object)a) }).ToArray(),
                EngineDeliveryPlanSupported = delivery,
                DeliveryProbeError = deliveryError,
                PlannedCosts = (arguments[3] as IEnumerable)?.Cast<object>().Select(v => new
                { Entry = Field(v, "Item1"), Count = Field(v, "Item2") }).ToArray(),
                PlannedCutScene = arguments[4],
                PlannedQuestMount = arguments[5],
                PlannedGuildActivities = arguments[6],
                ConfiguredAcceptOwners = owners.GetValueOrDefault(id) ?? [],
                ConfiguredExternalOwners = owner.ExternalOwners,
                OwnerKind = owner.Kind,
                OwnerStatus = owner.Status,
                OwnerEvidenceKind = "SOURCE_AND_CONFIGURATION_ONLY",
                OwnerEvidence = owner.Evidence,
                ExternalTrackerScene = owner.Scene,
                RuntimeFlow = "NOT_RUN",
                PlayerAcceptance = "NOT_RUN",
            });
        }
    });
}
finally { await loop.StopAsync(CancellationToken.None); }
if (Hash(assembly.Location) != assemblyHash || sourceHashes.Any(p => Hash(Path.Combine(repo, p.Key)) != p.Value))
    throw new InvalidOperationException("探針執行期間組件或來源變更；拒絕混合證據世代。");
var configPaths = Directory.GetFiles(Path.Combine(export, "config"), "*.json").Order().ToArray();
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    GeneratedAtUtc = DateTimeOffset.UtcNow,
    Scope = "ACTUAL_ENGINE_PREDICATES_DELIVERY_PLAN_AND_STATIC_OWNER_CLASSIFICATION_ONLY",
    NetworkStarted = false,
    DatabaseOpened = false,
    AwardsCommitted = false,
    OriginalGameExecuted = false,
    EngineAssembly = assembly.Location,
    EngineAssemblySha256 = assemblyHash,
    EngineAssemblyMvid = assembly.ManifestModule.ModuleVersionId,
    SourceToAssemblyBuildBinding = "PARENT_BUILD_GATE_REQUIRED",
    SourceHashes = sourceHashes,
    ExportConfigHashes = configPaths.ToDictionary(p => Path.GetFileName(p)!, Hash),
    ExternalDungeonTrackerConfigurationCount = dungeonTrackerScenes.Count,
    ExternalOwnerClassificationIsRuntimeEvidence = false,
    Tasks = records,
}, json));
Console.WriteLine($"QuestCoverage: {records.Count} task definitions; {output}");

static T Empty<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
static void Set(object target, string name, object value) => target.GetType()
    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
static object? Field(object target, string name) => target.GetType().GetField(name)!.GetValue(target);
static object? Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target);
static object? Call(object target, string name, params object?[] args) => target.GetType()
    .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);
static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
