using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Job EXP for roles older than <see cref="JobExpFromBase"/>. (Not original: it repairs this server's earlier rule.)
/// Their quests paid no Job EXP, so a role can stand at Base 30 far below the Job 40 that Job Advance asks with it, and
/// from now on kills bring only their share. At its first login since the rule a role's debt is frozen: what its total
/// Base EXP should have brought, less the Job EXP it earned. The current rank is paid as far as it has room, and the rest
/// with each job advance. The debt is never worked out again, so EXP lost later at a rank's cap is not refunded, and
/// nobody loses Job EXP it already has.
/// </summary>
public sealed partial class CharacterService
{
    /// <summary>
    /// Role blob "job_exp_catchup" (JSON): present once the role's debt is decided. Each payment commits it together with
    /// its EXP, so a crash cannot pay a part twice or lose it. A marker without Remaining owes nothing.
    /// </summary>
    public sealed class JobExpCatchUpState
    {
        public long Granted { get; set; }
        public long Remaining { get; set; }

        /// <summary>The job rank paid last: the next payment waits for another rank.</summary>
        public int Rank { get; set; }
    }

    /// <summary>At login: decides the debt once; later it pays what an advance left unpaid, if a crash came between them.</summary>
    internal async Task CatchUpJobExpAsync(GameSession session)
    {
        long role = session.Role!.Id;
        if (roles.LoadJobExpCatchUp(role) is { Length: > 0 } stored)
        {
            if (JsonSerializer.Deserialize<JobExpCatchUpState>(stored)?.Remaining > 0) await PayJobExpCatchUpAsync(session);
            return;
        }
        using var held = await LockAsync(session);
        var progress = session.Progress!;
        long owed = JobExpOwed(progress);
        long paid = Math.Min(owed, JobExpRoom(progress));
        var state = new JobExpCatchUpState { Granted = paid, Remaining = owed - paid, Rank = JobRank(progress.Career) };
        try
        {
            if (paid <= 0) roles.SaveJobExpCatchUp(role, JsonSerializer.SerializeToUtf8Bytes(state));
            else await GrantJobExpCatchUpAsync(session, paid, $"job-exp-catchup/{role}", state);
        }
        catch (EconomyOperationConflictException)
        {
            // The natural source allows one first grant per role, whatever happened to its marker.
            LogJobExpCaughtUp(log, role);
            roles.SaveJobExpCatchUp(role, JsonSerializer.SerializeToUtf8Bytes(new JobExpCatchUpState()));
        }
    }

    /// <summary>
    /// After a job advance: the current rank's part of the debt, as far as it has room. A refused write leaves the debt
    /// as it is, for the next login; the job change itself has already committed.
    /// </summary>
    private async Task PayJobExpCatchUpAsync(GameSession session)
    {
        long role = session.Role!.Id;
        try
        {
            using var held = await LockAsync(session);
            if (roles.LoadJobExpCatchUp(role) is not { Length: > 0 } stored) return;
            var state = JsonSerializer.Deserialize<JobExpCatchUpState>(stored) ?? new JobExpCatchUpState();
            var progress = session.Progress!;
            int rank = JobRank(progress.Career);
            long paid = Math.Min(state.Remaining, JobExpRoom(progress));
            if (paid <= 0 || rank == state.Rank) return;
            // One payment per rank, which the natural source holds too: the rest waits for the next rank.
            await GrantJobExpCatchUpAsync(session, paid, $"job-exp-catchup/{role}/{rank}", new JobExpCatchUpState
            {
                Granted = checked(state.Granted + paid),
                Remaining = state.Remaining - paid,
                Rank = rank,
            });
        }
        catch (Exception error) when (error is EconomyOperationConflictException or PersistenceException)
        {
            LogJobExpCatchUpWaits(log, role, error.Message);
        }
    }

    /// <summary>Pays Job EXP and writes the marker in one commit (caller holds the lock). A refused plan changes nothing.</summary>
    private async Task GrantJobExpCatchUpAsync(GameSession session, long paid, string sourceKey, JobExpCatchUpState next)
    {
        long role = session.Role!.Id;
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(next);
        var source = new OwnedAwardSource("cd13e/grant", sourceKey, new { Version = 1, Granted = paid, next.Remaining });
        await ApplyAwardAsync(session, new AwardPlan(0, 0, [new AwardGrant(JobExpItem, checked((int)paid))]),
            persist: builder => roles.SaveJobExpCatchUp(role, marker, builder), stateLockHeld: true, ownedAward: source);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Role {Role} already had its Job EXP catch-up")]
    private static partial void LogJobExpCaughtUp(ILogger logger, long role);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role {Role}: the rest of its Job EXP catch-up waits for a later login or rank: {Reason}")]
    private static partial void LogJobExpCatchUpWaits(ILogger logger, long role, string reason);

    /// <summary>Base EXP's share of Job EXP less the Job EXP earned, through the ranks up to the current one.</summary>
    private long JobExpOwed(RoleProgress p)
    {
        long baseExp = p.Exp;
        for (int level = 1; level < p.Level; level++) baseExp += BaseExpNeed(level);
        long earned = p.JobExp;
        for (int rank = 1; rank < JobRank(p.Career); rank++)
            for (int level = 1; JobLvConfig.Get(data, rank * 100L + level) is { } row && level < row.MaxJobLv; level++)
                earned += row.JobeExp;
        for (int level = 1; level < p.JobLevel; level++) earned += JobExpNeed(p.Career, level);
        return Math.Max(0, JobExpFromBase(baseExp) - earned);
    }

    /// <summary>The Job EXP the current rank can still take, as ApplyExp would hold it.</summary>
    private long JobExpRoom(RoleProgress p)
    {
        long room = -p.JobExp;
        for (int level = p.JobLevel; level < MaxJobLevel(p.Career); level++) room += JobExpNeed(p.Career, level);
        return Math.Max(0, room);
    }
}
