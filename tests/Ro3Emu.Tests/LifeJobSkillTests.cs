using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Data;
using System.Reflection;

namespace Ro3Emu.Tests;

public sealed class LifeJobSkillTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-life-skill-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (h, m, g) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = h; o.MicroGatePort = m; o.GameGatePort = g;
        });
        await _app.StartAsync(); _http = $"http://127.0.0.1:{h}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var p = new DirectoryInfo(AppContext.BaseDirectory);
        while (p != null && !File.Exists(Path.Combine(p.FullName, "Ro3Emu.slnx"))) p = p.Parent;
        return p?.FullName ?? throw new DirectoryNotFoundException("repository");
    }

    private Task<T> OnLoop<T>(Func<GameSession, Task<T>> fn) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        fn(_app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice")));

    private async Task Gm(string line)
    {
        using var c = new HttpClient();
        var r = await c.PostAsync($"{_http}/ro3emu/gm?account=alice", new StringContent(line));
        r.EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> parser, Func<T, bool> match) where T : IMessage<T>
    {
        var e = await b.Inbox.WaitAsync(e => e.IsGame(id) && match(parser.ParseFrom(e.Body)), TimeSpan.FromSeconds(12));
        return parser.ParseFrom(e.Body);
    }

    [ClientDataFact]
    public async Task SelectedMinerReductionChangesQuoteAndCommittedGatherCost()
    {
        await using (var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem"))))
        {
            await b.LoginAsync("alice", "pw"); await b.EnterGateAsync();
            long role = (await b.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
            await b.EnterWorldAsync(role);
            await Gm("!item 1291000 1"); // learning consumes the workshop certificate
            await b.SendGameAsync(LifeJobService.SwitchLife, new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
            Assert.Equal(0, (await Next(b, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser, _ => true)).Result);
            await Gm("!item 1290998 20");
            await b.SendGameAsync(LifeJobService.UpgradeTitle, new UpGradeJobTileReq { JobId = 1 });
            Assert.Equal(0, (await Next(b, LifeJobService.UpgradeTitle, UpGradeJobTileResp.Parser, _ => true)).Result);
            var offers = await OnLoop(s => Task.FromResult(s.LifeJob!.LifeJobInfoMap[1].TitleInfo.JobTitleDetail[1].OptionalSkills.Values.ToArray()));
            Assert.Equal(3, offers.Length); Assert.Contains(1000, offers);
            await b.SendGameAsync(LifeJobService.ChooseTitle, new ChooseTitlePointReq
            { LifejobId = (LifeJobType)1, Level = 1, TitlePoint = 1000 });
            Assert.Equal(0, (await Next(b, LifeJobService.ChooseTitle, ChooseTitlePointResp.Parser, _ => true)).Result);

            var quote = await Next(b, LifeJobService.HealthNotify, GetHealthCostNtf.Parser,
                m => m.CollectHealthCost.TryGetValue(100100, out int cost) && cost == 19);
            Assert.Equal(19, quote.CollectHealthCost[100100]);

            int result = await OnLoop(async s =>
            {
                using var gate = await CharacterService.Lock(s);
                return await _app.Services.GetRequiredService<LifeJobService>()
                    .CompleteGatherAsync(s, 100100, sceneEntry: 10522, pointId: 312);
            });
            Assert.Equal(0, result);
            var state = await OnLoop(s => Task.FromResult((
                Vigor: s.Restrictions!.RestrictCount[1004].Count,
                Exp: s.LifeJob!.LifeJobInfoMap[1].JobExp)));
            Assert.Equal(481, state.Vigor); Assert.True(state.Exp > 0);

            await Gm("!item 1290999 10");
            await b.SendGameAsync(LifeJobService.RefreshTitle, new RefreshTitlePointReq { LifejobId = 1, Level = 1 });
            Assert.Equal(0, (await Next(b, LifeJobService.RefreshTitle, RefreshTitlePointResp.Parser, _ => true)).Result);
            var refreshed = await OnLoop(s => Task.FromResult((
                Skill: s.LifeJob!.LifeJobInfoMap[1].TitleInfo.JobTitleDetail[1].SelectedSkill,
                Tokens: s.Items.Where(i => i.Entry == 1290999).Sum(i => i.Stack))));
            Assert.Equal(1000, refreshed.Skill); Assert.Equal(0, refreshed.Tokens);
        }
        await using var again = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await again.LoginAsync("alice", "pw"); await again.EnterGateAsync();
        await again.EnterWorldAsync(again.Roles.Single().Id);
        var persisted = await OnLoop(s => Task.FromResult((
            Skill: s.LifeJob!.LifeJobInfoMap[1].TitleInfo.JobTitleDetail[1].SelectedSkill,
            Vigor: s.Restrictions!.RestrictCount[1004].Count)));
        Assert.Equal(1000, persisted.Skill); Assert.Equal(481, persisted.Vigor);
    }

    [ClientDataFact]
    public async Task TwentiethGatherAddsOneMainUnitAndStagesCounterOnly()
    {
        await using var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await b.LoginAsync("alice", "pw"); await b.EnterGateAsync();
        long role = (await b.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await b.EnterWorldAsync(role);
        await Gm("!item 1291000 1"); // learning consumes the workshop certificate
        await b.SendGameAsync(LifeJobService.SwitchLife, new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 });
        Assert.Equal(0, (await Next(b, LifeJobService.SwitchLife, SwitchLifeJobResp.Parser, _ => true)).Result);

        var state = await OnLoop(s =>
        {
            // Skill 1100 is in miner slot four's configured pool. Seed 19 prior uses so
            // one operation deterministically reaches the configured 20-use boundary.
            var title = s.LifeJob!.LifeJobInfoMap[1].TitleInfo;
            title.JobLevel = 5;
            title.JobTitleDetail[4] = new LifeJobTitleSkillInfo
            {
                OriginalSkill = 1100, SelectedSkill = 1100,
                ExtraInfo = new SkillExtraInfo { Param1 = 19 },
            };
            var next = s.LifeJob.Clone();
            var baseMain = new AwardGrant(1291010, 4);
            var grants = new List<AwardGrant> { baseMain };
            var costs = new List<(int Entry, long Count)>();
            var service = _app.Services.GetRequiredService<LifeJobService>();
            var method = typeof(LifeJobService).GetMethod("ApplySkillRewards", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            method!.Invoke(service, [s, next, _app.Services.GetRequiredService<GameData>().Row("LifeCollectionConfig", 100100)!.Value,
                false, grants, costs, new List<AwardGrant> { baseMain }, 0]);
            return Task.FromResult((
                Total: grants.Where(x => x.Entry == 1291010).Sum(x => x.Count),
                Staged: next.LifeJobInfoMap[1].TitleInfo.JobTitleDetail[4].ExtraInfo.Param1,
                Published: s.LifeJob.LifeJobInfoMap[1].TitleInfo.JobTitleDetail[4].ExtraInfo.Param1));
        });
        Assert.Equal(5, state.Total); // base stack of four, plus exactly one main item
        Assert.Equal(0, state.Staged); Assert.Equal(19, state.Published);
    }

    [ClientDataFact]
    public async Task AscensionPromotesOneForManyAndLostGrantRemovesExtraSlots()
    {
        await using var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await b.LoginAsync("alice", "pw"); await b.EnterGateAsync();
        long role = (await b.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await b.EnterWorldAsync(role);
        var service = _app.Services.GetRequiredService<LifeJobService>();
        var normalize = typeof(LifeJobService).GetMethod("NormalizeTitles", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(normalize);

        var promoted = await OnLoop(s =>
        {
            var life = new DBLifeJobInfo();
            life.ActiveLifejob[1] = 3;
            var title = new LifeJobTitleInfo { JobLevel = 5 };
            // These are configured choices in Profession 3's ordinary slots four and five.
            title.JobTitleDetail[4] = new LifeJobTitleSkillInfo { OriginalSkill = 3103, SelectedSkill = 3103 };
            title.JobTitleDetail[5] = new LifeJobTitleSkillInfo { OriginalSkill = 3074, SelectedSkill = 3074 };
            life.LifeJobInfoMap[3] = new DBLifeJobInfoDetail { JobId = 3, JobLevel = 1, TitleInfo = title };
            normalize!.Invoke(service, [life]);
            s.LifeJob = life;
            return Task.FromResult((Original: title.JobTitleDetail[4].OriginalSkill,
                Effective: title.JobTitleDetail[4].SelectedSkill,
                Extra: title.SkillExtraSkill.Keys.Order().ToArray()));
        });
        Assert.Equal(3103, promoted.Original); Assert.Equal(3104, promoted.Effective);
        Assert.Equal([6, 7], promoted.Extra);

        await OnLoop(s =>
        {
            var title = s.LifeJob!.LifeJobInfoMap[3].TitleInfo;
            title.JobTitleDetail.Remove(4); // remove the grant, then rebuild entitlements
            normalize!.Invoke(service, [s.LifeJob]);
            return Task.FromResult(0);
        });
        Assert.Empty(await OnLoop(s => Task.FromResult(s.LifeJob!.LifeJobInfoMap[3].TitleInfo.SkillExtraSkill)));
        await OnLoop(async s =>
        {
            await service.ChooseTitleAsync(s,
                new ChooseTitlePointReq { LifejobId = (LifeJobType)3, Level = 6, TitlePoint = 3103 }, true);
            return 0;
        });
        Assert.Equal(ErrorCodes.LifeSkillMissing,
            (await Next(b, LifeJobService.ChooseTitleEx, ChooseTitlePointExResp.Parser, _ => true)).Result);
    }
}
