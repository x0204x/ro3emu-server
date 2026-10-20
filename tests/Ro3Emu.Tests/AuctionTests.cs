using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Actual 820xx wire, authoritative dungeon supply and durable auction finance. The host and helpers; each
/// subclass picks the game data its servers start on.</summary>
public abstract class AuctionTestsHost : IAsyncLifetime
{
    /// <summary>GameDataDir of every server the test starts.</summary>
    protected abstract string GameDataDir { get; }

    protected readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-auction-" + Guid.NewGuid().ToString("N"));
    protected readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.FromHours(2)));
    protected WebApplication app = null!;
    protected string http = "";
    protected (int Http, int Micro, int Game) ports;
    protected const int Multi = 50101, Boss = 4300001, Entry = 1200000;
    protected const long Scene = 3120401;
    protected static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    protected T Service<T>() where T : notnull => app.Services.GetRequiredService<T>();
    protected GameSession Session(BotClient bot) => Service<SessionRegistry>().All()
        .Single(s => s.Account == bot.Account && s.Role != null);

    public async Task InitializeAsync()
    {
        ports = TestPorts.FreePorts();
        await StartHostAsync();
    }

    protected async Task StartHostAsync()
    {
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = GameDataDir;
            o.SeasonStart = "2026-10-08";
            o.HttpPort = ports.Http;
            o.MicroGatePort = ports.Micro;
            o.GameGatePort = ports.Game;
        }, clock);
        SureHitRolls.Install(app); // lethal hits are prepared; a FLEE miss would only be noise
        await app.StartAsync();
        http = $"http://127.0.0.1:{ports.Http}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    protected async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        // C/D 15: the login requests its scene's stall maintenance instead of running it; run it now, so the
        // realm's system stall receipts land before a test's opening cut.
        await Service<GameLoop>().InvokeAsync(() => Service<StallsService>().RefreshWorldMaintenanceAsync());
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
        (await client.PostAsync($"{http}/ro3emu/gm?account={bot.Account}", new StringContent(command)))
            .EnsureSuccessStatusCode();
    }

    protected long Currency(BotClient bot) => Session(bot).Progress!.Currency.GetValueOrDefault(1);
    protected MailService.Mailbox Mailbox(BotClient bot) => Service<RoleStore>().LoadMailbox((long)bot.Self!.Uuid)
        is { } blob ? JsonSerializer.Deserialize<MailService.Mailbox>(blob)! : new MailService.Mailbox();

    protected AuctionStore.Item Seed(AuctionType type, params BotClient[] eligible)
    {
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var row = new AuctionStore.Item
        {
            SourceKey = "test:" + Guid.NewGuid().ToString("N"), Type = type,
            SrcType = type == AuctionType.Dungeon ? 5001 : 1003,
            GuildId = type == AuctionType.Guild ? 123456 : 0,
            RunId = type == AuctionType.Dungeon ? 999 : 0,
            Entry = Entry, State = AuctionState.Bidding,
            Start = now - 10, End = now + 120, Price = 500, Raise = 100,
            FixedPrice = 2000, MaxPrice = 2000, Currency = 1,
            Eligible = eligible.Select(b => (long)b.Self!.Uuid).ToList(),
        };
        SaveRows(row);
        return row;
    }

    protected void SaveRows(params AuctionStore.Item[] rows)
    {
        var plan = Service<AccountStore>().NewTransaction();
        foreach (var row in rows) Service<AuctionStore>().EnlistSave(row, plan);
        plan.Commit(Service<AccountStore>(), "test/auction-seed");
    }

    protected static async Task<BiddingAuctionRes> Bid(BotClient bot, long id, int price, BiddingType type)
    {
        await bot.SendGameAsync(AuctionService.Bid, new BiddingAuctionReq
        { AuctionId = id, Price = price, OpType = type });
        return await Next(bot, AuctionService.Bid, BiddingAuctionRes.Parser);
    }

    protected async Task<long> Party(BotClient leader, params BotClient[] others)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        foreach (var member in others)
        {
            await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
            Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
            await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
            await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
            { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self!.Uuid });
            Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        }
        return created.TeamId;
    }

    protected static async Task<long> Load(BotClient bot)
    {
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == Scene);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = Scene });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == Scene)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, m => m.DungeonId == sw.SceneID);
        return sw.SceneID;
    }

    protected async Task<MonsterUnit> BossIn(long runId)
    {
        for (int i = 0; i < 100; i++)
        {
            var actor = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
                .OfType<MonsterUnit>().SingleOrDefault(m => m.InstanceId == runId && m.Template.NpcId == Boss)));
            if (actor != null) return actor;
            await Task.Delay(20);
        }
        throw new TimeoutException("configured dungeon boss did not spawn");
    }

    protected async Task Kill(BotClient bot, MonsterUnit actor)
    {
        Vec3 pos = null!;
        await Service<GameLoop>().InvokeAsync(() => { actor.Hp = 1; pos = actor.Pos; return Task.CompletedTask; });
        await bot.SendGameAsync(MsgId.Walk, new WalkReq { Pos = pos, BStop = true, SpeedRate = 1 });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = actor.Uid });
        Assert.Equal(0, (await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            hit => hit.Dsts.Any(d => d.DstID == actor.Uid && d.IsDead));
    }

    protected sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
        public void Advance(TimeSpan by) => Now = Now.Add(by);
    }

    protected async Task RestartHostAsync(TimeSpan downtime)
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        clock.Advance(downtime);
        await StartHostAsync();
    }

    protected MailService.Mailbox MailboxOf(long role) => Service<RoleStore>().LoadMailbox(role)
        is { } blob ? System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(blob)! : new MailService.Mailbox();

    protected EconomySnapshot AuctionCut() => EconomySnapshotReader.Read(
        Service<AccountStore>().DatabasePath, EconomyCatalog.Load(GameDataDir), "auto");

    protected EconomyLedgerWindow AuctionWindow(EconomySnapshot opening, EconomySnapshot closing) =>
        EconomyLedgerReader.Read(Service<AccountStore>().DatabasePath,
            EconomyCatalog.Load(GameDataDir), "auto",
            opening.Cut.CommitSequence!.Value, closing.Cut.CommitSequence!.Value);
}

/// <summary>Auction finance on the synthetic fixture (no client data): bids, outbids, escrow, prebuys, settlement,
/// failed commits and restarts. The content of real lots (dungeon bosses, guild and world tabs) is in
/// <see cref="AuctionContentTests"/>.</summary>
public sealed partial class AuctionTests : AuctionTestsHost
{
    protected override string GameDataDir => SyntheticData.Dir;

    [Fact]
    public async Task ProxyTiesAndSameSecondOutbidsRefundEveryEscrowOnce()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        foreach (var bot in new[] { alice, bob, carol }) await Gm(bot, "!zeny 10000");
        long aliceFunds = Currency(alice), bobFunds = Currency(bob);
        var row = Seed(AuctionType.Dungeon, alice, bob, carol);
        await alice.SendGameAsync(AuctionService.Self, new QuerySelfAuctionReq { AuctionType = AuctionType.Dungeon });
        Assert.Contains((await Next(alice, AuctionService.Self, QuerySelfAuctionRes.Parser)).SelfAuctions,
            item => item.AuctionId == row.Id);
        Assert.Equal(0, (await Bid(alice, row.Id, 1000, BiddingType.Auto)).Result);
        Assert.Equal(0, (await Bid(bob, row.Id, 1000, BiddingType.Auto)).Result);
        Assert.Equal((long)alice.Self!.Uuid, Service<AuctionStore>().Get(row.Id)!.Owner); // earlier proxy wins tie
        Assert.Equal(0, (await Bid(bob, row.Id, 1200, BiddingType.Auto)).Result);
        Assert.Equal(0, (await Bid(alice, row.Id, 1500, BiddingType.Auto)).Result);
        Assert.Equal(0, (await Bid(bob, row.Id, 1800, BiddingType.Auto)).Result);
        var state = Service<AuctionStore>().Get(row.Id)!;
        Assert.Equal((long)bob.Self!.Uuid, state.Owner);
        Assert.Equal(1800, state.Reserve);
        Assert.Equal(2, Mailbox(alice).Letters.Count(m => m.DeliveryKey.StartsWith($"auction:{row.Id}:outbid:")));
        Assert.Equal(1, Mailbox(bob).Letters.Count(m => m.DeliveryKey.StartsWith($"auction:{row.Id}:outbid:")));
        Assert.Equal(bobFunds - 1200 - 1800, Currency(bob));
        Assert.Equal(aliceFunds - 1000 - 1500, Currency(alice)); // outbid refunds remain in mail until claimed
        // 82002 is the client's "You've been outbid" notice: only the role that just lost the lead receives it.
        // Bob lost the tie and was later overtaken once; Alice was overtaken twice; Carol never bid.
        int Notices(BotClient bot) => bot.Inbox.Snapshot().Count(e => e.IsGame(AuctionService.Changed));
        for (var until = DateTime.UtcNow.AddSeconds(5); (Notices(alice) < 2 || Notices(bob) < 2) && DateTime.UtcNow < until;)
            await Task.Delay(20);
        await Task.Delay(200);
        Assert.Equal(2, Notices(alice));
        Assert.Equal(2, Notices(bob));
        Assert.Equal(0, Notices(carol));
        var privateBid = await Bid(alice, Seed(AuctionType.Guild, alice).Id, 600, BiddingType.Manual);
        Assert.Equal(ErrorCodes.AuctionGuild, privateBid.Result);
        Assert.Null(privateBid.AuctionItem);
        Assert.Null(privateBid.BiddingAuctionItem);
        await alice.SendGameAsync(AuctionService.Changed, new AuctionItemChangeNotify { AuctionId = row.Id });
        Assert.Equal((long)bob.Self!.Uuid, Service<AuctionStore>().Get(row.Id)!.Owner);
    }

    [Fact]
    public async Task IncreasingOwnAutoLimitOnlyDebitsDeltaAndKeepsPublicPrice()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!zeny 10000");
        long starting = Currency(alice);
        var row = Seed(AuctionType.Dungeon, alice);
        Assert.Equal(0, (await Bid(alice, row.Id, 1000, BiddingType.Auto)).Result);
        Assert.Equal(500, Service<AuctionStore>().Get(row.Id)!.Price);
        Assert.Equal(0, (await Bid(alice, row.Id, 1500, BiddingType.Auto)).Result);
        var raised = Service<AuctionStore>().Get(row.Id)!;
        Assert.Equal(500, raised.Price);
        Assert.Equal(1500, raised.Reserve);
        Assert.Equal(starting - 1500, Currency(alice));
        raised.End = clock.GetUtcNow().ToUnixTimeSeconds() - 1;
        SaveRows(raised);
        await Service<AuctionService>().SettleAsync(row.Id);
        Assert.Equal(500, Service<AuctionStore>().Get(row.Id)!.Price);
        var award = Assert.Single(Mailbox(alice).Letters, m => m.DeliveryKey == $"auction:{row.Id}:award");
        Assert.Contains(award.Attachments, a => a.Entry == 1 && a.Count == 1000);
    }

    [Fact]
    public async Task DungeonAllPassStillRandomlyAwardsAndPaidSaleMailsEightyPercentDividend()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        foreach (var bot in new[] { alice, bob, carol }) await Gm(bot, "!zeny 10000");
        var unsold = Seed(AuctionType.Dungeon, alice, bob, carol);
        unsold.GiveUp = unsold.Eligible.ToList();
        unsold.End = clock.GetUtcNow().ToUnixTimeSeconds() - 1;
        SaveRows(unsold);
        await Service<AuctionService>().SettleAsync(unsold.Id);
        var finished = Service<AuctionStore>().Get(unsold.Id)!;
        Assert.True(finished.RandomAward);
        Assert.Contains(finished.Winner, unsold.Eligible);
        Assert.Single(new[] { alice, bob, carol }.Select(Mailbox)
            .SelectMany(box => box.Letters), m => m.DeliveryKey == $"auction:{unsold.Id}:unsold");
        var paid = Seed(AuctionType.Dungeon, alice, bob, carol);
        Assert.Equal(0, (await Bid(alice, paid.Id, 2000, BiddingType.Fixed)).Result);
        Assert.Equal(AuctionState.Settled, Service<AuctionStore>().Get(paid.Id)!.State);
        Assert.Equal(1600, new[] { alice, bob, carol }.Sum(b => Service<AuctionStore>().Bonuses((long)b.Self!.Uuid)
            .Where(x => x.Key == $"dungeon:{paid.Id}").Sum(x => x.Amount)));
        foreach (var bot in new[] { alice, bob, carol })
            Assert.Single(Mailbox(bot).Letters, m => m.DeliveryKey == $"auction:{paid.Id}:dividend:{bot.Self!.Uuid}");
        await Service<AuctionService>().SettleAsync(paid.Id);
        Assert.Single(Mailbox(alice).Letters, m => m.DeliveryKey == $"auction:{paid.Id}:award");
    }

    [Fact]
    public async Task ThreeWirePassesAccelerateDungeonUnsoldAward()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        var row = Seed(AuctionType.Dungeon, alice, bob, carol);
        foreach (var bot in new[] { alice, bob, carol })
        {
            await bot.SendGameAsync(AuctionService.Quit, new QuitAuctionReq { AuctionId = row.Id });
            Assert.Equal(0, (await Next(bot, AuctionService.Quit, QuitAuctionRes.Parser)).Result);
        }
        for (int i = 0; i < 100 && Service<AuctionStore>().Get(row.Id)!.State != AuctionState.Settled; i++)
            await Task.Delay(50);
        var settled = Service<AuctionStore>().Get(row.Id)!;
        Assert.Equal(AuctionState.Settled, settled.State);
        Assert.Contains(settled.Winner, row.Eligible);
        Assert.Single(new[] { alice, bob, carol }.Select(Mailbox).SelectMany(m => m.Letters),
            m => m.DeliveryKey == $"auction:{row.Id}:unsold");
    }

    [Fact]
    public async Task FailedReadyMarkerRetriesOnlyAfterCompletedProducerHandoff()
    {
        await using var alice = await InWorld("Alice");
        long runId = 880001, role = (long)alice.Self!.Uuid;
        var clear = Service<AccountStore>().NewTransaction();
        Service<AuctionService>().RecordDungeonClear(clear, runId, role, 0, 5001, 101,
            new Dictionary<int, long> { [1] = 73165 });
        clear.Commit(Service<AccountStore>(), "test/auction-clear");
        Assert.False(Service<AuctionStore>().GetRun(runId)!.Ready);
        FixtureSql.Execute(Service<AccountStore>(), "CREATE TRIGGER fail_auction_ready BEFORE UPDATE ON auction_run "
            + "BEGIN SELECT RAISE(ABORT, 'test ready failure'); END");
        Assert.ThrowsAny<Exception>(() => Service<AuctionService>().MarkDungeonRunReady(runId));
        Assert.False(Service<AuctionStore>().GetRun(runId)!.Ready);
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_auction_ready");
        for (int i = 0; i < 100 && !Service<AuctionStore>().GetRun(runId)!.Ready; i++)
            await Task.Delay(50);
        Assert.True(Service<AuctionStore>().GetRun(runId)!.Ready);
    }

    [Fact]
    public async Task PublicEscrowWaitsForOwnStartThenSelectsLowestPrice()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!zeny 10000");
        long startingFunds = Currency(alice);
        var older = Seed(AuctionType.Dungeon, alice);
        var future = Seed(AuctionType.Dungeon, alice);
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        future.State = AuctionState.Public;
        future.Start = now + 30;
        future.End = now + 150;
        future.Price = 600;
        SaveRows(future);
        var reserved = await Bid(alice, future.Id, 900, BiddingType.Auto);
        Assert.Equal(0, reserved.Result);
        // A PUBLIC row's wire endTime is its own start: the list cell counts "Starts at" down to it.
        Assert.Equal(future.Start, reserved.EndTime);
        Assert.Equal(future.Start, reserved.BiddingAuctionItem.EndTime);
        Assert.Equal(startingFunds - 900, Currency(alice));
        await Service<AuctionService>().ProcessPrebuysAsync();
        Assert.Equal(0, Service<AuctionStore>().Get(older.Id)!.Owner);
        Assert.Equal(0, Service<AuctionStore>().Get(future.Id)!.Owner);
        clock.Advance(TimeSpan.FromSeconds(31));
        await Service<AuctionService>().ProcessPrebuysAsync();
        Assert.Equal((long)alice.Self!.Uuid, Service<AuctionStore>().Get(older.Id)!.Owner);
        Assert.Equal(0, Service<AuctionStore>().Get(future.Id)!.Owner);
        Assert.Equal(startingFunds - 900, Currency(alice)); // one reservation, no second debit at activation
    }

    [Fact]
    public async Task PassedCheapLotCannotBeReacquiredBySecondPublicReservation()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!zeny 10000");
        var cheap = Seed(AuctionType.Dungeon, alice);
        var other = Seed(AuctionType.Dungeon, alice);
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        cheap.State = other.State = AuctionState.Public;
        cheap.Start = other.Start = now + 30;
        cheap.End = other.End = now + 150;
        other.Price = 600;
        SaveRows(cheap, other);
        Assert.Equal(0, (await Bid(alice, cheap.Id, 900, BiddingType.Auto)).Result);
        Assert.Equal(0, (await Bid(alice, other.Id, 900, BiddingType.Auto)).Result);
        await alice.SendGameAsync(AuctionService.Quit, new QuitAuctionReq { AuctionId = cheap.Id });
        Assert.Equal(0, (await Next(alice, AuctionService.Quit, QuitAuctionRes.Parser)).Result);
        clock.Advance(TimeSpan.FromSeconds(31));
        await Service<AuctionService>().ProcessPrebuysAsync();
        Assert.Equal(0, Service<AuctionStore>().Get(cheap.Id)!.Owner);
        Assert.Equal((long)alice.Self!.Uuid, Service<AuctionStore>().Get(other.Id)!.Owner);
        Assert.Single(Mailbox(alice).Letters, m => m.DeliveryKey.StartsWith("prebuy:")
            && m.DeliveryKey.EndsWith(":cancel"));
    }

    [Fact]
    public async Task QueuedOnlyRestartPausesUntilResumeWithoutSecondDebit()
    {
        long id, role, fundsAfterReserve;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm(alice, "!zeny 10000");
            role = (long)alice.Self!.Uuid;
            var row = Seed(AuctionType.Dungeon, alice);
            row.State = AuctionState.Public;
            row.Start = clock.GetUtcNow().ToUnixTimeSeconds() + 30;
            SaveRows(row);
            id = row.Id;
            Assert.Equal(0, (await Bid(alice, id, 800, BiddingType.Auto)).Result);
            fundsAfterReserve = Currency(alice);
            Assert.Equal(0, Service<AuctionStore>().Get(id)!.Owner);
        }
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await StartHostAsync();
        await using var again = await InWorld("Alice", create: false);
        Assert.Equal(role, (long)again.Self!.Uuid);
        Assert.Contains(id, (await Next(again, AuctionService.HasAuto, HasAutoBiddingAuctionNtf.Parser)).AuctionIds);
        Assert.True(Assert.Single(Service<AuctionStore>().Prebuys()).Paused);
        clock.Advance(TimeSpan.FromSeconds(31));
        await Service<AuctionService>().ProcessPrebuysAsync();
        Assert.Equal(0, Service<AuctionStore>().Get(id)!.Owner);
        await again.SendGameAsync(AuctionService.Resume, new ResumeAutoBiddingAuctionReq());
        Assert.Equal(0, (await Next(again, AuctionService.Resume, ResumeAutoBiddingAuctionRes.Parser)).Result);
        await Service<AuctionService>().ProcessPrebuysAsync();
        Assert.Equal(role, Service<AuctionStore>().Get(id)!.Owner);
        Assert.Equal(fundsAfterReserve, Currency(again));
    }

    [Fact]
    public async Task FailedSqlSettlementRestoresWalletRowAndMail()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(alice, "!zeny 10000");
        await Gm(bob, "!zeny 10000");
        var row = Seed(AuctionType.Dungeon, alice, bob);
        Assert.Equal(0, (await Bid(alice, row.Id, 800, BiddingType.Manual)).Result);
        long funds = Currency(bob);
        FixtureSql.Execute(Service<AccountStore>(), $"CREATE TRIGGER fail_auction_bid BEFORE UPDATE ON auction_item "
            + $"WHEN OLD.id={row.Id} BEGIN SELECT RAISE(ABORT, 'test failure'); END");
        Assert.Equal(ErrorCodes.Unknown, (await Bid(bob, row.Id, 1000, BiddingType.Manual)).Result);
        Assert.Equal(funds, Currency(bob));
        Assert.Equal((long)alice.Self!.Uuid, Service<AuctionStore>().Get(row.Id)!.Owner);
        Assert.DoesNotContain(Mailbox(alice).Letters, m => m.DeliveryKey.StartsWith($"auction:{row.Id}:outbid:"));
        FixtureSql.Execute(Service<AccountStore>(), "DROP TRIGGER fail_auction_bid");
        Assert.Equal(0, (await Bid(bob, row.Id, 1000, BiddingType.Manual)).Result);
        Assert.Equal(funds - 1000, Currency(bob));
        Assert.Single(Mailbox(alice).Letters, m => m.DeliveryKey.StartsWith($"auction:{row.Id}:outbid:"));
    }

    [Fact]
    public async Task IncrementalBatchPreorderCountsOnlyCommittedEscrow()
    {
        await using var alice = await InWorld("Alice");
        await Gm(alice, "!zeny 10000");
        var rows = Enumerable.Range(0, 4).Select(_ => Seed(AuctionType.Dungeon, alice)).ToArray();
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var row in rows)
        {
            row.State = AuctionState.Public;
            row.Start = now + 30;
            row.End = now + 150;
        }
        SaveRows(rows);
        async Task<BatchBiddingAuctionRes> Batch(int from, int count, int total)
        {
            var req = new BatchBiddingAuctionReq
            { AuctionType = AuctionType.Dungeon, SrcType = 5001, ItemID = Entry, PreBuyNum = total };
            foreach (var row in rows.Skip(from).Take(count))
                req.BiddingItems.Add(new BatchBiddingAuctionItem
                { AuctionId = row.Id, Price = 800, OpType = BiddingType.Auto });
            await alice.SendGameAsync(AuctionService.BatchBid, req);
            return await Next(alice, AuctionService.BatchBid, BatchBiddingAuctionRes.Parser);
        }
        Assert.Equal(0, (await Batch(0, 2, 2)).Result);
        Assert.Equal(0, (await Batch(2, 1, 3)).Result); // client sends only the new slot, total remains 3
        Assert.Equal(3, Service<AuctionStore>().Prebuys().Count);
        Assert.Equal(3, Session(alice).Auction!.PreBuyNum["4:5001:1200000"]);
        await Gm(alice, $"!zeny -{Currency(alice) - 100}");
        Assert.Equal(ErrorCodes.AuctionCurrencyShort, (await Batch(3, 1, 4)).Result);
        Assert.Equal(3, Service<AuctionStore>().Prebuys().Count);
        Assert.Equal(3, Session(alice).Auction!.PreBuyNum["4:5001:1200000"]);
    }

    [Fact]
    public async Task PartialBatchFailureStillSyncsItsOneFundedReservation()
    {
        await using var alice = await InWorld("Alice");
        var rows = new[] { Seed(AuctionType.Dungeon, alice), Seed(AuctionType.Dungeon, alice) };
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var row in rows)
        {
            row.State = AuctionState.Public;
            row.Start = now + 30;
            row.End = now + 150;
        }
        SaveRows(rows);
        long starting = Currency(alice);
        Assert.InRange(starting, 800, 1599);
        var req = new BatchBiddingAuctionReq
        { AuctionType = AuctionType.Dungeon, SrcType = 5001, ItemID = Entry, PreBuyNum = 2 };
        req.BiddingItems.Add(rows.Select(r => new BatchBiddingAuctionItem
        { AuctionId = r.Id, Price = 800, OpType = BiddingType.Auto }));
        await alice.SendGameAsync(AuctionService.BatchBid, req);
        var res = await Next(alice, AuctionService.BatchBid, BatchBiddingAuctionRes.Parser);
        Assert.Equal(0, res.Results[rows[0].Id].Result);
        Assert.Equal(ErrorCodes.AuctionCurrencyShort, res.Results[rows[1].Id].Result);
        Assert.Single(Service<AuctionStore>().Prebuys());
        Assert.Equal(1, Session(alice).Auction!.PreBuyNum["4:5001:1200000"]);
        Assert.Equal(starting - 800, Currency(alice));
    }

    [Fact]
    public async Task QueuedMaximumMustClearExistingManualPriceAndRaise()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm(alice, "!zeny 10000");
        long starting = Currency(alice);
        var manual = Seed(AuctionType.Dungeon, alice, bob);
        manual.Owner = (long)bob.Self!.Uuid;
        manual.Reserve = 500;
        var future = Seed(AuctionType.Dungeon, alice, bob);
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        future.State = AuctionState.Public;
        future.Start = now + 30;
        future.End = now + 150;
        SaveRows(manual, future);
        Assert.Equal(0, (await Bid(alice, future.Id, 500, BiddingType.Auto)).Result);
        future.Price = 700; // another listing's price rose before this reservation activates
        SaveRows(future);
        clock.Advance(TimeSpan.FromSeconds(31));
        await Service<AuctionService>().ProcessPrebuysAsync();
        Assert.Equal((long)bob.Self!.Uuid, Service<AuctionStore>().Get(manual.Id)!.Owner);
        Assert.Equal(0, Service<AuctionStore>().Get(future.Id)!.Owner);
        Assert.Equal(starting - 500, Currency(alice));
        Assert.Single(Mailbox(alice).Letters, m => m.DeliveryKey.StartsWith("prebuy:")
            && m.DeliveryKey.EndsWith(":unused"));
    }
}
