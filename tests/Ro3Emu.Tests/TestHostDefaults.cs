using System.Runtime.CompilerServices;

namespace Ro3Emu.Tests;

/// <summary>
/// Options every test host starts with unless the test sets its own. The web panel listens on a fixed port (5381) by
/// default, which hundreds of parallel test hosts cannot share, so it is off here; PanelTests turn it on with a free
/// port. Environment variables reach every ServerHost.Create through its configuration; a test's configure wins.
/// </summary>
internal static class TestHostDefaults
{
    [ModuleInitializer]
    internal static void Apply() => Environment.SetEnvironmentVariable("Ro3Emu__Panel__Enabled", "false");
}
