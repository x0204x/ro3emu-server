using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Invented talent graph and effects: a chosen profession talent's special effects reach skills and stats.</summary>
public sealed class ProfessionTalentEffectTests : IAsyncDisposable
{
    private const int Skill = 900, SkillRow = 90001, Mastery = 901, Sword = 777001, Buff = 77001;
    // TalentProcs.WhileActive keys Auto Guard Enhance's talent buff to Auto Guard's group; the rows' values are invented.
    private const int AutoGuardEnhance = 1153701, Granted = 77002, AutoGuard = 77003, AutoGuardGroup = 11403;
    private static readonly int[] PlayBook = [101], Career = [1000], BranchEffects = [1001], OptionEffects = [1002],
        Ratio = [1000], Flat = [10], GrantedBuff = [Granted];
    private static readonly int[][] GrantedAttrs = [[103, 9]], GuardAttrs = [[105, 32], [106, 32]], AmplifyAttrs = [[1043, 1]];
    private static readonly string[] OptionValues = ["5"];
    // TalentProcs.BuffBonuses keys Magic Infusion's talent buff to Mystical Amplification's group 12201; values invented.
    private const int MagicInfusion = 1249301, Amplify = 77021, AmplifyGroup = 12201;
    private const int Guard = 77011, GuardGroup = 77010;
    private readonly string _configRoot = Path.Combine(Path.GetTempPath(), "ro3emu-talent-effects-" + Guid.NewGuid().ToString("N"));
    private SyntheticHost _host = null!;
    private GameSession Session => _host.Service<SessionRegistry>().All().Single(s => s.Role != null);

    private async Task Start()
    {
        foreach (string path in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(_configRoot, Path.GetRelativePath(SyntheticData.Dir, path));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(path, dest);
        }
        Write("SeasonTalentTreeConfig", new Dictionary<string, object> { ["10"] = new { _iId = 10, _kMultiverseArray = PlayBook, _iNeedLevel = 0 } });
        object Node(int id, int type, int[] after, bool start = false) => new
        {
            _iId = id, _iType = type, _iTalentTreeID = 10, _iLevelGroupId = 100 + id,
            _iMaxLevel = 1, _iIsStartPoint = start ? 1 : 0, _kAfterids = after,
        };
        Write("SeasonTalentConfig", new Dictionary<string, object>
        {
            ["1"] = Node(1, 1, [2], start: true),
            ["2"] = Node(2, 5, [3]),
            ["3"] = Node(3, 6, [4]),
            ["4"] = Node(4, 7, []),
        });
        Write("SeasonTalentEffectConfig", new object[]
        {
            new { _iGroup = 103, _iLevel = 1, _iJobId = Career, _kSpecialEffectList = BranchEffects },
            new { _iGroup = 104, _iLevel = 1, _iJobId = Career, _kSpecialEffectList = OptionEffects },
        });
        Write("SpecialEffectConfig", new object[]
        {
            // The branch: cooldown set, damage ratio added.
            // ... and BuffDictRef: the second _kChangeAttri pair of the group's buffs this player applies +50.
            new
            {
                _iID = 1001, _kSpecialEffect = new[]
                {
                    new long[] { 21, 0, 0, Skill, 0, 2, 4000 }, [21, 0, 0, Skill, 29, 1, 250], [12, 0, GuardGroup, 2, 1, 50, 1, 1],
                },
            },
            // The option: flat attr, sword-only attr (two sword kinds, once), skill-level attr, per-level attr, a buff's tuples.
            new
            {
                _iID = 1002, _kDescData = OptionValues, _kSpecialEffect = new[]
                {
                    new long[] { 1, 103, 7 }, [16, 2, 1, 3], [16, 3, 1, 3], [19, Mastery, 2, 4, 6], [22, 2, 103, 5, Mastery], [2, Buff], [2, AutoGuardEnhance],
                    [2, MagicInfusion],
                },
            },
        });
        Write("BuffConfig", new object[]
        {
            new { _iID = Buff, _kSkillAttributeList = new[] { new[] { "0", $"{Skill}", "30", "1", "40" } } },
            new { _iID = AutoGuardEnhance, _kLogicData = GrantedBuff },
            new
            {
                _iID = Granted, _kChangeAttri = GrantedAttrs,
                _kSkillAttributeList = new[] { new[] { "0", $"{Skill}", "30", "1", "5" } },
            },
            new { _iID = AutoGuard, _iBuffID = AutoGuardGroup },
            new { _iID = Guard, _iBuffID = GuardGroup, _kChangeAttri = GuardAttrs },
            new { _iID = MagicInfusion },
            new { _iID = Amplify, _iBuffID = AmplifyGroup, _kChangeAttri = AmplifyAttrs },
        });
        Write("SkillConfig", new[]
        {
            new
            {
                _iID = SkillRow, _iSkillID = Skill, _iLevel = 1, _iMode = 0, _iMaxLevel = 1, _iBasicType = 1, _iCD = 10000,
                _kDamageParam1 = Ratio, _kDamageParam2 = Flat,
            },
        });
        var items = JsonNode.Parse(File.ReadAllText(Path.Combine(_configRoot, "config", "ItemConfig.json")))!.AsObject();
        items[$"{Sword}"] = new JsonObject { ["_iID"] = Sword, ["_iType"] = 5, ["_iSubType"] = 2, ["_iBagTab"] = 2 };
        File.WriteAllText(Path.Combine(_configRoot, "config", "ItemConfig.json"), items.ToJsonString());
        _host = await SyntheticHost.StartAsync(o => { o.GameDataDir = _configRoot; o.PlayBook = 101; });
    }

    private void Write(string table, object value) =>
        File.WriteAllText(Path.Combine(_configRoot, "config", table + ".json"), JsonSerializer.Serialize(value));

    public async ValueTask DisposeAsync()
    {
        if (_host != null) await _host.DisposeAsync();
        Directory.Delete(_configRoot, true);
    }

    private Task<T> OnLoop<T>(Func<Task<T>> work) => _host.Service<GameLoop>().InvokeAsync(work);

    private Task<JsonElement> Effective() => OnLoop(() =>
        Task.FromResult(_host.Service<CombatService>().EffectiveSkillRow(Session, SkillRow)!.Value));

    private Task<long> Stat(int id) => OnLoop(async () =>
    {
        await _host.Service<CharacterService>().RefreshStatsAsync(Session);
        return _host.Service<CharacterService>().Stats(Session)[id];
    });

    private static async Task<int> Request(BotClient bot, uint id, Google.Protobuf.IMessage request, Func<byte[], int> result)
    {
        await bot.SendGameAsync(id, request);
        return result((await bot.Inbox.WaitAsync(e => e.IsGame(id), TimeSpan.FromSeconds(5))).Body);
    }

    [Fact]
    public async Task ChosenTalentChangesTheSkillAndTheStatsUntilThePlanChanges()
    {
        await Start();
        await using var bot = await _host.InWorldAsync("Talent");
        await OnLoop(async () =>
        {
            Session.Progress!.Career = 1000;
            Session.Progress.Skills[Skill] = 1;
            Session.Tasks ??= new DBTaskInfo();
            Session.Tasks.CompleteTaskNum[10101170] = 1;
            Session.Tasks.CompleteTaskNum[10103160] = 1;
            Session.SeasonTalent!.Plan[0].TalentNodes[1].CurrentLevel = 1;
            await _host.Service<SeasonTalentService>().SyncAsync(Session);
            return 0;
        });
        long patk = await Stat(103), str = await Stat(1), intelligence = await Stat(4);
        Assert.Equal(10000, GameData.Int(await Effective(), "_iCD"));

        Assert.Equal(0, await Request(bot, SeasonTalentService.Upgrade, new SeasonTalentUpgradeReq { TalentId = 4 },
            body => SeasonTalentUpgradeResp.Parser.ParseFrom(body).Result));
        var row = await Effective();
        Assert.Equal(4000, GameData.Int(row, "_iCD"));
        Assert.Equal(1250, row.GetProperty("_kDamageParam1")[0].GetInt64());
        Assert.Equal(50, row.GetProperty("_kDamageParam2")[0].GetInt64()); // from the option's buff
        // The client's skill panel learns the new cooldown from 31132.
        await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillAffectedEx)
            && SkillBeAffectedParamExNotify.Parser.ParseFrom(e.Body).AffectParamDict.TryGetValue(Skill, out var info)
            && info.AffectParam.TryGetValue(0, out var cd) && cd.AffectValue == 4000, TimeSpan.FromSeconds(5));

        // No sword and no Mastery yet: only the flat attr.
        Assert.Equal(patk + 7, await Stat(103));
        Assert.Equal(str, await Stat(1));
        Assert.Equal(intelligence, await Stat(4));
        await OnLoop(() =>
        {
            Session.Progress!.Skills[Mastery] = 3;
            Session.Items.Add(new InvItem { Guid = 9_000_001, Entry = Sword, Stack = 1, Grid = 1 });
            return Task.FromResult(0);
        });
        Assert.Equal(patk + 7 + 5 * 3, await Stat(103));
        Assert.Equal(str + 3, await Stat(1)); // "a sword or a two-handed sword" counts once
        Assert.Equal(intelligence + 6, await Stat(4));

        // "While [Auto Guard] is active ...": the granted buff's attrs and skill tuples follow Auto Guard.
        var buffs = _host.Service<BuffService>();
        await OnLoop(async () => { await buffs.AddAsync(Session, AutoGuard, 60_000, Session.Role!.Id); return 0; });
        Assert.Equal(patk + 7 + 5 * 3 + 9, await Stat(103));
        Assert.Equal(55, (await Effective()).GetProperty("_kDamageParam2")[0].GetInt64());
        await OnLoop(async () => { await buffs.RemoveAsync(Session, Session.Buffs.Single(b => b.ConfigId == AutoGuard)); return 0; });
        Assert.Equal(patk + 7 + 5 * 3, await Stat(103));
        Assert.Equal(50, (await Effective()).GetProperty("_kDamageParam2")[0].GetInt64());

        // The talent changes the buff this player applies: [105, 32], [106, 32 + 50].
        await OnLoop(async () => { await buffs.AddAsync(Session, Guard, 60_000, Session.Role!.Id); return 0; });
        var guard = await OnLoop(() => Task.FromResult(Session.Buffs.Single(b => b.ConfigId == Guard).Changes.ToArray()));
        Assert.Equal([(105L, 32d), (106L, 82d)], guard);

        // "[Mystical Amplification] grants ${1} Extra MATK.": the talent buff has no data, the description's value joins.
        await OnLoop(async () => { await buffs.AddAsync(Session, Amplify, 60_000, Session.Role!.Id); return 0; });
        var amplify = await OnLoop(() => Task.FromResult(Session.Buffs.Single(b => b.ConfigId == Amplify).Changes.ToArray()));
        Assert.Equal([(1043L, 1d), (1043L, 5d)], amplify);

        Assert.Equal(0, await Request(bot, SeasonTalentService.SetPlan, new SeasonTalentSetCurPlanReq { PlanId = 1 },
            body => SeasonTalentSetCurPlanResp.Parser.ParseFrom(body).Result));
        Assert.Equal(10000, GameData.Int(await Effective(), "_iCD"));
        Assert.Equal(patk, await Stat(103));
    }
}
