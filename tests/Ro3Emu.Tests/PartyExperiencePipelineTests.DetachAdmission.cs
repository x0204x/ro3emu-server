using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed partial class PartyExperiencePipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachDuringBonusEligibilityCancelsUnadmittedKillAndRetryPaysOnce(bool detachWholeParty)
    {
        string fixture = CreateFixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture);
            await using var alice = await host.InWorldAsync("ExitAlice");
            await using var bob = await host.InWorldAsync("ExitBob");
            await using var carol = await host.InWorldAsync("ExitCarol");
            await CreateParty(alice, bob, carol);
            long[] ids = [(long)alice.Self!.Uuid, (long)bob.Self!.Uuid, (long)carol.Self!.Uuid];
            var sessions = ids.Select(id => Session(host, id)).ToArray();
            var before = ids.Select(id => RateProgress(host, id)).ToArray();
            var killer = sessions[0];
            var monster = NewPipelineMonster(killer, 990310);
            var reward = host.Service<RewardService>();
            var loop = host.Service<GameLoop>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await loop.InvokeAsync(() =>
            {
                reward.QualifiesForBonus = async (source, target) =>
                {
                    Assert.Same(killer, source);
                    Assert.Same(monster, target);
                    entered.TrySetResult();
                    await release.Task;
                    return false;
                };
                return Task.CompletedTask;
            });
            var pending = loop.InvokeAsync(() => reward.PrepareKilledAsync(killer, monster));
            try
            {
                await entered.Task.WaitAsync(Wait);
                string source = Assert.IsType<RewardSourceId>(monster.RewardSource).Key;
                Assert.False(await host.Service<CharacterService>().RewardSourceCommittedAsync(source));
                var detached = new List<Task>();
                await loop.InvokeAsync(() =>
                {
                    foreach (var session in detachWholeParty ? sessions : [killer])
                        detached.Add(host.Service<RoleHandover>().Begin(session));
                    return Task.CompletedTask;
                });
                await Task.WhenAll(detached).WaitAsync(Wait);
                Assert.Null(killer.Role); // 真交接已清空角色；不是只設 Cancelled 或靠 sleep 猜窗口。
                Assert.Null(killer.Attachment);
                release.TrySetResult();
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(Wait));
                Assert.Null(monster.RewardCommit);
                Assert.NotNull(monster.RewardSettlement);
                Assert.False(monster.RewardSettlement.Committed);
                Assert.False(monster.RewardSettlement.OutcomeUnknown);
                Assert.False(await host.Service<CharacterService>().RewardSourceCommittedAsync(source));
                Assert.Empty(Obligations(host, source));
                Assert.Equal(before, ids.Select(id => RateProgress(host, id)).ToArray());
            }
            finally
            {
                release.TrySetResult();
                await loop.InvokeAsync(() => { reward.QualifiesForBonus = null; return Task.CompletedTask; });
            }

            // admission 前取消沒有發獎，也沒有把同一來源誤標為已付；原角色重登可重試一次。
            await alice.DisposeAsync();
            await using var resumedBot = await host.InWorldAsync("ExitAlice", create: false);
            var resumed = Session(host, ids[0]);
            Assert.NotSame(killer, resumed);
            await loop.InvokeAsync(async () =>
            {
                // 原隊伍在重登後保留；仍在線的兩名隊員也必須獲得這次真正受理的份額。
                long[] expectedRecipients = detachWholeParty ? [ids[0]] : ids;
                Assert.Equal(expectedRecipients, host.Service<TeamService>().ExperienceRecipients(resumed)
                    .Select(session => session.Role!.Id));
                await reward.PrepareKilledAsync(resumed, monster);
                await reward.KilledAsync(resumed, monster);
            });
            long[] expectedBase = detachWholeParty ? [7, 0, 0] : [4, 4, 3];
            long[] expectedJob = detachWholeParty ? [1, 0, 0] : [1, 1, 0];
            Assert.Equal(expectedBase, ids.Select((id, i) => RateProgress(host, id).Base - before[i].Base));
            Assert.Equal(expectedJob, ids.Select((id, i) => RateProgress(host, id).Job - before[i].Job));
            string committedSource = Assert.IsType<RewardSourceId>(monster.RewardSource).Key;
            Assert.True(await host.Service<CharacterService>().RewardSourceCommittedAsync(committedSource));
            var obligations = Obligations(host, committedSource);
            if (detachWholeParty) Assert.Empty(obligations);
            else
            {
                Assert.Equal(new[] { (ids[1], 4L, 1L, true), (ids[2], 3L, 0L, true) }, obligations);
            }
            var after = ids.Select(id => RateProgress(host, id)).ToArray();
            await loop.InvokeAsync(() => reward.KilledAsync(resumed, monster));
            Assert.Equal(after, ids.Select(id => RateProgress(host, id)).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(fixture, true); }
    }
}
