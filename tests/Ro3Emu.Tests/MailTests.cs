using Google.Protobuf;
using MessagePack;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Current MailService micro RPCs and game 31602. The host and helpers; each subclass picks the
/// game data its server starts on.</summary>
public abstract class MailTestsHost : IAsyncLifetime
{
    /// <summary>GameDataDir of the server.</summary>
    protected abstract string GameDataDir { get; }

    protected sealed class TestClock : TimeProvider
    {
        private long _seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void Advance(long amount) => Interlocked.Add(ref _seconds, amount);
    }

    protected readonly string dir = Path.Combine(Path.GetTempPath(), "ro3emu-mail-" + Guid.NewGuid().ToString("N"));
    protected readonly TestClock clock = new();
    protected static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    protected WebApplication app = null!;
    protected string http = "";
    protected MailService Mail => app.Services.GetRequiredService<MailService>();

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dir;
            o.GameDataDir = GameDataDir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        }, clock);
        await app.StartAsync();
        http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    protected async Task<(BotClient Bot, long Role)> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    protected static Dictionary<object, object?> Row(object? value) => (Dictionary<object, object?>)value!;
    protected static Dictionary<object, object?> Params(object? value) => Row(Row(value)["Params"]);
    protected static bool Ok(Dictionary<object, object?> response) => response["Ret"] is object?[] ret && ret[0] is true;

    protected static async Task<T> Next<T>(BotClient bot, uint msgId, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(
            e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait)).Body);

    protected static Task<Dictionary<object, object?>> List(BotClient bot, long role, int category = 1, int start = 0, int limit = 150) =>
        bot.MicroAsync("MailService", "getUserMailList", new Dictionary<string, object?>
        { ["Uid"] = role.ToString(), ["Start"] = start, ["Limit"] = limit, ["MailType"] = category });

    protected static async Task<object?[]> Letters(BotClient bot, long role, int category = 1)
    {
        var response = await List(bot, role, category);
        Assert.True(Ok(response));
        return (object?[])response["MailList"]!;
    }

    protected static async Task<ClaimMailRsp> Claim(BotClient bot, params string[] ids)
    {
        var request = new ClaimMailReq();
        request.MailUid.Add(ids);
        await bot.SendGameAsync(MailService.ClaimMail, request);
        return await Next(bot, MailService.ClaimMail, ClaimMailRsp.Parser);
    }
}

/// <summary>Mail rules on the synthetic fixture (no client data): delivery, claims, replays, ownership, expiry, capacity
/// and the rollback of a failed mailbox write.</summary>
public sealed class MailTests : MailTestsHost
{
    protected override string GameDataDir => SyntheticData.Dir;

    [Fact]
    public async Task RawMailMicroReadDeleteGmPushAndClaimSync()
    {
        var (alice, role) = await InWorld("Alice");
        await using var _ = alice;
        Assert.Empty(await Letters(alice, role));
        Assert.Contains(47, app.Services.GetRequiredService<FunctionService>().Build(
            app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role)).FunctionOpenInfos.Keys);
        Assert.Contains(4701, app.Services.GetRequiredService<FunctionService>().Build(
            app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role)).FunctionOpenInfos.Keys);

        string id = await Mail.SendRawAsync(role, "Hello", "Full text", [new(1000000, 3)]);
        Assert.True((await alice.Inbox.WaitAsync(e => e.Name == "push:MailService.newMailNotice", Wait)).Body.Length > 0);
        var row = Row(Assert.Single(await Letters(alice, role)));
        Assert.Equal(id, row["MailId"]);
        Assert.Equal("Hello", row["Title"]);
        Assert.False((bool)Params(row)["haveRead"]!);
        Assert.False((bool)Params(row)["giftReceived"]!);
        Assert.False(Params(row).ContainsKey("localization_cfg_id"));
        var items = MailItemList.Parser.ParseFrom((byte[])Params(row)["items"]!);
        Assert.Equal(3, Assert.Single(items.Items).Stack);
        Assert.Equal("Full text", (await alice.MicroPositionalAsync("MailService", "getMailContent", [role.ToString(), id]))["Content"]);
        Assert.True(Ok(await alice.MicroPositionalAsync("MailService", "setMailHaveRead", [role.ToString(), id])));
        Assert.True((bool)Params(Assert.Single(await Letters(alice, role)))["haveRead"]!);

        Assert.False(Ok(await alice.MicroAsync("MailService", "deleteMail", new() { ["Uid"] = role.ToString(), ["MailIds"] = new[] { id } })));
        var claimed = await Claim(alice, id);
        Assert.Equal(0, claimed.Result);
        Assert.Equal(id, Assert.Single(claimed.MailUid));
        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => !m.SyncAllMark && m.PackInfo.ContainsKey(1));
        Assert.Contains(bag.PackInfo[1].Inc.Items.Values, x => x.Inc?.Stack == 23);
        Assert.True((bool)Params(Assert.Single(await Letters(alice, role)))["giftReceived"]!);
        Assert.True(Ok(await alice.MicroAsync("MailService", "deleteMail", new() { ["Uid"] = role.ToString(), ["MailIds"] = new[] { id } })));
        Assert.Empty(await Letters(alice, role));

        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account=alice", new StringContent("!mail News 1000000 2"))).EnsureSuccessStatusCode();
        await alice.Inbox.WaitAsync(e => e.Name == "push:MailService.newMailNotice", Wait);
        Assert.Equal("News", Row(Assert.Single(await Letters(alice, role)))["Title"]);
    }

    [Fact]
    public async Task TemplateBulkReplayAndReconnectKeepSingleGrants()
    {
        var (alice, role) = await InWorld("Alice");
        await using (alice)
        {
            string first = await Mail.SendTemplateAsync(role, 1024, [new(2, 5)]);
            string second = await Mail.SendRawAsync(role, "Second", "Body", [new(1000000, 2)]);
            var rows = await Letters(alice, role);
            Assert.Equal(2, rows.Length);
            Assert.Equal(1024L, Convert.ToInt64(Params(rows.Single(r => Row(r)["MailId"]!.Equals(first)))["cfg_id"]));
            Assert.Equal(1L, Convert.ToInt64(Row(rows.Single(r => Row(r)["MailId"]!.Equals(first)))["MailType"]));
            Assert.Equal(0, (await Claim(alice, first, second, first)).Result);
            var replay = await Claim(alice, first, second);
            Assert.NotEqual(0, replay.Result);
            Assert.Empty(replay.MailUid);
            Assert.All(await Letters(alice, role), r => Assert.True((bool)Params(r)["giftReceived"]!));
        }
        var (again, same) = await InWorld("Alice", create: false);
        await using var _ = again;
        Assert.Equal(role, same);
        Assert.Equal(2, (await Letters(again, role)).Length);
        Assert.All(await Letters(again, role), r => Assert.True((bool)Params(r)["giftReceived"]!));
        Assert.Equal(1000, app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role)
            .Progress!.Currency[1]); // starter Zeny unaffected by template's item 2
        Assert.Equal(5, app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role)
            .Progress!.Currency[2]);
    }

    [Fact]
    public async Task OfflineDeliveryIsDurableAndInvalidatesMailboxOnLogin()
    {
        var (alice, role) = await InWorld("Alice");
        await alice.DisposeAsync();
        string id = await Mail.SendTemplateAsync(role, 1020, [new(1000000, 1)]);
        var (again, same) = await InWorld("Alice", create: false);
        await using var _ = again;
        Assert.Equal(role, same);
        await again.Inbox.WaitAsync(e => e.Name == "push:MailService.newMailNotice", Wait);
        var row = Row(Assert.Single(await Letters(again, role)));
        Assert.Equal(id, row["MailId"]);
        Assert.Equal(1020L, Convert.ToInt64(Params(row)["cfg_id"]));
    }

    [Fact]
    public async Task MicroReconnectGroupSyncReinvalidatesReadUnclaimedMailWithoutGameLogin()
    {
        var (alice, role) = await InWorld("Alice");
        await using var original = alice;
        string id = await Mail.SendRawAsync(role, "Recovery", "Body", [new(1000000, 1)]);
        await alice.Inbox.WaitAsync(e => e.Name == "push:MailService.newMailNotice", Wait);
        Assert.True(Ok(await alice.MicroPositionalAsync("MailService", "setMailHaveRead", [role.ToString(), id])));
        Assert.True((bool)Params(Assert.Single(await Letters(alice, role)))["haveRead"]!);

        await using var reconnected = new BotClient(http, File.ReadAllText(Path.Combine(dir, "gate_rsa_public.pem")));
        await reconnected.LoginAsync("alice", "pw"); // new verified micro session only; Alice stays in the game
        Assert.Equal(role, app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice").Role!.Id);
        var sync = await reconnected.MicroPositionalAsync("MailService", "syncGroupMail", [role.ToString()]);
        Assert.True(Ok(sync));
        Assert.Single(sync); // exact ordinary ACK: Ret only
        await reconnected.Inbox.WaitAsync(e => e.Name == "push:MailService.newMailNotice", Wait);
        var row = Row(Assert.Single(await Letters(reconnected, role)));
        Assert.Equal(id, row["MailId"]);
        Assert.True((bool)Params(row)["haveRead"]!);
        Assert.False((bool)Params(row)["giftReceived"]!);
    }

    [Fact]
    public async Task OwnershipExpiryAndGroupNotice()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        string id = await Mail.SendRawAsync(bobId, "Guild", "Group body", [new(1000000, 1)], category: 3);
        await bob.Inbox.WaitAsync(e => e.Name == "push:MailService.newGroupMailNotice", Wait);
        Assert.Equal(id, Row(Assert.Single(await Letters(bob, bobId, 3)))["MailId"]);
        Assert.False(Ok(await alice.MicroAsync("MailService", "getUserMailList", new() { ["Uid"] = bobId.ToString(), ["MailType"] = 3, ["Start"] = 0, ["Limit"] = 150 })));
        Assert.False(Ok(await alice.MicroPositionalAsync("MailService", "getMailContent", [bobId.ToString(), id])));
        Assert.Empty((await Claim(alice, id)).MailUid);
        Assert.True(Ok(await bob.MicroPositionalAsync("MailService", "syncGroupMail", [bobId.ToString()])));

        string shortId = await Mail.SendRawAsync(aliceId, "Short", "Soon", [new(1000000, 1)],
            expiresAt: clock.GetUtcNow().AddSeconds(1));
        Assert.Contains(await Letters(alice, aliceId), x => Row(x)["MailId"]!.Equals(shortId));
        clock.Advance(1);
        Assert.DoesNotContain(await Letters(alice, aliceId), x => Row(x)["MailId"]!.Equals(shortId));
        Assert.NotEqual(0, (await Claim(alice, shortId)).Result);
        Assert.False(Ok(await alice.MicroPositionalAsync("MailService", "setMailHaveRead", [aliceId.ToString(), shortId])));
    }

    [Fact]
    public async Task MalformedMicroInputsAndCapacityRefusalLeaveMailClaimable()
    {
        var (alice, role) = await InWorld("Alice");
        await using var _ = alice;
        await Assert.ThrowsAsync<ArgumentException>(() => Mail.SendRawAsync(role, "Invalid", "Body", [new(999999999, 1)]));
        await Assert.ThrowsAsync<ArgumentException>(() => Mail.SendTemplateAsync(role, 1020, [new(2, 0)]));
        Assert.False(Ok(await alice.MicroAsync("MailService", "getUserMailList", new()
        { ["Uid"] = role.ToString(), ["MailType"] = 1, ["Start"] = "bad", ["Limit"] = 150 })));
        Assert.Empty(await Letters(alice, role)); // bad input does not close the micro connection
        Assert.Empty((object?[])(await List(alice, role, start: 300))["MailList"]!); // unsigned 16-bit wire value

        string id = await Mail.SendRawAsync(role, "Full bag", "Body", [new(2010121, 1000)]);
        var refused = await Claim(alice, id);
        Assert.Equal(ErrorCodes.BagFull, refused.Result);
        Assert.Empty(refused.MailUid);
        Assert.False((bool)Params(Assert.Single(await Letters(alice, role)))["giftReceived"]!);
        Assert.False(Ok(await alice.MicroAsync("MailService", "deleteMail", new()
        { ["Uid"] = role.ToString(), ["MailIds"] = new object?[] { id, 123 } })));
        Assert.Single(await Letters(alice, role));

        var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        session.Progress!.Currency[2] = long.MaxValue;
        string money = await Mail.SendRawAsync(role, "Overflow", "Body", [new(2, 1)]);
        Assert.Equal(ErrorCodes.Parameter, (await Claim(alice, money)).Result);
        Assert.False((bool)Params((await Letters(alice, role)).Single(r => Row(r)["MailId"]!.Equals(money)))["giftReceived"]!);
    }

    [Fact]
    public async Task VirtualExpClaimLevelsUpOnceAndMailboxWriteRollbackRestoresProgress()
    {
        var (alice, role) = await InWorld("Alice");
        await using var _ = alice;
        var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        var roles = app.Services.GetRequiredService<RoleStore>();
        long oldExp = session.Progress!.Exp, oldJobExp = session.Progress.JobExp;
        int oldLevel = session.Progress.Level, oldJobLevel = session.Progress.JobLevel;
        string id = await Mail.SendTemplateAsync(role, 1020, [new(100, 7), new(101, 9)]);
        var itemList = MailItemList.Parser.ParseFrom((byte[])Params(Assert.Single(await Letters(alice, role)))["items"]!);
        Assert.Contains(itemList.Items, i => i.ItemEntry == 100 && i.Stack == 7);
        Assert.Contains(itemList.Items, i => i.ItemEntry == 101 && i.Stack == 9);

        FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER mail_exp_abort BEFORE UPDATE OF mailbox ON role BEGIN SELECT RAISE(ABORT, 'mail exp test'); END");
        Assert.NotEqual(0, (await Claim(alice, id)).Result);
        Assert.Equal((oldLevel, oldExp, oldJobLevel, oldJobExp),
            (session.Progress.Level, session.Progress.Exp, session.Progress.JobLevel, session.Progress.JobExp));
        Assert.Equal((oldLevel, oldExp, oldJobLevel, oldJobExp),
            (roles.LoadProgress(role).Level, roles.LoadProgress(role).Exp, roles.LoadProgress(role).JobLevel, roles.LoadProgress(role).JobExp));
        Assert.False((bool)Params(Assert.Single(await Letters(alice, role)))["giftReceived"]!);
        FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER mail_exp_abort");

        var granted = await Claim(alice, id);
        Assert.Equal(0, granted.Result);
        Assert.Equal(id, Assert.Single(granted.MailUid));
        var progression = await Next(alice, CharacterService.RoleBaseInfoSync, RoleBaseInfoIncrement.Parser, m => !m.SyncAllMark);
        Assert.True(progression.Level > oldLevel); // BaseLvConfig[1] needs 5 EXP; the mail grants 7.
        Assert.True(session.Progress.JobLevel > oldJobLevel || session.Progress.JobExp > oldJobExp);
        Assert.Equal((session.Progress.Level, session.Progress.Exp, session.Progress.JobLevel, session.Progress.JobExp),
            (roles.LoadProgress(role).Level, roles.LoadProgress(role).Exp, roles.LoadProgress(role).JobLevel, roles.LoadProgress(role).JobExp));
        Assert.NotEqual(0, (await Claim(alice, id)).Result);
        Assert.True((bool)Params(Assert.Single(await Letters(alice, role)))["giftReceived"]!);
    }

    [Fact]
    public async Task SourceOwnedTemplateDeliveryRollsBackAndReadsLegacySourceKeyOnce()
    {
        var (alice, role) = await InWorld("Alice");
        await using var _ = alice;
        var roles = app.Services.GetRequiredService<RoleStore>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Mail.SendTemplateOnceAsync(
            role, 1024, [new(2, 3)], "mail:test:once", _ => throw new InvalidOperationException("source rollback")));
        Assert.Empty(await Letters(alice, role));

        string id = await Mail.SendTemplateOnceAsync(role, 1024, [new(2, 3)], "mail:test:once", _ => { });
        var json = JsonNode.Parse(Encoding.UTF8.GetString(roles.LoadMailbox(role)!))!.AsObject();
        var saved = Assert.Single(json["Letters"]!.AsArray())!.AsObject();
        Assert.Equal("mail:test:once", saved["DeliveryKey"]!.GetValue<string>());
        saved["SourceKey"] = saved["DeliveryKey"]!.GetValue<string>();
        saved.Remove("DeliveryKey");
        roles.SaveMailbox(role, Encoding.UTF8.GetBytes(json.ToJsonString()));

        string replay = await Mail.SendTemplateOnceAsync(role, 1024, [new(2, 3)], "mail:test:once",
            _ => throw new InvalidOperationException("duplicate source callback"));
        Assert.Equal(id, replay);
        Assert.Equal(id, Row(Assert.Single(await Letters(alice, role)))["MailId"]);
        await Mail.SendRawAsync(role, "Follow-up", "Body"); // persist the loaded legacy key in canonical form
        var persisted = JsonNode.Parse(Encoding.UTF8.GetString(roles.LoadMailbox(role)!))!.AsObject();
        var migrated = persisted["Letters"]!.AsArray().Single(x => x!["Id"]!.GetValue<string>() == id)!.AsObject();
        Assert.Equal("mail:test:once", migrated["DeliveryKey"]!.GetValue<string>());
        Assert.False(migrated.ContainsKey("SourceKey"));
    }

    [Fact]
    public async Task FailedMailboxWriteRollsBackGrantAndConcurrentDeliverySurvivesClaim()
    {
        var (alice, role) = await InWorld("Alice");
        await using var _ = alice;
        string id = await Mail.SendRawAsync(role, "Rollback", "Body", [new(2, 7)]);
        FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER mail_test_abort BEFORE UPDATE OF mailbox ON role BEGIN SELECT RAISE(ABORT, 'mail test'); END");
        var failed = await Claim(alice, id);
        Assert.NotEqual(0, failed.Result);
        Assert.Empty(failed.MailUid);
        Assert.False((bool)Params(Assert.Single(await Letters(alice, role)))["giftReceived"]!);
        Assert.False(app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role)
            .Progress!.Currency.ContainsKey(2));
        FixtureSql.Execute(app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER mail_test_abort");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = Task.Run(async () => { await start.Task; return await Mail.SendRawAsync(role, "Concurrent", "Body"); });
        var claim = Task.Run(async () => { await start.Task; return await Claim(alice, id); });
        start.SetResult();
        await Task.WhenAll(send, claim);
        Assert.Equal(0, (await claim).Result);
        Assert.Equal(2, (await Letters(alice, role)).Length);
        Assert.Equal(7, app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role)
            .Progress!.Currency[2]);
    }

    [Fact]
    public async Task ActivityBatchSharesMailboxAndRollsBackOnSourceFailure()
    {
        var (alice, role) = await InWorld("Alice");
        await using var _ = alice;
        var session = app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        string rawId = await Mail.SendRawAsync(role, "Existing", "Body");
        int oldLevel = session.Progress!.Level;
        MailService.ActivityTemplateDelivery[] deliveries = [new(1020, [new(1000000, 2), new(100, 7)])];

        await Assert.ThrowsAsync<InvalidOperationException>(() => Mail.DeliverActivityBatchAsync(
            session, deliveries, _ => throw new InvalidOperationException("source rollback")));
        Assert.Equal(rawId, Row(Assert.Single(await Letters(alice, role)))["MailId"]);

        await Mail.DeliverActivityBatchAsync(session, deliveries, _ => { });
        var rows = await Letters(alice, role);
        Assert.Equal(2, rows.Length);
        var template = Row(Assert.Single(rows, r => Params(r).ContainsKey("cfg_id")));
        Assert.Equal(1020L, Convert.ToInt64(Params(template)["cfg_id"]));
        var attachments = MailItemList.Parser.ParseFrom((byte[])Params(template)["items"]!).Items;
        Assert.Contains(attachments, item => item.ItemEntry == 1000000 && item.Stack == 2);
        Assert.Contains(attachments, item => item.ItemEntry == 100 && item.Stack == 7);
        string id = Assert.IsType<string>(template["MailId"]);
        Assert.Equal(0, (await Claim(alice, id)).Result);
        Assert.True(session.Progress.Level > oldLevel);
        Assert.True((bool)Params((await Letters(alice, role)).Single(r => Row(r)["MailId"]!.Equals(id)))["giftReceived"]!);
    }
}

/// <summary>Mail on the client's own data: a real template's configured award.</summary>
public sealed class MailClientDataTests : MailTestsHost
{
    protected override string GameDataDir => Path.Combine(ClientData.RepoRoot, "data");

    [ClientDataFact]
    public async Task ConfiguredTemplateAwardResolvesGroupAndClaimsExactFixedGrant()
    {
        var (alice, role) = await InWorld("Alice");
        await using var _ = alice;
        string id = await Mail.SendTemplateAsync(role, 1024);
        var row = Row(Assert.Single(await Letters(alice, role)));
        var item = Assert.Single(MailItemList.Parser.ParseFrom((byte[])Params(row)["items"]!).Items);
        Assert.Equal(1, item.ItemEntry);
        Assert.Equal(5000, item.Stack);
        Assert.Equal(0, (await Claim(alice, id)).Result);
        Assert.Equal(6000, app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role)
            .Progress!.Currency[1]);
        await Assert.ThrowsAsync<AwardResolutionException>(() => Mail.SendTemplateAsync(role, 1025)); // conditional group
    }
}
