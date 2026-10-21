using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Quest targets of the Growth Classroom's Pet (1004), Divine Totem (1005), Soul Echo (1006) and Equip (1002) modules
/// (TaskObjectives.cs), driven by the real requests: each module's first stage completes and opens the next.
/// </summary>
public sealed class GrowthObjectiveTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-growthobj-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task<BotClient> Relog(BotClient bot)
    {
        await bot.DisposeAsync();
        var deadline = DateTime.UtcNow + Wait;
        while (Service<SessionRegistry>().All().Any(s => s.Account == "alice" && s.Role != null) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await Task.Delay(200);
        return await InWorld(create: false);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account=alice", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);

    private async Task Until(Func<GameSession, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await Read(() => condition(Session)))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(25);
        }
    }

    private Task At(long task, long progress) => Until(s => s.Tasks!.Infos.TryGetValue(task, out var q)
        && q.Status is TaskState.Running or TaskState.Finish && q.CompleteNum[0] == progress, $"{task} at {progress}");

    private Task Delivered(long task) => Until(s => TaskService.IsDelivered(s, task), $"{task} delivered");

    private Task<long> Progress(long task) => Read(() => Session.Tasks!.Infos[task].CompleteNum[0]);

    private Task<int> Stage(long module) => Read(() => Service<TaskService>().GrowthStage(Session, module));

    private async Task<long> Give(int entry, int count = 1)
    {
        await Gm($"!item {entry} {count}");
        return await Read(() => Session.Items.Last(i => i.Entry == entry).Guid);
    }

    // ---------------------------------------------------------------- pets

    private static async Task CombinePet(BotClient bot, Func<string, Task> gm, int pet, int fragment, int pieces)
    {
        await gm($"!item {fragment} {pieces}");
        await bot.SendGameAsync(PetService.Combine, new PetCombineReq { PetId = pet });
        Assert.Equal(0, (await Next(bot, PetService.Combine, PetCombineResp.Parser, r => r.PetId == pet)).Result);
    }

    private static async Task Deploy(BotClient bot, int pet, int target, int slot)
    {
        await bot.SendGameAsync(PetService.StateUpdate,
            new PetStateUpdateReq { PetId = pet, Target = (PetStateSource_Type)target, TargetSlotIndex = slot });
        Assert.Equal(0, (await Next(bot, PetService.StateUpdate, PetStateUpdateResp.Parser, r => r.Req.PetId == pet)).Result);
    }

    [ClientDataFact]
    public async Task PetTargetsReadTheRosterAndLineupAndTheirStageOpensTheNext()
    {
        var bot = await InWorld();
        await Gm("!quest done 10104330"); // function 33
        await Gm("!lvl 60");
        // Stage 1: 1700300 [306002,0], 1700301 [306003,2], 1700305 [306001,4,0,1], 1700311 [103010,30,1],
        // 1700317 [306001,0,3,1], 1700323 [103009,0,3].
        await At(1700323, 0);
        Assert.Equal(1, await Stage(1004));

        await CombinePet(bot, Gm, 10001, 1610001, 20); // camp 1, blue
        await At(1700323, 1);
        await CombinePet(bot, Gm, 20001, 1620001, 40); // camp 1, purple
        await CombinePet(bot, Gm, 20002, 1620002, 40); // camp 1, purple
        await Delivered(1700323); // three owned pets

        await Deploy(bot, 10001, 1, 1);
        Assert.Equal(0, await Progress(1700305)); // blue is below purple
        Assert.Equal(0, await Progress(1700317)); // star 1
        Assert.Equal(0, await Progress(1700300)); // one camp-1 fighter
        await Deploy(bot, 20001, 1, 2);
        await Delivered(1700305);
        Assert.Equal(0, await Progress(1700300));
        await Deploy(bot, 20002, 1, 3);
        await Delivered(1700300); // three fighters of one camp activate its effect

        await CombinePet(bot, Gm, 11001, 1611001, 20);
        await CombinePet(bot, Gm, 12001, 1612001, 20);
        await Deploy(bot, 11001, 2, 1);
        await At(1700301, 1);
        await Deploy(bot, 12001, 2, 2);
        await Delivered(1700301);

        await Gm("!item 1250703 1");
        for (int i = 0; i < 3; i++)
        {
            await bot.SendGameAsync(PetService.LevelUp, new PetLvUpReq { PetId = 10001, EType = (EPetLvUpType)2 });
            Assert.Equal(0, (await Next(bot, PetService.LevelUp, PetLvUpResp.Parser)).Result);
        }
        await Delivered(1700311); // a pet at level 31

        await Gm("!item 1610001 60");
        foreach (bool star in new[] { false, false, false, true, false, false, false, true })
        {
            if (star)
            {
                await bot.SendGameAsync(PetService.StarUp, new PetStarUpReq { PetId = 10001 });
                Assert.Equal(0, (await Next(bot, PetService.StarUp, PetStarUpResp.Parser)).Result);
            }
            else
            {
                await bot.SendGameAsync(PetService.ProgressUp, new PetProgressUpReq { PetId = 10001 });
                Assert.Equal(0, (await Next(bot, PetService.ProgressUp, PetProgressUpResp.Parser)).Result);
            }
        }
        await Delivered(1700317); // the deployed pet at star 3

        // Every stage-1 quest is delivered: stage 2 opens and reads the same state at once.
        await Until(s => Service<TaskService>().GrowthStage(s, 1004) == 2, "module 1004 stage 2");
        await Delivered(1700306); // [306001,4,0,2]: two purple fighters
        await Delivered(1700324); // [103009,0,4]: five owned pets
        await At(1700302, 2); // [306003,3]
        await At(1700312, 1); // [103010,30,2]
        await At(1700318, 1); // [306001,0,3,2]

        bot = await Relog(bot);
        await At(1700302, 2);
        await At(1700312, 1);
        await At(1700318, 1);
        Assert.Equal(2, await Stage(1004));
        await bot.DisposeAsync();
    }

    // ---------------------------------------------------------------- Divine Totem

    private static async Task<int> Equip(BotClient bot, int slot, long guid)
    {
        await bot.SendGameAsync(82362, new SetUpTotemReq { TotemPos = slot, TotemGuid = guid });
        return (await Next(bot, 82362, SetUpTotemResp.Parser)).Result;
    }

    private static async Task<int> Combine(BotClient bot, long left, long right)
    {
        await bot.SendGameAsync(82366, new CombineTotemReq { LeftTotemGuid = left, RightTotemGuid = right });
        return (await Next(bot, 82366, CombineTotemResp.Parser)).Result;
    }

    [ClientDataFact]
    public async Task TotemTargetsReadTheDeploymentAndCountCombinesAcrossRelog()
    {
        var bot = await InWorld();
        await Gm("!lvl 60"); // function 29 and three slots
        // Stage 1: 1700500 [103035,3,1], 1700506 [315002,3,1], 1700512 [315003,0,1], 1700518 [315004,4,1].
        await At(1700518, 0);
        await Gm("!quest accept 1700519"); // [315004,4,2]
        await Gm("!quest accept 1700501"); // [103035,3,2]

        long first = await Give(17400001); // purple, no skills
        Assert.Equal(0, await Equip(bot, 1, first));
        await Delivered(1700500);
        await At(1700501, 1);
        Assert.Equal(0, await Progress(1700506));
        Assert.Equal(0, await Progress(1700512));

        long book = await Give(10051001); // TotemSkillConfig 10051001, quality 5
        await bot.SendGameAsync(82364, new RefineTotemSkillReq { TotemPos = 1, TotemGuid = first, SkillBookGuid = book });
        Assert.Equal(0, (await Next(bot, 82364, RefineTotemSkillResp.Parser)).Result);
        await Delivered(1700506);

        // A line at its bar's end (as the refine panel shows full), then deployed.
        long second = await Give(17400003);
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            using var held = await CharacterService.Lock(Session);
            var attrs = Session.Items.Single(i => i.Guid == second).Attrs!;
            attrs[230] = attrs[260];
        });
        Assert.Equal(0, await Equip(bot, 2, second));
        await Delivered(1700512);
        await Delivered(1700501);

        long a = await Give(17400002), b = await Give(17400002);
        Assert.Equal(0, await Combine(bot, a, b));
        await Delivered(1700518);
        await At(1700519, 1);
        await Until(s => Service<TaskService>().GrowthStage(s, 1005) == 2, "module 1005 stage 2");

        bot = await Relog(bot);
        await At(1700519, 1); // the event count survives
        long c = await Give(17400005), d = await Give(17400005);
        Assert.Equal(0, await Combine(bot, c, d));
        await Delivered(1700519);
        await bot.DisposeAsync();
    }

    // ---------------------------------------------------------------- Soul Echo

    private static async Task SetUp(BotClient bot, int slot, long guid)
    {
        await bot.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = slot, EmblemGuid = guid });
        Assert.Equal(0, (await Next(bot, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
    }

    private static async Task Feed(BotClient bot, Func<string, Task> gm, long guid, int books)
    {
        await gm($"!item 1257002 {books}");
        await bot.SendGameAsync(EmblemService.Upgrade, new UpgradeEmblemReq { EmblemGuid = guid, ConsumeItems = { [1257002] = books } });
        Assert.Equal(0, (await Next(bot, EmblemService.Upgrade, UpgradeEmblemResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task SoulEchoTargetsReadWornLevelsAndResonanceAndTheAchievementFollows()
    {
        var bot = await InWorld();
        await Gm("!quest done 10103040"); // function 55
        await Gm("!lvl 60"); // function 28
        // Stage 1: 1700400 [103036,4,1], 1700406 [316001,5,1], 1700412 [316002,1,1].
        await At(1700412, 0);
        // 1890111: orange, mark 1001 lit at levels 5, 10, 15 and 20.
        var echoes = new List<long>();
        for (int i = 0; i < 3; i++) echoes.Add(await Give(1890111));

        await SetUp(bot, 1, echoes[0]);
        await Delivered(1700400);
        Assert.Equal(0, await Progress(1700406));
        await Feed(bot, Gm, echoes[0], 1); // level 5..9: one mark
        await Delivered(1700406);
        Assert.Equal(0, await Progress(1700412));
        await SetUp(bot, 2, echoes[1]);
        await SetUp(bot, 3, echoes[2]);
        await Feed(bot, Gm, echoes[1], 1);
        Assert.Equal(0, await Progress(1700412)); // two marks
        await Feed(bot, Gm, echoes[2], 1);
        await Delivered(1700412); // three marks of 1001: resonance stage 1

        // Stage 2: 1700401 [103036,4,2], 1700407 [316001,10,1], 1700413 [316002,2,1].
        await Until(s => Service<TaskService>().GrowthStage(s, 1006) == 2, "module 1006 stage 2");
        await Delivered(1700401);
        await At(1700413, 0);
        await Feed(bot, Gm, echoes[0], 14); // level 20: four marks, six in all
        await Delivered(1700407);
        await Delivered(1700413);

        // Stage 3: 1700414 [316002,3,1] needs ten marks of one id.
        await Until(s => Service<TaskService>().GrowthStage(s, 1006) == 3, "module 1006 stage 3");
        await At(1700414, 0);
        bot = await Relog(bot);
        await At(1700414, 0);
        Assert.Equal(3, await Stage(1006));
        await Feed(bot, Gm, echoes[1], 14);
        await Feed(bot, Gm, echoes[2], 14);
        await Delivered(1700414);
        // Achievement 100101401 [316002,3,1] is no longer blocked.
        await Until(s => s.Achievements?.Infos.TryGetValue(100101401, out var row) == true
            && row.Status == AchievementStatusType.Complete, "achievement 100101401");
        await bot.DisposeAsync();
    }

    // ---------------------------------------------------------------- enchant

    private static async Task<int> Enchant(BotClient bot, long gear, long scroll)
    {
        await bot.SendGameAsync(EnchantService.Enchant, new EnchantReq { EquipGuid = gear, ItemGuid = scroll });
        return (await Next(bot, EnchantService.Enchant, EnchantResp.Parser)).Result;
    }

    [ClientDataFact]
    public async Task EnchantTargetsCountTheEnchantsOnWornGear()
    {
        var bot = await InWorld();
        await Gm("!quest done 10103020"); // function 7
        await Gm("!lvl 50"); // function 83
        await Gm("!item 2 100000");
        // Stage 1: 1700130 [302003,0,1], 1700136 [302003,3,1]; later stages accepted here.
        await At(1700130, 0);
        await Gm("!quest accept 1700132"); // [302003,0,3]
        await Gm("!quest accept 1700141"); // [302003,4,3]

        long gear = await Give(2030540); // purple body armour, four enchant slots
        Assert.Equal(0, await Enchant(bot, gear, await Give(1011201)));
        Assert.Equal(0, await Progress(1700130)); // not worn
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = gear, Pos = (EquipGridType)1 });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await Delivered(1700130);
        await Delivered(1700136);
        await At(1700132, 1);
        await At(1700141, 1);
        Assert.Equal(0, await Enchant(bot, gear, await Give(1011202)));
        await At(1700132, 2);
        await At(1700141, 2);

        bot = await Relog(bot);
        await At(1700132, 2);
        await At(1700141, 2);
        Assert.Equal(0, await Enchant(bot, gear, await Give(1011203)));
        await Delivered(1700132);
        await Delivered(1700141);
        await bot.DisposeAsync();
    }
}
