using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>Real HTTP GM, micro-mail and posted timer admission, with handshake barriers instead of sleeps.</summary>
public sealed class OwnerEntryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-owner-entry-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _url = "";
    private GameLoop Loop => _app.Services.GetRequiredService<GameLoop>();
    private OwnerRegistry Owners => _app.Services.GetRequiredService<OwnerRegistry>();
    private RoleStore Roles => _app.Services.GetRequiredService<RoleStore>();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        { o.DataDir = _directory; o.GameDataDir = SyntheticData.Dir; o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game; });
        await _app.StartAsync(); _url = $"http://127.0.0.1:{http}";
    }
    public async Task DisposeAsync()
    { await _app.StopAsync(); await _app.DisposeAsync(); Directory.Delete(_directory, recursive: true); }
    private async Task<(BotClient Bot, long Role)> Login(string name)
    {
        var bot = new BotClient(_url, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role); return (bot, role);
    }

    [Theory]
    [InlineData("GM")]
    [InlineData("micro/MailService")]
    [InlineData("timer/owner-test")]
    public async Task EntryWaitsForOwnerAndOtherRoleProgressCommits(string origin)
    {
        var (bot, role) = await Login("OwnerAlice"); await using var client = bot;
        long startingZeny = Roles.LoadProgress(role).Currency.GetValueOrDefault(1);
        long other = Roles.Create("other", 1, "OwnerBob", 1, 1000, 1)!.Id;
        string mail = await _app.Services.GetRequiredService<MailService>().SendRawAsync(role, "Owner test", "Read once");
        var entered = Signal(); var release = Signal(); var blocked = Signal(); var timerDone = Signal();
        await Loop.InvokeAsync(() => { Owners.DeferredForTest = observed => { if (observed == origin) blocked.TrySetResult(); }; return Task.CompletedTask; });
        var held = Loop.InvokeAsync(() => Owners.RunAsync("held role", [ReservationKey.Role(role)], async () =>
        { entered.SetResult(); await release.Task; }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var http = new HttpClient();
        Task contender;
        if (origin == "GM") contender = http.PostAsync(_url + "/ro3emu/gm?account=owneralice", new StringContent("!item 1 7"));
        else if (origin == "micro/MailService") contender = bot.MicroPositionalAsync("MailService", "setMailHaveRead", [role.ToString(), mail]);
        else
        {
            await Loop.InvokeAsync(() =>
            {
                var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);
                Loop.PostOwned(session, () => { Roles.SavePosition(role, session.SceneEntry, 123, 0, 456, 0); timerDone.SetResult(); return Task.CompletedTask; }, "owner-test");
                return Task.CompletedTask;
            });
            contender = timerDone.Task;
        }
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(contender.IsCompleted);
            await Loop.InvokeAsync(() => Owners.RunAsync("disjoint progress", [ReservationKey.Role(other)], () =>
            {
                var progress = Roles.LoadProgress(other); progress.Exp = 123; Roles.SaveProgress(other, progress);
                return Task.CompletedTask;
            })).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(123, Roles.LoadProgress(other).Exp);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(held, contender).WaitAsync(TimeSpan.FromSeconds(5));
        if (origin == "GM") Assert.Equal(startingZeny + 7, Roles.LoadProgress(role).Currency[1]);
        if (origin.StartsWith("timer")) Assert.Equal(123, Roles.Get(role)!.X);
        if (origin.StartsWith("micro")) Assert.Contains("\"Read\":true", System.Text.Encoding.UTF8.GetString(Roles.LoadMailbox(role)!));
    }
}
