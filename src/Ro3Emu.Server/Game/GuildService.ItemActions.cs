using System.Globalization;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The guild's item actions and its Guild Store: 113 UnlockGuildBadge, 114 SendGuildMemberPackage, and 32301
/// BuyGuildCommodity for the Guild Store (ShopConfig 2702, shop class enum_GuildBuildingShopClassID.Marketplace) whose
/// three commodities are the member packs 1211101..1211103.
/// </summary>
public sealed partial class GuildService
{
    public const uint BuyCommodity = 32301;
    internal const int UnlockBadgeAction = 113, MemberPackageAction = 114;
    // ErrorCodeConfig: 10039 "Your guild position does not meet this item's requirements", 10041 "Not enough Guild
    // Funds", 10044 "Purchase limit reached".
    public const int ErrStorePosition = 10039, ErrStoreFunds = 10041, ErrStoreLimit = 10044;
    private const int GuildStoreClass = 2702, FundsCurrency = 104;
    private const int IconRight = 5; // GuildPermissionConfig 5 "Change Icon" (32230 tp 3)

    private bool _itemActionsHooked;

    /// <summary>On the first guild login, as HookDeposits: the item actions reserve the actor's own guild keys.</summary>
    private void HookItemActions()
    {
        if (_itemActionsHooked) return;
        _itemActionsHooked = true;
        character.RegisterItemAction(UnlockBadgeAction, UseBadgeItemAsync, keys: OwnGuildKeys);
        character.RegisterItemAction(MemberPackageAction, UsePackageItemAsync, keys: OwnGuildKeys);
    }

    private static bool DefaultIcon(System.Text.Json.JsonElement row) => GameData.Int(row, "_iDefaultUnlock") == 1;

    /// <summary>A GuildIconConfig frame or design the guild may pick: unlocked by default or by a 113 item.</summary>
    private bool IconUnlocked(GuildStore.State? g, System.Text.Json.JsonElement row) =>
        DefaultIcon(row) || g?.UnlockedHeadIcons.ContainsKey(GameData.Int(row, "_iID")) == true;

    private static IEnumerable<UnLockHeadIcon> UnlockedIcons(GuildStore.State g) =>
        g.UnlockedHeadIcons.OrderBy(p => p.Key).Select(p => new UnLockHeadIcon { Id = (int)p.Key, Removetime = p.Value });

    /// <summary>
    /// 113 UnlockGuildBadge [113, GuildIconConfig id]: 1210901..1211007 unlock an emblem design (_iType 2, 2xxxxx) or a
    /// frame (_iType 1, 1xxxxx) for the guild. The guild keeps it in unLockHeadIconData {id, removetime}, 32213 field 60
    /// and the 32231 Change_UnLockHeadIcon (13) push, which replaces the client's kUnLockHeadIconData
    /// (LC_DAL_Guild.lua:2026-2038, 3246-3276); LC_GuildHelper.CheckIsIconUnlock counts any entry, and the badge tip shows
    /// "N days" only for a removetime in the future, so 0 is permanent. The picker (UI_Sys_Guild_Manage_InfoWidgetCtrl
    /// _initListByType / _savePattern) then lets the guild choose it through 32230 tp 3. The client checks nothing before
    /// 31323. Not in a guild: NotInGuild; already unlocked (by default or an item): AlreadyUnlocked. (inferred) The
    /// items' text says they go to the Guild Master or a Leader, who uses them: the position needs right 5 "Change
    /// Icon", else 10039. The item and the guild record commit together.
    /// </summary>
    private async Task<int> UseBadgeItemAsync(ItemActionUse use)
    {
        var s = use.Session;
        if (use.Count != 1) return ErrorCodes.Parameter;
        if (Own(s) is not { } g) return ErrorCodes.NotInGuild;
        if (!HasRight(g, s.Role!.Id, IconRight)) return ErrStorePosition;
        var icons = new List<long>();
        foreach (var effect in use.Effects)
        {
            if (effect.Length < 2 || data.Row("GuildIconConfig", effect[1]) is not { } row
                || GameData.Int(row, "_iType") is not (1 or 2)) return ErrorCodes.ItemNoConfig;
            if (!IconUnlocked(g, row)) icons.Add(effect[1]);
        }
        if (icons.Count == 0) return ErrorCodes.AlreadyUnlocked;
        foreach (long icon in icons) g.UnlockedHeadIcons[icon] = 0;
        bool done = false;
        try
        {
            done = await character.CommitSharedItemActionAsync(use, "item-action/guild-badge",
                tx => store.EnlistSave(g, tx, "guild disappeared during badge unlock"), null);
        }
        catch (RowMissingException e) when (e.Is(GuildStore.Table, g.Id)) { }
        finally { if (!done) foreach (long icon in icons) g.UnlockedHeadIcons.Remove(icon); }
        if (!done) return ErrorCodes.ItemsShort;
        var change = new GuildBaseInfoChangeNtf { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeUnLockHeadIcon };
        change.UnLockHeadIconData.AddRange(UnlockedIcons(g));
        await PushBase(g, change);
        return 0;
    }

    /// <summary>
    /// 114 SendGuildMemberPackage [114, MultiverseGroupConfig, MailConfig, op, position]: 1211101..1211103 [114, 2004..2006,
    /// 1120, 6, 20] "send every guild Member except each Temporary Member a ... Pack by mail". Every member whose
    /// position compares true with the LC_InteractCondition operator table (6 is &lt;=: positions 1..20, without 21
    /// Temporary Member) gets MailConfig 1120 "Guild Gift Pack" with the group's award (2004: one 1211104 Basic Pack).
    /// (inferred) Any member may use a pack: the Guild Store already limits who buys them ([9007,6,3]). The item is spent
    /// first; each letter then goes once, keyed by the use, so a retried delivery never doubles one.
    /// ErrorCodeConfig 10045 ("only in the guild where it was purchased") would need the purchase's guild on the item
    /// (ItemBaseInfo.ownerGuildId), which this server does not keep: a pack works in its holder's current guild.
    /// </summary>
    private async Task<int> UsePackageItemAsync(ItemActionUse use)
    {
        var s = use.Session;
        if (use.Count != 1) return ErrorCodes.Parameter;
        if (Own(s) is not { } g) return ErrorCodes.NotInGuild;
        long group = use.Arg(1), op = use.Arg(3), position = use.Arg(4);
        int template = (int)use.Arg(2);
        if (data.Row("MailConfig", template) == null || data.Row("MultiverseGroupConfig", group) == null) return ErrorCodes.ItemNoConfig;
        IReadOnlyList<MailService.Attachment> attachments;
        try { attachments = awards.Resolve(s, group).Grants.Select(a => new MailService.Attachment(a.Entry, a.Count, a.BindType)).ToArray(); }
        catch (AwardResolutionException) { return ErrorCodes.ItemNoConfig; }
        var recipients = g.Members.Values.Where(m => Compare(op, m.Official, position)).Select(m => m.RoleId).Order().ToArray();
        if (recipients.Length == 0) return ErrorCodes.Parameter;
        string source = $"guild-pack:{g.Id}:{s.Role!.Id}:{use.Item.Guid}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        if (!await character.CommitItemActionAsync(use, null, null, new
        {
            Guild = g.Id, Group = group, Template = template,
            Recipients = recipients
        })) return ErrorCodes.ItemsShort;
        var context = ownerRegistry.Current;
        foreach (long role in recipients)
        {
            try { await mail.SendTemplateOnceAsync(role, template, attachments, source + ":" + role, _ => { }, context); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Guild pack {Item} letter to role {Role} failed", use.Item.Entry, role);
            }
        }
        return 0;
    }

    /// <summary>LC_InteractCondition operator table (lua :313-356): 1 ==, 2 ~=, 3 &gt;, 4 &gt;=, 5 &lt;, 6 &lt;=.</summary>
    private static bool Compare(long op, long value, long param) => op switch
    {
        1 => value == param,
        2 => value != param,
        3 => value > param,
        4 => value >= param,
        5 => value < param,
        6 => value <= param,
        _ => false,
    };

    // ------------------------------------------------------------------ 32301 Guild Store

    /// <summary>This week's guild-wide Guild Store purchases, every commodity of the store listed (0 when none).</summary>
    private IEnumerable<ShopCommodityInfo> StoreCounts(GuildStore.State g)
    {
        bool current = g.ShopCommodityWeek == WeekStart;
        return data.Table("ShopCommodityConfig").Values.Where(r => GameData.Int(r, "_iClassID") == GuildStoreClass)
            .Select(r => (int)GameData.Int(r, "_iID")).Order()
            .Select(id => new ShopCommodityInfo { Commodityid = id, Buycount = current ? g.ShopCommodityBuys.GetValueOrDefault(id) : 0 });
    }

    public Task BuyCommodityAsync(GameSession s, BuyGuildCommodityReq req) =>
        RunGuildAsync(s, [], [], () => BuyCommodityCoreAsync(s, req), characterGate: true);

    /// <summary>
    /// 32301 BuyGuildCommodityReq {commodityId, playBook, count}: a Guild Store commodity (ShopCommodityConfig _iClassID
    /// 2702) paid from the guild's funds (_kItemPerPrice [104, price], enum_GuildResources 104 iMoney). The client sends it
    /// for a price in a guild resource only (UI_Com_Shop_BuyTipsPanelCtrl), after its own check of the funds (tip 22001).
    /// _iIsBuy is the buyer's [9007,6,3] CheckGuildPosition (Guild Master to Vice Guild Master), else 10039; the limit
    /// _iPurchaseNum (3) per _iPurchaseTimesLimit 2 week is the guild's, kept with the guild (DBGuildData
    /// shopCommodityData, 32213 field 66, the 32231 Change_ShopCommodityInfo 17 push) and shown as "Weekly Purchase
    /// Limit: bought/3" (LC_ShopHelper.GetGuildMarketplaceBuyedInfo), else 10044; funds short 10041. The reply
    /// {result, commodityId, count} raises "Purchase Successful" (LC_DAL_Guild._onProcessBuyCommodityResult). The
    /// funds, the counter and the goods commit together. (inferred) The week starts on Monday 05:00, as the guild
    /// activity week; the push lists every commodity of the store, so a new week's zeros replace the old counts.
    /// </summary>
    private async Task BuyCommodityCoreAsync(GameSession s, BuyGuildCommodityReq req)
    {
        var g = Own(s);
        var row = data.Row("ShopCommodityConfig", req.CommodityId);
        int code = g == null ? ErrorCodes.NotInGuild
            : row is not { } r || GameData.Int(r, "_iClassID") != GuildStoreClass ? ErrorCodes.Parameter
            : req.Count is < 1 or > 999 ? ErrorCodes.Parameter : 0;
        long[] price = code == 0 ? Flat(row!.Value, "_kItemPerPrice") : [];
        int item = code == 0 ? (int)GameData.Int(row!.Value, "_iItemId") : 0;
        int perMount = code == 0 ? Math.Max(1, (int)GameData.Int(row!.Value, "_iItemPerMount")) : 0;
        if (code == 0 && (price.Length < 2 || price[0] != FundsCurrency || price[1] < 0 || data.Row("ItemConfig", item) == null))
            code = ErrorCodes.Parameter;
        if (code == 0 && !functions.ConditionMet(s, GameData.Int(row!.Value, "_iIsBuy"))) code = ErrStorePosition;
        long week = WeekStart;
        int bought = code == 0 && g!.ShopCommodityWeek == week ? g.ShopCommodityBuys.GetValueOrDefault(req.CommodityId) : 0;
        long limit = code == 0 ? GameData.Int(row!.Value, "_iPurchaseNum") : 0;
        if (code == 0 && limit > 0 && bought + req.Count > limit) code = ErrStoreLimit;
        long total = code == 0 ? checked(price[1] * req.Count) : 0;
        if (code == 0 && g!.Money < total) code = ErrStoreFunds;
        if (code == 0)
        {
            var old = (Money: g!.Money, Week: g.ShopCommodityWeek, Buys: new Dictionary<int, int>(g.ShopCommodityBuys));
            if (g.ShopCommodityWeek != week) { g.ShopCommodityWeek = week; g.ShopCommodityBuys.Clear(); }
            g.ShopCommodityBuys[req.CommodityId] = bought + req.Count;
            g.Money -= total;
            int count = checked(req.Count * perMount);
            string operation = Guid.NewGuid().ToString("N");
            var guildOwner = new EconomyOwner(EconomyOwnerKind.Guild, g.Id.ToString(CultureInfo.InvariantCulture));
            var roleOwner = new EconomyOwner(EconomyOwnerKind.Role, s.Role!.Id.ToString(CultureInfo.InvariantCulture));
            var effects = new List<EconomyEffect>
            {
                new(new(EconomyAssetKind.Item, item), roleOwner, EconomyCompartment.Inventory, count,
                    EconomyLegKind.Source, EconomyReason.ShopGoods),
            };
            if (total > 0) effects.Add(new(new(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMoney), guildOwner,
                EconomyCompartment.GuildTreasury, -total, EconomyLegKind.Sink, EconomyReason.GuildResourceSpend));
            var economy = new EconomyEnvelope(operation, s.Account!, s.Role.Id, "guild-store",
                $"{req.CommodityId}:{req.Count}", $"guild-store:{g.Id}:{s.Role.Id}:{operation}", NowMs(), effects, []);
            bool ok = false;
            try
            {
                ok = await character.ExchangeLockedAsync(s, [], [(item, count)],
                    persist: tx => { store.EnlistSave(g, tx, "guild disappeared during store purchase"); tx.WithEconomy(economy); },
                    asyncCommit: true) != null;
            }
            catch (RowMissingException e) when (e.Is(GuildStore.Table, g.Id)) { }
            finally
            {
                if (!ok)
                {
                    g.Money = old.Money; g.ShopCommodityWeek = old.Week;
                    g.ShopCommodityBuys.Clear();
                    foreach (var (id, n) in old.Buys) g.ShopCommodityBuys[id] = n;
                }
            }
            if (!ok) code = ErrorCodes.Unknown;
        }
        await s.SendGameAsync(BuyCommodity, new BuyGuildCommodityRes { Result = code, CommodityId = req.CommodityId, Count = req.Count });
        if (code != 0) return;
        await PushBase(g!, new GuildBaseInfoChangeNtf { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeResource, Resource = Resource(g!) });
        var counts = new GuildBaseInfoChangeNtf { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeShopCommodityInfo };
        counts.ShopCommodityData.AddRange(StoreCounts(g!));
        await PushBase(g!, counts);
    }

    private static long[] Flat(System.Text.Json.JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Array
            ? value.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
}
