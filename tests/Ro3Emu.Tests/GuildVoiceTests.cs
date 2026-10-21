using Microsoft.AspNetCore.Builder;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Guild voice channels (83700-83723, LC_DAL_GuildVoice / LC_GuildVoiceServer): the channel list with its counts,
/// join and leave, the 83709 state push the panel rebuilds itself from, Order mode (the mic on request), kicks with
/// their block list, and leaving the channel when the guild or the game is left.
/// </summary>
public sealed class GuildVoiceTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-guildvoice-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "ro3emu.db")}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(db);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private async Task<(BotClient Bot, long Id)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
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
        (await client.PostAsync($"{_http}/ro3emu/gm?account={name.ToLowerInvariant()}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    /// <summary>Alice founds a guild with auto-accept on and Bob joins it (GuildCaravanTests' shortest path).</summary>
    private async Task<(BotClient Alice, long AliceId, BotClient Bob, long BobId, long Guild)> GuildOfTwo()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await Gm("Alice", "!lvl 34");
        await alice.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Voices", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(alice, GuildService.Create, GuildCreateResp.Parser)).Result);
        long guild = (await Next(alice, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
        await Gm("Bob", "!lvl 34");
        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(alice, GuildService.Edit, GuildEditResp.Parser)).Result);
        await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guild } });
        Assert.Equal(0, Assert.Single((await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await Next(bob, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == guild);
        return (alice, aliceId, bob, bobId, guild);
    }

    private static Task<GuildVoiceChannelJoinRsp> Join(BotClient bot, int channel) =>
        Ask(bot, GuildVoiceService.JoinMsg, new GuildVoiceChannelJoinReq { ChannelIndex = channel }, GuildVoiceChannelJoinRsp.Parser);

    private static Task<GuildVoiceChannelLeaveRsp> Leave(BotClient bot, int channel) =>
        Ask(bot, GuildVoiceService.LeaveMsg, new GuildVoiceChannelLeaveReq { ChannelIndex = channel }, GuildVoiceChannelLeaveRsp.Parser);

    private static Task<GuildVoiceChannelUpdateNotify> Update(BotClient bot, Func<GuildVoiceChannelInfo, bool> match) =>
        Next(bot, GuildVoiceService.UpdateNotify, GuildVoiceChannelUpdateNotify.Parser, n => match(n.ChannelInfo));

    private static GuildVoiceChannelMember Member(GuildVoiceChannelInfo info, long id) => info.Members.Single(m => m.PlayerId == id);

    [ClientDataFact]
    public async Task TheChannelListCountsWhoJoinedAndLeft()
    {
        var (alice, aliceId, bob, bobId, _) = await GuildOfTwo();
        await using var a = alice;
        await using var b = bob;

        var counts = await Ask(alice, GuildVoiceService.AllMemberCountMsg, new GuildVoiceChannelGetAllMemberCountReq(), GuildVoiceChannelGetAllMemberCountRsp.Parser);
        Assert.Equal(0, counts.ErrorCode);
        Assert.Equal([0, 1, 2], counts.ChannelCounts.Select(c => c.ChannelIndex)); // GuildSettingConfig.GuildVoiceChannelCount = 3, 0-based
        Assert.All(counts.ChannelCounts, c => Assert.Equal(0, c.MemberCount));

        var aliceJoin = await Join(alice, 0);
        Assert.Equal(0, aliceJoin.ErrorCode);
        Assert.Equal(0, aliceJoin.ChannelInfo.ChannelIndex);
        Assert.Equal(GuildSpeakAuthorityType.Free, aliceJoin.ChannelInfo.SpeakMode);
        Assert.Equal([aliceId], aliceJoin.ChannelInfo.Members.Select(m => m.PlayerId));

        var bobJoin = await Join(bob, 0);
        Assert.Equal(0, bobJoin.ErrorCode);
        Assert.Equal(2, bobJoin.ChannelInfo.Members.Count);
        // The one already inside learns of Bob from the state push; everybody may talk in Free mode.
        var both = await Update(alice, i => i.Members.Count == 2);
        Assert.True(Member(both.ChannelInfo, bobId).HasSpeakPermission);
        Assert.Equal(2, both.ChannelInfo.TotalMemberCount);

        counts = await Ask(bob, GuildVoiceService.AllMemberCountMsg, new GuildVoiceChannelGetAllMemberCountReq(), GuildVoiceChannelGetAllMemberCountRsp.Parser);
        Assert.Equal(2, counts.ChannelCounts.Single(c => c.ChannelIndex == 0).MemberCount);

        Assert.Equal(0, (await Leave(bob, 0)).ErrorCode);
        var aliceAlone = await Update(alice, i => i.Members.Count == 1);
        Assert.Equal(aliceId, aliceAlone.ChannelInfo.Members.Single().PlayerId);
    }

    [ClientDataFact]
    public async Task JoinAndLeaveRefuseWhatTheClientExpectsThemTo()
    {
        var (alice, _, bob, _, _) = await GuildOfTwo();
        var (carol, _) = await InWorld("Carol");
        await using var a = alice;
        await using var b = bob;
        await using var c = carol;

        Assert.Equal(ErrorCodes.GuildVoiceNotInGuild, (await Join(carol, 0)).ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceBadChannel, (await Join(alice, 3)).ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceBadChannel, (await Join(alice, -1)).ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceNotInChannel, (await Leave(alice, 0)).ErrorCode);
        Assert.Equal(0, (await Join(alice, 0)).ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceAlreadyInChannel, (await Join(alice, 0)).ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceInAnotherChannel, (await Join(alice, 1)).ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceNotInChannel, (await Leave(alice, 1)).ErrorCode);
    }

    [ClientDataFact]
    public async Task OrderModeGivesTheMicOnRequest()
    {
        var (alice, aliceId, bob, bobId, _) = await GuildOfTwo();
        await using var a = alice;
        await using var b = bob;
        await Join(alice, 0);
        await Join(bob, 0);

        // Free mode: nothing to ask for; the leader never needs to.
        Assert.Equal(ErrorCodes.GuildVoiceFreeMode, (await Ask(bob, GuildVoiceService.ApplyMicMsg,
            new GuildVoiceChannelApplyMicReq { ChannelIndex = 0 }, GuildVoiceChannelApplyMicRsp.Parser)).ErrorCode);

        // Only the leader or a member with the GuildVoice right (23) switches the mode.
        Assert.Equal(ErrorCodes.GuildVoiceNoPermission, (await Ask(bob, GuildVoiceService.SetSpeakModeMsg,
            new GuildVoiceChannelSetSpeakModeReq { ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Order }, GuildVoiceChannelSetSpeakModeRsp.Parser)).ErrorCode);
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.SetSpeakModeMsg,
            new GuildVoiceChannelSetSpeakModeReq { ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Order }, GuildVoiceChannelSetSpeakModeRsp.Parser)).ErrorCode);
        var ordered = await Update(bob, i => i.SpeakMode == GuildSpeakAuthorityType.Order);
        Assert.True(Member(ordered.ChannelInfo, aliceId).HasSpeakPermission);
        Assert.False(Member(ordered.ChannelInfo, bobId).HasSpeakPermission);

        Assert.Equal(ErrorCodes.GuildVoiceManagerNeedsNoApply, (await Ask(alice, GuildVoiceService.ApplyMicMsg,
            new GuildVoiceChannelApplyMicReq { ChannelIndex = 0 }, GuildVoiceChannelApplyMicRsp.Parser)).ErrorCode);
        Assert.Equal(0, (await Ask(bob, GuildVoiceService.ApplyMicMsg,
            new GuildVoiceChannelApplyMicReq { ChannelIndex = 0, Reason = "boss call" }, GuildVoiceChannelApplyMicRsp.Parser)).ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceAlreadyApplied, (await Ask(bob, GuildVoiceService.ApplyMicMsg,
            new GuildVoiceChannelApplyMicReq { ChannelIndex = 0 }, GuildVoiceChannelApplyMicRsp.Parser)).ErrorCode);
        var asked = await Update(alice, i => i.ApplyList.Count == 1);
        Assert.Equal((bobId, "boss call"), (asked.ChannelInfo.ApplyList[0].PlayerId, asked.ChannelInfo.ApplyList[0].Reason));

        Assert.Equal(0, (await Ask(alice, GuildVoiceService.HandleApplyMsg,
            new GuildVoiceChannelHandleApplyReq { ChannelIndex = 0, TargetPlayerId = bobId, IsAgree = true }, GuildVoiceChannelHandleApplyRsp.Parser)).ErrorCode);
        var granted = await Update(bob, i => i.ApplyList.Count == 0 && i.Members.Single(m => m.PlayerId == bobId).HasSpeakPermission);
        Assert.Equal(GuildSpeakAuthorityType.Order, granted.ChannelInfo.SpeakMode);
        Assert.Equal(ErrorCodes.GuildVoiceNotApplied, (await Ask(alice, GuildVoiceService.HandleApplyMsg,
            new GuildVoiceChannelHandleApplyReq { ChannelIndex = 0, TargetPlayerId = bobId, IsAgree = true }, GuildVoiceChannelHandleApplyRsp.Parser)).ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceAlreadyCanSpeak, (await Ask(bob, GuildVoiceService.ApplyMicMsg,
            new GuildVoiceChannelApplyMicReq { ChannelIndex = 0 }, GuildVoiceChannelApplyMicRsp.Parser)).ErrorCode);
        // The panel sending the mode the channel already has keeps the mic Bob was given.
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.SetSpeakModeMsg,
            new GuildVoiceChannelSetSpeakModeReq { ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Order }, GuildVoiceChannelSetSpeakModeRsp.Parser)).ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceAlreadyCanSpeak, (await Ask(bob, GuildVoiceService.ApplyMicMsg,
            new GuildVoiceChannelApplyMicReq { ChannelIndex = 0 }, GuildVoiceChannelApplyMicRsp.Parser)).ErrorCode);

        // Taking it back, in a batch; the leader's own mic cannot be taken.
        Assert.Equal(ErrorCodes.GuildVoiceLeaderCannotMute, (await Ask(alice, GuildVoiceService.SetSpeakPermissionMsg,
            new GuildVoiceChannelSetSpeakPermissionReq
            {
                ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Order,
                PermissionItems = { new GuildVoiceChannelSpeakPermissionItem { TargetPlayerId = aliceId, GrantPermission = false } }
            },
            GuildVoiceChannelSetSpeakPermissionRsp.Parser)).ErrorCode);
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.SetSpeakPermissionMsg,
            new GuildVoiceChannelSetSpeakPermissionReq
            {
                ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Order,
                PermissionItems = { new GuildVoiceChannelSpeakPermissionItem { TargetPlayerId = bobId, GrantPermission = false } }
            },
            GuildVoiceChannelSetSpeakPermissionRsp.Parser)).ErrorCode);
        // Taken back: Bob may ask again (holding the mic, he would get "already may speak").
        Assert.Equal(0, (await Ask(bob, GuildVoiceService.ApplyMicMsg,
            new GuildVoiceChannelApplyMicReq { ChannelIndex = 0 }, GuildVoiceChannelApplyMicRsp.Parser)).ErrorCode);

        // A refused request leaves the mic off and the list empty.
        await Update(alice, i => i.ApplyList.Count == 1);
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.HandleApplyMsg,
            new GuildVoiceChannelHandleApplyReq { ChannelIndex = 0, TargetPlayerId = bobId, IsAgree = false }, GuildVoiceChannelHandleApplyRsp.Parser)).ErrorCode);
        // Waited for by its content (earlier pushes still sit in the inbox): the list emptied and the mic stayed off.
        await Update(bob, i => i.ApplyList.Count == 0 && !i.Members.Single(m => m.PlayerId == bobId).HasSpeakPermission);
        Assert.Equal(0, (await Ask(bob, GuildVoiceService.ApplyMicMsg,
            new GuildVoiceChannelApplyMicReq { ChannelIndex = 0 }, GuildVoiceChannelApplyMicRsp.Parser)).ErrorCode); // may ask again
    }

    [ClientDataFact]
    public async Task ThePermissionPanelSwitchesTheModeThroughItsSave()
    {
        // UI_Sys_Chat_PermissionSetupWidgetCtrl._onClickSave sends only 83722, carrying the dropdown's mode with the
        // changed mics; this client never sends 83702.
        var (alice, aliceId, bob, bobId, _) = await GuildOfTwo();
        await using var a = alice;
        await using var b = bob;
        await Join(alice, 0);
        await Join(bob, 0);

        Assert.Equal(ErrorCodes.GuildVoiceNoPermission, (await Ask(bob, GuildVoiceService.SetSpeakPermissionMsg,
            new GuildVoiceChannelSetSpeakPermissionReq { ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Order },
            GuildVoiceChannelSetSpeakPermissionRsp.Parser)).ErrorCode);
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.SetSpeakPermissionMsg,
            new GuildVoiceChannelSetSpeakPermissionReq { ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Order },
            GuildVoiceChannelSetSpeakPermissionRsp.Parser)).ErrorCode);
        var ordered = await Update(bob, i => i.SpeakMode == GuildSpeakAuthorityType.Order);
        Assert.False(Member(ordered.ChannelInfo, bobId).HasSpeakPermission);
        Assert.True(Member(ordered.ChannelInfo, aliceId).HasSpeakPermission);

        // Mode and a mic in one Save: the mic given in the same request survives the switch's reset.
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.SetSpeakPermissionMsg,
            new GuildVoiceChannelSetSpeakPermissionReq { ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Free }, GuildVoiceChannelSetSpeakPermissionRsp.Parser)).ErrorCode);
        await Update(bob, i => i.SpeakMode == GuildSpeakAuthorityType.Free);
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.SetSpeakPermissionMsg,
            new GuildVoiceChannelSetSpeakPermissionReq
            {
                ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Order,
                PermissionItems = { new GuildVoiceChannelSpeakPermissionItem { TargetPlayerId = bobId, GrantPermission = true } }
            },
            GuildVoiceChannelSetSpeakPermissionRsp.Parser)).ErrorCode);
        await Update(bob, i => i.SpeakMode == GuildSpeakAuthorityType.Order && i.Members.Single(m => m.PlayerId == bobId).HasSpeakPermission);
    }

    [ClientDataFact]
    public async Task AKickBlocksTheChannelUntilUnblocked()
    {
        var (alice, aliceId, bob, bobId, _) = await GuildOfTwo();
        await using var a = alice;
        await using var b = bob;
        await Join(alice, 0);
        await Join(bob, 0);

        Assert.Equal(ErrorCodes.GuildVoiceNoPermission, (await Ask(bob, GuildVoiceService.KickMsg,
            new GuildVoiceChannelKickPlayerReq { ChannelIndex = 0, TargetPlayerId = aliceId }, GuildVoiceChannelKickPlayerRsp.Parser)).ErrorCode);
        Assert.Equal(ErrorCodes.GuildVoiceCannotKickSelf, (await Ask(alice, GuildVoiceService.KickMsg,
            new GuildVoiceChannelKickPlayerReq { ChannelIndex = 0, TargetPlayerId = aliceId }, GuildVoiceChannelKickPlayerRsp.Parser)).ErrorCode);

        using var kicked = bob.Inbox.Expect(e => e.IsGame(GuildVoiceService.KickedNotify));
        Assert.Equal(0, (await Ask(alice, GuildVoiceService.KickMsg,
            new GuildVoiceChannelKickPlayerReq { ChannelIndex = 0, TargetPlayerId = bobId }, GuildVoiceChannelKickPlayerRsp.Parser)).ErrorCode);
        Assert.Equal(0, GuildVoiceChannelKickedNotify.Parser.ParseFrom((await kicked.WaitAsync(Wait)).Body).ChannelIndex);
        await Update(alice, i => i.Members.Count == 1);

        Assert.Equal(ErrorCodes.GuildVoiceBlocked, (await Join(bob, 0)).ErrorCode);
        Assert.Equal(0, (await Join(bob, 1)).ErrorCode); // the block is per channel
        Assert.Equal(0, (await Leave(bob, 1)).ErrorCode);
        var blocked = await Ask(alice, GuildVoiceService.BlackListMsg, new GuildVoiceChannelGetBlackListReq { ChannelIndex = 0 }, GuildVoiceChannelGetBlackListRsp.Parser);
        Assert.Equal(0, blocked.ErrorCode);
        Assert.True(blocked.BlackList[bobId] > DateTimeOffset.UtcNow.AddHours(23).ToUnixTimeMilliseconds()); // GuildVoiceChannelBlacklistDuration 24 h

        var unban = await Ask(alice, GuildVoiceService.UnbanMsg, new GuildVoiceChannelUnbanPlayerReq { ChannelIndex = 0, TargetPlayerId = bobId }, GuildVoiceChannelUnbanPlayerRsp.Parser);
        Assert.Equal((0, bobId), (unban.ErrorCode, unban.TargetPlayerId));
        Assert.Equal(ErrorCodes.GuildVoiceNotBlocked, (await Ask(alice, GuildVoiceService.UnbanMsg,
            new GuildVoiceChannelUnbanPlayerReq { ChannelIndex = 0, TargetPlayerId = bobId }, GuildVoiceChannelUnbanPlayerRsp.Parser)).ErrorCode);
        Assert.Equal(0, (await Join(bob, 0)).ErrorCode);
    }

    [ClientDataFact]
    public async Task LeavingTheGuildLeavesItsVoiceChannel()
    {
        var (alice, _, bob, bobId, _) = await GuildOfTwo();
        await using var a = alice;
        await using var b = bob;
        await Join(alice, 0);
        await Join(bob, 0);
        await Update(alice, i => i.Members.Count == 2);

        await bob.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        var left = await Update(alice, i => i.Members.Count == 1);
        Assert.DoesNotContain(left.ChannelInfo.Members, m => m.PlayerId == bobId);
        Assert.Equal(ErrorCodes.GuildVoiceNotInGuild, (await Join(bob, 0)).ErrorCode);
    }

    [ClientDataFact]
    public async Task TheBotJoinsTheGuildSitsInAChannelAndAsksForTheMicOncePerRound()
    {
        var (alice, _) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        await Gm("Alice", "!lvl 34");
        await alice.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Voices", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(alice, GuildService.Create, GuildCreateResp.Parser)).Result);
        long guild = (await Next(alice, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
        await alice.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(alice, GuildService.Edit, GuildEditResp.Parser)).Result);

        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await BotGuildVoice.JoinGuildAsync(bob, _http, "bob", guild, log.Enqueue, stop.Token);
        Assert.Contains(log, l => l == $"in guild {guild}");
        Assert.Equal(0, (await Join(alice, 0)).ErrorCode);
        var sitting = BotGuildVoice.SitAsync(bob, bobId, 0, askMic: true, log.Enqueue, stop.Token);
        await Update(alice, i => i.Members.Count == 2);

        await Ask(alice, GuildVoiceService.SetSpeakModeMsg,
            new GuildVoiceChannelSetSpeakModeReq { ChannelIndex = 0, SpeakMode = GuildSpeakAuthorityType.Order }, GuildVoiceChannelSetSpeakModeRsp.Parser);
        var asked = await Update(alice, i => i.ApplyList.Count == 1);
        Assert.Equal(bobId, asked.ChannelInfo.ApplyList[0].PlayerId);
        await Ask(alice, GuildVoiceService.HandleApplyMsg,
            new GuildVoiceChannelHandleApplyReq { ChannelIndex = 0, TargetPlayerId = bobId, IsAgree = false }, GuildVoiceChannelHandleApplyRsp.Parser);
        await Update(alice, i => i.ApplyList.Count == 0);
        // A refusal is final for the round: no second request turns up.
        await Assert.ThrowsAsync<TimeoutException>(() => Update(alice, i => i.ApplyList.Count == 1).WaitAsync(TimeSpan.FromSeconds(1)));

        await Ask(alice, GuildVoiceService.KickMsg, new GuildVoiceChannelKickPlayerReq { ChannelIndex = 0, TargetPlayerId = bobId }, GuildVoiceChannelKickPlayerRsp.Parser);
        Assert.Equal(0, await sitting.WaitAsync(Wait));
        Assert.Contains(log, l => l.Contains("kicked from voice channel 0"));
        Assert.Single(log, l => l.Contains("asked for the mic"));
    }

    [ClientDataFact]
    public async Task LoggingOutLeavesTheVoiceChannel()
    {
        var (alice, _, bob, _, _) = await GuildOfTwo();
        await using var a = alice;
        await Join(alice, 0);
        await Join(bob, 0);
        await Update(alice, i => i.Members.Count == 2);

        await bob.DisposeAsync();
        await Update(alice, i => i.Members.Count == 1);
        var counts = await Ask(alice, GuildVoiceService.AllMemberCountMsg, new GuildVoiceChannelGetAllMemberCountReq(), GuildVoiceChannelGetAllMemberCountRsp.Parser);
        Assert.Equal(1, counts.ChannelCounts.Single(c => c.ChannelIndex == 0).MemberCount);
    }
}
