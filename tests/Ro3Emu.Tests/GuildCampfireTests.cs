using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed class GuildCampfireTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-campfire-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private readonly TestClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 19, 59, 50, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(int count) => Interlocked.Add(ref _seconds, count);
    }

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.SeasonStart = "2026-10-05"; o.PlayBook = 101;
        }, _clock);
        SureHitRolls.Install(_app); // lethal hits are prepared; a FLEE miss would only be noise
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => _app.Services.GetRequiredService<SessionRegistry>()
        .All().Single(s => s.Account == bot.Account && s.Role != null);

    private (int Receipts, long Cost, int Reason) FuelLedger(long role)
    {
        using var db = _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.AccountStore>().Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM operation_receipt WHERE command_kind='guild-campfire-fuel' AND actor_role=$role";
        cmd.Parameters.AddWithValue("$role", role);
        int receipts = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT COALESCE(SUM(e.delta),0),COALESCE(MAX(e.reason_code),0) FROM economy_entry e " +
            "JOIN operation_receipt r ON r.operation_id=e.operation_id " +
            "WHERE r.command_kind='guild-campfire-fuel' AND r.actor_role=$role AND e.asset_id=1201031";
        using var row = cmd.ExecuteReader();
        Assert.True(row.Read());
        return (receipts, row.GetInt64(0), row.GetInt32(1));
    }

    private Task Move(BotClient bot, Vec3 position) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
    {
        Session(bot).Position = position.Clone();
        return Task.CompletedTask;
    });

    private static string RepoRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "Ro3Emu.slnx"))) return current.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    [ClientDataFact]
    public async Task GuildRoomSeedAndOwnedFuelAreScopedPersistedAndTimeGated()
    {
        var alice = await InWorld("Alice");
        await Gm(alice, "!lvl 40");
        await alice.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Campers", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(alice, GuildService.Create, GuildCreateResp.Parser)).Result);
        await QuestProbe.AcceptAsync(_app, _http, "alice", 1602000); // [106018,1] "Guild Event - Campfire Quest"
        // The guild home itself is always open (GuildHomeService); before 20:00 it shows no event content.
        await alice.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = GuildCampfireService.Dungeon });
        Assert.Equal(0, (await Next(alice, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        var change = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
            r => r.SceneEntry == GuildCampfireService.Dungeon);
        Assert.NotEqual(0, change.SceneID);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = GuildCampfireService.Dungeon });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        await Task.Delay(100);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(GuildCampfireService.Stage)
            || e.IsGame(GameAoiSink.SyncAoiView) && AoIViewChangeData.Parser.ParseFrom(e.Body).Bases.Any(b => b.EntityTemplateID == 7020004));

        Assert.Equal(0, (await QuestProbe.ReadAsync(_app, "alice", 1602000))!.CompleteNum[0]); // no event yet
        // 20:00: the member already inside gets the fire, the seeds and stage 1.
        _clock.Advance(20);
        var ao = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            r => r.Bases.Any(b => b.EntityTemplateID == 7020004));
        var fire = ao.Bases.Single(b => b.EntityTemplateID == 7020004);
        var seed = ao.Bases.First(b => b.EntityTemplateID == 7020001);
        Assert.Equal(1, (await Next(alice, GuildCampfireService.Stage, GuildCampFireStageInfo.Parser)).NowStage);
        // Present at the bonfire as the event starts: quest target 106018 is met.
        await QuestProbe.DoneAsync(_app, "alice", 1602000); // delivery waits on its action 115

        await alice.SendGameAsync(GuildCampfireService.Info, new GetGuildCampFireReq());
        var info = await Next(alice, GuildCampfireService.Info, GetGuildCampFireRsp.Parser);
        Assert.Equal(1, info.NowStageInfo.NowStage);
        Assert.NotEmpty(info.ShowList);

        await alice.SendGameAsync(GuildCampfireService.Accelerant, new GuildCampFireAddAccelerantReq
        { CampFireId = (long)fire.Uuid, AcceleantCfgId = 1 });
        Assert.Equal(ErrorCodes.NpcTooFar,
            (await Next(alice, GuildCampfireService.Accelerant, GuildCampFireAddAccelerantRsp.Parser)).Result);

        await Move(alice, seed.MovementInfo.Pos);
        var collect = new NpcInteractionReq { NpcUid = (long)seed.Uuid, InteractionId = 41002104 };
        await alice.SendGameAsync(SceneService.NpcInteraction, collect);
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await alice.SendGameAsync(SceneService.NpcInteraction, collect);
        Assert.Equal(ErrorCodes.RewardClaimed,
            (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await alice.SendGameAsync(GuildCampfireService.PlayerInfo, new NullArg());
        Assert.Equal(1, (await Next(alice, GuildCampfireService.PlayerInfo, CampFirePlayerInfo.Parser,
            p => p.AddSeedCount == 1)).AddSeedCount);

        await Move(alice, fire.MovementInfo.Pos);
        var fuel = new GuildCampFireAddAccelerantReq { CampFireId = (long)fire.Uuid, AcceleantCfgId = 1 };
        await alice.SendGameAsync(GuildCampfireService.Accelerant, fuel);
        Assert.Equal(ErrorCodes.ItemsShort,
            (await Next(alice, GuildCampfireService.Accelerant, GuildCampFireAddAccelerantRsp.Parser)).Result);
        Assert.Equal(0, FuelLedger((long)alice.Self!.Uuid).Receipts);
        await Gm(alice, "!item 1201031 1");
        await alice.SendGameAsync(GuildCampfireService.Accelerant, fuel);
        Assert.Equal(0, (await Next(alice, GuildCampfireService.Accelerant, GuildCampFireAddAccelerantRsp.Parser)).Result);
        Assert.Equal((1, -1L, (int)EconomyReason.GuildActivityCost), FuelLedger((long)alice.Self!.Uuid));
        await alice.SendGameAsync(GuildCampfireService.Accelerant, fuel);
        Assert.Equal(ErrorCodes.ItemsShort,
            (await Next(alice, GuildCampfireService.Accelerant, GuildCampFireAddAccelerantRsp.Parser)).Result);
        Assert.Equal((1, -1L, (int)EconomyReason.GuildActivityCost), FuelLedger((long)alice.Self!.Uuid));

        _clock.Advance(30);
        Assert.Equal(2, (await Next(alice, GuildCampfireService.Stage, GuildCampFireStageInfo.Parser,
            r => r.NowStage == 2)).NowStage);
        var lucky = await Next(alice, GuildCampfireService.Lucky, GuildCampFireLuckyPlayerNotify.Parser);
        Assert.Equal((long)alice.Self!.Uuid, Assert.Single(lucky.PlayerList).PlayerId);

        MonsterUnit? fruit = null;
        for (int i = 0; i < 100 && fruit == null; i++)
        {
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            {
                fruit = _app.Services.GetRequiredService<World>().AllUnits().OfType<MonsterUnit>()
                    .FirstOrDefault(m => m.InstanceId == change.SceneID && m.Template.NpcId == 7020018 && !m.Dead);
                return Task.CompletedTask;
            });
            if (fruit == null) await Task.Delay(20);
        }
        Assert.NotNull(fruit);
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            fruit!.Hp = 1;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = fruit!.Pos, BStop = true, SpeedRate = 1 });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = fruit.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.Dsts.Any(d => d.DstID == fruit.Uid && d.IsDead));
        var nextFruit = await Next(alice, GuildCampfireService.Fruit, GuildCampFireCreateMonsterFruitNotify.Parser);
        Assert.Single(nextFruit.Infos);
        Assert.True(nextFruit.Infos[0].NpcGuid > 0);

        _clock.Advance(860); // 20:15, configured client boss stage 4
        Assert.Equal(4, (await Next(alice, GuildCampfireService.Stage, GuildCampFireStageInfo.Parser,
            r => r.NowStage == 4)).NowStage);
        MonsterUnit? boss = null;
        for (int i = 0; i < 100 && boss == null; i++)
        {
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            {
                boss = _app.Services.GetRequiredService<World>().AllUnits().OfType<MonsterUnit>()
                    .FirstOrDefault(m => m.InstanceId == change.SceneID && m.Template.NpcId == 7020012 && !m.Dead);
                return Task.CompletedTask;
            });
            if (boss == null) await Task.Delay(20);
        }
        Assert.NotNull(boss);
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            boss!.Hp = 1;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = boss!.Pos, BStop = true, SpeedRate = 1 });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = boss.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.Dsts.Any(d => d.DstID == boss.Uid && d.IsDead));
        await Next(alice, GuildCampfireService.BossDead, GuildCampFireBossDeadNotify.Parser);
        await alice.SendGameAsync(GuildCampfireService.Info, new GetGuildCampFireReq());
        Assert.True((await Next(alice, GuildCampfireService.Info, GetGuildCampFireRsp.Parser)).NowStageInfo.BossDead);

        await alice.DisposeAsync();
        await using var again = await InWorld("Alice", create: false);
        await again.SendGameAsync(GuildCampfireService.PlayerInfo, new NullArg());
        Assert.Equal(1, (await Next(again, GuildCampfireService.PlayerInfo, CampFirePlayerInfo.Parser)).AddSeedCount);
        _clock.Advance(600);
        // 20:25, after the event: the home is still enterable, and a room that never saw the event gets no Finish.
        await again.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = GuildCampfireService.Dungeon });
        Assert.Equal(0, (await Next(again, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        await Next(again, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == GuildCampfireService.Dungeon);
        await again.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = GuildCampfireService.Dungeon });
        Assert.Equal(0, (await Next(again, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        _clock.Advance(2);
        await Task.Delay(200);
        Assert.DoesNotContain(again.Inbox.Snapshot(), e => e.IsGame(GuildCampfireService.Finish));
    }

    [ClientDataFact]
    public async Task HeldFuelCommitDefersSameRoleGmItemWhileUnrelatedRoleMailProgresses()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(alice, "!lvl 40");
        await alice.SendGameAsync(GuildService.Create, new GuildCreateReq
        { Name = "Campers", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        Assert.Equal(0, (await Next(alice, GuildService.Create, GuildCreateResp.Parser)).Result);
        _clock.Advance(20);
        await alice.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = GuildCampfireService.Dungeon });
        Assert.Equal(0, (await Next(alice, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        _ = await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser,
            response => response.SceneEntry == GuildCampfireService.Dungeon);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = GuildCampfireService.Dungeon });
        Assert.Equal(0, (await Next(alice, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        var view = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            response => response.Bases.Any(unit => unit.EntityTemplateID == 7020004));
        var fire = view.Bases.Single(unit => unit.EntityTemplateID == 7020004);
        await Move(alice, fire.MovementInfo.Pos);
        await Gm(alice, "!item 1201031 1");
        long role = (long)alice.Self!.Uuid;
        Assert.Equal(1, _app.Services.GetRequiredService<CharacterService>().CountOf(Session(alice), 1201031));

        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var owners = _app.Services.GetRequiredService<OwnerRegistry>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gmDeferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? fuelTask = null, gmTask = null;
        int held = 0;
        try
        {
            accounts.BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/guild-campfire-fuel" || Interlocked.Exchange(ref held, 1) != 0)
                    return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            };
            owners.DeferredForTest = origin => { if (origin == "GM") gmDeferred.TrySetResult(); };
            var fuel = new GuildCampFireAddAccelerantReq
            { CampFireId = (long)fire.Uuid, AcceleantCfgId = 1 };
            fuelTask = loop.InvokeAsync(() => _app.Services.GetRequiredService<GuildCampfireService>()
                .AccelerantAsync(Session(alice), fuel));
            await entered.Task.WaitAsync(Wait);
            Assert.Equal(0, FuelLedger(role).Receipts);
            Assert.Equal(1, _app.Services.GetRequiredService<CharacterService>().CountOf(Session(alice), 1201031));
            // Bob's independent role-owned write can commit while Alice's fuel is parked.
            string letter = await _app.Services.GetRequiredService<MailService>()
                .SendRawAsync((long)bob.Self!.Uuid, "Unrelated", "While fuel is held").WaitAsync(Wait);
            Assert.NotEmpty(letter);
            gmTask = Gm(alice, "!item 1201031 1");
            await gmDeferred.Task.WaitAsync(Wait);
            Assert.False(gmTask.IsCompleted);
            Assert.Equal(0, FuelLedger(role).Receipts);
            release.TrySetResult();
            await fuelTask.WaitAsync(Wait);
            Assert.Equal(0, (await Next(alice, GuildCampfireService.Accelerant,
                GuildCampFireAddAccelerantRsp.Parser)).Result);
            await gmTask.WaitAsync(Wait);
            Assert.Equal((1, -1L, (int)EconomyReason.GuildActivityCost), FuelLedger(role));
            Assert.Equal(1, _app.Services.GetRequiredService<CharacterService>().CountOf(Session(alice), 1201031));
        }
        finally
        {
            release.TrySetResult();
            accounts.BeforeOwnedCommitForTest = null;
            owners.DeferredForTest = null;
            if (fuelTask != null) await fuelTask.WaitAsync(Wait);
            if (gmTask != null) await gmTask.WaitAsync(Wait);
        }
    }
}
