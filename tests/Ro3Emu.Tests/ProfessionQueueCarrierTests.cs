using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>自造職業與技能驗證 queue 載體的 wire 投射；不複製 client 資料，也不假造載體執行規則。</summary>
public sealed class ProfessionQueueCarrierTests
{
    private const int Career = 7100, NoCarrierCareer = 7200, InvalidCarrierCareer = 7300;
    private const int Carrier = 1999901, CarrierGroup = 19999, Group = 19972, Active = 1997201;
    private const int QueueSlot = (int)SkillWheelSlotType.SkillWheelSkillQueue;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CareerQueueCarrierSurvivesQueueEditsAndReconnectThenExplicitZeroRemovesIt()
    {
        await using var f = await Fixture.StartAsync();
        await using (var bot = await f.Host.InWorldAsync("Queue"))
        {
            await Next(bot, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.SyncAllMark);
            await f.SetJobAsync(Career);
            await f.AssertCatalogueOnly();
            AssertCarrier(await NextWheel(bot, m => m.SkillWheel.All.WhellSkills.ContainsKey(QueueSlot)));
            await bot.SendGameAsync(SkillService.QueueUpdate, new SkillQueueUpdateReq
            {
                Plan = 0, QueueIndex = 0, OpType = SkillLoadOperation.SkillLoadOpLoad,
                Info = new SkillData { SkillId = Group, SkillLevel = 1 },
            });
            Assert.Equal(0, (await Next(bot, SkillService.QueueUpdate, SkillQueueUpdateResp.Parser)).Result);
            var queued = await NextWheel(bot, m => m.SkillAssignPlans.All.Value[0].SkillQueue[0].SkillId == Group);
            AssertCarrier(queued);
            await f.AssertCatalogueOnly();
            Assert.Equal(Carrier, queued.SkillWheel.All.SkillQueue.SkillConfigId);
            Assert.NotEqual(Active, queued.SkillWheel.All.WhellSkills[QueueSlot].SkillConfigId);
        }

        await f.Host.RestartAsync();
        await using var again = await f.Host.InWorldAsync("Queue", create: false);
        var reconnected = await NextWheel(again, m => m.SkillWheel.All.WhellSkills.ContainsKey(QueueSlot));
        AssertCarrier(reconnected);
        await f.AssertCatalogueOnly();
        Assert.Equal(Group, reconnected.SkillAssignPlans.All.Value[0].SkillQueue[0].SkillId);
        await f.SetJobAsync(NoCarrierCareer);
        var removed = await NextWheel(again, m => !m.SkillWheel.All.WhellSkills.ContainsKey(QueueSlot));
        Assert.False(removed.OriginSkillWheel.All.WhellSkills.ContainsKey(QueueSlot));
        Assert.DoesNotContain(CarrierGroup, removed.SkillList.All.Value);
        await f.AssertCatalogueOnly();
    }

    [Fact]
    public async Task DisplayDoesNotGrantCarrierCombatEffectsAndWrongCarrierTypeIsNotProjected()
    {
        await using var f = await Fixture.StartAsync();
        await using var bot = await f.Host.InWorldAsync("Refuse");
        await Next(bot, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.SyncAllMark);
        await f.SetJobAsync(Career);
        await f.AssertCatalogueOnly();
        AssertCarrier(await NextWheel(bot, m => m.SkillWheel.All.WhellSkills.ContainsKey(QueueSlot)));
        var before = await f.Read(() => (f.Player.CurrentHp, f.Player.CurrentSp, f.Player.SkillCast, f.Player.SkillCds.Count));
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = Carrier });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var after = await f.Read(() => (f.Player.CurrentHp, f.Player.CurrentSp, f.Player.SkillCast, f.Player.SkillCds.Count));
        Assert.Equal(before, after);
        Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(CombatService.UseInSkill)
            || e.IsGame(CombatService.SkillHit));
        await f.SetJobAsync(InvalidCarrierCareer);
        var invalid = await NextWheel(bot, m => !m.SkillWheel.All.WhellSkills.ContainsKey(QueueSlot));
        Assert.False(invalid.OriginSkillWheel.All.WhellSkills.ContainsKey(QueueSlot));
        Assert.DoesNotContain(CarrierGroup, invalid.SkillList.All.Value);
    }

    private static void AssertCarrier(DBSkillDataIncrement state)
    {
        var actual = state.SkillWheel.All.WhellSkills[QueueSlot];
        Assert.Equal(Carrier, actual.SkillConfigId);
        Assert.False(actual.Enable);
        Assert.Contains(CarrierGroup, state.SkillList.All.Value);
        foreach (var plan in state.SkillAssignPlans.All.Value)
            Assert.False(plan.SkillAssigns.TryGetValue(CarrierGroup, out var assigned) && assigned.SkillLevel > 0);
        Assert.Equal(actual, state.OriginSkillWheel.All.WhellSkills[QueueSlot]);
    }

    private static async Task<DBSkillDataIncrement> NextWheel(BotClient bot, Func<DBSkillDataIncrement, bool> predicate)
    {
        await bot.SendGameAsync(1023, new PlayerAssetReq { AssetType = { 5 } });
        return await Next(bot, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.SyncAllMark && predicate(m));
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-profession-queue-" + Guid.NewGuid().ToString("N"));
        public SyntheticHost Host { get; private set; } = null!;
        public GameSession Player => Host.Service<SessionRegistry>().All().Single(s => s.Role != null);

        public static async Task<Fixture> StartAsync()
        {
            var f = new Fixture();
            f.WriteData();
            f.Host = await SyntheticHost.StartAsync(o => o.GameDataDir = f._root);
            return f;
        }

        public Task SetJobAsync(int career) => Host.Service<GameLoop>().InvokeAsync(async () =>
            await Host.Service<CharacterService>().SetJobAsync(Player, career));
        public Task<T> Read<T>(Func<T> read) => Host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));
        public Task AssertCatalogueOnly() => Host.Service<GameLoop>().InvokeAsync(() =>
        {
            var progress = Player.Progress!;
            var skills = Host.Service<SkillService>();
            Assert.Equal(0, skills.Level(progress, CarrierGroup));
            Assert.False(progress.Skills.ContainsKey(CarrierGroup));
            Assert.False(progress.GrantedSkills.ContainsKey(CarrierGroup));
            Assert.DoesNotContain(skills.Tree(progress.Career), slot => slot.Group == CarrierGroup);
            return Task.CompletedTask;
        });

        private void WriteData()
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            Write("JobConfig", new Dictionary<int, object>
            {
                [1000] = new { _iID = 1000, _iJobRank = 1 },
                [Career] = new { _iID = Career, _iJobRank = 2, _iParentId = 1000, _iQueueSkillId = Carrier },
                // 明確 0 必須蓋過父職；不能把父職載體自動套用所有職業。
                [NoCarrierCareer] = new { _iID = NoCarrierCareer, _iJobRank = 2, _iParentId = Career, _iQueueSkillId = 0 },
                [InvalidCarrierCareer] = new { _iID = InvalidCarrierCareer, _iJobRank = 2, _iParentId = 1000, _iQueueSkillId = Active },
            });
            Write("SkillConfig", new Dictionary<int, object>
            {
                [1000001] = new { _iID = 1000001, _iSkillID = 10000, _iLevel = 1, _iBasicType = 1 },
                [Carrier] = new
                {
                    _iID = Carrier, _iSkillID = 19999, _iLevel = 1, _iMode = 0, _iBasicType = 1,
                    _iSystemType = 6, _iInitialLevel = 1, _kScriptLogic = "jinengduilie1"
                },
                [Active] = new
                {
                    _iID = Active, _iSkillID = Group, _iLevel = 1, _iMode = 0, _iBasicType = 1,
                    _iInitialLevel = 1, _iMaxLevel = 1, _iQueue = 1
                },
            });
            Write("SkillTreeConfig", new Dictionary<int, object> { [1] = new { _iID = 1, _iJob = Career, _iY = 1, _iX1 = Group } });
        }

        private void Write(string table, Dictionary<int, object> rows) => File.WriteAllText(
            Path.Combine(_root, "config", table + ".json"), JsonSerializer.Serialize(rows.ToDictionary(
                p => p.Key.ToString(CultureInfo.InvariantCulture), p => p.Value)));

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }
}
