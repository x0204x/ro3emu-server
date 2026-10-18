using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Voice;

namespace Ro3Emu.Tests;

/// <summary>Team voice relay PoC: the 77015/77016 ticket handoff and the voice gate's own frames.</summary>
public sealed class VoiceRelayTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-voice-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly FakeClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private int _voicePort;

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _voicePort = TestPorts.FreePort();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            o.Voice.Port = _voicePort;
            // Short enough that the "no Auth in time" / idle tests do not need to wait the production defaults.
            o.Voice.FirstFrameTimeoutSeconds = 2;
            o.Voice.IdleTimeoutSeconds = 2;
        }, _clock);
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

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    /// <summary>Alice creates a party and invites Bob in (the same flow as SocialTests.PartyCreateInviteAcceptAndLeave).</summary>
    private static async Task<long> CreatePartyAsync(BotClient alice, long aliceId, BotClient bob, long bobId)
    {
        await alice.SendGameAsync(Server.Game.TeamService.TeamCreate, new Romsg.TeamCreateReq { TeamType = Romsg.TeamType.Five, TargetId = 1001 });
        var created = await Next(alice, Server.Game.TeamService.TeamCreate, Romsg.TeamCreateResp.Parser);
        await Next(alice, Server.Game.TeamService.TeamNewMember, Romsg.BroadCastNewMemberEnter.Parser);
        await Next(alice, Server.Game.TeamService.TeamAll, Romsg.TeamTotalDetailInfo.Parser);

        await alice.SendGameAsync(Server.Game.TeamService.TeamInvite, new Romsg.InviteIntoTeamReq { Guid = bobId });
        await Next(alice, Server.Game.TeamService.TeamInvite, Romsg.InviteIntoTeamResp.Parser);
        await Next(bob, Server.Game.TeamService.TeamNotifyInvitee, Romsg.NotifyInvitee.Parser);

        await bob.SendGameAsync(Server.Game.TeamService.TeamInviteeResp, new Romsg.InviteeResp { TeamId = created.TeamId, IsAgree = true, InviteGuid = aliceId });
        await Next(bob, Server.Game.TeamService.TeamInviteeResp, Romsg.InviteeRespResp.Parser);
        await Next(bob, Server.Game.TeamService.TeamNewMember, Romsg.BroadCastNewMemberEnter.Parser);
        await Next(alice, Server.Game.TeamService.TeamNewMember, Romsg.BroadCastNewMemberEnter.Parser, m => m.DetailInfo.Guid == bobId);
        return created.TeamId;
    }

    private static async Task<(long RoleId, byte[] Token)> GetTicketAsync(BotClient bot, int expectedPort = 0)
    {
        var ticket = await GetRawTicketAsync(bot);
        if (expectedPort != 0) Assert.Equal(expectedPort, ticket.Port);
        return (ticket.RoleId, ticket.Token);
    }

    /// <summary>77016 parsed by hand (not through BotVoiceTicket) so the wire layout itself is pinned here:
    /// u16 port | u8 hostLen | host ASCII | i64 roleId | 16-byte token | u32 ttlSeconds.</summary>
    private static async Task<(ushort Port, string Host, long RoleId, byte[] Token, uint Ttl)> GetRawTicketAsync(BotClient bot)
    {
        using var expected = bot.Inbox.Expect(e => e.IsGame(VoiceTicketService.Ticket));
        await bot.SendGameBytesAsync(VoiceTicketService.TicketReq, []);
        byte[] body = (await expected.WaitAsync(Wait)).Body;
        ushort port = BitConverter.ToUInt16(body, 0);
        int hostLen = body[2];
        string host = System.Text.Encoding.ASCII.GetString(body, 3, hostLen);
        int at = 3 + hostLen;
        Assert.Equal(at + 8 + 16 + 4, body.Length);
        long roleId = BitConverter.ToInt64(body, at);
        byte[] token = body[(at + 8)..(at + 24)];
        uint ttl = BitConverter.ToUInt32(body, at + 24);
        return (port, host, roleId, token, ttl);
    }

    private async Task<VoiceTestClient> ConnectAuthedAsync(long roleId, byte[] token)
    {
        var client = new VoiceTestClient(_voicePort) { Tag = roleId.ToString() };
        await client.ConnectAsync();
        await client.AuthAsync(roleId, token);
        await client.WaitTypeAsync(VoiceDown.AuthOk, Wait);
        return client;
    }

    [Fact]
    public async Task TicketHandoffGivesTheConfiguredPortAndTheCallerRoleId()
    {
        var (alice, aliceId) = await InWorld("Alice");
        await using var a = alice;
        var (roleId, token) = await GetTicketAsync(alice, _voicePort);
        Assert.Equal(aliceId, roleId);
        Assert.NotEqual(new byte[16], token);
    }

    [Fact]
    public async Task TicketCarriesTheHostTheGameGateWasReachedOn()
    {
        var (alice, _) = await InWorld("Alice");
        await using var a = alice;
        var ticket = await GetRawTicketAsync(alice);
        // The bot reached the game gate on loopback, so GateHost falls back to PublicHost: the same rule as the
        // server list.
        Assert.Equal("127.0.0.1", ticket.Host);
        Assert.Equal(_voicePort, ticket.Port);
        Assert.Equal(60u, ticket.Ttl);
    }

    [Fact]
    public async Task MicAudioAndVadFlowBetweenPartyMembers()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long teamId = await CreatePartyAsync(alice, aliceId, bob, bobId);

        var (aliceRole, aliceToken) = await GetTicketAsync(alice);
        var (bobRole, bobToken) = await GetTicketAsync(bob);
        await using var aliceVoice = await ConnectAuthedAsync(aliceRole, aliceToken);
        await using var bobVoice = await ConnectAuthedAsync(bobRole, bobToken);

        string channel = teamId.ToString();
        await aliceVoice.JoinAsync(channel);
        var aliceJoin = await aliceVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        Assert.Equal(VoiceJoinCode.Ok, aliceJoin.Payload[0]);

        await bobVoice.JoinAsync(channel);
        var bobJoin = await bobVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        Assert.Equal(VoiceJoinCode.Ok, bobJoin.Payload[0]);
        // Bob joining a non-empty room is told about Alice already being in it.
        await bobVoice.WaitAsync((t, p) => t == VoiceDown.MemberChange && BitConverter.ToInt64(p) == aliceRole && p[8] == 1, Wait);

        await aliceVoice.MicAsync(true);
        // Both the listener and the speaker herself get told the mic turned on.
        await bobVoice.WaitAsync((t, p) => t == VoiceDown.MicState && BitConverter.ToInt64(p) == aliceRole && p[8] == 1, Wait);
        await aliceVoice.WaitAsync((t, p) => t == VoiceDown.MicState && BitConverter.ToInt64(p) == aliceRole && p[8] == 1, Wait);

        for (ushort seq = 0; seq < 10; seq++)
            await aliceVoice.SendAudioAsync(seq, [(byte)seq, (byte)seq, (byte)seq]);

        await bobVoice.WaitTypeAsync(VoiceDown.Vad, Wait); // speaking = 1, checked below with the rest of the Vad frames
        for (ushort seq = 0; seq < 10; seq++)
        {
            var (type, payload) = await bobVoice.WaitAsync((t, p) => t == VoiceDown.Audio, Wait);
            Assert.Equal(VoiceDown.Audio, type);
            Assert.Equal(aliceRole, BitConverter.ToInt64(payload));
            Assert.Equal(seq, BitConverter.ToUInt16(payload, 8));
            Assert.Equal(new byte[] { (byte)seq, (byte)seq, (byte)seq }, payload[10..]);
        }
        // Alice never hears her own voice.
        await aliceVoice.ExpectNoneAsync((t, _) => t == VoiceDown.Audio, TimeSpan.FromMilliseconds(300));

        // 300 ms of silence after the last frame flips speaking back to 0 (the sweep ticks every 50 ms).
        await bobVoice.WaitAsync((t, p) => t == VoiceDown.Vad && p[8] == 0, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task EchoBotRepeatsAPartyMembersAudioUnderItsOwnRoleIdAndStopsWhenItLeaves()
    {
        var (alice, _) = await InWorld("Alice");
        var (echo, echoId) = await InWorld("EchoBot");
        await using var a = alice;
        await using var e = echo;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // The bot waits for an invite and accepts it, the way BotCli voice-echo does.
        var accepted = BotVoiceEcho.AcceptPartyInviteAsync(echo, _ => { }, stop.Token);
        await alice.SendGameAsync(Server.Game.TeamService.TeamCreate, new Romsg.TeamCreateReq { TeamType = Romsg.TeamType.Five, TargetId = 1001 });
        var created = await Next(alice, Server.Game.TeamService.TeamCreate, Romsg.TeamCreateResp.Parser);
        await alice.SendGameAsync(Server.Game.TeamService.TeamInvite, new Romsg.InviteIntoTeamReq { Guid = echoId });
        long teamId = await accepted.WaitAsync(Wait);
        Assert.Equal(created.TeamId, teamId);

        var (aliceRole, aliceToken) = await GetTicketAsync(alice);
        await using var aliceVoice = await ConnectAuthedAsync(aliceRole, aliceToken);
        await aliceVoice.JoinAsync(teamId.ToString());
        Assert.Equal(VoiceJoinCode.Ok, (await aliceVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait)).Payload[0]);
        await aliceVoice.MicAsync(true);

        var run = BotVoiceEcho.RunAsync(echo, echoId, teamId, TimeSpan.FromMilliseconds(50), _ => { }, stop.Token);
        // The bot is in the room with its mic on before Alice talks.
        await aliceVoice.WaitAsync((t, p) => t == VoiceDown.MicState && BitConverter.ToInt64(p) == echoId && p[8] == 1, Wait);

        for (ushort seq = 0; seq < 5; seq++)
            await aliceVoice.SendAudioAsync(seq, [(byte)(0xA0 + seq), (byte)seq, 7]);
        for (ushort seq = 0; seq < 5; seq++)
        {
            var (_, payload) = await aliceVoice.WaitAsync((t, p) => t == VoiceDown.Audio, Wait);
            Assert.Equal(echoId, BitConverter.ToInt64(payload));
            Assert.Equal(new byte[] { (byte)(0xA0 + seq), (byte)seq, 7 }, payload[10..]);
        }

        // Leaving the party: the membership sweep removes it from the room and the run ends on that Kicked.
        await echo.SendGameAsync(Server.Game.TeamService.TeamLeave, new Romsg.LeaveTeamReq());
        var end = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BotVoiceEchoEndKind.Kicked, end.Kind);
        Assert.Equal(VoiceKickCode.RemovedFromRoom, end.KickCode);
    }

    [Fact]
    public async Task JoinIsRefusedForNonMembersAndForUnsupportedRoomFormats()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        var (carol, _) = await InWorld("Carol");
        await using var a = alice;
        await using var b = bob;
        await using var c = carol;
        long teamId = await CreatePartyAsync(alice, aliceId, bob, bobId);

        var (carolRole, carolToken) = await GetTicketAsync(carol);
        await using var carolVoice = await ConnectAuthedAsync(carolRole, carolToken);

        await carolVoice.JoinAsync(teamId.ToString());
        var notMember = await carolVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        Assert.Equal(VoiceJoinCode.NotMember, notMember.Payload[0]);

        await carolVoice.JoinAsync("2_5_1");
        var unsupported = await carolVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        Assert.Equal(VoiceJoinCode.UnsupportedRoom, unsupported.Payload[0]);
    }

    [Fact]
    public async Task LeavingThePartyDropsTheMemberFromTheRoomOnTheNextSweep()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long teamId = await CreatePartyAsync(alice, aliceId, bob, bobId);

        var (aliceRole, aliceToken) = await GetTicketAsync(alice);
        var (bobRole, bobToken) = await GetTicketAsync(bob);
        await using var aliceVoice = await ConnectAuthedAsync(aliceRole, aliceToken);
        await using var bobVoice = await ConnectAuthedAsync(bobRole, bobToken);
        await aliceVoice.JoinAsync(teamId.ToString());
        await aliceVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await bobVoice.JoinAsync(teamId.ToString());
        await bobVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);

        await bob.SendGameAsync(Server.Game.TeamService.TeamLeave, new Romsg.LeaveTeamReq());
        await Next(bob, Server.Game.TeamService.TeamLeave, Romsg.LeaveTeamResp.Parser);

        // The 1 s membership sweep removes Bob (no longer this team) and tells Alice he left.
        await aliceVoice.WaitAsync((t, p) => t == VoiceDown.MemberChange && BitConverter.ToInt64(p) == bobRole && p[8] == 0, TimeSpan.FromSeconds(3));
        await bobVoice.WaitTypeAsync(VoiceDown.Kicked, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AuthRejectsWrongReusedAndExpiredTickets()
    {
        var (alice, aliceId) = await InWorld("Alice");
        await using var a = alice;

        // Wrong token: the ticket survives for a correct retry.
        var (roleId, token) = await GetTicketAsync(alice);
        var wrongToken = token.ToArray();
        wrongToken[0] ^= 0xFF;
        await using (var wrong = new VoiceTestClient(_voicePort))
        {
            await wrong.ConnectAsync();
            await wrong.AuthAsync(roleId, wrongToken);
            await wrong.ExpectNoneAsync((t, _) => t == VoiceDown.AuthOk, TimeSpan.FromMilliseconds(500));
        }

        // Correct token once, then reused: the second attempt is refused (single use).
        await using (var first = new VoiceTestClient(_voicePort))
        {
            await first.ConnectAsync();
            await first.AuthAsync(roleId, token);
            await first.WaitTypeAsync(VoiceDown.AuthOk, Wait);
        }
        await using (var reused = new VoiceTestClient(_voicePort))
        {
            await reused.ConnectAsync();
            await reused.AuthAsync(roleId, token);
            await reused.ExpectNoneAsync((t, _) => t == VoiceDown.AuthOk, TimeSpan.FromMilliseconds(500));
        }

        // Expired: advance the test clock past TicketSeconds before presenting it.
        var (expiredRole, expiredToken) = await GetTicketAsync(alice);
        _clock.Advance(TimeSpan.FromSeconds(61));
        await using var expired = new VoiceTestClient(_voicePort);
        await expired.ConnectAsync();
        await expired.AuthAsync(expiredRole, expiredToken);
        await expired.ExpectNoneAsync((t, _) => t == VoiceDown.AuthOk, TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task NoAuthWithinTheFirstFrameTimeoutClosesTheConnection()
    {
        await using var client = new VoiceTestClient(_voicePort);
        await client.ConnectAsync();
        await client.PingAsync(); // not an Auth frame: refused as the very first frame
        await Wait200UntilAsync(() => client.Closed, TimeSpan.FromSeconds(4));
        Assert.True(client.Closed);
    }

    [Fact]
    public async Task SustainedFloodDisconnectsAWellBehavedBurstIsOnlyThrottled()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long teamId = await CreatePartyAsync(alice, aliceId, bob, bobId);
        var (aliceRole, aliceToken) = await GetTicketAsync(alice);
        var (bobRole, bobToken) = await GetTicketAsync(bob);
        await using var aliceVoice = await ConnectAuthedAsync(aliceRole, aliceToken);
        await using var bobVoice = await ConnectAuthedAsync(bobRole, bobToken);
        await aliceVoice.JoinAsync(teamId.ToString());
        await aliceVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await bobVoice.JoinAsync(teamId.ToString());
        await bobVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await aliceVoice.MicAsync(true);
        await bobVoice.WaitTypeAsync(VoiceDown.MicState, Wait);

        // A 200-frame burst in well under a second: the token bucket forwards at most Burst + FramesPerSecond's
        // worth (defaults 10 + 50 = 60) and otherwise just drops the rest, no disconnect.
        for (ushort seq = 0; seq < 200; seq++) await aliceVoice.SendAudioAsync(seq, [1]);
        int forwarded = 0;
        try
        {
            while (true)
            {
                await bobVoice.WaitAsync((t, _) => t == VoiceDown.Audio, TimeSpan.FromMilliseconds(300));
                forwarded++;
            }
        }
        catch (TimeoutException) { }
        // The bucket keeps refilling (50/s) while the burst is still being written, so a slow run lets a few more
        // through than the 60 banked; the point is that most of the 200 are dropped.
        Assert.InRange(forwarded, 1, 80);
        Assert.False(aliceVoice.Closed);

        // Sustained excess for the whole 5 s flood window disconnects the sender.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        ushort floodSeq = 0;
        bool writeFailed = false;
        while (DateTime.UtcNow < deadline && !aliceVoice.Closed && !writeFailed)
        {
            try
            {
                for (int i = 0; i < 20 && !aliceVoice.Closed; i++) await aliceVoice.SendAudioAsync(floodSeq++, [1]);
            }
            catch (IOException) { writeFailed = true; } // the server closed the socket under a pending write
            await Task.Delay(20);
        }
        Assert.True(aliceVoice.Closed || writeFailed);
    }

    [Fact]
    public async Task LeaderMuteSilencesTheMemberAndBroadcastsMicOff()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long teamId = await CreatePartyAsync(alice, aliceId, bob, bobId);
        var (aliceRole, aliceToken) = await GetTicketAsync(alice);
        var (bobRole, bobToken) = await GetTicketAsync(bob);
        await using var aliceVoice = await ConnectAuthedAsync(aliceRole, aliceToken);
        await using var bobVoice = await ConnectAuthedAsync(bobRole, bobToken);
        await aliceVoice.JoinAsync(teamId.ToString());
        await aliceVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await bobVoice.JoinAsync(teamId.ToString());
        await bobVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await bobVoice.MicAsync(true);
        await aliceVoice.WaitTypeAsync(VoiceDown.MicState, Wait);

        // Leader (Alice) forbids Bob's chat: TeamChangeMemberState, EteamMemberStateForbiddenChat.
        await alice.SendGameAsync(Server.Game.TeamService.TeamChangeMemberState, new Romsg.ChangeTeamMemberStateReq
        {
            EOperType = Romsg.ETeamMemberStateOperEnum.EteamMemberStateForbiddenChat,
            MemberGuid = bobId,
            IsForbiddenChat = true,
        });
        await Next(alice, Server.Game.TeamService.TeamChangeMemberState, Romsg.ChangeTeamMemberStateResp.Parser);

        // The next sweep forces Bob's mic off and tells the room.
        await aliceVoice.WaitAsync((t, p) => t == VoiceDown.MicState && BitConverter.ToInt64(p) == bobRole && p[8] == 0, TimeSpan.FromSeconds(3));
        await bobVoice.SendAudioAsync(0, [9, 9, 9]);
        await aliceVoice.ExpectNoneAsync((t, _) => t == VoiceDown.Audio, TimeSpan.FromMilliseconds(500));
    }

    private static bool MicStateOf(byte type, byte[] payload, long role, bool on) =>
        type == VoiceDown.MicState && BitConverter.ToInt64(payload) == role && payload[8] == (on ? 1 : 0);

    [Fact]
    public async Task LeaderMuteHoldsAgainstMicOnAndARejoinAndLiftingItGivesTheMicBack()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long teamId = await CreatePartyAsync(alice, aliceId, bob, bobId);
        var (aliceRole, aliceToken) = await GetTicketAsync(alice);
        var (bobRole, bobToken) = await GetTicketAsync(bob);
        await using var aliceVoice = await ConnectAuthedAsync(aliceRole, aliceToken);
        await using var bobVoice = await ConnectAuthedAsync(bobRole, bobToken);
        string channel = teamId.ToString();
        await aliceVoice.JoinAsync(channel);
        await aliceVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await bobVoice.JoinAsync(channel);
        await bobVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await bobVoice.MicAsync(true);
        await aliceVoice.WaitAsync((t, p) => MicStateOf(t, p, bobRole, true), Wait);
        await bobVoice.WaitAsync((t, p) => MicStateOf(t, p, bobRole, true), Wait);

        async Task Forbid(bool forbidden)
        {
            await alice.SendGameAsync(Server.Game.TeamService.TeamChangeMemberState, new Romsg.ChangeTeamMemberStateReq
            {
                EOperType = Romsg.ETeamMemberStateOperEnum.EteamMemberStateForbiddenChat,
                MemberGuid = bobId,
                IsForbiddenChat = forbidden,
            });
            await Next(alice, Server.Game.TeamService.TeamChangeMemberState, Romsg.ChangeTeamMemberStateResp.Parser);
        }

        await Forbid(true);
        await aliceVoice.WaitAsync((t, p) => MicStateOf(t, p, bobRole, false), TimeSpan.FromSeconds(3));
        await bobVoice.WaitAsync((t, p) => MicStateOf(t, p, bobRole, false), TimeSpan.FromSeconds(3));

        // Bob's client asks for the mic again and talks at once, inside the same second: refused, answered to him
        // alone with MicState off, and his audio reaches nobody.
        await bobVoice.MicAsync(true);
        await bobVoice.WaitAsync((t, p) => MicStateOf(t, p, bobRole, false), Wait);
        for (ushort seq = 0; seq < 5; seq++) await bobVoice.SendAudioAsync(seq, [9, 9, 9]);
        await aliceVoice.ExpectNoneAsync((t, p) => t == VoiceDown.Audio || MicStateOf(t, p, bobRole, true), TimeSpan.FromMilliseconds(600));

        // Leaving and joining again does not shake the mute off.
        await bobVoice.LeaveAsync();
        await aliceVoice.WaitAsync((t, p) => t == VoiceDown.MemberChange && BitConverter.ToInt64(p) == bobRole && p[8] == 0, Wait);
        await bobVoice.JoinAsync(channel);
        Assert.Equal(VoiceJoinCode.Ok, (await bobVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait)).Payload[0]);
        await bobVoice.MicAsync(true);
        await bobVoice.WaitAsync((t, p) => MicStateOf(t, p, bobRole, false), Wait);
        for (ushort seq = 5; seq < 10; seq++) await bobVoice.SendAudioAsync(seq, [9, 9, 9]);
        await aliceVoice.ExpectNoneAsync((t, p) => t == VoiceDown.Audio || MicStateOf(t, p, bobRole, true), TimeSpan.FromMilliseconds(600));

        // The leader lifts the mute: Bob still wants his mic, so it comes back and his voice flows again.
        await Forbid(false);
        await aliceVoice.WaitAsync((t, p) => MicStateOf(t, p, bobRole, true), TimeSpan.FromSeconds(3));
        await bobVoice.SendAudioAsync(10, [7]);
        var heard = await aliceVoice.WaitTypeAsync(VoiceDown.Audio, Wait);
        Assert.Equal(bobRole, BitConverter.ToInt64(heard.Payload));
    }

    [Fact]
    public async Task ControlFramesHaveABudgetAndAFloodingMemberCannotDisconnectTheOthers()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long teamId = await CreatePartyAsync(alice, aliceId, bob, bobId);
        var (aliceRole, aliceToken) = await GetTicketAsync(alice);
        var (bobRole, bobToken) = await GetTicketAsync(bob);
        await using var aliceVoice = await ConnectAuthedAsync(aliceRole, aliceToken);
        await using var bobVoice = await ConnectAuthedAsync(bobRole, bobToken);
        string channel = teamId.ToString();
        await bobVoice.JoinAsync(channel);
        await bobVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await aliceVoice.JoinAsync(channel);
        await aliceVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await bobVoice.WaitAsync((t, p) => t == VoiceDown.MemberChange && BitConverter.ToInt64(p) == aliceRole && p[8] == 1, Wait);

        async Task<int> CountAtBob(Func<byte, byte[], bool> match)
        {
            int n = 0;
            try { while (true) { await bobVoice.WaitAsync(match, TimeSpan.FromMilliseconds(500)); n++; } }
            catch (TimeoutException) { }
            return n;
        }

        // Joining the room she is already in: confirmed, but Bob hears nothing of it.
        for (int i = 0; i < 10; i++) await aliceVoice.JoinAsync(channel);
        for (int i = 0; i < 10; i++) await aliceVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        Assert.Equal(0, await CountAtBob((t, p) => t == VoiceDown.MemberChange && BitConverter.ToInt64(p) == aliceRole));

        // The same Mic over and over: one change, one event.
        for (int i = 0; i < 100; i++) await aliceVoice.MicAsync(true);
        Assert.Equal(1, await CountAtBob((t, p) => t == VoiceDown.MicState && BitConverter.ToInt64(p) == aliceRole));

        // Toggling as fast as the socket goes: the budget (ControlBurst 20 + ControlPerSecond 10, already spent
        // above) lets a handful through, and the sustained flood disconnects Alice, not the room.
        var started = DateTime.UtcNow;
        bool writeFailed = false;
        try { for (int i = 0; i < 400 && !aliceVoice.Closed; i++) await aliceVoice.MicAsync(i % 2 == 1); }
        catch (IOException) { writeFailed = true; } // the server closed her socket under a pending write
        int toggles = await CountAtBob((t, p) => t == VoiceDown.MicState && BitConverter.ToInt64(p) == aliceRole);
        double seconds = (DateTime.UtcNow - started).TotalSeconds;
        Assert.InRange(toggles, 0, 20 + 10 + (int)Math.Ceiling(seconds * 10));
        if (!writeFailed)
        {
            var kicked = await aliceVoice.WaitTypeAsync(VoiceDown.Kicked, Wait);
            Assert.Equal(VoiceKickCode.Flood, kicked.Payload[0]);
        }
        await bobVoice.WaitAsync((t, p) => t == VoiceDown.MemberChange && BitConverter.ToInt64(p) == aliceRole && p[8] == 0, Wait);

        Assert.False(bobVoice.Closed);
        await bobVoice.PingAsync();
        await bobVoice.WaitTypeAsync(VoiceDown.Pong, Wait);
    }

    [Fact]
    public async Task LegacyAccountsDifferingOnlyInCaseKeepTheirOwnVoiceConnections()
    {
        var lower = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await using var l = lower;
        await lower.LoginAsync("admin", "pw");
        // A database from before accounts were one per name regardless of case can hold a second spelling.
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")};Pooling=False"))
        {
            raw.Open();
            using var insert = raw.CreateCommand();
            insert.CommandText = "INSERT INTO account(name,password_hash,salt,created_utc) SELECT 'Admin',password_hash,salt,created_utc FROM account WHERE name='admin'";
            Assert.Equal(1, insert.ExecuteNonQuery());
        }
        var upper = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await using var u = upper;
        await upper.LoginAsync("Admin", "pw");

        async Task<long> Enter(BotClient bot, string role)
        {
            await bot.EnterGateAsync();
            long id = (await bot.CreateRoleAsync(role)).RoleBaseInfo.Id;
            await bot.EnterWorldAsync(id);
            return id;
        }
        long lowerId = await Enter(lower, "Lowercase");
        long upperId = await Enter(upper, "Uppercase");

        var (lowerRole, lowerToken) = await GetTicketAsync(lower);
        var (upperRole, upperToken) = await GetTicketAsync(upper);
        Assert.Equal(lowerId, lowerRole);
        Assert.Equal(upperId, upperRole);
        await using var lowerVoice = await ConnectAuthedAsync(lowerRole, lowerToken);
        await using var upperVoice = await ConnectAuthedAsync(upperRole, upperToken);

        // Two accounts, two connections: neither replaces the other.
        await lowerVoice.ExpectNoneAsync((t, _) => t == VoiceDown.Kicked, TimeSpan.FromMilliseconds(500));
        await upperVoice.ExpectNoneAsync((t, _) => t == VoiceDown.Kicked, TimeSpan.FromMilliseconds(100));
        foreach (var voice in new[] { lowerVoice, upperVoice })
        {
            await voice.PingAsync();
            await voice.WaitTypeAsync(VoiceDown.Pong, Wait);
            Assert.False(voice.Closed);
        }
    }

    [Fact]
    public async Task MicOffForwardsNothingSpeakerOffHearsNothing()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long teamId = await CreatePartyAsync(alice, aliceId, bob, bobId);
        var (aliceRole, aliceToken) = await GetTicketAsync(alice);
        var (bobRole, bobToken) = await GetTicketAsync(bob);
        await using var aliceVoice = await ConnectAuthedAsync(aliceRole, aliceToken);
        await using var bobVoice = await ConnectAuthedAsync(bobRole, bobToken);
        await aliceVoice.JoinAsync(teamId.ToString());
        await aliceVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await bobVoice.JoinAsync(teamId.ToString());
        await bobVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);

        // Mic stays off (the default): nothing is forwarded even though both are in the room.
        await aliceVoice.SendAudioAsync(0, [1, 2, 3]);
        await bobVoice.ExpectNoneAsync((t, _) => t == VoiceDown.Audio, TimeSpan.FromMilliseconds(500));

        // Mic on, but the listener turned its speaker off: it hears nothing either.
        await aliceVoice.MicAsync(true);
        await bobVoice.WaitTypeAsync(VoiceDown.MicState, Wait);
        await bobVoice.SpeakerAsync(false);
        // Speaker has no reply; a Pong on the same connection proves the server handled it before Alice's audio,
        // which travels on another socket and could otherwise overtake it.
        await bobVoice.PingAsync();
        await bobVoice.WaitTypeAsync(VoiceDown.Pong, Wait);
        await aliceVoice.SendAudioAsync(1, [4, 5, 6]);
        await bobVoice.ExpectNoneAsync((t, _) => t == VoiceDown.Audio, TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task VoicePortZeroLeavesTheRelayOffAndTheTicketSaysSo()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-voice-off-" + Guid.NewGuid().ToString("N"));
        var off = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
            // o.Voice.Port left at 0 (the default): the relay must not open a listener at all.
        });
        await off.StartAsync();
        try
        {
            // ServerHost only registers the voice gate's hosted service when a port is configured for it.
            Assert.DoesNotContain(off.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is VoiceGateServer);

            var (bot, _) = await InWorldOn($"http://127.0.0.1:{httpPort}", dataDir, "Solo");
            await using var s = bot;
            var ticket = await GetRawTicketAsync(bot);
            Assert.Equal(0, ticket.Port);
            Assert.Equal("", ticket.Host);
            Assert.Equal(new byte[16], ticket.Token);
            Assert.Equal(0u, ticket.Ttl);
        }
        finally
        {
            await off.StopAsync();
            await off.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task MicOffMidSentenceEndsTheSpeakingLightAtOnce()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long teamId = await CreatePartyAsync(alice, aliceId, bob, bobId);
        var (aliceRole, aliceToken) = await GetTicketAsync(alice);
        var (bobRole, bobToken) = await GetTicketAsync(bob);
        await using var aliceVoice = await ConnectAuthedAsync(aliceRole, aliceToken);
        await using var bobVoice = await ConnectAuthedAsync(bobRole, bobToken);
        await aliceVoice.JoinAsync(teamId.ToString());
        await aliceVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await bobVoice.JoinAsync(teamId.ToString());
        await bobVoice.WaitTypeAsync(VoiceDown.JoinResult, Wait);
        await aliceVoice.MicAsync(true);
        await bobVoice.WaitAsync((t, p) => t == VoiceDown.MicState && p[8] == 1, Wait);

        await aliceVoice.SendAudioAsync(0, [1]);
        await bobVoice.WaitAsync((t, p) => t == VoiceDown.Vad && p[8] == 1, Wait);
        await aliceVoice.MicAsync(false);

        // Vad speaking=0 rides with the MicState off, not 300 ms later from the silence sweep.
        await bobVoice.WaitAsync((t, p) => t == VoiceDown.MicState && p[8] == 0, Wait);
        await bobVoice.WaitAsync((t, p) => t == VoiceDown.Vad && p[8] == 0, TimeSpan.FromMilliseconds(150));
    }

    [Fact]
    public async Task OneIpCannotHoldMoreThanItsConnectionShare()
    {
        var clients = new List<VoiceTestClient>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var c = new VoiceTestClient(_voicePort);
                await c.ConnectAsync();
                clients.Add(c);
            }
            var extra = new VoiceTestClient(_voicePort);
            clients.Add(extra);
            await extra.ConnectAsync();
            await Wait200UntilAsync(() => extra.Closed, Wait);
            Assert.True(extra.Closed);
            Assert.False(clients[0].Closed);
        }
        finally
        {
            foreach (var c in clients) await c.DisposeAsync();
        }
    }

    [Fact]
    public async Task RepeatedConnectionsWithoutAValidAuthGetTheIpRefused()
    {
        var (alice, _) = await InWorld("Alice");
        await using var a = alice;
        for (int i = 0; i < 10; i++)
        {
            await using var bad = new VoiceTestClient(_voicePort);
            await bad.ConnectAsync();
            await bad.AuthAsync(1, new byte[16]);
            await Wait200UntilAsync(() => bad.Closed, Wait);
            Assert.True(bad.Closed);
        }

        // Now even a valid ticket is refused from this IP for the ban period.
        var (roleId, token) = await GetTicketAsync(alice);
        await using var good = new VoiceTestClient(_voicePort);
        await good.ConnectAsync();
        try { await good.AuthAsync(roleId, token); } catch (IOException) { } // the socket may already be gone
        await good.ExpectNoneAsync((t, _) => t == VoiceDown.AuthOk, TimeSpan.FromMilliseconds(500));
        await Wait200UntilAsync(() => good.Closed, Wait);
        Assert.True(good.Closed);
    }

    [Theory]
    [InlineData("127.0.0.1", null, false, true)]
    [InlineData("192.168.1.20", null, false, false)] // plaintext voice off loopback needs the explicit opt-in
    [InlineData("192.168.1.20", null, true, true)]
    [InlineData("192.168.1.20", "127.0.0.1", false, true)] // bound to loopback whatever the public host says
    public void PlaintextVoiceServesOnLoopbackOrOnlyWithTheOptIn(string publicHost, string? bind, bool allow, bool served)
    {
        var o = new ServerOptions { PublicHost = publicHost, BindAddress = bind };
        o.Voice.Port = 33101;
        o.Voice.AllowPlaintextOffLoopback = allow;
        Assert.Equal(served, o.VoiceServed());
        o.Voice.Port = 0;
        Assert.False(o.VoiceServed());
    }

    private static async Task<(BotClient Bot, long Id)> InWorldOn(string http, string dataDir, string name)
    {
        var bot = new BotClient(http, File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private static async Task Wait200UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
    }
}
