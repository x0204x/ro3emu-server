using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// GuildHuntingService: the Thursday 20:30 Hunting Challenge. Matching (43510), the guild's own Baphomet in its home with
/// the live rank (43511), commander skills (43515/43514/43512), the 5840 guard, settlement (43513), rank mails, the
/// history query (43516, 43504 branch 200) and the persisted week across restarts. The host and helpers; each subclass
/// picks the game data its servers start on.
/// </summary>
public abstract class GuildHuntingHost : IAsyncLifetime
{
    /// <summary>GameDataDir of every server the test starts.</summary>
    protected abstract string GameDataDir { get; }

    protected static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    protected const long Home = 4000101;
    protected readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-guildhunt-" + Guid.NewGuid().ToString("N"));
    protected readonly Clock clock = new();
    protected WebApplication app = null!;
    private string http = "";

    protected sealed class Clock : TimeProvider
    {
        // Thursday 2026-10-15, ten seconds before the 20:30 candidate phase (TimeLimitConfig 86).
        private long seconds = new DateTimeOffset(2026, 10, 15, 20, 29, 50, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Set(int hour, int minute, int second) => Interlocked.Exchange(ref seconds,
            new DateTimeOffset(2026, 10, 15, hour, minute, second, TimeSpan.Zero).ToUnixTimeSeconds());
    }

    protected T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    protected Task<T> OnLoop<T>(Func<T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    public Task InitializeAsync() => StartAsync();

    protected async Task StartAsync()
    {
        var (web, micro, gate) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = GameDataDir;
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = gate;
            o.SeasonStart = "2026-10-05"; o.PlayBook = 101;
        }, clock);
        SureHitRolls.Install(app); // the felling hit is prepared; a FLEE miss would only be noise
        await app.StartAsync();
        http = $"http://127.0.0.1:{web}";
    }

    protected async Task StopAsync()
    {
        await app.StopAsync(); await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public async Task DisposeAsync()
    {
        await StopAsync();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    protected async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        return bot;
    }

    protected static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    protected async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    protected async Task<long> Founder(BotClient bot, string guild)
    {
        await Gm(bot, "!lvl 40");
        await bot.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = guild, Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(bot, GuildService.Create, GuildCreateResp.Parser)).Result);
        return (await Next(bot, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
    }

    protected static async Task<long> EnterHome(BotClient bot)
    {
        await bot.SendGameAsync(GuildHomeService.EnterHome, new GuildEnterHomeReq());
        var reply = await Next(bot, GuildHomeService.EnterHome, GuildEnterHomeResp.Parser);
        Assert.Equal(0, reply.Result);
        await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == Home);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = Home });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, r => r.SceneId == Home)).Result);
        return reply.GuildSceneId;
    }

    protected async Task<MonsterUnit> Boss(long room)
    {
        for (int i = 0; i < 250; i++)
        {
            var boss = await OnLoop(() => Service<World>().AllUnits().OfType<MonsterUnit>()
                .FirstOrDefault(m => m.InstanceId == room && m.Template.NpcId == GuildHuntingService.BossNpc && !m.Dead));
            if (boss != null) return boss;
            await Task.Delay(20);
        }
        throw new TimeoutException("Baphomet did not spawn");
    }

    /// <summary>The role's Hunting Challenge letters (MailConfig 10200-10214), once at least <paramref name="count"/> arrived.</summary>
    protected async Task<List<MailService.Letter>> Letters(long role, int count)
    {
        var roles = Service<RoleStore>();
        List<MailService.Letter> Read() => roles.LoadMailbox(role) is { } bytes
            ? System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(bytes)!.Letters
                .Where(l => l.TemplateId is >= 10200 and <= 10214).ToList() : [];
        for (int i = 0; i < 250 && Read().Count < count; i++) await Task.Delay(20);
        return Read();
    }

    /// <summary>The newest guild_hunting row as committed: the week and the runtime's mailed flag and Hunters' damage.</summary>
    protected (DBGveHuntingActivityData Data, bool Mailed, long Damage) Saved(long guild)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dataDir, "ro3emu.db")};Mode=ReadOnly;Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT data, runtime FROM guild_hunting ORDER BY cycle_start DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (new DBGveHuntingActivityData(), false, 0);
        using var runtime = System.Text.Json.JsonDocument.Parse((byte[])reader[1]);
        long damage = runtime.RootElement.GetProperty("Scores").TryGetProperty(guild.ToString(), out var score)
            ? score.GetProperty("Damage").GetInt64() : 0;
        return (DBGveHuntingActivityData.Parser.ParseFrom((byte[])reader[0]), runtime.RootElement.GetProperty("Mailed").GetBoolean(), damage);
    }

    protected static async Task Until(Func<bool> done)
    {
        for (int i = 0; i < 250 && !done(); i++) await Task.Delay(20);
        Assert.True(done());
    }

    /// <summary>Lets game time pass a second at a time from the given time (up to a minute) until <paramref name="done"/>.</summary>
    protected async Task Elapse(int hour, int minute, int second, Func<bool> done)
    {
        for (int s = 0; s < 60 && !done(); s++)
        {
            clock.Set(hour, minute + (second + s) / 60, (second + s) % 60);
            for (int i = 0; i < 10 && !done(); i++) await Task.Delay(20);
        }
        Assert.True(done());
    }

    /// <summary>Alice's guild alone in this week's event, its 1000 damage committed by the periodic save.</summary>
    protected async Task<(BotClient Alice, long Guild)> ScoredWeek()
    {
        var alice = await InWorld("Alice");
        long hunters = await Founder(alice, "Hunters");
        long room = await EnterHome(alice);
        clock.Set(20, 32, 0);
        await Next(alice, GuildHuntingService.Matched, GveHuntingMatchedNotify.Parser);
        var boss = await Boss(room);
        await Service<GameLoop>().InvokeAsync(() => { boss.Hp = boss.Template.MaxHp - 1000; return Task.CompletedTask; });
        clock.Set(20, 32, 2);
        await Next(alice, GuildHuntingService.LiveRank, GveHuntingLiveRankNotify.Parser, r => r.Guilds.Single().Score == 1000);
        clock.Set(20, 32, 10); // past the periodic save
        await Until(() => Saved(hunters).Damage == 1000);
        return (alice, hunters);
    }
}

/// <summary>GuildHuntingService on the client's own data: matching, the boss, commander skills and the real rewards.
/// The persistence rules run on the synthetic fixture (<see cref="GuildHuntingPersistenceTests"/>).</summary>
public sealed class GuildHuntingTests : GuildHuntingHost
{
    protected override string GameDataDir => Path.Combine(ClientData.RepoRoot, "data");

    [ClientDataFact]
    public async Task MatchedGuildsRaceTheirBossesAndTheScorerIsRankedAndMailed()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long hunters = await Founder(alice, "Hunters");
        long idlers = await Founder(bob, "Idlers");
        long room = await EnterHome(alice);

        // 20:30 GveHuntCandidate: both guilds online are matched into one group, the preparation phase ends 20:32.
        clock.Set(20, 30, 0);
        var matched = await Next(alice, GuildHuntingService.Matched, GveHuntingMatchedNotify.Parser);
        Assert.Equal(new[] { hunters, idlers }.Order(), matched.GuildIds.Order());
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 20, 32, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), matched.CandidateEndMs);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 20, 37, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), matched.StartPhaseEndMs);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 20, 40, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), matched.SettlementPhaseEndMs);
        Assert.Equal(matched.GuildIds, (await Next(bob, GuildHuntingService.Matched, GveHuntingMatchedNotify.Parser)).GuildIds);
        // A racing guild cannot lose its roster before the result.
        await bob.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(ErrorCodes.GuildHunting, (await Next(bob, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);

        // 20:32 GveHuntStart: Alice's home gets its Baphomet, the commander's skills and the live rank 1..N.
        clock.Set(20, 32, 0);
        var boss = await Boss(room);
        var skills = await Next(alice, GuildHuntingService.Skills, GveHuntingSkillsNotify.Parser);
        Assert.Equal(3, skills.HuntingSkills.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, skills.HuntingSkills.Select(s => s.SlotID).Order());
        var rank = await Next(alice, GuildHuntingService.LiveRank, GveHuntingLiveRankNotify.Parser);
        Assert.Equal(new[] { 1, 2 }, rank.Guilds.Select(g => g.Rank).Order());

        await Service<GameLoop>().InvokeAsync(() => { boss.Hp = boss.Template.MaxHp - 1000; return Task.CompletedTask; });
        clock.Set(20, 32, 2);
        rank = await Next(alice, GuildHuntingService.LiveRank, GveHuntingLiveRankNotify.Parser,
            r => r.Guilds.Any(g => g.GuildId == hunters && g.Score == 1000));
        Assert.Equal(1, rank.Guilds.Single(g => g.GuildId == hunters).Rank);
        Assert.Equal(2, rank.Guilds.Single(g => g.GuildId == idlers).Rank);

        // A commander skill (the leader holds right 26) against the other guild, or the own-guild one at itself.
        var skill = skills.HuntingSkills.First(s => s.SkillConfigId != 16322201);
        await alice.SendGameAsync(GuildHuntingService.UseSkill, new UseGveHuntingSkillReq
        { ToGuildId = idlers, SkillConfigId = skill.SkillConfigId });
        var used = await Next(alice, GuildHuntingService.SkillUsed, UseHuntingSkillNtf.Parser);
        Assert.Equal((hunters, idlers, skill.SkillConfigId), (used.SourceGuildId, used.TargetGuildId, used.SkillConfigId));
        var after = await Next(alice, GuildHuntingService.Skills, GveHuntingSkillsNotify.Parser, n => n.HuntingSkills.Count == 2);
        Assert.DoesNotContain(after.HuntingSkills, s => s.SkillConfigId == skill.SkillConfigId);
        // The slot is spent: the same skill again is a parameter error in the framework envelope.
        await alice.SendGameAsync(GuildHuntingService.UseSkill, new UseGveHuntingSkillReq
        { ToGuildId = idlers, SkillConfigId = skill.SkillConfigId });
        var error = await Next(alice, 99999, APIErrorMsg.Parser, e => e.OriginMsgId == GuildHuntingService.UseSkill);
        Assert.Equal(ErrorCodes.Parameter, error.ErrorCode);

        // 20:37 GveHuntEnd: the boss leaves, both guilds get the group result and the server top (scorers only).
        clock.Set(20, 37, 0);
        var result = await Next(alice, GuildHuntingService.MatchResult, GveHuntingMatchResultNotify.Parser);
        Assert.Equal(new[] { (hunters, 1, 1000L), (idlers, 2, 0L) },
            result.MatchResults.OrderBy(r => r.Rank).Select(r => (r.GuildId, r.Rank, r.Score)));
        Assert.Equal(1, result.MatchResults.Single(r => r.GuildId == hunters).CommandSkillKill);
        Assert.Equal(hunters, Assert.Single(result.TopResults).GuildId);
        Assert.Equal(result.MatchResults, (await Next(bob, GuildHuntingService.MatchResult, GveHuntingMatchResultNotify.Parser)).MatchResults);
        for (int i = 0; i < 100 && await OnLoop(() => Service<World>().AllUnits().Contains(boss)); i++) await Task.Delay(20);
        Assert.False(await OnLoop(() => Service<World>().AllUnits().Contains(boss)));

        // Mails: group rank 1 (10200, GuildHuntRewardConfig 1001 -> Zeny 5000) and server rank 1 (10204, 1005 -> 102 x10000).
        var letters = await Letters((long)alice.Self!.Uuid, 2);
        var group = Assert.Single(letters, l => l.TemplateId == 10200);
        Assert.Equal(new MailService.Attachment(1, 5000), Assert.Single(group.Attachments));
        var top = Assert.Single(letters, l => l.TemplateId == 10204);
        Assert.Equal(new MailService.Attachment(102, 10000), Assert.Single(top.Attachments));
        Assert.Equal(2, letters.Count);
        Assert.Empty(await Letters((long)bob.Self!.Uuid, 0)); // no score, not in the home: nothing

        // After the result the roster is free again.
        await bob.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(0, (await Next(bob, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);

        // 43516 and 43504 branch 200 answer the settled week.
        await alice.SendGameAsync(GuildHuntingService.History, new GveHuntingHistoryQueryReq());
        var history = await Next(alice, GuildHuntingService.History, GveHuntingMatchResultNotify.Parser);
        Assert.Equal(result, history);
        await alice.SendGameAsync(DailyActivityService.RingAccept, new CommonDailyActivityReq
        { ActivityId = GuildHuntingService.Activity, RequestId = 7, GveHuntingQueryResult = new GveHuntingQueryResultReq() });
        var query = await Next(alice, DailyActivityService.RingAccept, CommonDailyActivityRes.Parser, r => r.RequestId == 7);
        Assert.Equal(0, query.Result);
        Assert.Equal(result, query.GveHuntingQueryResult);
    }

    [ClientDataFact]
    public async Task AFelledBossIsTheFullScoreAndDoesNotComeBack()
    {
        await using var alice = await InWorld("Alice");
        long hunters = await Founder(alice, "Hunters");
        long room = await EnterHome(alice);
        clock.Set(20, 32, 0);
        var boss = await Boss(room);
        await Service<GameLoop>().InvokeAsync(() => { boss.Hp = 1; return Task.CompletedTask; });
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = boss.Pos, BStop = true, SpeedRate = 1 });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = boss.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.Dsts.Any(d => d.DstID == boss.Uid && d.IsDead));
        clock.Set(20, 33, 0);
        var rank = await Next(alice, GuildHuntingService.LiveRank, GveHuntingLiveRankNotify.Parser,
            r => r.Guilds.Single().Score == boss.Template.MaxHp);
        Assert.Equal(hunters, rank.Guilds.Single().GuildId);
        Assert.True(rank.Guilds.Single().ReachTs > 0);
        clock.Set(20, 33, 2);
        await Next(alice, GuildHuntingService.LiveRank, GveHuntingLiveRankNotify.Parser,
            r => r.Guilds.Single().Score == boss.Template.MaxHp);
        Assert.False(await OnLoop(() => Service<World>().AllUnits().OfType<MonsterUnit>()
            .Any(m => m.InstanceId == room && m.Template.NpcId == GuildHuntingService.BossNpc && !m.Dead)));
    }
}

/// <summary>
/// GuildHuntingService's persistence and failure rules on the synthetic fixture (no client data): a result the database
/// does not hold is never announced or mailed, a failed save is retried, and a restart keeps the week and settles and
/// mails each thing once.
/// </summary>
public sealed class GuildHuntingPersistenceTests : GuildHuntingHost
{
    protected override string GameDataDir => SyntheticData.Dir;

    [Fact]
    public async Task AFailedSettlementSaveHoldsTheResultBackAndARestartSettlesItOnce()
    {
        var (alice, hunters) = await ScoredWeek();
        long role = (long)alice.Self!.Uuid;
        int failures = 0;
        Service<AccountStore>().BeforeCommitForTest = label =>
        {
            if (label != "guild-hunting/save") return;
            Interlocked.Increment(ref failures);
            throw new IOException("Test-only failure at commit.");
        };

        // 20:37 GveHuntEnd: the frozen result cannot be saved, then neither can the retry.
        clock.Set(20, 37, 0);
        await Until(() => Volatile.Read(ref failures) >= 1);
        await Elapse(20, 37, 1, () => Volatile.Read(ref failures) >= 2);
        await Task.Delay(300);
        // Nothing was announced or mailed from a result the database does not hold.
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(GuildHuntingService.MatchResult));
        Assert.Empty(await Letters(role, 0));
        Assert.False(Saved(hunters).Data.IsSettled);
        await alice.DisposeAsync();
        await StopAsync();

        // The restarted server settles the saved scores: one result, each letter once.
        await StartAsync();
        clock.Set(20, 38, 0);
        await using var again = await InWorld("Alice", create: false);
        await Until(() => Saved(hunters) is { Data.IsSettled: true, Mailed: true });
        await again.SendGameAsync(GuildHuntingService.History, new GveHuntingHistoryQueryReq());
        var result = await Next(again, GuildHuntingService.History, GveHuntingMatchResultNotify.Parser);
        var own = Assert.Single(result.MatchResults);
        Assert.Equal((hunters, 1, 1000L), (own.GuildId, own.Rank, own.Score));
        Assert.Equal(hunters, Assert.Single(result.TopResults).GuildId);
        Assert.Equal(new[] { 10200, 10204 }, (await Letters(role, 2)).Select(l => l.TemplateId).Order());
        clock.Set(20, 38, 10);
        await Task.Delay(300);
        Assert.Equal(2, (await Letters(role, 0)).Count);
    }

    [Fact]
    public async Task AFailedSaveAfterTheMailsIsRetriedAndARestartMailsNothingTwice()
    {
        var (alice, hunters) = await ScoredWeek();
        long role = (long)alice.Self!.Uuid;
        int saves = 0;
        Service<AccountStore>().BeforeCommitForTest = label =>
        {
            // The settlement commits; every save after it (the mailed flag and its retries) fails.
            if (label == "guild-hunting/save" && Interlocked.Increment(ref saves) > 1)
                throw new IOException("Test-only failure at commit.");
        };

        clock.Set(20, 37, 0);
        var result = await Next(alice, GuildHuntingService.MatchResult, GveHuntingMatchResultNotify.Parser);
        Assert.Equal(1000, Assert.Single(result.MatchResults).Score);
        Assert.Equal(new[] { 10200, 10204 }, (await Letters(role, 2)).Select(l => l.TemplateId).Order());
        await Until(() => Volatile.Read(ref saves) >= 2);
        Assert.Equal((true, false), (Saved(hunters).Data.IsSettled, Saved(hunters).Mailed));
        // The settled week's failed save is retried by the tick (and fails again here).
        await Elapse(20, 37, 1, () => Volatile.Read(ref saves) >= 3);
        Assert.False(Saved(hunters).Mailed);
        await alice.DisposeAsync();
        await StopAsync();

        // The restart finds the settled week with its mails unflagged: it mails again, the delivery keys skip both
        // letters, and the flag is saved.
        await StartAsync();
        clock.Set(20, 38, 0);
        await using var again = await InWorld("Alice", create: false);
        await Until(() => Saved(hunters).Mailed);
        Assert.Equal(2, (await Letters(role, 0)).Count);
        await again.SendGameAsync(GuildHuntingService.History, new GveHuntingHistoryQueryReq());
        Assert.Equal(result, await Next(again, GuildHuntingService.History, GveHuntingMatchResultNotify.Parser));
    }

    [Fact]
    public async Task ARestartKeepsTheGroupTheScoreAndTheResult()
    {
        var alice = await InWorld("Alice");
        long hunters = await Founder(alice, "Hunters");
        long room = await EnterHome(alice);
        clock.Set(20, 32, 0); // a server that first sees the event during combat still matches the online guilds
        await Next(alice, GuildHuntingService.Matched, GveHuntingMatchedNotify.Parser);
        var boss = await Boss(room);
        await Service<GameLoop>().InvokeAsync(() => { boss.Hp = boss.Template.MaxHp - 700; return Task.CompletedTask; });
        clock.Set(20, 32, 2);
        await Next(alice, GuildHuntingService.LiveRank, GveHuntingLiveRankNotify.Parser,
            r => r.Guilds.Single().Score == 700);
        clock.Set(20, 32, 10); // past the periodic save
        await Task.Delay(300);
        await alice.DisposeAsync();
        await StopAsync();

        await StartAsync();
        clock.Set(20, 33, 0);
        await using var again = await InWorld("Alice", create: false);
        room = await EnterHome(again);
        // Entering the home mid-event restores the HUD: the group, then the rank with the saved score.
        Assert.Equal(hunters, Assert.Single((await Next(again, GuildHuntingService.Matched, GveHuntingMatchedNotify.Parser)).GuildIds));
        boss = await Boss(room);
        Assert.Equal(boss.Template.MaxHp - 700, boss.Hp);
        clock.Set(20, 37, 0);
        var result = await Next(again, GuildHuntingService.MatchResult, GveHuntingMatchResultNotify.Parser);
        Assert.Equal(700, Assert.Single(result.MatchResults).Score);
        await again.DisposeAsync();
        await StopAsync();

        await StartAsync();
        clock.Set(21, 30, 0);
        await using var later = await InWorld("Alice", create: false);
        await later.SendGameAsync(GuildHuntingService.History, new GveHuntingHistoryQueryReq());
        Assert.Equal(result, await Next(later, GuildHuntingService.History, GveHuntingMatchResultNotify.Parser));
        // The mails left after the result are sent by the restarted server, once.
        Assert.Equal(new[] { 10200, 10204 }, (await Letters((long)later.Self!.Uuid, 2)).Select(l => l.TemplateId).Order());
    }
}
