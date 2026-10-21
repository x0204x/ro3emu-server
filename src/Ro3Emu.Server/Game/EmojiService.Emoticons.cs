using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class EmojiService
{
    public const uint EmoticonSync = 5041;
    internal const int EmoticonCollectionAction = 212;
    private const int ErrEmotesUnlocked = 2001; // ErrorCodeConfig "Emotes are already unlocked"

    /// <summary>
    /// 212 ActivatedEmojiCollection [212, enum_EmoticonsType, EmoticonTabConfig id]: 1220003 [212,0,2] Deviling and
    /// 1220004 [212,0,3] Muka unlock a chat emoticon tab (type 0 Chat). Its emoticons are the tab's _iIndex
    /// (EmoticonsConfig rows 50-67 and 100-117, all _iUnlockType 1). These are the chat panel's emoticons, not the
    /// PerformanceConfig emotes of 5006: LC_ChatHelper.CheckEmojiUnLock asks LC_ChickenHelper.CheckIsIconUnlock for an
    /// _iUnlockType 1 emoticon, which reads the ChickenEmojiInfo set built from AUTO_SYNC 5041 DBEmojiInfo.emojiInfo
    /// (LC_DAL_Chicken._allDBEmojiInfoDataProcess, _processDirtyEmojiInfo); a tab shows unlocked when one of its
    /// emoticons is. The ids join emojiInfo in the item's commit (role blob "emoji_info"); a tab whose emoticons are all
    /// there already is "Emotes are already unlocked" (2001) and keeps the item.
    /// </summary>
    private async Task<int> UseEmoticonItemAsync(ItemActionUse use)
    {
        var s = use.Session;
        if (s.EmojiInfo == null) return ErrorCodes.FeatureUnavailable;
        if (use.Count != 1) return ErrorCodes.Parameter;
        var ids = new List<long>();
        foreach (var effect in use.Effects)
        {
            long type = effect.Length > 1 ? effect[1] : 0, tab = effect.Length > 2 ? effect[2] : 0;
            if (_data.Row("EmoticonTabConfig", tab) is not { } row || !row.TryGetProperty("_iIndex", out var index)
                || index.ValueKind != JsonValueKind.Array) return ErrorCodes.ItemNoConfig;
            foreach (var value in index.EnumerateArray())
                if (GameData.Number(value) is { } id && _data.Row("EmoticonsConfig", (long)id) is { } emoticon
                    && GameData.Int(emoticon, "_iType") == type) ids.Add((long)id);
        }
        if (ids.Count == 0) return ErrorCodes.ItemNoConfig;
        var fresh = ids.Distinct().Where(id => !s.EmojiInfo.EmojiInfo.Contains(id)).ToList();
        if (fresh.Count == 0) return ErrEmotesUnlocked;
        var next = s.EmojiInfo.Clone();
        next.EmojiInfo.AddRange(fresh);
        byte[] saved = next.ToByteArray();
        if (!await _character.CommitItemActionAsync(use, tx => tx.RoleBlob(s.Role!.Id, "emoji_info", saved), () => s.EmojiInfo = next,
            new { Emoticons = fresh })) return ErrorCodes.ItemsShort;
        await SyncEmoticonsAsync(s);
        return 0;
    }

    private async Task LoginEmoticonsAsync(GameSession session)
    {
        var saved = _roles.LoadEmojiInfo(session.Role!.Id);
        session.EmojiInfo = saved is { Length: > 0 } ? DBEmojiInfo.Parser.ParseFrom(saved) : new DBEmojiInfo();
        if (session.EmojiInfo.EmojiInfo.Count > 0) await SyncEmoticonsAsync(session);
    }

    /// <summary>
    /// 5041. The full handler walks emojiInfo and dungeonEmojiInstalls with pairs() unguarded (LC_DAL_Chicken.lua:3822-3860)
    /// and an empty field decodes as nil, so nothing is sent before the first unlock, and dungeonEmojiInstalls carries a
    /// wire-only dungeon 0 entry {emojiIds [0]} while the role has no Survivor installs (inferred, as PersonalService
    /// seeds its empty maps; no Survivor dungeon is 0).
    /// </summary>
    private Task SyncEmoticonsAsync(GameSession session)
    {
        var wire = session.EmojiInfo!.Clone();
        if (wire.DungeonEmojiInstalls.Count == 0)
            wire.DungeonEmojiInstalls[0] = new DBDungeonEmojiInstall { DungeonId = 0, EmojiIds = { 0 } };
        return CharacterService.SyncAsync(session, EmoticonSync, wire);
    }
}
