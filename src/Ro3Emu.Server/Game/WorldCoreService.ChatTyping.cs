using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 83718 GUILD_VOICE_TYPEDOWN is, despite its name, the chat input's typing state. LC_ChatHelper.ChangeTypeDownState
/// (LC_ChatServer.SendTypeDownReq: type, channelId) sends it when a chat input gains focus (type 0 Typing) and when it
/// loses it (1 ExitTyping), from the chat window, the main screen chat and the shop chat
/// (UI_Sys_Chat_MainFunctionWidgetCtrl._onFocus and the next function, the same pair in
/// UI_Sys_Chat_FunctionContentWidgetCtrl and UI_Sys_Shop_ChatFunctionWidgetCtrl), only with chat bubbles on and the
/// bubble box of that channel ticked. channelId is the channel's place in the chat settings' forbiddenHeapPop list
/// (1-based). The reply's handler LC_DAL_Chat.OnGetTypeDownResponse is empty.
///
/// Others see it as unit custom data TextTypingState [type, channelId] (DirtyDataConfig 5000013: _iType 4, a list;
/// _iSyncScope 1, as the emoji bubble 4000003, so whoever sees the player): LC_CustomDataHandler_Chat.ChangeTypingState
/// starts or ends the typing bubble over that unit when the receiver shows bubbles of that channel, and
/// UI_Sys_SceneUIPanelCtrl._onOtherTyping skips the receiver's own unit. The audience is therefore the player's viewers,
/// not the channel's members; each client filters by its own settings. A start is kept on the entity (whoever comes into
/// view meanwhile sees the bubble); an end removes it.
/// </summary>
public sealed partial class WorldCoreService
{
    public const uint TypeDown = 83718;

    /// <summary>enum_CustomData TextTypingState (Lua_CustomDataDefine).</summary>
    internal const int TextTypingState = 5000013;

    private const int Typing = 0, ExitTyping = 1; // enum_TypeDownState (Lua_ChatDefine)

    /// <summary>(inferred) At most this many typing starts per player and window; an end always goes out, so no bubble
    /// is left up. The client sends one per focus change, so only a scripted client reaches it.</summary>
    private const int TypingStartsPerWindow = 10;
    private const long TypingWindowMs = 10_000;

    public async Task TypeDownAsync(GameSession s, GuildVoiceTypeDownMessageReq req)
    {
        bool valid = req.Type is Typing or ExitTyping && req.ChannelId >= 1 && req.ChannelId <= ChatChannelCount();
        await s.SendGameAsync(TypeDown, new GuildVoiceTypeDownMessageResp { ErrorCode = valid ? 0 : ErrorCodes.Parameter });
        if (!valid || s.Role == null) return;
        var state = s.WorldState;
        bool shown = state.CustomData.TryGetValue(TextTypingState, out var known);
        var value = new CustomData
        {
            Id = TextTypingState,
            CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.ListLong, ListLongParam = { req.Type, req.ChannelId } },
        };
        if (req.Type == Typing)
        {
            if (shown && known!.Equals(value)) return;
            long now = Now;
            if (now - state.TypingWindowStartMs >= TypingWindowMs) { state.TypingWindowStartMs = now; state.TypingInWindow = 0; }
            if (++state.TypingInWindow > TypingStartsPerWindow) return;
            state.CustomData[TextTypingState] = value.Clone();
        }
        else
        {
            if (!shown) return;
            state.CustomData.Remove(TextTypingState);
        }
        // During a scene load nobody sees the player; the next scene's viewers get the entity as it is then.
        if (!Ready(s)) return;
        var prepared = new PreparedMessage(CustomChange, new CustomDataChange { EntityId = s.Role.Id, CustomDatas = { value } });
        foreach (var viewer in _world.Viewers(s)) await viewer.SendPreparedAsync(prepared);
    }

    /// <summary>Entries of the chat settings' channel lists: one per ChatChannelConfig row.</summary>
    private int ChatChannelCount() => _data.Table("ChatChannelConfig").Count;
}
