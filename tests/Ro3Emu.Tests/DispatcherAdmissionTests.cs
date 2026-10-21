using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Messaging;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>
/// How the dispatcher admits every message of the client's table, for a player in the world and for an account on the
/// role screen ("refused": the stage gate drops it before admission, see LoginGateTests). The expected list is
/// Snapshots/dispatcher-admission.txt: moving admission rules around (to the routes, into services) must leave it
/// unchanged. Regenerate it with RO3EMU_UPDATE_SNAPSHOTS=1 only for an intended change.
/// </summary>
public sealed class DispatcherAdmissionTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-admission-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private BotClient Bot() => new(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));

    private GameSession Session(string account) =>
        _app.Services.GetRequiredService<SessionRegistry>().All().First(s => s.Account == account);

    [ClientDataFact]
    public async Task EveryMessageIsAdmittedAsInTheSnapshot()
    {
        await using var player = Bot();
        await player.LoginAsync("player", "pw");
        await player.EnterGateAsync();
        await player.EnterWorldAsync((await player.CreateRoleAsync("Player")).RoleBaseInfo.Id);
        await using var lobby = Bot();
        await lobby.LoginAsync("lobby", "pw");
        await lobby.EnterGateAsync();

        var handler = _app.Services.GetRequiredService<GameMessageHandler>();
        var catalog = _app.Services.GetRequiredService<MsgCatalog>();
        var ids = catalog.Ids.Concat(handler.Routes.Exact.Select(r => r.From))
            .Concat(handler.Routes.Ranges.SelectMany(r => new[] { r.From, r.To }))
            .Append(999_999u).Distinct().Order().ToList();
        var inWorld = Session("player");
        var onRoleScreen = Session("lobby");

        var actual = await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            var text = new StringBuilder();
            foreach (uint id in ids)
                text.Append(id).Append('\t').Append(Describe(handler.Admit(inWorld, id, ReadOnlyMemory<byte>.Empty)))
                    .Append('\t').Append(handler.Routes.Accepts(onRoleScreen, id)
                        ? Describe(handler.Admit(onRoleScreen, id, ReadOnlyMemory<byte>.Empty)) : "refused").Append('\n');
            return Task.FromResult(text.ToString());
        });

        string file = Path.Combine(ClientData.RepoRoot, "tests", "Ro3Emu.Tests", "Snapshots", "dispatcher-admission.txt");
        if (Environment.GetEnvironmentVariable("RO3EMU_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, actual);
        }
        string expected = File.ReadAllText(file).Replace("\r\n", "\n");
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var changed = actualLines.Zip(expectedLines).Where(p => p.First != p.Second).Take(20)
            .Select(p => $"was {p.Second}\nnow {p.First}").ToList();
        Assert.True(changed.Count == 0 && actualLines.Length == expectedLines.Length,
            "admission changed:\n" + string.Join("\n", changed));
    }

    private static string Describe(Admission a) => a.Mode switch
    {
        AdmissionMode.Session => $"session {string.Join(",", a.Keys.Select(Key))} {a.RealmLabel}",
        _ => $"{a.Mode.ToString().ToLowerInvariant()} {a.Origin}",
    };

    /// <summary>A key without the ids that change from run to run.</summary>
    private static string Key(ReservationKey key) => key.Kind switch
    {
        ReservationKind.RolePart => $"role:{key.Part}",
        ReservationKind.RoleLifecycle => "lifecycle",
        ReservationKind.AccountAttachment => "account",
        _ => $"{key.Kind}:{key.Id}:{key.Part}",
    };
}
