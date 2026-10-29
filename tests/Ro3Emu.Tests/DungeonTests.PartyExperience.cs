using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    [ClientDataFact]
    public async Task RepeatedThreeMemberFieldKillsCommitBothPartyExperienceObligations()
    {
        await using var alice = await InWorld("ExpThreeA");
        await using var bob = await InWorld("ExpThreeB");
        await using var carol = await InWorld("ExpThreeC");
        await Party(alice, bob, carol);

        var killer = Session(alice);
        var first = Session(bob);
        var second = Session(carol);
        var reward = Service<RewardService>();
        await OnLoop(() =>
        {
            reward.ResolveKillForTest = (_, _) => ([], 100, 50);
            return Task.CompletedTask;
        });
        try
        {
            for (int i = 0; i < 5; i++)
            {
                var firstBefore = Progression(first);
                var secondBefore = Progression(second);
                var monster = PartyExperienceMonster(killer, 910000001 + i);
                await OnLoop(async () =>
                {
                    await reward.PrepareKilledAsync(killer, monster);
                    await reward.KilledAsync(killer, monster);
                });
                Assert.NotEqual(firstBefore.Base, Progression(first).Base);
                Assert.NotEqual(firstBefore.Job, Progression(first).Job);
                Assert.NotEqual(secondBefore.Base, Progression(second).Base);
                Assert.NotEqual(secondBefore.Job, Progression(second).Job);
            }
        }
        finally
        {
            await OnLoop(() =>
            {
                reward.ResolveKillForTest = null;
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// Minimum red contract for field-party EXP sharing. The exact split, level eligibility, distance,
    /// death/loading, contribution and offline rules are not established by the Asia client and must not
    /// be invented here. The exact G client establishes Base and Job fields on authoritative 5066 syncs and
    /// names a party EXP bonus in its UI; therefore this test only requires positive changes in both tracks.
    /// </summary>
    [ClientDataFact]
    public async Task SameMapPartyMemberReceivesBaseAndJobExperienceFromOrdinaryKill()
    {
        await using var alice = await InWorld("PartyExpAlice");
        await using var bob = await InWorld("PartyExpBob");
        await Party(alice, bob);

        var killer = Session(alice);
        var member = Session(bob);
        var killerBefore = Progression(killer);
        var memberBefore = Progression(member);
        int memberSyncCount = bob.Inbox.Snapshot().Count(e => e.IsGame(CharacterService.RoleBaseInfoSync));
        var reward = Service<RewardService>();
        await OnLoop(() =>
        {
            reward.ResolveKillForTest = (_, _) => ([], 100, 50);
            return Task.CompletedTask;
        });
        var monster = PartyExperienceMonster(killer);

        try
        {
            await OnLoop(async () =>
            {
                await reward.PrepareKilledAsync(killer, monster);
                await reward.KilledAsync(killer, monster);
            });
        }
        finally
        {
            await OnLoop(() =>
            {
                reward.ResolveKillForTest = null;
                return Task.CompletedTask;
            });
        }

        var killerAfter = Progression(killer);
        var memberAfter = Progression(member);
        Assert.NotEqual(killerBefore.Base, killerAfter.Base);
        Assert.NotEqual(killerBefore.Job, killerAfter.Job);
        Assert.True(memberBefore.Base != memberAfter.Base,
            "A same-map party member did not receive Base EXP from the ordinary kill.");
        Assert.True(memberBefore.Job != memberAfter.Job,
            "A same-map party member did not receive Job EXP from the ordinary kill.");
        var memberProgression = bob.Inbox.Snapshot()
            .Where(e => e.IsGame(CharacterService.RoleBaseInfoSync))
            .Skip(memberSyncCount)
            .Select(e => Romsg.RoleBaseInfoIncrement.Parser.ParseFrom(e.Body));
        Assert.Contains(memberProgression, increment =>
            (increment.HasExp || increment.HasLevel) && (increment.HasJobExp || increment.HasJobLevel));
    }

    private static ((int Level, long Exp) Base, (int Level, long Exp) Job) Progression(GameSession session)
    {
        var progress = session.Progress!;
        return ((progress.Level, progress.Exp), (progress.JobLevel, progress.JobExp));
    }

    private static MonsterUnit PartyExperienceMonster(GameSession killer, long uid = 910000001) => new()
    {
        Uid = uid,
        SceneEntry = killer.SceneEntry,
        RuntimeMap = killer.RuntimeMap,
        Template = new MonsterTemplate(10001, 1, 10, 0, 1, 1, 0, 0, 1, 0, 0, 100, 1, 1, 1, 1,
            false, 0, 1000, 5, 10, 0, 1000, .1f, 1000, 1, 500),
        Group = new SpawnGroup(-1, false, 1, 0, [], []),
        Home = new SpawnPoint(-1, 0, 0, 0, 0, 10001),
        Hp = 0,
    };
}
