using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Xunit.Abstractions;

namespace Ro3Emu.Tests;

/// <summary>
/// C/D 15 logout guardrail ("logout"): the handover cleanup runs under its production
/// reservation (role, lifecycle, account and the role's own guild keys - no Realm) with the owner audit on, so every
/// nested expansion and every write outside it is listed instead of thrown. Logout must need nothing more: any entry
/// is a new realm dependency (in production it would fail the cleanup and leave the role for recovery).
/// </summary>
public sealed class LogoutScopeAuditTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private readonly string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-logoutaudit-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private string http = "";

    public async Task InitializeAsync()
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = hp; o.MicroGatePort = mp; o.GameGatePort = gp;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{hp}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task PlainRoleLogoutStaysInsideItsReservation()
    {
        var audit = Audit();
        var (bot, id) = await InWorld("OutPlain");
        await bot.DisposeAsync();
        await WaitDetached(id);
        AssertEmpty(audit);
    }

    [Fact]
    public async Task GuildMemberLogoutStaysInsideItsReservation()
    {
        var audit = Audit();
        var (founder, _) = await InWorld("OutLead");
        var (member, memberId) = await InWorld("OutMate");
        await Gm("OutLead", "!lvl 34");
        await founder.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Out", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        long guild = (await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
        await Gm("OutMate", "!lvl 34");
        await founder.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(founder, GuildService.Edit, GuildEditResp.Parser)).Result);
        await member.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guild } });
        Assert.Equal(0, Assert.Single((await Next(member, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await Next(member, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == guild);
        await member.DisposeAsync(); // logs out with an online guild mate
        await WaitDetached(memberId);
        await founder.DisposeAsync();
        AssertEmpty(audit);
    }

    private ConcurrentQueue<string> Audit()
    {
        var audit = new ConcurrentQueue<string>();
        app.Services.GetRequiredService<OwnerRegistry>().UncoveredForTest = audit.Enqueue;
        return audit;
    }

    private void AssertEmpty(ConcurrentQueue<string> audit)
    {
        app.Services.GetRequiredService<OwnerRegistry>().UncoveredForTest = null;
        var distinct = audit.Select(Normalize).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        output.WriteLine($"logout realm dependencies ({distinct.Count} distinct, {audit.Count} total):");
        foreach (var line in distinct) output.WriteLine("  " + line);
        Assert.True(distinct.Count == 0, "Logout needs keys outside its reservation: " + string.Join(" | ", distinct));
    }

    private async Task WaitDetached(long role)
    {
        var owners = app.Services.GetRequiredService<OwnerRegistry>();
        var loop = app.Services.GetRequiredService<GameLoop>();
        for (int i = 0; i < 250; i++)
        {
            bool detached = await loop.InvokeAsync(() => Task.FromResult(owners.Cached(role) is not { } owner
                || owner.State is RoleOwnerState.Detached or RoleOwnerState.RecoveryRequired));
            if (detached) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Role {role} did not detach.");
    }

    private BotClient Bot() => new(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));

    private async Task<(BotClient Bot, long Id)> InWorld(string name)
    {
        var bot = Bot();
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private async Task Gm(string name, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={name.ToLowerInvariant()}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    // Role ids differ per run; keep the shape of each entry.
    private static string Normalize(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line, @"\d{3,}", "#");
}
