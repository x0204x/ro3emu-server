using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>DraculaWeddingService: Wedding Crashers (DailyActivity 3400) from the guild home's lottery to the lair's rescue.</summary>
public sealed class DraculaWeddingTests : IAsyncLifetime
{
    private const long Home = GuildHomeService.DefaultScene, Lair = DraculaWeddingService.Lair;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-dracula-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private readonly TestClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    /// <summary>
    /// Tuesday 2026-10-06 12:00 UTC, outside any occurrence; the flows open one with the !dracula test aid (the realm's
    /// opening day, and so its custom first week, depends on the machine's zone: SeasonService reads it as local time).
    /// </summary>
    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(long count) => Interlocked.Add(ref _seconds, count);
    }

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.SeasonStart = "2026-10-05"; o.PlayBook = 101;
        }, _clock);
        SureHitRolls.Install(_app); // lethal hits are prepared; a FLEE miss would only be noise
        Service<DraculaWeddingService>().Lottery = TimeSpan.Zero;
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);
    private Task<T> Read<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        await bot.EnterWorldAsync((await bot.CreateRoleAsync(name)).RoleBaseInfo.Id);
        await Gm(bot, "!lvl 99");
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

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);
    private static long Id(BotClient bot) => (long)bot.Self!.Uuid;

    private async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 400; i++)
        {
            if (await Read(condition)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(what);
    }

    private async Task<long> CreateGuild(BotClient founder, string name)
    {
        await founder.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = name, Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        return (await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
    }

    private static async Task Join(BotClient leader, BotClient member, long guild)
    {
        await leader.SendGameAsync(GuildService.Invite, new GuildInviteReq { TarPlayerID = { Id(member) } });
        var invite = await Next(member, GuildService.InviteNtf, GuildInviteNtf.Parser);
        await member.SendGameAsync(GuildService.InviteAck, new GuildInviteAckReq { GuildID = guild, Token = invite.Token, Accept = true });
        Assert.Equal(0, (await Next(member, GuildService.InviteAck, GuildInviteAckResp.Parser)).Result);
        await Next(member, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == guild);
    }

    private static async Task Load(BotClient bot, long scene)
    {
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == scene);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, r => r.SceneId == scene)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
    }

    private static async Task EnterHome(BotClient bot)
    {
        await bot.SendGameAsync(GuildHomeService.EnterHome, new GuildEnterHomeReq());
        Assert.Equal(0, (await Next(bot, GuildHomeService.EnterHome, GuildEnterHomeResp.Parser)).Result);
        await Load(bot, Home);
    }

    private static async Task<int> EnterLair(BotClient bot)
    {
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = Lair });
        return (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result;
    }

    /// <summary>The 30404 that runs LC_DunGeon_Actions.ClientPlayCutSceneStory(plot, true).</summary>
    private static Task<DungeonEventNotify> Story(BotClient bot, long scene, long plot) =>
        Next(bot, DungeonService.Event, DungeonEventNotify.Parser, e => (int)e.EventType == 6 && e.DungeonEntry == scene
            && e.CliFuncname == "ClientPlayCutSceneStory" && e.Params.Count == 2 && e.Params[0].IntParams.SequenceEqual([plot])
            && e.Params[1].BoolParams.SequenceEqual([true]));

    private async Task<IReadOnlyList<MonsterUnit>> WaveIn(long instance, int count)
    {
        for (int i = 0; i < 200; i++)
        {
            var alive = await Read(() => Service<World>().AllUnits().OfType<MonsterUnit>()
                .Where(m => m.InstanceId == instance && !m.Dead && m.Hp > 0).ToArray());
            if (alive.Length == count) return alive;
            await Task.Delay(20);
        }
        throw new TimeoutException($"The wave of {count} did not spawn in instance {instance}");
    }

    private async Task Kill(BotClient bot, MonsterUnit actor)
    {
        await OnLoop(async () =>
        {
            await Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(actor.X + 1, actor.Y, actor.Z, actor.Face));
            Service<MonsterService>().Engage(actor, Session(bot));
            actor.Hp = 1;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = actor.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.Dsts.Any(d => d.DstID == actor.Uid && d.IsDead));
    }

    private static async Task<int> Interact(BotClient bot, DungeonService.SceneActorUnit actor, int interaction)
    {
        await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        {
            NpcUid = actor.Uid, InteractionId = interaction, SceneConfigId = actor.SceneEntry,
        });
        return (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser,
            r => r.Req.NpcUid == actor.Uid && r.Req.InteractionId == interaction)).Result;
    }

    private Task Beside(BotClient bot, DungeonService.SceneActorUnit actor) =>
        OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(actor.X + 1, actor.Y, actor.Z, 0)));

    private MailService.Mailbox Mailbox(long role) => Service<RoleStore>().LoadMailbox(role) is { Length: > 0 } blob
        ? JsonSerializer.Deserialize<MailService.Mailbox>(blob) ?? new() : new();

    [ClientDataFact]
    public void TheLairPlanFollowsItsTaskChainAndTheWindowIsTheClientCountdowns()
    {
        var plan = Service<DungeonCatalog>().Dracula(Lair);
        Assert.NotNull(plan);
        var tasks = Service<GameData>().Table("TaskConfig").Values.Where(r => GameData.Int(r, "_iDungeonID") == Lair).ToArray();
        Assert.Equal(11, tasks.Length); // 9000100-9000110
        Assert.Equal(tasks.Length, plan.Waves.Count);
        // Chases end in the foes' task regions; fights spawn the elite with its wave group, then Dracula alone.
        long[] regions = [406, 407, 408, 409, 410];
        for (int i = 0; i < 5; i++)
        {
            var chase = plan.Waves[2 * i].Step;
            Assert.NotNull(chase);
            Assert.Equal(regions[i], Assert.Single(chase.Complete).Id);
            Assert.Empty(plan.Waves[2 * i].Spawns);
        }
        Assert.Equal([4114025, .. Enumerable.Repeat(4114026, 10)], plan.Waves[1].Spawns.Select(s => s.NpcId));
        Assert.Equal(4114027, plan.Waves[3].Spawns[0].NpcId);
        Assert.Equal(4114029, plan.Waves[5].Spawns[0].NpcId);
        Assert.Equal(4114031, plan.Waves[7].Spawns[0].NpcId);
        Assert.Equal(4114015, Assert.Single(plan.Waves[9].Spawns).NpcId);
        var key = plan.Waves[10].Step!;
        Assert.Equal([4114033, 4114034, 4114035, 4114036, 4114038], key.Actors.Select(a => a.NpcId));
        Assert.Equal(6000023, Assert.Single(key.Complete).Id);

        // DailyActivity 3400 is _iIsCustome with _iTimeCtrlId 1079: the custom rows open it once, the day after the
        // opening day (whatever its weekday) at 20:30-21:00; after the four custom weeks TimeLimitConfig 1079 (row 93)
        // opens it every Thursday 20:00-20:30.
        var schedule = Service<TimedTodoSchedule>();
        var opening = DateTimeOffset.FromUnixTimeSeconds(Service<ActivityClock>().ServerOpen).UtcDateTime.Date;
        var first = schedule.CurrentOrNext(DraculaWeddingService.Activity,
            new DateTimeOffset(opening.AddHours(12), TimeSpan.Zero).ToUnixTimeSeconds())!;
        Assert.Equal(new DateTimeOffset(opening.AddDays(1).AddHours(20.5), TimeSpan.Zero), DateTimeOffset.FromUnixTimeSeconds(first.Start));
        Assert.Equal(new DateTimeOffset(opening.AddDays(1).AddHours(21), TimeSpan.Zero), DateTimeOffset.FromUnixTimeSeconds(first.End));
        var later = schedule.CurrentOrNext(DraculaWeddingService.Activity,
            new DateTimeOffset(2026, 12, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds())!;
        Assert.Equal(new DateTimeOffset(2026, 12, 3, 20, 0, 0, TimeSpan.Zero), DateTimeOffset.FromUnixTimeSeconds(later.Start));
        Assert.Equal(new DateTimeOffset(2026, 12, 3, 20, 30, 0, TimeSpan.Zero), DateTimeOffset.FromUnixTimeSeconds(later.End));
    }

    [ClientDataFact]
    public async Task TheBrideIsDrawnInTheHomeAndTheKeyHolderRescuesHer()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(alice, "!quest accept 1602110"); // Guild Activities - Wedding Crashers, target [210010, 1]
        long guild = await CreateGuild(alice, "Brides");
        await Join(alice, bob, guild);
        // No occurrence yet: the lair is closed.
        Assert.Equal(ErrorCodes.ActivityPeriod, await EnterLair(alice));
        await EnterHome(alice);
        await EnterHome(bob);

        // The event opens: the lottery lands on one of the two members present; both get her entity, then the home story.
        await Gm(alice, "!dracula open");
        _clock.Advance(1);
        var select = await Next(alice, DraculaWeddingService.SelectBride, GuildDraculaSelectBrideNtf.Parser);
        Assert.Contains(select.BridePlayerId, new[] { Id(alice), Id(bob) });
        Assert.Equal((ulong)select.BridePlayerId, select.BrideEntity.Uuid); // the lottery stops on uuid == member id
        Assert.Equal(select.BridePlayerId, (await Next(bob, DraculaWeddingService.SelectBride, GuildDraculaSelectBrideNtf.Parser)).BridePlayerId);
        Assert.Equal(select.BridePlayerId, (await Next(alice, DraculaWeddingService.WeddingState, GuildDraculaWeddingStateNtf.Parser)).BridePlayerId);
        var open = await Next(alice, DungeonService.Event, DungeonEventNotify.Parser, e => e.Vars.Any(v => v.VarId == 1306));
        Assert.Equal([1L], open.Vars.Single(v => v.VarId == 1306).Vi64);
        await Story(alice, Home, 3001);
        await Story(bob, Home, 3001);

        // The story's action 3000 sends each client into the lair: one room for the guild.
        Assert.Equal(0, await EnterLair(alice));
        await Load(alice, Lair);
        var state = await Next(alice, DraculaWeddingService.WeddingState, GuildDraculaWeddingStateNtf.Parser);
        Assert.Equal(select.BridePlayerId, state.BridePlayerId);
        await Story(alice, Lair, 3003);
        long instance = await Read(() => Session(alice).InstanceId);
        Assert.NotEqual(0, state.Stage2SceneId);
        Assert.Equal(0, await EnterLair(bob));
        await Load(bob, Lair);
        Assert.Equal(instance, await Read(() => Session(bob).InstanceId));
        var info = await Next(bob, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser);
        Assert.Equal([select.BridePlayerId], info.Vars.Single(v => v.VarId == 1301).Vi64);
        // The tracker runs the lair's first task.
        Assert.Contains(await Read(() => Service<TaskService>().ExtraTasks.SelectMany(f => f(Session(bob))).ToArray()),
            t => t.TaskId == 9000100 && t.Status == TaskState.Running);

        var plan = await Read(() => Session(alice).DungeonAttempt!.Plan);
        var map = Service<WorldData>().Map(Lair)!;
        for (int wave = 0; wave < plan.Waves.Count - 1; wave++)
        {
            await Until(() => Session(alice).DungeonAttempt?.Wave == wave, $"wave {wave + 1}");
            if (plan.Waves[wave].Step is { } chase)
            {
                var region = map.Regions[chase.Complete[0].Id];
                await Gm(alice, FormattableString.Invariant($"!actors walk {region.X} {region.Z}"));
                continue;
            }
            await Gm(alice, "!hp 999999");
            foreach (var actor in await WaveIn(instance, plan.Waves[wave].Spawns.Count)) await Kill(alice, actor);
        }

        // The key: Alice did the fighting, Bob searches the bushes until one holds it.
        await Until(() => Session(alice).DungeonAttempt?.Wave == plan.Waves.Count - 1, "the key step");
        Assert.Contains(await Read(() => Service<TaskService>().ExtraTasks.SelectMany(f => f(Session(bob))).ToArray()),
            t => t.TaskId == 9000110);
        var actors = await Read(() => Session(alice).DungeonAttempt!.SceneActors.ToArray());
        Assert.Equal(5, actors.Length);
        var cage = actors.Single(a => a.NpcId == 4114038);
        await Beside(alice, cage);
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Interact(alice, cage, 6000023)); // no key yet
        foreach (var bush in actors.Where(a => a.NpcId != 4114038))
        {
            await Beside(bob, bush);
            Assert.Equal(0, await Interact(bob, bush, (int)Service<DungeonCatalog>().Interactions(bush.NpcId)[0]));
            if (await Read(() => Session(alice).DungeonAttempt!.Dracula!.Rescuer) != 0) break;
        }
        Assert.Equal(Id(bob), await Read(() => Session(alice).DungeonAttempt!.Dracula!.Rescuer));
        var found = await Next(alice, DungeonService.Event, DungeonEventNotify.Parser, e => e.Vars.Any(v => v.VarId == 1302 && v.Vi64[0] != 0));
        Assert.Equal([Id(bob)], found.Vars.Single(v => v.VarId == 1302).Vi64);
        Assert.True(await Read(() => Session(bob).Buffs.Any(b => b.ConfigId == 4121301)), "the key buff");
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Interact(alice, cage, 6000023)); // only its holder opens it
        await Beside(bob, cage);
        Assert.Equal(0, await Interact(bob, cage, 6000023));

        // 83642 before the 30403 success, whose cutscene shows the rescuer and the bride.
        var complete = await Next(alice, DraculaWeddingService.WeddingComplete, GuildDraculaWeddingCompleteNtf.Parser);
        Assert.Equal(Id(bob), complete.RescuerPlayerId);
        Assert.Equal((ulong)Id(bob), complete.RescuerEntity.Uuid);
        Assert.Equal(select.BridePlayerId, complete.BridePlayerId);
        Assert.Equal((ulong)select.BridePlayerId, complete.BrideEntity.Uuid);
        Assert.Equal((DungeonRes)1, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        var order = alice.Inbox.Snapshot().Where(e => e.IsGame(DraculaWeddingService.WeddingComplete) || e.IsGame(DungeonService.Result))
            .Select(e => e.IsGame(DungeonService.Result)).ToArray();
        Assert.Equal([false, true], order);
        Assert.Equal((DungeonRes)1, (await Next(bob, DungeonService.Result, DungeonResNotify.Parser)).Res);

        // Both members get the participant letter, the damage dealer the MVP letter; the guild quest counts the clear.
        await Until(() => Mailbox(Id(alice)).Letters.Count(l => l.TemplateId is 10208 or 10209) == 2
            && Mailbox(Id(bob)).Letters.Any(l => l.TemplateId == 10208), "the reward letters");
        Assert.DoesNotContain(Mailbox(Id(bob)).Letters, l => l.TemplateId == 10209);
        Assert.Contains(Mailbox(Id(alice)).Letters.Single(l => l.TemplateId == 10209).Attachments, a => a.Entry == 1300069);
        await Until(() => Session(alice).Tasks!.Infos.TryGetValue(1602110, out var quest) && quest.CompleteNum[0] == 1
            || TaskService.IsDelivered(Session(alice), 1602110), "quest target 210010");
        Assert.False(await Read(() => Session(bob).Buffs.Any(b => b.ConfigId == 4121301)), "the key is gone");
        // The occurrence is over for the guild: no second lair.
        Assert.Equal(ErrorCodes.ActivityPeriod, await EnterLair(alice));
    }

    [ClientDataFact]
    public async Task ALairStillRunningWhenTheEventEndsFailsWithoutRewards()
    {
        await using var alice = await InWorld("Alice");
        await CreateGuild(alice, "Late");
        await EnterHome(alice);
        await Gm(alice, "!dracula open");
        _clock.Advance(1);
        var select = await Next(alice, DraculaWeddingService.SelectBride, GuildDraculaSelectBrideNtf.Parser);
        Assert.Equal(Id(alice), select.BridePlayerId);
        await Story(alice, Home, 3001);
        Assert.Equal(0, await EnterLair(alice));
        await Load(alice, Lair);
        await Story(alice, Lair, 3003);

        _clock.Advance(30 * 60); // past the end of the (test) window
        var complete = await Next(alice, DraculaWeddingService.WeddingComplete, GuildDraculaWeddingCompleteNtf.Parser);
        Assert.Equal(0, complete.WinCamp);
        Assert.Equal(0, complete.RescuerPlayerId);
        Assert.Equal((DungeonRes)2, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        await Task.Delay(200);
        Assert.DoesNotContain(Mailbox(Id(alice)).Letters, l => l.TemplateId is 10208 or 10209);
        Assert.Equal(ErrorCodes.ActivityPeriod, await EnterLair(alice));
    }
}
