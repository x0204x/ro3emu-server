using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Scenes the game client has no Unity scene for (client-scenes.json, Ro3Emu.DataExport.ClientScenes): the client
/// retries "Failed to load the area" forever there, so no warp sends a player to one, and a character saved on one logs
/// in at the spawn map. Synthetic fixture: the town's field is such a scene.
/// </summary>
public sealed class ClientSceneTests : IAsyncDisposable
{
    private const long Field = 10112;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _gameData = Path.Combine(Path.GetTempPath(), "ro3emu-scenes-" + Guid.NewGuid().ToString("N"));

    /// <summary>The synthetic fixture with a MapResource table, and the field's scene missing from the client.</summary>
    private string GameData()
    {
        foreach (var file in Directory.GetFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_gameData, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        var mapConfig = Path.Combine(_gameData, "config", "MapConfig.json");
        var maps = JsonNode.Parse(File.ReadAllText(mapConfig))!;
        maps[Field.ToString(CultureInfo.InvariantCulture)]!["_iMapResourceID"] = Field;
        maps[SyntheticData.Town.ToString(CultureInfo.InvariantCulture)]!["_iMapResourceID"] = SyntheticData.Town;
        File.WriteAllText(mapConfig, maps.ToJsonString());
        var resources = new JsonObject
        {
            [SyntheticData.Town.ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["_iID"] = SyntheticData.Town, ["_kResource"] = "sc_synthetic_town" },
            [Field.ToString(CultureInfo.InvariantCulture)] = new JsonObject { ["_iID"] = Field, ["_kResource"] = "sc_synthetic_field" },
        };
        File.WriteAllText(Path.Combine(_gameData, "config", "MapResource.json"), resources.ToJsonString());
        File.WriteAllText(Path.Combine(_gameData, WorldData.ClientSceneList), """{"Missing":["sc_synthetic_field"]}""");
        return _gameData;
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_gameData, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private static async Task<string> GmReply(BotClient bot) =>
        BroadCastNotify.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify), Wait)).Body).Content;

    private static bool SwitchTo(BotEvent e, long scene) =>
        e.IsGame(MsgId.SwitchSceneLine) && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == scene;

    [Fact]
    public async Task WarpsToASceneTheClientLacksAreRefusedWithTheReason()
    {
        string data = GameData();
        await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = data);
        await using var alice = await host.InWorldAsync("Alice");
        Assert.False(host.Service<WorldData>().ClientHasScene(Field));
        Assert.True(host.Service<WorldData>().ClientHasScene(SyntheticData.Town));

        using (var http = new HttpClient())
            (await http.PostAsync($"{host.Http}/ro3emu/gm?account=alice", new StringContent($"!warp {Field}"))).EnsureSuccessStatusCode();
        Assert.StartsWith($"Cannot warp to {Field} ", await GmReply(alice));
        await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)31, ParamString = $"{Field},0,0" });
        var panel = GeneralGMProcRsp.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.GeneralProcGm), Wait)).Body).ErrInfo;
        Assert.Contains("the game client has no scene for it", panel);
        // The world map's 30012 travel is refused too, before any map change.
        await alice.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = Field });
        Assert.Equal(ErrorCodes.SceneMissing, SceneTeleportRsp.Parser.ParseFrom(
            (await alice.Inbox.WaitAsync(e => e.IsGame(SceneService.SceneTeleport), Wait)).Body).Result);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => SwitchTo(e, Field));
    }

    [Fact]
    public async Task ACharacterSavedOnASceneTheClientLacksLogsInAtTheSpawnMapAfterARestart()
    {
        string data = GameData();
        await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = data);
        long role;
        await using (var alice = await host.InWorldAsync("Alice"))
        {
            role = await host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(host, "alice").Role!.Id));
            await alice.SwitchRoleAsync();
        }
        // Where the GM warp of the report left it, saved before this fix.
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            host.Service<RoleStore>().SavePosition(role, Field, 1, 0, 1, 0);
            return Task.CompletedTask;
        });
        await host.RestartAsync();

        await using var again = await host.InWorldAsync("Alice", create: false);

        Assert.Contains(again.Inbox.Snapshot(), e => SwitchTo(e, SyntheticData.Town));
        Assert.DoesNotContain(again.Inbox.Snapshot(), e => SwitchTo(e, Field));
        Assert.Equal(SyntheticData.Town, await host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(Session(host, "alice").SceneEntry)));
    }

    private static GameSession Session(SyntheticHost host, string account) =>
        host.Service<SessionRegistry>().All().First(s => s.Account == account && s.Role != null);
}

/// <summary>
/// Glast Heim on the client's own data (issue: "stuck at 80% loading after a GM map teleport to Glast Heim, also after a
/// server restart"). MapConfig 10700 and its nine floors have scene editor files and walk maps, so the server runs the
/// map, but the client has no Unity scene for them (sc_glast_01_001.unity is not in its VFS index).
/// </summary>
public sealed class GlastHeimSceneTests : IAsyncLifetime
{
    private const long GlastHeim = 10700, Prontera = 10100;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-glast-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private string _http = "";

    public async Task InitializeAsync()
    {
        if (!ClientData.Available) return;
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
        if (_app == null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private T Service<T>() where T : notnull => _app!.Services.GetRequiredService<T>();

    /// <summary>What the export writes for the October 2026 client (DataExportTests.TheInstalledClientHasNoSceneForGlastHeim).</summary>
    private void ClientLacksGlastHeim() => Service<WorldData>().UseClientSceneListForTest(
        ["sc_amatsu_001", "sc_louyang_001", "sc_ayothaya_001", "sc_dewata_001", "sc_malaya_001", "sc_glast_01_001", "sc_gl_church_001",
            "sc_gl_cas01_001", "sc_gl_cas02_001", "sc_gl_sew01_001", "sc_gl_knt01_001", "sc_gl_knt02_001", "sc_gl_dun01_001",
            "sc_gl_chyard_001", "sc_gl_prison_001"]);

    private long _role;

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        _role = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(_role);
        return bot;
    }

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private static bool SwitchTo(BotEvent e, long scene) =>
        e.IsGame(MsgId.SwitchSceneLine) && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == scene;

    /// <summary>
    /// The report's failure path, as it was (data exported without the list): the warp sends 70010 for 10700 on line 1,
    /// the client never reports 30005, and its give-up (LC_TransitionBase.LoadNewSceneAsyncFailToLastScene: 30012 to
    /// 10100 after three failed loads) is refused while that map change is pending, so the client reconnects and the
    /// character, saved on 10700, comes back to it.
    /// </summary>
    [ClientDataFact]
    public async Task WithoutTheListAWarpToGlastHeimLeavesTheClientLoadingAMapItCannotShow()
    {
        Service<WorldData>().UseClientSceneListForTest([]);
        await using var alice = await InWorld("Alice");

        await Gm("alice", $"!warp {GlastHeim}");
        var sw = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == GlastHeim);
        Assert.Equal((0, 1), (sw.Result, sw.Linenum));
        await alice.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = Prontera });
        Assert.Equal(ErrorCodes.SwitchingScenes, (await Next(alice, SceneService.SceneTeleport, SceneTeleportRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task GmWarpsToGlastHeimAreRefusedBecauseTheClientHasNoSceneForIt()
    {
        ClientLacksGlastHeim();
        await using var alice = await InWorld("Alice");

        await Gm("alice", $"!warp {GlastHeim}");
        Assert.Equal("Cannot warp to 10700 Glast Heim the Ancient City: the game client has no scene for it (its map files are not in this client), "
            + "so the client would never finish loading.", (await Next(alice, GmCommands.BroadcastNotify, BroadCastNotify.Parser)).Content);
        // The client's GM panel: op 31 GmJumpMap, "map teleport via ID", on a floor of the dungeon.
        await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)31, ParamString = "10791,0,0" });
        Assert.StartsWith("Cannot warp to 10791 Glast Heim Underground Prison Floor 1: the game client has no scene",
            (await Next(alice, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser)).ErrInfo);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => SwitchTo(e, GlastHeim) || SwitchTo(e, 10791));

        // A map the client has still goes.
        await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)31, ParamString = $"{Prontera},0,0" });
        Assert.Equal(0, (await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == Prontera)).Result);
    }

    [ClientDataFact]
    public async Task ACharacterSavedInGlastHeimLogsInAtTheSpawnMap()
    {
        ClientLacksGlastHeim();
        await using var alice = await InWorld("Alice");
        long role = _role;
        await alice.SwitchRoleAsync();
        await Service<GameLoop>().InvokeAsync(() =>
        {
            Service<RoleStore>().SavePosition(role, GlastHeim, 207.89f, 9.14f, 281.95f, 0);
            return Task.CompletedTask;
        });

        await alice.EnterWorldAsync(role);

        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => SwitchTo(e, GlastHeim));
        Assert.Contains(alice.Inbox.Snapshot(), e => SwitchTo(e, RoleStore.SpawnSceneEntry));
    }
}
