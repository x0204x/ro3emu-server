using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The guild member widget obtains Base/GuildSnap from UserDataService, not just 32214.</summary>
public sealed class GuildProfileTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-guild-profile-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _httpUrl = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http;
            o.MicroGatePort = micro;
            o.GameGatePort = game;
        });
        await _app.StartAsync();
        _httpUrl = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    [ClientDataFact]
    public async Task TwoBotsSeePersistedMembershipAndOfflineStateAndClearDepartedGuild()
    {
        await using var alice = await InWorld("Alice");
        var bob = await InWorld("Bob");
        long bobId = (long)bob.Self!.Uuid;
        var empty = await Profile(alice, bobId);
        Assert.Equal(0, empty.Base.GuildID);
        Assert.Equal(-1, empty.Base.Offlinetime);
        Assert.Equal(0, empty.Snap.Guid);

        long guildId;
        try
        {
            // This is a personal preference and must work before the player belongs to any guild.
            await bob.SendGameAsync(GuildService.Edit, new GuildEditReq
            { Tp = GuildEditReq.Types.EditType.EditCantBeInvited, CantBeInvited = true });
            Assert.Equal(0, (await Next(bob, GuildService.Edit, GuildEditResp.Parser)).Result);
            await Gm("alice", "!lvl 21");
            await Gm("bob", "!lvl 21");
            await alice.SendGameAsync(GuildService.Create, new GuildCreateReq
            { Name = "Asters", Announce = "Welcome", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
            Assert.Equal(0, (await Next(alice, GuildService.Create, GuildCreateResp.Parser)).Result);
            guildId = (await Next(alice, GuildService.Full, GuildFullInfoNtf.Parser)).Guid;
            await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guildId } });
            Assert.Equal(0, Assert.Single((await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
            await alice.SendGameAsync(GuildService.ApplyOpe, new GuildEnterApplyOpeReq { Agree = true, PlayerIds = { bobId } });
            Assert.Equal(0, Assert.Single((await Next(alice, GuildService.ApplyOpe, GuildEnterApplyOpeResp.Parser)).Data).Result);
            await Next(bob, GuildService.Full, GuildFullInfoNtf.Parser, n => n.Guid == guildId);

            var joined = await Profile(alice, bobId);
            Assert.Equal(guildId, joined.Base.GuildID);
            Assert.Equal(20, joined.Base.GuildOfficial);
            Assert.True(joined.Base.CantBeInvited);
            Assert.Equal(-1, joined.Base.Offlinetime);
            Assert.True(joined.Online);
            Assert.Equal(21, joined.Level);
            Assert.Equal(guildId, joined.Snap.Guid);
            Assert.Equal("Asters", joined.Snap.Name);
            Assert.Equal(2, joined.Snap.MemberCount);
            Assert.True(joined.Version > empty.Version);
        }
        finally { await bob.DisposeAsync(); }

        await Next(alice, GuildService.MemberChange, GuildMemberInfoChangeNtf.Parser,
            n => n.Info.Any(m => m.PlayerID == bobId && m.OfflineTime > 0));
        // Fence the disconnect, which unregisters the session after enqueuing the roster notification.
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => Task.CompletedTask);
        var offline = await Profile(alice, bobId);
        Assert.False(offline.Online);
        Assert.Equal(guildId, offline.Base.GuildID);
        Assert.InRange(offline.Base.Offlinetime, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(offline.Base.Offlinetime, offline.OfflineTime);
        var reopenedStore = new GuildStore(_app.Services.GetRequiredService<AccountStore>());
        var saved = Assert.IsType<GuildStore.State>(reopenedStore.ForRole(bobId));
        Assert.Equal(offline.OfflineTime, saved.Members[bobId].OfflineTime * 1000);

        await using var reconnected = await InWorld("Bob", create: false);
        await Next(reconnected, GuildService.Full, GuildFullInfoNtf.Parser, n => n.Guid == guildId);
        var online = await Profile(alice, bobId);
        Assert.True(online.Online);
        Assert.Equal(-1, online.Base.Offlinetime);
        Assert.True(online.Version > offline.Version);
        Assert.True(online.Base.CantBeInvited);

        await reconnected.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(0, (await Next(reconnected, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        await Next(reconnected, GuildService.Full, GuildFullInfoNtf.Parser, n => n.Guid == 0);
        var departed = await Profile(alice, bobId);
        Assert.Equal(0, departed.Base.GuildID);
        Assert.Equal(0, departed.Base.GuildOfficial);
        Assert.Equal(0, departed.Snap.Guid);
        Assert.True(departed.Base.CantBeInvited);
        Assert.Null(reopenedStore.ForRole(bobId));
        var personal = DBRoleGuildInfo.Parser.ParseFrom(_app.Services.GetRequiredService<RoleStore>().LoadGuildInfo(bobId)!);
        Assert.Equal(0, personal.GuildId);
        Assert.Equal(guildId, personal.LastGuildId);
        Assert.True(personal.CantBeInvited);
    }

    private static async Task<(RoleAdditionBaseInfo Base, GuildSnapInfo Snap, bool Online, long OfflineTime, int Level, long Version)>
        Profile(BotClient reader, long roleId)
    {
        var response = await reader.GetRoleAttributesAsync(roleId);
        var entry = (Dictionary<object, object?>)((object?[])response["Results"]!).Single()!;
        var fields = (Dictionary<object, object?>)entry["Data"]!;
        var versions = (Dictionary<object, object?>)entry["FieldsVersion"]!;
        return (RoleAdditionBaseInfo.Parser.ParseFrom((byte[])fields["Base"]!),
            GuildSnapInfo.Parser.ParseFrom((byte[])fields["GuildSnap"]!), Convert.ToBoolean(fields["Online"]),
            Convert.ToInt64(fields["OfflineTime"]), Convert.ToInt32(fields["Level"]), Convert.ToInt64(versions["Base"]));
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private async Task Gm(string account, string command)
    {
        using var client = new HttpClient();
        using var response = await client.PostAsync($"{_httpUrl}/ro3emu/gm?account={account}", new StringContent(command));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T> => parser.ParseFrom((await bot.Inbox.WaitAsync(
            e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait)).Body);

    private static string RepoRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path != null && !File.Exists(Path.Combine(path.FullName, "Ro3Emu.slnx"))) path = path.Parent;
        return path?.FullName ?? throw new DirectoryNotFoundException("Ro3Emu.slnx");
    }

}
