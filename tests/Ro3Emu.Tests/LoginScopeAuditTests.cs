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
/// C/D 15 guardrail (step 4): role select runs under its production reservation (role,
/// account, lifecycle and the login keys the services declare - no Realm), the scene loaded hooks under the role's
/// keys plus the keys the hooks declare and the scene online publication under the role's own guild keys, with the
/// owner audit on, so every nested expansion and every write outside the reserved
/// keys is listed instead of thrown. Login must need nothing more: any entry is a new realm dependency (in production
/// it would throw out of the select). Runs with the repository game data: activities, Eve and guild state only exist
/// with it.
/// </summary>
public sealed class LoginScopeAuditTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-loginaudit-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = hp; o.MicroGatePort = mp; o.GameGatePort = gp;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{hp}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task ProductionSelectScopeCoversTheWholeLogin()
    {
        var audit = Audit();
        long id;
        await using (var fresh = Bot())
        {
            await fresh.LoginAsync("auditrole", "pw");
            await fresh.EnterGateAsync();
            id = (await fresh.CreateRoleAsync("AuditRole")).RoleBaseInfo.Id;
            await fresh.EnterWorldAsync(id); // the login completes: audit entries proceed under the parent
        }
        await using (var returning = Bot())
        {
            await returning.LoginAsync("auditrole", "pw");
            await returning.EnterGateAsync();
            await returning.EnterWorldAsync(id);
        }
        AssertEmpty(audit);
    }

    [Fact]
    public async Task GuildMembersLoginAndPublishInsideTheirOwnGuildKeys()
    {
        var audit = Audit();
        var (founder, founderId) = await InWorld("AuditLead");
        var (member, memberId) = await InWorld("AuditMate");
        await Gm("AuditLead", "!lvl 34");
        await founder.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Audit", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        long guild = (await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
        await Gm("AuditMate", "!lvl 34");
        await founder.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(founder, GuildService.Edit, GuildEditResp.Parser)).Result);
        await member.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guild } });
        Assert.Equal(0, Assert.Single((await Next(member, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await Next(member, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == guild);
        await member.DisposeAsync();
        // The member's relog runs the caravan, challenge and guild logins and the scene publication with an online
        // guild mate; the founder's relog then sees the member offline.
        await using (var again = Bot())
        {
            await again.LoginAsync("auditmate", "pw");
            await again.EnterGateAsync();
            await again.EnterWorldAsync(memberId);
        }
        await founder.DisposeAsync();
        await using (var again = Bot())
        {
            await again.LoginAsync("auditlead", "pw");
            await again.EnterGateAsync();
            await again.EnterWorldAsync(founderId);
        }
        AssertEmpty(audit);
    }

    private ConcurrentQueue<string> Audit()
    {
        var audit = new ConcurrentQueue<string>();
        _app.Services.GetRequiredService<OwnerRegistry>().UncoveredForTest = audit.Enqueue;
        return audit;
    }

    private void AssertEmpty(ConcurrentQueue<string> audit)
    {
        _app.Services.GetRequiredService<OwnerRegistry>().UncoveredForTest = null;
        var distinct = audit.Select(Normalize).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        output.WriteLine($"login realm dependencies ({distinct.Count} distinct, {audit.Count} total):");
        foreach (var line in distinct) output.WriteLine("  " + line);
        Assert.True(distinct.Count == 0, "Login needs keys outside its reservation: " + string.Join(" | ", distinct));
    }

    private BotClient Bot() => new(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));

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
        (await client.PostAsync($"{_http}/ro3emu/gm?account={name.ToLowerInvariant()}", new StringContent(command))).EnsureSuccessStatusCode();
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
