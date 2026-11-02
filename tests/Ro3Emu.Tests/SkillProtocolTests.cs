using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Skill parameter, training accounting and boss presentation over the game wire.</summary>
public sealed class SkillProtocolTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-skill-protocol-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
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

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(
            e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait)).Body);

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", Operator.Command(line))).EnsureSuccessStatusCode();
    }

    private GameSession Session(string account) => _app.Services.GetRequiredService<SessionRegistry>().All()
        .Single(s => s.Account == account);
    private Task Run(Func<Task> work) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(work);

    [ClientDataFact]
    public async Task ActiveBuffChangesEffectiveDamageParameterAndWireThenRemovalClearsIt()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        var learn = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
        learn.LevelUpList[11001] = new SkillData { SkillId = 11001, SkillLevel = 1 };
        await alice.SendGameAsync(SkillService.PlanUpdate, learn);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        var session = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var buffs = _app.Services.GetRequiredService<BuffService>();

        await Run(() => buffs.AddAsync(session, 1100301, 10000, session.Role!.Id));
        var affected = await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            m => m.AffectParamDict.ContainsKey(11001));
        Assert.True(affected.IsAll);
        Assert.Equal(session.Role!.Id, affected.Uuid);
        Assert.Equal(4830, affected.AffectParamDict[11001].AffectParam[29].AffectValueList[0]);
        Assert.Equal(4830, combat.EffectiveSkillRow(session, 1100101)!.Value.GetProperty("_kDamageParam1")[0].GetInt64());

        await Run(() => buffs.RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == 1100301)));
        var reset = await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            m => m.IsAll && m.AffectParamDict.ContainsKey(0));
        Assert.Empty(reset.AffectParamDict[0].AffectParam);
        Assert.Equal(4600, combat.EffectiveSkillRow(session, 1100101)!.Value.GetProperty("_kDamageParam1")[0].GetInt64());

        // The exported -1 damage-vector index applies to every component (inferred).
        await Run(() => buffs.AddAsync(session, 1246101, 10000, session.Role.Id));
        var vector = combat.EffectiveSkillRow(session, 1220101)!.Value.GetProperty("_kDamageParam1");
        Assert.Equal(6500, vector[0].GetInt64());
        Assert.Equal(8500, vector[1].GetInt64());
        await Run(() => buffs.RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == 1246101)));

        await Run(() =>
        {
            // Isolate 31027 from skill-load validation: learned level 2 is deliberately
            // different from the selected level 1 in the active manual wheel slot.
            session.Progress!.Skills[10011] = 2;
            session.Progress.SkillBar[3] = 10011;
            var plan = session.SkillPlans!.SkillAssignPlans[session.SkillPlans.CurrentPlanIndex];
            while (plan.ManualLoadSkill.Count < 3) plan.ManualLoadSkill.Add(new SkillData());
            plan.ManualLoadSkill[2] = new SkillData { SkillId = 10011, SkillLevel = 1 };
            return Task.CompletedTask;
        });
        await Run(() => buffs.AddAsync(session, 1150101, 10000, session.Role.Id));
        var replacement = await Next(alice, CombatService.SkillSlotModify, SkillSlotModifyNotify.Parser,
            m => m.Slot == 3 && m.SkillId == 1001111);
        Assert.Equal(1001101, replacement.SourceSkillId);
        Assert.Equal(1001111, combat.EffectiveSkillRow(session, 1001101)!.Value.GetProperty("_iID").GetInt32());
        Assert.Null(combat.EffectiveSkillRow(session, 1001121));
    }

    [ClientDataFact]
    public async Task TrainingPauseGatesRealStatisticsAndInvalidBossRequestIsRefused()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        await alice.SendGameAsync(CombatService.TrainingOperation, new BattleTrainingGroundOpReq());
        Assert.Equal(ErrorCodes.Parameter,
            (await Next(alice, CombatService.TrainingOperation, BattleTrainingGroundOpResp.Parser)).Result);

        await alice.SendGameAsync(CombatService.TrainingOperation, new BattleTrainingGroundOpReq { OpType = (BattleTrainingGroundOpType)1 });
        Assert.Equal(0, (await Next(alice, CombatService.TrainingOperation, BattleTrainingGroundOpResp.Parser)).Result);
        Assert.True(session.SkillProtocol.TrainingPaused);
        int before = alice.Inbox.Snapshot().Count(e => e.IsGame(CombatService.DamageStatistics));
        var actual = new Romsg.DamageStatistics
        {
            DamageSourceId = session.Role!.Id, DamageTargetId = 123, DamageValue = 42, SkillId = 1100101,
        };
        await Run(() => combat.RecordDamageStatisticAsync(session, actual));
        Assert.Equal(before, alice.Inbox.Snapshot().Count(e => e.IsGame(CombatService.DamageStatistics)));

        await alice.SendGameAsync(CombatService.TrainingOperation, new BattleTrainingGroundOpReq { OpType = (BattleTrainingGroundOpType)2 });
        Assert.Equal(0, (await Next(alice, CombatService.TrainingOperation, BattleTrainingGroundOpResp.Parser)).Result);
        await Run(() => combat.RecordDamageStatisticAsync(session, actual));
        var report = await Next(alice, CombatService.DamageStatistics, DamageStatisticsNotify.Parser);
        Assert.Equal(42, Assert.Single(report.Damages).DamageValue);
        Assert.Null(report.Damages[0].DamageCalStep);

        await alice.SendGameAsync(CombatService.BossAllSkillCds, new BossAllSkillShowCdReq { BossUId = 999 });
        var boss = await Next(alice, CombatService.BossAllSkillCds, BossAllSkillShowCdRes.Parser);
        Assert.Equal(ErrorCodes.SkillTargetInvalid, boss.Result);
        Assert.Empty(boss.BossSkillCds);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository data/config");
    }
}
