using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;

namespace Ro3Emu.Tests;

/// <summary>
/// A small hand-written game data set (Fixtures/GameData, copied next to the test binaries) that stands in for the
/// tables exported from the client. Every value in it is invented; it holds only what the server needs to boot, let a
/// bot log in, create a role and enter the world, plus the rows the fixture-based tests read. A test that checks a rule
/// rather than real content (a failed save, a retry after a restart, who owns what) points GameDataDir here and runs in
/// a public checkout and in CI; a test about the real content keeps <see cref="ClientDataFactAttribute"/>.
/// See docs/guide/testing.md.
/// </summary>
public static class SyntheticData
{
    /// <summary>The fixture root, used as <see cref="ServerOptions.GameDataDir"/> (config/, sceneeditor/, msgtable.tsv).</summary>
    public static string Dir { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "GameData");

    /// <summary>The fixture's only open-world map; new roles are born there (RoleStore.SpawnSceneEntry).</summary>
    public const long Town = 10111;
}

/// <summary>
/// A whole server on the synthetic fixture: free loopback ports, a throwaway data dir, bots ready to play. Stop and
/// start again (<see cref="RestartAsync"/>) keep the data dir, so a test can check what survives a restart.
/// </summary>
public sealed class SyntheticHost : IAsyncDisposable
{
    private readonly Action<ServerOptions>? _configure;
    private readonly TimeProvider? _clock;

    private SyntheticHost(string dataDir, Action<ServerOptions>? configure, TimeProvider? clock)
    {
        DataDir = dataDir;
        this._configure = configure;
        this._clock = clock;
    }

    public string DataDir { get; }
    public WebApplication App { get; private set; } = null!;
    public string Http { get; private set; } = "";

    public static async Task<SyntheticHost> StartAsync(Action<ServerOptions>? configure = null, TimeProvider? clock = null)
    {
        var host = new SyntheticHost(Path.Combine(Path.GetTempPath(), "ro3emu-synthetic-" + Guid.NewGuid().ToString("N")), configure, clock);
        await host.StartCoreAsync();
        return host;
    }

    private async Task StartCoreAsync()
    {
        var (web, micro, gate) = TestPorts.FreePorts();
        App = ServerHost.Create([], o =>
        {
            o.DataDir = DataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = gate;
            _configure?.Invoke(o);
        }, _clock);
        await App.StartAsync();
        Http = $"http://127.0.0.1:{web}";
    }

    public T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    /// <summary>A bot that has not logged in yet.</summary>
    public BotClient NewBot() => new(Http, File.ReadAllText(Path.Combine(DataDir, "gate_rsa_public.pem")));

    /// <summary>A bot logged in as <paramref name="name"/> (lower case account, password "pw") with a role in the world:
    /// a new role, or with <paramref name="create"/> false the account's only one.</summary>
    public async Task<BotClient> InWorldAsync(string name, bool create = true)
    {
        var bot = NewBot();
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        return bot;
    }

    /// <summary>Stops the server and starts a new one on the same data dir (new ports).</summary>
    public async Task RestartAsync()
    {
        await StopCoreAsync();
        await StartCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync()
    {
        await StopCoreAsync();
        try { Directory.Delete(DataDir, true); } catch (IOException) { }
    }
}
