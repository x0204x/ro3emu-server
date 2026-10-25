using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

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
                    && new EmoticonsConfig(emoticon).Type == type) ids.Add((long)id);
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
        await SyncEmoticonsAsync(session);
    }

    /// <summary>
    /// 5041. The full handler walks emojiInfo and dungeonEmojiInstalls with pairs() unguarded (LC_DAL_Chicken.lua:3822-3860)
    /// and an empty field decodes as nil, so an empty emojiInfo carries a wire-only 0 (no emoticon has id 0; the set is
    /// only asked for real ids). dungeonEmojiInstalls key 0 is the Survivor wheel itself (enum_ExpressionDungeonId.Chicken,
    /// LC_ChickenHelper.GetChickenEmojiInstalls); the client has no default of its own, so a role that never edited it
    /// gets the EmoticonsConfig Survivor emotes marked _iAssemble 1 (inferred: the default wheel).
    /// </summary>
    private Task SyncEmoticonsAsync(GameSession session)
    {
        var wire = session.EmojiInfo!.Clone();
        if (wire.EmojiInfo.Count == 0) wire.EmojiInfo.Add(0);
        if (!wire.DungeonEmojiInstalls.ContainsKey(WheelChicken))
            wire.DungeonEmojiInstalls[WheelChicken] = new DBDungeonEmojiInstall { DungeonId = WheelChicken, EmojiIds = { DefaultWheel() } };
        return CharacterService.SyncAsync(session, EmoticonSync, wire);
    }

    public const uint DungeonEmojiInstall = 43304;
    private const int WheelChicken = 0, WheelOther = 1, WheelSlots = 6, SurvivorEmoticonType = 1;

    private int[] DefaultWheel()
    {
        var ids = _data.Table("EmoticonsConfig").Values
            .Select(row => new EmoticonsConfig(row)).Where(e => e.Type == SurvivorEmoticonType && e.Assemble == 1)
            .Select(e => (int)e.Id).Order().Take(WheelSlots).ToList();
        while (ids.Count < WheelSlots) ids.Add(0);
        return [.. ids];
    }

    /// <summary>
    /// 43304 from the Survivor emote wheel editor (UI_Sys_ExpressionWidgetBaseCtrl.lua:955-1048 -> LC_DAL_Chicken
    /// SendChickenSelectEmoji): the whole six-slot wheel of dungeonId 0 (Chicken; 1 is the "Other" wheel), 0 = an empty
    /// slot. Each emote is a Survivor EmoticonsConfig row (_iType 1) the role may use, once. The client binds no response
    /// and reads the change from 5041; the reply still carries the result.
    /// </summary>
    public async Task InstallDungeonEmojiAsync(GameSession session, DungeonEmojiInstallReq req)
    {
        int result = ErrorCodes.Parameter;
        if (session.Role != null && session.EmojiInfo != null && req.DungeonId is WheelChicken or WheelOther
            && req.EmojiIds.Count <= WheelSlots && req.EmojiIds.All(id => id == 0 || WheelEmoji(session, id))
            && req.EmojiIds.Where(id => id != 0).Distinct().Count() == req.EmojiIds.Count(id => id != 0))
        {
            var next = session.EmojiInfo.Clone();
            var wheel = new DBDungeonEmojiInstall { DungeonId = req.DungeonId, EmojiIds = { req.EmojiIds } };
            while (wheel.EmojiIds.Count < WheelSlots) wheel.EmojiIds.Add(0);
            next.DungeonEmojiInstalls[req.DungeonId] = wheel;
            var transaction = _roles.NewTransaction();
            transaction.RoleBlob(session.Role.Id, "emoji_info", next.ToByteArray());
            await _roles.CommitAsync(transaction, "emoji/wheel", _ => session.EmojiInfo = next);
            await SyncEmoticonsAsync(session);
            result = 0;
        }
        await session.SendGameAsync(DungeonEmojiInstall, new DungeonEmojiInstallResp { Result = result });
    }

    /// <summary>A Survivor emote the role may use: unlock type 0, or unlocked into emojiInfo and not expired.</summary>
    internal bool WheelEmoji(GameSession session, long id)
    {
        if (_data.Row("EmoticonsConfig", id) is not { } row || new EmoticonsConfig(row).Type != SurvivorEmoticonType) return false;
        if (new EmoticonsConfig(row).UnlockType == 0) return true;
        var info = session.EmojiInfo;
        return info != null && info.EmojiInfo.Contains(id)
            && (!info.EmojiExpireTime.TryGetValue(id, out long expire) || expire == 0 || expire > _clock.GetUtcNow().ToUnixTimeSeconds());
    }

    /// <summary>The Survivor wheel as the client sees it: the saved one, else the default.</summary>
    internal IReadOnlyList<int> Wheel(GameSession session) =>
        session.EmojiInfo?.DungeonEmojiInstalls.TryGetValue(WheelChicken, out var saved) == true ? saved.EmojiIds : DefaultWheel();
}
