using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed class GuildChallengeTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-guild-challenge-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.FromHours(2)));
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> action) => Service<GameLoop>().InvokeAsync(action);
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

    private (int Receipts, long Pending, long Held) SourceCurrency(string source, long role, int entry)
    {
        using var db = Service<AccountStore>().Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM operation_receipt WHERE source_key=$source";
        cmd.Parameters.AddWithValue("$source", source);
        int receipts = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT compartment_kind,delta FROM economy_entry WHERE source_key=$source " +
            "AND owner_kind=$ownerKind AND owner_id=$role AND asset_id=$entry";
        cmd.Parameters.AddWithValue("$ownerKind", (int)EconomyOwnerKind.Role);
        cmd.Parameters.AddWithValue("$role", role.ToString());
        cmd.Parameters.AddWithValue("$entry", entry);
        long pending = 0, held = 0;
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            var compartment = (EconomyCompartment)rows.GetInt32(0);
            if (compartment == EconomyCompartment.Pending) pending += rows.GetInt64(1);
            if (compartment == EconomyCompartment.Currency) held += rows.GetInt64(1);
        }
        return (receipts, pending, held);
    }

    private (int Receipts, List<(EconomyOwnerKind OwnerKind, string OwnerId, EconomyCompartment Compartment,
        EconomyAssetKind AssetKind, long AssetId, long Delta, EconomyLegKind LegKind, EconomyReason Reason)> Legs)
        AuctionSource(string source)
    {
        using var db = Service<AccountStore>().Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM operation_receipt WHERE source_key=$source";
        cmd.Parameters.AddWithValue("$source", source);
        int receipts = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT owner_kind,owner_id,compartment_kind,asset_kind,asset_id,delta,leg_kind,reason_code " +
            "FROM economy_entry WHERE source_key=$source";
        var legs = new List<(EconomyOwnerKind, string, EconomyCompartment,
            EconomyAssetKind, long, long, EconomyLegKind, EconomyReason)>();
        using var rows = cmd.ExecuteReader();
        while (rows.Read()) legs.Add(((EconomyOwnerKind)rows.GetInt32(0), rows.GetString(1),
            (EconomyCompartment)rows.GetInt32(2), (EconomyAssetKind)rows.GetInt32(3),
            rows.GetInt64(4), rows.GetInt64(5), (EconomyLegKind)rows.GetInt32(6),
            (EconomyReason)rows.GetInt32(7)));
        return (receipts, legs);
    }

    public async Task InitializeAsync()
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data"); o.SeasonStart = "2026-08-01";
            o.HttpPort = hp; o.MicroGatePort = mp; o.GameGatePort = gp;
        }, _clock);
        SureHitRolls.Install(_app); // lethal hits are prepared; a FLEE miss would only be noise
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{hp}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id); return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var frame = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(frame.Body);
    }
    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }
    private async Task<long> Guild(BotClient leader, params BotClient[] members)
    {
        foreach (var b in members.Prepend(leader)) await Gm(b, "!lvl 99");
        await leader.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Testers",
            Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 }
        });
        Assert.Equal(0, (await Next(leader, GuildService.Create, GuildCreateResp.Parser)).Result);
        long guild = (await Next(leader, GuildService.Full, GuildFullInfoNtf.Parser, n => n.Guid > 0)).Guid;
        await leader.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(leader, GuildService.Edit, GuildEditResp.Parser)).Result);
        foreach (var b in members)
        {
            await b.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guild } });
            Assert.Equal(0, Assert.Single((await Next(b, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
            await Next(b, GuildService.Full, GuildFullInfoNtf.Parser, n => n.IsJoin);
        }
        return guild;
    }
    private static async Task<GuildDungeonChallengeResp> Info(BotClient bot)
    {
        await bot.SendGameAsync(GuildChallengeService.Info, new GuildDungeonChallengeReq());
        return await Next(bot, GuildChallengeService.Info, GuildDungeonChallengeResp.Parser);
    }
    private static async Task<int> Open(BotClient bot, int difficulty = 1, int stage = 1)
    {
        await bot.SendGameAsync(GuildChallengeService.Open, new GuildDungeonChallengeOpenReq { Difficulty = difficulty, Stage = stage });
        var response = await Next(bot, GuildChallengeService.Open, GuildDungeonChallengeOpenResp.Parser);
        Assert.Equal((difficulty, stage), (response.Difficulty, response.Stage)); return response.Result;
    }
    private static async Task<int> Enter(BotClient bot, long scene = 3011130)
    {
        await bot.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = scene });
        return (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result;
    }
    private static async Task<long> Load(BotClient bot, long scene = 3011130)
    {
        var change = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, r => r.SceneEntry == scene);
        Assert.NotEqual(scene, change.SceneID);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, r => r.SceneId == scene)).Result);
        var snapshot = await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, r => r.DungeonId == change.SceneID);
        Assert.Equal(0, snapshot.StageModuleData.Stage); // Whale Prepare=0
        Assert.Contains(snapshot.Vars, v => v.VarId == 901);
        Assert.Contains(snapshot.Vars, v => v.VarId == 902);
        return change.SceneID;
    }
    private static async Task Command(BotClient bot, string command)
    {
        await bot.SendGameAsync(DungeonService.ClientEvent, new ClientDungeonEvent { StrCmd = command });
        await Next(bot, DungeonService.ClientEvent, NullArg.Parser);
    }
    private async Task Kill(BotClient bot)
    {
        var a = Session(bot).DungeonAttempt!;
        var actor = Assert.Single(a.Room.Monsters);
        Vec3 pos = null!;
        await OnLoop(() =>
        {
            Service<MonsterService>().Engage(actor, Session(bot));
            actor.Hp = 1; pos = actor.Pos; // test aid: actual wire attack and authoritative lethal-hit callback below
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = pos, BStop = true, SpeedRate = 1 });
        // The basic attack rolls HIT against the monster's FLEE, so one swing may miss; swing again
        // (cooldown cleared as a test aid) until the authoritative lethal hit arrives.
        for (int swing = 0; ; swing++)
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = actor.Uid });
            Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            var hit = await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == actor.Uid));
            if (hit.Dsts.Any(d => d.DstID == actor.Uid && d.IsDead)) return;
            Assert.True(swing < 20, "the basic attack never landed");
            await OnLoop(() => { Session(bot).SkillCds.Clear(); Session(bot).SkillModeCds.Clear(); return Task.CompletedTask; });
        }
    }

    private static async Task DeliveredAsync(Func<bool> condition)
    {
        for (var until = DateTime.UtcNow.AddSeconds(10); !condition();)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the recovery requested by the login did not deliver");
            await Task.Delay(20);
        }
    }

    [ClientDataFact]
    public async Task MessagesHaveLuaVisibleShapeAndEnforceMembershipScheduleDifficultyAndPermission()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        Assert.Equal(ErrorCodes.GuildMissing, (await Info(alice)).Result);
        Assert.Equal(ErrorCodes.GuildMissing, await Open(alice));
        await Guild(alice, bob);
        var data = (await Info(alice)).Data;
        Assert.NotNull(data.DataInfo);
        Assert.Equal(1, data.DataInfo.CurTimeMaxDiffiulty);
        Assert.Equal(1, data.RecommendDifficulty);
        Assert.Null(data.DungeonNtf);
        Assert.Equal(3, data.DataInfo.DifficultyInfo.Count);
        foreach (var (difficulty, row) in data.DataInfo.DifficultyInfo)
        {
            Assert.Equal(difficulty, row.Difficulty);
            Assert.Equal(difficulty, row.StageReward.Count);
            Assert.All(row.StageReward.Values, Assert.False);
        }
        Assert.False(Service<TimedTodoSchedule>().IsOpen(GuildChallengeService.Activity));
        Assert.Equal(ErrorCodes.ActivityPeriod, await Open(alice));
        await Gm(alice, "!guildchallenge open");
        await Next(alice, GuildChallengeService.Notify, GuildDungeonChallengeSync.Parser, n => n.DataInfo?.DifficultyInfo.Count == 3);
        Assert.Equal(ErrorCodes.GuildPermission, await Open(bob));
        Assert.Equal(ErrorCodes.Parameter, await Open(alice, 9));
        Assert.Equal(ErrorCodes.Parameter, await Open(alice, 1, 0));
        Assert.Equal(ErrorCodes.FeatureUnavailable, await Open(alice, 2));
        Assert.Equal(0, await Open(alice));
        data = (await Info(alice)).Data;
        Assert.Equal(3011130, data.DungeonNtf.SceneEntryID);
        Assert.Equal(1001, data.DungeonNtf.CallData.GuildChallengeData.ConfigId);
        Assert.Equal((1, 1), (data.CurDifficulty, data.CurStage));
        Assert.Equal(ErrorCodes.Parameter, await Enter(alice, 3011120));
        // 83300 is server-only. Forged incoming reward/unlock data cannot mutate the aggregate.
        await alice.SendGameAsync(GuildChallengeService.Notify, new GuildDungeonChallengeSync
        { DataInfo = new GuildChallenge { CurTimeMaxDiffiulty = 3 } });
        Assert.Equal(1, (await Info(alice)).Data.DataInfo.CurTimeMaxDiffiulty);
    }

    [ClientDataTheory]
    [InlineData(3011130, 6000041)]
    [InlineData(3011120, 6000042)]
    [InlineData(3011121, 6100024)]
    [InlineData(3011107, 6000004)]
    [InlineData(3011108, 6100000)]
    [InlineData(3011110, 6020004)]
    public void AllSixStagesUseRespawnConfigEnemyAtAuthoredPoint(long scene, int npc)
    {
        var plan = Service<DungeonCatalog>().GuildChallenge(scene, 120);
        Assert.NotNull(plan);
        Assert.Equal(120, plan.MaxPlayers);
        Assert.Equal(npc, Assert.Single(Assert.Single(plan.Waves).Spawns).NpcId);
        Assert.Null(Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = scene }));
    }

    [ClientDataFact]
    public async Task SharedRoomManagerStartAuthoritativeKillPaysMembersAndPublishesAuctionOnce()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long guild = await Guild(alice, bob, carol);
        await Gm(alice, "!guildchallenge open");
        Assert.Equal(0, await Open(alice));
        Assert.Equal(0, await Enter(alice)); long room = await Load(alice);
        Assert.Equal(0, await Enter(bob)); Assert.Equal(room, await Load(bob));
        Assert.Empty(Session(alice).DungeonAttempt!.Room.Monsters);
        await Command(bob, "DungeonStart");
        Assert.False(Session(alice).DungeonAttempt!.Started);
        long aliceZeny = Session(alice).Progress!.Currency.GetValueOrDefault(1);
        long bobZeny = Session(bob).Progress!.Currency.GetValueOrDefault(1);
        await Command(alice, "DungeonStart");
        Assert.Equal(6000041, Assert.Single(Session(alice).DungeonAttempt!.Room.Monsters).Template.NpcId);
        Assert.True((await Info(alice)).Data.LockState);
        Assert.Equal(ErrorCodes.DungeonBusy, await Enter(carol));
        await Command(alice, "DungeonWin");
        Assert.Empty(Service<GuildChallengeStore>().Clears());
        await Kill(alice);
        foreach (var b in new[] { alice, bob })
        {
            var result = await Next(b, DungeonService.Result, DungeonResNotify.Parser);
            Assert.Equal(DungeonRes.Success, result.Res);
            Assert.NotNull(result.Data.GuildChallengeData);
            Assert.Equal(5000, result.Rewards[1]);
            Assert.Equal(1500, result.Rewards[102]);
            Assert.NotEmpty(result.AuctionRewards);
            Assert.True((await Info(b)).Data.DataInfo.DifficultyInfo[1].StageReward[1]);
        }
        Assert.Equal(aliceZeny + 5000, Session(alice).Progress!.Currency[1]);
        Assert.Equal(bobZeny + 5000, Session(bob).Progress!.Currency[1]);
        Assert.False((await Info(carol)).Data.DataInfo.DifficultyInfo[1].StageReward[1]); // personal reward availability
        Assert.Empty(Session(carol).GuildChallenge!.Granted); // the absent member did not earn a personal clear
        var lots = Service<AuctionStore>().Items().Where(r => r.GuildId == guild).ToArray();
        Assert.NotEmpty(lots); Assert.All(lots, lot => { Assert.Equal(1003, lot.SrcType); Assert.Equal(2, lot.Eligible.Count); });
        var clear = Assert.Single(Service<GuildChallengeStore>().Clears());
        string auctionSource = $"guild:challenge:{guild}:{clear.Challenge}:{clear.Stage}:" +
            $"{guild}:{clear.Season}:{clear.Cycle}:{clear.Difficulty}:{clear.Stage}";
        Assert.All(lots, lot => Assert.StartsWith(auctionSource + ":", lot.SourceKey));
        var auctionLedger = AuctionSource(auctionSource);
        Assert.Equal(1, auctionLedger.Receipts);
        Assert.Equal(lots.Length, auctionLedger.Legs.Count);
        foreach (var lot in lots)
        {
            var leg = Assert.Single(auctionLedger.Legs, leg => leg.OwnerId == $"auction:{lot.Id}");
            Assert.Equal(EconomyOwnerKind.Escrow, leg.OwnerKind);
            Assert.Equal(EconomyCompartment.AuctionLot, leg.Compartment);
            Assert.Equal(EconomyAssetKind.Item, leg.AssetKind);
            Assert.Equal((long)lot.Entry, leg.AssetId);
            Assert.Equal(1L, leg.Delta);
            Assert.Equal(EconomyLegKind.Source, leg.LegKind);
            Assert.Equal(EconomyReason.ActivityReward, leg.Reason);
        }
        await alice.SendGameAsync(AuctionService.Query, new QueryAuctionReq
        { AuctionType = AuctionType.Guild, GuildId = guild, SrcType = 1003, StartIndex = 1, EndIndex = 100 });
        Assert.Equal(lots.Length, (await Next(alice, AuctionService.Query, QueryAuctionRes.Parser)).Auctions.Count);
        await OnLoop(() => Service<GuildChallengeService>().RecoverAsync());
        Assert.Equal(lots.Length, Service<AuctionStore>().Items().Count);
        var replayLedger = AuctionSource(auctionSource);
        Assert.Equal(1, replayLedger.Receipts);
        Assert.Equal(auctionLedger.Legs.Count, replayLedger.Legs.Count);
        Assert.Equal(aliceZeny + 5000, Session(alice).Progress!.Currency[1]);
        Assert.Equal(ErrorCodes.DungeonBusy, await Open(alice));
        var saved = JsonSerializer.Deserialize<GuildChallengeService.PersonalState>(Service<RoleStore>().LoadGuildChallenge((long)alice.Self!.Uuid)!)!;
        Assert.Single(saved.Granted);
        Assert.True(Service<GuildChallengeStore>().Load(guild).Complete);
        Assert.True(Service<GuildChallengeStore>().Load(guild).Cycle < 0);
        await Gm(alice, "!guildchallenge close");
        Assert.False(Service<TimedTodoSchedule>().IsOpen(125));
        _clock.Now = new DateTimeOffset(2026, 10, 11, 20, 31, 0, TimeSpan.FromHours(2));
        Assert.True(Service<TimedTodoSchedule>().IsOpen(125));
        Assert.False((await Info(alice)).Data.DataInfo.DifficultyInfo[1].StageReward[1]);
        Assert.Equal(0, await Open(alice)); // the test clear did not consume the real Sunday occurrence
        Assert.True(Service<GuildChallengeStore>().Load(guild).Cycle > 0);
    }

    [ClientDataFact]
    public async Task ClosingTestWindowSettlesFailureAndCreatesNoEntitlementOrAuction()
    {
        await using var alice = await InWorld("Alice"); await Guild(alice);
        await Gm(alice, "!guildchallenge open"); Assert.Equal(0, await Open(alice));
        Assert.Equal(0, await Enter(alice)); await Load(alice); await Command(alice, "DungeonStart");
        await Gm(alice, "!guildchallenge close");
        Assert.Equal(DungeonRes.Fail, (await Next(alice, DungeonService.Result, DungeonResNotify.Parser)).Res);
        Assert.Empty(Service<GuildChallengeStore>().Clears()); Assert.Empty(Service<AuctionStore>().Items());
        Assert.Empty(Session(alice).GuildChallenge!.Granted);
        Assert.Equal(ErrorCodes.ActivityPeriod, await Open(alice));
        Assert.Empty(Session(alice).DungeonAttempt!.Room.Monsters);
    }

    [ClientDataFact]
    public void RealScheduleUsesSundayCivilWindowAfterCustomPrefix()
    {
        var schedule = Service<TimedTodoSchedule>();
        var window = schedule.CurrentOrNext(125)!;
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 20, 30, 0, TimeSpan.FromHours(2)).ToUnixTimeSeconds(), window.Start);
        Assert.Equal(5400, window.End - window.Start);
        Assert.False(window.Contains(window.Start - 1)); Assert.True(window.Contains(window.Start));
        Assert.False(window.Contains(window.End));
    }

    [ClientDataFact]
    public async Task FailedPersonalWriteRollsBackAndCommittedClearRecoversAfterRestartExactlyOnce()
    {
        var alice = await InWorld("Alice"); long guild = await Guild(alice);
        await Gm(alice, "!guildchallenge open"); Assert.Equal(0, await Open(alice));
        Assert.Equal(0, await Enter(alice)); await Load(alice); await Command(alice, "DungeonStart");
        long zeny = Session(alice).Progress!.Currency.GetValueOrDefault(1);
        void Trigger(bool create)
        {
            string sql = create
                ? "CREATE TRIGGER fail_challenge BEFORE UPDATE OF guild_challenge ON role BEGIN SELECT RAISE(ABORT, 'challenge failure'); END"
                : "DROP TRIGGER fail_challenge";
            FixtureSql.Execute(Service<AccountStore>(), sql);
        }
        Trigger(true);
        await Kill(alice);
        var result = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal(DungeonRes.Success, result.Res);
        Assert.Empty(result.Rewards);
        Assert.Equal(zeny, Session(alice).Progress!.Currency.GetValueOrDefault(1));
        Assert.Empty(Session(alice).GuildChallenge!.Granted);
        var clear = Assert.Single(Service<GuildChallengeStore>().Clears());
        Assert.Empty(clear.Delivered); Assert.True(Service<GuildChallengeStore>().Load(guild).Complete);
        Assert.Single(Service<GuildChallengeStore>().Pending());
        long role = (long)alice.Self!.Uuid;
        Assert.Equal((1, 5000L, 0L), SourceCurrency("guild-challenge:clear:" + clear.Id, role, 1));
        Assert.Equal((1, 1500L, 0L), SourceCurrency("guild-challenge:clear:" + clear.Id, role, 102));
        Assert.Equal(0, SourceCurrency($"guild-challenge:deliver:{clear.Id}:{role}", role, 1).Receipts);
        Trigger(false);
        await alice.DisposeAsync(); await _app.StopAsync(); await _app.DisposeAsync();
        await InitializeAsync();
        await using var returned = await InWorld("Alice", create: false);
        // C/D 15: the login requests recovery as its own operation right after the select; wait for that delivery.
        // The pending row clears at the commit; the session's currency follows at the apply.
        await DeliveredAsync(() => Service<GuildChallengeStore>().Pending().Count == 0
            && Session(returned).Progress!.Currency.GetValueOrDefault(1) == zeny + 5000);
        Assert.Equal(zeny + 5000, Session(returned).Progress!.Currency[1]);
        Assert.Equal((1, -5000L, 5000L), SourceCurrency($"guild-challenge:deliver:{clear.Id}:{role}", role, 1));
        Assert.Equal((1, -1500L, 1500L), SourceCurrency($"guild-challenge:deliver:{clear.Id}:{role}", role, 102));
        Assert.Empty(Service<GuildChallengeStore>().Pending());
        Assert.Single(Session(returned).GuildChallenge!.Granted); // recovered test entitlement is durable
        Assert.False((await Info(returned)).Data.DataInfo.DifficultyInfo[1].StageReward[1]); // real occurrence is separate
        Assert.Single(Assert.Single(Service<GuildChallengeStore>().Clears()).Delivered);
        int lots = Service<AuctionStore>().Items().Count;
        Assert.True(lots > 0);
        await OnLoop(() => Service<GuildChallengeService>().RecoverAsync());
        Assert.Equal(1, SourceCurrency($"guild-challenge:deliver:{clear.Id}:{role}", role, 1).Receipts);
        Assert.Equal(lots, Service<AuctionStore>().Items().Count);
        Assert.Equal(zeny + 5000, Session(returned).Progress!.Currency[1]);
    }

    [ClientDataFact]
    public async Task HeldRealClearDefersSameRoleLoginAndDeliversPendingOnceToNewAttachment()
    {
        var alice = await InWorld("Alice");
        BotClient? returned = null;
        Task? killing = null, login = null;
        var accounts = Service<AccountStore>();
        var handover = Service<RoleHandover>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiescing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int held = 0;
        try
        {
            long guild = await Guild(alice);
            await Gm(alice, "!guildchallenge open");
            Assert.Equal(0, await Open(alice));
            Assert.Equal(0, await Enter(alice));
            await Load(alice); await Command(alice, "DungeonStart");
            var oldSession = Session(alice);
            long role = (long)alice.Self!.Uuid;
            long zeny = oldSession.Progress!.Currency.GetValueOrDefault(1);
            long contribution = oldSession.Progress.Currency.GetValueOrDefault(102);
            // SDK login does not replace the game attachment. Gate registration does, and
            // can race the held clear while the old role's durable handover waits for it.
            var reconnecting = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
            returned = reconnecting;
            await reconnecting.LoginAsync("alice", "pw");
            accounts.BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/guild-challenge-clear" || Interlocked.Exchange(ref held, 1) != 0)
                    return Task.CompletedTask;
                entered.TrySetResult();
                return release.Task;
            };
            handover.PhaseForTest = (phase, session) =>
            {
                if (phase == "Quiescing" && ReferenceEquals(session, oldSession)) quiescing.TrySetResult();
            };
            killing = Kill(alice); // real skill hit and authoritative dungeon settlement
            await entered.Task.WaitAsync(Wait);
            Assert.Empty(Service<GuildChallengeStore>().Clears());
            Assert.Equal(zeny, Service<RoleStore>().LoadProgress(role).Currency.GetValueOrDefault(1));
            login = Task.Run(async () =>
            {
                await reconnecting.EnterGateAsync();
                await reconnecting.EnterWorldAsync(role);
            });
            await quiescing.Task.WaitAsync(Wait);
            Assert.False(login.IsCompleted);
            Assert.Empty(Service<GuildChallengeStore>().Clears());
            release.TrySetResult();
            await login.WaitAsync(Wait);
            await DeliveredAsync(() => Service<GuildChallengeStore>().Pending().Count == 0); // recovery runs right after login
            var clear = Assert.Single(Service<GuildChallengeStore>().Clears());
            Assert.Equal(role, (long)returned.Self!.Uuid);
            Assert.Null(oldSession.Role);
            Assert.Single(clear.Delivered);
            Assert.Contains(role, clear.Delivered);
            Assert.Empty(Service<GuildChallengeStore>().Pending());
            Assert.Equal(zeny + 5000, Session(returned).Progress!.Currency.GetValueOrDefault(1));
            Assert.Equal(contribution + 1500, Session(returned).Progress!.Currency.GetValueOrDefault(102));
            Assert.Single(Session(returned).GuildChallenge!.Granted);
            Assert.Equal((1, 5000L, 0L), SourceCurrency("guild-challenge:clear:" + clear.Id, role, 1));
            Assert.Equal((1, 1500L, 0L), SourceCurrency("guild-challenge:clear:" + clear.Id, role, 102));
            Assert.Equal((1, -5000L, 5000L), SourceCurrency($"guild-challenge:deliver:{clear.Id}:{role}", role, 1));
            Assert.Equal((1, -1500L, 1500L), SourceCurrency($"guild-challenge:deliver:{clear.Id}:{role}", role, 102));
            var lots = Service<AuctionStore>().Items().Where(lot => lot.GuildId == guild).ToArray();
            Assert.NotEmpty(lots);
            string auctionSource = $"guild:challenge:{guild}:{clear.Challenge}:{clear.Stage}:" +
                $"{guild}:{clear.Season}:{clear.Cycle}:{clear.Difficulty}:{clear.Stage}";
            var itemSource = AuctionSource(auctionSource);
            Assert.Equal(1, itemSource.Receipts);
            Assert.Equal(lots.Length, itemSource.Legs.Count);
            Assert.All(itemSource.Legs, leg =>
            {
                Assert.Equal(EconomyAssetKind.Item, leg.AssetKind);
                Assert.Equal(EconomyCompartment.AuctionLot, leg.Compartment);
                Assert.Equal(1L, leg.Delta);
            });
            await OnLoop(() => Service<GuildChallengeService>().RecoverAsync());
            Assert.Equal(1, SourceCurrency($"guild-challenge:deliver:{clear.Id}:{role}", role, 1).Receipts);
            Assert.Equal(lots.Length, Service<AuctionStore>().Items().Count);
            Assert.Equal(zeny + 5000, Session(returned).Progress!.Currency.GetValueOrDefault(1));
        }
        finally
        {
            release.TrySetResult();
            accounts.BeforeOwnedCommitForTest = null;
            handover.PhaseForTest = null;
            if (login != null) _ = await Record.ExceptionAsync(() => login.WaitAsync(Wait));
            if (killing != null)
            {
                // The old wire may close before Kill observes its final hit packet.
                var failure = await Record.ExceptionAsync(() => killing.WaitAsync(Wait));
                Assert.True(failure is null or IOException or OperationCanceledException, failure?.ToString());
            }
            if (returned != null) await returned.DisposeAsync();
            await alice.DisposeAsync();
        }
    }

    [ClientDataFact]
    public async Task OpenWaitingAtChallengeGateCannotPersistForNewGuildAfterTransfer()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long oldGuild = await Guild(alice, bob);
        await Gm(carol, "!lvl 99");
        await carol.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Second",
            Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 }
        });
        Assert.Equal(0, (await Next(carol, GuildService.Create, GuildCreateResp.Parser)).Result);
        long newGuild = (await Next(carol, GuildService.Full, GuildFullInfoNtf.Parser, n => n.Guid > 0)).Guid;
        await carol.SendGameAsync(GuildService.Edit, new GuildEditReq
        { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(carol, GuildService.Edit, GuildEditResp.Parser)).Result);

        var challenge = Service<GuildChallengeService>();
        var gate = (SemaphoreSlim)typeof(GuildChallengeService)
            .GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(challenge)!;
        var loop = Service<GameLoop>();
        var bobSession = Session(bob);
        await gate.WaitAsync();
        Task? opening = null;
        try
        {
            await loop.InvokeAsync(() =>
            {
                opening = loop.TrackAsync("test challenge stale open", () => challenge.OpenAsync(bobSession,
                    new GuildDungeonChallengeOpenReq { Difficulty = 1, Stage = 1 }));
                return Task.CompletedTask;
            });
            Assert.False(opening!.IsCompleted);
            await alice.SendGameAsync(GuildService.Kick, new GuildMemberKickReq
            { TarPlayerIDs = { (long)bob.Self!.Uuid }, IfSingleKick = true });
            Assert.Equal(0, (await Next(alice, GuildService.Kick, GuildMemberKickResp.Parser)).Result);
            await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { newGuild } });
            Assert.Equal(0, Assert.Single((await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
            await Next(bob, GuildService.Full, GuildFullInfoNtf.Parser, n => n.Guid == newGuild);
        }
        finally { gate.Release(); }
        await opening!.WaitAsync(Wait);
        Assert.Equal(ErrorCodes.GuildMissing,
            (await Next(bob, GuildChallengeService.Open, GuildDungeonChallengeOpenResp.Parser)).Result);
        Assert.False(Service<GuildChallengeStore>().Load(newGuild).Selected);
        Assert.NotEqual(oldGuild, Service<GuildStore>().ForRole((long)bob.Self!.Uuid)!.Id);
    }

    [ClientDataFact]
    public async Task HardStageReplayPaysAbsentMemberOnceAndBothContinueToFinalAuction()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long guild = await Guild(alice, bob);
        // Synthetic historical easy clear only: stage admission and both current encounters use real configs.
        var state = Service<GuildChallengeStore>().Load(guild);
        state.Season = Service<SeasonService>().SeasonId; state.PassedDifficulty = 1;
        Service<GuildChallengeStore>().Save(guild, state);
        await Gm(alice, "!guildchallenge open");
        Assert.Equal(2, (await Info(alice)).Data.DataInfo.CurTimeMaxDiffiulty);
        Assert.Equal(ErrorCodes.Parameter, await Open(alice, 2, 2));
        Assert.Equal(0, await Open(alice, 2));
        long zeny = Session(alice).Progress!.Currency.GetValueOrDefault(1);
        long bobZeny = Session(bob).Progress!.Currency.GetValueOrDefault(1);
        Assert.Equal(0, await Enter(alice, 3011120)); await Load(alice, 3011120);
        await Command(alice, "DungeonStart"); await Kill(alice);
        var first = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal(DungeonRes.Success, first.Res); Assert.Equal(6000, first.Rewards[1]);
        Assert.Empty(first.AuctionRewards); Assert.Empty(Service<AuctionStore>().Items());
        Assert.Equal(2, (await Info(alice)).Data.CurStage);
        Assert.False((await Info(bob)).Data.DataInfo.DifficultyInfo[2].StageReward[1]);
        // The manager can restart a rewarded nonfinal stage so an absent member can earn their own reward.
        Assert.Equal(0, await Open(alice, 2));
        Assert.Equal(0, await Enter(alice, 3011120)); long replayRoom = await Load(alice, 3011120);
        Assert.Equal(0, await Enter(bob, 3011120)); Assert.Equal(replayRoom, await Load(bob, 3011120));
        await Command(alice, "DungeonStart"); await Kill(alice);
        var replay = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal(DungeonRes.Success, replay.Res); Assert.Empty(replay.Rewards);
        Assert.Equal(6000, (await Next(bob, DungeonService.Result, DungeonResNotify.Parser)).Rewards[1]);
        Assert.Equal(2, (await Info(alice)).Data.CurStage);
        Assert.True((await Info(bob)).Data.DataInfo.DifficultyInfo[2].StageReward[1]);
        Assert.Empty(Service<AuctionStore>().Items());
        // The client's Next sends 30400 directly without another 83302.
        Assert.Equal(0, await Enter(alice, 3011121)); long nextRoom = await Load(alice, 3011121);
        Assert.Equal(0, await Enter(bob, 3011121)); Assert.Equal(nextRoom, await Load(bob, 3011121));
        await Command(alice, "DungeonStart"); await Kill(alice);
        var final = await Next(alice, DungeonService.Result, DungeonResNotify.Parser);
        Assert.Equal(DungeonRes.Success, final.Res); Assert.Equal(6000, final.Rewards[1]);
        Assert.NotEmpty(final.AuctionRewards);
        Assert.Equal(DungeonRes.Success, (await Next(bob, DungeonService.Result, DungeonResNotify.Parser)).Res);
        var clears = Service<GuildChallengeStore>().Clears();
        Assert.Equal(3, clears.Count);
        Assert.Equal(clears.Where(c => c.Stage == 1).Min(c => c.StageTimeMs) + clears.Single(c => c.Stage == 2).StageTimeMs,
            final.Data.GuildChallengeData.PassDifficultTime);
        Assert.Equal(zeny + 12000, Session(alice).Progress!.Currency[1]);
        Assert.Equal(bobZeny + 12000, Session(bob).Progress!.Currency[1]);
        Assert.Empty(Service<GuildChallengeStore>().Pending());
        Assert.All((await Info(alice)).Data.DataInfo.DifficultyInfo[2].StageReward.Values, Assert.True);
        Assert.Equal(2, Service<GuildChallengeStore>().Load(guild).PassedDifficulty);
        Assert.Equal(2, (await Info(alice)).Data.DataInfo.CurTimeMaxDiffiulty); // next occurrence unlock policy
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
    }
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }

    /// <summary>103014 CheckGVEJoinCnt counts a settled Ilmata stage attempt for each member present: growth title 6's
    /// 706070 and the guild activity quest 1602090; an absent member gets nothing. The count survives a relogin.</summary>
    [ClientDataFact]
    public async Task IlmataAttemptCountsAsGveParticipationForThePresentMembers()
    {
        var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await Guild(alice, bob, carol);
        foreach (var (account, task) in new[] { ("alice", 706070L), ("bob", 1602090L), ("carol", 706070L) })
            await QuestProbe.AcceptAsync(_app, _http, account, task);
        await QuestProbe.AtAsync(_app, "alice", 706070, 0);
        await Gm(alice, "!guildchallenge open");
        Assert.Equal(0, await Open(alice));
        Assert.Equal(0, await Enter(alice)); long room = await Load(alice);
        Assert.Equal(0, await Enter(bob)); Assert.Equal(room, await Load(bob));
        await Command(alice, "DungeonStart");
        await Kill(alice);
        foreach (var b in new[] { alice, bob })
            Assert.Equal(DungeonRes.Success, (await Next(b, DungeonService.Result, DungeonResNotify.Parser)).Res);
        await QuestProbe.DoneAsync(_app, "alice", 706070);
        await QuestProbe.DoneAsync(_app, "bob", 1602090); // delivery waits on its action 115
        Assert.Equal(0, (await QuestProbe.ReadAsync(_app, "carol", 706070))!.CompleteNum[0]);

        await alice.DisposeAsync();
        await QuestProbe.OfflineAsync(_app, "alice");
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(1, (await QuestProbe.DoneAsync(_app, "alice", 706070)).CompleteNum[0]);
    }
}
