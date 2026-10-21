using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>C/D 11 D6: the offline mailbox and micro mutations share the game claim owner/vector.</summary>
public sealed class OfflineMailOwnerTests : IAsyncLifetime
{
    private sealed class Clock : TimeProvider
    {
        private long _seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void Advance(long amount) => Interlocked.Add(ref _seconds, amount);
    }

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _directory = Path.Combine(RepoRoot(), ".loadtest-temp", "step11-mail-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private WebApplication _app = null!;
    private string _url = "";
    private GameLoop Loop => _app.Services.GetRequiredService<GameLoop>();
    private OwnerRegistry Owners => _app.Services.GetRequiredService<OwnerRegistry>();
    private AccountStore Accounts => _app.Services.GetRequiredService<AccountStore>();
    private RoleStore Roles => _app.Services.GetRequiredService<RoleStore>();
    private MailService Mail => _app.Services.GetRequiredService<MailService>();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path != null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "Ro3Emu.slnx"))) return path.FullName;
        throw new InvalidOperationException("repository root");
    }

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory; o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
        }, _clock);
        await _app.StartAsync();
        _url = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        Accounts.BeforeOwnedCommitForTest = null; Accounts.BeforeOwnedApplyForTest = null;
        await Loop.InvokeAsync(() => { Owners.DeferredForTest = null; return Task.CompletedTask; });
        await _app.StopAsync(); await _app.DisposeAsync();
        Directory.Delete(_directory, true);
    }

    private async Task<(BotClient Bot, GameSession Session)> Login(string name)
    {
        var bot = new BotClient(_url, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        var session = await Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<SessionRegistry>()
            .All().Single(s => s.Role?.Id == id)));
        return (bot, session);
    }

    private MailService.Mailbox Box(long role) => JsonSerializer.Deserialize<MailService.Mailbox>(Roles.LoadMailbox(role)!)!;
    private static bool Ok(Dictionary<object, object?> response) => response["Ret"] is object?[] ret && ret[0] is true;
    private static Task<Dictionary<object, object?>> List(BotClient bot, long role) => bot.MicroAsync("MailService", "getUserMailList",
        new() { ["Uid"] = role.ToString(), ["MailType"] = 1, ["Start"] = 0, ["Limit"] = 150 });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoOfflineDeliveriesShareOwnerAndRestageAfterCommitOrApply(bool afterCommit)
    {
        long role = Roles.Create("offline", 1, "OfflineMail", 1, 1000, 1)!.Id;
        long other = Roles.Create("other", 1, "OtherMail", 1, 1000, 1)!.Id;
        long revision = Roles.ReadRevisionVector(role).Revision("mailbox");
        var entered = Signal(); var release = Signal(); var deferred = Signal();
        bool held = false;
        Task Hold(string label)
        {
            if (label != "mail/deliver" || held) return Task.CompletedTask;
            held = true; entered.TrySetResult(); return release.Task;
        }
        if (afterCommit) Accounts.BeforeOwnedApplyForTest = Hold;
        else Accounts.BeforeOwnedCommitForTest = Hold;
        Owners.DeferredForTest = origin => { if (origin == "mail delivery") deferred.TrySetResult(); };
        var first = Mail.SendRawAsync(role, "first", "one", [new(1, 7)]);
        Task<string>? second = null;
        RoleOwner? owner = null;
        try
        {
            await entered.Task.WaitAsync(Wait);
            owner = await Loop.InvokeAsync(() => Task.FromResult(Owners.Get(role)));
            Assert.Equal(RoleOwnerState.Detached, owner.State);
            second = Mail.SendRawAsync(role, "second", "two", [new(1, 11)]);
            await deferred.Task.WaitAsync(Wait);
            Assert.False(second.IsCompleted);
            Assert.Equal(revision, owner.Revisions["mailbox"]);
            if (afterCommit) Assert.Single(Box(role).Letters);
            else Assert.Null(Roles.LoadMailbox(role));
            // A mailbox belonging to another offline owner is independent, even while Apply is held.
            string unrelated = await Mail.SendRawAsync(other, "other", "three").WaitAsync(Wait);
            Assert.Equal(unrelated, Assert.Single(Box(other).Letters).Id);
        }
        finally { release.TrySetResult(); }
        string firstId = await first.WaitAsync(Wait), secondId = await second!.WaitAsync(Wait);
        var letters = Box(role).Letters;
        Assert.Equal(2, letters.Count); Assert.NotEqual(firstId, secondId);
        Assert.Equal(7, Assert.Single(letters.Single(m => m.Id == firstId).Attachments).Count);
        Assert.Equal(11, Assert.Single(letters.Single(m => m.Id == secondId).Attachments).Count);
        Assert.Equal(revision + 2, Roles.ReadRevisionVector(role).Revision("mailbox"));
        await Loop.InvokeAsync(() =>
        {
            Assert.Same(owner, Owners.Get(role));
            Assert.Equal(revision + 2, owner!.Revisions["mailbox"]);
            Assert.Empty(owner.Operations); return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ClaimAndMicroReadOrPurgeSerializeWithoutRestoringAttachments(bool microFirst, bool purge)
    {
        var (bot, session) = await Login("MailboxRace"); await using var client = bot;
        long role = session.Role!.Id;
        string id = await Mail.SendRawAsync(role, "race", "body", [new(1, 7)],
            expiresAt: _clock.GetUtcNow().AddMinutes(1));
        long before = session.Progress!.Currency.GetValueOrDefault(1);
        var entered = Signal(); var release = Signal(); var deferred = Signal();
        string heldLabel = microFirst ? purge ? "mail/micro-purge" : "mail/micro-read" : "character/exchange";
        GameLoop.WorkPermit? claimPermit = null;
        bool held = false;
        Accounts.BeforeOwnedApplyForTest = label =>
        {
            if (label != heldLabel || held || !microFirst && !ReferenceEquals(Loop.CurrentWork, claimPermit))
                return Task.CompletedTask;
            held = true; entered.TrySetResult(); return release.Task;
        };
        Owners.DeferredForTest = origin =>
        {
            if (origin == (microFirst ? "Character.StateLock" : "micro/MailService")) deferred.TrySetResult();
        };
        Task StartClaim() => Loop.InvokeAsync(async () =>
        {
            claimPermit = Loop.CurrentWork;
            await Mail.ClaimAsync(session, new ClaimMailReq { MailUid = { id } });
        });
        Task<Dictionary<object, object?>> StartMicro() => purge ? List(bot, role)
            : bot.MicroPositionalAsync("MailService", "setMailHaveRead", [role.ToString(), id]);
        Task? claim = null;
        Task<Dictionary<object, object?>>? micro = null;
        try
        {
            if (microFirst)
            {
                if (purge) _clock.Advance(60);
                micro = StartMicro();
            }
            else claim = StartClaim();
            await entered.Task.WaitAsync(Wait);
            if (microFirst) claim = StartClaim();
            else
            {
                if (purge) _clock.Advance(60);
                micro = StartMicro();
            }
            await deferred.Task.WaitAsync(Wait);
            Assert.False(claim!.IsCompleted); Assert.False(micro!.IsCompleted);
            Assert.Equal(before, session.Progress!.Currency.GetValueOrDefault(1));
        }
        finally { release.TrySetResult(); }
        await claim!.WaitAsync(Wait);
        Assert.True(Ok(await micro!.WaitAsync(Wait)));
        var reply = ClaimMailRsp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(MailService.ClaimMail), Wait)).Body);
        bool granted = !(microFirst && purge);
        Assert.Equal(granted ? 0 : ErrorCodes.MailMissing, reply.Result);
        Assert.Equal(before + (granted ? 7 : 0), Roles.LoadProgress(role).Currency.GetValueOrDefault(1));
        Assert.Equal(before + (granted ? 7 : 0), session.Progress.Currency.GetValueOrDefault(1));
        if (purge) Assert.Empty(Box(role).Letters);
        else
        {
            var saved = Assert.Single(Box(role).Letters);
            Assert.True(saved.Read); Assert.True(saved.Claimed);
        }
        await bot.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { id } });
        var retry = ClaimMailRsp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(MailService.ClaimMail), Wait)).Body);
        Assert.Equal(purge ? ErrorCodes.MailMissing : ErrorCodes.MailAlreadyClaimed, retry.Result);
        Assert.Equal(before + (granted ? 7 : 0), Roles.LoadProgress(role).Currency.GetValueOrDefault(1));
    }
}
