using System.Globalization;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class ChatService
{
    internal const int SoundTransmissionAction = 105; // ItemUseEffectType_Soundtransmission
    private const uint SystemBroadcast = 1022;

    /// <summary>
    /// 105 Soundtransmission [105, ChatConfig id]: the horns 1003100 Small Horn [105,1], 1003101 [105,2], 1003102 [105,3].
    /// "Use" only opens UI_Sys_Chat_SoundTransmissionPanel (the item's _iUseBeforeEffect); its send button
    /// (UI_Sys_Chat_SoundTransmissionWidgetCtrl.lua:581-650) uses one horn with one ItemUseParamInfo chatCustomizeData
    /// {soundTransmission {playerName "id:name", words}}, the words at most PublicParamConfig.TrumpetWordLimit (50)
    /// characters and not empty. Everyone online gets the marquee as 1022 SystemBoardCastMsgNtf {chatId = the horn's
    /// ChatConfig row (1/2/3: the same text "^{1}^{2}${1}:^{3}${2}" in three marquee prefabs), paramList 1 the sender
    /// as "id:name" (_kChatParamTypes 13 TRASSION_SENDER_NAME, a player link), 2 the words (1 STRING)}, which
    /// LC_DAL_SystemMessage.CheckMessage pushes to LC_SystemMessageHelper.MessagePush. The sender's name comes from the
    /// session, not from the client's playerName. Words with a blocked word are TextSensitive and keep the horn.
    /// (inferred) source MsgSource_player with the sender's playerId.
    /// </summary>
    private async Task<int> UseHornAsync(ItemActionUse use)
    {
        var s = use.Session;
        int chatId = (int)use.Arg(1);
        if (use.Count != 1 || use.Request.ItemParam.Count != 1
            || use.Request.ItemParam[0].VarCase != ItemUseParamInfo.VarOneofCase.ChatCustomizeData
            || use.Request.ItemParam[0].ChatCustomizeData.SoundTransmission is not { } horn) return ErrorCodes.Parameter;
        if (_data.Row("ChatConfig", chatId) == null) return ErrorCodes.ItemNoConfig;
        string words = horn.Words ?? "";
        long limit = _data.Table("PublicParamConfig").TryGetValue("TrumpetWordLimit", out var v) && GameData.Number(v) is { } n ? (long)n : 50;
        int length = new StringInfo(words).LengthInTextElements;
        if (words.Trim().Length == 0 || length > limit) return ErrorCodes.Parameter;
        if (_options.Value.GuildBlockedWords.Any(word => !string.IsNullOrWhiteSpace(word)
            && words.Contains(word, StringComparison.OrdinalIgnoreCase))) return ErrorCodes.TextSensitive;
        if (!await _character.CommitItemActionAsync(use, null, null, new { ChatId = chatId, Words = words }))
            return ErrorCodes.ItemsShort;
        var ntf = new SystemBoardCastMsgNtf { Source = MsgSource.Player, ChatId = chatId, PlayerId = s.Role!.Id };
        ntf.ParamList[1] = new ChatParameter { Params = { $"{s.Role.Id}:{s.Role.Name}" }, Type = ChatParamType.ChatParamString };
        ntf.ParamList[2] = new ChatParameter { Params = { words }, Type = ChatParamType.ChatParamString };
        await RoleFanout.EachAsync(_sessions.All().Where(p => p.Role != null), p => p.SendGameAsync(SystemBroadcast, ntf));
        _log.LogInformation("{Name} horn {Item}: {Words}", s.Role.Name, use.Item.Entry, words);
        return 0;
    }
}
