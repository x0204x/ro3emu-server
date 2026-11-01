using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Bot;

/// <summary>
/// A bot member for manual Tavern tests (TavernService): it opens or enters a room, and with <c>askMic</c> asks for a
/// seat whenever the room is in Manual or Order mode without one, so one player can try the owner's side (speaking
/// mode, requests, mute, remove, hand-over, gifts) alone. The relay echo runs on the room's guid (BotVoiceEcho).
/// </summary>
public static class BotTavern
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
    private const string LevelCommand = "!lvl 36"; // FunctionConfig 2410 "Tavern" opens at level 36

    /// <summary>Opens a room (topic 1 Chitchat, language 21 Unlimited) and returns its guid.</summary>
    public static async Task<long> CreateAsync(BotClient bot, string httpBase, string account, string name, Action<string> log, CancellationToken ct)
    {
        var request = new CreateBarRoomReq { BarRoomName = name, BarRoomType = 1, LanguageType = 21 };
        var reply = await AskAsync(bot, TavernService.CreateMsg, request, CreateBarRoomResp.Parser, ct);
        if (reply.Result == ErrorCodes.FeatureUnavailable)
        {
            await LevelUpAsync(httpBase, account, log, ct);
            reply = await AskAsync(bot, TavernService.CreateMsg, request, CreateBarRoomResp.Parser, ct);
        }
        if (reply.Result != 0) throw new InvalidOperationException($"opening a Tavern room failed (result {reply.Result})");
        long room = reply.Info.BarRoomSummary.BarRoomGuid;
        log($"opened Tavern room {room} \"{name}\"");
        return room;
    }

    /// <summary>Enters room <paramref name="room"/>; a bot below the Tavern's level is raised to 36 through the local
    /// server's GM endpoint first, which only answers on a server playing on this PC.</summary>
    public static async Task EnterAsync(BotClient bot, string httpBase, string account, long room, Action<string> log, CancellationToken ct)
    {
        var request = new EnterBarRoomReq { BarRoomGuid = room };
        int result = (await AskAsync(bot, TavernService.EnterMsg, request, EnterBarRoomResp.Parser, ct)).Result;
        if (result == ErrorCodes.FeatureUnavailable)
        {
            await LevelUpAsync(httpBase, account, log, ct);
            result = (await AskAsync(bot, TavernService.EnterMsg, request, EnterBarRoomResp.Parser, ct)).Result;
        }
        if (result == ErrorCodes.TavernAlreadyIn) { log($"already in Tavern room {room}"); return; }
        if (result != 0) throw new InvalidOperationException($"entering Tavern room {room} failed (result {result})");
        log($"in Tavern room {room}");
    }

    /// <summary>
    /// Stays in the room until removed from it or <paramref name="ct"/>: logs the owner, the mode and the bot's seat on
    /// every change, and with <paramref name="askMic"/> asks once for a seat each time the room needs one.
    /// </summary>
    public static async Task StayAsync(BotClient bot, long roleId, long room, bool askMic, Action<string> log, CancellationToken ct)
    {
        string last = "";
        bool asked = false;
        while (!ct.IsCancellationRequested)
        {
            var detail = await AskAsync(bot, TavernService.GetMsg, new BarRoomGetReq(), BarRoomGetResp.Parser, ct);
            if (detail.Result != 0) { log($"no longer in Tavern room {room} (result {detail.Result})"); return; }
            var info = detail.Info;
            bool seated = info.CurSpeaks.Any(s => s.UserId == roleId);
            string state = $"owner {info.OwnerGuid}, {info.ControlMicType} mode, {info.BarRoomSummary.MemberList.Count} member(s), "
                + $"heat {info.BarRoomSummary.HotValue}{(seated ? ", seated" : "")}{(info.ForbiddenSpeaker.ContainsKey(roleId) ? ", muted" : "")}"
                + (info.IsCloseMic ? ", mics closed" : "");
            if (state != last) log(last = state);
            if (info.ControlMicType == SpeakAuthorityType.Free || seated) asked = false;
            else if (askMic && !asked && info.OwnerGuid != roleId)
            {
                asked = true;
                var apply = await AskAsync(bot, TavernService.ApplyMicMsg, new ApplyOpenMicReq { BarRoomGuid = room, Reason = "bot" }, ApplyOpenMicResp.Parser, ct);
                log(apply.Result == 0 ? "asked for a seat" : $"seat request refused ({apply.Result})");
            }
            using var change = bot.Inbox.Expect(e => e.IsGame(TavernService.InfoChangeNotify) || e.IsGame(TavernService.UserEnterNotify));
            try { await change.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            catch (TimeoutException) { }
        }
    }

    private static async Task LevelUpAsync(string httpBase, string account, Action<string> log, CancellationToken ct)
    {
        log("the Tavern is not open yet: raising to 36 with the GM command");
        using var http = new HttpClient { BaseAddress = new Uri(httpBase), Timeout = ReplyTimeout };
        (await http.PostAsync($"/ro3emu/gm?account={Uri.EscapeDataString(account)}", new StringContent(LevelCommand), ct)).EnsureSuccessStatusCode();
    }

    private static async Task<T> AskAsync<T>(BotClient bot, uint id, Google.Protobuf.IMessage request, Google.Protobuf.MessageParser<T> parser, CancellationToken ct)
        where T : Google.Protobuf.IMessage<T>
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(id));
        await bot.SendGameAsync(id, request);
        return parser.ParseFrom((await reply.WaitAsync(ReplyTimeout, ct)).Body);
    }
}
