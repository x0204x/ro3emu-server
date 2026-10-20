using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Invented graph: protocol/ownership/rollback checks run without any proprietary client export.</summary>
public sealed class ProfessionTalentChoiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string configRoot = Path.Combine(Path.GetTempPath(), "ro3emu-profession-" + Guid.NewGuid().ToString("N"));
        public SyntheticHost Host { get; private set; } = null!;
        public GameSession Session => Host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        public SeasonTalentService Talents => Host.Service<SeasonTalentService>();

        public async Task Start()
        {
            foreach (string path in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(configRoot, Path.GetRelativePath(SyntheticData.Dir, path));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(path, dest);
            }
            Write("SeasonTalentTreeConfig", new[]
            {
                new { _iId = 10, _kMultiverseArray = new[] { 101 }, _iNeedLevel = 0 },
                new { _iId = 20, _kMultiverseArray = new[] { 101 }, _iNeedLevel = 2 },
            });
            object Node(int id, int type, int[] after, bool start = false) => new
            {
                _iId = id, _iType = type, _iTalentTreeID = 10, _iLevelGroupId = 100 + id,
                _iMaxLevel = 1, _iIsStartPoint = start ? 1 : 0, _kAfterids = after,
            };
            Write("SeasonTalentConfig", new Dictionary<string, object>
            {
                ["1"] = Node(1, 1, [2, 9], start: true),
                ["2"] = Node(2, 5, [3, 6, 8]),
                ["3"] = Node(3, 6, [4, 5]),
                ["4"] = Node(4, 7, []),
                ["5"] = Node(5, 7, []),
                ["6"] = Node(6, 6, [7]),
                ["7"] = Node(7, 7, []),
                ["8"] = Node(8, 6, []), // a generic branch without a selectable child
                ["9"] = Node(9, 5, [10]),
                ["10"] = Node(10, 6, [11]),
                ["11"] = Node(11, 7, []),
            });
            Write("SeasonTalentEffectConfig", new[] { 3, 4, 5, 6, 7, 8, 10, 11 }.Select(id => new
            {
                _iGroup = 100 + id, _iLevel = 1, _iJobId = new[] { id == 8 ? 0 : id is 6 or 7 ? 2000 : 1000 },
            }));
            Host = await SyntheticHost.StartAsync(o => { o.GameDataDir = configRoot; o.PlayBook = 101; });
        }

        private void Write(string table, object value) =>
            File.WriteAllText(Path.Combine(configRoot, "config", table + ".json"), JsonSerializer.Serialize(value));

        public Task Unlock(bool parentPurchased = true) => Host.Service<GameLoop>().InvokeAsync(async () =>
        {
            Session.Progress!.Career = 1000;
            Session.Tasks ??= new DBTaskInfo();
            Session.Tasks.CompleteTaskNum[10101170] = 1;
            Session.Tasks.CompleteTaskNum[10103160] = 1;
            Session.SeasonTalent!.Plan[0].TalentNodes[1].CurrentLevel = parentPurchased ? 1 : 0;
            await Talents.SyncAsync(Session);
        });

        public DBSeasonTalent Saved() => DBSeasonTalent.Parser.ParseFrom(
            Host.Service<RoleStore>().LoadSeasonTalent(Session.Role!.Id)!);

        public async ValueTask DisposeAsync()
        {
            if (Host != null) await Host.DisposeAsync();
            Directory.Delete(configRoot, true);
        }
    }

    private static async Task<int> Choose(BotClient bot, int node)
    {
        await bot.SendGameAsync(SeasonTalentService.Upgrade, new SeasonTalentUpgradeReq { TalentId = node });
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(SeasonTalentService.Upgrade), TimeSpan.FromSeconds(5));
        var reply = SeasonTalentUpgradeResp.Parser.ParseFrom(packet.Body);
        Assert.Equal(node, reply.TalentId);
        return reply.Result;
    }

    [Fact]
    public async Task LeafSelectionPersistsWholePathAndOnlyReplacesItsOwnChoice()
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Choice");
        await f.Unlock();
        Assert.False(f.Session.SeasonTalent!.UnlockedNode[4]); // the old handler refused here
        Assert.Equal(0, await Choose(bot, 11)); // an unrelated choice must survive replacements
        Assert.Equal(0, await Choose(bot, 4));
        var before = f.Saved();
        Assert.Equal(1, before.Plan[0].TalentNodes[2].CurrentLevel);
        Assert.Equal(1, before.Plan[0].TalentNodes[3].CurrentLevel);
        Assert.Equal(1, before.Plan[0].TalentNodes[4].CurrentLevel);
        Assert.True(before.Plan[0].TalentNodes[4].IsActivated);
        Assert.Equal(0, before.Plan[1].TalentNodes[4].CurrentLevel);
        Assert.False(before.UnlockedTrees[20]); // free choice markers are not purchased levels
        Assert.Equal(0, await Choose(bot, 4)); // retry does not increment or charge for the choice
        Assert.Equal(before, f.Saved());

        Assert.Equal(0, await Choose(bot, 5));
        Assert.Equal(0, f.Session.SeasonTalent.Plan[0].TalentNodes[4].CurrentLevel);
        Assert.Equal(1, f.Session.SeasonTalent.Plan[0].TalentNodes[5].CurrentLevel);
        Assert.Equal(0, await Choose(bot, 8)); // generic job-0 effect, no child to choose
        var after = f.Saved();
        Assert.Equal(0, after.Plan[0].TalentNodes[3].CurrentLevel);
        Assert.Equal(0, after.Plan[0].TalentNodes[5].CurrentLevel);
        Assert.Equal(1, after.Plan[0].TalentNodes[8].CurrentLevel);
        Assert.Equal(1, after.Plan[0].TalentNodes[11].CurrentLevel);
        // IsActivated is a projection of the active plan and is rebuilt on all plan views.
        // Only the active plan's saved levels may change.
        foreach (var (id, node) in before.Plan[1].TalentNodes)
            Assert.Equal(node.CurrentLevel, after.Plan[1].TalentNodes[id].CurrentLevel);
        Assert.Equal(after, f.Session.SeasonTalent);
        Assert.False(after.UnlockedTrees[20]);
    }

    [Fact]
    public async Task SelectionRequiresFeatureUnlockedParentAndCompleteJobCompatiblePath()
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("InvalidChoice");
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Choose(bot, 4));
        await f.Unlock(parentPurchased: false);
        Assert.Equal(ErrorCodes.TalentLocked, await Choose(bot, 4));
        await f.Unlock();
        var before = f.Session.SeasonTalent!.Clone();
        Assert.Equal(ErrorCodes.TalentInvalid, await Choose(bot, 3)); // branch has unselected children
        Assert.Equal(ErrorCodes.TalentInvalid, await Choose(bot, 7)); // another career's option
        Assert.Equal(ErrorCodes.TalentInvalid, await Choose(bot, 999));
        Assert.Equal(before, f.Session.SeasonTalent);
        Assert.Null(f.Host.Service<RoleStore>().LoadSeasonTalent(f.Session.Role!.Id));
    }

    [Fact]
    public async Task FailedReplacementKeepsPreviousLiveAndDurableChoice()
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Rollback");
        await f.Unlock();
        Assert.Equal(0, await Choose(bot, 4));
        var before = f.Saved();
        FixtureSql.Execute(f.Host.Service<AccountStore>(), """
            CREATE TRIGGER reject_profession BEFORE UPDATE OF season_talent ON role
            BEGIN SELECT RAISE(ABORT, 'deliberate choice failure'); END;
            """);
        Assert.Equal(ErrorCodes.Unknown, await Choose(bot, 5));
        Assert.Equal(before, f.Session.SeasonTalent);
        Assert.Equal(before, f.Saved());
        FixtureSql.Execute(f.Host.Service<AccountStore>(), "DROP TRIGGER reject_profession");
        Assert.Equal(0, await Choose(bot, 5));
        Assert.Equal(1, f.Saved().Plan[0].TalentNodes[5].CurrentLevel);
    }
}
