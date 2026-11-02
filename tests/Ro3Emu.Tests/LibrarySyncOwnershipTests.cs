using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class LibrarySyncOwnershipTests
{
    [Fact]
    public async Task CommittedCodexKillReachesClientWhileAnotherRoleIsReserved()
    {
        const int monsterId = 16320099;
        var wait = TimeSpan.FromSeconds(5);
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        var loop = host.Service<GameLoop>();
        var owners = host.Service<OwnerRegistry>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = loop.InvokeAsync(() => owners.RunAsync("unrelated role work",
            [ReservationKey.Role((long)bob.Self!.Uuid)], async () => { entered.SetResult(); await release.Task; }));
        try
        {
            await entered.Task.WaitAsync(wait);
            await loop.InvokeAsync(async () =>
            {
                var session = host.Service<SessionRegistry>().All().Single(s => s.Account == "alice");
                var monster = new MonsterUnit
                {
                    Uid = 900001, SceneEntry = session.SceneEntry, RuntimeMap = session.RuntimeMap,
                    Template = host.Service<MonsterService>().Template(monsterId)!, Hp = 0,
                    Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, monsterId)
                };
                await host.Service<RewardService>().PrepareKilledAsync(session, monster);
            }).WaitAsync(wait);
            var saved = DBLibraryInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadLibrary((long)alice.Self!.Uuid)!);
            Assert.Equal(1, saved.Monsters[monsterId].KillCount);
            var message = await alice.Inbox.WaitAsync(e => e.IsGame(CardService.LibrarySync)
                && DBLibraryInfoIncrement.Parser.ParseFrom(e.Body).Monsters.ContainsKey(monsterId), wait);
            Assert.NotNull(message);
            Assert.False(held.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await held.WaitAsync(wait);
        }
    }
}
