using System.Text.Json;
using System.Security.Cryptography;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// MG_Lifejob: 5010 professions, 5018 restriction 1004 vigor, 33501..33515 learning/production.
/// LC_LifeJobHelper, LifeJob/Collection/Formula/Level configs own all IDs, prices, conditions and EXP thresholds.
/// State and inventory are committed together through CharacterService.ExchangeLockedAsync; never charge a quote.
/// </summary>
public sealed partial class LifeJobService
{
    public const uint LifeSync = 5010, RestrictSync = 5018;
    public const uint SwitchLife = 33501, ChooseTitle = 33502, RefreshTitle = 33503, StartForge = 33504,
        UpgradeTitle = 33505, StudyRecipe = 33506, StudyCollection = 33507, StudyDrop = 33508,
        CheckCollect = 33509, ForgeTime = 33510, ChooseTitleEx = 33511, RefreshTitleEx = 33512,
        HealthAll = 33513, HealthOne = 33514, HealthNotify = 33515;
    public const int VigorId = 1004, VigorUsesId = 1005;

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly AwardService _awards;
    private readonly RoleStore _roles;
    private readonly GameLoop _loop;
    private readonly TimeProvider _clock;
    private readonly ILogger<LifeJobService> _log;
    private readonly Dictionary<GameSession, ForgeAttempt> _forging = new();
    private sealed record ForgeAttempt(long Recipe, TimeSpan Started, int Duration, long Scene, long Job);

    /// <summary>Restriction consumption is durable separately from the current balance; epoch follows To-Do's 05:00.</summary>
    public sealed class RuntimeState
    {
        public long EnergyDay { get; set; }
        public long EnergySpent { get; set; }
        public long PotionDay { get; set; }
        public int FoodBook { get; set; }
    }

    public LifeJobService(GameData data, CharacterService character, FunctionService functions, SeasonService seasons,
        AwardService awards, RoleStore roles, GameLoop loop, TimeProvider clock, SceneService scene, ILogger<LifeJobService> log)
    {
        this._data = data; this._character = character; this._functions = functions; this._seasons = seasons;
        this._awards = awards; this._roles = roles; this._loop = loop; this._clock = clock; this._log = log;
        character.LifeItemUse = UseLifeItemAsync;
        character.ExtraAttributes.Add(FoodAttributes);
        character.ProgressHandlers.Add(OnLevelChangedAsync);
        scene.Leaving.Add(CancelAsync);
    }

    /// <summary>
    /// New roles start with unchosen slots, not fictitious cross-profession recipes (inferred from the LearnJob UI).
    /// VitalityInitCount applies once. RestrictConfig[1004] has no automatic recovery.
    /// </summary>
    public async Task LoadAsync(GameSession s)
    {
        long id = s.Role!.Id;
        byte[]? savedLife = _roles.LoadLifeJob(id), savedRestriction = _roles.LoadLifeRestrict(id),
            savedRuntime = _roles.LoadLifeRuntime(id);
        var life = savedLife is { } lifeBytes ? DBLifeJobInfo.Parser.ParseFrom(lifeBytes) : new DBLifeJobInfo();
        var restriction = savedRestriction is { } restrictionBytes ? DBRestrictInfo.Parser.ParseFrom(restrictionBytes) : new DBRestrictInfo();
        var runtime = savedRuntime is { } runtimeBytes ? JsonSerializer.Deserialize<RuntimeState>(runtimeBytes) ?? new() : new();
        if (!restriction.RestrictCount.ContainsKey(VigorId)) restriction.RestrictCount[VigorId] = new RestrictInfo
        { Id = VigorId, Count = Param("VitalityInitCount", 500), LimitCount = RestrictMax(VigorId), LastRecoverTime = NowSeconds };
        if (!restriction.RestrictCount.ContainsKey(VigorUsesId)) restriction.RestrictCount[VigorUsesId] = new RestrictInfo
        { Id = VigorUsesId, Count = RestrictMax(VigorUsesId), LimitCount = RestrictMax(VigorUsesId) };
        Reconcile(life, restriction, runtime);
        EnsureJobRows(life);
        NormalizeTitles(life);
        restriction.RestrictCount[VigorId].LimitCount = VigorLimit(s, life);
        byte[] lifeBytesNext = life.ToByteArray(), restrictionBytesNext = restriction.ToByteArray(),
            runtimeBytesNext = JsonSerializer.SerializeToUtf8Bytes(runtime);
        if (!lifeBytesNext.AsSpan().SequenceEqual(savedLife) || !restrictionBytesNext.AsSpan().SequenceEqual(savedRestriction)
            || !runtimeBytesNext.AsSpan().SequenceEqual(savedRuntime))
        {
            var tx = _roles.NewTransaction();
            tx.ItemDelta(id, s.Items, s.Items);
            tx.RoleBlob(id, "lifejob", lifeBytesNext);
            tx.RoleBlob(id, "life_restrict", restrictionBytesNext);
            tx.RoleBlob(id, "life_runtime", runtimeBytesNext);
            tx.WithEconomy(_character.ProgressionEconomy(s, "cd13c/life-login-reconcile", [], [],
                new
                {
                    LifeHash = Convert.ToHexString(SHA256.HashData(lifeBytesNext)),
                    RestrictHash = Convert.ToHexString(SHA256.HashData(restrictionBytesNext)),
                    RuntimeHash = Convert.ToHexString(SHA256.HashData(runtimeBytesNext)),
                    Vigor = restriction.RestrictCount[VigorId].Count,
                    VigorUses = restriction.RestrictCount[VigorUsesId].Count,
                    runtime.EnergySpent, runtime.EnergyDay, runtime.PotionDay, runtime.FoodBook
                }));
            await _roles.CommitAsync(tx, "life/load", _ =>
            { s.LifeJob = life; s.Restrictions = restriction; s.LifeRuntime = runtime; });
        }
        else { s.LifeJob = life; s.Restrictions = restriction; s.LifeRuntime = runtime; }
    }

    public async Task LoginAsync(GameSession s)
    {
        if (s.LifeJob == null) await LoadAsync(s);
        await SyncAsync(s);
        await HealthAsync(s);
    }

    public static long ActiveLevel(GameSession s, long job) => s.LifeJob is { } life
        && life.ActiveLifejob.Values.Contains(job) && life.LifeJobInfoMap.TryGetValue(job, out var info) ? info.JobLevel : -1;

    private long NowSeconds => _clock.GetUtcNow().ToUnixTimeSeconds();
    private long Today => Day(_clock.GetLocalNow().DateTime);
    private static long Day(DateTime now) => DateOnly.FromDateTime(now.AddHours(-5)).DayNumber;
    public static long DailyEnergyUsed(GameSession s, long restriction) => restriction == VigorId
        && s.LifeRuntime.EnergyDay == Day(DateTime.Now) ? s.LifeRuntime.EnergySpent : 0;
    private long Param(string name, long fallback) => _data.Table("PublicParamConfig").TryGetValue(name, out var v)
        ? (long)(GameData.Number(v) ?? fallback) : fallback;
    private long RestrictMax(int id) => _data.Row("RestrictConfig", id) is { } r ? GameData.Int(r, "_iMaxRestrictNum") : 0;
    private static RuntimeState Copy(RuntimeState r) => new()
    { EnergyDay = r.EnergyDay, EnergySpent = r.EnergySpent, PotionDay = r.PotionDay, FoodBook = r.FoodBook };

    private void Reconcile(DBLifeJobInfo life, DBRestrictInfo restrict, RuntimeState runtime)
    {
        if (runtime.EnergyDay != Today) { runtime.EnergyDay = Today; runtime.EnergySpent = 0; }
        if (runtime.PotionDay != Today)
        {
            runtime.PotionDay = Today;
            if (restrict.RestrictCount.TryGetValue(VigorUsesId, out var uses))
            { uses.Count = RestrictMax(VigorUsesId); uses.LastRecoverTime = NowSeconds; }
        }
        if (runtime.FoodBook != 0 && runtime.FoodBook != _seasons.PlayBook)
        {
            life.MagicFoodTimes.Clear();
            // Localization_en[110112]: life titles and their skills reset when the season changes.
            foreach (var job in life.LifeJobInfoMap.Values.Where(j => j.JobLevel > 0)) job.TitleInfo = new LifeJobTitleInfo { JobLevel = 1 };
        }
        runtime.FoodBook = _seasons.PlayBook;
    }

    /// <summary>Restrictions another service keeps (the Rift's RestrictConfig 1020): shown in the same 5018 map, never
    /// saved with or compared against the life restrictions.</summary>
    public List<Func<GameSession, RestrictInfo?>> ProjectedRestrictions { get; } = new();

    private DBRestrictInfo RestrictView(GameSession s)
    {
        var view = s.Restrictions!.Clone();
        foreach (var project in ProjectedRestrictions)
            if (project(s) is { } info) view.RestrictCount[info.Id] = info;
        return view;
    }

    /// <summary>5018 with the projected restrictions; the sync sends only what changed.</summary>
    public Task SyncRestrictionsAsync(GameSession s) =>
        s.Restrictions == null ? Task.CompletedTask : CharacterService.SyncAsync(s, RestrictSync, RestrictView(s));

    private async Task SyncAsync(GameSession s)
    {
        await CharacterService.SyncAsync(s, RestrictSync, RestrictView(s));
        // LC_DAL_LifeJob:800 dereferences the old career asset when a new row arrives incrementally.
        // Rebuild all life assets when learning a previously unrecorded job; no fabricated inactive progress rows.
        if (s.Synced.GetValueOrDefault(LifeSync) is DBLifeJobInfo before
            && s.LifeJob!.LifeJobInfoMap.Keys.Any(id => !before.LifeJobInfoMap.ContainsKey(id))) s.Synced.Remove(LifeSync);
        await CharacterService.SyncAsync(s, LifeSync, s.LifeJob!.Clone(), PrepareWire);
    }

    /// <summary>LC_PbHelper.ParseMapTypeIncrement materializes a table before removing a tombstone (wire only).</summary>
    private static void PrepareWire(IMessage message)
    {
        if (message is not DBLifeJobInfoIncrement { SyncAllMark: true } full) return;
        if (full.ActiveLifejob.Count == 0) full.ActiveLifejob[0] = new() { Del = true };
        if (full.LifeJobInfoMap.Count == 0) full.LifeJobInfoMap[0] = new() { Del = true };
        if (full.LifeJobUnLockDrop.Count == 0) full.LifeJobUnLockDrop[0] = new() { Del = true };
    }

    private bool Current(JsonElement row) => Numbers(row, "_kMultiverseArray") is var books
        && (books.Length == 0 || books.Contains(0) || books.Contains(_seasons.PlayBook));
    private static long[] Numbers(JsonElement row, string field) => row.TryGetProperty(field, out var a) && a.ValueKind == JsonValueKind.Array
        ? a.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
    private static List<(int Entry, long Count)> Costs(JsonElement row, string field, int count = 1) =>
        GameData.Pairs(row, field).Select(p => (checked((int)p.Id), checked((long)p.Value * count))).ToList();

    /// <summary>33513/14 both send 33515; both maps must be present even for a single-id query (DAL lines 1542..1554).</summary>
    public Task HealthAsync(GameSession s, GetHealthCostReq? req = null, int error = 0)
    {
        var reply = new GetHealthCostNtf { Result = error };
        foreach (var r in _data.Table("LifeCollectionConfig").Values.Where(Current))
            reply.CollectHealthCost[(int)GameData.Int(r, "_iCollectionId")] = VigorCost(s, r, false);
        foreach (var r in _data.Table("LifeFormulaConfig").Values.Where(Current))
            reply.ForgeHealthCost[(int)GameData.Int(r, "_iFormulaId")] = VigorCost(s, r, true);
        if (req != null && (req.Type is not (0 or 1) || !(req.Type == 0 ? reply.ForgeHealthCost : reply.CollectHealthCost).ContainsKey(req.Id)))
            reply.Result = ErrorCodes.LifeConfig;
        return s.SendGameAsync(HealthNotify, reply);
    }

    /// <summary>Keep every 335xx request typed and non-mutating before role entry and after role exit.</summary>
    public Task RejectUnloadedAsync(GameSession s, uint message)
    {
        const int error = ErrorCodes.FeatureUnavailable;
        if (message is HealthAll or HealthOne or HealthNotify) return HealthAsync(s, error: error);
        IMessage reply = message switch
        {
            SwitchLife => new SwitchLifeJobResp { Result = error },
            ChooseTitle => new ChooseTitlePointResp { Result = error },
            RefreshTitle => new RefreshTitlePointResp { Result = error },
            StartForge => new StartForgeResp { Result = error },
            UpgradeTitle => new UpGradeJobTileResp { Result = error },
            StudyRecipe => new StudyRecipeResp { Result = error },
            StudyCollection => new StudyCollectionResp { Result = error },
            StudyDrop => new StudyCollectionDropResp { Result = error },
            CheckCollect => new CheckCanCollectNtf { Result = error },
            ForgeTime => new GetForgeCostTimeResp { Result = error },
            ChooseTitleEx => new ChooseTitlePointExResp { Result = error },
            RefreshTitleEx => new RefreshTitlePointExResp { Result = error },
            _ => new FinishLifeJobGameResp { Result = error },
        };
        return s.SendGameAsync(message, reply);
    }

    /// <summary>
    /// 33501: two selected slots. Every learn consumes LifeJobConfig._iChangeConsume (the workshop certificate 1291000):
    /// the client asks "Use the following item to unlock this class?" (GetJobSwitchCost) for the first profession too.
    /// </summary>
    public async Task SwitchAsync(GameSession s, SwitchLifeJobReq req)
    {
        using var gate = await CharacterService.Lock(s);
        long job = (long)req.NewLifejobId;
        var row = _data.Row("LifeJobConfig", job);
        int error = row == null ? ErrorCodes.LifeInvalidJob : req.OpJobType is not (1 or 2) ? ErrorCodes.Parameter
            : s.LifeJob!.ActiveLifejob.Values.Contains(job) ? ErrorCodes.LifeAlreadyLearned
            : !_functions.ConditionMet(s, GameData.Int(row.Value, "_iCondition")) ? ErrorCodes.LifeRequirements : 0;
        if (error == 0)
        {
            var next = s.LifeJob!.Clone();
            var cost = Costs(row!.Value, "_iChangeConsume");
            next.ActiveLifejob[req.OpJobType] = job;
            if (!next.LifeJobInfoMap.TryGetValue(job, out var known) || known.JobLevel == 0) next.LifeJobInfoMap[job] = NewJob(job);
            // (inferred) Free, condition-zero gathering entries are the initial profession curriculum.
            foreach (var c in _data.Table("LifeCollectionConfig").Values.Where(c => Current(c) && GameData.Int(c, "_iJobId") == job && GameData.Int(c, "_iCondition") == 0))
                UnlockCollection(next, c);
            if (await CommitAsync(s, "cd13c/life-switch", next, s.Restrictions!.Clone(), Copy(s.LifeRuntime), cost, [],
                result: new { Job = job, Slot = req.OpJobType }) == null)
                error = _character.ShortageOf(s, cost);
            else _forging.Remove(s);
        }
        await s.SendGameAsync(SwitchLife, new SwitchLifeJobResp { Result = error, NewLifejobId = req.NewLifejobId });
    }

    /// <summary>
    /// Every profession has a row from the start, level 0 until learned: the Learn tip (UI_Sys_Life_Tips_GloryPanel
    /// RefreshView) reads LifeJobTitleInfo[jobId].iTitleId for a profession that is not learned yet, without a nil check.
    /// </summary>
    private void EnsureJobRows(DBLifeJobInfo life)
    {
        foreach (var key in _data.Table("LifeJobConfig").Keys)
            if (long.TryParse(key, out long job) && !life.LifeJobInfoMap.ContainsKey(job))
                life.LifeJobInfoMap[job] = new DBLifeJobInfoDetail
                {
                    JobId = job, TitleInfo = new LifeJobTitleInfo(),
                    ToolsInfo = new LifeJobToolsInfo(), GatherInfo = new GatherJobInfo(), BuildInfo = new BuildJobInfo()
                };
    }

    private DBLifeJobInfoDetail NewJob(long job) => new()
    {
        JobId = job, JobLevel = 1, TitleInfo = new LifeJobTitleInfo { JobLevel = 1 },
        ToolsInfo = new LifeJobToolsInfo(), GatherInfo = new GatherJobInfo(), BuildInfo = new BuildJobInfo(),
    };

    public async Task LearnRecipeAsync(GameSession s, StudyRecipeReq req)
    {
        using var gate = await CharacterService.Lock(s);
        var row = _data.Row("LifeFormulaConfig", req.FormulaID);
        int error = DefinitionError(s, row, "_iUnlockCondition");
        if (error == 0)
        {
            long job = GameData.Int(row!.Value, "_iJobId");
            if (s.LifeJob!.LifeJobInfoMap[job].BuildInfo.RecipeList.ContainsKey(req.FormulaID)) error = ErrorCodes.LifeAlreadyLearned;
            else
            {
                var next = s.LifeJob.Clone(); next.LifeJobInfoMap[job].BuildInfo.RecipeList[req.FormulaID] = true;
                var cost = Costs(row.Value, "_kUnlockCost");
                if (await CommitAsync(s, "cd13c/life-recipe", next, s.Restrictions!.Clone(), Copy(s.LifeRuntime), cost, [],
                    result: new { Recipe = req.FormulaID }) == null) error = _character.ShortageOf(s, cost);
            }
        }
        await s.SendGameAsync(StudyRecipe, new StudyRecipeResp { Result = error, FormulaID = req.FormulaID });
    }

    public async Task LearnCollectionAsync(GameSession s, StudyCollectionReq req)
    {
        using var gate = await CharacterService.Lock(s);
        var row = _data.Row("LifeCollectionConfig", req.CollectionID);
        int error = DefinitionError(s, row, "_iCondition");
        if (error == 0)
        {
            long job = GameData.Int(row!.Value, "_iJobId");
            if (s.LifeJob!.LifeJobInfoMap[job].GatherInfo.ItemDetails.ContainsKey(req.CollectionID)) error = ErrorCodes.LifeCollectionLearned;
            else
            {
                var next = s.LifeJob.Clone(); UnlockCollection(next, row.Value);
                await CommitAsync(s, "cd13c/life-collection", next, s.Restrictions!.Clone(), Copy(s.LifeRuntime), [], [],
                    result: new { Collection = req.CollectionID });
            }
        }
        await s.SendGameAsync(StudyCollection, new StudyCollectionResp { Result = error });
    }

    private void UnlockCollection(DBLifeJobInfo next, JsonElement collection)
    {
        next.LifeJobInfoMap[GameData.Int(collection, "_iJobId")].GatherInfo.ItemDetails[GameData.Int(collection, "_iCollectionId")] = 1;
        foreach (long id in Numbers(collection, "_iDropId"))
            if (_data.Row("LifeDropConfig", id) is { } drop && GameData.Int(drop, "_iCondition") == 0 && Costs(drop, "_kUnlockcost").Count == 0)
                next.LifeJobUnLockDrop[id] = 1;
    }

    public async Task LearnDropAsync(GameSession s, StudyCollectionDropReq req)
    {
        using var gate = await CharacterService.Lock(s);
        var collection = _data.Row("LifeCollectionConfig", req.CollectionID);
        var drop = _data.Row("LifeDropConfig", req.DropId);
        int error = DefinitionError(s, collection, "_iCondition");
        if (error == 0) error = drop == null || !Numbers(collection!.Value, "_iDropId").Contains(req.DropId) ? ErrorCodes.LifeConfig
            : !s.LifeJob!.LifeJobInfoMap[GameData.Int(collection.Value, "_iJobId")].GatherInfo.ItemDetails.ContainsKey(req.CollectionID) ? ErrorCodes.LifeCollectionLocked
            : s.LifeJob.LifeJobUnLockDrop.ContainsKey(req.DropId) ? ErrorCodes.LifeCollectionLearned
            : !_functions.ConditionMet(s, GameData.Int(drop.Value, "_iCondition")) ? ErrorCodes.LifeRequirements : 0;
        if (error == 0)
        {
            var next = s.LifeJob!.Clone(); next.LifeJobUnLockDrop[req.DropId] = 1;
            var cost = Costs(drop!.Value, "_kUnlockcost");
            if (await CommitAsync(s, "cd13c/life-drop", next, s.Restrictions!.Clone(), Copy(s.LifeRuntime), cost, [],
                result: new { Drop = req.DropId, Collection = req.CollectionID }) == null) error = _character.ShortageOf(s, cost);
        }
        await s.SendGameAsync(StudyDrop, new StudyCollectionDropResp { Result = error, DropId = req.DropId });
    }

    private int DefinitionError(GameSession s, JsonElement? row, string condition) => row == null || !Current(row.Value) ? ErrorCodes.LifeConfig
        : ActiveLevel(s, GameData.Int(row.Value, "_iJobId")) < 0 ? ErrorCodes.LifeWrongJob
        : !_functions.ConditionMet(s, GameData.Int(row.Value, condition)) ? ErrorCodes.LifeRequirements : 0;

    public int CheckGather(GameSession s, long collectionId)
    {
        var row = _data.Row("LifeCollectionConfig", collectionId);
        int error = DefinitionError(s, row, "_iCondition");
        return error != 0 ? error
            : !s.LifeJob!.LifeJobInfoMap[GameData.Int(row!.Value, "_iJobId")].GatherInfo.ItemDetails.ContainsKey(collectionId) ? ErrorCodes.LifeCollectionLocked
            : Energy(s) < VigorCost(s, row.Value, false) ? ErrorCodes.LifeVigor : 0;
    }

    public int GatheringDuration(GameSession s, long collectionId) => _data.Row("LifeCollectionConfig", collectionId) is { } row
        ? OperationDuration(s, row, false) : 1;

    private static long Energy(GameSession s) => s.Restrictions?.RestrictCount.GetValueOrDefault(VigorId)?.Count ?? 0;

    /// <summary>
    /// A resolved collection outcome belongs to one pending interaction. Retrying its writer command must
    /// reuse the same drops, skill rolls, free-vigor result and life state.
    /// </summary>
    internal sealed class GatherResolution
    {
        internal required long RoleId { get; init; }
        internal required byte[] BeforeLife { get; init; }
        internal required byte[] BeforeRestrict { get; init; }
        internal required byte[] BeforeRuntime { get; init; }
        internal required DBLifeJobInfo Life { get; init; }
        internal required DBRestrictInfo Restrict { get; init; }
        internal required RuntimeState Runtime { get; init; }
        internal required AwardGrant[] Grants { get; init; }

        internal bool Matches(GameSession s) => s.Role?.Id == RoleId && s.LifeJob != null && s.Restrictions != null
            && s.LifeJob.ToByteArray().AsSpan().SequenceEqual(BeforeLife)
            && s.Restrictions.ToByteArray().AsSpan().SequenceEqual(BeforeRestrict)
            && JsonSerializer.SerializeToUtf8Bytes(s.LifeRuntime).AsSpan().SequenceEqual(BeforeRuntime);
    }

    /// <summary>Resolve once under StateLock before the first writer admission; no result is exposed yet.</summary>
    internal (int Error, GatherResolution? Resolution) ResolveGather(GameSession s, long collectionId, int extraAwards,
        long sceneEntry, int pointId)
    {
        int error = CheckGather(s, collectionId);
        if (error != 0) return (error, null);
        if (extraAwards is < 0 or > 100) return (ErrorCodes.Parameter, null);
        var row = _data.Row("LifeCollectionConfig", collectionId)!.Value;
        var beforeLife = s.LifeJob!.ToByteArray();
        var beforeRestrict = s.Restrictions!.ToByteArray();
        var beforeRuntime = JsonSerializer.SerializeToUtf8Bytes(s.LifeRuntime);
        var grants = new List<AwardGrant>();
        var mainGrants = new List<AwardGrant>();
        var next = s.LifeJob!.Clone(); var restrict = s.Restrictions!.Clone(); var runtime = Copy(s.LifeRuntime);
        try
        {
            // (inferred) Each eligible main/extra drop rolls independently in basis points; repeated pool groups remain intact.
            foreach (long id in Numbers(row, "_iDropId"))
                if (s.LifeJob!.LifeJobUnLockDrop.ContainsKey(id) && _data.Row("LifeDropConfig", id) is { } drop
                    && _functions.ConditionMet(s, GameData.Int(drop, "_iCondition")) && Random.Shared.Next(10000) < DropChance(s, drop))
                {
                    var output = _awards.Resolve(s, GameData.Int(drop, "_iMultiGroupIDId"), _seasons.PlayBook).Grants;
                    grants.AddRange(output);
                    if (GameData.Int(drop, "_iIsMain") == 1) mainGrants.AddRange(output);
                }
            ApplySkillRewards(s, next, row, false, grants, [], mainGrants, extraAwards);
            if (grants.Any(g => (g.Entry is 100 or 101) && g.BindType != 0))
                throw new AwardResolutionException("Virtual life collection EXP cannot carry item binding");
        }
        catch (AwardResolutionException e) { _log.LogWarning(e, "Life collection award {Collection}", collectionId); return (ErrorCodes.LifeConfig, null); }
        if (sceneEntry > 0 && pointId > 0)
        {
            if (!next.GeneralCollectionTimesByMap.TryGetValue(sceneEntry, out var map))
                next.GeneralCollectionTimesByMap[sceneEntry] = map = new GeneralCollectionTimesByMap();
            map.Times[pointId] = checked(map.Times.GetValueOrDefault(pointId) + 1);
        }
        SpendEnergy(next, restrict, runtime, RollFreeVigor(s, row, false) ? 0 : VigorCost(s, row, false));
        RecoverZeroVigor(s, restrict);
        AddExp(s, next, GameData.Int(row, "_iJobId"), OperationExp(s, row, false));
        Reconcile(next, restrict, runtime);
        NormalizeTitles(next);
        restrict.RestrictCount[VigorId].LimitCount = VigorLimit(s, next);
        return (0, new GatherResolution
        {
            RoleId = s.Role!.Id, BeforeLife = beforeLife,
            BeforeRestrict = beforeRestrict, BeforeRuntime = beforeRuntime, Life = next,
            Restrict = restrict, Runtime = runtime, Grants = grants.ToArray()
        });
    }

    /// <summary>Commit the frozen collection with its source claim; publish life state before economic notices.</summary>
    internal async Task<int> CompleteGatherAsync(GameSession s, GatherResolution resolved,
        RewardSettlement settlement, Action committed)
    {
        if (!resolved.Matches(s)) return ErrorCodes.Cooldown;
        return await CommitAsync(s, "gather-yield", resolved.Life, resolved.Restrict, resolved.Runtime, [], resolved.Grants,
            rewardSettlement: settlement, gatherCommitted: committed) != null ? 0 : ErrorCodes.ItemsShort;
    }

    /// <summary>
    /// Direct life-job completion retains the existing service contract. Each call admits a fresh
    /// server-side source; the world actor path supplies its stable pending-interaction key instead.
    /// </summary>
    public async Task<int> CompleteGatherAsync(GameSession s, long collectionId, int extraAwards = 0,
        long sceneEntry = 0, int pointId = 0)
    {
        var (error, resolved) = ResolveGather(s, collectionId, extraAwards, sceneEntry, pointId);
        if (error != 0) return error;
        string source = $"gather/direct/{RewardSourceId.ProcessRun}/{Guid.NewGuid():N}";
        return await CompleteGatherAsync(s, resolved!, new RewardSettlement(source, "gather-yield"), () => { });
    }

    private int RecipeError(GameSession s, long id)
    {
        var row = _data.Row("LifeFormulaConfig", id);
        int error = DefinitionError(s, row, "_iUnlockCondition");
        if (error != 0) return error;
        return !s.LifeJob!.LifeJobInfoMap[GameData.Int(row!.Value, "_iJobId")].BuildInfo.RecipeList.ContainsKey(id) ? ErrorCodes.LifeRecipe : 0;
    }

    /// <summary>33510 reserves a session-local, single-use production presentation (duration is milliseconds).</summary>
    public async Task ForgeTimeAsync(GameSession s, GetForgeCostTimeReq req)
    {
        using var gate = await CharacterService.Lock(s);
        int error = RecipeError(s, req.ForgeID), duration = 0;
        _forging.Remove(s);
        if (error == 0)
        {
            var row = _data.Row("LifeFormulaConfig", req.ForgeID)!.Value;
            var cost = Costs(row, "_kcost");
            error = Energy(s) < VigorCost(s, row, true) ? ErrorCodes.LifeVigor
                : cost.Any(c => _character.CountOf(s, c.Entry) < c.Count) ? _character.ShortageOf(s, cost) : 0;
            if (error == 0)
            {
                duration = OperationDuration(s, row, true);
                _forging[s] = new ForgeAttempt(req.ForgeID, _loop.Now, duration, s.WorldScene, GameData.Int(row, "_iJobId"));
            }
        }
        await s.SendGameAsync(ForgeTime, new GetForgeCostTimeResp { Result = error, CostTime = duration });
    }

    /// <summary>33504 item_unique_id is the recipe id (Life_ContentWidgetCtrl:1479), not an inventory GUID.</summary>
    public async Task ForgeAsync(GameSession s, StartForgeReq req)
    {
        using var gate = await CharacterService.Lock(s);
        var reply = new StartForgeResp();
        // (inferred) Bound batch work and expire a single-use presentation after thirty minutes.
        int error = req.ItemNum is < 1 or > 1000 ? ErrorCodes.Parameter : RecipeError(s, req.ItemUniqueId);
        if (error == 0) error = !_forging.TryGetValue(s, out var pending) || pending.Recipe != req.ItemUniqueId
            || pending.Scene != s.WorldScene || ActiveLevel(s, pending.Job) < 0 || s.Dead || s.SceneLoading
            || _loop.Now < pending.Started + TimeSpan.FromMilliseconds(pending.Duration)
            || _loop.Now > pending.Started + TimeSpan.FromMinutes(30) ? ErrorCodes.Parameter : 0;
        if (error == 0)
        {
            var row = _data.Row("LifeFormulaConfig", req.ItemUniqueId)!.Value;
            long vigor = (long)VigorCost(s, row, true) * req.ItemNum;
            var cost = Costs(row, "_kcost", req.ItemNum);
            error = Energy(s) < vigor ? ErrorCodes.LifeVigor : cost.Any(c => _character.CountOf(s, c.Entry) < c.Count) ? _character.ShortageOf(s, cost) : 0;
            if (error == 0)
            {
                var grants = new List<AwardGrant>();
                var outcomes = new Dictionary<long, List<AwardGrant>>();
                var actualCosts = new List<(int Entry, long Count)>();
                var next = s.LifeJob!.Clone(); var restrict = s.Restrictions!.Clone(); var runtime = Copy(s.LifeRuntime);
                try
                {
                    for (int n = 0; n < req.ItemNum; n++)
                    {
                        var tuple = WeightedOutput(row);
                        var output = _awards.Resolve(s, tuple.Group, _seasons.PlayBook).Grants.ToList();
                        var operationCosts = Costs(row, "_kcost");
                        ApplySkillRewards(s, next, row, true, output, operationCosts);
                        actualCosts.AddRange(operationCosts);
                        SpendEnergy(next, restrict, runtime, RollFreeVigor(s, row, true) ? 0 : VigorCost(s, row, true));
                        RecoverZeroVigor(s, restrict);
                        grants.AddRange(output);
                        if (!outcomes.TryGetValue(tuple.Type, out var list)) outcomes[tuple.Type] = list = [];
                        list.AddRange(output);
                    }
                    AddExp(s, next, GameData.Int(row, "_iJobId"), OperationExp(s, row, true) * req.ItemNum);
                    var received = await CommitAsync(s, "cd13c/life-forge", next, restrict, runtime,
                        actualCosts.GroupBy(c => c.Entry).Select(g => (g.Key, g.Sum(c => c.Count))).ToArray(), grants,
                        result: new
                        {
                            Recipe = req.ItemUniqueId, Batch = req.ItemNum,
                            OutputTypes = outcomes.Select(p => new
                            {
                                Type = p.Key,
                                Grants = p.Value.Select(g => new { g.Entry, g.Count, g.BindType }).ToArray()
                            }).ToArray()
                        });
                    if (received == null) error = _character.ShortageOf(s, cost);
                    else
                    {
                        _forging.Remove(s); reply.ForgeNum = req.ItemNum;
                        foreach (var (type, list) in outcomes)
                        {
                            var summary = new RepeatedItemBaseInfo();
                            foreach (var g in list.GroupBy(g => (g.Entry, g.BindType)))
                            {
                                var item = received.FirstOrDefault(i => i.Entry == g.Key.Entry && i.BindType == g.Key.BindType)
                                    ?? s.Items.FirstOrDefault(i => i.Entry == g.Key.Entry && i.Grid == 0);
                                var info = item == null ? new ItemBaseInfo { ItemEntry = g.Key.Entry } : _character.ItemInfoOf(item);
                                info.Stack = g.Sum(x => x.Count); summary.Items.Add(info);
                            }
                            reply.OutputItemMap[type] = summary;
                        }
                    }
                }
                catch (AwardResolutionException e) { _log.LogWarning(e, "Life formula award {Formula}", req.ItemUniqueId); error = ErrorCodes.LifeConfig; }
            }
        }
        reply.Result = error;
        await s.SendGameAsync(StartForge, reply);
    }

    private static (long Type, long Group) WeightedOutput(JsonElement formula)
    {
        var rows = formula.GetProperty("_iSuccessOutput").EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() >= 3).ToArray();
        long total = rows.Sum(r => (long)(GameData.Number(r[1]) ?? 0));
        if (total <= 0) throw new AwardResolutionException("LifeFormula has zero output weight");
        long roll = Random.Shared.NextInt64(total);
        foreach (var row in rows)
        {
            roll -= (long)(GameData.Number(row[1]) ?? 0);
            if (roll < 0) return ((long)(GameData.Number(row[0]) ?? 1), (long)(GameData.Number(row[2]) ?? 0));
        }
        throw new AwardResolutionException("LifeFormula output weights overflow");
    }

    private void SpendEnergy(DBLifeJobInfo next, DBRestrictInfo restrict, RuntimeState runtime, long amount)
    {
        Reconcile(next, restrict, runtime);
        var energy = restrict.RestrictCount[VigorId];
        energy.Count -= amount; energy.HistoryUseCount = checked(energy.HistoryUseCount + amount);
        runtime.EnergySpent = checked(runtime.EnergySpent + amount);
    }

    /// <summary>Current row's next threshold and current row's base-level gate (LC_LifeJobHelper.CheckJobLevelUpCondition).</summary>
    private void AddExp(GameSession s, DBLifeJobInfo next, long job, long gain)
    {
        var info = next.LifeJobInfoMap[job]; info.JobExp = checked(info.JobExp + gain);
        var rows = _data.Table("LifeJobLevelConfig").Values.Where(r => Current(r) && GameData.Int(r, "_iJobId") == job)
            .ToDictionary(r => GameData.Int(r, "_iLevel"));
        while (rows.TryGetValue(info.JobLevel, out var row) && rows.ContainsKey(info.JobLevel + 1)
            && GameData.Int(row, "_iNextNeedExp") is var need and > 0 && info.JobExp >= need
            && _functions.ConditionMet(s, GameData.Int(row, "_iCondition")))
        { info.JobExp -= need; info.JobLevel++; }
    }

    private async Task OnLevelChangedAsync(GameSession s)
    {
        if (s.LifeJob == null) return;
        var next = s.LifeJob.Clone();
        foreach (var job in next.LifeJobInfoMap.Keys) AddExp(s, next, job, 0);
        if (!next.Equals(s.LifeJob))
        {
            await CommitAsync(s, "cd13c/life-level-reconcile", next, s.Restrictions!.Clone(), Copy(s.LifeRuntime), [], []);
        }
    }

    private async Task<List<InvItem>?> CommitAsync(GameSession s, string kind, DBLifeJobInfo life, DBRestrictInfo restrict,
        RuntimeState runtime, IReadOnlyList<(int Entry, long Count)> cost, IReadOnlyList<AwardGrant> grants,
        IReadOnlyDictionary<long, int>? instances = null, RewardSettlement? rewardSettlement = null,
        Action? gatherCommitted = null, object? result = null)
    {
        if (grants.Any(g => g.ExpireTimeStamp != 0)) throw new AwardResolutionException("Life award requires expiring grant support");
        if (rewardSettlement == null)
        {
            Reconcile(life, restrict, runtime);
            NormalizeTitles(life);
            restrict.RestrictCount[VigorId].LimitCount = VigorLimit(s, life);
        }
        long baseExp = rewardSettlement == null ? 0 : grants.Where(g => g.Entry == 100).Sum(g => (long)g.Count);
        long jobExp = rewardSettlement == null ? 0 : grants.Where(g => g.Entry == 101).Sum(g => (long)g.Count);
        IEnumerable<AwardGrant> itemGrants = rewardSettlement == null ? grants :
            grants.Where(g => g.Entry is not (100 or 101));
        var semanticCost = cost.Concat((instances ?? new Dictionary<long, int>()).Select(pair =>
            (s.Items.Single(item => item.Guid == pair.Key).Entry, (long)pair.Value))).ToArray();
        var goods = itemGrants.Select(g => (g.Entry, g.Count)).ToArray();
        var received = await _character.ExchangeLockedAsync(s, cost, [], instances, popup: false, sync: false,
            persist: tx =>
            {
                tx.RoleBlob(s.Role!.Id, "lifejob", life.ToByteArray());
                tx.RoleBlob(s.Role.Id, "life_restrict", restrict.ToByteArray());
                tx.RoleBlob(s.Role.Id, "life_runtime", JsonSerializer.SerializeToUtf8Bytes(runtime));
                if (rewardSettlement == null)
                    tx.WithEconomy(_character.ProgressionEconomy(s, kind, semanticCost, goods,
                        new
                        {
                            LifeHash = Convert.ToHexString(SHA256.HashData(life.ToByteArray())),
                            RestrictHash = Convert.ToHexString(SHA256.HashData(restrict.ToByteArray())),
                            RuntimeHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(runtime))),
                            Vigor = restrict.RestrictCount[VigorId].Count,
                            VigorUses = restrict.RestrictCount[VigorUsesId].Count,
                            runtime.EnergySpent, runtime.EnergyDay, runtime.PotionDay, runtime.FoodBook,
                            Grants = grants.Select(g => new { g.Entry, g.Count, g.BindType }).ToArray(),
                            Result = result
                        }));
            },
            boundGoods: itemGrants.Select(g => (g.Entry, g.Count, g.BindType)).ToArray(),
            baseExp: baseExp, jobExp: jobExp,
            rewardSettlement: rewardSettlement,
            committed: () =>
            {
                s.LifeJob = life; s.Restrictions = restrict; s.LifeRuntime = runtime;
                gatherCommitted?.Invoke();
            }, asyncCommit: true);
        if (received == null) return null;
        await _character.ItemsChangedAsync(s, stats: false);
        await SyncAsync(s);
        await HealthAsync(s);
        await _functions.SyncAsync(s);
        return received;
    }

    public Task CancelAsync(GameSession s) { _forging.Remove(s); return Task.CompletedTask; }
}
