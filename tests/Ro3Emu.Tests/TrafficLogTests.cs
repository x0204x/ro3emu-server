using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class TrafficLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3-traffic-" + Guid.NewGuid().ToString("N"));

    private TrafficLog Create(string mode)
    {
        var options = Options.Create(new ServerOptions { DataDir = _dir, TrafficLog = mode });
        // The message names/request types come from the shared msgtable.tsv.
        var catalog = new MsgCatalog(Options.Create(new ServerOptions { DataDir = RepoData() }), NullLogger<MsgCatalog>.Instance);
        return new TrafficLog(options, catalog);
    }

    private static string RepoData()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) return Path.Combine(d.FullName, "data");
        throw new DirectoryNotFoundException("repository root");
    }

    private string[] Lines() => Directory.Exists(Path.Combine(_dir, "logs"))
        ? Directory.GetFiles(Path.Combine(_dir, "logs"), "traffic-*.jsonl").SelectMany(File.ReadAllLines).ToArray() : [];

    [ClientDataFact]
    public void OnModeFormatsBothDirectionsOffTheCallerThread()
    {
        var log = Create("on");
        var hp = new SyncClientPlayerReviveInfoNtf { BDead = true, SceneEntry = 10101 };
        log.ServerToClient("alice", 32802, ((IMessage)hp).Descriptor, hp.ToByteArray());
        var buffer = new SceneLoadedReq { SceneId = 10101 }.ToByteArray();
        log.ClientToServer("alice", MsgId.SceneLoaded, buffer);
        Array.Clear(buffer); // a reused receive buffer must not change what was logged
        log.Rpc("alice", "c2s-rpc", "Ping", () => "1, 2");
        log.Dispose();
        var lines = Lines();
        Assert.True(lines.Length == 3, string.Join(" | ", lines));
        Assert.True(lines[1].Contains("sceneId"), lines[1]);
        Assert.Contains("\"dir\":\"s2c\",\"id\":32802", lines[0]);
        Assert.Matches("\"sceneEntry\": \"?10101", lines[0]);
        Assert.Matches("\"sceneId\": \"?10101", lines[1]);
        Assert.Contains("\"name\":\"Ping\",\"body\":\"1, 2\"", lines[2]);
    }

    [Fact]
    public void AccountListLogsOnlyThoseAccountsAndOffWritesNothing()
    {
        var log = Create("alice, carol");
        Assert.True(log.Enabled("ALICE"));
        Assert.False(log.Enabled("bob"));
        log.ClientToServer("bob", MsgId.SceneLoaded, new byte[] { 8, 1 });
        log.ClientToServer("carol", MsgId.SceneLoaded, new byte[] { 8, 1 });
        log.Dispose();
        Assert.Single(Lines());

        Directory.Delete(_dir, true);
        var off = Create("off");
        Assert.False(off.Enabled("alice"));
        off.ClientToServer("alice", MsgId.SceneLoaded, new byte[] { 8, 1 });
        off.Dispose();
        Assert.Empty(Lines());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [ClientDataFact]
    public void OnlyALoadedCatalogCallsAnIdUnknownToTheClient()
    {
        // Audit 11.10: the dispatcher sends unknown ids to a runtime scope instead of the manifest's Realm default.
        var loaded = new MsgCatalog(Options.Create(new ServerOptions { DataDir = RepoData() }), NullLogger<MsgCatalog>.Instance);
        Assert.False(loaded.UnknownToClient(MsgId.SceneLoaded));
        Assert.True(loaded.UnknownToClient(4_000_000_001));
        var missing = new MsgCatalog(Options.Create(new ServerOptions { DataDir = _dir }), NullLogger<MsgCatalog>.Instance);
        Assert.False(missing.UnknownToClient(4_000_000_001)); // no table: nothing is called unknown
    }
}
