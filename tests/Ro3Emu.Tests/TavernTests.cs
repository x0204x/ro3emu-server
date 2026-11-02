using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Voice;

namespace Ro3Emu.Tests;

/// <summary>
/// The Tavern (MG_Barroom 82481-82514, LC_BarRoomServer / LC_DAL_BarRoom / LC_BarRoomHelper): rooms and the
/// PartyService room list, the mic seats and the owner's tools, gifts with heat and Star Power, the daily quest and
/// its achievement, the room's chat channel 10 and its voice room on the relay.
/// </summary>
public sealed class TavernTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-tavern-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication _app = null!;
    private string _http = "";
    private int _voicePort;

    // BarroomTypeConfig: topic 1 Chitchat, 2 Tips; language 24 English, 25 Korean.
    private const int Chitchat = 1, Tips = 2, English = 24, Korean = 25;
    private const int HotCocoa = 1200801, Gingerbread = 1200802, GrapeWine = 1200804; // 110/111 = 1/1, 10/5, 400/300

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _voicePort = TestPorts.FreePort();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.Voice.Port = _voicePort;
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

    private async Task<(BotClient Bot, long Id)> InWorld(string name, bool tavern = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        if (tavern) await Gm(name, "!lvl 36"); // FunctionConfig 2410 opens at level 36
        return (bot, id);
    }

    private async Task Gm(string name, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={name.ToLowerInvariant()}", Operator.Command(command))).EnsureSuccessStatusCode();
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

    private static Task<CreateBarRoomResp> Create(BotClient bot, string name, int type = Chitchat, int language = English) =>
        Ask(bot, TavernService.CreateMsg, new CreateBarRoomReq { BarRoomName = name, BarRoomType = type, LanguageType = language }, CreateBarRoomResp.Parser);

    private static Task<EnterBarRoomResp> Enter(BotClient bot, long room) =>
        Ask(bot, TavernService.EnterMsg, new EnterBarRoomReq { BarRoomGuid = room }, EnterBarRoomResp.Parser);

    private static Task<BarRoomGetResp> Get(BotClient bot) => Ask(bot, TavernService.GetMsg, new BarRoomGetReq(), BarRoomGetResp.Parser);

    private static Task<BarRoomInfoUpdateNotify> Changed(BotClient bot, long room) =>
        Next(bot, TavernService.InfoChangeNotify, BarRoomInfoUpdateNotify.Parser, n => n.BarRoomGuid == room);

    /// <summary>The room list as LC_BarRoomServer.PartyQuerySummaryRequest asks for it (Discover adds the two conditions).</summary>
    private static async Task<List<BarRoomSummary>> List(BotClient bot, bool discover = false)
    {
        object?[] conditions = discover
            ?
            [
                new Dictionary<string, object?> { ["Key"] = "BaroomCreateTime", ["MinValue"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1800, ["MaxValue"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2592000 },
                new Dictionary<string, object?> { ["Key"] = "BaroomHeadCount", ["MinValue"] = 2, ["MaxValue"] = 30 },
            ]
            : [];
        var reply = await bot.MicroPositionalAsync("PartyService", "getPartysSummary", [TavernService.PartyType, Array.Empty<object>(), conditions, Array.Empty<object>(), 0, 100, true]);
        Assert.Equal(true, ((object?[])reply["Ret"]!)[0]);
        return [.. ((object?[])reply["SummaryList"]!).Select(e => BarRoomSummary.Parser.ParseFrom((byte[])((Dictionary<object, object?>)e!)["GameSummaryData"]!))];
    }

    private GameSession Session(long role) => _app.Services.GetRequiredService<SessionRegistry>().All().First(s => s.Role?.Id == role);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("timed out waiting for " + what);
            await Task.Delay(50);
        }
    }

    [ClientDataFact]
    public async Task ARoomIsCreatedListedJoinedAndClosedWhenEmpty()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;

        var created = await Create(alice, "Cheers");
        Assert.Equal(0, created.Result);
        long room = created.Info.BarRoomSummary.BarRoomGuid;
        Assert.True(room >= VoiceRoomKey.TavernBase);
        Assert.Equal(aliceId, created.Info.OwnerGuid);
        Assert.Equal([aliceId], created.Info.BarRoomSummary.MemberList.Select(m => m.MemberId));
        Assert.Equal(SpeakAuthorityType.Free, created.Info.ControlMicType);
        await Next(alice, TavernService.Sync, DBBarRoomInfoIncrement.Parser, i => i.BarRoomId == room);

        var listed = Assert.Single(await List(bob));
        Assert.Equal((room, "Cheers", Chitchat, English), (listed.BarRoomGuid, listed.BarRoomName, listed.BarRoomType, listed.LanguageType));
        Assert.Empty(await List(bob, discover: true)); // Discover wants 2..30 members

        var entered = await Enter(bob, room);
        Assert.Equal(0, entered.Result);
        Assert.Equal(2, entered.Info.BarRoomSummary.MemberList.Count);
        Assert.Equal(bobId, (await Next(alice, TavernService.UserEnterNotify, BarRoomUserEnterNotify.Parser)).UserId);
        Assert.Equal(ErrorCodes.TavernAlreadyIn, (await Enter(bob, room)).Result);
        Assert.Equal(room, Assert.Single(await List(alice, discover: true)).BarRoomGuid);

        // The owner leaves: Bob, the one left, owns the room; when he leaves too it closes.
        Assert.Equal(0, (await Ask(alice, TavernService.ExitMsg, new ExitBarRoomReq { BarRoomGuid = room }, ExitBarRoomResp.Parser)).Result);
        await Changed(bob, room);
        Assert.Equal(bobId, (await Get(bob)).Info.OwnerGuid);
        Assert.Equal(ErrorCodes.TavernNotInRoom, (await Get(alice)).Result);
        Assert.Equal(0, (await Ask(bob, TavernService.ExitMsg, new ExitBarRoomReq { BarRoomGuid = room }, ExitBarRoomResp.Parser)).Result);
        Assert.Empty(await List(alice));
        Assert.Equal(ErrorCodes.TavernNotFound, (await Enter(alice, room)).Result);
    }

    [ClientDataFact]
    public async Task CreationFollowsTheFunctionAndTheTables()
    {
        var (novice, _) = await InWorld("Novice", tavern: false);
        var (alice, _) = await InWorld("Alice");
        await using var n = novice;
        await using var a = alice;

        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Create(novice, "Early")).Result);
        Assert.Equal(ErrorCodes.TavernNameInvalid, (await Create(alice, "   ")).Result);
        Assert.Equal(ErrorCodes.TavernNameTooLong, (await Create(alice, new string('x', 19))).Result); // BarRoomNameLen 18
        Assert.Equal(ErrorCodes.TavernBadType, (await Create(alice, "Room", type: English)).Result);
        Assert.Equal(ErrorCodes.TavernBadLanguage, (await Create(alice, "Room", language: Tips)).Result);
        long room = (await Create(alice, "Room")).Info.BarRoomSummary.BarRoomGuid;

        // The owner edits it, then has to wait BarRoomSetInfoCd (5 s).
        var edit = new SetBarRoomInfoReq { BarRoomGuid = room, BarRoomName = "Renamed", BarRoomType = Tips, LanguageType = Korean };
        Assert.Equal(0, (await Ask(alice, TavernService.SetInfoMsg, edit, SetBarRoomInfoResp.Parser)).Result);
        Assert.Equal(ErrorCodes.TavernTooQuick, (await Ask(alice, TavernService.SetInfoMsg, edit, SetBarRoomInfoResp.Parser)).Result);
        var summary = (await Get(alice)).Info.BarRoomSummary;
        Assert.Equal(("Renamed", Tips, Korean), (summary.BarRoomName, summary.BarRoomType, summary.LanguageType));
    }

    [ClientDataFact]
    public async Task SeatsAreAskedForGrantedAndGivenUp()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long room = (await Create(alice, "Stage")).Info.BarRoomSummary.BarRoomGuid;
        await Enter(bob, room);

        var apply = new ApplyOpenMicReq { BarRoomGuid = room, Reason = "a song" };
        Assert.Equal(ErrorCodes.TavernNoApplyNeeded, (await Ask(bob, TavernService.ApplyMicMsg, apply, ApplyOpenMicResp.Parser)).Result); // Free mode

        var manual = new ModifyControlMicTypeReq { BarRoomGuid = room, ControlMicType = SpeakAuthorityType.Manual, MicOrder = new SetMicOrderReq { BarRoomGuid = room } };
        Assert.Equal(ErrorCodes.TavernNoPermission, (await Ask(bob, TavernService.MicTypeMsg, manual, ModifyControlMicTypeResp.Parser)).Result);
        Assert.Equal(0, (await Ask(alice, TavernService.MicTypeMsg, manual, ModifyControlMicTypeResp.Parser)).Result);
        await Changed(bob, room);

        Assert.Equal(0, (await Ask(bob, TavernService.ApplyMicMsg, apply, ApplyOpenMicResp.Parser)).Result);
        Assert.Equal(ErrorCodes.TavernAlreadyApplied, (await Ask(bob, TavernService.ApplyMicMsg, apply, ApplyOpenMicResp.Parser)).Result);
        await Changed(alice, room);
        Assert.Equal("a song", (await Get(alice)).Info.ApplyUsers[bobId]);

        using (var told = bob.Inbox.Expect(e => e.IsGame(TavernService.HandleApplyMsg)))
        {
            var agree = new OwnerHandleOpenMicReq { BarRoomGuid = room, ApplyerId = bobId, IsAgree = true };
            Assert.Equal(0, (await Ask(alice, TavernService.HandleApplyMsg, agree, OwnerHandleOpenMicResp.Parser)).Result);
            Assert.True(OwnerHandleOpenMicResp.Parser.ParseFrom((await told.WaitAsync(Wait)).Body).IsAgree);
        }
        var detail = (await Get(alice)).Info;
        Assert.Empty(detail.ApplyUsers);
        Assert.Equal((bobId, 1), (detail.CurSpeaks.Single().UserId, detail.CurSpeaks.Single().MicOrder));
        Assert.Equal(ErrorCodes.TavernAlreadyOnMic, (await Ask(bob, TavernService.ApplyMicMsg, apply, ApplyOpenMicResp.Parser)).Result);

        Assert.Equal(ErrorCodes.TavernOwnerKeepsMic, (await Ask(alice, TavernService.DownMicMsg, new DownMicReq { BarRoomGuid = room }, DownMicResp.Parser)).Result);
        Assert.Equal(0, (await Ask(bob, TavernService.DownMicMsg, new DownMicReq { BarRoomGuid = room }, DownMicResp.Parser)).Result);
        Assert.Equal(ErrorCodes.TavernNotOnMic, (await Ask(bob, TavernService.DownMicMsg, new DownMicReq { BarRoomGuid = room }, DownMicResp.Parser)).Result);

        // The owner's own list: more than BarroomSpeakNum (10) is refused, the owner needs no seat.
        var order = new ModifyControlMicTypeReq { BarRoomGuid = room, ControlMicType = SpeakAuthorityType.Order, MicOrder = new SetMicOrderReq { BarRoomGuid = room } };
        order.MicOrder.SpeakUsers.Add(new CurSpeakUsers { UserId = aliceId, MicOrder = 1 });
        order.MicOrder.SpeakUsers.Add(new CurSpeakUsers { UserId = bobId, MicOrder = 2 });
        var kept = await Ask(alice, TavernService.MicTypeMsg, order, ModifyControlMicTypeResp.Parser);
        Assert.Equal([bobId], kept.MicOrder.SpeakUsers.Select(u => u.UserId));
        Assert.Equal(SpeakAuthorityType.Order, (await Get(bob)).Info.ControlMicType);
    }

    [ClientDataFact]
    public async Task TheOwnerMutesRemovesAndHandsOverTheRoom()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long room = (await Create(alice, "Rules")).Info.BarRoomSummary.BarRoomGuid;
        await Enter(bob, room);

        // Mute: Bob's client gets the reply too ("The host has muted you."); a muted player cannot take the room.
        using (var told = bob.Inbox.Expect(e => e.IsGame(TavernService.ForbidMsg)))
        {
            Assert.Equal(0, (await Ask(alice, TavernService.ForbidMsg, new ForbiddenUserReq { BarRoomGuid = room, TargetGuid = bobId }, ForbiddenUserResp.Parser)).Result);
            await told.WaitAsync(Wait);
        }
        Assert.True((await Get(alice)).Info.ForbiddenSpeaker[bobId] > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86_000);
        Assert.Equal(ErrorCodes.TavernMuted, (await Ask(alice, TavernService.TransferMsg, new TransferOwnerReq { BarRoomGuid = room, TargetGuid = bobId }, TransferOwnerResp.Parser)).Result);
        Assert.Equal(0, (await Ask(alice, TavernService.UnforbidMsg, new CanecelForbiddenUserReq { BarRoomGuid = room, TargetGuid = bobId }, CanecelForbiddenUserResp.Parser)).Result);
        Assert.Equal(ErrorCodes.TavernNotMuted, (await Ask(alice, TavernService.UnforbidMsg, new CanecelForbiddenUserReq { BarRoomGuid = room, TargetGuid = bobId }, CanecelForbiddenUserResp.Parser)).Result);

        // 82514 reaches the member as 77003 on the room's voice id.
        using (var muted = bob.Inbox.Expect(e => e.IsGame(TavernService.MuteInChannelNotify)))
        {
            var mute = await Ask(alice, TavernService.MuteMsg, new MuteBarRoomPlayerReq { BarRoomUid = room, TargetPlayerUid = bobId, IsMute = true }, MuteBarRoomPlayerResp.Parser);
            Assert.Equal((0, room.ToString(System.Globalization.CultureInfo.InvariantCulture)), (mute.Result, mute.GroupId));
            var notify = MutePlayerInChannelNotify.Parser.ParseFrom((await muted.WaitAsync(Wait)).Body);
            Assert.True(notify.IsMute);
        }
        Assert.Equal(ErrorCodes.TavernNoPermission, (await Ask(bob, TavernService.MuteMsg, new MuteBarRoomPlayerReq { BarRoomUid = room, TargetPlayerUid = aliceId, IsMute = true }, MuteBarRoomPlayerResp.Parser)).Result);

        // Close all mics, then open them again.
        Assert.Equal(0, (await Ask(alice, TavernService.ShutUpMsg, new ShutUpAllReq { BarRoomGuid = room }, ShutUpAllResp.Parser)).Result);
        Assert.True((await Get(bob)).Info.IsCloseMic);
        Assert.Equal(0, (await Ask(alice, TavernService.UnshutUpMsg, new CancelShutUpAllReq { BarRoomGuid = room }, CancelShutUpAllResp.Parser)).Result);
        Assert.False((await Get(bob)).Info.IsCloseMic);

        // Hand-over, then BarRoomTransferRoomCd (30 s) holds the next one.
        Assert.Equal(0, (await Ask(alice, TavernService.TransferMsg, new TransferOwnerReq { BarRoomGuid = room, TargetGuid = bobId }, TransferOwnerResp.Parser)).Result);
        Assert.Equal(bobId, (await Get(alice)).Info.OwnerGuid);
        Assert.Equal(ErrorCodes.TavernTransferCooldown, (await Ask(bob, TavernService.TransferMsg, new TransferOwnerReq { BarRoomGuid = room, TargetGuid = aliceId }, TransferOwnerResp.Parser)).Result);

        // Removed: out of the room, on its Blocks list until lifted.
        using (var left = alice.Inbox.Expect(e => e.IsGame(TavernService.Sync) && DBBarRoomInfoIncrement.Parser.ParseFrom(e.Body) is { HasBarRoomId: true, BarRoomId: 0 }))
        {
            Assert.Equal(0, (await Ask(bob, TavernService.KickMsg, new KickUserReq { BarRoomGuid = room, TargetGuid = aliceId }, KickUserResp.Parser)).Result);
            await left.WaitAsync(Wait);
        }
        Assert.Equal(ErrorCodes.TavernBlocked, (await Enter(alice, room)).Result);
        Assert.Contains(aliceId, (await Get(bob)).Info.BlackList.Keys);
        Assert.Equal(0, (await Ask(bob, TavernService.UnkickMsg, new CancelKickUserReq { BarRoomGuid = room, TargetGuid = aliceId }, CancelKickUserResp.Parser)).Result);
        Assert.Equal(ErrorCodes.TavernNotBlocked, (await Ask(bob, TavernService.UnkickMsg, new CancelKickUserReq { BarRoomGuid = room, TargetGuid = aliceId }, CancelKickUserResp.Parser)).Result);
        Assert.Equal(0, (await Enter(alice, room)).Result);
    }

    [ClientDataFact]
    public async Task InvitationsBringFriendsIn()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long room = (await Create(alice, "Party")).Info.BarRoomSummary.BarRoomGuid;

        var invite = await Ask(alice, TavernService.InviteMsg, new InviteEnterBarRoomReq { BarRoomGuid = room, TargetGuidList = { bobId }, RoomName = "?" }, InviteEnterBarRoomResp.Parser);
        Assert.Equal((0, bobId), (invite.Result, invite.TargetGuidList.Single()));
        var notify = await Next(bob, TavernService.InviteNotify, InviteEnterBarRoomNotify.Parser);
        Assert.Equal((room, aliceId, "Party", "Alice", 36), (notify.BarRoomGuid, notify.InviterGuid, notify.RoomName, notify.InviterName, notify.InviterLevel));

        using var answer = alice.Inbox.Expect(e => e.IsGame(TavernService.InviteResponseMsg));
        var accepted = await Ask(bob, TavernService.InviteResponseMsg,
            new ResponseInviteEnterBarRoomReq { BarRoomGuid = room, InviterGuid = aliceId, IsAccept = true }, ResponseInviteEnterBarRoomResp.Parser);
        Assert.Equal((0, bobId, true), (accepted.Result, accepted.InviterGuid, accepted.IsAccept)); // the invitee's own copy opens the window
        Assert.Equal(2, accepted.Info.BarRoomSummary.MemberList.Count);
        var toInviter = ResponseInviteEnterBarRoomResp.Parser.ParseFrom((await answer.WaitAsync(Wait)).Body);
        Assert.Equal((bobId, true), (toInviter.InviterGuid, toInviter.IsAccept)); // "Bob has accepted your invitation."
    }

    [ClientDataFact]
    public async Task GiftsWarmTheRoomAndGiveStarPower()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long room = (await Create(alice, "Bar")).Info.BarRoomSummary.BarRoomGuid;
        await Enter(bob, room);
        await Gm("Alice", $"!item {GrapeWine} 4");
        await Gm("Alice", $"!item {HotCocoa} 1");

        var gift = new PresentGiftReq { BarRoomGuid = room, TargetGuid = bobId, GiftId = GrapeWine, GiftNum = 4 };
        Assert.Equal(ErrorCodes.TavernGiftSelf, (await Ask(alice, TavernService.GiftMsg, new PresentGiftReq(gift) { TargetGuid = aliceId }, PresentGiftResp.Parser)).Result);
        Assert.Equal(ErrorCodes.ItemsShort, (await Ask(alice, TavernService.GiftMsg, new PresentGiftReq(gift) { GiftNum = 5 }, PresentGiftResp.Parser)).Result);
        var given = await Ask(alice, TavernService.GiftMsg, gift, PresentGiftResp.Parser);
        Assert.Equal((0, GrapeWine, 4, bobId), (given.Result, given.ItemId, given.ItemNum, given.ReceiveUser));
        Assert.Equal((1200, 1600), (given.UserBarRoomHot, given.HotAdd)); // 4 x (111 GainStar 300, 110 GainBarroomHot 400)
        await Next(bob, TavernService.Sync, DBBarRoomInfoIncrement.Parser, i => i.Popularity == 1200 && i.WeekPopularity == 1200);
        Assert.Equal(1600, (await Get(bob)).Info.BarRoomSummary.HotValue);
        Assert.Equal(0, _app.Services.GetRequiredService<CharacterService>().CountOf(Session(aliceId), GrapeWine));

        // 102100600 "A Drink for a Story": 1000 Star Power (106008).
        await Eventually(() => Session(bobId).Achievements?.Infos.GetValueOrDefault(102100600)?.Status == AchievementStatusType.Complete, "achievement 102100600");

        Assert.Equal(ErrorCodes.Parameter, (await Ask(alice, TavernService.GiftMsg, new PresentGiftReq(gift) { GiftId = 1, GiftNum = 1 }, PresentGiftResp.Parser)).Result);
        Assert.Equal(0, (await Ask(bob, TavernService.ExitMsg, new ExitBarRoomReq { BarRoomGuid = room }, ExitBarRoomResp.Parser)).Result);
        Assert.Equal(ErrorCodes.TavernRecipientLeft, (await Ask(alice, TavernService.GiftMsg, new PresentGiftReq(gift) { GiftId = HotCocoa, GiftNum = 1 }, PresentGiftResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task TwentyMinutesInARoomCompleteTheDailyQuest()
    {
        var (alice, aliceId) = await InWorld("Alice");
        await using var a = alice;
        await Create(alice, "Quest");
        var session = Session(aliceId);
        await Eventually(() => session.Tasks?.Infos.GetValueOrDefault(2000)?.Status == TaskState.Running, "the daily Tavern quest to be offered");

        // GM op 141 GMAddBarroomToDayStayTimes: the day's 1200 s, as twenty minutes in the room would add them.
        await alice.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)TavernService.GmAddStay, ParamString = "1200" });
        await Next(alice, TavernService.Sync, DBBarRoomInfoIncrement.Parser, i => i.IsToDayActComplete && i.ActDays == 1);
        await Eventually(() => TaskService.IsDelivered(session, 2000), "the daily Tavern quest to deliver itself");
        var character = _app.Services.GetRequiredService<CharacterService>();
        Assert.Equal(3, character.CountOf(session, HotCocoa)); // award 8000 -> 1008000
        // (inferred) The first active day's drink (ServerOptions.TavernDayDrinks).
        await Eventually(() => character.CountOf(session, Gingerbread) == 1 && session.Tavern?.DrinkOwed == 0, "the active day's Gingerbread");
        await Eventually(() => session.Achievements?.Infos.GetValueOrDefault(102100500)?.Status == AchievementStatusType.Complete, "achievement 102100500");
    }

    [ClientDataFact]
    public async Task OnlyMembersUseTheRoomsChannel()
    {
        var (alice, _) = await InWorld("Alice");
        var (bob, _) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long room = (await Create(alice, "Talk")).Info.BarRoomSummary.BarRoomGuid;
        string channel = ChatService.TavernPrefix + room;

        await alice.JoinChatAsync(channel);
        await Assert.ThrowsAnyAsync<Exception>(() => bob.JoinChatAsync(channel));
        await Enter(bob, room);
        await bob.JoinChatAsync(channel);
        await bob.SendChatAsync(channel, 2, "hello", channelIndex: 10);
        Assert.Equal("hello", (await alice.NextChatLineAsync()).Content);
    }

    /// <summary>A ticket from the game gate, then Auth on the relay.</summary>
    private async Task<VoiceTestClient> Relay(BotClient bot)
    {
        var ticket = await BotVoiceTicket.RequestAsync(bot, Wait);
        var client = new VoiceTestClient(_voicePort) { Tag = ticket.RoleId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
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
    public async Task TheRoomsVoiceFollowsItsSpeakingRules()
    {
        var (alice, _) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        long room = (await Create(alice, "Voice")).Info.BarRoomSummary.BarRoomGuid;
        string channel = room.ToString(System.Globalization.CultureInfo.InvariantCulture); // LC_BarRoomTalk: tostring(guid)
        await using var aliceVoice = await Relay(alice);
        await using var bobVoice = await Relay(bob);

        Assert.Equal(VoiceJoinCode.NotMember, await JoinRoom(bobVoice, channel));
        await Enter(bob, room);
        Assert.Equal(VoiceJoinCode.Ok, await JoinRoom(aliceVoice, channel));
        Assert.Equal(VoiceJoinCode.Ok, await JoinRoom(bobVoice, channel));
        await bobVoice.MicAsync(true);
        await aliceVoice.WaitAsync((t, p) => MicIs(t, p, bobId, true), Wait); // Free mode: anyone

        // Manual mode without a seat: the relay holds Bob's mic off; given a seat it is heard again.
        var manual = new ModifyControlMicTypeReq { BarRoomGuid = room, ControlMicType = SpeakAuthorityType.Manual, MicOrder = new SetMicOrderReq { BarRoomGuid = room } };
        Assert.Equal(0, (await Ask(alice, TavernService.MicTypeMsg, manual, ModifyControlMicTypeResp.Parser)).Result);
        await aliceVoice.WaitAsync((t, p) => MicIs(t, p, bobId, false), Wait);
        manual.MicOrder.SpeakUsers.Add(new CurSpeakUsers { UserId = bobId, MicOrder = 1 });
        Assert.Equal(0, (await Ask(alice, TavernService.MicTypeMsg, manual, ModifyControlMicTypeResp.Parser)).Result);
        await aliceVoice.WaitAsync((t, p) => MicIs(t, p, bobId, true), Wait);
        await bobVoice.SendAudioAsync(0, [5, 5, 5]);
        var (_, audio) = await aliceVoice.WaitTypeAsync(VoiceDown.Audio, Wait);
        Assert.Equal(new byte[] { 5, 5, 5 }, audio[10..]);

        // Closing every mic holds it off again.
        Assert.Equal(0, (await Ask(alice, TavernService.ShutUpMsg, new ShutUpAllReq { BarRoomGuid = room }, ShutUpAllResp.Parser)).Result);
        await aliceVoice.WaitAsync((t, p) => MicIs(t, p, bobId, false), Wait);
    }
}
