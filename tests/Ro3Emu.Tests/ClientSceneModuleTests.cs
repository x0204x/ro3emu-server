using System.Net;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using static Ro3Emu.Tests.PanelTests;

namespace Ro3Emu.Tests;

/// <summary>
/// Maps whose Unity scene the client has but whose scene-editor module its Lua loader refuses (client-scenes.json
/// UnloadableModules, Ro3Emu.DataExport.ClientScenes): the loading screen stops at 85% and never sends 30005. Report: a
/// panel teleport to Gonryun left the player on the loading screen. Synthetic fixture: the town's field is such a map.
/// </summary>
public sealed class ClientSceneModuleTests : IAsyncDisposable
{
    private const long Field = 10112;
    private const string Admin = "scene-admin";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _gameData = Path.Combine(Path.GetTempPath(), "ro3emu-modules-" + Guid.NewGuid().ToString("N"));

    /// <summary>The synthetic fixture, with the field's scene-editor module on the export's list.</summary>
    private string GameData()
    {
        foreach (var file in Directory.GetFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(_gameData, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        File.WriteAllText(Path.Combine(_gameData, WorldData.ClientSceneList), """{"Missing":[],"UnloadableModules":["synthetic_field"]}""");
        return _gameData;
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_gameData, true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private static bool SwitchTo(BotEvent e, long scene) =>
        e.IsGame(MsgId.SwitchSceneLine) && SwitchSceneLineResp.Parser.ParseFrom(e.Body).SceneEntry == scene;

    [Fact]
    public async Task EveryWarpToAMapWhoseModuleTheClientCannotLoadIsRefusedWithTheReason()
    {
        string data = GameData();
        await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = data);
        await using var alice = await host.InWorldAsync("Alice");

        using (var http = new HttpClient())
            (await http.PostAsync($"{host.Http}/ro3emu/gm?account=alice", Operator.Command($"!warp {Field}"))).EnsureSuccessStatusCode();
        string reply = BroadCastNotify.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify), Wait)).Body).Content;
        Assert.StartsWith($"Cannot warp to {Field} ", reply);
        Assert.Contains("cannot load its map script synthetic_field", reply);
        // The client's GM panel: op 31 GmJumpMap.
        await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)31, ParamString = $"{Field},0,0" });
        Assert.Contains("cannot load its map script", GeneralGMProcRsp.Parser.ParseFrom(
            (await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.GeneralProcGm), Wait)).Body).ErrInfo);
        // The world map's 30012 travel.
        await alice.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = Field });
        Assert.Equal(ErrorCodes.SceneMissing, SceneTeleportRsp.Parser.ParseFrom(
            (await alice.Inbox.WaitAsync(e => e.IsGame(SceneService.SceneTeleport), Wait)).Body).Result);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => SwitchTo(e, Field));
    }

    /// <summary>Where the report's teleport left the character: its saved position is on the map, so every login loaded
    /// it again. It now comes back on the spawn map and is saved there.</summary>
    [Fact]
    public async Task ACharacterSavedOnSuchAMapLogsInAtTheSpawnMapAndIsSavedThere()
    {
        string data = GameData();
        await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = data);
        long role;
        await using (var alice = await host.InWorldAsync("Alice"))
        {
            role = Session(host, "alice").Role!.Id;
            await alice.SwitchRoleAsync();
        }
        await host.Service<GameLoop>().InvokeAsync(() =>
        {
            host.Service<RoleStore>().SavePosition(role, Field, 1, 0, 1, 0);
            return Task.CompletedTask;
        });
        await host.RestartAsync();

        await using (var again = await host.InWorldAsync("Alice", create: false))
        {
            Assert.Contains(again.Inbox.Snapshot(), e => SwitchTo(e, SyntheticData.Town));
            Assert.DoesNotContain(again.Inbox.Snapshot(), e => SwitchTo(e, Field));
            await again.SwitchRoleAsync();
        }
        var until = DateTime.UtcNow + Wait;
        while (host.Service<RoleStore>().Get(role)!.SceneEntry != SyntheticData.Town && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.Equal(SyntheticData.Town, host.Service<RoleStore>().Get(role)!.SceneEntry);
    }

    [Fact]
    public async Task ThePanelMarksSuchAMapAndRefusesATeleportThereInAndOutOfTheGame()
    {
        string data = GameData();
        var (host, panel) = await StartAsync(o =>
        {
            o.GmAccounts = [Admin];
            o.GameDataDir = data;
        });
        await using var hosted = host;
        Assert.Equal(0, await SdkLoginAsync(host, Admin, "pw"));
        using var admin = new PanelBrowser(panel);
        Assert.Equal(HttpStatusCode.OK, (await admin.LoginAsync(Admin, "pw")).StatusCode);

        // The list behind the teleport form: 10112 is the fixture's only named map.
        var map = Assert.Single((await admin.GetJsonAsync("/api/maps")).GetProperty("maps").EnumerateArray());
        Assert.Equal(Field, map.GetProperty("id").GetInt64());
        Assert.False(map.GetProperty("enterable").GetBoolean());

        await using var alice = await host.InWorldAsync("Alice");
        long role = Session(host, "alice").Role!.Id;
        var online = await admin.PostAsync($"/api/admin/characters/{role}/teleport", new { map = Field });
        Assert.Equal(HttpStatusCode.Conflict, online.StatusCode);
        Assert.Contains("cannot load its map script", await online.Content.ReadAsStringAsync());
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => SwitchTo(e, Field));

        await alice.SwitchRoleAsync();
        // Whether the logout has finished or not, the map is refused first.
        var offline = await admin.PostAsync($"/api/admin/characters/{role}/teleport", new { map = Field });
        Assert.Equal(HttpStatusCode.Conflict, offline.StatusCode);
        Assert.Contains("cannot load its map script", await offline.Content.ReadAsStringAsync());
        Assert.Equal(SyntheticData.Town, host.Service<RoleStore>().Get(role)!.SceneEntry);
    }

    private static GameSession Session(SyntheticHost host, string account) =>
        host.Service<SessionRegistry>().All().First(s => s.Account == account && s.Role != null);
}
