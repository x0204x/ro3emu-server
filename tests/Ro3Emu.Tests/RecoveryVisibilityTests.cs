using System.Text.Json;
using Ro3Emu.Panel;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>A role that cannot log in until a restart shows up where the operator looks: metrics and the panel.</summary>
public sealed class RecoveryVisibilityTests
{
    [Fact]
    public async Task ARoleInRecoveryIsCountedInTheMetricsAndOnThePanel()
    {
        var (host, _) = await PanelTests.StartAsync(_ => { });
        await using var _host = host;
        var owners = host.Service<OwnerRegistry>();
        Assert.Equal(0, owners.RolesInRecovery);

        var bot = await host.InWorldAsync("RecoveryShown");
        long role = bot.Roles.Single().Id;
        host.Service<RoleSessionService>().CleanupFailureForTest = (step, _) =>
            step == "festival" ? new IOException("injected cleanup failure") : null;
        await bot.DisposeAsync();

        var loop = host.Service<GameLoop>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (await loop.InvokeAsync(() => Task.FromResult(owners.Get(role).State)) != RoleOwnerState.RecoveryRequired)
        {
            Assert.True(DateTime.UtcNow < deadline, "the role never entered recovery");
            await Task.Delay(25);
        }

        Assert.Equal(1, owners.RolesInRecovery);
        using var http = Operator.Client(host.Http);
        using var metrics = JsonDocument.Parse(await http.GetStringAsync("/ro3emu/metrics"));
        Assert.Equal(1, metrics.RootElement.GetProperty("ownership").GetProperty("rolesInRecovery").GetInt64());
        Assert.Equal(1, (await host.Service<IPanelBackend>().DashboardAsync()).RolesInRecovery);
    }

    [Fact]
    public void AnOwnerLeavingRecoveryIsNotCountedTwice()
    {
        var changes = new List<bool>();
        var owner = new RoleOwner(1, 1) { RecoveryChanged = (_, entered) => changes.Add(entered) };
        owner.State = RoleOwnerState.RecoveryRequired;
        owner.State = RoleOwnerState.RecoveryRequired;
        owner.State = RoleOwnerState.Detached;
        owner.State = RoleOwnerState.Attached;
        Assert.Equal([true, false], changes);
    }
}
