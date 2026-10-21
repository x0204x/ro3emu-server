using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using MessagePack;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Tests;

/// <summary>Detached friend projections share durable owner revisions with game and store writers.</summary>
public sealed class SocialOwnerTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(RepoRoot(), ".loadtest-temp",
        "social-owner-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path != null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "Ro3Emu.slnx"))) return path.FullName;
        throw new InvalidOperationException("repository root");
    }

    private async Task DisposeDetachedAsync(BotClient bot, long role)
    {
        var loop = Service<GameLoop>();
        var handover = Service<RoleHandover>();
        var selected = await loop.InvokeAsync(() => Task.FromResult(Service<SessionRegistry>().All()
            .Single(session => session.Role?.Id == role)));
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handover.PhaseForTest = (phase, session) =>
        {
            if (phase == "Detached" && ReferenceEquals(session, selected)) detached.TrySetResult();
        };
        try { await bot.DisposeAsync(); await detached.Task.WaitAsync(Wait); }
        finally { handover.PhaseForTest = null; }
    }

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<(BotClient Bot, long Id)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    /// <summary>A second login of an existing role (after DisposeDetachedAsync).</summary>
    private async Task<BotClient> BackInWorld(string name, long id)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        await bot.EnterWorldAsync(id);
        return bot;
    }

    /// <summary>
    /// A role's applicants as FriendService reads them (same role reservation, same loop-owned projection). FriendService
    /// answers only the caller's own selected role, so a detached owner's projection is read here directly.
    /// </summary>
    private Task<string[]> ProjectedApplicants(long role) => Service<GameLoop>().InvokeAsync(async () =>
    {
        using var scope = await Service<OwnerRegistry>().EnterAsync("test/friend/read", [ReservationKey.Role(role)]);
        return (await Service<SocialOwnerCache>().LoadSocialAsync([role]))[role]!.Applicants.Select(id => id.ToString()).ToArray();
    });

    private static Dictionary<string, object?> Args(long self, string? key = null, long target = 0)
    {
        var args = new Dictionary<string, object?> { ["Uid"] = self.ToString() };
        if (key != null) args[key] = target.ToString();
        return args;
    }
    private static string[] Ids(Dictionary<object, object?> response, string field) =>
        ((object?[])response[field]!).Select(value => value is Dictionary<object, object?> row
            ? (string)row["uid"]! : (string)value!).ToArray();

    [Fact]
    public async Task OfflineOwnerAndExternalFriendWriteInvalidateWarmProjection()
    {
        var (alice, a) = await InWorld("OwnerAlice");
        var (bob, b) = await InWorld("OwnerBob");
        await using var _ = alice;
        await DisposeDetachedAsync(bob, b);
        Assert.Equal(RoleOwnerState.Detached,
            await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<OwnerRegistry>().Cached(b)?.State)));

        var cache = Service<SocialOwnerCache>();
        Assert.Empty(await ProjectedApplicants(b));
        long loaded = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(cache.SocialSnapshotLoads));
        Assert.Empty(await ProjectedApplicants(b));
        Assert.Equal(loaded, await Service<GameLoop>().InvokeAsync(() => Task.FromResult(cache.SocialSnapshotLoads)));

        var owners = Service<OwnerRegistry>();
        await Service<GameLoop>().InvokeAsync(() => owners.RunAsync("test/external-friend",
            [ReservationKey.Role(a), ReservationKey.Role(b)], () => Service<FriendStore>().ApplyAsync(b, a)));
        Assert.Equal([a.ToString()], await ProjectedApplicants(b));
        // Only the owner answers its own applicants: Bob comes back and accepts from his own session.
        await using var bobAgain = await BackInWorld("OwnerBob", b);
        Assert.Equal([a.ToString()], Ids(await bobAgain.MicroAsync("FriendService", "fetchApplicants", Args(b)), "Applicants"));
        var accepted = await bobAgain.MicroAsync("FriendService", "accept", Args(b, "Applicant", a));
        Assert.Equal(a.ToString(), accepted["Applicant"]);
        Assert.Equal([b.ToString()], Ids(await alice.MicroAsync("FriendService", "fetchFriendsInfo", Args(a)), "Friends"));
        Assert.Equal([a.ToString()], Ids(await bobAgain.MicroAsync("FriendService", "fetchFriendsInfo", Args(b)), "Friends"));
    }

    [Fact]
    public async Task FriendReadWaitsForCommittedApplyAndThenSeesBothOwnerProjections()
    {
        var (alice, a) = await InWorld("RaceAlice");
        var (bob, b) = await InWorld("RaceBob");
        await using var aliceLease = alice;
        await using var bobLease = bob;
        Assert.Empty(Ids(await bob.MicroAsync("FriendService", "fetchApplicants", Args(b)), "Applicants"));
        var accounts = Service<AccountStore>();
        var owners = Service<OwnerRegistry>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        accounts.BeforeOwnedApplyForTest = label =>
        {
            if (label != "micro/friend/apply") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        };
        owners.DeferredForTest = origin => { if (origin == "micro/friend/read") deferred.TrySetResult(); };
        Task<Dictionary<object, object?>>? query = null;
        try
        {
            var apply = alice.MicroAsync("FriendService", "apply", Args(a, "Target", b));
            await entered.Task.WaitAsync(Wait);
            query = bob.MicroAsync("FriendService", "fetchApplicants", Args(b));
            await deferred.Task.WaitAsync(Wait);
            Assert.False(query.IsCompleted);
            release.TrySetResult();
            await apply.WaitAsync(Wait);
            Assert.Equal([a.ToString()], Ids(await query.WaitAsync(Wait), "Applicants"));
        }
        finally
        {
            release.TrySetResult(); accounts.BeforeOwnedApplyForTest = null; owners.DeferredForTest = null;
        }
    }

    [Fact]
    public async Task WriterReceiptPreservesBlacklistOrderingAndSelfBlockOnWarmReads()
    {
        var (alice, a) = await InWorld("ListAlice");
        var (bob, b) = await InWorld("ListBob");
        var (charlie, c) = await InWorld("ListCharlie");
        await using var aliceLease = alice;
        await using var bobLease = bob;
        await using var charlieLease = charlie;
        await alice.MicroAsync("FriendService", "addBlacklist", Args(a, "Target", c));
        await alice.MicroAsync("FriendService", "addBlacklist", Args(a, "Target", b));
        await alice.MicroAsync("FriendService", "addBlacklist", Args(a, "Target", a));
        var warm = Ids(await alice.MicroAsync("FriendService", "fetchBlacklist", Args(a)), "Blacklist");
        Assert.Equal(Service<FriendStore>().Blacklist(a).Select(id => id.ToString()), warm);
        Assert.Equal([a.ToString()], Ids(await alice.MicroAsync("FriendService", "fetchBeBlacklist", Args(a)), "BeBlacklist"));
        await Service<GameLoop>().InvokeAsync(() => Service<SocialOwnerCache>().LoadSocialAsync([a], force: true));
        Assert.Equal(warm, Ids(await alice.MicroAsync("FriendService", "fetchBlacklist", Args(a)), "Blacklist"));
    }

    [Fact]
    public async Task DeletedApplicantStopsBulkAfterCommittedPrefixAndTombstoneFetchKeepsRows()
    {
        var (alice, a) = await InWorld("BulkAlice");
        var (valid, v) = await InWorld("ValidDonor");
        var (deleted, d) = await InWorld("DeletedDonor");
        await using var aliceLease = alice;
        await using var validLease = valid;
        await valid.MicroAsync("FriendService", "apply", Args(v, "Target", a));
        await valid.MicroAsync("FriendService", "apply", Args(v, "Target", d));
        await deleted.MicroAsync("FriendService", "apply", Args(d, "Target", a));
        Assert.Equal([v.ToString(), d.ToString()],
            Ids(await alice.MicroAsync("FriendService", "fetchApplicants", Args(a)), "Applicants"));

        await DisposeDetachedAsync(deleted, d);
        using (var http = new HttpClient())
        using (var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["identity"] = "deleteddonor", ["credential"] = "pw", ["role"] = d.ToString(),
        }))
        {
            var response = await http.PostAsync(_http + "/ro3emu/account/delete-role", form);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(0, body.RootElement.GetProperty("code").GetInt32());
        }
        // A deleted role can never be selected again, so its surviving rows are read from the tombstone projection.
        Assert.Equal([v.ToString()], await Service<GameLoop>().InvokeAsync(async () =>
        {
            Assert.Null((await Service<SocialOwnerCache>().LoadSocialAsync([d]))[d]);
            return (await Service<SocialOwnerCache>().TombstoneRowsAsync(d)).Applicants.Select(id => id.ToString()).ToArray();
        }));

        // Alice's own micro session (bound to her verified account), called directly so the exception is observable.
        var aliceMicro = new MicroSession(null!, 1) { Account = alice.Account };
        await Assert.ThrowsAsync<OwnerRevisionConflictException>(async () =>
        {
            await Service<FriendService>().InvokeAsync(aliceMicro, "refuseAll",
                new Dictionary<object, object?> { ["Uid"] = a.ToString() });
        });
        Assert.Equal([d.ToString()], Service<FriendStore>().Applicants(a).Select(id => id.ToString()));
        Assert.Equal([d.ToString()],
            Ids(await alice.MicroAsync("FriendService", "fetchApplicants", Args(a)), "Applicants"));
    }

    private static bool Ok(Dictionary<object, object?> response) => ((object?[])response["Ret"]!)[0] is true;
    private static string Message(Dictionary<object, object?> response) => (string)((object?[])response["Ret"]!)[1]!;

    private sealed record FriendWorldState(BotClient Alice, long A, BotClient Bob, long B, BotClient Charlie, long C,
        BotClient Mallory, long M) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Alice.DisposeAsync(); await Bob.DisposeAsync(); await Charlie.DisposeAsync(); await Mallory.DisposeAsync();
        }
    }

    /// <summary>Alice and Bob are friends, Charlie has applied to Bob and Alice has blacklisted Mallory.</summary>
    private async Task<FriendWorldState> FriendWorld(string tag)
    {
        var (alice, a) = await InWorld(tag + "Alice");
        var (bob, b) = await InWorld(tag + "Bob");
        var (charlie, c) = await InWorld(tag + "Charlie");
        var (mallory, m) = await InWorld(tag + "Mallory");
        Assert.True(Ok(await alice.MicroAsync("FriendService", "apply", Args(a, "Target", b))));
        Assert.True(Ok(await bob.MicroAsync("FriendService", "accept", Args(b, "Applicant", a))));
        Assert.True(Ok(await charlie.MicroAsync("FriendService", "apply", Args(c, "Target", b))));
        Assert.True(Ok(await alice.MicroAsync("FriendService", "addBlacklist", Args(a, "Target", m))));
        AssertFriendWorldUnchanged(a, b, c, m);
        return new(alice, a, bob, b, charlie, c, mallory, m);
    }

    /// <summary>The committed rows of <see cref="FriendWorld"/>.</summary>
    private void AssertFriendWorldUnchanged(long a, long b, long c, long m)
    {
        var store = Service<FriendStore>();
        Assert.Equal([b], store.Friends(a));
        Assert.Equal([a], store.Friends(b));
        Assert.Empty(store.Friends(m));
        Assert.Equal([c], store.Applicants(b));
        Assert.Empty(store.Applicants(a));
        Assert.Empty(store.Applicants(m));
        Assert.Equal([m], store.Blacklist(a));
        Assert.Empty(store.Blacklist(b));
        Assert.Empty(store.Blacklist(m));
    }

    /// <summary>Every friend write, aimed at Alice's or Bob's lists.</summary>
    private static (string Method, Dictionary<string, object?> Args)[] ForeignWrites(long a, long b, long c, long m) =>
    [
        ("addBlacklist", Args(b, "Target", a)), ("delBlacklist", Args(a, "Target", m)), ("remove", Args(a, "Target", b)),
        ("accept", Args(b, "Applicant", c)), ("refuse", Args(b, "Applicant", c)), ("acceptAll", Args(b)),
        ("refuseAll", Args(b)), ("refuseBatch", Args(b)), ("apply", Args(a, "Target", m)),
    ];

    [Fact]
    public async Task UnverifiedMicroSessionCannotChangeOrReadAnyFriendList()
    {
        await using var w = await FriendWorld("Anon");

        // A micro connection that never completed verifySdk: no account is bound to it.
        var anonymous = new MicroSession(null!, 2);
        var router = Service<MicroServiceRouter>();
        async Task<Dictionary<object, object?>> Call(string method, Dictionary<string, object?> args) =>
            MessagePackSerializer.Deserialize<Dictionary<object, object?>>(
                await router.InvokeAsync(anonymous, "FriendService", method, MessagePackSerializer.Serialize(args)));

        foreach (var (method, args) in ForeignWrites(w.A, w.B, w.C, w.M))
        {
            var reply = await Call(method, args);
            Assert.False(Ok(reply), method);
            Assert.Equal("not verified", Message(reply)); // the micro gate refuses it before FriendService
        }
        foreach (var (method, uid, field) in new[]
        {
            ("fetchBlacklist", w.A, "Blacklist"), ("fetchFriendsInfo", w.A, "Friends"), ("fetchApplicants", w.B, "Applicants"),
        })
        {
            var reply = await Call(method, Args(uid));
            Assert.False(Ok(reply), method);
            Assert.False(reply.ContainsKey(field), method);
        }
        AssertFriendWorldUnchanged(w.A, w.B, w.C, w.M);
        Assert.Equal([w.M.ToString()], Ids(await w.Alice.MicroAsync("FriendService", "fetchBlacklist", Args(w.A)), "Blacklist"));
        Assert.Equal([w.C.ToString()], Ids(await w.Bob.MicroAsync("FriendService", "fetchApplicants", Args(w.B)), "Applicants"));
    }

    [Fact]
    public async Task PlayerSendingAnotherRolesUidCannotChangeItsLists()
    {
        await using var w = await FriendWorld("Forge");
        foreach (var (method, args) in ForeignWrites(w.A, w.B, w.C, w.M))
        {
            var reply = await w.Mallory.MicroAsync("FriendService", method, args);
            Assert.False(Ok(reply), method);
            Assert.Equal("role mismatch", Message(reply));
        }
        AssertFriendWorldUnchanged(w.A, w.B, w.C, w.M);
        Assert.Equal([w.M.ToString()], Ids(await w.Alice.MicroAsync("FriendService", "fetchBlacklist", Args(w.A)), "Blacklist"));
        Assert.Equal([w.A.ToString()], Ids(await w.Bob.MicroAsync("FriendService", "fetchFriendsInfo", Args(w.B)), "Friends"));
        Assert.Equal([w.C.ToString()], Ids(await w.Bob.MicroAsync("FriendService", "fetchApplicants", Args(w.B)), "Applicants"));
    }

    [Fact]
    public async Task PlayerCannotReadAnotherRolesLists()
    {
        await using var w = await FriendWorld("Peek");
        foreach (var (method, uid, field) in new[]
        {
            ("fetchBlacklist", w.A, "Blacklist"), ("fetchFriendsInfo", w.A, "Friends"), ("fetchFriendsInfo", w.B, "Friends"),
            ("fetchApplicants", w.B, "Applicants"), ("fetchBeBlacklist", w.B, "BeBlacklist"),
        })
        {
            var reply = await w.Mallory.MicroAsync("FriendService", method, Args(uid));
            Assert.False(Ok(reply), method);
            Assert.Equal("role mismatch", Message(reply));
            Assert.False(reply.ContainsKey(field), method);
        }
        // Mallory's own lists stay readable: Alice's block shows up in her BeBlacklist.
        Assert.Equal([w.A.ToString()], Ids(await w.Mallory.MicroAsync("FriendService", "fetchBeBlacklist", Args(w.M)), "BeBlacklist"));
        Assert.Empty(Ids(await w.Mallory.MicroAsync("FriendService", "fetchFriendsInfo", Args(w.M)), "Friends"));
    }
}
