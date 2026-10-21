using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>E03 source, transfer, expiry and replay evidence for Mail and Adventures.</summary>
public sealed class MailAdventureEconomyTests : IAsyncLifetime
{
    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void Set(long value) => Interlocked.Exchange(ref _seconds, value);
        public void Advance(long value) => Interlocked.Add(ref _seconds, value);
    }

    private readonly string _directory = Path.Combine(RepoRoot(), ".loadtest-temp", "e03-mail-adventure-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private static readonly Lazy<EconomyCatalog> Catalog = new(() => EconomyCatalog.Load(Path.Combine(RepoRoot(), "data")));
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private EconomySnapshot Cut() => EconomySnapshotReader.Read(Path.Combine(_directory, "ro3emu.db"), Catalog.Value, "auto");
    private EconomyLedgerWindow Window(EconomySnapshot a, EconomySnapshot b) =>
        EconomyLedgerReader.Read(Path.Combine(_directory, "ro3emu.db"), Catalog.Value, a.Cut.RealmId,
            a.Cut.CommitSequence!.Value, b.Cut.CommitSequence!.Value);

    private string ReceiptCommands(EconomyLedgerWindow window)
    {
        if (window.Receipts.Count == 1) return "";
        var connectionString = new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(_directory, "ro3emu.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var db = new SqliteConnection(connectionString);
        db.Open();
        var labels = new List<string>();
        foreach (var receipt in window.Receipts.Take(8))
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT command_kind, source_key FROM operation_receipt " +
                "WHERE realm_id=$realm AND operation_id=$operation";
            command.Parameters.AddWithValue("$realm", window.RealmId);
            command.Parameters.AddWithValue("$operation", receipt.OperationId);
            using var row = command.ExecuteReader();
            string label = row.Read() ? $"{row.GetString(0)}/{row.GetString(1)}" : "receipt missing";
            labels.Add($"seq={receipt.CommitSequence} legs={receipt.LegCount} " +
                (label.Length <= 160 ? label : label[..160]));
        }
        return $"Expected one receipt; found {window.Receipts.Count}: {string.Join("; ", labels)}";
    }

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.PlayBook = 101; o.SeasonStart = "2026-10-05";
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path != null && !File.Exists(Path.Combine(path.FullName, "Ro3Emu.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("repo root");
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        var role = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);
        // C/D 15: the login requests its scene's stall maintenance instead of running it; run it now, so the
        // realm's system stall receipts land before the test's opening cut as they did when the login ran it.
        await Service<GameLoop>().InvokeAsync(() => Service<StallsService>().RefreshWorldMaintenanceAsync());
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser)
        where T : IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    private static void Exact(EconomySnapshot before, EconomySnapshot after, EconomyLedgerWindow window)
    {
        Assert.True(window.CompleteHistory, string.Join("; ", window.Issues.Select(x => x.Detail)));
        var check = EconomyExactnessChecker.Check(before, after, window);
        Assert.DoesNotContain(check.Issues, x => x.Kind == EconomyIssueKind.Corruption);
        Assert.All(check.Assets, x => { Assert.Equal("0", x.Residual); Assert.Equal("0", x.LegResidual); });
        Assert.All(check.Locations, x => Assert.Equal("0", x.Residual));
    }

    [ClientDataFact]
    public async Task MailDeliveryClaimAndExpiryHaveExactReceiptsAndRefusedReplayIsSilent()
    {
        await using var alice = await InWorld("Alice");
        long role = (long)alice.Self!.Uuid;
        var mail = Service<MailService>();
        var before = Cut();
        string claimId = await mail.SendRawAsync(role, "Gift", "Body", [new(1000000, 2)]);
        var delivered = Cut();
        var delivery = Window(before, delivered);
        Assert.Single(delivery.Receipts);
        Assert.Equal((EconomyLegKind.Source, EconomyReason.GmGrant, 2L),
            (Assert.Single(delivery.Legs).Kind, delivery.Legs[0].Reason, delivery.Legs[0].Delta));
        Exact(before, delivered, delivery);

        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { claimId } });
        Assert.Equal(0, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        var claimed = Cut();
        var transfer = Window(delivered, claimed);
        Assert.Single(transfer.Receipts);
        Assert.Equal(2, transfer.Legs.Count(x => x.Kind == EconomyLegKind.Transfer));
        Assert.DoesNotContain(transfer.Legs, x => x.Kind is EconomyLegKind.Source or EconomyLegKind.Sink);
        Exact(delivered, claimed, transfer);

        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { claimId } });
        Assert.NotEqual(0, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(claimed.Cut.CommitSequence, Cut().Cut.CommitSequence);

        string expiryId = await mail.SendRawAsync(role, "Short", "Body", [new(1000000, 3)],
            expiresAt: _clock.GetUtcNow().AddSeconds(1));
        var held = Cut();
        _clock.Advance(1);
        var list = await alice.MicroAsync("MailService", "getUserMailList", new Dictionary<string, object?>
        { ["Uid"] = role.ToString(), ["Start"] = 0, ["Limit"] = 150, ["MailType"] = 1 });
        Assert.DoesNotContain((object?[])list["MailList"]!, value =>
            value is Dictionary<object, object?> row && Equals(row["MailId"], expiryId));
        var purged = Cut();
        var sink = Window(held, purged);
        Assert.Single(sink.Receipts);
        Assert.Equal((EconomyLegKind.Sink, EconomyReason.MailExpiry, -3L),
            (Assert.Single(sink.Legs).Kind, sink.Legs[0].Reason, sink.Legs[0].Delta));
        Exact(held, purged, sink);
    }

    [ClientDataFact]
    public async Task FragmentGiftConservesTwoOwnersAndConsumedAskRefusesReplay()
    {
        var segment = Service<ActivityClock>().Segments(1014, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(segment.Start + 60);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        using (var client = new HttpClient())
        {
            (await client.PostAsync(_http + "/ro3emu/gm?account=alice", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
            (await client.PostAsync(_http + "/ro3emu/gm?account=bob", new StringContent("!lvl 99"))).EnsureSuccessStatusCode();
            (await client.PostAsync(_http + "/ro3emu/gm?account=bob", new StringContent("!item 1111001 2"))).EnsureSuccessStatusCode();
        }
        // This fragment cycle starts after Eve has expired. Finish the login calendar's
        // pending settlement before opening the one-command economic observation window.
        foreach (var bot in new[] { alice, bob })
        {
            var session = Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)bot.Self!.Uuid);
            await Service<NewActivityService>().ReconcileAsync(session);
            var eve = session.NewActivities!.ActivityInfo[117];
            Assert.True(eve.CycleStartTime > 0 && eve.CycleEndTime <= _clock.GetUtcNow().ToUnixTimeSeconds());
            Assert.Equal(eve.CycleStartTime, session.ActivityRuntime.EveSettledCycleStart);
            var saved = System.Text.Json.JsonSerializer.Deserialize<ActivityRuntimeState>(
                Service<RoleStore>().LoadActivityRuntime(session.Role!.Id)!);
            Assert.Equal(eve.CycleStartTime, saved!.EveSettledCycleStart);
        }
        await alice.SendGameAsync(82963, new NewActivityFragmentGatherAskReq { ItemConfigID = 1111001 });
        var ask = await Next(alice, 82963, NewActivityFragmentGatherAskResp.Parser);
        var send = new NewActivityFragmentGatherSendReq
        { ToUserID = (long)alice.Self!.Uuid, AskID = ask.AskID, ItemID = 1111001 };
        var before = Cut();
        await bob.SendGameAsync(82964, send);
        await Next(bob, 82964, NewActivityFragmentGatherSendResp.Parser);
        var after = Cut();
        var ledger = Window(before, after);
        Assert.True(ledger.Receipts.Count == 1, ReceiptCommands(ledger));
        Assert.Single(ledger.Receipts);
        Assert.Equal(2, ledger.Legs.Count(x => x.Kind == EconomyLegKind.Transfer));
        Assert.DoesNotContain(ledger.Legs, x => x.Kind is EconomyLegKind.Source or EconomyLegKind.Sink);
        Exact(before, after, ledger);

        await bob.SendGameAsync(82964, send);
        await Next(bob, 82964, NewActivityFragmentGatherSendResp.Parser);
        Assert.Equal(after.Cut.CommitSequence, Cut().Cut.CommitSequence);
    }

    [ClientDataFact]
    public async Task SharedDeliveryKeyIsScopedPerOfflineRecipient()
    {
        long firstRole = Service<RoleStore>().Create("mail-one", 1, "MailOne", 1, 1000, 1)!.Id;
        long secondRole = Service<RoleStore>().Create("mail-two", 1, "MailTwo", 1, 1000, 1)!.Id;
        var mail = Service<MailService>();
        var before = Cut();
        string first = await mail.SendTemplateOnceAsync(firstRole, 1020, [new(1000000, 1)],
            "shared-reward", _ => { });
        string second = await mail.SendTemplateOnceAsync(secondRole, 1020, [new(1000000, 1)],
            "shared-reward", _ => { });
        var after = Cut();
        var ledger = Window(before, after);
        Assert.Equal(2, ledger.Receipts.Count);
        Assert.Equal(2, ledger.Receipts.SelectMany(x => x.SourceKeys ?? []).Distinct().Count());
        Assert.All(ledger.Legs, x => Assert.Equal(EconomyLegKind.Source, x.Kind));
        Exact(before, after, ledger);
        Assert.Equal(first, await mail.SendTemplateOnceAsync(firstRole, 1020, [new(1000000, 1)],
            "shared-reward", _ => { }));
        Assert.Equal(second, await mail.SendTemplateOnceAsync(secondRole, 1020, [new(1000000, 1)],
            "shared-reward", _ => { }));
        Assert.Equal(after.Cut.CommitSequence, Cut().Cut.CommitSequence);
    }

    [ClientDataFact]
    public async Task ClaimedDeletedDeliveryReplaysOriginalIdAndRejectsChangedReward()
    {
        await using var alice = await InWorld("Alice");
        long role = (long)alice.Self!.Uuid;
        var mail = Service<MailService>();
        int persisted = 0;
        string first = await mail.SendTemplateOnceAsync(role, 1020, [new(1000000, 1)],
            "claimed-once", _ => persisted++);
        Assert.Equal(1, persisted);
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { first } });
        Assert.Equal(0, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        var removed = await alice.MicroAsync("MailService", "deleteMail", new()
        { ["Uid"] = role.ToString(), ["MailIds"] = new[] { first } });
        Assert.True(removed["Ret"] is object?[] { Length: > 0 } ret && ret[0] is true);
        var afterDelete = Cut();

        await Assert.ThrowsAsync<EconomyOperationConflictException>(() =>
            mail.SendTemplateOnceAsync(role, 1020, [new(1000000, 2)], "claimed-once", _ => persisted++));
        Assert.Equal(first, await mail.SendTemplateOnceAsync(role, 1020, [new(1000000, 1)],
            "claimed-once", _ => persisted++));
        Assert.Equal(1, persisted);
        Assert.Equal(afterDelete.Cut.CommitSequence, Cut().Cut.CommitSequence);
    }

    [ClientDataFact]
    public async Task ExpiredDeliveryReplaysOriginalIdWithoutRepeatingSourceCallback()
    {
        long role = Service<RoleStore>().Create("expired-source", 1, "ExpiredSource", 1, 1000, 1)!.Id;
        var mail = Service<MailService>();
        int persisted = 0;
        string first = await mail.SendTemplateOnceAsync(role, 1020, [new(1000000, 1)],
            "expired-once", _ => persisted++);
        _clock.Advance(366L * 86400);
        await mail.SendRawAsync(role, "Purge", "Expired source letter");
        var afterPurge = Cut();

        Assert.Equal(first, await mail.SendTemplateOnceAsync(role, 1020, [new(1000000, 1)],
            "expired-once", _ => persisted++));
        await Assert.ThrowsAsync<EconomyOperationConflictException>(() =>
            mail.SendTemplateOnceAsync(role, 1020, [new(1000000, 2)], "expired-once", _ => persisted++));
        Assert.Equal(1, persisted);
        Assert.Equal(afterPurge.Cut.CommitSequence, Cut().Cut.CommitSequence);
    }

    [ClientDataFact]
    public async Task OfflinePassGiftSourcesEntitlementAndSinksTokenInOneReceipt()
    {
        await using var alice = await InWorld("Alice");
        long recipient = Service<RoleStore>().Create("pass-offline", 1, "PassOffline", 1, 1000, 1)!.Id;
        using (var client = new HttpClient())
        {
            (await client.PostAsync(_http + "/ro3emu/gm?account=alice", new StringContent("!lvl 99")))
                .EnsureSuccessStatusCode();
            (await client.PostAsync(_http + "/ro3emu/gm?account=alice", new StringContent("!item 1020001 2")))
                .EnsureSuccessStatusCode();
        }
        var sender = Service<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
        await Service<NewActivityService>().ReconcileAsync(sender);
        var request = new CommonActivityReq
        {
            RequestId = 9001, ActivityId = 200,
            BattlePassGift = new BattlePassGiftReq { TargetPlayerId = recipient, GiftMessage = "Enjoy the pass" },
        };
        var before = Cut();
        await alice.SendGameAsync(NewActivityService.Common, request);
        Assert.Equal(0, (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser)).Result);
        var after = Cut();
        var window = Window(before, after);
        Assert.Single(window.Receipts);
        Assert.Contains(window.Legs, leg => leg.Kind == EconomyLegKind.Source &&
            leg.Reason == EconomyReason.ActivityReward && leg.Asset.Id == 703 && leg.Delta == 1 &&
            leg.Location.Compartment == EconomyCompartment.Mail &&
            leg.Location.Owner.Id == recipient.ToString());
        Assert.Contains(window.Legs, leg => leg.Kind == EconomyLegKind.Sink &&
            leg.Reason == EconomyReason.ItemUse && leg.Asset.Id == 1020001 && leg.Delta == -1);
        Exact(before, after, window);

        await alice.SendGameAsync(NewActivityService.Common, request);
        Assert.Equal(ErrorCodes.BattlePassGiftPending,
            (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser)).Result);
        Assert.Equal(after.Cut.CommitSequence, Cut().Cut.CommitSequence);
    }

    [ClientDataFact]
    public async Task SuspendedMailClaimSerializesWithGmItemGrant()
    {
        await using var alice = await InWorld("Alice");
        long role = (long)alice.Self!.Uuid;
        string id = await Service<MailService>().SendRawAsync(role, "Gift", "Body", [new(1000000, 2)]);
        var session = Service<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        int before = session.Items.Where(i => i.Entry == 1000000).Sum(i => i.Stack);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accounts = Service<AccountStore>();
        var owners = Service<OwnerRegistry>();
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "character/exchange") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        owners.DeferredForTest = origin => { if (origin == "GM") deferred.TrySetResult(); };
        Task? gm = null;
        try
        {
            await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { id } });
            await entered.Task.WaitAsync(Wait);
            gm = Service<GameLoop>().InvokeAsync(() =>
                Service<GmCommands>().RunAsync(session, "!item 1000000 1"));
            await deferred.Task.WaitAsync(Wait);
            Assert.False(gm.IsCompleted);
            Assert.Equal(before, session.Items.Where(i => i.Entry == 1000000).Sum(i => i.Stack));
        }
        finally
        {
            release.TrySetResult(); accounts.BeforeOwnedCommitForTest = null;
            owners.DeferredForTest = null;
        }
        Assert.Equal(0, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        await gm!.WaitAsync(Wait);
        Assert.Equal(before + 3, session.Items.Where(i => i.Entry == 1000000).Sum(i => i.Stack));
    }
}
