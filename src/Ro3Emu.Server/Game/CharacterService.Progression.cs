using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    // ---- level, exp, stat points ----

    private int MaxJobLevel(int career) => (int)(JobLvRow(career, 1) is { } row ? GameData.Int(row, "_iMaxJobLv") : 10);

    private int JobRank(int career) => (int)(data.Row("JobConfig", career) is { } job ? Math.Max(1, GameData.Int(job, "_iJobRank")) : 1);

    private JsonElement? JobLvRow(int career, int jobLevel) => data.Row("JobLvConfig", JobRank(career) * 100L + jobLevel);

    private long BaseExpNeed(int level) => data.Row("BaseLvConfig", level) is { } row ? GameData.Int(row, "_iBaseExp") : 0;

    private long JobExpNeed(int career, int jobLevel) => JobLvRow(career, jobLevel) is { } row ? GameData.Int(row, "_iJobeExp") : 0;

    /// <summary>All stat points a role has earned: the starting 48, BaseLvConfig._iStatusPoint per level reached, GM extras.</summary>
    public int TotalPoints(RoleProgress p) =>
        StartingStatPoints + p.BonusPoints + Enumerable.Range(2, Math.Max(0, p.Level - 1))
            .Sum(lv => (int)(data.Row("BaseLvConfig", lv) is { } row ? GameData.Int(row, "_iStatusPoint") : 0));

    /// <summary>Points spent to raise a stat from 1 to <paramref name="value"/> (BasePointConfig: cost of reaching each value).</summary>
    public int StatCost(int value) =>
        Enumerable.Range(2, Math.Max(0, value - 1)).Sum(v => (int)(data.Row("BasePointConfig", v) is { } row ? GameData.Int(row, "_iStatusPoint") : 0));

    private int UsedPoints(RoleProgress p) => p.Stats.Sum(StatCost);

    /// <summary>Stat points spent on the six stats (quests: 104013 CheckCostBasePoint).</summary>
    public int SpentStatPoints(RoleProgress p) => UsedPoints(p);

    private PlanBaseProperty CurrentPlan(RoleProgress p, PlanBaseProperty? saved = null)
    {
        int used = UsedPoints(p);
        var plan = saved?.Clone() ?? new PlanBaseProperty();
        plan.AttrPoints = Math.Max(0, TotalPoints(p) - used);
        plan.UsedAttrPoints = used;
        for (int i = 0; i < 6; i++) plan.BaseProperty[i + 1] = p.Stats[i];
        return plan;
    }

    private Task SendStatPlanAsync(GameSession session) => SyncAsync(session, BaseAttrSync, BuildAttr(session));

    /// <summary>Sets base and job level (exp inside the level restarts at 0); a higher level shows the level-up popup.</summary>
    public async Task<string> SetLevelAsync(GameSession session, int level, int? jobLevel)
    {
        using var _ = await LockAsync(session);
        var p = CloneProgress(session.Progress!);
        p.Level = Math.Clamp(level, 1, MaxLevel);
        p.Exp = 0;
        if (jobLevel is { } jl)
        {
            p.JobLevel = Math.Clamp(jl, 1, MaxJobLevel(p.Career));
            p.JobExp = 0;
        }
        SkipFreeJobLevels(p);
        await CommitCharacterAsync(session, "gm-level", progress: p);
        await AfterLevelChangeAsync(session, persist: false);
        return $"Base level {p.Level}, job level {p.JobLevel}.";
    }

    /// <summary>Adds exp, levelling up through BaseLvConfig / JobLvConfig as needed.
    /// A kill reward may pass its role-owner check so a queued StateLock cannot credit a later role.</summary>
    public async Task<string> AddExpAsync(GameSession session, long baseExp, long jobExp, Func<bool>? stillOwner = null)
    {
        if (stillOwner?.Invoke() == false) return string.Empty;
        using var held = await LockIntentAsync(session, stillOwner);
        if (held == null) return string.Empty;
        if (stillOwner?.Invoke() == false) return string.Empty;
        var p = session.Progress!;
        int oldLevel = p.Level, oldJob = p.JobLevel;
        // BaseExpAddConfig catch-up bonus below the server level (CharacterService.LevelRules).
        baseExp = WithPursueBonus(p.Level, Math.Max(0, baseExp));
        var staged = CloneProgress(p);
        ApplyExp(staged, baseExp, Math.Max(0, jobExp));
        var builder = roles.NewTransaction();
        builder.ItemDelta(session.Role!.Id, session.Items, session.Items);
        builder.Progress(session.Role!.Id, staged);
        var expSource = new RewardSettlement($"exp/{session.Role.Id}/{Guid.NewGuid():N}", "cd13e/character-exp");
        builder.WithEconomy(RewardMutation(session, expSource, p, staged, [], [],
            Math.Max(0, baseExp), Math.Max(0, jobExp), new ItemAutoDecomposeTipsNtf()));
        await roles.CommitAsync(builder, "character/exp", _ => CopyProgress(p, staged));
        if (stillOwner?.Invoke() == false || session.Attachment?.Cancelled == true) return string.Empty;
        var rewards = new Romsg.GetRewardNotify { TargetUid = session.Role!.Id };
        if (baseExp > 0) rewards.Rewards.Add(ItemInfo(new InvItem { Entry = BaseExpItem, Stack = (int)Math.Min(baseExp, int.MaxValue) }));
        if (jobExp > 0) rewards.Rewards.Add(ItemInfo(new InvItem { Entry = JobExpItem, Stack = (int)Math.Min(jobExp, int.MaxValue) }));
        rewards.RewardType.Add(1); // ManifestType Float: "+N Base EXP"
        if (rewards.Rewards.Count > 0) await session.SendGameAsync(GetRewardNotify, rewards);
        if (stillOwner?.Invoke() == false) return string.Empty;

        if (p.Level != oldLevel || p.JobLevel != oldJob)
        {
            await AfterLevelChangeAsync(session, stillOwner, persist: false);
        }
        else
        {
            await SyncAsync(session, RoleBaseInfoSync, BuildBaseInfo(session));
            if (stillOwner?.Invoke() == false) return string.Empty;
            foreach (var handler in StateChanged) handler(session);
        }
        return $"Base {p.Level} ({p.Exp}/{BaseExpNeed(p.Level)}), job {p.JobLevel} ({p.JobExp}/{JobExpNeed(p.Career, p.JobLevel)}).";
    }

    /// <summary>
    /// JobLvConfig rows needing 0 exp below the rank's max (first jobs: levels 1-14) are passed through, as the
    /// character window does (it shows job 5 of a Swordman as 0/2246, the need of level 15).
    /// </summary>
    private void SkipFreeJobLevels(RoleProgress p)
    {
        int maxJob = MaxJobLevel(p.Career);
        while (p.JobLevel < maxJob && JobLvRow(p.Career, p.JobLevel) is { } row && GameData.Int(row, "_iJobeExp") == 0) p.JobLevel++;
    }

    private async Task AfterLevelChangeAsync(GameSession session, Func<bool>? stillOwner = null, bool persist = true)
    {
        var p = session.Progress!;
        if (persist) Save(session);
        await SyncAsync(session, RoleBaseInfoSync, BuildBaseInfo(session));
        if (stillOwner?.Invoke() == false) return;
        await SendStatPlanAsync(session);
        if (stillOwner?.Invoke() == false) return;
        await SendStatsAsync(session);
        if (stillOwner?.Invoke() == false) return;
        foreach (var handler in ProgressHandlers)
        {
            await handler(session);
            if (stillOwner?.Invoke() == false) return;
        }
        foreach (var handler in StateChanged) handler(session);
    }

    public async Task<string> AddStatPointsAsync(GameSession session, int points)
    {
        using var _ = await LockAsync(session);
        var p = CloneProgress(session.Progress!);
        p.BonusPoints = Math.Max(-StartingStatPoints, p.BonusPoints + points);
        await CommitCharacterAsync(session, "gm-stat-points", progress: p);
        await SendStatPlanAsync(session);
        foreach (var handler in StateChanged) handler(session);
        return $"{TotalPoints(p) - UsedPoints(p)} free stat points.";
    }

    /// <summary>31500: six absolute values in one saved plan; the active plan also changes combat stats.</summary>
    public async Task AllocateAsync(GameSession session, AllocateAttrPointReq req)
    {
        using var _ = await LockAsync(session);
        var p = CloneProgress(session.Progress!);
        var plans = session.StatPlans.Info.Clone();
        if (!plans.AllPlanBaseProperty.TryGetValue(req.PlanIndex, out var selected))
        {
            await session.SendGameAsync(AllocateAttrPoints, new AllocateAttrPointRsp { Result = ErrorCodes.Parameter, PlanIndex = req.PlanIndex });
            return;
        }
        var previous = Enumerable.Range(1, 6).Select(id => selected.BaseProperty.GetValueOrDefault(id, 1)).ToArray();
        var wanted = (int[])previous.Clone();
        foreach (var (stat, value) in req.Points)
            if (stat is >= 1 and <= 6) wanted[stat - 1] = value;
        bool ok = req.Points.Keys.All(k => k is >= 1 and <= 6)
            && wanted.Select((v, i) => v >= previous[i] && v <= MaxStat).All(x => x) && wanted.Sum(StatCost) <= TotalPoints(p);
        if (ok)
        {
            for (int i = 0; i < 6; i++) selected.BaseProperty[i + 1] = wanted[i];
            selected.UsedAttrPoints = wanted.Sum(StatCost);
            selected.AttrPoints = TotalPoints(p) - selected.UsedAttrPoints;
            if (req.PlanIndex == plans.CurPlanIndex) wanted.CopyTo(p.Stats, 0);
        }
        log.LogInformation("{Name} allocates stats {Stats}: {Result}", session.Role!.Name, string.Join(',', wanted), ok ? "ok" : "refused");
        int code = ok ? 0 : wanted.Sum(StatCost) > TotalPoints(p) ? ErrorCodes.StatPointsShort : ErrorCodes.Parameter;
        if (ok)
        {
            plans.AllPlanBaseProperty[plans.CurPlanIndex] = CurrentPlan(p, plans.AllPlanBaseProperty[plans.CurPlanIndex]);
            await CommitStatsAsync(session, "stat-allocate", p, plans);
        }
        await session.SendGameAsync(AllocateAttrPoints, new AllocateAttrPointRsp { Result = code, PlanIndex = req.PlanIndex, PlanBaseProperty = selected.Clone() });
        if (!ok) return;
        await SendStatPlanAsync(session);
        if (req.PlanIndex == plans.CurPlanIndex) await SendStatsAsync(session, showChange: true);
        foreach (var handler in ProgressHandlers) await handler(session);
        foreach (var handler in StateChanged) handler(session);
    }

    /// <summary>31501: reset the addressed plan; if selected, also refresh the role's effective stats.</summary>
    public async Task ResetStatsAsync(GameSession session, ResetAttrPointReq? req = null)
    {
        using var _ = await LockAsync(session);
        var p = CloneProgress(session.Progress!);
        var plans = session.StatPlans.Info.Clone();
        int index = req?.PlanIndex ?? plans.CurPlanIndex;
        if (!plans.AllPlanBaseProperty.TryGetValue(index, out var plan))
        {
            await session.SendGameAsync(ResetAttrPoints, new ResetAttrPointResp { Result = ErrorCodes.Parameter, PlanIndex = index });
            return;
        }
        for (int i = 1; i <= 6; i++) plan.BaseProperty[i] = 1;
        plan.AttrPoints = TotalPoints(p);
        plan.UsedAttrPoints = 0;
        if (index == plans.CurPlanIndex) Array.Fill(p.Stats, 1);
        plans.AllPlanBaseProperty[plans.CurPlanIndex] = CurrentPlan(p, plans.AllPlanBaseProperty[plans.CurPlanIndex]);
        await CommitStatsAsync(session, "stat-reset", p, plans);
        await session.SendGameAsync(ResetAttrPoints, new ResetAttrPointResp { Result = 0, PlanIndex = index, PlanBaseProperty = plan.Clone() });
        await SendStatPlanAsync(session);
        if (index == plans.CurPlanIndex) await SendStatsAsync(session, showChange: true);
        foreach (var handler in ProgressHandlers) await handler(session);
        foreach (var handler in StateChanged) handler(session);
    }

    /// <summary>
    /// PublicParamConfig.TransformJobLevelLimit (Config/ComplexConfig/PublicParamConfig.lua:1845-1879): Base / Job level
    /// needed to advance to a job of that rank. The change-job panel checks the same table before it sends 31176.
    /// </summary>
    private static readonly Dictionary<int, (int Base, int Job)> TransformJobLevelLimit = new()
    {
        [2] = (10, 10), [3] = (30, 40), [4] = (40, 40), [5] = (50, 40),
    };

    // ErrorCodeConfig (the mapping of these texts to 31176 is inferred).
    private const int ErrJobNotExist = 2051, ErrJobCannotAdvance = 2054, ErrJobBaseLevel = 2058, ErrJobJobLevel = 2059;

    /// <summary>
    /// 31176 PLAYER_TRANSFORM_JOB, sent by the change-job panel (first jobs: at the end of cutscene 130011). The reply
    /// must come before 5066 and the 31049 model change: the client arms its success effect on the reply and plays it
    /// when the new model has loaded (LC_System_PlayerInfo._onChangeModelOver).
    /// </summary>
    public async Task TransformJobAsync(GameSession session, PlayerTransformJobReq req)
    {
        int error;
        using (await LockAsync(session))
        {
            error = TransformJobError(session, req.JobId);
            log.LogInformation("{Name} job change {From} -> {To}: {Result}", session.Role!.Name, session.Progress!.Career, req.JobId, error == 0 ? "ok" : error.ToString());
            if (error == 0) await CommitJobAsync(session, req.JobId, 1);
            await session.SendGameAsync(PlayerTransformJob, new PlayerTransformJobResp { Result = error, JobId = req.JobId });
            if (error != 0) return;
            await PublishJobAsync(session);
        }
        foreach (var handler in ProgressHandlers) await handler(session);
        foreach (var handler in JobChanged) await handler(session);
    }

    /// <summary>Called after a job change (quests: CheckJobChange / CheckJobRank targets).</summary>
    public List<Func<GameSession, Task>> JobChanged { get; } = new();

    private int TransformJobError(GameSession session, int jobId)
    {
        var p = session.Progress!;
        if (data.Row("JobConfig", jobId) is not { } job) return ErrJobNotExist;
        if (GameData.Int(job, "_iParentId") != p.Career || GameData.Int(job, "_iNotTransformJob") != 1) return ErrJobCannotAdvance;
        long gender = GameData.Int(job, "_iGenderLimit"); // enum_GenderType.All = 0
        if (gender != 0 && session.Role!.Sex != 0 && session.Role.Sex != gender) return ErrJobCannotAdvance;
        if (TransformJobLevelLimit.TryGetValue((int)GameData.Int(job, "_iJobRank"), out var limit))
        {
            if (p.Level < limit.Base) return ErrJobBaseLevel;
            if (p.JobLevel < limit.Job) return ErrJobJobLevel;
        }
        return 0;
    }

    public const uint PlayerTransformJob = 31176;

    /// <summary>Job change (GM): career in 5066 and the model through a Base entity change.</summary>
    public async Task<string> SetJobAsync(GameSession session, int career)
    {
        if (data.Row("JobConfig", career) == null) throw new ArgumentException("unknown job");
        using (await LockAsync(session))
        {
            await CommitJobAsync(session, career, Math.Min(session.Progress!.JobLevel, MaxJobLevel(career)));
            // The 31176 reply remains before the base/model publication, after the durable outcome.
            await session.SendGameAsync(PlayerTransformJob, new PlayerTransformJobResp { Result = 0, JobId = career });
            await PublishJobAsync(session);
        }
        foreach (var handler in ProgressHandlers) await handler(session);
        foreach (var handler in JobChanged) await handler(session);
        return $"Job {career} {data.TextOf(data.Row("JobConfig", career)!.Value, "_iName")}".TrimEnd();
    }

    /// <summary>Career, job level, speed; then 5066, the 31049 model change and the stats (caller holds the lock).</summary>
    private async Task CommitJobAsync(GameSession session, int career, int jobLevel)
    {
        var progress = CloneProgress(session.Progress!);
        progress.Career = career;
        progress.JobLevel = jobLevel;
        progress.JobExp = 0;
        SkipFreeJobLevels(progress);
        await CommitCharacterAsync(session, "job", progress: progress, apply: () =>
        {
            session.Role = session.Role! with { Career = career };
            session.Speed = data.JobAttributes(career).GetValueOrDefault(Speed, RoleSessionService.DefaultSpeed);
        });
    }

    private async Task PublishJobAsync(GameSession session)
    {
        Refresh(session);
        await SyncAsync(session, RoleBaseInfoSync, BuildBaseInfo(session));
        var entity = RoleSessionService.PlayerEntity(session);
        entity.NotifyType = EntityBase.Types.NotifyType.Base;
        await ToSelfAndViewersAsync(session, EntityBaseChange, entity);
        await SendStatsAsync(session);
    }

    /// <summary>
    /// GM: sets the speed attribute for this session (0 = back to normal). The client plays the run animation from
    /// PublicParamConfig.RunBaseSpeed (875) up, the walk animation below.
    /// </summary>
    public async Task<string> SetSpeedAsync(GameSession session, long speed)
    {
        using var _ = await LockAsync(session);
        session.SpeedBonus = 0;
        if (speed > 0) session.SpeedBonus = speed - Stats(session)[Speed];
        await SendStatsAsync(session);
        return $"Speed {session.Speed} ({session.Speed / 100.0:F2} m/s); run animation from 875.";
    }
}
