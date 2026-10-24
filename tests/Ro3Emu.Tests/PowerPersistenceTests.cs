using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class PowerPersistenceTests
{
    [Fact]
    public async Task AFailedPowerSaveDoesNotPublishOrReplaceTheCurrentSnapshot()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("Alice");
        await bot.Inbox.WaitAsync(e => e.IsGame(PowerService.ChangeNtf), TimeSpan.FromSeconds(5));
        var accounts = host.Service<AccountStore>();
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
            using var gate = await CharacterService.Lock(session);
            var before = session.Power;
            var saved = host.Service<RoleStore>().LoadEmblems(session.Role!.Id);
            session.Emblems.Power = null; // force a record write without changing the computed gameplay score
            accounts.BeforeCommitForTest = label =>
            {
                if (label == "power/record") throw new IOException("Power save failure probe");
            };
            try
            {
                await Assert.ThrowsAsync<IOException>(() => host.Service<PowerService>().PublishAsync(session, always: true));
                Assert.Same(before, session.Power);
                Assert.Equal(saved, host.Service<RoleStore>().LoadEmblems(session.Role.Id));
            }
            finally { accounts.BeforeCommitForTest = null; }
        });
        await Assert.ThrowsAsync<TimeoutException>(() =>
            bot.Inbox.WaitAsync(e => e.IsGame(PowerService.ChangeNtf), TimeSpan.FromMilliseconds(300)));
    }
}
