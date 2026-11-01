using Romsg;
using Tencent.IM;

namespace Ro3Emu.Server.Game;

public sealed partial class ChatService
{
    /// <summary>ChatConfig 7501 (_iType 10 CommonMessage, a tip): "You have been muted until ${1}."
    /// (10320000539), the client's own mute notice; ${1} a CHAT_PARAM_TIMESTAMP that LC_SystemMessageDecodeFunc
    /// .DecodeTimeStamp shows as a local date to the minute.</summary>
    internal const int MutedChat = 7501;

    /// <summary>ChatChannelType 2 Guild, 3 Team and 5 SecretChat (friend groups): the looser budget, with whispers.</summary>
    private const int GuildChannelType = 2, TeamChannelType = 3, FriendGroupChannelType = 5;

    /// <summary>
    /// Ro3Emu:ChatFlood (<see cref="ChatFloodGuard"/>): a refused line is answered chat_result false (the client keeps
    /// it out of its recent lines) and shown to nobody; a mute, when it starts and on each line sent during it, also
    /// brings the client's mute tip with the time it ends. GM accounts are exempt.
    /// </summary>
    private async Task<bool> FloodRefusedAsync(GameSession sender, SendTextChatMessageRequest req)
    {
        if (!_options.Value.ChatFlood.Enabled || _gm.Allowed(sender)) return false;
        bool open = req.ChatType != ChatTypePrivate && ChannelType(req.ReceiveId) is not (GuildChannelType or TeamChannelType or FriendGroupChannelType);
        var (verdict, until) = _flood.Check(sender.Account!, req.ReceiveId, open, req.Content);
        if (verdict == ChatFloodGuard.Verdict.Pass) return false;
        await DownAsync(sender, Cmd.SendText, new SendTextChatMessageResponse { ChatResult = false });
        if (verdict == ChatFloodGuard.Verdict.Duplicate) return true;
        if (verdict == ChatFloodGuard.Verdict.MuteStarted)
            LogMuted(_log, sender.Role!.Name, sender.Account!, until);
        string end = until.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var notice = new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = MutedChat };
        notice.ParamList[1] = new ChatParameter { Params = { end }, Type = ChatParamType.ChatParamTimestamp };
        await sender.SendGameAsync(SystemBroadcast, notice);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Chat flood: {Name} ({Account}) muted until {Until:u}")]
    private static partial void LogMuted(ILogger logger, string name, string account, DateTimeOffset until);

    /// <summary>The ChatChannelType a group id starts with ("2_7", "@TGS#_1_1"); 0 when it has none.</summary>
    internal static int ChannelType(string group)
    {
        var id = group.AsSpan();
        if (id.StartsWith("@TGS#_", StringComparison.Ordinal)) id = id[6..];
        int end = id.IndexOf('_');
        return end > 0 && int.TryParse(id[..end], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int type) ? type : 0;
    }
}
