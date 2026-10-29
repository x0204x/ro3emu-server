using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class MapTests
{
    private long MergedKillReceiptCount(string kind)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind=$kind";
        command.Parameters.AddWithValue("$kind", kind);
        return (long)command.ExecuteScalar()!;
    }

    private long MergedMvpSourceCount()
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM mvp_settlement WHERE cfg_id=1";
        return (long)command.ExecuteScalar()!;
    }

    private MonsterUnit MergedBoss(long uid) => _app.Services.GetRequiredService<World>()
        .AllUnits().OfType<MonsterUnit>().Single(m => m.Uid == uid);

    private async Task WaitMergedBossAsync(long uid, Func<MonsterUnit, bool> ready)
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var until = DateTime.UtcNow + Wait;
        while (!await loop.InvokeAsync(() => Task.FromResult(ready(MergedBoss(uid)))))
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("MVP did not reach the expected death barrier.");
            await Task.Delay(10);
        }
    }

    [ClientDataFact]
    public async Task MergedMvpDeathCommitsEventAndOrdinaryRewardOnce()
    {
        var (alice, uid) = await ReadySoloMvpAsync();
        await using (alice)
        {
            var reward = _app.Services.GetRequiredService<RewardService>();
            var combat = _app.Services.GetRequiredService<CombatService>();
            reward.ResolveKillForTest = (_, _) => ([(1, 7)], 0, 0);
            long role = (long)alice.Self!.Uuid;
            var roles = _app.Services.GetRequiredService<RoleStore>();
            long before = roles.LoadProgress(role).Currency.GetValueOrDefault(1);
            var phases = new List<string>();
            var finalized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<GameSession, MonsterUnit, long, Task> damage = (_, m, _) =>
            { if (m.Uid == uid) phases.Add("damage"); return Task.CompletedTask; };
            Func<GameSession, MonsterUnit, long, Task> eventBarrier = (_, m, _) =>
            { if (m.Uid == uid) phases.Add("event"); return Task.CompletedTask; };
            Func<GameSession, MonsterUnit, Task> rewardBarrier = (_, m) =>
            { if (m.Uid == uid) phases.Add("reward"); return Task.CompletedTask; };
            Func<GameSession, MonsterUnit, Task> published = (_, m) =>
            { if (m.Uid == uid) phases.Add("published"); return Task.CompletedTask; };
            Func<MonsterUnit, Task> finalizer = m =>
            { if (m.Uid == uid) { phases.Add("finalized"); finalized.TrySetResult(); } return Task.CompletedTask; };
            combat.EventDamageRecorded.Add(damage);
            combat.BeforeLethalPublication.Add(eventBarrier);
            combat.BeforeKilled.Add(rewardBarrier);
            combat.Killed.Add(published);
            combat.AfterLethalPublication.Add(finalizer);
            try
            {
                await SwingWait.UntilDoneAsync(_app.Services.GetRequiredService<GameLoop>(), _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == alice.Account && s.Role != null), uid);
                await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = uid });
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await Next(alice, MvpService.DeadNotify, MVPDeadNotify.Parser);
                await finalized.Task.WaitAsync(Wait);
                await WaitMergedBossAsync(uid, m => m.Dead);
                Assert.Equal(new[] { "damage", "event", "reward", "published", "finalized" }, phases);
                Assert.Equal(1, MergedMvpSourceCount());
                Assert.Equal(1, MergedKillReceiptCount("event/mvp-settlement"));
                Assert.Equal(1, MergedKillReceiptCount("monster-kill"));
                Assert.Equal(before + 7, roles.LoadProgress(role).Currency.GetValueOrDefault(1));
            }
            finally
            {
                reward.ResolveKillForTest = null;
                combat.EventDamageRecorded.Remove(damage);
                combat.BeforeLethalPublication.Remove(eventBarrier);
                combat.BeforeKilled.Remove(rewardBarrier);
                combat.Killed.Remove(published);
                combat.AfterLethalPublication.Remove(finalizer);
            }
        }
    }

    [ClientDataFact]
    public async Task MergedMvpOrdinaryRewardRefusalWithholdsLethalPublication()
    {
        var (alice, uid) = await ReadySoloMvpAsync();
        await using (alice)
        {
            var reward = _app.Services.GetRequiredService<RewardService>();
            var accounts = _app.Services.GetRequiredService<AccountStore>();
            reward.ResolveKillForTest = (_, _) => ([(1, 7)], 0, 0);
            long role = (long)alice.Self!.Uuid;
            var roles = _app.Services.GetRequiredService<RoleStore>();
            long before = roles.LoadProgress(role).Currency.GetValueOrDefault(1);
            var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            accounts.BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/monster-kill") return Task.CompletedTask;
                refused.TrySetResult();
                throw new IOException("Refuse the ordinary kill before commit.");
            };
            try
            {
                await SwingWait.UntilDoneAsync(_app.Services.GetRequiredService<GameLoop>(), _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == alice.Account && s.Role != null), uid);
                await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = uid });
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await refused.Task.WaitAsync(Wait);
                await WaitMergedBossAsync(uid, m => m.Hp == 1);
                Assert.False(MergedBoss(uid).Dead);
                Assert.Equal(1, MergedMvpSourceCount());
                Assert.Equal(1, MergedKillReceiptCount("event/mvp-settlement"));
                Assert.Equal(0, MergedKillReceiptCount("monster-kill"));
                Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
                    && SkillHitInfo.Parser.ParseFrom(e.Body).Dsts.Any(d => d.DstID == uid && d.IsDead));
                accounts.BeforeOwnedCommitForTest = null;
                await SwingWait.UntilDoneAsync(_app.Services.GetRequiredService<GameLoop>(), _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == alice.Account && s.Role != null), uid);
                await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = uid });
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await Next(alice, MvpService.DeadNotify, MVPDeadNotify.Parser);
                await WaitMergedBossAsync(uid, m => m.Dead);
                Assert.Equal(1, MergedMvpSourceCount());
                Assert.Equal(1, MergedKillReceiptCount("event/mvp-settlement"));
                Assert.Equal(1, MergedKillReceiptCount("monster-kill"));
                Assert.Equal(before + 7, roles.LoadProgress(role).Currency.GetValueOrDefault(1));
            }
            finally { accounts.BeforeOwnedCommitForTest = null; reward.ResolveKillForTest = null; }
        }
    }

    [ClientDataFact]
    public async Task MergedMvpLostOrdinaryRewardAcknowledgementKeepsSinglePayment()
    {
        var (alice, uid) = await ReadySoloMvpAsync();
        await using (alice)
        {
            var reward = _app.Services.GetRequiredService<RewardService>();
            var accounts = _app.Services.GetRequiredService<AccountStore>();
            reward.ResolveKillForTest = (_, _) => ([(1, 7)], 0, 0);
            long role = (long)alice.Self!.Uuid;
            var roles = _app.Services.GetRequiredService<RoleStore>();
            long before = roles.LoadProgress(role).Currency.GetValueOrDefault(1);
            int injected = 0;
            accounts.AfterWriterCommitForTest = label =>
            {
                if (label == "economy/monster-kill" && Interlocked.Increment(ref injected) == 1)
                    throw new IOException("Lose the committed ordinary reward acknowledgement.");
            };
            try
            {
                await SwingWait.UntilDoneAsync(_app.Services.GetRequiredService<GameLoop>(), _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == alice.Account && s.Role != null), uid);
                await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = uid });
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await WaitMergedBossAsync(uid, m => m.Dead);
                Assert.Equal(1, MergedMvpSourceCount());
                Assert.Equal(1, MergedKillReceiptCount("event/mvp-settlement"));
                Assert.Equal(1, MergedKillReceiptCount("monster-kill"));
                Assert.Equal(1, injected);
                Assert.Equal(before + 7, roles.LoadProgress(role).Currency.GetValueOrDefault(1));
            }
            finally { accounts.AfterWriterCommitForTest = null; reward.ResolveKillForTest = null; }
        }
    }

    [ClientDataFact]
    public async Task MergedMvpUnknownEventThenOrdinaryRefusalRetriesWithoutHpOrSourceDrift()
    {
        var (alice, uid) = await ReadySoloMvpAsync();
        await using (alice)
        {
            var mvp = _app.Services.GetRequiredService<MvpService>();
            var reward = _app.Services.GetRequiredService<RewardService>();
            var accounts = _app.Services.GetRequiredService<AccountStore>();
            reward.ResolveKillForTest = (_, _) => ([(1, 7)], 0, 0);
            var retryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int probes = 0, rewardAttempts = 0;
            mvp.AfterSettlementCommitForTest = _ =>
            {
                // The basic attack repeats after a refused reward restores the boss to 1 HP.
                // Keep this fixture on the retained source rather than landing a second cast.
                var player = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice");
                player.Attack = null;
                if (player.SkillCast is { } cast) cast.Cancelled = true;
                return Task.FromException(new IOException("Lose MVP settlement acknowledgement."));
            };
            mvp.BeforeSettlementProbeForTest = _ =>
            {
                int n = Interlocked.Increment(ref probes);
                if (n == 1) return Task.FromException(new IOException("First MVP source probe unavailable."));
                if (n == 3) { retryEntered.TrySetResult(); return releaseRetry.Task; }
                return Task.CompletedTask;
            };
            accounts.BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/monster-kill") return Task.CompletedTask;
                if (Interlocked.Increment(ref rewardAttempts) == 1)
                    FixtureSql.Execute(accounts, "CREATE TRIGGER merged_kill_fail BEFORE INSERT ON economy_entry BEGIN SELECT RAISE(ABORT,'merged kill refusal'); END");
                else FixtureSql.Execute(accounts, "DROP TRIGGER IF EXISTS merged_kill_fail");
                return Task.CompletedTask;
            };
            try
            {
                await SwingWait.UntilDoneAsync(_app.Services.GetRequiredService<GameLoop>(), _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == alice.Account && s.Role != null), uid);
                await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = uid });
                Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
                await retryEntered.Task.WaitAsync(Wait);
                Assert.Equal(1, MergedBoss(uid).Hp);
                Assert.False(MergedBoss(uid).Dead);
                Assert.Equal(1, MergedMvpSourceCount());
                Assert.Equal(0, MergedKillReceiptCount("monster-kill"));
                releaseRetry.TrySetResult();
                await WaitMergedBossAsync(uid, m => m.Dead);
                Assert.Equal(0, MergedBoss(uid).Hp);
                Assert.Equal(2, rewardAttempts);
                Assert.Equal(1, MergedMvpSourceCount());
                Assert.Equal(1, MergedKillReceiptCount("event/mvp-settlement"));
                Assert.Equal(1, MergedKillReceiptCount("monster-kill"));
            }
            finally
            {
                releaseRetry.TrySetResult();
                accounts.BeforeOwnedCommitForTest = null;
                if (rewardAttempts == 1) FixtureSql.Execute(accounts, "DROP TRIGGER IF EXISTS merged_kill_fail");
                mvp.AfterSettlementCommitForTest = null;
                mvp.BeforeSettlementProbeForTest = null;
                reward.ResolveKillForTest = null;
            }
        }
    }
}
