using System.Runtime.CompilerServices;

namespace Ro3Emu.Tests;

/// <summary>
/// Options every test host starts with unless the test sets its own. The web panel listens on a fixed port (5381) by
/// default, which hundreds of parallel test hosts cannot share, so it is off here; PanelTests turn it on with a free
/// port. The update manager never asks GitHub from a test (UpdateTests give it a local feed). The walk check only reports
/// ("log"): most tests put a bot where they need it with one walk; AntiCheatTests set "enforce". The game data is the
/// synthetic fixture: a host without GameDataDir would read its tables from its data dir and the economy catalogue from
/// the client's export above the binaries, so it ran on other data on your machine than in CI (docs/guide/testing.md);
/// a client-data test sets GameDataDir to data/ itself. Every host has the same operator token (<see cref="Operator"/>),
/// so the tests reach the loopback tools through one helper. Environment variables reach every ServerHost.Create through
/// its configuration; a test's configure wins.
/// </summary>
internal static class TestHostDefaults
{
    [ModuleInitializer]
    internal static void Apply()
    {
        Environment.SetEnvironmentVariable("Ro3Emu__Panel__Enabled", "false");
        Environment.SetEnvironmentVariable("Ro3Emu__Updates__Check", "false");
        Environment.SetEnvironmentVariable("Ro3Emu__WalkCheck", "log");
        Environment.SetEnvironmentVariable("Ro3Emu__GameDataDir", SyntheticData.Dir);
        Environment.SetEnvironmentVariable("Ro3Emu__OperatorToken", Operator.Token);
    }
}
