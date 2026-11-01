using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Google.Protobuf;

namespace Ro3Emu.Tests;

/// <summary>卡片授予技能的快捷列、實際施放與重新登入回歸。</summary>
public sealed class CardGrantedSkillTests : IAsyncLifetime
{
    private const int Lunatic = 1420100, Dagger = 2010121;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-card-grant-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _httpUrl = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = http;
            o.MicroGatePort = micro;
            o.GameGatePort = game;
        });
        Service<CombatService>().Rolls = new FixedCardRolls();
        await _app.StartAsync();
        _httpUrl = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private GameSession Session(string name) => Service<SessionRegistry>().All().Single(s => s.Role?.Name == name);

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var evt = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(evt.Body);
    }

    private async Task Gm(string account, string command)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_httpUrl}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private Task<T> OnLoop<T>(Func<T> action) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(action()));

    /// <summary>Socket observers run after the reply; poll the loop-owned state.</summary>
    private async Task Eventually(Func<bool> condition, string what)
    {
        for (int i = 0; i < 50; i++)
        {
            if (await OnLoop(condition)) return;
            await Task.Delay(100);
        }
        Assert.Fail("timed out: " + what);
    }

    private async Task<int> Insert(BotClient bot, int grid, int socket, long guid)
    {
        await bot.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq { EquipPos = (EquipGridType)grid, SlotIndex = socket, CardGuid = guid });
        return (await Next(bot, CardService.SlotInsert, EquipmentCardInsertResp.Parser)).Result;
    }

    private async Task WearDagger(BotClient bot, GameSession session)
    {
        long dagger = session.Items.Single(i => i.Entry == Dagger).Guid;
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
    }

    [ClientDataTheory]
    [InlineData(1430600, 20044)] // 治癒術
    [InlineData(1420605, 20042)] // 施毒
    public async Task CardSkillCanBeLoadedOnManualAndAutoBars(int card, int group)
    {
        await using var bot = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", $"!item {card} 1");
        Assert.Equal(0, await Insert(bot, 6, 1, session.Items.Single(i => i.Entry == card).Guid));
        await Eventually(() => session.Progress!.GrantedSkills.GetValueOrDefault(group) == 1, "卡片技能授予");
        foreach (bool manual in new[] { true, false })
        {
            await bot.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
            {
                OpType = SkillLoadOperation.SkillLoadOpLoad, IsManual = manual, SocketNum = 5,
                Info = new SkillData { SkillId = group, SkillLevel = 1 }
            });
            Assert.Equal(0, (await Next(bot, SkillService.SkillLoad, SkillLoadResp.Parser)).Result);
            var state = await OnLoop(() => Service<SkillService>().Build(session));
            var plan = state.SkillAssignPlans[state.CurrentPlanIndex];
            Assert.Equal(group, (manual ? plan.ManualLoadSkill : plan.AutoLoadSkill)[5].SkillId);
            if (manual) Assert.Equal(group * 100 + 1, state.SkillWheel.WhellSkills[6].SkillConfigId);
            Assert.DoesNotContain(group, session.Progress!.Skills.Keys);
        }
        await bot.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
        {
            OpType = SkillLoadOperation.SkillLoadOpLoad, IsManual = true, SocketNum = 4,
            Info = new SkillData { SkillId = group, SkillLevel = 2 }
        });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(bot, SkillService.SkillLoad, SkillLoadResp.Parser)).Result);
        await CastGranted(bot, session, group);
        await bot.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)6, SlotIndex = 1 });
        Assert.Equal(0, (await Next(bot, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
        await Eventually(() => session.Progress!.GrantedSkills.Count == 0, "拔卡收回技能");
        var removed = await OnLoop(() => Service<SkillService>().Build(session));
        var removedPlan = removed.SkillAssignPlans[removed.CurrentPlanIndex];
        Assert.DoesNotContain(removedPlan.ManualLoadSkill.Concat(removedPlan.AutoLoadSkill), x => x.SkillId == group);
        Assert.DoesNotContain(removed.SkillWheel.WhellSkills.Values, x => x.SkillConfigId == group * 100 + 1);
        await bot.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
        {
            OpType = SkillLoadOperation.SkillLoadOpLoad, IsManual = true, SocketNum = 5,
            Info = new SkillData { SkillId = group, SkillLevel = 1 }
        });
        Assert.NotEqual(0, (await Next(bot, SkillService.SkillLoad, SkillLoadResp.Parser)).Result);
        await OnLoop(() => { session.SkillCds.Clear(); session.SkillModeCds.Clear(); return 0; });
        await Task.Delay(1100);
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = group * 100 + 1 });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task SocketedSkillBarSurvivesEquipmentChangeAndRelogWithoutPermanentLearning()
    {
        const int card = 1430601, group = 20040;
        await using (var bot = await InWorld("Alice"))
        {
            var session = Session("Alice");
            await Gm("alice", "!lvl 80");
            await Gm("alice", $"!item {card} 1");
            Assert.Equal(0, await Insert(bot, 6, 1, session.Items.Single(i => i.Entry == card).Guid));
            await Eventually(() => session.Progress!.GrantedSkills.ContainsKey(group), "插卡");
            foreach (bool manual in new[] { true, false })
            {
                await bot.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
                {
                    OpType = SkillLoadOperation.SkillLoadOpLoad, IsManual = manual, SocketNum = 5,
                    Info = new SkillData { SkillId = group, SkillLevel = 1 }
                });
                Assert.Equal(0, (await Next(bot, SkillService.SkillLoad, SkillLoadResp.Parser)).Result);
            }
            // 卡槽是角色方案的一部分；換主手裝備不應移除仍在卡槽中的技能。
            await WearDagger(bot, session);
            Assert.Equal(2004001, (await OnLoop(() => Service<SkillService>().Build(session))).SkillWheel.WhellSkills[6].SkillConfigId);
            Assert.DoesNotContain(group, session.Progress!.Skills.Keys);
            var saved = DBSkillData.Parser.ParseFrom(Service<RoleStore>().LoadSkillPlans(session.Role!.Id));
            Assert.DoesNotContain(group, saved.SkillAssignPlans[0].SkillAssigns.Keys);
        }
        await using (var again = await InWorld("Alice", create: false))
        {
            var session = Session("Alice");
            var state = await OnLoop(() => Service<SkillService>().Build(session));
            Assert.Equal(group, state.SkillAssignPlans[state.CurrentPlanIndex].ManualLoadSkill[5].SkillId);
            Assert.Equal(group, state.SkillAssignPlans[state.CurrentPlanIndex].AutoLoadSkill[5].SkillId);
            Assert.Equal(2004001, state.SkillWheel.WhellSkills[6].SkillConfigId);
            Assert.DoesNotContain(group, session.Progress!.Skills.Keys);
            await again.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2004001 });
            Assert.Equal(0, (await Next(again, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Eventually(() => session.Buffs.Any(b => b.ConfigId == 1500601), "重登後施放偽裝");
            await again.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)6, SlotIndex = 1 });
            Assert.Equal(0, (await Next(again, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
            await Eventually(() => session.Progress!.GrantedSkills.Count == 0, "重登前拔卡");
        }
        await using var last = await InWorld("Alice", create: false);
        var finalSession = Session("Alice");
        Assert.Empty(finalSession.Progress!.GrantedSkills);
        var finalState = await OnLoop(() => Service<SkillService>().Build(finalSession));
        Assert.DoesNotContain(finalState.SkillWheel.WhellSkills.Values, x => x.SkillConfigId == 2004001);
        await last.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2004001 });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(last, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    private async Task CastGranted(BotClient bot, GameSession session, int group)
    {
        if (group == 20044)
        {
            await OnLoop(() => { session.CurrentHp = 10; return 0; });
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2004401, TargetId = session.Role!.Id });
            Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
                m => m.SkillId == 2004401 && m.Dsts.Any(d => d.DstID == session.Role.Id && d.DamageType == DamageType.Heal));
            Assert.True(session.CurrentHp > 10);
        }
        else
        {
            var monster = await Target(session);
            var hp = monster.Hp;
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2004201, TargetId = monster.Uid });
            Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
                m => m.SkillId == 2004201 && m.Dsts.Any(d => d.DstID == monster.Uid && d.Damage > 0));
            Assert.True(monster.Hp < hp);
        }
    }

    private Task<MonsterUnit> Target(GameSession session) => Service<GameLoop>().InvokeAsync(async () =>
    {
        var monsters = Service<MonsterService>();
        var template = monsters.Template(5000007)! with { MaxHp = 1_000_000, RoamDistance = 0, SurroundDistance = 0, SpeedCm = 0 };
        var monster = await monsters.SpawnSingleAsync(session.SceneEntry, 5000007,
            new MapPoint(session.Position.X, session.Position.Y, session.Position.Z, 0), session.InstanceId, customTemplate: template);
        return monster!;
    });

    private sealed class FixedCardRolls : CombatRolls
    {
        public override double Hit() => 0;
        public override double DamageFloat() => 0.5;
        public override double Crit() => 1;
    }

    [ClientDataFact]
    public async Task SocketedPrimaryAndFlatStatsRaiseRealHitsAndPercentHpChangesCombatMaximum()
    {
        await using var bot = await InWorld("Alice");
        var session = Session("Alice");
        var character = Service<CharacterService>();
        await Gm("alice", "!lvl 80");
        await WearDagger(bot, session);
        await OnLoop(() => { Service<CombatService>().Rolls = new FixedCardRolls(); return 0; });
        var before = await OnLoop(() => character.Stats(session));
        long low = await Hit();
        await Gm("alice", "!item 1420108 1");
        Assert.Equal(0, await Insert(bot, 1, 1, session.Items.Single(i => i.Entry == 1420108).Guid));
        var after = await OnLoop(() => character.Stats(session));
        Assert.Equal(before[1] + 1, after[1]);
        Assert.True(after[103] >= before[103] + 7);
        Assert.True(await Hit() > low);

        await Gm("alice", "!item 1460300 1");
        long hpBefore = (await OnLoop(() => character.Stats(session)))[101];
        Assert.Equal(0, await Insert(bot, 3, 1, session.Items.Single(i => i.Entry == 1460300).Guid));
        var hpAfter = await OnLoop(() => character.Stats(session));
        Assert.True(hpAfter[101] > hpBefore);
        Assert.Equal(hpAfter[101], await OnLoop(() => (long)Service<CombatService>().Duel(session, Service<MonsterService>().Template(5000007)!).PlayerHp));

        async Task<long> Hit()
        {
            await Task.Delay(1100);
            var target = await Target(session);
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.Equal(0, accepted.Result);
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, m => m.SessionId == accepted.SessionId);
            await OnLoop(() => { session.Attack = null; return 0; });
            return Assert.Single(hit.Dsts, x => x.DstID == target.Uid).Damage;
        }
    }

    [ClientDataFact]
    public async Task CardSizePassiveAndStatusProcReachRealCombatPackets()
    {
        await using var bot = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await WearDagger(bot, session);
        var plain = await Hit();
        await Gm("alice", "!item 1460100 1");
        Assert.Equal(0, await Insert(bot, 1, 1, session.Items.Single(i => i.Entry == 1460100).Guid));
        await Eventually(() => session.Buffs.Any(b => b.ConfigId == 2050101), "體型增傷被動");
        var raised = await Hit();
        Assert.Equal(plain.Damage + (long)Math.Floor(plain.Damage * 0.075), raised.Damage);

        await Gm("alice", "!item 1420101 1");
        Assert.Equal(0, await Insert(bot, 1, 1, session.Items.Single(i => i.Entry == 1420101).Guid));
        await Eventually(() => session.Buffs.Any(b => b.ConfigId == 2000101), "暈眩被動");
        var effects = Service<CardEffects>();
        int rolls = 0;
        await OnLoop(() => { effects.Roll = _ => { rolls++; return 0; }; return 0; });
        try
        {
            var proc = await Hit();
            Assert.True(rolls > 0, "實際施放必須經過卡片觸發機率判定");
            Assert.Contains(session, proc.Target.Viewers);
            var updates = bot.Inbox.Snapshot().Where(x => x.IsGame(BuffService.BuffUpdate))
                .Select(x => BuffUpdateNotify.Parser.ParseFrom(x.Body)).ToArray();
            Assert.True(updates.Any(x => x.UnitId == proc.Target.Uid && x.Buffs.Any(b => b.ConfigId == 901601)),
                $"預期目標 {proc.Target.Uid} 暈眩，實際：" + string.Join(";", updates.Select(x => $"{x.UnitId}:{string.Join(',', x.Buffs.Select(b => b.ConfigId))}")));
            var update = updates.First(x => x.UnitId == proc.Target.Uid && x.Buffs.Any(b => b.ConfigId == 901601));
            Assert.Equal(500, Assert.Single(update.Buffs, b => b.ConfigId == 901601).Duration);
        }
        finally { await OnLoop(() => { effects.Roll = bound => Random.Shared.Next(bound); return 0; }); }

        async Task<(long Damage, MonsterUnit Target)> Hit()
        {
            await Task.Delay(1100);
            var target = await Target(session);
            Assert.InRange(target.Template.Size, 1, 3);
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
            Assert.Equal(0, accepted.Result);
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, m => m.SessionId == accepted.SessionId);
            // 普攻會自動追擊；每個案例只保留這一擊，避免換卡後舊目標先觸發共用被動冷卻。
            await OnLoop(() => { session.Attack = null; return 0; });
            return (Assert.Single(hit.Dsts, x => x.DstID == target.Uid).Damage, target);
        }
    }
}
