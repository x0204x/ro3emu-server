using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Build window wire and durable composition; uses ordinary game messages and isolated ports.</summary>
public sealed partial class GenreTests : IAsyncLifetime
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ro3emu-genre-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "data", "config"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("repository data/config");
    }

    private async Task<BotClient> Enter(string name, bool create = true)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id); return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(10));
        return parser.ParseFrom(e.Body);
    }

    private async Task Level(string account, int level)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={account}", new StringContent($"!lvl {level} 5"))).EnsureSuccessStatusCode();
    }

    [ClientDataFact]
    public async Task FivePresetsRenameAndSelectedChildrenSurviveRelog()
    {
        await using (var player = await Enter("Builder"))
        {
            var initial = await Next(player, GenreService.GenreSync, DBGenreIncrement.Parser);
            Assert.Equal(5, initial.GenreMap.All.SystemMap.Count);
            await Level("builder", 40);
            using (var client = new HttpClient())
                (await client.PostAsync($"{http}/ro3emu/gm?account=builder", new StringContent("!job 1100"))).EnsureSuccessStatusCode();
            var attrs = new AllocateAttrPointReq { PlanIndex = 1 };
            foreach (var (stat, value) in new[] { (1, 5), (2, 1), (3, 1), (4, 1), (5, 1), (6, 1) }) attrs.Points[stat] = value;
            await player.SendGameAsync(CharacterService.AllocateAttrPoints, attrs);
            Assert.Equal(0, (await Next(player, CharacterService.AllocateAttrPoints, AllocateAttrPointRsp.Parser)).Result);
            var learned = new SkillGrowthSkillPlanUpdateReq { Plan = 1 };
            learned.LevelUpList[11001] = new SkillData { SkillId = 11001, SkillLevel = 3 };
            await player.SendGameAsync(SkillService.PlanUpdate, learned);
            Assert.Equal(0, (await Next(player, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
            await player.SendGameAsync(82816, new GenreChangePlanNameReq { TargetIndex = 1, Name = "Tower" });
            Assert.Equal(0, (await Next(player, 82816, GenreChangePlanNameResp.Parser)).Result);
            await player.SendGameAsync(82806, new SwitchGenrePlanReq
            {
                TargetPlan = 1, IsApply = true, ChildSystem = { [1] = 1, [2] = 1, [3] = 1 },
            });
            Assert.Equal(0, (await Next(player, 82806, SwitchGenrePlanResp.Parser)).Result);
        }
        await using var again = await Enter("Builder", create: false);
        var restored = await Next(again, GenreService.GenreSync, DBGenreIncrement.Parser);
        Assert.Equal(1, restored.CurrentPlan);
        Assert.Equal("Tower", restored.GenreMap.All.SystemMap[1].PlanName);
        Assert.Equal(1, restored.GenreMap.All.SystemMap[1].GenrePlans[2]);
        Assert.Equal(1, restored.GenreMap.All.SystemMap[1].GenrePlans[3]);
        Assert.Equal(1, restored.GenreMap.All.SystemMap[1].GenrePlans[1]);
        Assert.Equal(5, again.Self!.Property.Single(p => p.Idx == 1).Data);
        var skillSync = await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
        Assert.Equal(1, skillSync.CurrentPlanIndex);
    }

    [ClientDataFact]
    public async Task InvalidChildSelectionDoesNotChangeCurrentPreset()
    {
        await using (var player = await Enter("Invalid"))
        {
            await Next(player, GenreService.GenreSync, DBGenreIncrement.Parser);
            await Level("invalid", 40);
            await player.SendGameAsync(82806, new SwitchGenrePlanReq
            {
                TargetPlan = 1, IsApply = true, ChildSystem = { [2] = 1, [3] = 99 },
            });
            Assert.NotEqual(0, (await Next(player, 82806, SwitchGenrePlanResp.Parser)).Result);

            // Abort after genre and stat_plans were written, proving the enclosing role transaction rolls back.
            FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(),
                "CREATE TRIGGER genre_abort BEFORE UPDATE OF skill_plans ON role BEGIN SELECT RAISE(ABORT, 'genre test abort'); END");
            await player.SendGameAsync(82806, new SwitchGenrePlanReq
            {
                TargetPlan = 1, IsApply = true, ChildSystem = { [2] = 1, [3] = 1 },
            });
            Assert.NotEqual(0, (await Next(player, 82806, SwitchGenrePlanResp.Parser)).Result);
            FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER genre_abort");
            FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(),
                "CREATE TRIGGER genre_save_abort BEFORE UPDATE OF genre ON role BEGIN SELECT RAISE(ABORT, 'genre save abort'); END");
            await player.SendGameAsync(82806, new SwitchGenrePlanReq
            {
                TargetPlan = 1, IsApply = false, ChildSystem = { [2] = 1 },
            });
            Assert.NotEqual(0, (await Next(player, 82806, SwitchGenrePlanResp.Parser)).Result);
            FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER genre_save_abort");
        }
        await using var again = await Enter("Invalid", create: false);
        var restored = await Next(again, GenreService.GenreSync, DBGenreIncrement.Parser);
        Assert.Equal(0, restored.CurrentPlan);
        Assert.Equal(0, restored.GenreMap.All.SystemMap[1].GenrePlans[2]);
        Assert.Equal(0, restored.GenreMap.All.SystemMap[1].GenrePlans[3]);
        Assert.Equal(0, (await Next(again, CharacterService.BaseAttrSync, BaseAttrInfoIncrement.Parser)).CurPlanIndex);
        Assert.Equal(0, (await Next(again, CharacterService.EquipSync, DBEquipInfoIncrement.Parser)).CurrentEquipPlan);
        Assert.Equal(0, (await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser)).CurrentPlanIndex);
    }

    [ClientDataFact]
    public async Task RecommendationImportsOnlyRecipientOwnedItemAndRejectsMissingItem()
    {
        await using var donor = await Enter("Donor");
        await using var owner = await Enter("Owner");
        await using var missing = await Enter("Missing");
        long donorDagger = (await Next(donor, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser))
            .PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == 2010121).ItemGuid;
        long ownDagger = (await Next(owner, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser))
            .PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == 2010121).ItemGuid;
        await Next(missing, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        await Level("donor", 40); await Level("owner", 40); await Level("missing", 40);
        using (var client = new HttpClient())
            (await client.PostAsync($"{http}/ro3emu/gm?account=missing", new StringContent("!delitem 2010121 1"))).EnsureSuccessStatusCode();

        async Task<long> SentTo(BotClient seeker)
        {
            await seeker.SendGameAsync(82807, new GenreSeekerCodeReq());
            string code = (await Next(seeker, 82807, GenreSeekerCodeResp.Parser)).Code;
            Assert.NotEmpty(code);
            await donor.SendGameAsync(82812, new GenreResponseCodeReq { Code = code });
            var response = await Next(donor, 82812, GenreResponseCodeResp.Parser);
            Assert.Equal(0, response.Result);
            var gear = new DBEquipPlan(); gear.ItemsEquipGrid[1] = donorDagger;
            var emptyTotem = new TotemPlan();
            for (int slot = 1; slot <= 3; slot++) emptyTotem.SetUpTotem[slot] = 0;
            await donor.SendGameAsync(82803, new GenreResponserSaveReq
            { SeekUserId = response.SeekUserId, ResponseId = response.ResponseId,
                Plan = new GenrePlanConcrete { EquipPlan = gear, TotemPlan = emptyTotem } });
            Assert.Equal(0, (await Next(donor, 82803, GenreResponserSaveResp.Parser)).Result);
            await donor.SendGameAsync(82802, new GenreResponserSendPlanReq
            { SeekUserId = response.SeekUserId, ResponseId = response.ResponseId });
            Assert.Equal(0, (await Next(donor, 82802, GenreResponserSendPlanResp.Parser)).Result);
            return response.ResponseId;
        }

        long ownerReply = await SentTo(owner);
        await owner.SendGameAsync(82813, new GenreSeekerApplyPlanReq
        {
            PlanId = 1, ResponseId = ownerReply, ResponseUserId = (long)donor.Self!.Uuid,
            SystemType = { [3] = 1, [5] = 0 },
        });
        Assert.Equal(0, (await Next(owner, 82813, GenreSeekerApplyPlanResp.Parser)).Result);
        Assert.NotEqual(donorDagger, ownDagger);
        var appearance = await Next(owner, CharacterService.EntityBaseChange, EntityBase.Parser);
        Assert.Equal(2010121, appearance.EquipInfo[0]);

        long missingReply = await SentTo(missing);
        await missing.SendGameAsync(82813, new GenreSeekerApplyPlanReq
        {
            PlanId = 1, ResponseId = missingReply, ResponseUserId = (long)donor.Self!.Uuid,
            SystemType = { [3] = 1, [5] = 0 },
        });
        Assert.NotEqual(0, (await Next(missing, 82813, GenreSeekerApplyPlanResp.Parser)).Result);
        Assert.Equal(0, missing.Self!.EquipInfo[0]);
    }

    [ClientDataFact]
    public async Task FormerSkillPlanAndInvalidSavedPlanKeepLearnedSkills()
    {
        long roleId;
        await using (var player = await Enter("AttackPlan"))
        {
            roleId = (long)player.Self!.Uuid;
            var initial = await Next(player, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
            Assert.Contains(10000, initial.SkillAssignPlans.All.Value[0].SkillAssigns.Keys);
            await Level("attackplan", 40);
            using (var client = new HttpClient())
                (await client.PostAsync($"{http}/ro3emu/gm?account=attackplan", new StringContent("!job 1100"))).EnsureSuccessStatusCode();

            await player.SendGameAsync(SkillService.SwitchPlan, new SwitchPlanReq { Plan = 1 });
            Assert.Equal(0, (await Next(player, SkillService.SwitchPlan, SwitchPlanResp.Parser)).Result);
            // This migration test switches after SchemeCD; cooldown refusal is covered by SkillGrowthTests.
            await app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            {
                app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == roleId)
                    .SkillPlanSwitchUtc = default;
                return Task.CompletedTask;
            });
            await player.SendGameAsync(SkillService.SwitchPlan, new SwitchPlanReq { Plan = 0 });
            Assert.Equal(0, (await Next(player, SkillService.SwitchPlan, SwitchPlanResp.Parser)).Result);

            var learn = new SkillGrowthSkillPlanUpdateReq { Plan = 0 };
            learn.LevelUpList[11001] = new SkillData { SkillId = 11001, SkillLevel = 3 };
            await player.SendGameAsync(SkillService.PlanUpdate, learn);
            Assert.Equal(0, (await Next(player, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        }

        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "ro3emu.db")}"))
        {
            db.Open();
            using var read = db.CreateCommand();
            read.CommandText = "SELECT skill_plans FROM role WHERE id = $id";
            read.Parameters.AddWithValue("$id", roleId);
            var saved = DBSkillData.Parser.ParseFrom((byte[])read.ExecuteScalar()!);
            Assert.Equal(3, saved.SkillAssignPlans[0].SkillAssigns[11001].SkillLevel);
            // An obsolete plan from another career fails tree validation before reaching Bash.
            saved.SkillAssignPlans[0].SkillAssigns.Clear();
            saved.SkillAssignPlans[0].SkillAssigns[12100] = new SkillData { SkillId = 12100, SkillLevel = 1 };
            Assert.Equal(1, FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(),
                "UPDATE role SET skill_plans = $plans WHERE id = $id",
                ("$plans", saved.ToByteArray()), ("$id", roleId)));
        }

        await using var again = await Enter("AttackPlan", create: false);
        var restored = await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser);
        Assert.Equal(0, restored.CurrentPlanIndex);
        Assert.Equal(1001001, restored.CurrentAtkSkillId);
        var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == roleId);
        Assert.Equal(3, session.Progress!.Skills[11001]);
        Assert.DoesNotContain(12100, session.Progress.Skills.Keys);
        Assert.DoesNotContain(12100, session.SkillPlans!.SkillAssignPlans[0].SkillAssigns.Keys);
    }

    [ClientDataFact]
    public async Task StandaloneStatPlansAndGenreShareOneAtomicPersistedState()
    {
        await using (var player = await Enter("Scholar"))
        {
            await Next(player, GenreService.GenreSync, DBGenreIncrement.Parser);
            await Level("scholar", 40);
            using (var client = new HttpClient())
                (await client.PostAsync($"{http}/ro3emu/gm?account=scholar", new StringContent("!job 1100"))).EnsureSuccessStatusCode();

            var allocation = new AllocateAttrPointReq { PlanIndex = 1 };
            for (int stat = 1; stat <= 6; stat++) allocation.Points[stat] = stat == 1 ? 5 : 1;
            await player.SendGameAsync(CharacterService.AllocateAttrPoints, allocation);
            Assert.Equal(0, (await Next(player, CharacterService.AllocateAttrPoints, AllocateAttrPointRsp.Parser)).Result);

            await player.SendGameAsync(CharacterService.RenameStatPlan,
                new PlanAttrPointNameChangeReq { PlanIndex = 1, PlanName = "Tank" });
            Assert.Equal("Tank", (await Next(player, CharacterService.RenameStatPlan, PlanAttrPointNameChangeResq.Parser)).PlanName);
            await player.SendGameAsync(CharacterService.ChangeStatPlan, new PlanAttrPointChangeReq { DestPlanIndex = 1 });
            Assert.Equal(0, (await Next(player, CharacterService.ChangeStatPlan, PlanAttrPointChangeResq.Parser)).Result);

            await player.SendGameAsync(CharacterService.ResetAttrPoints, new ResetAttrPointReq { PlanIndex = 1 });
            var reset = await Next(player, CharacterService.ResetAttrPoints, ResetAttrPointResp.Parser);
            Assert.Equal(0, reset.Result);
            Assert.Equal(1, reset.PlanBaseProperty.BaseProperty[1]);

            await player.SendGameAsync(82827, new GenreAttrSettingSchoolReq { PlanId = 0, ProfessionId = 1100 });
            Assert.Equal(0, (await Next(player, 82827, GenreAttrSettingSchoolResp.Parser)).Result);
            await player.SendGameAsync(CharacterService.AllocateAttrPoints, allocation);
            Assert.Equal(0, (await Next(player, CharacterService.AllocateAttrPoints, AllocateAttrPointRsp.Parser)).Result);

            await player.SendGameAsync(CharacterService.ChangeStatPlan, new PlanAttrPointChangeReq { DestPlanIndex = 0 });
            Assert.Equal(0, (await Next(player, CharacterService.ChangeStatPlan, PlanAttrPointChangeResq.Parser)).Result);
            await player.SendGameAsync(82806, new SwitchGenrePlanReq
            {
                TargetPlan = 1, IsApply = true, ChildSystem = { [2] = 1 },
            });
            Assert.Equal(0, (await Next(player, 82806, SwitchGenrePlanResp.Parser)).Result);
            var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)player.Self!.Uuid);
            Assert.Equal(default, session.TotemSwitchUtc);
            Assert.Equal(default, session.EmblemPlanSwitchUtc);
            Assert.Equal(default, session.TalentPlanSwitchUtc);

            using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "ro3emu.db")}");
            db.Open();
            byte[] PersistedPlans()
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT stat_plans FROM role WHERE id = $id";
                command.Parameters.AddWithValue("$id", (long)player.Self!.Uuid);
                return (byte[])command.ExecuteScalar()!;
            }
            byte[] before = PersistedPlans();
            FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(),
                "CREATE TRIGGER genre_stat_abort BEFORE UPDATE OF skill_plans ON role BEGIN SELECT RAISE(ABORT, 'genre stat abort'); END");
            await player.SendGameAsync(82806, new SwitchGenrePlanReq
            {
                TargetPlan = 0, IsApply = true, ChildSystem = { [2] = 0 },
            });
            Assert.NotEqual(0, (await Next(player, 82806, SwitchGenrePlanResp.Parser)).Result);
            Assert.True(before.SequenceEqual(PersistedPlans()));
            Assert.Equal(default, session.TotemSwitchUtc);
            Assert.Equal(default, session.EmblemPlanSwitchUtc);
            Assert.Equal(default, session.TalentPlanSwitchUtc);
            FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER genre_stat_abort");
        }

        await using var again = await Enter("Scholar", create: false);
        var plans = await Next(again, CharacterService.BaseAttrSync, BaseAttrInfoIncrement.Parser);
        Assert.Equal(1, plans.CurPlanIndex);
        Assert.Equal("Tank", plans.AllPlanBaseProperty[1].All.PlanName);
        Assert.Equal(5, plans.AllPlanBaseProperty[1].All.BaseProperty[1]);
        Assert.Equal(1100, plans.AllPlanBaseProperty[0].All.SkillGenre);
        var genre = await Next(again, GenreService.GenreSync, DBGenreIncrement.Parser);
        Assert.Equal(1, genre.CurrentPlan);
        Assert.Equal(1, genre.GenreMap.All.SystemMap[1].GenrePlans[2]);
    }

    [ClientDataFact]
    public async Task StatsOnlyGenreApplyLeavesTalentSwitchAvailable()
    {
        await using var player = await Enter("TalentBuild");
        await Next(player, GenreService.GenreSync, DBGenreIncrement.Parser);
        await Level("talentbuild", 40);
        using (var client = new HttpClient())
            foreach (string command in new[] { "!job 1100", "!quest done 10101170", "!quest done 10103160" })
                (await client.PostAsync($"{http}/ro3emu/gm?account=talentbuild", new StringContent(command))).EnsureSuccessStatusCode();

        await player.SendGameAsync(82806, new SwitchGenrePlanReq
        {
            TargetPlan = 1, IsApply = true, ChildSystem = { [2] = 1 },
        });
        Assert.Equal(0, (await Next(player, 82806, SwitchGenrePlanResp.Parser)).Result);
        var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)player.Self!.Uuid);
        Assert.Equal(default, session.TalentPlanSwitchUtc);

        await player.SendGameAsync(SeasonTalentService.SetPlan, new SeasonTalentSetCurPlanReq { PlanId = 1 });
        Assert.Equal(0, (await Next(player, SeasonTalentService.SetPlan, SeasonTalentSetCurPlanResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task GenreEchoSwitchCommitsMatchingPlanAndItemMarkers()
    {
        long first, second;
        await using (var player = await Enter("EchoBuild"))
        {
            await Next(player, GenreService.GenreSync, DBGenreIncrement.Parser);
            await Level("echobuild", 40);
            using (var client = new HttpClient())
                foreach (string command in new[] { "!job 1100", "!quest done 10103040", "!item 1830101", "!item 1830101" })
                    (await client.PostAsync($"{http}/ro3emu/gm?account=echobuild", new StringContent(command))).EnsureSuccessStatusCode();
            var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)player.Self!.Uuid);
            var guids = session.Items.Where(i => i.Entry == 1830101).Select(i => i.Guid).Order().ToArray();
            Assert.Equal(2, guids.Length);
            (first, second) = (guids[0], guids[1]);

            await player.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = first });
            Assert.Equal(0, (await Next(player, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
            await player.SendGameAsync(EmblemService.SwitchPlan, new EmbleSwitchPlanReq { TargetIndex = 1 });
            Assert.Equal(0, (await Next(player, EmblemService.SwitchPlan, EmbleSwitchPlanResp.Parser)).Result);
            await player.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 1, EmblemGuid = second });
            Assert.Equal(0, (await Next(player, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
            session.EmblemPlanSwitchUtc = DateTime.UtcNow.AddSeconds(-4);
            await player.SendGameAsync(EmblemService.SwitchPlan, new EmbleSwitchPlanReq { TargetIndex = 0 });
            Assert.Equal(0, (await Next(player, EmblemService.SwitchPlan, EmbleSwitchPlanResp.Parser)).Result);
            session.EmblemPlanSwitchUtc = DateTime.UtcNow.AddSeconds(-4);
            DateTime before = session.EmblemPlanSwitchUtc;

            FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(),
                "CREATE TRIGGER genre_echo_abort BEFORE UPDATE OF skill_plans ON role BEGIN SELECT RAISE(ABORT, 'genre echo abort'); END");
            await player.SendGameAsync(82806, new SwitchGenrePlanReq { TargetPlan = 1, IsApply = true, ChildSystem = { [6] = 1 } });
            Assert.NotEqual(0, (await Next(player, 82806, SwitchGenrePlanResp.Parser)).Result);
            Assert.Equal(before, session.EmblemPlanSwitchUtc);
            Assert.Equal(0, session.Emblems.CurrentPlan);
            Assert.Equal(2, session.Items.Single(i => i.Guid == first).Attrs![10001]);
            Assert.Equal(0, session.Items.Single(i => i.Guid == second).Attrs![10001]);
            FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER genre_echo_abort");

            await player.SendGameAsync(82806, new SwitchGenrePlanReq { TargetPlan = 1, IsApply = true, ChildSystem = { [6] = 1 } });
            Assert.Equal(0, (await Next(player, 82806, SwitchGenrePlanResp.Parser)).Result);
            Assert.Equal(1, session.Emblems.CurrentPlan);
            Assert.Equal(0, session.Items.Single(i => i.Guid == first).Attrs![10001]);
            Assert.Equal(2, session.Items.Single(i => i.Guid == second).Attrs![10001]);
            DateTime switched = session.EmblemPlanSwitchUtc;
            Assert.True(switched > before);
            await player.SendGameAsync(82806, new SwitchGenrePlanReq { TargetPlan = 0, IsApply = true, ChildSystem = { [6] = 0 } });
            Assert.Equal(ErrorCodes.Cooldown, (await Next(player, 82806, SwitchGenrePlanResp.Parser)).Result);
            Assert.Equal(switched, session.EmblemPlanSwitchUtc);

            session.EmblemPlanSwitchUtc = DateTime.UtcNow.AddSeconds(-4);
            await player.SendGameAsync(82806, new SwitchGenrePlanReq { TargetPlan = 0, IsApply = true, ChildSystem = { [6] = 0 } });
            Assert.Equal(0, (await Next(player, 82806, SwitchGenrePlanResp.Parser)).Result);
        }

        await using var again = await Enter("EchoBuild", create: false);
        var saved = await Next(again, EmblemService.PlanSync, DBEmblemInfoIncrement.Parser);
        Assert.Equal(0, saved.CurrentPlan);
        Assert.Equal(first, saved.EmblePlans[0].All.SetUpEmblem[1]);
        Assert.Equal(second, saved.EmblePlans[1].All.SetUpEmblem[1]);
        var bag = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(2, bag.PackInfo[5].All.Items[first].ItemInfos[10001].LongVal);
        Assert.Equal(1, bag.PackInfo[5].All.Items[first].ItemInfos[10002].LongVal);
        Assert.Equal(0, bag.PackInfo[5].All.Items[second].ItemInfos[10001].LongVal);
        Assert.Equal(0, bag.PackInfo[5].All.Items[second].ItemInfos[10002].LongVal);
    }
}
