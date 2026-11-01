using System.Runtime.CompilerServices;

namespace Ro3Emu.Tests;

/// <summary>
/// Options every test host starts with unless the test sets its own. The web panel listens on a fixed port (5381) by
/// default, which hundreds of parallel test hosts cannot share, so it is off here; PanelTests turn it on with a free
/// port. The update manager never asks GitHub from a test (UpdateTests give it a local feed). The walk check only reports
/// ("log"): most tests put a bot where they need it with one walk; AntiCheatTests set "enforce". Environment variables reach
/// every ServerHost.Create through its configuration; a test's configure wins.
/// </summary>
internal static class TestHostDefaults
{
    [ModuleInitializer]
    internal static void Apply()
    {
        Environment.SetEnvironmentVariable("Ro3Emu__Panel__Enabled", "false");
        Environment.SetEnvironmentVariable("Ro3Emu__Updates__Check", "false");
        Environment.SetEnvironmentVariable("Ro3Emu__WalkCheck", "log");
    }
}
