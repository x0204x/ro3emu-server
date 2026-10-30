using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed partial class PartyExperiencePipelineTests
{
    [Fact]
    public async Task MemberReturningToLineDuringAdmissionCannotIncreaseFrozenRecipientBonus()
    {
        string fixture = CreateFixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture);
            await using var alice = await host.InWorldAsync("FrozenAlice");
            await using var bob = await host.InWorldAsync("FrozenBob");
            await using var carol = await host.InWorldAsync("FrozenCarol");
            await using var dave = await host.InWorldAsync("FrozenDave");
            await CreateParty(alice, bob, carol, dave);
            long[] ids = [(long)alice.Self!.Uuid, (long)bob.Self!.Uuid, (long)carol.Self!.Uuid, (long)dave.Self!.Uuid];
            var sessions = ids.Select(id => Session(host, id)).ToArray();
            var before = ids.Select(id => RateProgress(host, id)).ToArray();
            var loop = host.Service<GameLoop>(); var owners = host.Service<OwnerRegistry>();
            var teams = host.Service<TeamService>(); var reward = host.Service<RewardService>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            object? killWork = null;
            await loop.InvokeAsync(() =>
            {
                sessions[3].LineNumber = sessions[0].LineNumber + 1;
                Assert.Equal(3, teams.ExperienceRecipients(sessions[0]).Count);
                owners.DeferredForTest = origin =>
                {
                    if (origin == "Character.StateLock" && killWork != null && ReferenceEquals(loop.CurrentWork, killWork))
                        deferred.TrySetResult();
                };
                return Task.CompletedTask;
            });
            var hold = loop.InvokeAsync(() => owners.RunAsync("test/hold-party-recipient", [ReservationKey.Role(ids[1])], async () =>
            { entered.TrySetResult(); await release.Task; }));
            var monster = NewPipelineMonster(sessions[0], 990308);
            Task? kill = null;
            try
            {
                await entered.Task.WaitAsync(Wait);
                kill = loop.InvokeAsync(() =>
                {
                    killWork = loop.CurrentWork;
                    return reward.KilledAsync(sessions[0], monster);
                });
                await deferred.Task.WaitAsync(Wait); // 候選3人已選定，真正卡在角色預約，而非依時間猜測。
                await loop.InvokeAsync(() =>
                {
                    sessions[3].LineNumber = sessions[0].LineNumber;
                    Assert.Equal(4, teams.ExperienceRecipients(sessions[0]).Count);
                    Assert.Equal(19000, teams.ExpBonus(sessions[0]));
                    Assert.NotNull(kill); Assert.False(kill.IsCompleted);
                    return Task.CompletedTask;
                });
            }
            finally
            {
                release.TrySetResult();
                await loop.InvokeAsync(() => { owners.DeferredForTest = null; return Task.CompletedTask; });
            }
            await hold.WaitAsync(Wait);
            await (kill ?? throw new InvalidOperationException("擊殺沒有進入預約等待")).WaitAsync(Wait);
            // 自造7EXP按已預約3人17000倍率先取整11，只分4／4／3；回線者本次不追補。
            Assert.Equal(new long[] { 4, 4, 3, 0 }, ids.Select((id, i) => RateProgress(host, id).Base - before[i].Base));
            Assert.Equal(new long[] { 4, 4, 3, 0 }, ids.Select((id, i) => RateProgress(host, id).Job - before[i].Job));
            string source = Assert.IsType<RewardSourceId>(monster.RewardSource).Key;
            var obligations = Obligations(host, source);
            Assert.Equal(2, obligations.Count);
            Assert.DoesNotContain(obligations, row => row.Role == ids[3]);
            Assert.All(obligations, row => Assert.True(row.Delivered));
            var settled = ids.Select(id => RateProgress(host, id)).ToArray();
            await loop.InvokeAsync(() => reward.KilledAsync(sessions[0], monster));
            Assert.Equal(settled, ids.Select(id => RateProgress(host, id)).ToArray());
            Assert.Equal(obligations.ToArray(), Obligations(host, source).ToArray());
            await loop.InvokeAsync(() => reward.KilledAsync(sessions[0], NewPipelineMonster(sessions[0], 990309)));
            // 下一次才以4人19000倍率的13池分4／3／3／3，不能永遠漏掉已回線隊員。
            Assert.Equal(new long[] { 4, 3, 3, 3 }, ids.Select((id, i) => RateProgress(host, id).Base - settled[i].Base));
            Assert.Equal(new long[] { 4, 3, 3, 3 }, ids.Select((id, i) => RateProgress(host, id).Job - settled[i].Job));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(fixture, true); }
    }
}
