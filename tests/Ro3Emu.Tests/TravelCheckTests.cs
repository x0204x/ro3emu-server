using System.Text.Json.Nodes;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Xunit;

namespace Ro3Emu.Tests;

/// <summary>
/// The raw 30012 and 30131 a client may send (SceneService.Travel): one row per sender of the client's Lua, plus jumps it
/// has no sender for. The synthetic tests run in CI; the client-data ones hold the rule to the real tables.
/// </summary>
public sealed class TravelCheckTests
{
    private const long Town = SyntheticData.Town, Field = 10112, TerritoryWars = 30301, Dungeon = 1000101, RealField = 10113;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    private static Task<SyntheticHost> StartAsync(string mode = "enforce", string? gameData = null) =>
        SyntheticHost.StartAsync(o =>
        {
            o.TravelCheck = mode;
            if (gameData != null) o.GameDataDir = gameData;
        });

    private static GameSession Session(SyntheticHost host, BotClient bot) =>
        host.Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private static long Refusals(SyntheticHost host, BotClient bot) =>
        host.Service<CheatMonitor>().LoggedCount(bot.Account!, CheatMonitor.Travel);

    private static Task<T> OnLoop<T>(SyntheticHost host, Func<T> read) =>
        host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private static async Task<T> Next<T>(BotClient bot, uint msgId, MessageParser<T> parser) where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId), Wait);
        return parser.ParseFrom(e.Body);
    }

    private static async Task<int> TeleportAsync(BotClient bot, long target)
    {
        await bot.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = target });
        return (await Next(bot, SceneService.SceneTeleport, SceneTeleportRsp.Parser)).Result;
    }

    /// <summary>The rule's answer for a role standing in a scene, on foot or flying.</summary>
    private static Task<bool> AllowedAsync(SyntheticHost host, GameSession s, long from, bool flying, long target) =>
        OnLoop(host, () =>
        {
            var (scene, flight) = (s.SceneEntry, s.WorldState.Flying);
            (s.SceneEntry, s.WorldState.Flying) = (from, flying);
            bool allowed = host.Service<SceneService>().TravelAllowed(s, target);
            (s.SceneEntry, s.WorldState.Flying) = (scene, flight);
            return allowed;
        });

    private static async Task AssertTableAsync(SyntheticHost host, GameSession s,
        params (string Sender, long From, bool Flying, long Target, bool Allowed)[] table)
    {
        foreach (var row in table)
            Assert.True(row.Allowed == await AllowedAsync(host, s, row.From, row.Flying, row.Target), row.Sender);
    }

    /// <summary>The fixture with a Territory Wars map and an authored 141 row, which no other test needs.</summary>
    private static string FixtureWithTravelRows()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ro3-travel-" + Guid.NewGuid().ToString("N"));
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string copy = Path.Combine(dir, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        string maps = Path.Combine(dir, "config", "MapConfig.json");
        var rows = JsonNode.Parse(File.ReadAllText(maps))!.AsObject();
        rows["30301"] = new JsonObject { ["_iID"] = TerritoryWars, ["_iSceneType"] = 8, ["_kResSceneEditorName"] = "synthetic_field" };
        File.WriteAllText(maps, rows.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "config", "TypewriterConfig.json"),
            """{ "0": { "_iID": 0, "_kAction": [ [ "141", "10112", "7", "2", "0", "0" ], [ "3000", "10111", "8" ] ] } }""");
        return dir;
    }

    [Fact]
    public async Task EverySenderOfTheFixtureIsAllowedAndARawJumpIsNot()
    {
        string data = FixtureWithTravelRows();
        try
        {
            await using var host = await StartAsync(gameData: data);
            await using var bot = await host.InWorldAsync("Table");
            await AssertTableAsync(host, Session(host, bot),
                ("a failed load's retry", Town, false, Town, true),
                ("a failed load, leaving Territory Wars, auto battle, the idle tip", Field, false, 10100, true),
                ("EnterTerritoryWarsMap", Town, false, TerritoryWars, true),
                ("the GvG transfer from a dungeon", Dungeon, false, 10110, true),
                ("the GvG transfer from Territory Wars", TerritoryWars, false, 10110, true),
                ("FindPathAcrossMapNew in flight", Town, true, Field, true),
                ("a raw jump on foot", Town, false, Field, false),
                ("the GvG target from a field", Town, false, 10110, false));

            Assert.Equal([(Field, 7L)], await OnLoop(host, () => host.Service<SceneService>().AuthoredJumps()));
        }
        finally
        {
            Directory.Delete(data, true);
        }
    }

    [Fact]
    public async Task ARefusedJumpSpendsNothingAndLeavesTheRoleUsable()
    {
        await using var host = await StartAsync();
        await using var bot = await host.InWorldAsync("Jumper");
        var s = Session(host, bot);
        int items = await OnLoop(host, () => s.Items.Count);

        Assert.Equal(ErrorCodes.Parameter, await TeleportAsync(bot, Field));

        Assert.Equal((Town, false, items), await OnLoop(host, () => (s.SceneEntry, s.SceneLoading, s.Items.Count)));
        Assert.Equal(1, Refusals(host, bot));

        // The client's retry of its own map still goes through, and is the first scene change the bot sees.
        Assert.Equal(0, await TeleportAsync(bot, Town));
        Assert.Equal(Town, (await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).SceneEntry);
        Assert.Equal(1, Refusals(host, bot));
    }

    [Fact]
    public async Task AFlyingRoleCrossesMaps()
    {
        await using var host = await StartAsync();
        await using var bot = await host.InWorldAsync("Flyer");
        var s = Session(host, bot);
        await OnLoop(host, () => s.WorldState.Flying = true);

        Assert.Equal(0, await TeleportAsync(bot, Field));

        Assert.Equal(Field, (await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).SceneEntry);
        Assert.Equal(0, Refusals(host, bot));
    }

    [Theory]
    [InlineData("log", 1)]
    [InlineData("off", 0)]
    public async Task TheOtherModesLetAJumpThrough(string mode, long counted)
    {
        await using var host = await StartAsync(mode);
        await using var bot = await host.InWorldAsync("Mode" + mode);

        Assert.Equal(0, await TeleportAsync(bot, Field));

        Assert.Equal(Field, (await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).SceneEntry);
        Assert.Equal(counted, Refusals(host, bot));
    }

    [ClientDataFact]
    public async Task EverySenderOnTheClientsTablesIsAllowedAndARawJumpIsNot()
    {
        await using var host = await StartAsync(gameData: Path.Combine(RepoRoot(), "data"));
        await using var bot = await host.InWorldAsync("Table");
        var data = host.Service<GameData>();
        var parameters = data.Table("PublicParamConfig");
        long autoBattle = parameters["AutoBattleAutoBackMainCityID"].GetInt64(), idle = parameters["OnHookReturnMap"][0].GetInt64();
        long cave = MapConfig.All(data).First(m => m.SceneType == 6).Id;
        await AssertTableAsync(host, Session(host, bot),
            ("LC_TransitionBase: a failed load", RealField, false, 10100, true),
            ("LC_TerritoryWarsServer: leave", TerritoryWars, false, 10100, true),
            ("LC_TerritoryWarsServer: EnterTerritoryWarsMap", RealField, false, TerritoryWars, true),
            ("UI_Sys_GVG_TransferNodeWidgetCtrl, LC_DunGeon_GVG", Dungeon, false, 10110, true),
            ("LC_DAL_AutoAttack: ReturnMainCity", Dungeon, false, autoBattle, true),
            ("UI_Sys_Main_OnHookTipWidgetCtrl: Return", RealField, false, idle, true),
            ("LC_Actor_ControlMove: FindPathAcrossMapNew in flight", RealField, true, 10220, true),
            ("a retry of the role's map", RealField, false, RealField, true),
            ("a raw jump between fields on foot", RealField, false, 10220, false),
            ("a raw jump out of a dungeon on foot", Dungeon, false, RealField, false),
            ("a raw jump to a cave on foot", 10100, false, cave, false),
            ("the GvG target from a field", RealField, false, 10110, false));
    }

    /// <summary>The rows of TaskConfig, DialogueConfig and TypewriterConfig are what the client can send, and another point of
    /// such a map is refused. A few rows name a point their scene file does not have, which is answered as it always was.</summary>
    [ClientDataFact]
    public async Task EveryAuthoredJumpIsAllowedAndAnotherPointIsNot()
    {
        await using var host = await StartAsync(gameData: Path.Combine(RepoRoot(), "data"));
        await using var bot = await host.InWorldAsync("Jumps");
        var world = host.Service<WorldData>();
        var jumps = await OnLoop(host, () => host.Service<SceneService>().AuthoredJumps());

        Assert.Contains((10220L, 1010212001L), jumps);
        Assert.True(jumps.Count >= 20, $"{jumps.Count} jumps");
        Assert.True(jumps.Count(jump => world.Map(jump.Scene)?.Points.ContainsKey(jump.Point) == true) >= 15);

        long other = world.Map(10220)!.Points.Keys.First(p => !jumps.Contains((10220, p)));
        await bot.SendGameAsync(SceneService.CustomJumpMap, new ClientCustomJumpMapReq { SceneId = 10220, PointId = other });
        Assert.Equal(ErrorCodes.Parameter, (await Next(bot, SceneService.CustomJumpMap, ClientCustomJumpMapResp.Parser)).Result);
        Assert.Equal(1, Refusals(host, bot));

        await bot.SendGameAsync(SceneService.CustomJumpMap, new ClientCustomJumpMapReq { SceneId = 10220, PointId = 1010212001 });
        Assert.Equal(0, (await Next(bot, SceneService.CustomJumpMap, ClientCustomJumpMapResp.Parser)).Result);
        Assert.Equal(10220, (await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).SceneEntry);
    }

    /// <summary>A raw 32700 at a portal or Kafra has its own route around the check on 30012: the client's "Cannot teleport
    /// while dead" holds there too.</summary>
    [ClientDataFact]
    public async Task ADeadRoleCannotUseAPortal()
    {
        await using var host = await StartAsync(gameData: Path.Combine(RepoRoot(), "data"));
        await using var bot = await host.InWorldAsync("Ghost");
        var s = Session(host, bot);
        var portal = host.Service<WorldData>().Map(10111)!.Npcs.First(n => n.Kind == WorldNpcKind.Portal);
        await OnLoop(host, () => s.Dead = true);

        await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = portal.Uid, InteractionId = 8, SceneConfigId = 10111 });

        Assert.Equal(ErrorCodes.TeleportDead, (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        Assert.Equal(10111, await OnLoop(host, () => s.SceneEntry));
    }

    private static string RepoRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
