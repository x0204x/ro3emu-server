using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Growth Classroom (Become Stronger, function 48; UI_Sys_BecomeStronger_GrowthClassroomPanel). Its quests are
/// TaskService.GrowthClass.cs; this is its 5045 AUTO_SYNC DBGrowthClassInfo and the rank rewards (33830).
/// <list type="bullet">
/// <item>5045 (LC_DAL_GrowthClassroom._onAutoSyncAllDBGrowthClassInfo): moduleTaskCompleteNum = the module's delivered
/// quests ("%d/%d" with its quest count), moduleUnlockedStage = its open stage (the rows past it are locked, the rank
/// tabs past it too), rankProgress = all delivered growth quests (fOverallCompletion; GetNeedTaskCountToNextRank names it
/// a task count), rankReward[rank] = 1 once claimed. moduleProgress is never read by the client and stays empty.</item>
/// <item>Ranks (BecomeStrongerRankConfig in id order): a rank is reached when rankProgress covers the sum of _iStandard
/// up to and including it (rank 1 has 0), and its reward is then claimable once
/// (LC_BecomeStrongerHelper.GetNextClaimableRankReward / GetRankRewardStateByRankId).</item>
/// <item>33830 {rankId} (SendBecomeStrongerReward from the rank preview and the reward tip): the reward is the
/// MultiverseGroup _iReward (the preview's GetSeasonAwardList; see FixedAward for ranks 2, 5 and 6). The grant and
/// rankReward commit as one cd13e/growthclass-award receipt; the client has no handler for the reply and reads the claim
/// from 5045.</item>
/// </list>
/// Refusals (inferred mapping): function closed 2057, unknown rank 2061, already claimed 2062, not reached 3900.
/// Only rankReward is stored (role blob "growth_class"); the rest is derived from the quests.
/// </summary>
public sealed class GrowthClassService
{
    public const uint Sync = 5045, GetRankReward = 33830;

    private readonly GameData _data;
    private readonly TaskService _tasks;
    private readonly CharacterService _character;
    private readonly AwardService _awards;
    private readonly FunctionService _functions;
    private readonly RoleStore _roles;
    private readonly ILogger<GrowthClassService> _log;
    private List<(int Id, long Standard, long Reward)>? _ranks;

    public GrowthClassService(GameData data, TaskService tasks, CharacterService character, AwardService awards,
        FunctionService functions, RoleStore roles, GmCommands gm, ILogger<GrowthClassService> log)
    {
        this._data = data; this._tasks = tasks; this._character = character; this._awards = awards;
        this._functions = functions; this._roles = roles; this._log = log;
        tasks.SyncGrowthClass = SyncAsync;
        gm.Register("growthclass", "done <module> <stage> | info (Growth Classroom test aid: deliver a module's quests through a stage)", GmAsync);
    }

    /// <summary>BecomeStrongerRankConfig rows in id order: (rank, _iStandard, _iReward).</summary>
    private List<(int Id, long Standard, long Reward)> Ranks() => _ranks ??= _data.Table("BecomeStrongerRankConfig").Values
        .SelectMany(v => v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : [v])
        .Where(r => r.ValueKind == JsonValueKind.Object && GameData.Int(r, "_iID") > 0)
        .Select(r => ((int)GameData.Int(r, "_iID"), GameData.Int(r, "_iStandard"), GameData.Int(r, "_iReward")))
        .OrderBy(r => r.Item1).ToList();

    public void Load(GameSession s)
    {
        var bytes = _roles.LoadGrowthClass(s.Role!.Id);
        s.GrowthClass = bytes is { Length: > 0 } ? DBGrowthClassInfo.Parser.ParseFrom(bytes) : new DBGrowthClassInfo();
    }

    public Task LoginAsync(GameSession s) => SyncAsync(s);

    private Task SyncAsync(GameSession s) => s.GrowthClass == null || s.Tasks == null ? Task.CompletedTask
        : CharacterService.SyncAsync(s, Sync, Build(s));

    /// <summary>The 5045 view: the stored claims and what the delivered growth quests say.</summary>
    private DBGrowthClassInfo Build(GameSession s)
    {
        var info = new DBGrowthClassInfo();
        foreach (var (rank, claimed) in s.GrowthClass!.RankReward) info.RankReward[rank] = claimed;
        int total = 0;
        foreach (var module in _tasks.GrowthClassTasks().Values.Select(t => t.Module).Distinct().Order())
        {
            int done = _tasks.GrowthClassTasks().Count(t => t.Value.Module == module && TaskService.IsDelivered(s, t.Key));
            info.ModuleTaskCompleteNum[(int)module] = done;
            info.ModuleUnlockedStage[(int)module] = _tasks.GrowthStage(s, module);
            total += done;
        }
        info.RankProgress = total;
        return info;
    }

    /// <summary>The growth quests delivered so far (rankProgress).</summary>
    private int Progress(GameSession s) => _tasks.GrowthClassTasks().Keys.Count(id => TaskService.IsDelivered(s, id));

    /// <summary>33830: one reached rank's reward, once.</summary>
    public async Task ClaimAsync(GameSession s, GrowthClassGetRankRewardReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            int index = Ranks().FindIndex(r => r.Id == req.RankId);
            result = s.GrowthClass == null || s.Tasks == null ? ErrorCodes.Unknown
                : !_functions.IsOpen(s, TaskService.BecomeStrongerFunction) ? ErrorCodes.FunctionLocked
                : index < 0 ? ErrorCodes.RewardMissing
                : s.GrowthClass.RankReward.GetValueOrDefault(req.RankId) == 1 ? ErrorCodes.RewardClaimed
                : Ranks().Take(index + 1).Sum(r => r.Standard) > Progress(s) ? ErrorCodes.NoRankReward
                : await CommitClaimAsync(s, req.RankId, Ranks()[index].Reward);
        }
        await SyncAsync(s);
        await s.SendGameAsync(GetRankReward, new GrowthClassGetRankRewardRsp { Result = result, RankId = req.RankId });
    }

    private async Task<int> CommitClaimAsync(GameSession s, int rank, long group)
    {
        long role = s.Role!.Id;
        var next = s.GrowthClass!.Clone();
        next.RankReward[rank] = 1;
        byte[] bytes = next.ToByteArray();
        int? playBook = s.Synced.GetValueOrDefault(SeasonService.SeasonSync) is DBSeasonInfo { Current: { } current }
            ? (int)current.PlayBookId : null;
        AwardPlan plan;
        try { plan = _awards.Resolve(s, group, playBook); }
        catch (AwardResolutionException e) when (FixedAward(group, playBook) is { } items)
        {
            _log.LogInformation("Growth Classroom rank {Rank} pays the fixed items of its award: {Reason}", rank, e.Message);
            plan = items;
        }
        catch (AwardResolutionException e)
        {
            _log.LogWarning("Growth Classroom rank {Rank} reward unresolved for {Role}: {Reason}", rank, role, e.Message);
            return ErrorCodes.RewardMissing;
        }
        try
        {
            bool ok;
            if (plan.Grants.Count == 0)
            {
                _roles.SaveGrowthClass(role, bytes);
                s.GrowthClass = next;
                ok = true;
            }
            else ok = await _character.ApplyAwardAsync(s, plan, persist: tx => _roles.SaveGrowthClass(role, bytes, tx),
                stateLockHeld: true, committed: () => s.GrowthClass = next,
                ownedAward: new CharacterService.OwnedAwardSource("cd13e/growthclass-award", $"growthclass/{role}/{rank}",
                    new { Version = 1, Rank = rank, Award = group, plan.AwardId }));
            if (ok) _log.LogInformation("{Name} claims Growth Classroom rank {Rank} reward", s.Role.Name, rank);
            return ok ? 0 : ErrorCodes.Unknown;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "Growth Classroom rank {Rank} reward failed for {Role}", rank, role);
            return ErrorCodes.Unknown;
        }
    }

    /// <summary>
    /// (inferred) AwardService refuses ranks 2 and 6 (AwardConfig 1001002 has _kCondition [[2,11,99]], no recovered
    /// evaluator) and 5 (1001005 has _kEXP [1,10000]). The reached rank is this claim's eligibility, so such a rank
    /// pays its row's fixed _kAward items, all that the rank preview shows (GetSeasonAwardList); the EXP is not paid.
    /// </summary>
    private AwardPlan? FixedAward(long group, int? playBook)
    {
        if (_data.Row("MultiverseGroupConfig", group) is not { } mg || GameData.Int(mg, "_itype") != 0) return null;
        long id = playBook is > 0 && GameData.Int(mg, $"_iValue_{playBook}") is var selected and > 0
            ? selected : GameData.Int(mg, "_iValue_default");
        if (_data.Row("AwardConfig", id) is not { ValueKind: JsonValueKind.Array } rows || rows.GetArrayLength() != 1
            || GameData.Int(rows[0], "_iDropPool") != 0
            || !rows[0].TryGetProperty("_kAward", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var grants = new List<AwardGrant>();
        foreach (var t in items.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() != 3 || !t[0].TryGetInt32(out int entry)
                || !t[1].TryGetInt32(out int count) || !t[2].TryGetInt32(out int bind) || bind is < 0 or > 1
                || count <= 0 || _data.Row("ItemConfig", entry) == null) return null;
            grants.Add(new AwardGrant(entry, count, bind));
        }
        return grants.Count > 0 ? new AwardPlan(group, id, grants) : null;
    }

    private async Task GmAsync(GameSession s, string[] args)
    {
        string reply;
        if (s.GrowthClass == null || s.Tasks == null) reply = "No Growth Classroom state.";
        else if (args is ["done", var moduleText, var stageText] && long.TryParse(moduleText, out long module)
            && int.TryParse(stageText, out int stage))
        {
            var ids = _tasks.GrowthClassTasks().Where(t => t.Value.Module == module && t.Value.Stage <= stage)
                .Select(t => t.Key).Order().ToList();
            foreach (long id in ids.Where(id => !TaskService.IsDelivered(s, id))) await _tasks.GmAsync(s, "done", id);
            reply = $"Module {module} through stage {stage}: {ids.Count} quests delivered. {Describe(s)}";
        }
        else if (args is ["info"]) reply = Describe(s);
        else reply = "Usage: !growthclass done <module> <stage> | info";
        await GmCommands.ReplyAsync(s, reply);
    }

    private string Describe(GameSession s)
    {
        var info = Build(s);
        return $"rankProgress {info.RankProgress}; stages " +
            string.Join(", ", info.ModuleUnlockedStage.Select(m => $"{m.Key}:{m.Value} ({info.ModuleTaskCompleteNum[m.Key]} done)")) +
            $"; claimed [{string.Join(',', info.RankReward.Keys.Order())}]";
    }
}
