using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Gifts, heat and tags, and the room list (UI_Sys_Chat_BarRoom_GiveawayWidget, LC_BarRoomHelper.OnPresentGift /
/// SortRoomList, UI_Sys_Chat_BarRoomWidget's tag pass, LC_BarRoomServer.PartyQuerySummaryRequest).
/// </summary>
public sealed partial class TavernService
{
    /// <summary>
    /// 82491 from the Giveaway panel: giftNum of the drink giftId to a member. The drink's ItemConfig use effects give the
    /// numbers: 110 GainBarroomHot x count to the room's heat (the reply's hotAdd), 111 GainStar x count to the receiver's
    /// Star Power (userBarRoomHot, the "${6} Star Points" of the 59229 line the giver's client posts to the room).
    /// Refused for yourself (2574), a receiver no longer in the room (2555), offline (2563) or of another server (2564),
    /// and for a short bag. The drinks are spent under an event/tavern-gift receipt; (inferred) the Star Power is no
    /// asset and is credited in the receiver's own commit, as friend-gift popularity is.
    /// </summary>
    private async Task GiftAsync(GameSession s, PresentGiftReq req)
    {
        long self = s.Role!.Id;
        var (heat, star) = GiftValues(req.GiftId);
        int code;
        long guid = 0;
        lock (_sync)
        {
            code = InRoomLocked(self, req.BarRoomGuid, out var room);
            if (code == 0)
            {
                guid = room.Guid;
                code = (heat <= 0 && star <= 0) || req.GiftNum <= 0 || req.GiftNum > MaxGiftCount ? ErrorCodes.Parameter
                    : req.TargetGuid == self ? ErrorCodes.TavernGiftSelf
                    : !room.Members.TryGetValue(req.TargetGuid, out var member) ? ErrorCodes.TavernRecipientLeft
                    : member.Area != room.Members[self].Area ? ErrorCodes.TavernOtherServer
                    : 0;
            }
        }
        var receiver = code == 0 ? Online(req.TargetGuid) : null;
        if (code == 0 && receiver == null) code = ErrorCodes.TavernRecipientOffline;
        if (code == 0 && !await PayGiftAsync(s, req)) code = ErrorCodes.ItemsShort;
        if (code != 0)
        {
            await s.SendGameAsync(GiftMsg, new PresentGiftResp { Result = code });
            return;
        }
        int hotAdd = (int)Math.Min(int.MaxValue, (long)heat * req.GiftNum), starAdd = (int)Math.Min(int.MaxValue, (long)star * req.GiftNum);
        lock (_sync)
            if (_rooms.TryGetValue(guid, out var room)) room.Hot = AddClamped(room.Hot, hotAdd);
        await s.SendGameAsync(GiftMsg, new PresentGiftResp
        {
            Result = 0, ItemId = req.GiftId, ItemNum = req.GiftNum, GifterUser = s.Role.Name, ReceiveUser = req.TargetGuid,
            UserBarRoomHot = starAdd, HotAdd = hotAdd,
        });
        LogGift(_log, s.Role.Name, req.GiftNum, req.GiftId, guid, hotAdd, starAdd, req.TargetGuid);
        if (starAdd > 0) _loop.PostOwned(receiver!, () => CreditStarAsync(receiver!, starAdd), "tavern star power");
        await PushChangeAsync(guid, except: self);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} gives {Count}x {Item} in Tavern room {Room}: +{Heat} heat, +{Star} Star Power to {Target}")]
    private static partial void LogGift(ILogger logger, string name, int count, int item, long room, int heat, int star, long target);

    private async Task<bool> PayGiftAsync(GameSession s, PresentGiftReq req)
    {
        if (_character.CountOf(s, req.GiftId) < req.GiftNum) return false;
        string source = $"tavern/gift/{s.Role!.Id}/{Guid.NewGuid():N}";
        var paid = await _character.ExchangeAsync(s, [(req.GiftId, (long)req.GiftNum)], [], popup: false,
            persist: tx => tx.WithEventEconomy(EventEconomy.ForRole(_data, s, "tavern-gift", source, [(req.GiftId, (long)req.GiftNum)], [],
                new { req.BarRoomGuid, req.TargetGuid, req.GiftId, req.GiftNum })));
        return paid != null;
    }

    /// <summary>A drink's (110 GainBarroomHot, 111 GainStar) use effects; (0, 0) for anything that is no Tavern gift.</summary>
    private (int Heat, int Star) GiftValues(int item)
    {
        if (ItemConfig.Get(_data, item) is not { UseEffect: { ValueKind: JsonValueKind.Array } effects }) return (0, 0);
        int heat = 0, star = 0;
        foreach (var effect in effects.EnumerateArray())
        {
            if (effect.ValueKind != JsonValueKind.Array || effect.GetArrayLength() < 2) continue;
            if (!int.TryParse(Text(effect[0]), out int id) || !int.TryParse(Text(effect[1]), out int value)) continue;
            if (id == EffectHeat) heat += value;
            else if (id == EffectStar) star += value;
        }
        return (heat, star);
    }

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

    // ---- heat and tags ----

    /// <summary>
    /// The end of a heat window (PublicParamConfig.BarroomHeatTagsTime, 5 min): historyHot keeps the heat and its gain
    /// over the window, and the two server tags are dealt again until <paramref name="ends"/>. BarroomTagConfig only
    /// names the icons and the client only keeps the latest BarroomHeatTagsNum Spark and BarroomHotTagsNum Boom tags, so
    /// (inferred) Spark (the heat icon) goes to the rooms that gained at least BarroomHeatTagsMiniIncrease heat in the
    /// window, most first, and Boom (cheers) to the hottest rooms. Heat itself (inferred) only grows while the room
    /// exists. RedBag and Activity are operator tags (GM 137). Under the lock.
    /// </summary>
    private void HeatWindowLocked(long ends)
    {
        foreach (var room in _rooms.Values)
        {
            room.LastHotAdd = Math.Max(0, room.Hot - room.LastHot);
            room.LastHot = room.Hot;
            room.Tags.Remove(RoomTagType.Spark);
            room.Tags.Remove(RoomTagType.Boom);
        }
        long minimum = Param("BarroomHeatTagsMiniIncrease", DefaultHeatMiniIncrease);
        foreach (var room in _rooms.Values.Where(r => r.LastHotAdd >= minimum).OrderByDescending(r => r.LastHotAdd)
                     .ThenBy(r => r.Guid).Take((int)Param("BarroomHeatTagsNum", DefaultHeatTagsNum)))
            room.Tags[RoomTagType.Spark] = ends;
        foreach (var room in _rooms.Values.Where(r => r.Hot > 0).OrderByDescending(r => r.Hot)
                     .ThenBy(r => r.Guid).Take((int)Param("BarroomHotTagsNum", DefaultHotTagsNum)))
            room.Tags[RoomTagType.Boom] = ends;
    }

    // ---- the room list ----

    /// <summary>
    /// PartyService.getPartysSummary for party type 4: the live rooms as SummaryList[].GameSummaryData (BarRoomSummary
    /// bytes, LC_BarRoomHelper.OnPartyQuerySummary). Discover sends the int conditions BaroomCreateTime (created in
    /// [now - BarroomFindBarRoom[0], ...], unix s) and BaroomHeadCount ([2, 30] members); the plain list sends none and
    /// filters by topic, language and name itself. (inferred) Past the page size the rooms go by the weights of
    /// PublicParamConfig.BarroomSortParam [6000, 4000] on heat and head count; the client sorts what it gets again.
    /// </summary>
    public object[] Directory(IReadOnlyList<(string Key, long Min, long Max)> conditions, int offset, int limit)
    {
        long[] weights = ParamList("BarroomSortParam");
        long heatWeight = weights.Length > 0 ? weights[0] : 6000, headWeight = weights.Length > 1 ? weights[1] : 4000;
        lock (_sync)
            return [.. _rooms.Values.Where(r => conditions.All(c => Matches(r, c)))
                .OrderByDescending(r => r.Hot * heatWeight + HeadCount(r) * headWeight).ThenBy(r => r.Guid)
                .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 0, 100))
                .Select(r => (object)new Dictionary<string, object?> { ["GameSummaryData"] = Summary(r).ToByteArray() })];
    }

    /// <summary>getSubPartysCountByType for party type 4: (inferred) the live rooms per topic. No client code reads it.</summary>
    public object[] Counts()
    {
        lock (_sync)
            return [.. _rooms.Values.GroupBy(r => r.Type).OrderBy(g => g.Key)
                .Select(g => (object)new Dictionary<string, object?> { ["SubType"] = g.Key, ["Count"] = g.Count() })];
    }

    /// <summary>The room's head count as the list shows it, with a GM's extra count (GmSetBarroomInfo).</summary>
    private static long HeadCount(Room room) => room.Members.Count + (long)room.GmCount;

    private static bool Matches(Room room, (string Key, long Min, long Max) condition) => condition.Key switch
    {
        "BaroomCreateTime" => room.CreateSec >= condition.Min && room.CreateSec <= condition.Max,
        "BaroomHeadCount" => HeadCount(room) >= condition.Min && HeadCount(room) <= condition.Max,
        _ => true,
    };
}
