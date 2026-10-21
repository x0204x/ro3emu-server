using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Real authored status/vehicle templates with a test-only scene-editor placement, over the game wire.</summary>
public sealed class WorldInteractionTests : IAsyncLifetime
{
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), "ro3emu-interact-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    public async Task InitializeAsync()
    {
        var fixture = Path.Combine(_stateDir, "fixture");
        var config = Path.Combine(fixture, "config");
        Directory.CreateDirectory(config);
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "data", "config"), "*.json"))
            File.Copy(file, Path.Combine(config, Path.GetFileName(file)));
        var sceneDir = Path.Combine(fixture, "sceneeditor");
        Directory.CreateDirectory(sceneDir);
        var source = Path.Combine(RepoRoot(), "data", "sceneeditor", "sc_prt_fild08_001_sceneeditor.json");
        var scene = JsonNode.Parse(await File.ReadAllTextAsync(source))!.AsObject();
        var npcs = scene["kNpcList"]!.AsObject();
        npcs["900001"] = new JsonObject
        {
            ["Id"] = 900001, ["NpcTag"] = 2, ["IsServerNpc"] = true,
            ["NpcId"] = 7100004, ["x"] = 347.26, ["y"] = -8.42, ["z"] = 294.01, ["FaceDirection"] = 180
        };
        npcs["900002"] = new JsonObject
        {
            ["Id"] = 900002, ["NpcTag"] = 2, ["IsServerNpc"] = true,
            ["NpcId"] = 400140, ["x"] = 349.26, ["y"] = -8.42, ["z"] = 294.01, ["FaceDirection"] = 180
        };
        await File.WriteAllTextAsync(Path.Combine(sceneDir, "sc_prt_fild08_001_sceneeditor.json"), scene.ToJsonString());

        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _stateDir; o.GameDataDir = fixture;
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_stateDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_stateDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id); return bot;
    }
    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(msgId), Wait)).Body);
    private long Npc(int template) => _app.Services.GetRequiredService<WorldData>().Map(10111)!.Npcs.Single(n => n.NpcId == template).Uid;
    private static NpcInteractionReq Request(long uid, int id) => new() { NpcUid = uid, InteractionId = id, SceneConfigId = 10111 };

    [ClientDataFact]
    public async Task SitStatusEntersAndQuitsAndRefusesDuplicateOrForeignAction()
    {
        await using var bot = await InWorld("Alice");
        long sit = Npc(7100004);
        await bot.SendGameAsync(SceneService.NpcInteraction, Request(sit, 7100004));
        Assert.Equal(0, (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        var changed = await Next(bot, WorldInteractionService.Change, InteractChangeNtf.Parser);
        Assert.Equal(InteractInfo.Types.EState.Sit, changed.DataInfo.EType);
        Assert.Equal(sit, changed.DataInfo.ActiveObjId);
        Assert.Equal(7100004, changed.DataInfo.InteractConfigId);

        await bot.SendGameAsync(SceneService.NpcInteraction, Request(sit, 7100004));
        Assert.Equal(ErrorCodes.NpcParameter, (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await bot.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = 26 });
        Assert.Equal(ErrorCodes.NpcParameter, (await Next(bot, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result);
        await bot.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = 42 });
        Assert.Equal(InteractInfo.Types.EState.Idle,
            (await Next(bot, WorldInteractionService.Change, InteractChangeNtf.Parser)).DataInfo.EType);
        Assert.Equal(0, (await Next(bot, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result);
    }

    [ClientDataFact]
    public async Task VehicleHasExclusiveSeatAndDriverOnlyFacing()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long vehicle = Npc(400140);
        await alice.SendGameAsync(SceneService.NpcInteraction, Request(vehicle, 5400001));
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        Assert.Equal(InteractInfo.Types.EState.Vehicle,
            (await Next(alice, WorldInteractionService.Change, InteractChangeNtf.Parser)).DataInfo.EType);
        Assert.Equal(29, (await Next(alice, WorldInteractionService.Change3C, Change3CNtf.Parser)).CfgId);

        await bob.SendGameAsync(SceneService.NpcInteraction, Request(vehicle, 5400001));
        Assert.Equal(ErrorCodes.NpcParameter, (await Next(bob, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await alice.SendGameAsync(WorldInteractionService.SeatChange, new ChangeVehicleSeatReq { SeatIdx = 9 });
        Assert.Equal(ErrorCodes.NpcParameter,
            (await Next(alice, WorldInteractionService.SeatChange, ChangeVehicleSeatRes.Parser)).Result);
        await alice.SendGameAsync(WorldInteractionService.VehicleSync, new SyncVehicleReq { Face = 73 });
        var turn = await Next(bob, GameAoiSink.EntityFaceToAllId, EntityFaceToAll.Parser);
        Assert.Equal(73, turn.Face);
        await alice.SendGameAsync(WorldInteractionService.Action, new UseInteractActionReq { ButtonId = 26 });
        Assert.Equal(0, (await Next(alice, WorldInteractionService.Action, UseInteractActionRes.Parser)).Result);
        await bob.SendGameAsync(SceneService.NpcInteraction, Request(vehicle, 5400001));
        Assert.Equal(0, (await Next(bob, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository data/config");
    }
}
