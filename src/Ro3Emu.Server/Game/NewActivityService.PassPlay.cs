using System.Globalization;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Where pass EXP comes from (this server's design). The client data grants no currency 132 at all, so the original
/// server's sources are unknown (inferred): three daily challenges of 300 and seven weekly ones of 1000, daily
/// activity points, monsters, dungeons, MVP fights and story chapters. +20% in a party, double while the player is
/// behind the season pace, and past the last level a bonus chest every 2000. The novice pass 9201 (currency 920132)
/// levels from main story quests. Players read their challenges with /pass and /pass week.
/// </summary>
public sealed partial class NewActivityService
{
    internal const int PassExpCurrency = 132, NovicePassExpCurrency = 920132, Liveness = 5;
    private const long PassDailyReward = 300, PassWeeklyReward = 1000;
    private const long KillExp = 3, KillExpDailyCap = 300, DungeonExp = 200, DungeonsPaidPerDay = 5, MvpExp = 200,
        MvpsPaidPerDay = 3, ChapterExp = 500, LivenessExp = 6, NoviceQuestExp = 300, OverflowChestExp = 2000;
    /// <summary>60 levels of 2000 in about six and a half weeks of regular play.</summary>
    private const double PacePerDay = 120_000 / 45.5;

    internal enum PassGoal { Kills, PartyKills, Liveness, Dungeons, Mvps, Quests, LoginDays }

    internal sealed record PassChallenge(string Id, string Text, PassGoal Goal, long Target);

    /// <summary>Three of these a day, the same three for a role all day.</summary>
    internal static readonly PassChallenge[] DailyChallenges =
    [
        new("d.kills", "Defeat 100 monsters", PassGoal.Kills, 100),
        new("d.party", "Defeat 50 monsters in a party", PassGoal.PartyKills, 50),
        new("d.activity", "Reach 60 daily activity", PassGoal.Liveness, 60),
        new("d.dungeon", "Clear a dungeon", PassGoal.Dungeons, 1),
        new("d.quests", "Complete 3 quests", PassGoal.Quests, 3),
    ];

    internal static readonly PassChallenge[] WeeklyChallenges =
    [
        new("w.kills", "Defeat 1000 monsters", PassGoal.Kills, 1000),
        new("w.party", "Defeat 300 monsters in a party", PassGoal.PartyKills, 300),
        new("w.activity", "Earn 300 daily activity", PassGoal.Liveness, 300),
        new("w.dungeons", "Clear 5 dungeons", PassGoal.Dungeons, 5),
        new("w.mvps", "Take part in 3 MVP fights", PassGoal.Mvps, 3),
        new("w.quests", "Complete 20 quests", PassGoal.Quests, 20),
        new("w.days", "Play on 5 days", PassGoal.LoginDays, 5),
    ];

    /// <summary>
    /// Task ids of the challenges in the client's season task panel (TaskConfig type 13). The client data has no such
    /// rows; Ro3Emu.ClientHooks.PassChallenges adds them with these ids, so both sides must keep them equal.
    /// </summary>
    internal const long DailyTaskBase = 99_130_001, WeeklyTaskBase = 99_130_101;

    internal static long TaskIdOf(PassChallenge c) => Array.IndexOf(DailyChallenges, c) is var d and >= 0
        ? DailyTaskBase + d : WeeklyTaskBase + Array.IndexOf(WeeklyChallenges, c);

    /// <summary>Today's three and the week's seven, as 5027 task rows: running with progress, or delivered when done.</summary>
    private IEnumerable<Romsg.TaskInfo> PassTasks(GameSession session)
    {
        if (!session.PassChallengeRows || session.ActivityRuntime?.PassPlay is not { } play || session.Role == null
            || OpenPass(session) == null) return [];
        var (day, week) = PassPeriod(_clock.GetLocalNow());
        if (play.Day != day || play.Week != week)
        {
            play = JsonClone(play);
            Roll(play, session.Role.Id, day, week);
        }
        static Romsg.TaskInfo Row(PassChallenge c, Dictionary<string, long> progress, List<string> done) => new()
        {
            TaskId = TaskIdOf(c),
            Status = done.Contains(c.Id) ? Romsg.TaskState.Delivery : Romsg.TaskState.Running,
            CompleteNum = { Math.Min(c.Target, progress.GetValueOrDefault(c.Id)) },
        };
        return DailyChallenges.Where(c => play.Daily.Contains(c.Id)).Select(c => Row(c, play.DailyProgress, play.DailyDone))
            .Concat(WeeklyChallenges.Select(c => Row(c, play.WeeklyProgress, play.WeeklyDone))).ToArray();
    }

    /// <summary>Ro3Emu.ClientHooks.PassChallenges sends it before opening the panel: its TaskConfig has the rows.</summary>
    public const uint PassPanelMsg = 77014;

    /// <summary>77014: from now on this session's 5027 carries the challenges (a client without the rows never asks).</summary>
    public Task PassPanelAsync(GameSession session)
    {
        if (!_passPlay || session.Role == null || session.Tasks == null) return Task.CompletedTask;
        if (!session.PassChallengeRows) _log.LogInformation("Pass challenge rows on for role {Role}", session.Role.Id);
        session.PassChallengeRows = true;
        return session.DeferTaskSync ? Task.CompletedTask : _tasks.SyncTasksAsync(session);
    }

    private long[]? _chapterEnds;
    /// <summary>The last main quest of each story chapter (TaskConfig type 1, ids 1010CXXX grouped by chapter C).</summary>
    private long[] ChapterEnds() => _chapterEnds ??= _data.Table("TaskConfig").Values
        .Where(r => GameData.Int(r, "_iTaskType") == 1 && GameData.Int(r, "_iTaskID") is >= 10_100_000 and < 10_200_000)
        .Select(r => GameData.Int(r, "_iTaskID")).GroupBy(id => id / 1000).Select(g => g.Max()).ToArray();

    private void InitializePassPlay(CombatService combat, DungeonService dungeons, MvpService mvps, TaskService tasks,
        ChatService chat)
    {
        combat.Killed.Add((s, _) =>
        {
            bool party = _teams.MembersOf(s.Role!.Id).Count > 1;
            PassEvent(s, party ? [(PassGoal.Kills, 1), (PassGoal.PartyKills, 1)] : [(PassGoal.Kills, 1)], kills: 1);
            return Task.CompletedTask;
        });
        dungeons.Completed.Add((s, _, _) => { PassEvent(s, [(PassGoal.Dungeons, 1)], dungeons: 1); return Task.CompletedTask; });
        mvps.ParticipantSettled.Add(s => PassEvent(s, [(PassGoal.Mvps, 1)], mvps: 1));
        tasks.QuestDelivered.Add((s, task, _) => PassEvent(s, [(PassGoal.Quests, 1)], quest: task));
        _character.StateChanged.Add(s =>
        {
            // Daily activity points are a currency: watch the balance after every award instead of the grants.
            if (s.Progress is { } progress && s.ActivityRuntime?.PassPlay is { } play
                && progress.Currency.GetValueOrDefault(Liveness) != play.LivenessSeen)
                PassEvent(s, []);
        });
        chat.PlayerCommands["/pass"] = PassCommandAsync;
        tasks.ExtraTasks.Add(PassTasks);
    }

    /// <summary>The pass whose EXP currency 132 is earned now: the latest open one (Pass II once Pass I is done).</summary>
    private long? OpenPass(GameSession session) =>
        PassIds.Where(id => id != 9201).Cast<long?>().LastOrDefault(id => ActivityError(session, id!.Value, 23) == 0);

    private static (long Day, long Week) PassPeriod(DateTimeOffset localNow)
    {
        // The game day turns at 05:00, like the daily activity rows.
        var day = DateOnly.FromDateTime(localNow.AddHours(-5).Date);
        int sinceMonday = ((int)day.DayOfWeek + 6) % 7;
        return (day.DayNumber, day.DayNumber - sinceMonday);
    }

    internal static IReadOnlyList<PassChallenge> DailyFor(long roleId, long day)
    {
        // A fixed shuffle per role and day: the same three until the day turns, different between players.
        var rng = new Random(HashCode.Combine(roleId, day) & int.MaxValue);
        return DailyChallenges.OrderBy(_ => rng.Next()).Take(3).ToArray();
    }

    private static bool Roll(PassPlayState play, long roleId, long day, long week)
    {
        bool changed = false;
        if (play.Day != day)
        {
            play.Day = day;
            play.Daily = DailyFor(roleId, day).Select(c => c.Id).ToList();
            play.DailyProgress.Clear();
            play.DailyDone.Clear();
            play.KillExpToday = play.DungeonsToday = play.MvpsToday = 0;
            play.LivenessSeen = 0;
            changed = true;
        }
        if (play.Week != week)
        {
            play.Week = week;
            play.WeeklyProgress.Clear();
            play.WeeklyDone.Clear();
            changed = true;
        }
        if (play.LastPlayedDay != day)
        {
            play.LastPlayedDay = day;
            play.WeeklyProgress["w.days"] = play.WeeklyProgress.GetValueOrDefault("w.days") + 1;
            changed = true;
        }
        return changed;
    }

    internal void PassEvent(GameSession session, (PassGoal Goal, long Count)[] progress, long kills = 0, long dungeons = 0,
        long mvps = 0, long quest = 0)
    {
        if (!_passPlay || session.Role == null || !session.ActivitiesReady) return;
        // Detached: the event often fires inside another owned operation; the activity gate then admits the role and its
        // lifecycle itself once that operation is done.
        var role = session.Role;
        _loop.TrackRuntimeDetached("pass progress", async () =>
        {
            if (!ReferenceEquals(session.Role, role)) return;
            try { await PassEventAsync(session, progress, kills, dungeons, mvps, quest); }
            catch (OperationCanceledException) { } // the role left meanwhile
        });
    }

    private async Task PassEventAsync(GameSession session, (PassGoal Goal, long Count)[] progress, long kills, long dungeons,
        long mvps, long quest)
    {
        if (!session.ActivitiesReady || session.Role == null || session.NewActivities == null || session.ActivityRuntime == null) return;
        // Like ReconcileAsync: the owner scope takes the activity gate with it, and the caller releases the gate.
        var scope = _loop.Owners != null ? await EnterLocalWithGateAsync(session, "activity/pass-progress") : null;
        if (scope == null) await session.ActivityGate.WaitAsync();
        try
        {
            if (!session.ActivitiesReady || session.Role == null || session.NewActivities == null || session.ActivityRuntime == null) return;
            await PassProgressCoreAsync(session, progress, kills, dungeons, mvps, quest);
            if (!session.DeferTaskSync) await _tasks.SyncTasksAsync(session); // the challenges in the season task panel
        }
        finally { session.ActivityGate.Release(); scope?.Dispose(); }
    }

    private async Task PassProgressCoreAsync(GameSession session, (PassGoal Goal, long Count)[] progress, long kills,
        long dungeons, long mvps, long quest)
    {
        long role = session.Role!.Id;
        var runtime = CloneRuntime(session);
        var play = runtime.PassPlay ??= new PassPlayState();
        var (day, week) = PassPeriod(_clock.GetLocalNow());
        bool dirty = Roll(play, role, day, week);
        var notices = new List<string>();
        long baseExp = 0;

        long liveness = session.Progress!.Currency.GetValueOrDefault(Liveness);
        if (liveness < play.LivenessSeen) play.LivenessSeen = 0; // the daily reset came before ours
        long livenessGained = liveness - play.LivenessSeen;
        if (livenessGained > 0)
        {
            play.LivenessSeen = liveness;
            baseExp += livenessGained * LivenessExp;
            progress = [.. progress, (PassGoal.Liveness, livenessGained)];
            dirty = true;
        }
        if (kills > 0)
        {
            long paid = Math.Min(kills * KillExp, Math.Max(0, KillExpDailyCap - play.KillExpToday));
            play.KillExpToday += paid;
            baseExp += paid;
        }
        if (dungeons > 0 && play.DungeonsToday < DungeonsPaidPerDay) { play.DungeonsToday++; baseExp += DungeonExp; }
        if (mvps > 0 && play.MvpsToday < MvpsPaidPerDay) { play.MvpsToday++; baseExp += MvpExp; }
        if (quest != 0 && ChapterEnds().Contains(quest) && !play.ChaptersPaid.Contains(quest))
        {
            play.ChaptersPaid.Add(quest);
            baseExp += ChapterExp;
            notices.Add("Story chapter complete: +" + ChapterExp + " Pass EXP");
        }

        long challengeExp = 0;
        foreach (var (goal, count) in progress)
        {
            dirty = true;
            foreach (var c in DailyChallenges.Where(c => c.Goal == goal && play.Daily.Contains(c.Id) && !play.DailyDone.Contains(c.Id)))
            {
                long now = play.DailyProgress[c.Id] = Math.Min(c.Target, play.DailyProgress.GetValueOrDefault(c.Id) + count);
                if (now < c.Target) continue;
                play.DailyDone.Add(c.Id);
                challengeExp += PassDailyReward;
                notices.Add($"Daily challenge done: {c.Text}, +{PassDailyReward} Pass EXP");
            }
            foreach (var c in WeeklyChallenges.Where(c => c.Goal == goal && !play.WeeklyDone.Contains(c.Id)))
            {
                long now = play.WeeklyProgress[c.Id] = Math.Min(c.Target, play.WeeklyProgress.GetValueOrDefault(c.Id) + count);
                if (now < c.Target) continue;
                play.WeeklyDone.Add(c.Id);
                challengeExp += PassWeeklyReward;
                notices.Add($"Weekly challenge done: {c.Text}, +{PassWeeklyReward} Pass EXP");
            }
        }
        // Play on 5 days counts on the first event of a day (Roll), so check it here too.
        foreach (var c in WeeklyChallenges.Where(c => c.Goal == PassGoal.LoginDays && !play.WeeklyDone.Contains(c.Id)
            && play.WeeklyProgress.GetValueOrDefault(c.Id) >= c.Target))
        {
            play.WeeklyDone.Add(c.Id);
            challengeExp += PassWeeklyReward;
            notices.Add($"Weekly challenge done: {c.Text}, +{PassWeeklyReward} Pass EXP");
        }

        var grants = new List<AwardGrant>();
        if (OpenPass(session) is long passId && baseExp + challengeExp > 0)
        {
            double multiplier = 1;
            if (_teams.MembersOf(role).Count > 1) multiplier += 0.2;
            var info = session.NewActivities!.ActivityInfo[passId];
            double days = Math.Max(1, (Now - info.CycleStartTime) / 86400.0);
            if (play.Instance != info.ActivityInstance) { play.Instance = info.ActivityInstance; play.Earned = 0; play.Overflow = 0; }
            if (play.Earned < days * PacePerDay * 0.75) multiplier *= 2; // catching up
            long exp = (long)Math.Round((baseExp + challengeExp) * multiplier);
            play.Earned += exp;
            var pass = info.ActivityParam.BattlePass;
            if (PassConfig(passId) is { } config && pass.Level >= GameData.Int(config, "_iMaxLevel"))
            {
                play.Overflow += exp;
                while (play.Overflow >= OverflowChestExp)
                {
                    play.Overflow -= OverflowChestExp;
                    grants.AddRange(OverflowChest(session, passId));
                    notices.Add("Pass bonus chest earned");
                }
            }
            grants.Add(new AwardGrant(PassExpCurrency, checked((int)exp)));
        }
        if (quest != 0 && ActivityError(session, 9201, 23) == 0 && _data.Row("TaskConfig", quest) is { } task
            && GameData.Int(task, "_iTaskType") == 1)
            grants.Add(new AwardGrant(NovicePassExpCurrency, (int)NoviceQuestExp));

        if (grants.Count == 0)
        {
            // Progress alone is kept on the session and saved with the next commit; every tenth change is saved now,
            // as a plain role blob like the activity calendar: it moves no asset, so it has no economy receipt.
            if (!dirty) return;
            if (++play.Unsaved < 10) { session.ActivityRuntime = runtime; return; }
            play.Unsaved = 0;
            play.Sequence++;
            var save = _roles.NewTransaction();
            save.RoleBlob(role, "activity_runtime", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(runtime));
            await _roles.CommitAsync(save, "activity/pass-progress", _ => session.ActivityRuntime = runtime);
            foreach (var notice in notices) await GmCommands.ReplyAsync(session, notice);
            return;
        }
        play.Unsaved = 0;
        play.Sequence++;
        int result = await CommitAsync(session, session.NewActivities!.Clone(), extra: grants, runtime: runtime,
            source: string.Create(CultureInfo.InvariantCulture, $"pass/play/{role}/{play.Day}/{play.Sequence}"));
        if (result != 0) { _log.LogWarning("Pass progress for role {Role} not saved: {Result}", role, result); return; }
        if (grants.Count > 0) await ReconcilePassAsync(session);
        foreach (var notice in notices) await GmCommands.ReplyAsync(session, notice);
    }

    private IEnumerable<AwardGrant> OverflowChest(GameSession session, long passId)
    {
        // (inferred) The chest repeats the last level's free reward.
        if (PassConfig(passId) is not { } config || PassReward(passId, (int)GameData.Int(config, "_iMaxLevel")) is not { } row)
            return [];
        long group = GameData.Int(row, "_iFreeReward");
        try { return _awards.Resolve(session, group, Playbook(session)).Grants; }
        catch (AwardResolutionException) { return []; }
    }

    /// <summary>"/pass" lists today's challenges, "/pass week" the weekly ones, on the marquee.</summary>
    private async Task PassCommandAsync(GameSession session, string args)
    {
        if (session.ActivityRuntime == null || session.NewActivities == null || session.Role == null)
        {
            await GmCommands.ReplyAsync(session, "The pass is not ready yet.");
            return;
        }
        var play = session.ActivityRuntime.PassPlay ?? new PassPlayState();
        var (day, week) = PassPeriod(_clock.GetLocalNow());
        if (play.Day != day || play.Week != week)
        {
            play = JsonClone(play);
            Roll(play, session.Role.Id, day, week);
        }
        string head = OpenPass(session) is long passId
            && session.NewActivities.ActivityInfo[passId].ActivityParam.BattlePass is { } pass
            ? $"Pass Lv {pass.Level}, {pass.Exp}/2000 EXP"
            : "No pass is open right now";
        string Line(PassChallenge c, Dictionary<string, long> done, List<string> finished) =>
            finished.Contains(c.Id) ? $"{c.Text} (done)" : $"{c.Text} {done.GetValueOrDefault(c.Id)}/{c.Target}";
        if (args.Trim().Equals("week", StringComparison.OrdinalIgnoreCase))
        {
            await GmCommands.ReplyAsync(session, $"{head}. This week ({play.WeeklyDone.Count}/{WeeklyChallenges.Length} done, "
                + $"+{PassWeeklyReward} each): " + string.Join(" | ", WeeklyChallenges.Select(c => Line(c, play.WeeklyProgress, play.WeeklyDone))));
            return;
        }
        var today = DailyChallenges.Where(c => play.Daily.Contains(c.Id));
        await GmCommands.ReplyAsync(session, $"{head}. Today (+{PassDailyReward} each): "
            + string.Join(" | ", today.Select(c => Line(c, play.DailyProgress, play.DailyDone))) + ". Weekly: /pass week");
    }

    private static PassPlayState JsonClone(PassPlayState play) =>
        System.Text.Json.JsonSerializer.Deserialize<PassPlayState>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(play))!;
}

public sealed partial class ActivityRuntimeState
{
    public PassPlayState? PassPlay { get; set; }
}

/// <summary>A role's pass challenges and EXP caps for the current day and week.</summary>
public sealed class PassPlayState
{
    public long Day { get; set; }
    public long Week { get; set; }
    public long LastPlayedDay { get; set; }
    public List<string> Daily { get; set; } = new();
    public Dictionary<string, long> DailyProgress { get; set; } = new();
    public List<string> DailyDone { get; set; } = new();
    public Dictionary<string, long> WeeklyProgress { get; set; } = new();
    public List<string> WeeklyDone { get; set; } = new();
    public long KillExpToday { get; set; }
    public long DungeonsToday { get; set; }
    public long MvpsToday { get; set; }
    public long LivenessSeen { get; set; }
    public List<long> ChaptersPaid { get; set; } = new();
    /// <summary>The pass instance <see cref="Earned"/> and <see cref="Overflow"/> belong to.</summary>
    public long Instance { get; set; }
    public long Earned { get; set; }
    public long Overflow { get; set; }
    public long Sequence { get; set; }
    public int Unsaved { get; set; }
}
