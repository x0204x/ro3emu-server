using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Stage G: a returning login with nothing to change writes nothing (each write was a commit per login).</summary>
public sealed class ReturningLoginWriteTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-relog-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    public async Task InitializeAsync()
    {
        var (h, m, g) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o => { o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data"); o.HttpPort = h; o.MicroGatePort = m; o.GameGatePort = g; });
        await _app.StartAsync(); _http = $"http://127.0.0.1:{h}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<(BotClient Bot, long Role)> InWorld(bool create)
    {
        var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await b.LoginAsync("relog", "pw"); await b.EnterGateAsync();
        long id = create ? (await b.CreateRoleAsync("Relogger")).RoleBaseInfo.Id : b.Roles.Single().Id;
        await b.EnterWorldAsync(id); return (b, id);
    }

    private async Task LoggedOut()
    {
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var deadline = DateTime.UtcNow + Wait;
        while (registry.All().Any(s => s.Account == "relog" && s.Role != null) && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(200); // the logout's asynchronous saves
    }

    [Fact]
    public async Task ASecondReturningLoginCommitsNoRolePart()
    {
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        long role;
        { var (bot, id) = await InWorld(create: true); role = id; await bot.DisposeAsync(); }
        await LoggedOut();
        { var (bot, _) = await InWorld(create: false); await bot.DisposeAsync(); } // first return: one-time migrations
        await LoggedOut();
        var before = accounts.RoleVector(role, sqlOnly: true);
        var (third, _) = await InWorld(create: false);
        await using (third)
        {
            var after = accounts.RoleVector(role, sqlOnly: true);
            // The position and the login touch (identity) are a login's own record; everything else must be left alone.
            var changed = after.Parts.Where(kv => before.Parts.GetValueOrDefault(kv.Key) != kv.Value)
                .Select(kv => kv.Key).Where(part => part is not ("public_position" or "identity")).Order().ToArray();
            Assert.True(changed.Length == 0, "parts written by a returning login: " + string.Join(", ", changed));
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx")) && !File.Exists(Path.Combine(d.FullName, "Ro3Emu.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
