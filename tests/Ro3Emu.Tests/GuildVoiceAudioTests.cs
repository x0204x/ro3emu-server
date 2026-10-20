using Microsoft.AspNetCore.Builder;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Voice;

namespace Ro3Emu.Tests;

/// <summary>
/// Guild voice channels on the relay: the room "2_&lt;guildId&gt;_&lt;index&gt;" admits only who sits in that panel
/// channel (GuildVoiceService), Order mode holds the mic off for whoever has not been given it, and a kick from the
/// panel channel drops the voice connection on the next sweep.
/// </summary>
public sealed class GuildVoiceAudioTests : IAsyncLifetime
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ro3emu-guildaudio-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication app = null!;
    private string http = "";
    private int voicePort;

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        voicePort = TestPorts.FreePort();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = dir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.Voice.Port = voicePort;
        });
        await app.StartAsync();
        http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync();
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "ro3emu.db")}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(db);
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private async Task<(BotClient Bot, long Id)> InWorld(string name)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private static async Task<T> Ask<T>(BotClient bot, uint id, Google.Protobuf.IMessage request, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T>
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(id));
        await bot.SendGameAsync(id, request);
        return parser.ParseFrom((await reply.WaitAsync(Wait)).Body);
    }

    private async Task Gm(string name, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={name.ToLowerInvariant()}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private async Task<(BotClient Alice, long AliceId, BotClient Bob, long BobId, long Guild)> GuildOfTwo()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await Gm("Alice", "!lvl 34");
        using (var full = alice.Inbox.Expect(e => e.IsGame(GuildService.Full) && GuildFullInfoNtf.Parser.ParseFrom(e.Body).Guid > 0))
        {
            Assert.Equal(0, (await Ask(alice, GuildService.Create, new GuildCreateReq
            {
                Name = "Voices", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
            }, GuildCreateResp.Parser)).Result);
            long guild = GuildFullInfoNtf.Parser.ParseFrom((await full.WaitAsync(Wait)).Body).Guid;
            Assert.Equal(0, (await Ask(alice, GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true }, GuildEditResp.Parser)).Result);
            using var log = new CancellationTokenSource(Wait);
            await BotGuildVoice.JoinGuildAsync(bob, http, "bob", guild, _ => { }, log.Token);
            return (alice, aliceId, bob, bobId, guild);
        }
    }

    private static Task<GuildVoiceChannelJoinRsp> Seat(BotClient bot, int channel) =>
        Ask(bot, GuildVoiceService.JoinMsg, new GuildVoiceChannelJoinReq { ChannelIndex = channel }, GuildVoiceChannelJoinRsp.Parser);

    /// <summary>A ticket from the game gate, then Auth on the relay.</summary>
    private async Task<VoiceTestClient> Relay(BotClient bot)
    {
        var ticket = await BotVoiceTicket.RequestAsync(bot, Wait);
        Assert.Equal(voicePort, ticket.Port);
        var client = new VoiceTestClient(voicePort) { Tag = ticket.RoleId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        await client.ConnectAsync();
        await client.AuthAsync(ticket.RoleId, ticket.Token, ticket.Secret);
        await client.WaitTypeAsync(VoiceDown.AuthOk, Wait);
        return client;
    }

    private static async Task<byte> JoinRoom(VoiceTestClient client, string room)
    {
        await client.JoinAsync(room);
        return (await client.WaitTypeAsync(VoiceDown.JoinResult, Wait)).Payload[0];
    }

    private static bool MicIs(byte type, byte[] p, long role, bool on) =>
        type == VoiceDown.MicState && BitConverter.ToInt64(p) == role && p[8] == (on ? 1 : 0);

    [ClientDataFact]
    public async Task MembersOfAPanelChannelTalkInItsRoom()
    {
        var (alice, aliceId, bob, bobId, guild) = await GuildOfTwo();
        await using var a = alice;
        await using var b = bob;
        await Seat(alice, 0);
        await Seat(bob, 0);
        await using var aliceVoice = await Relay(alice);
        await using var bobVoice = await Relay(bob);
        string room = $"2_{guild}_0";

        Assert.Equal(VoiceJoinCode.Ok, await JoinRoom(aliceVoice, room));
        Assert.Equal(VoiceJoinCode.Ok, await JoinRoom(bobVoice, room));
        await bobVoice.WaitAsync((t, p) => t == VoiceDown.MemberChange && BitConverter.ToInt64(p) == aliceId && p[8] == 1, Wait);

        await aliceVoice.MicAsync(true);
        await bobVoice.WaitAsync((t, p) => MicIs(t, p, aliceId, true), Wait);
        await aliceVoice.SendAudioAsync(0, [1, 2, 3]);
        var (_, audio) = await bobVoice.WaitTypeAsync(VoiceDown.Audio, Wait);
        Assert.Equal(aliceId, BitConverter.ToInt64(audio));
        Assert.Equal(new byte[] { 1, 2, 3 }, audio[10..]);
        _ = bobId;
    }

    [ClientDataFact]
    public async Task OnlyWhoSitsInThatPanelChannelGetsIntoItsRoom()
    {
        var (alice, _, bob, _, guild) = await GuildOfTwo();
        var (carol, _) = await InWorld("Carol");
        await using var a = alice;
        await using var b = bob;
        await using var c = carol;
        await Seat(alice, 0);
        await Seat(bob, 1);
        await using var aliceVoice = await Relay(alice);
        await using var bobVoice = await Relay(bob);
        await using var carolVoice = await Relay(carol);

        Assert.Equal(VoiceJoinCode.NotMember, await JoinRoom(bobVoice, $"2_{guild}_0"));   // sits in channel 1
        Assert.Equal(VoiceJoinCode.NotMember, await JoinRoom(carolVoice, $"2_{guild}_0")); // not in the guild
        Assert.Equal(VoiceJoinCode.NotMember, await JoinRoom(aliceVoice, $"2_{guild + 1}_0")); // another guild
        Assert.Equal(VoiceJoinCode.Ok, await JoinRoom(aliceVoice, $"2_{guild}_0"));
        Assert.Equal(VoiceJoinCode.Ok, await JoinRoom(bobVoice, $"2_{guild}_1"));
    }

    [ClientDataFact]
    public async Task OrderModeHoldsTheMicOfWhoeverHasNotBeenGivenIt()
    {
        var (alice, aliceId, bob, bobId, guild) = await GuildOfTwo();
        await using var a = alice;
        await using var b = bob;
        await Seat(alice, 0);
        await Seat(bob, 0);
        await using var aliceVoice = await Relay(alice);
        await using var bobVoice = await Relay(bob);
        string room = $"2_{guild}_0";
        await JoinRoom(aliceVoice, room);
        await JoinRoom(bobVoice, room);
        await bobVoice.MicAsync(true);
        await aliceVoice.WaitAsync((t, p) => MicIs(t, p, bobId, true), Wait);

        // The leader switches to Order mode (as the permission panel's Save does): Bob's mic goes off for the room.
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.SetSpeakPermissionMsg,
            new GuildVoiceChannelSetSpeakPermissionReq { ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Order },
            GuildVoiceChannelSetSpeakPermissionRsp.Parser)).ErrorCode);
        await aliceVoice.WaitAsync((t, p) => MicIs(t, p, bobId, false), Wait);
        await bobVoice.SendAudioAsync(0, [7, 7, 7]);
        await aliceVoice.ExpectNoneAsync((t, _) => t == VoiceDown.Audio, TimeSpan.FromMilliseconds(500));

        // Given the mic: his own Mic=on comes back and his audio reaches the room.
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.SetSpeakPermissionMsg,
            new GuildVoiceChannelSetSpeakPermissionReq
            {
                ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Order,
                PermissionItems = { new GuildVoiceChannelSpeakPermissionItem { TargetPlayerId = bobId, GrantPermission = true } }
            },
            GuildVoiceChannelSetSpeakPermissionRsp.Parser)).ErrorCode);
        await aliceVoice.WaitAsync((t, p) => MicIs(t, p, bobId, true), Wait);
        await bobVoice.SendAudioAsync(1, [8, 8, 8]);
        var (_, audio) = await aliceVoice.WaitTypeAsync(VoiceDown.Audio, Wait);
        Assert.Equal(new byte[] { 8, 8, 8 }, audio[10..]);

        // The leader never needs it.
        await aliceVoice.MicAsync(true);
        await bobVoice.WaitAsync((t, p) => MicIs(t, p, aliceId, true), Wait);
    }

    [ClientDataFact]
    public async Task AKickFromThePanelChannelDropsTheVoiceConnection()
    {
        var (alice, _, bob, bobId, guild) = await GuildOfTwo();
        await using var a = alice;
        await using var b = bob;
        await Seat(alice, 0);
        await Seat(bob, 0);
        await using var aliceVoice = await Relay(alice);
        await using var bobVoice = await Relay(bob);
        string room = $"2_{guild}_0";
        await JoinRoom(aliceVoice, room);
        await JoinRoom(bobVoice, room);

        Assert.Equal(0, (await Ask(alice, GuildVoiceService.KickMsg,
            new GuildVoiceChannelKickPlayerReq { ChannelIndex = 0, TargetPlayerId = bobId }, GuildVoiceChannelKickPlayerRsp.Parser)).ErrorCode);
        var (_, kicked) = await bobVoice.WaitTypeAsync(VoiceDown.Kicked, TimeSpan.FromSeconds(4)); // the relay's 1 s sweep
        Assert.Equal(VoiceKickCode.RemovedFromRoom, kicked[0]);
        await aliceVoice.WaitAsync((t, p) => t == VoiceDown.MemberChange && BitConverter.ToInt64(p) == bobId && p[8] == 0, Wait);
    }
}
