using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Skill plans and allocation requests over the ordinary game wire.</summary>
public sealed class SkillGrowthTests : IAsyncLifetime
{
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-skill-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication app = null!;
    private string http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser,
        Func<T, bool>? match = null) where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Ro3Emu.slnx");
    }

    [ClientDataFact]
    public async Task FreshNoviceWheelShowsNormalAttackAndRepairsAnOlderEmptySavedWheel()
    {
        long roleId;
        await using (var novice = await InWorld("Novice"))
        {
            roleId = (long)novice.Self!.Uuid;
            var fresh = await Next(novice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
            Assert.True(fresh.SyncAllMark);
            Assert.Equal(1000001, fresh.SkillWheel.All.WhellSkills[1].SkillConfigId);
            Assert.Equal(1000001, fresh.OriginSkillWheel.All.WhellSkills[1].SkillConfigId);
            Assert.Equal(1000001, fresh.SkillWheel.All.AtkSkillId);
        }

        // Simulate an existing role saved before Novices had a visible normal-attack slot.
        var roles = app.Services.GetRequiredService<RoleStore>();
        var old = DBSkillData.Parser.ParseFrom(roles.LoadSkillPlans(roleId)!);
        old.SkillWheel.WhellSkills.Clear();
        old.OriginSkillWheel.WhellSkills.Clear();
        Assert.Equal(1, FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(),
            "UPDATE role SET skill_plans = $plans WHERE id = $id",
            ("$plans", old.ToByteArray()), ("$id", roleId)));

        await using var again = await InWorld("Novice", create: false);
        var repaired = await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
        Assert.Equal(1000001, repaired.SkillWheel.All.WhellSkills[1].SkillConfigId);
        Assert.Equal(1000001, repaired.OriginSkillWheel.All.WhellSkills[1].SkillConfigId);

        await Gm("novice", "!job 1100");
        await again.SendGameAsync(1023, new PlayerAssetReq { AssetType = { 5 } });
        var swordman = await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
            m => m.SyncAllMark && m.CurrentAtkSkillId == 1001001);
        Assert.DoesNotContain(swordman.SkillWheel.All.WhellSkills.Values,
            slot => slot.SkillConfigId == 1001001);
    }

    [ClientDataFact]
    public async Task NoviceCanRebindNormalAttackAndSavedManualSlotSurvivesLogin()
    {
        await using (var novice = await InWorld("Novice"))
        {
            await Next(novice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
            await novice.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
                { OpType = SkillLoadOperation.SkillLoadOpLoad, IsManual = true, SocketNum = 2,
                    Info = new SkillData { SkillId = 10000, SkillLevel = 2 } });
            Assert.Equal(ErrorCodes.SkillNotLearned,
                (await Next(novice, SkillService.SkillLoad, SkillLoadResp.Parser)).Result);

            await novice.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
                { OpType = SkillLoadOperation.SkillLoadOpLoad, IsManual = true, SocketNum = 2,
                    Info = new SkillData { SkillId = 10000, SkillLevel = 1 } });
            Assert.Equal(0, (await Next(novice, SkillService.SkillLoad, SkillLoadResp.Parser)).Result);
        }

        await using var again = await InWorld("Novice", create: false);
        var saved = await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
        Assert.False(saved.SkillWheel.All.WhellSkills.ContainsKey(1));
        Assert.Equal(1000001, saved.SkillWheel.All.WhellSkills[3].SkillConfigId);
        Assert.Equal(1000001, saved.OriginSkillWheel.All.WhellSkills[3].SkillConfigId);
        Assert.Equal(10000, saved.SkillAssignPlans.All.Value[0].ManualLoadSkill[2].SkillId);
    }

    [ClientDataFact]
    public async Task AllocationAndDependentRefundAreAtomic()
    {
        await using var alice = await InWorld("Alice");
        var fresh = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
        Assert.True(fresh.SyncAllMark);
        Assert.Equal(5, fresh.MaxPlanCount);
        Assert.Equal(5, fresh.SkillAssignPlans.All.Value.Count);
        Assert.Equal(0, fresh.SkillAssignPlans.All.Value[0].SkillAssigns[10002].SkillLevel); // base-level 5 gate
        await alice.SendGameAsync(SkillService.PlanUpdate, new SkillGrowthSkillPlanUpdateReq { Plan = 99 });
        Assert.Equal(ErrorCodes.Parameter,
            (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);

        await Gm("alice", "!job 1100");
        await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
            m => !m.SyncAllMark && m.CurrentAtkSkillId == 1001001);
        var learn = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
        learn.LevelUpList[11001] = new SkillData { SkillId = 11001, SkillLevel = 5 };
        learn.LevelUpList[11002] = new SkillData { SkillId = 11002, SkillLevel = 1 };
        await alice.SendGameAsync(SkillService.PlanUpdate, learn);
        var allocated = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
            m => !m.SyncAllMark && m.SkillAssignPlans?.Inc?.Value[0].SkillAssigns.ContainsKey(11002) == true);
        Assert.Equal(1, allocated.SkillAssignPlans.Inc.Value[0].SkillAssigns[11002].Inc.SkillLevel);
        Assert.Equal(9, allocated.SkillAssignPlans.Inc.Value[0].SkillPoints[2].All);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);

        var bad = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
        bad.LevelUpList[11001] = new SkillData { SkillId = 11001, SkillLevel = 0 };
        await alice.SendGameAsync(SkillService.PlanUpdate, bad);
        Assert.Equal(ErrorCodes.SkillPrerequisite,
            (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        bad.LevelUpList[999999] = new SkillData { SkillId = 999999, SkillLevel = 1 };
        await alice.SendGameAsync(SkillService.PlanUpdate, bad);
        Assert.Equal(ErrorCodes.SkillNotInTree,
            (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        bad.LevelUpList.Remove(999999);
        bad.LevelUpList[11002] = new SkillData { SkillId = 11002, SkillLevel = 0 };
        await alice.SendGameAsync(SkillService.PlanUpdate, bad);
        var refunded = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
            m => !m.SyncAllMark && m.SkillAssignPlans?.Inc?.Value[0].SkillAssigns.ContainsKey(11001) == true);
        Assert.Equal(0, refunded.SkillAssignPlans.Inc.Value[0].SkillAssigns[11001].Inc.SkillLevel);
        Assert.Equal(15, refunded.SkillAssignPlans.Inc.Value[0].SkillPoints[2].All);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task PlansNameGenreAndSelectedLevelsSurviveRelog()
    {
        await using (var alice = await InWorld("Alice"))
        {
            await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
            await Gm("alice", "!job 1100");
            await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
                m => !m.SyncAllMark && m.CurrentAtkSkillId == 1001001);
            var learn = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
            learn.LevelUpList[11001] = new SkillData { SkillLevel = 3 };
            await alice.SendGameAsync(SkillService.PlanUpdate, learn);
            Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
            await alice.SendGameAsync(SkillService.SwitchPlan, new SwitchPlanReq { Plan = 1 });
            Assert.Equal(0, (await Next(alice, SkillService.SwitchPlan, SwitchPlanResp.Parser)).Result);
            var switched = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
                m => !m.SyncAllMark && m.CurrentPlanIndex == 1);
            Assert.Equal(1, switched.CurrentPlanIndex);

            await alice.SendGameAsync(SkillService.PlanNameChange, new SkillPlanChangeNameReq { Plan = 1, Name = "Quest" });
            Assert.Equal(0, (await Next(alice, SkillService.PlanNameChange, SkillPlanChangeNameResp.Parser)).Result);
            await alice.SendGameAsync(SkillService.PlanNameChange, new SkillPlanChangeNameReq { Plan = 1, Name = "TooLong01" });
            Assert.Equal(ErrorCodes.Parameter,
                (await Next(alice, SkillService.PlanNameChange, SkillPlanChangeNameResp.Parser)).Result);
            await alice.SendGameAsync(SkillService.SwitchGenre, new SkillSwitchGenreReq { Genre = 2 });
            Assert.Equal(0, (await Next(alice, SkillService.SwitchGenre, SkillSwitchGenreResp.Parser)).Result);
            learn.Plan = 1;
            learn.LevelUpList[11001] = new SkillData { SkillLevel = 2 };
            await alice.SendGameAsync(SkillService.PlanUpdate, learn);
            Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
            await alice.SendGameAsync(SkillService.SwitchPlan, new SwitchPlanReq { Plan = 0 });
            Assert.Equal(ErrorCodes.Cooldown,
                (await Next(alice, SkillService.SwitchPlan, SwitchPlanResp.Parser)).Result);
        }
        await using var again = await InWorld("Alice", create: false);
        var reloaded = await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
        Assert.Equal(1, reloaded.CurrentPlanIndex);
        Assert.Equal("Quest", reloaded.SkillAssignPlans.All.Value[1].Name);
        Assert.Equal(2, reloaded.SkillAssignPlans.All.Value[1].SkillGenre);
        Assert.Equal(2, reloaded.SkillAssignPlans.All.Value[1].SkillAssigns[11001].SkillLevel);
        Assert.Equal(3, reloaded.SkillAssignPlans.All.Value[0].SkillAssigns[11001].SkillLevel);
        await again.SendGameAsync(SkillService.SwitchPlan, new SwitchPlanReq { Plan = 0 });
        Assert.Equal(0, (await Next(again, SkillService.SwitchPlan, SwitchPlanResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task LoadQueueAndResetValidateInputsAndPersist()
    {
        await using (var alice = await InWorld("Alice"))
        {
            await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
            await Gm("alice", "!job 3100"); // Archer 13006 is queue eligible.
            await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
                m => !m.SyncAllMark && m.SkillList.All.Value.Contains(13006));
            var learn = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
            learn.LevelUpList[13006] = new SkillData { SkillLevel = 1 };
            await alice.SendGameAsync(SkillService.PlanUpdate, learn);
            Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);

            await alice.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
                { OpType = SkillLoadOperation.SkillLoadOpLoad, IsManual = true, SocketNum = 2,
                    Info = new SkillData { SkillId = 13006, SkillLevel = 1 } });
            Assert.Equal(0, (await Next(alice, SkillService.SkillLoad, SkillLoadResp.Parser)).Result);
            var wheel = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
                m => !m.SyncAllMark && m.SkillWheel?.Inc?.WhellSkills.ContainsKey(3) == true);
            Assert.Equal(1300601, wheel.SkillWheel.Inc.WhellSkills[3].All.SkillConfigId);
            await alice.SendGameAsync(SkillService.QueueUpdate, new SkillQueueUpdateReq
                { Plan = 0, OpType = SkillLoadOperation.SkillLoadOpLoad, QueueIndex = 0,
                    Info = new SkillData { SkillId = 13006, SkillLevel = 1 } });
            Assert.Equal(0, (await Next(alice, SkillService.QueueUpdate, SkillQueueUpdateResp.Parser)).Result);
            var loaded = await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
                m => !m.SyncAllMark && m.SkillAssignPlans?.Inc?.Value[0].SkillQueue.ContainsKey(0) == true);
            Assert.Equal(13006, loaded.SkillAssignPlans.Inc.Value[0].SkillQueue[0].Inc.SkillId);

            await alice.SendGameAsync(SkillService.QueueUpdate, new SkillQueueUpdateReq
                { Plan = 0, OpType = SkillLoadOperation.SkillLoadOpLoad, QueueIndex = 12,
                    Info = new SkillData { SkillId = 13006, SkillLevel = 1 } });
            Assert.Equal(ErrorCodes.Parameter,
                (await Next(alice, SkillService.QueueUpdate, SkillQueueUpdateResp.Parser)).Result);
            await alice.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
                { OpType = SkillLoadOperation.SkillLoadOpLoad, IsManual = true, SocketNum = 6,
                    Info = new SkillData { SkillId = 13006, SkillLevel = 1 } });
            Assert.Equal(ErrorCodes.Parameter,
                (await Next(alice, SkillService.SkillLoad, SkillLoadResp.Parser)).Result);
            await alice.SendGameAsync(SkillService.SkillReset, new SkillResetReq { Plan = 0 });
            Assert.Equal(0, (await Next(alice, SkillService.SkillReset, SkillResetResp.Parser)).Result);
        }
        await using var again = await InWorld("Alice", create: false);
        var reloaded = await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
        Assert.Equal(0, reloaded.SkillAssignPlans.All.Value[0].SkillAssigns[13006].SkillLevel);
        Assert.Equal(0, reloaded.SkillAssignPlans.All.Value[0].SkillQueue[0].SkillId);
        Assert.DoesNotContain(reloaded.SkillWheel.All.WhellSkills.Values, slot => slot.SkillConfigId == 1300601);
    }

    [ClientDataFact]
    public async Task LegacyJsonPlansMigrateOnceAndKeepSelectedLoads()
    {
        long roleId;
        await using (var alice = await InWorld("Legacy"))
        {
            roleId = (long)alice.Self!.Uuid;
            await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
            await Gm("legacy", "!job 3100");
            await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
                m => !m.SyncAllMark && m.SkillList.All.Value.Contains(13006));
        }

        var legacy = new
        {
            Current = 1,
            Plans = Enumerable.Range(0, 5).Select(index => new
            {
                Unlocked = true, Name = index == 1 ? "Migrated" : "",
                Genre = index == 1 ? 2 : 0,
                Levels = index == 1 ? new Dictionary<int, int> { [13006] = 2 } : new(),
                Manual = index == 1 ? new Dictionary<int, int> { [3] = 13006 } : new(),
                ManualLevels = index == 1 ? new Dictionary<int, int> { [3] = 1 } : new(),
                Auto = index == 1 ? new Dictionary<int, int> { [1] = 13006 } : new(),
                AutoLevels = index == 1 ? new Dictionary<int, int> { [1] = 2 } : new(),
                Queue = index == 1 ? new Dictionary<int, int> { [0] = 13006 } : new(),
                QueueLevels = index == 1 ? new Dictionary<int, int> { [0] = 1 } : new(),
            }).ToArray(),
        };
        Assert.Equal(1, FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(),
            "UPDATE role SET skill_plans = $plans WHERE id = $id",
            ("$plans", System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(legacy)), ("$id", roleId)));

        for (int login = 0; login < 2; login++)
        {
            await using var again = await InWorld("Legacy", create: false);
            var loaded = await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
            Assert.Equal(1, loaded.CurrentPlanIndex);
            Assert.Equal(5, loaded.MaxPlanCount);
            var plan = loaded.SkillAssignPlans.All.Value[1];
            Assert.Equal("Migrated", plan.Name);
            Assert.Equal(2, plan.SkillGenre);
            Assert.Equal(2, plan.SkillAssigns[13006].SkillLevel);
            Assert.Equal(13006, plan.ManualLoadSkill[2].SkillId);
            Assert.Equal(1, plan.ManualLoadSkill[2].SkillLevel);
            Assert.Equal(2, plan.AutoLoadSkill[0].SkillLevel);
            Assert.Equal(13006, plan.SkillQueue[0].SkillId);
            Assert.Equal(1, plan.SkillQueue[0].SkillLevel);
            Assert.Equal(1300601, loaded.SkillWheel.All.WhellSkills[3].SkillConfigId);
            Assert.Equal(2, app.Services.GetRequiredService<SessionRegistry>().All()
                .Single(s => s.Role?.Id == roleId).Progress!.Skills[13006]);
        }

        using var persistedDb = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dataDir, "ro3emu.db")}");
        persistedDb.Open();
        using var read = persistedDb.CreateCommand();
        read.CommandText = "SELECT skill_plans FROM role WHERE id = $id";
        read.Parameters.AddWithValue("$id", roleId);
        var blob = (byte[])read.ExecuteScalar()!;
        Assert.NotEqual((byte)'{', blob[0]);
        var durable = DBSkillData.Parser.ParseFrom(blob);
        Assert.Equal(1, durable.CurrentPlanIndex);
        Assert.Equal("Migrated", durable.SkillAssignPlans[1].Name);
        Assert.Equal(13006, durable.SkillAssignPlans[1].SkillQueue[0].SkillId);
    }
}
