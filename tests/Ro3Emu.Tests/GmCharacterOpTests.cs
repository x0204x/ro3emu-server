using Microsoft.AspNetCore.Builder;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// GM panel (30300) character, gear, quest, dungeon, guild, mail, position and server ops (GmCommands.Character): each
/// answers with its result or a refusal that says why, never "not implemented".
/// </summary>
public sealed class GmCharacterOpTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-gmchar-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
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

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    /// <summary>One panel op and its 30300 reply text.</summary>
    private static async Task<string> Op(BotClient bot, int op, string param = "")
    {
        await bot.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)op, ParamString = param });
        return (await Next(bot, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser)).ErrInfo;
    }

    [ClientDataFact]
    public async Task StrengthenAndRefineSetTheSlotsGridLevels()
    {
        await using var alice = await InWorld("Alice");
        await Op(alice, 24, "40,1"); // levels 1-3 need base level
        Assert.Equal("Strengthen +3 on grids 1,2.", await Op(alice, 4, "1,3"));
        // 5007 carries the off hand's new level too.
        await Next(alice, CharacterService.EquipSync, DBEquipInfoIncrement.Parser,
            m => m.ItemsStrength.TryGetValue(2, out var level) && (level.All == 3 || level.Inc == 3));
        // An empty level is 1; slot 2 is the armor grid (3).
        Assert.Equal("Refine +1 on grids 3.", await Op(alice, 5, "2,"));
        Assert.StartsWith("No strengthen level 999", await Op(alice, 4, "1,999"));
        Assert.StartsWith("Bad parameters", await Op(alice, 4, "7,1"));
    }

    [ClientDataFact]
    public async Task StatResetSkillsAndRoleAttr()
    {
        await using var alice = await InWorld("Alice");
        await Op(alice, 8, "10");
        Assert.Contains("reset", await Op(alice, 9));
        Assert.Equal(0, (await Next(alice, CharacterService.ResetAttrPoints, ResetAttrPointResp.Parser)).Result);
        Assert.Matches(@"^[1-9]\d* skills learned at their max level", await Op(alice, 110, "1"));
        Assert.Equal("Skills cleared to their free levels.", await Op(alice, 110, "0"));
        // GMRoleAttrSet rows carry no columns in the export.
        Assert.Contains("server-side table", await Op(alice, 301, "431,1,0,1"));
    }

    [ClientDataFact]
    public async Task TaskStatesMapToTheQuestPaths()
    {
        await using var alice = await InWorld("Alice");
        Assert.StartsWith("accept 102020", await Op(alice, 99, "0,102020"));
        Assert.StartsWith("drop 102020", await Op(alice, 99, "1,102020"));
        Assert.Equal("Quest 102020 is not running.", await Op(alice, 99, "3,102020"));
        Assert.Equal("No quest 1.", await Op(alice, 99, "4,1"));
        Assert.StartsWith("Bad parameters", await Op(alice, 99, "9,102020"));
    }

    [ClientDataFact]
    public async Task DungeonGuildAndPosition()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        Assert.Equal("No dungeon 1.", await Op(alice, 108, "1"));
        Assert.Contains("entry requested", await Op(alice, 108, "2100001"));
        Assert.Equal("Not in a dungeon.", await Op(bob, 93, "leave"));
        Assert.Equal("Enter needs a dungeon id: use GmEnterDungeon.", await Op(bob, 93, "enter"));

        Assert.Contains("not open", await Op(bob, 94, "create,GmGuild"));
        await Op(bob, 24, "40,1");
        Assert.Matches(@"^Guild GmGuild \(\d+\) created\.$", await Op(bob, 94, "create,GmGuild"));
        Assert.StartsWith("Already in guild GmGuild", await Op(bob, 94, "create,Other"));
        Assert.Contains("!league", await Op(bob, 94, "leaguematch,3"));

        Assert.StartsWith("Scene ", await Op(bob, 79));
        Assert.StartsWith("Flew to " + bob.Self!.Uuid, await Op(alice, 83, bob.Self.Uuid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal("No entity 7.", await Op(alice, 83, "7"));
    }

    [ClientDataFact]
    public async Task MailAndDropPools()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        Assert.Matches(@"^Mail \d+ delivered with 5 .+ \(1000000\)\.$", await Op(alice, 70, "Hello,Body,1,0,,1,1000000:5"));
        Assert.StartsWith("Bad parameters", await Op(alice, 70, "Hello,Body,2,0,,,"));
        Assert.Equal("MailConfig 2 delivered to 2 online roles.", await Op(alice, 71, "2,"));
        Assert.Equal("No MailConfig 999999.", await Op(alice, 71, "999999,"));
        Assert.Equal("Mail delivered to 2 online roles.", await Op(alice, 72, "All,Body,1,0,7,,"));
        Assert.Contains("no record of server-wide mail", await Op(alice, 73, "1,1"));

        // 21001: MVP first strike, 30 x 1000 Minted Coin at 100%.
        Assert.StartsWith("Drop pool 21001: ", await Op(alice, 26, "21001"));
        Assert.Equal("No drop pool 1.", await Op(alice, 26, "1"));
    }

    [ClientDataFact]
    public async Task ServerOpsAnswerOrRefuseWithAReason()
    {
        await using var alice = await InWorld("Alice");
        Assert.Contains("level cap 99", await Op(alice, 64));
        Assert.Contains("host clock", await Op(alice, 62, "2026-10-29 12:00:00"));
        Assert.Contains("season start", await Op(alice, 63, "2026-10-29"));
        Assert.Contains("only through an account's own login", await Op(alice, 54, "x"));
        Assert.Contains("WalkCheck", await Op(alice, 186, "0"));
        foreach (int tavern in new[] { 134, 136, 137, 138, 139, 140, 141 })
            Assert.Contains("no tavern", await Op(alice, tavern, "1"));
        Assert.Contains("own timeline", await Op(alice, 51, "123"));
        Assert.Contains("requested", await Op(alice, 51, "state"));
        Assert.Contains("Siege TEST AID", BroadCastNotify.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify), Wait)).Body).Content);
        // 135 GMActivityServer is not a tavern op.
        Assert.Contains("not implemented", await Op(alice, 135));
    }
}
