using Microsoft.AspNetCore.Builder;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class CommonWorldTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-common-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.GmAccounts = ["alice"];
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

    private async Task<(BotClient Bot, long Id)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name, 1)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(5))).Body);

    [Fact]
    public async Task AuthenticatedLocalGmInspectAndExplicitLegacyRefusal()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, _) = await InWorld("Bob");
        await using var _a = alice;
        await using var _b = bob;

        await alice.SendGameAsync(WorldCommonService.OnlineStatusHeart, new NullArg());
        var presence = await Next(alice, WorldCommonService.OnlineStatusHeart, OnlineClientHeartResp.Parser);
        Assert.Contains(aliceId, presence.AllInfo.AllOnlineId);
        Assert.Equal(2, presence.AllInfo.AllOnlineId.Count);

        await alice.SendGameAsync(WorldCommonService.Inspect, new InspectCommandReq
        { ServerEntityType = 3, ServerEntityId = aliceId, Cmd = "height" });
        var height = await Next(alice, WorldCommonService.DebugHeight, DebugShowHeightNtf.Parser);
        Assert.True(height.Clear);
        Assert.Single(height.Data);
        var own = await Next(alice, WorldCommonService.Inspect, InspectCommandResp.Parser);
        Assert.Contains($"uid={aliceId}", own.Res);

        await bob.SendGameAsync(WorldCommonService.Inspect, new InspectCommandReq
        { ServerEntityType = 0, ServerEntityId = aliceId, Cmd = "summary" });
        Assert.Equal("GM inspect denied.", (await Next(bob, WorldCommonService.Inspect, InspectCommandResp.Parser)).Res);

        await alice.SendGameAsync(WorldCommonService.GmTransmit, new GMCommandTransmitReq { LPlayerId = aliceId, Data = "!where" });
        Assert.Contains("Scene", (await Next(alice, GmCommands.BroadcastNotify, BroadCastNotify.Parser)).Content);
        await Next(alice, WorldCommonService.GmTransmit, UnUsedRsp.Parser);

        await bob.SendGameAsync(WorldCommonService.GmTransmit, new GMCommandTransmitReq { Data = "!where" });
        Assert.Contains("denied", (await Next(bob, GmCommands.BroadcastNotify, BroadCastNotify.Parser)).Content);
        await Next(bob, WorldCommonService.GmTransmit, UnUsedRsp.Parser);

        await alice.SendGameAsync(WorldCommonService.LegacyGmProc, new GMProcReq { GmType = 1 });
        Assert.NotEqual(0, (await Next(alice, WorldCommonService.LegacyGmProc, GMProcRes.Parser)).Result);
        await bob.SendGameAsync(WorldCommonService.LegacyGmProc, new GMProcReq { GmType = 1 });
        Assert.Equal(ErrorCodes.NoPermission, (await Next(bob, WorldCommonService.LegacyGmProc, GMProcRes.Parser)).Result);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
