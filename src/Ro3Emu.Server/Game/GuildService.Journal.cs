using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// 32228 guild journal (Members > Events).
/// <list type="bullet">
/// <item>Each change appends a GuildEventData row with the guild's next index in the same transaction as the change
/// (guild_event table, GuildEventAppendPlan); the newest GuildEventDateMaxNum (500) stay. (inferred storage)</item>
/// <item>Params follow LC_GuildEventHelper's per-event formatters, which render nothing unless the count matches
/// exactly; playerID[i] turns the i-th name into a profile link, so it lists the named roles in order.</item>
/// <item>Pages go newest first (the event widget stops at the first row older than the member's join,
/// UI_Sys_Guild_Member_EventWidgetCtrl.lua:400-505). An empty page is a success with no rows: checked in the
/// client, it clears the table without a Lua error, while a nonzero result toasts the error and keeps the old rows.</item>
/// </list>
/// </summary>
public sealed partial class GuildService
{
    public const uint Events = 32228;
    private const int EvApproved = 1, EvLeft = 2, EvKicked = 3, EvPosition = 4, EvLeader = 6, EvJoined = 8,
        EvBuilding = 9, EvPurchase = 12, EvFounded = 13, EvAuction = 14;
    private const int GuildMaterialItem = 126; // LC_GuildHelper.GUILD_MATERIALS_ITEM_ID

    private static LanguageParam Str(string value) => new() { Tp = LanguageParamType.Str, StrParam = value };
    private static LanguageParam Int(long value) => new() { Tp = LanguageParamType.Int, IntParam = value };

    internal static byte[] JournalRow(int cfgId, long[] players, params LanguageParam[] values)
    {
        var row = new GuildEventData { EventCfgID = cfgId, Time = NowMs(), Param = new LanguageData() };
        row.PlayerID.AddRange(players);
        row.Param.Param.AddRange(values);
        return row.ToByteArray();
    }

    /// <summary>Queue a journal row on the guild; it commits with the guild's next save.</summary>
    private void Journal(GuildStore.State g, int cfgId, long[] players, params LanguageParam[] values)
    {
        store.JournalCap = (int)Math.Clamp(Setting("GuildEventDateMaxNum", 500)[0], 1, 10_000);
        g.PendingEvents.Add(JournalRow(cfgId, players, values));
    }

    /// <summary>A guild auction lot went to a member: "${1} won ${2} in the Guild Auction" (event 14).</summary>
    internal static byte[] AuctionWonRow(long winner, string winnerName, long itemId) =>
        JournalRow(EvAuction, [winner], Str(winnerName), Int(itemId));

    private long JournalType(long cfgId) =>
        data.Row("GuildEventConfig", cfgId) is { } row ? GameData.Int(row, "_iType") : 0;

    public Task EventsAsync(GameSession s, GuildEventReq req) => RunGuildAsync(s, [], [], () => EventsCoreAsync(s, req));

    private Task EventsCoreAsync(GameSession s, GuildEventReq req)
    {
        var g = Own(s);
        var resp = new GuildEventResp
        {
            FromIndex = req.FromIndex, Count = req.Count, GetUp = req.GetUp,
            OnlyShowMe = req.OnlyShowMe
        };
        if (g == null) { resp.Result = ErrorCodes.NotInGuild; return s.SendGameAsync(Events, resp); }
        var journal = store.Journal(g.Id);
        resp.LastIndex = journal.Count == 0 ? 0 : journal[0].Index;
        var types = req.ValidType.ToHashSet();
        long me = s.Role!.Id;
        var rows = journal.Select(r => (r.Index, Row: GuildEventData.Parser.ParseFrom(r.Data)))
            .Where(r => types.Count == 0 || types.Contains(JournalType(r.Row.EventCfgID)))
            .Where(r => !req.OnlyShowMe || r.Row.PlayerID.Contains(me)).ToList();
        int count = (int)Math.Clamp(req.Count <= 0 ? 20 : req.Count, 1, 100);
        // getUp walks back from fromIndex (open: 0 = newest; scrolling: oldest shown - 1); otherwise the newer side.
        var page = req.GetUp || req.FromIndex <= 0
            ? rows.Where(r => req.FromIndex <= 0 || r.Index <= req.FromIndex).Take(count).ToList()
            : rows.Where(r => r.Index > req.FromIndex).TakeLast(count).ToList();
        resp.Datas.AddRange(page.Select(r => new GuildEventResp.Types.eventData { Data = r.Row, Index = r.Index }));
        resp.ToTop = page.Count == 0 || page[^1].Index == rows[^1].Index;
        resp.ToBottom = page.Count == 0 || page[0].Index == rows[0].Index;
        return s.SendGameAsync(Events, resp);
    }

}
