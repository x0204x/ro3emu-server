using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Ordinary player wire transactions against isolated playbook/config copies.</summary>
public sealed class SeasonTalentTests
{
    private const int Root = 10101001, Child = 10101002, Remnant = 1250900, MintedCoin = 2;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private sealed class ServerFixture : IAsyncDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "ro3emu-talent-" + Guid.NewGuid().ToString("N"));
        private WebApplication app = null!;
        private string http = "";

        public GameSession Session => app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role != null);
        public CharacterService Character => app.Services.GetRequiredService<CharacterService>();
        public AccountStore Accounts => app.Services.GetRequiredService<AccountStore>();
        public RoleStore Roles => app.Services.GetRequiredService<RoleStore>();
        public MapPoint TaskPoint(long scene, long point) => app.Services.GetRequiredService<WorldData>().Map(scene)!.Points[point];
        public string DatabaseFile => Path.Combine(dir, "state", "ro3emu.db");
        public string StateDir => Path.Combine(dir, "state");
        public string GameDataDir => Path.Combine(dir, "game");
        public GameLoop Loop => app.Services.GetRequiredService<GameLoop>();
        public SeasonTalentService Talents => app.Services.GetRequiredService<SeasonTalentService>();

        public async Task PrepareFirstProfessionChoice(BotClient bot)
        {
            await Gm("alice", "!job 1200");
            await Gm("alice", "!lvl 50");
            await Gm("alice", "!quest done 10101170");
            await Gm("alice", "!quest done 10103160");
            await Gm("alice", "!item 1250900 100000");
            await Gm("alice", "!item 2 1000000");
            Assert.False(Session.SeasonTalent!.UnlockedNode[10102003]);
            var data = app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>();
            foreach (var row in data.Table("SeasonTalentConfig").Values
                .OrderBy(r => Ro3Emu.Server.Data.GameData.Int(r, "_iId")))
            {
                long id = Ro3Emu.Server.Data.GameData.Int(row, "_iId");
                if (Ro3Emu.Server.Data.GameData.Int(row, "_iTalentTreeID") != 10101 && id is not (10102001 or 10102002)) continue;
                // Reach the choice through real paid protocol upgrades, without seeding node levels.
                for (int level = 0; level < Ro3Emu.Server.Data.GameData.Int(row, "_iMaxLevel"); level++)
                    Assert.Equal(0, await Upgrade(bot, (int)id));
            }
        }

        public async Task StartAsync(int playbook = 101, bool removeLevelTwo = false, bool extraMemberAttr = false,
            bool oathOpen = false, bool missingGraph = false, bool jobSpecificRow = false)
        {
            string config = Path.Combine(dir, "game", "config");
            Directory.CreateDirectory(config);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "config"), "*.json"))
                File.Copy(file, Path.Combine(config, Path.GetFileName(file)));
            // The quest unlock test reports a real nearby task NPC; include that map's authoritative points.
            string sceneEditor = Path.Combine(dir, "game", "sceneeditor");
            Directory.CreateDirectory(sceneEditor);
            File.Copy(Path.Combine(RepoRoot(), "data", "sceneeditor", "sc_izlude_001_sceneeditor.json"),
                Path.Combine(sceneEditor, "sc_izlude_001_sceneeditor.json"));

            if (missingGraph)
            {
                File.Delete(Path.Combine(config, "SeasonTalentTreeConfig.json"));
                File.Delete(Path.Combine(config, "SeasonTalentConfig.json"));
            }

            if (oathOpen)
            {
                // The export's 330 total-level gate is unreachable in this bounded ordinary-attr slice.
                // A private fixture reaches the configured oath cap without altering shared content.
                string treeFile = Path.Combine(config, "SeasonTalentTreeConfig.json");
                var trees = JsonNode.Parse(File.ReadAllText(treeFile))!.AsObject();
                trees["10104"]!["_iNeedLevel"] = 0;
                trees["10105"]!["_iNeedLevel"] = 0;
                File.WriteAllText(treeFile, trees.ToJsonString());
            }

            if (removeLevelTwo || extraMemberAttr || jobSpecificRow)
            {
                string effectFile = Path.Combine(config, "SeasonTalentEffectConfig.json");
                var effects = JsonNode.Parse(File.ReadAllText(effectFile))!.AsArray();
                JsonNode? firstRootEffect = null;
                foreach (var row in effects.ToList())
                {
                    if ((int?)row?["_iGroup"] != 110101) continue;
                    if ((int?)row?["_iLevel"] == 1) firstRootEffect = row;
                    if (removeLevelTwo && (int?)row?["_iLevel"] == 2) effects.Remove(row);
                    if (extraMemberAttr) row!["_kAttrs"]!.AsArray().Add(new JsonArray(1330, 10));
                }
                if (jobSpecificRow)
                {
                    var specific = firstRootEffect!.DeepClone();
                    specific["_iId"] = 900001;
                    specific["_iJobId"] = new JsonArray(1100);
                    specific["_kAttrs"] = new JsonArray(new JsonArray(101, 150));
                    effects.Add(specific);
                }
                File.WriteAllText(effectFile, effects.ToJsonString());
            }

            var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
            app = ServerHost.Create([], o =>
            {
                o.DataDir = Path.Combine(dir, "state");
                o.GameDataDir = Path.Combine(dir, "game");
                o.HttpPort = httpPort;
                o.MicroGatePort = microPort;
                o.GameGatePort = gamePort;
                o.PlayBook = playbook;
            });
            await app.StartAsync();
            http = $"http://127.0.0.1:{httpPort}";
        }

        public async Task<BotClient> InWorld(string name, bool create = true)
        {
            var bot = new BotClient(http, File.ReadAllText(Path.Combine(dir, "state", "gate_rsa_public.pem")));
            await bot.LoginAsync(name.ToLowerInvariant(), "pw");
            await bot.EnterGateAsync();
            long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
            await bot.EnterWorldAsync(id);
            return bot;
        }

        public async Task Gm(string account, string line)
        {
            using var httpClient = new HttpClient();
            var resp = await httpClient.PostAsync($"{http}/ro3emu/gm?account={account}", new StringContent(line));
            resp.EnsureSuccessStatusCode();
        }

        public async Task Unlock(string account)
        {
            await Gm(account, "!job 1100");
            await Gm(account, "!lvl 20");
            await Gm(account, "!quest done 10101170");
            await Gm(account, "!quest done 10103160"); // TaskService/RewardService grant 50 real remnants
        }

        public async ValueTask DisposeAsync()
        {
            if (app != null) { await app.StopAsync(); await app.DisposeAsync(); }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private static async Task<int> Upgrade(BotClient bot, int node)
    {
        await bot.SendGameAsync(SeasonTalentService.Upgrade, new SeasonTalentUpgradeReq { TalentId = node });
        return (await Next(bot, SeasonTalentService.Upgrade, SeasonTalentUpgradeResp.Parser)).Result;
    }

    [ClientDataFact]
    public async Task FirstProfessionChoiceCommitsItsPathAndReplacesOnlyItsOwnPlan()
    {
        await using var fixture = new ServerFixture();
        await fixture.StartAsync();
        var alice = await fixture.InWorld("Alice");
        await fixture.PrepareFirstProfessionChoice(alice);
        Assert.True(fixture.Session.SeasonTalent!.UnlockedNode[10102003]);
        Assert.Equal(0, await Upgrade(alice, 10102102)); // Rapid Pierce; client submits the leaf.
        var state = fixture.Session.SeasonTalent!;
        Assert.Equal(1, state.Plan[0].TalentNodes[10102003].CurrentLevel);
        Assert.Equal(1, state.Plan[0].TalentNodes[10102101].CurrentLevel);
        Assert.Equal(1, state.Plan[0].TalentNodes[10102102].CurrentLevel);
        Assert.Equal(0, state.Plan[1].TalentNodes[10102102].CurrentLevel);
        Assert.Equal(0, await Upgrade(alice, 10102106)); // Switch to Swift Sword / Critical Attack.
        state = fixture.Session.SeasonTalent!;
        Assert.Equal(0, state.Plan[0].TalentNodes[10102101].CurrentLevel);
        Assert.Equal(0, state.Plan[0].TalentNodes[10102102].CurrentLevel);
        Assert.Equal(1, state.Plan[0].TalentNodes[10102105].CurrentLevel);
        Assert.Equal(1, state.Plan[0].TalentNodes[10102106].CurrentLevel);
        var saved = DBSeasonTalent.Parser.ParseFrom(fixture.Roles.LoadSeasonTalent(fixture.Session.Role!.Id)!);
        Assert.Equal(state, saved);
        await alice.DisposeAsync();
        await using var again = await fixture.InWorld("Alice", create: false);
        await Next(again, SeasonTalentService.TalentSync, DBSeasonTalentIncrement.Parser);
        var restored = fixture.Session.SeasonTalent!.Plan[0];
        Assert.Equal(0, restored.TalentNodes[10102102].CurrentLevel);
        Assert.Equal(1, restored.TalentNodes[10102105].CurrentLevel);
        Assert.Equal(1, restored.TalentNodes[10102106].CurrentLevel);
        Assert.True(restored.TalentNodes[10102106].IsActivated);
    }

    [ClientDataFact]
    public async Task QuestRewardUnlocksFivePlansAndPaidLevelsPersistAcrossSwitchAndRelog()
    {
        await using var fixture = new ServerFixture();
        await fixture.StartAsync();
        var alice = await fixture.InWorld("Alice");
        var first = await Next(alice, SeasonTalentService.TalentSync, DBSeasonTalentIncrement.Parser);
        Assert.True(first.SyncAllMark);
        Assert.Equal(5, first.Plan.Count);
        Assert.Equal(232, first.TalentNodes.Count);
        Assert.Equal(232, first.UnlockedNode.Count);
        Assert.Equal(11, first.UnlockedTrees.Count);
        Assert.False(first.UnlockedTrees[10101].All);
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Upgrade(alice, Root));

        await fixture.Gm("alice", "!job 1100");
        await fixture.Gm("alice", "!lvl 20");
        await fixture.Gm("alice", "!quest done 10101170");
        int packetStart = alice.Inbox.Snapshot().Count;
        await fixture.Gm("alice", "!quest done 10103150"); // predecessor setup; target itself is completed by the normal client event
        var point = fixture.TaskPoint(10101, 1010316001);
        await fixture.Gm("alice", FormattableString.Invariant($"!warp 10101 {point.X} {point.Z}"));
        var warp = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = warp.SceneEntry });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        packetStart = alice.Inbox.Snapshot().Count;
        await alice.SendGameAsync(SceneService.ClientNpcInteract, new ClientNpcInteractReq
        {
            Areapointid = 1010316001, DialogID = 1010316001,
        });
        await Next(alice, SeasonTalentService.TalentSync, DBSeasonTalentIncrement.Parser,
            m => !m.SyncAllMark && m.UnlockedTrees.TryGetValue(10101, out var tree) && tree.HasAll && tree.All);
        await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser,
            m => m.FunctionOpenInfos.ContainsKey(1003));
        var opened = alice.Inbox.Snapshot().Skip(packetStart).ToList();
        int taskPacket = opened.FindIndex(e => e.IsGame(TaskService.TaskInfoSync));
        int talentPacket = opened.FindIndex(e => e.IsGame(SeasonTalentService.TalentSync));
        int functionPacket = opened.FindIndex(e => e.IsGame(FunctionService.FunctionOpenSync));
        Assert.True(taskPacket >= 0 && taskPacket < talentPacket && talentPacket < functionPacket);
        Assert.Equal(50, fixture.Character.CountOf(fixture.Session, Remnant));
        await fixture.Gm("alice", "!item 2 500");
        // The HTTP GM reply can finish before its 5009 reaches BotInbox under full-suite load.
        // Cross that wire boundary before attributing subsequent 5009 packets to a talent purchase.
        var topUp = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.Currency.TryGetValue(MintedCoin, out var c) && c.All == 500);
        Assert.Equal(500, topUp.Currency[MintedCoin].All);
        long hp0 = fixture.Character.Stats(fixture.Session)[101];

        for (int level = 1; level <= 3; level++)
        {
            int before = alice.Inbox.Snapshot().Count;
            Assert.Equal(0, await Upgrade(alice, Root));
            Assert.Equal(level, fixture.Session.SeasonTalent!.Plan[0].TalentNodes[Root].CurrentLevel);
            Assert.Equal(50 - 10 * level, fixture.Character.CountOf(fixture.Session, Remnant));
            Assert.Equal(500 - 100 * level, fixture.Character.CountOf(fixture.Session, MintedCoin));
            Assert.InRange(fixture.Character.Stats(fixture.Session)[101] - hp0, 100L * level, 100L * level + 3);
            var packets = alice.Inbox.Snapshot().Skip(before).ToList();
            var bagFrames = packets.Where(e => e.IsGame(CharacterService.ItemsSync)).ToArray();
            var bagDetails = bagFrames.Select(e =>
            {
                var b = DBItemsInfoIncrement.Parser.ParseFrom(e.Body);
                return $"v{b.Version}/coin{(b.Currency.TryGetValue(MintedCoin, out var c) ? c.All : -1)}/packs{string.Join(',', b.PackInfo.Keys)}";
            }).ToArray();
            Assert.True(bagFrames.Length == 1, $"level={level}; 5009 count={bagFrames.Length}; "
                + $"packet IDs={string.Join(',', packets.Select(e => e.MsgId))}; bag payloads={string.Join(';', bagDetails)}");
            var bagChange = DBItemsInfoIncrement.Parser.ParseFrom(packets.Single(e => e.IsGame(CharacterService.ItemsSync)).Body);
            Assert.Equal(500 - 100 * level, bagChange.Currency[MintedCoin].All);
            Assert.Equal(50 - 10 * level, bagChange.PackInfo[1].Inc.Items.Values
                .Single(v => v.Inc?.Stack == 50 - 10 * level).Inc.Stack);
            var talentChange = DBSeasonTalentIncrement.Parser.ParseFrom(packets.Single(e => e.IsGame(SeasonTalentService.TalentSync)).Body);
            Assert.Equal(level, talentChange.Plan[0].Inc.TalentNodes[Root].Inc.CurrentLevel);
            Assert.Equal(level, talentChange.TalentNodes[Root].Inc.CurrentLevel);
            var changedStats = OnePropertyChange.Parser.ParseFrom(packets.Single(e => e.IsGame(CharacterService.OnePropertyChange)).Body);
            Assert.Equal(fixture.Character.Stats(fixture.Session)[101],
                changedStats.Property.Propertys.Single(p => p.PropertyId == 101).Data);
        }
        long hp3 = fixture.Character.Stats(fixture.Session)[101];
        Assert.Equal(ErrorCodes.SkillMaxLevel, await Upgrade(alice, Root));
        Assert.Equal(20, fixture.Character.CountOf(fixture.Session, Remnant));

        await alice.SendGameAsync(SeasonTalentService.SetPlan, new SeasonTalentSetCurPlanReq { PlanId = 1 });
        Assert.Equal(0, (await Next(alice, SeasonTalentService.SetPlan, SeasonTalentSetCurPlanResp.Parser)).Result);
        Assert.Equal(0, fixture.Session.SeasonTalent!.Plan[1].TalentNodes[Root].CurrentLevel);
        Assert.Equal(hp0, fixture.Character.Stats(fixture.Session)[101]);
        Assert.Equal(ErrorCodes.TalentLocked, await Upgrade(alice, Child));
        await alice.SendGameAsync(SeasonTalentService.SetPlan, new SeasonTalentSetCurPlanReq { PlanId = 0 });
        Assert.Equal(ErrorCodes.Cooldown, (await Next(alice, SeasonTalentService.SetPlan, SeasonTalentSetCurPlanResp.Parser)).Result);
        await alice.SendGameAsync(SeasonTalentService.RenamePlan, new SeasonTalentSetPlanNameReq { PlanId = 1, PlanName = "Second" });
        Assert.Equal(0, (await Next(alice, SeasonTalentService.RenamePlan, SeasonTalentSetPlanNameResp.Parser)).Result);
        await Task.Delay(TimeSpan.FromSeconds(3.1));
        await alice.SendGameAsync(SeasonTalentService.SetPlan, new SeasonTalentSetCurPlanReq { PlanId = 0 });
        Assert.Equal(0, (await Next(alice, SeasonTalentService.SetPlan, SeasonTalentSetCurPlanResp.Parser)).Result);
        Assert.Equal(hp3, fixture.Character.Stats(fixture.Session)[101]);

        await alice.DisposeAsync();
        await using var again = await fixture.InWorld("Alice", create: false);
        var saved = await Next(again, SeasonTalentService.TalentSync, DBSeasonTalentIncrement.Parser);
        Assert.True(saved.SyncAllMark);
        Assert.Equal(3, saved.Plan[0].All.TalentNodes[Root].CurrentLevel);
        Assert.Equal(0, saved.Plan[1].All.TalentNodes[Root].CurrentLevel);
        Assert.Equal("Second", saved.Plan[1].All.PlanName);
        Assert.Equal(hp3, fixture.Character.Stats(fixture.Session)[101]);
    }

    [ClientDataFact]
    public async Task InvalidPathPlanNameAndMissingEffectNeverCharge()
    {
        await using var fixture = new ServerFixture();
        await fixture.StartAsync(removeLevelTwo: true);
        await using var alice = await fixture.InWorld("Alice");
        await fixture.Unlock("alice");
        Assert.Equal(50, fixture.Character.CountOf(fixture.Session, Remnant));
        Assert.Equal(ErrorCodes.TalentInvalid, await Upgrade(alice, 99999999));
        Assert.Equal(ErrorCodes.TalentLocked, await Upgrade(alice, Child));
        Assert.Equal(ErrorCodes.CurrencyShort, await Upgrade(alice, Root));
        await fixture.Gm("alice", "!delitem 1250900 50");
        await fixture.Gm("alice", "!item 2 500");
        Assert.Equal(ErrorCodes.ItemsShort, await Upgrade(alice, Root));
        await fixture.Gm("alice", "!item 1250900 50");
        Assert.Equal(0, await Upgrade(alice, Root));
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Upgrade(alice, Root)); // no target level 2 row
        Assert.Equal(40, fixture.Character.CountOf(fixture.Session, Remnant));
        Assert.Equal(400, fixture.Character.CountOf(fixture.Session, MintedCoin));
        await alice.SendGameAsync(SeasonTalentService.SetPlan, new SeasonTalentSetCurPlanReq { PlanId = 5 });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, SeasonTalentService.SetPlan, SeasonTalentSetCurPlanResp.Parser)).Result);
        await alice.SendGameAsync(SeasonTalentService.RenamePlan, new SeasonTalentSetPlanNameReq { PlanId = 0, PlanName = "123456789" });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, SeasonTalentService.RenamePlan, SeasonTalentSetPlanNameResp.Parser)).Result);
        Assert.Equal(40, fixture.Character.CountOf(fixture.Session, Remnant));
    }

    [ClientDataFact]
    public async Task WrongPlaybookRefusesAndJobRankLossClearsTalentOnlyProperty()
    {
        await using (var wrong = new ServerFixture())
        {
            await wrong.StartAsync(playbook: 102);
            await using var bob = await wrong.InWorld("Bob");
            await wrong.Unlock("bob");
            await wrong.Gm("bob", "!item 2 500");
            Assert.False(wrong.Session.SeasonTalent!.UnlockedTrees[10101]);
            Assert.Equal(ErrorCodes.FeatureUnavailable, await Upgrade(bob, Root));
            Assert.Equal(50, wrong.Character.CountOf(wrong.Session, Remnant));
        }

        await using var fixture = new ServerFixture();
        await fixture.StartAsync(extraMemberAttr: true);
        await using var alice = await fixture.InWorld("Alice");
        await fixture.Unlock("alice");
        await fixture.Gm("alice", "!item 2 500");
        Assert.Equal(0, await Upgrade(alice, Root));
        Assert.Equal(10, fixture.Character.Stats(fixture.Session)[1330]);
        int start = alice.Inbox.Snapshot().Count;
        await fixture.Gm("alice", "!job 1000");
        Assert.Equal(0, fixture.Character.Stats(fixture.Session)[1330]);
        // The GM reply returns over HTTP; the stat packet may still be on its way to the bot.
        bool Cleared() => alice.Inbox.Snapshot().Skip(start)
            .Where(e => e.IsGame(CharacterService.OnePropertyChange))
            .Select(e => OnePropertyChange.Parser.ParseFrom(e.Body))
            .Any(m => m.Property.Propertys.Any(p => p.PropertyId == 1330 && p.Data == 0));
        for (int i = 0; i < 250 && !Cleared(); i++) await Task.Delay(20);
        Assert.True(Cleared());
        Assert.Equal(1, fixture.Session.SeasonTalent!.Plan[0].TalentNodes[Root].CurrentLevel); // durable during gate loss
        await fixture.Gm("alice", "!job 1100");
        Assert.Equal(10, fixture.Character.Stats(fixture.Session)[1330]);
    }

    [ClientDataFact]
    public async Task ExactJobEffectWinsAndOtherEligibleJobUsesGenericEffect()
    {
        await using var fixture = new ServerFixture();
        await fixture.StartAsync(jobSpecificRow: true);
        await using var alice = await fixture.InWorld("Alice");
        await fixture.Unlock("alice");
        await fixture.Gm("alice", "!item 2 500");
        long before = fixture.Character.Stats(fixture.Session)[101];
        Assert.Equal(0, await Upgrade(alice, Root));
        Assert.InRange(fixture.Character.Stats(fixture.Session)[101] - before, 150, 153);

        await fixture.Gm("alice", "!job 1400");
        long generic = fixture.Character.Stats(fixture.Session)[101];
        await alice.SendGameAsync(SeasonTalentService.SetPlan, new SeasonTalentSetCurPlanReq { PlanId = 1 });
        Assert.Equal(0, (await Next(alice, SeasonTalentService.SetPlan, SeasonTalentSetCurPlanResp.Parser)).Result);
        long empty = fixture.Character.Stats(fixture.Session)[101];
        Assert.InRange(generic - empty, 100, 103);
    }

    [ClientDataFact]
    public async Task OathUsesConfiguredCostAndGlobalLevelSurvivesPlanSwitchAndRelog()
    {
        await using var fixture = new ServerFixture();
        await fixture.StartAsync(oathOpen: true);
        var alice = await fixture.InWorld("Alice");
        await fixture.Gm("alice", "!job 1400");
        await fixture.Gm("alice", "!lvl 50");
        await fixture.Gm("alice", "!quest done 10101170");
        await fixture.Gm("alice", "!quest done 10103160");
        await fixture.Gm("alice", "!item 1250901 10");
        Assert.Equal(10, fixture.Session.SeasonTalent!.BattleOathMaxLevel);
        Assert.Equal(0, await Upgrade(alice, 10100000));
        Assert.Equal(0, fixture.Character.CountOf(fixture.Session, 1250901));
        Assert.Equal(1, fixture.Session.SeasonTalent.BattleOathLevel);
        Assert.Equal(1, fixture.Session.SeasonTalent.TalentNodes[10100000].CurrentLevel);
        Assert.All(fixture.Session.SeasonTalent.Plan.Values, p => Assert.Equal(1, p.TalentNodes[10100000].CurrentLevel));
        Assert.Equal(10, fixture.Character.Stats(fixture.Session)[1330]);
        Assert.Equal(10, fixture.Character.Stats(fixture.Session)[1331]);
        await alice.SendGameAsync(SeasonTalentService.SetPlan, new SeasonTalentSetCurPlanReq { PlanId = 1 });
        Assert.Equal(0, (await Next(alice, SeasonTalentService.SetPlan, SeasonTalentSetCurPlanResp.Parser)).Result);
        Assert.Equal(1, fixture.Session.SeasonTalent.Plan[1].TalentNodes[10100000].CurrentLevel);
        Assert.Equal(10, fixture.Character.Stats(fixture.Session)[1330]);
        await alice.DisposeAsync();

        await using var again = await fixture.InWorld("Alice", create: false);
        var full = await Next(again, SeasonTalentService.TalentSync, DBSeasonTalentIncrement.Parser);
        Assert.Equal(1, full.BattleOathLevel);
        Assert.Equal(1, full.TalentNodes[10100000].All.CurrentLevel);
        Assert.All(full.Plan.Values, p => Assert.Equal(1, p.All.TalentNodes[10100000].CurrentLevel));
        Assert.Equal(1, full.CurPlanId);
    }

    [ClientDataFact]
    public async Task MissingTalentGraphNeverAdvertisesFunctionOrEmpty5050()
    {
        await using var fixture = new ServerFixture();
        await fixture.StartAsync(missingGraph: true);
        await using var alice = await fixture.InWorld("Alice");
        await fixture.Unlock("alice");
        await fixture.Gm("alice", "!item 2 500");
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(SeasonTalentService.TalentSync));
        var functions = await Next(alice, FunctionService.FunctionOpenSync, FunctionOpenInfoIncrement.Parser);
        Assert.False(functions.FunctionOpenInfos.ContainsKey(1003));
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Upgrade(alice, Root));
        Assert.Equal(50, fixture.Character.CountOf(fixture.Session, Remnant));
    }

    [ClientDataFact]
    public async Task AbortedTalentWriteRollsBackItemCurrencyAndLevel()
    {
        await using var fixture = new ServerFixture();
        await fixture.StartAsync();
        await using var alice = await fixture.InWorld("Alice");
        await fixture.Unlock("alice");
        await fixture.Gm("alice", "!item 2 500");
        long roleId = fixture.Session.Role!.Id;
        FixtureSql.Execute(fixture.Accounts, """
                CREATE TRIGGER talent_abort BEFORE UPDATE OF season_talent ON role
                BEGIN SELECT RAISE(ABORT, 'deliberate talent write failure'); END;
                """);
        Assert.Equal(ErrorCodes.Unknown, await Upgrade(alice, Root));
        Assert.Equal(0, fixture.Session.SeasonTalent!.Plan[0].TalentNodes[Root].CurrentLevel);
        Assert.Equal(50, fixture.Character.CountOf(fixture.Session, Remnant));
        Assert.Equal(500, fixture.Character.CountOf(fixture.Session, MintedCoin));
        await alice.SendGameAsync(SeasonTalentService.SetPlan, new SeasonTalentSetCurPlanReq { PlanId = 1 });
        Assert.Equal(ErrorCodes.Unknown, (await Next(alice, SeasonTalentService.SetPlan, SeasonTalentSetCurPlanResp.Parser)).Result);
        Assert.Equal(0, fixture.Session.SeasonTalent.CurPlanId);
        await alice.SendGameAsync(SeasonTalentService.RenamePlan, new SeasonTalentSetPlanNameReq { PlanId = 1, PlanName = "Rejected" });
        Assert.Equal(ErrorCodes.Unknown, (await Next(alice, SeasonTalentService.RenamePlan, SeasonTalentSetPlanNameResp.Parser)).Result);
        Assert.Empty(fixture.Session.SeasonTalent.Plan[1].PlanName);
        using (var db = new SqliteConnection($"Data Source={fixture.DatabaseFile}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT currency, season_talent FROM role WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", roleId);
            using var row = cmd.ExecuteReader();
            Assert.True(row.Read());
            Assert.Contains("2:500", row.GetString(0));
            Assert.True(row.IsDBNull(1));
            row.Close();
            cmd.CommandText = "SELECT COALESCE(SUM(stack), 0) FROM item WHERE role_id = $id AND entry = 1250900";
            Assert.Equal(50L, (long)cmd.ExecuteScalar()!);
        }
        FixtureSql.Execute(fixture.Accounts, "DROP TRIGGER talent_abort");
        Assert.Equal(0, await Upgrade(alice, Root));
        Assert.Equal(40, fixture.Character.CountOf(fixture.Session, Remnant));
        Assert.Equal(400, fixture.Character.CountOf(fixture.Session, MintedCoin));
        await alice.SendGameAsync(SeasonTalentService.SetPlan, new SeasonTalentSetCurPlanReq { PlanId = 1 });
        Assert.Equal(0, (await Next(alice, SeasonTalentService.SetPlan, SeasonTalentSetCurPlanResp.Parser)).Result);
        Assert.Equal(1, fixture.Session.SeasonTalent!.CurPlanId); // failed switch did not start cooldown
        await alice.SendGameAsync(SeasonTalentService.RenamePlan, new SeasonTalentSetPlanNameReq { PlanId = 1, PlanName = "Saved" });
        Assert.Equal(0, (await Next(alice, SeasonTalentService.RenamePlan, SeasonTalentSetPlanNameResp.Parser)).Result);
        Assert.Equal("Saved", fixture.Session.SeasonTalent.Plan[1].PlanName);
    }

    [ClientDataFact]
    public async Task TalentPurchaseReceiptExplainsBothMaterialAndCurrencyCost()
    {
        await using var fixture = new ServerFixture();
        await fixture.StartAsync();
        await using var alice = await fixture.InWorld("Alice");
        await fixture.Unlock("alice");
        await fixture.Gm("alice", "!item 2 500");
        var catalog = EconomyCatalog.Load(fixture.GameDataDir);
        var before = S3EconomyReader.Read(fixture.StateDir, catalog);
        Assert.Equal(0, await Upgrade(alice, Root));
        var after = S3EconomyReader.Read(fixture.StateDir, catalog);
        var window = S3EconomyReader.ReadLedger(fixture.StateDir, catalog, before, after)!;
        var receipt = Assert.Single(window.Receipts);
        Assert.Equal("cd13c/season-talent-upgrade",
            EconomyReceiptReader.Read(fixture.DatabaseFile, receipt.OperationId)!.CommandKind);
        Assert.Contains(window.Legs, leg => leg.Kind == EconomyLegKind.Sink &&
            leg.Asset.Id == Remnant && leg.Delta == -10);
        Assert.Contains(window.Legs, leg => leg.Kind == EconomyLegKind.Sink &&
            leg.Asset.Id == MintedCoin && leg.Delta == -100);
        Assert.Equal(1, fixture.Session.SeasonTalent!.Plan[0].TalentNodes[Root].CurrentLevel);
        Assert.Equal(40, fixture.Character.CountOf(fixture.Session, Remnant));
        Assert.Equal(400, fixture.Character.CountOf(fixture.Session, MintedCoin));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}
