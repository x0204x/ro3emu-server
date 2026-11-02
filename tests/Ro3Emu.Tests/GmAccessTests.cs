using Microsoft.AspNetCore.Builder;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Who has GM (docs/guide/gm-access.md): an empty ServerOptions.GmAccounts means everyone for tests and local play, nobody
/// in game on a server other machines reach or a host started as Production on purpose; the loopback HTTP /ro3emu/gm
/// keeps "empty = everyone".
/// </summary>
public sealed class GmAccessTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public void AnExplicitProductionEnvironmentClosesAnEmptyList()
    {
        Assert.False(ServerHost.ResolveOptions(["--environment", "Production"]).GmOpenWhenEmpty);
        Assert.True(ServerHost.ResolveOptions(["--environment", "Development"]).GmOpenWhenEmpty);
        // An explicit option wins over the environment.
        Assert.True(ServerHost.ResolveOptions(["--environment", "Production", "--Ro3Emu:GmOpenWhenEmpty=true"]).GmOpenWhenEmpty);
        // No environment given (tests, tools/run-server.ps1): the host's implicit "Production" does not count.
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is null && Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is null)
            Assert.True(ServerHost.ResolveOptions([]).GmOpenWhenEmpty);
    }

    [Theory]
    // list, open-when-empty, account, loopback tool, expected
    [InlineData("", null, "anyone", false, true)]    // unset: everyone (tests, local play)
    [InlineData("", true, "anyone", false, true)]
    [InlineData("", false, "anyone", false, false)]  // Production: nobody in game
    [InlineData("", false, "anyone", true, true)]    // ...but the loopback tool is unchanged
    [InlineData("admin", false, "admin", false, true)]
    [InlineData("admin", false, "Admin", false, false)] // the stored spelling only: a case variant is not the GM
    [InlineData("admin", null, "tester", false, false)]
    [InlineData("admin", false, "tester", true, false)] // a list binds the tool as before
    public void TheAllowlistMatrix(string list, bool? openWhenEmpty, string account, bool loopbackTool, bool expected)
    {
        var options = new ServerOptions
        {
            GmAccounts = list.Length == 0 ? [] : list.Split(','),
            GmOpenWhenEmpty = openWhenEmpty,
        };
        Assert.Equal(expected, GmCommands.Allowed(options, account, loopbackTool));
    }

    /// <summary>
    /// An empty list opens GM, in game and as the panel's Admin, only on a server that only this machine reaches:
    /// a public bind, a public panel bind or a proxy in front of the panel (PublicUrl, TrustedProxies) close it, as
    /// does an explicit Production. An explicit GmOpenWhenEmpty=true still opens the game on a public server, never
    /// the panel's admin area; a list decides alone.
    /// </summary>
    [Theory]
    // environment, extra settings (";"-separated Ro3Emu:key=value), account, GM in game, panel Admin
    [InlineData("Development", "", "stranger", true, true)]                                 // local play
    [InlineData("Production", "", "stranger", false, false)]
    [InlineData("Development", "PublicHost=192.0.2.10", "stranger", false, false)]          // binds every interface
    [InlineData("Development", "PublicHost=192.0.2.10;BindAddress=127.0.0.1", "stranger", false, false)] // a tunnel
    [InlineData("Development", "PublicHost=192.0.2.10;BindAddress=127.0.0.1;Panel:BindAddress=127.0.0.1", "stranger", false, false)]
    [InlineData("Development", "PublicHost=not-an-ip;BindAddress=127.0.0.1", "stranger", false, false)]
    [InlineData("Development", "PublicHost=192.0.2.10;BindAddress=127.0.0.1;GmOpenWhenEmpty=true", "stranger", true, false)]
    [InlineData("Development", "BindAddress=0.0.0.0", "stranger", false, false)]
    [InlineData("Development", "Panel:BindAddress=0.0.0.0", "stranger", false, false)]
    [InlineData("Development", "Panel:PublicUrl=https://panel.example.com", "stranger", false, false)]
    [InlineData("Development", "Panel:TrustedProxies:0=127.0.0.1", "stranger", false, false)]
    [InlineData("Development", "BindAddress=not-an-ip", "stranger", false, false)]          // a typo never opens
    [InlineData("Development", "BindAddress=::1", "stranger", true, true)]
    [InlineData("Development", "PublicHost=192.0.2.10;GmOpenWhenEmpty=true", "stranger", true, false)]
    [InlineData("Development", "GmOpenWhenEmpty=false", "stranger", false, false)]
    [InlineData("Production", "PublicHost=192.0.2.10;GmAccounts:0=boss", "boss", true, true)]
    [InlineData("Production", "PublicHost=192.0.2.10;GmAccounts:0=boss", "stranger", false, false)]
    [InlineData("Development", "GmAccounts:0=boss", "stranger", false, false)]
    public void AnEmptyListIsOpenOnlyOnALocalServer(string environment, string settings, string account, bool inGame,
        bool panelAdmin)
    {
        string[] args = ["--environment", environment,
            .. settings.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => "--Ro3Emu:" + s)];
        var options = ServerHost.ResolveOptions(args);
        Assert.Equal(inGame, GmCommands.Allowed(options, account, loopbackTool: false));
        Assert.Equal(panelAdmin, Ro3Emu.Server.Accounts.StaffRoles.GmAdmin(options, account));
    }

    [Fact]
    public async Task InProductionAnEmptyListRefusesChatGmButTheLoopbackToolStillRuns()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-gmprod-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create(["--environment", "Production"], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await app.StartAsync();
        try
        {
            string http = $"http://127.0.0.1:{httpPort}";
            await using var alice = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
            await alice.LoginAsync("alice", "pw");
            await alice.EnterGateAsync();
            await alice.EnterWorldAsync((await alice.CreateRoleAsync("Alice")).RoleBaseInfo.Id);
            await alice.JoinChatAsync("4_1");

            await alice.SendChatAsync("4_1", 2, "!where", channelIndex: 4);
            Assert.Contains("not enabled", await Marquee(alice, "not enabled"));
            // The client's GM panel asks whether to show its button: no.
            await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { ParamString = "ro3-gm-panel-capability-v1:1" });
            Assert.EndsWith(":0", GeneralGMProcRsp.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.GeneralProcGm), Wait)).Body).ErrInfo);

            using var client = new HttpClient();
            var response = await client.PostAsync($"{http}/ro3emu/gm?account=alice", Operator.Command("!where"));
            response.EnsureSuccessStatusCode();
            Assert.Contains("Scene", await Marquee(alice, "Scene"));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    private static async Task<string> Marquee(BotClient bot, string? contains = null) =>
        BroadCastNotify.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(GmCommands.BroadcastNotify)
            && (contains == null || BroadCastNotify.Parser.ParseFrom(e.Body).Content.Contains(contains)), Wait)).Body).Content;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
