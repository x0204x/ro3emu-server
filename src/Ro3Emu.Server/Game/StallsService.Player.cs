using Google.Protobuf;
using System.Globalization;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class StallsService
{
    private int PlayerGate(GameSession s) => !_functions.IsOpen(s, 3) ? ErrorCodes.FunctionLocked
        : s.SceneLoading ? ErrorCodes.SwitchingScenes
        : s.Dead || (s.StatusBits & (1L << 15)) != 0 || s.InstanceId != 0 ? ErrorCodes.FeatureUnavailable : 0;

    /// <summary>
    /// ServerOptions.StallNameMaxLength (24; the original's StallsNameCharacterLimit 10 was sized for Chinese) bounds what a
    /// player types; the client's input uses the same limit (ClientHooks.LuaPatches). The client's own default title,
    /// Localization 37824 "${1}'s Stall" (LC_StallHelper.OnGetStallTitle), is accepted at any length.
    /// </summary>
    private int NameError(GameSession s, string title) => string.IsNullOrWhiteSpace(title) || title.Any(char.IsControl)
        || title.EnumerateRunes().Count() > _nameMaxLength && title != DefaultTitle(s) ? ErrorCodes.StallName : 0;

    private string DefaultTitle(GameSession s) => _data.ClientText(37824).Replace("${1}", s.Role?.Name ?? "");

    private int MaxGrid(GameSession s)
    {
        int skill = (int)Param("StallsPlaidMerchantSkill", 16005);
        int level = s.Progress!.Skills.GetValueOrDefault(skill);
        var row = _data.Table("SkillConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iSkillID") == skill && GameData.Int(r, "_iLevel") == level);
        var param = row.ValueKind == System.Text.Json.JsonValueKind.Object ? Field(row, "_kSpSkillParam") : [];
        return (int)Param("StallsDefaultPlaidNum", 10) + (param.Length > 1 ? (int)param[1] : 0);
    }

    private async Task StartAsync(GameSession s, PlayerStartStallsReq req)
    {
        var role = s.Role;
        var attachment = s.Attachment;
        var runtime = s.RuntimeMap;
        long generation = s.SceneGeneration;
        bool claimed = runtime.Kind == Ro3Emu.Server.Maps.RuntimeMapKind.PublicLine;
        if (claimed) OpeningStart(runtime); // Claim before MarketStore's first await, including post-commit notifications.
        StallRow? row = null;
        try
        {
            int code = await _market.RunAsync(s, tx =>
            {
                if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment)) return ErrorCodes.FeatureUnavailable;
                if (PlayerGate(s) is var gate and not 0) return gate;
                if (s.RuntimeMap != runtime || s.SceneGeneration != generation) return ErrorCodes.SwitchingScenes;
                if (NameError(s, req.StallsTitle) is var name and not 0) return name;
                var old = Own(tx, s);
                if (Active(old)) return ErrorCodes.StallExists;
                // Prontera allowlist wins over conflicting older all-open-world text (inferred).
                if (!ParamList("StallsMapID").Contains(runtime.SceneEntry)) return ErrorCodes.StallArea;
                if (!float.IsFinite(s.Position.X) || !float.IsFinite(s.Position.Y) || !float.IsFinite(s.Position.Z)) return ErrorCodes.Parameter;
                if (tx.All<StallRow>(Kind).Any(r => !r.Ended && r.Scene == runtime.SceneEntry
                    && (r.SystemId > 0 || r.LineNumber == runtime.LineNumber) &&
                    Distance(r, s) < Param("StallsIntervalRadius", 5))) return ErrorCodes.StallTooClose;
                int slots = MaxGrid(s);
                if (req.Itembase.Count == 0) return ErrorCodes.StallItemMissing;
                if (req.Itembase.Count > slots) return ErrorCodes.StallFull;
                if (req.Itembase.Select(i => i.Itemid).Distinct().Count() != req.Itembase.Count) return ErrorCodes.Parameter;
                int credit = s.Trade?.CreditPoint ?? (int)Param("TradeCreditDefault", 1000);
                if (credit < Param("CreditBooth", 20)) return ErrorCodes.TradeLowCredit;
                var meta = Metadata(tx, s.Role!.Id);
                row = new StallRow
                {
                    Key = PlayerKey(s.Role.Id), Owner = s.Role.Id, Scene = runtime.SceneEntry, LineNumber = runtime.LineNumber,
                    Title = req.StallsTitle,
                    OwnerName = s.Role.Name, Career = s.Progress!.Career, Level = s.Progress.Level, Sex = s.Role.Sex,
                    Area = s.Role.Area, Appearance = RoleProto.Appearances(s.Role), X = s.Position.X, Y = s.Position.Y, Z = s.Position.Z,
                    OpenTime = Now, Timeout = Now + Param("StallsTime", 604800), MaxGrid = slots,
                    NextItemId = old?.NextItemId ?? 1, TaxRate = meta.CurTax.Key,
                };
                var seller = tx.Role(s.Role.Id);
                foreach (var put in req.Itembase)
                    if (TakeFromBag(s, seller, row, put) is var error and not 0) return error;
                var fee = ParamList("StallsExpenses");
                if (fee.Length != 2) return ErrorCodes.Parameter;
                if (seller.Currency.GetValueOrDefault((int)fee[0]) < fee[1]) return ErrorCodes.StallCurrency;
                seller.Currency[(int)fee[0]] = seller.Currency.GetValueOrDefault((int)fee[0]) - fee[1]; // fee on open (inferred timing).
                if (fee[1] > 0) tx.Economy.Sink(new(EconomyAssetKind.Currency, fee[0]), fee[1], EconomyReason.StallOpenFee,
                    EconomyCompartment.Currency, new(EconomyOwnerKind.Role, seller.RoleId.ToString(CultureInfo.InvariantCulture)));
                meta.NeedNotify = false;
                Save(tx, row, meta);
                return 0;
            });
            // Escrow has been applied and the market gates released. The opening claim still
            // owns the line until Pin/Publish below; this actor-publication wait owns no DB keys.
            if (code == 0 && OpeningCommittedForTests is { } openingCommitted) await openingCommitted();
            if (code == 0)
                await PublishAsync(row!, beforeProjection: current =>
                    ReferenceEquals(s.Role, role) && ReferenceEquals(s.Attachment, attachment)
                        ? s.SendGameAsync(Start, new PlayerStartStallsRsp { Result = 0, Info = SellerView(current) })
                        : Task.CompletedTask);
            else if (ReferenceEquals(s.Role, role) && ReferenceEquals(s.Attachment, attachment))
                await s.SendGameAsync(Start, new PlayerStartStallsRsp { Result = code });
        }
        finally { if (claimed) OpeningEnd(runtime); }
    }

    private int TakeFromBag(GameSession s, MarketParticipant seller, StallRow row, StallsPutItem put)
    {
        if (row.Stock.Count >= row.MaxGrid) return ErrorCodes.StallFull;
        var item = seller.Items.FirstOrDefault(i => i.Guid == put.Itemid);
        if (item == null) return ErrorCodes.ItemMissing;
        if (_trade.TransactionItemError(s, item) is var error and not 0) return error;
        if (SaleConfig(item.Entry) is not { } config) return ErrorCodes.NotForSale;
        if (put.Itemnum <= 0 || put.Itemnum > item.Stack || put.Itemnum > GameData.Int(config, "_iShelveLimit")) return ErrorCodes.StallQuantity;
        if (!ValidPrice(config, put.Price, put.Pricetype)) return ErrorCodes.TradePrice;
        var escrow = item.Clone();
        escrow.Stack = (int)put.Itemnum;
        row.Stock.Add(new StallItem { Id = row.NextItemId++, Item = escrow, Price = put.Price, Currency = put.Pricetype });
        item.Stack -= (int)put.Itemnum;
        if (item.Stack == 0) seller.Items.Remove(item);
        return 0;
    }

    private async Task PutAsync(GameSession s, PlayerStallsPutItemReq req)
    {
        StallRow? row = null;
        int code = await _market.RunAsync(s, tx =>
        {
            if (PlayerGate(s) is var gate and not 0) return gate;
            row = Own(tx, s);
            if (!Active(row)) return ErrorCodes.StallNotOpen;
            if (req.PutItem == null) return ErrorCodes.Parameter;
            int error = TakeFromBag(s, tx.Role(s.Role!.Id), row!, req.PutItem);
            if (error != 0) return error;
            Save(tx, row!);
            return 0;
        });
        await s.SendGameAsync(Put, new PlayerStallsPutItemRsp { Result = code, Baseinfo = code == 0 ? ItemView(row!.Stock[^1]) : null });
        if (code == 0) await PublishAsync(row!);
    }

    private async Task RemoveAsync(GameSession s, PlayerStallsRemoveItemReq req)
    {
        StallRow? row = null;
        int code = await _market.RunAsync(s, tx =>
        {
            row = Own(tx, s);
            if (!Active(row)) return ErrorCodes.StallNotOpen;
            var item = row!.Stock.FirstOrDefault(i => i.Id == req.StallsItemID);
            if (item == null) return ErrorCodes.StallItemMissing;
            tx.DeliverMail(row.Owner, (int)Param("StallsTakenOffMailId", 1027), [item.Item]);
            row.Stock.Remove(item);
            Save(tx, row);
            return 0;
        });
        await s.SendGameAsync(Remove, new PlayerStallsRemoveItemRsp { Result = code });
        if (code == 0) await PublishAsync(row!);
    }

    private async Task UpdateAsync(GameSession s, PlayerStallsUpdateItemPriceReq req)
    {
        StallRow? row = null;
        int code = await _market.RunAsync(s, tx =>
        {
            row = Own(tx, s);
            if (!Active(row)) return ErrorCodes.StallNotOpen;
            var offer = row!.Stock.FirstOrDefault(i => i.Id == req.StallsItemID);
            if (offer == null) return ErrorCodes.StallItemMissing;
            if (SaleConfig(offer.Item.Entry) is not { } config) return ErrorCodes.NotForSale;
            if (!ValidPrice(config, req.Price, offer.Currency)) return ErrorCodes.TradePrice;
            if (req.Num < 0) return ErrorCodes.StallQuantity;
            long next = offer.Item.Stack + (req.BAdd ? (long)req.Num : -req.Num);
            if (next <= 0 || next > GameData.Int(config, "_iShelveLimit")) return ErrorCodes.StallQuantity;
            if (req.BAdd && req.Num > 0)
            {
                if (PlayerGate(s) is var gate and not 0) return gate;
                var seller = tx.Role(s.Role!.Id);
                // Quantity edits have no item GUID. Only truly identical, tradable bag instances can replenish it.
                var matching = seller.Items.Where(i => i.Entry == offer.Item.Entry && _trade.TransactionItemError(s, i) == 0 && SameItem(i, offer.Item)).ToList();
                if (matching.Sum(i => (long)i.Stack) < req.Num) return ErrorCodes.StallQuantity;
                int left = req.Num;
                foreach (var item in matching)
                {
                    int take = Math.Min(left, item.Stack);
                    item.Stack -= take; left -= take;
                    if (item.Stack == 0) seller.Items.Remove(item);
                    if (left == 0) break;
                }
            }
            else if (!req.BAdd && req.Num > 0)
            {
                var back = offer.Item.Clone(); back.Stack = req.Num;
                tx.DeliverMail(row.Owner, (int)Param("StallsTakenOffMailId", 1027), [back]);
            }
            offer.Item.Stack = (int)next;
            offer.Price = req.Price;
            Save(tx, row);
            return 0;
        });
        await s.SendGameAsync(Update, new PlayerStallsUpdateItemPriceItemRsp { Result = code });
        if (code == 0) await PublishAsync(row!);
    }

    private bool SameItem(InvItem a, InvItem b)
    {
        var x = _character.ItemInfoOf(a); var y = _character.ItemInfoOf(b);
        x.ItemGuid = y.ItemGuid = 0; x.Stack = y.Stack = 1;
        return x.Equals(y);
    }

    private async Task BuyAsync(GameSession s, PlayerStallsBuyItemReq req)
    {
        StallRow? row = null;
        int code = await _market.RunAsync(s, tx =>
        {
            if (req.Num <= 0 || req.StallsID < 0 || req.SystemStallID < 0 || (req.StallsID == 0) == (req.SystemStallID == 0)) return ErrorCodes.Parameter;
            if (s.Dead || s.InstanceId != 0 || (s.StatusBits & (1L << 15)) != 0) return ErrorCodes.FeatureUnavailable;
            row = tx.Get<StallRow>(Kind, req.SystemStallID > 0 ? SystemKey(req.SystemStallID) : PlayerKey(req.StallsID));
            if (!Active(row)) return ErrorCodes.StallMissing;
            if (row!.Owner == s.Role!.Id) return ErrorCodes.TradeRequest;
            // Remote links may be browsed; purchase requires the displayed map/line and BoothRange (inferred).
            if (s.SceneLoading || s.SceneEntry != row.Scene || (row.SystemId == 0 && s.LineNumber != row.LineNumber)
                || Distance(row, s) > Param("BoothRange", 5)) return ErrorCodes.StallDistance;
            var offer = row.Stock.FirstOrDefault(i => i.Id == req.StallsItemID);
            if (offer == null) return ErrorCodes.StallItemMissing;
            if (req.Price != offer.Price) return ErrorCodes.StallPriceChanged;
            if (req.Num > offer.Item.Stack) return ErrorCodes.StallQuantity;
            if (row.SystemId > 0)
            {
                if (_data.Row("StallSystemItem", offer.SystemConfig) is not { } config || !SystemItemAvailable(config)) return ErrorCodes.NotForSale;
                // Cumulative per role for this generated stall's lifetime (inferred reset scope).
                if ((long)offer.Bought.GetValueOrDefault(s.Role.Id) + req.Num > GameData.Int(config, "_iBuyLimit")) return ErrorCodes.PurchaseLimit;
                offer.Bought[s.Role.Id] = offer.Bought.GetValueOrDefault(s.Role.Id) + req.Num;
            }
            else
            {
                if (!_functions.IsOpen(s, 3)) return ErrorCodes.FunctionLocked;
                if (SaleConfig(offer.Item.Entry) is not { } config) return ErrorCodes.NotForSale;
                if (req.Num > GameData.Int(config, "_iBuyLimit")) return ErrorCodes.StallQuantity;
            }
            long gross = (long)offer.Price * req.Num;
            var buyer = tx.Role(s.Role.Id);
            if (buyer.Currency.GetValueOrDefault(offer.Currency) < gross) return ErrorCodes.StallCurrency;
            buyer.Currency[offer.Currency] = buyer.Currency.GetValueOrDefault(offer.Currency) - gross;
            var bought = offer.Item.Clone(); bought.Stack = req.Num;
            tx.DeliverMail(s.Role.Id, (int)Param("StallsBuyItemMailId", 1028), [bought]);
            offer.Item.Stack -= req.Num;
            if (row.SystemId == 0)
            {
                var meta = Metadata(tx, row.Owner);
                // Weekly gross across the stored currency units, tier selected before this sale, ceiling tax:
                // (inferred) backend accounting/rounding; client proves curTax.key /100 displays the percentage.
                long tax = (gross * meta.CurTax.Key + 9999) / 10000;
                if (tax > 0) tx.Economy.Sink(new(EconomyAssetKind.Currency, offer.Currency), tax, EconomyReason.StallTax,
                    EconomyCompartment.Currency, new(EconomyOwnerKind.Role, buyer.RoleId.ToString(CultureInfo.InvariantCulture)));
                long net = gross - tax;
                if (net > 0) tx.DeliverMail(row.Owner, (int)Param("StallsSellItemMailId", 1029),
                    currency: new Dictionary<int, long> { [offer.Currency] = net }, contentParams:
                    [_data.ItemName(bought.Entry), req.Num.ToString(CultureInfo.InvariantCulture), s.Role.Name,
                        DateTimeOffset.FromUnixTimeSeconds(Now).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        tax.ToString(CultureInfo.InvariantCulture), net.ToString(CultureInfo.InvariantCulture)]); // (inferred) UTC display format.
                row.Gains[offer.Currency] = checked(row.Gains.GetValueOrDefault(offer.Currency) + net);
                meta.WeekMoney.Value = checked(meta.WeekMoney.Value + gross);
                row.TaxRate = TaxRate(meta.WeekMoney.Value);
                var record = new StallsRecord
                {
                    ItemEntryID = bought.Entry, ItemNum = req.Num, Buytime = Now, Tradetax = tax,
                    Gainmoney = net, Areaid = s.Role.Area, Buyerid = s.Role.Id, Buyername = s.Role.Name, CoinEntryID = offer.Currency
                };
                row.Records.Insert(0, record.ToByteArray());
                if (row.Records.Count > Param("StallsLogsNumMax", 100)) row.Records.RemoveAt(row.Records.Count - 1);
                if (offer.Item.Stack == 0) row.Stock.Remove(offer);
                Save(tx, row, meta);
            }
            else
            {
                tx.Economy.Sink(new(EconomyAssetKind.Currency, offer.Currency), gross, EconomyReason.NpcShopPayment,
                    EconomyCompartment.Currency, new(EconomyOwnerKind.Role, buyer.RoleId.ToString(CultureInfo.InvariantCulture)));
                Save(tx, row);
            }
            return 0;
        });
        await s.SendGameAsync(Buy, new PlayerStallsBuyItemResp { Result = code });
        if (code == 0) await PublishAsync(row!);
    }

    private async Task InfoAsync(GameSession s)
    {
        StallRow? row = null; DBStallsInfo? meta = null;
        await _market.RunAsync(s, tx => { row = Own(tx, s); meta = Metadata(tx, s.Role!.Id); return 0; });
        await s.SendGameAsync(Info, new PlayerStallsInfoResp
        {
            CurrentStallsID = Active(row) ? row!.Owner : 0,
            Info = Active(row) ? SellerView(row!) : null, CurTax = meta!.CurTax.Clone()
        });
    }

    private async Task ViewAsync(GameSession s, PlayerStallsSyncBuyerStallsInfoReq req)
    {
        StallRow? row = null;
        int code = await _market.RunAsync(s, tx =>
        {
            if (req.StallsID < 0 || req.SystemStallID < 0 || (req.StallsID == 0) == (req.SystemStallID == 0)) return ErrorCodes.Parameter;
            row = tx.Get<StallRow>(Kind, req.SystemStallID > 0 ? SystemKey(req.SystemStallID) : PlayerKey(req.StallsID));
            return !Active(row) ? ErrorCodes.StallMissing
                : req.SceneEntryID != row!.Scene || (row.SystemId == 0 && req.SceneLineNum != row.LineNumber)
                  || (row.SystemId > 0 && req.SceneLineNum != s.LineNumber) ? ErrorCodes.StallMissing : 0;
        });
        if (code == 0) { s.ViewedStall = row!.Owner; s.ViewedSystemStall = row.SystemId; }
        await s.SendGameAsync(View, new PlayerStallsSyncBuyerStallsInfoResp
        { Result = code, Info = code == 0 ? BuyerView(row!, row!.SystemId > 0 ? s.LineNumber : 0) : null });
    }

    private void Close(MarketTransaction tx, StallRow row, int mail)
    {
        row.Ended = true; row.EndMailId = mail;
        // Closing mail returns only remaining escrow; sold proceeds already belong to individual sale mails.
        string[] parameters = mail switch
        {
            1031 => [row.Gains.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture),
                row.Gains.GetValueOrDefault(2).ToString(CultureInfo.InvariantCulture)],
            1032 => [Math.Max(0, (Now - row.OpenTime) / 60).ToString(CultureInfo.InvariantCulture),
                Math.Max(0, (Now - row.OpenTime) % 60).ToString(CultureInfo.InvariantCulture),
                row.Gains.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture),
                row.Gains.GetValueOrDefault(2).ToString(CultureInfo.InvariantCulture)],
            _ => [],
        };
        tx.DeliverMail(row.Owner, mail, row.Stock.Select(i => i.Item), contentParams: parameters);
        row.Stock.Clear();
        var meta = Metadata(tx, row.Owner); meta.NeedNotify = true;
        Save(tx, row, meta);
    }

    private async Task EndAsync(GameSession s)
    {
        StallRow? row = null;
        int code = await _market.RunAsync(s, tx =>
        {
            row = Own(tx, s);
            if (!Active(row)) return ErrorCodes.StallNotOpen;
            Close(tx, row!, (int)Param("StallsCloseMailId", 1030));
            return 0;
        });
        await s.SendGameAsync(End, new PlayerStallsEndResp { Result = code });
        if (code == 0) await PublishAsync(row!);
    }

    private async Task RenameAsync(GameSession s, PlayerUpdateStallsNameReq req)
    {
        StallRow? row = null;
        int code = await _market.RunAsync(s, tx =>
        {
            if (NameError(s, req.StallsTitle) is var error and not 0) return error;
            row = Own(tx, s);
            if (!Active(row)) return ErrorCodes.StallNotOpen;
            row!.Title = req.StallsTitle; Save(tx, row); return 0;
        });
        await s.SendGameAsync(Rename, new PlayerUpdateStallsNameResp { Result = code, StallsTitle = req.StallsTitle });
        if (code == 0) await PublishAsync(row!);
    }

    private async Task TopAsync(GameSession s, PlayerStallsTopItemReq req)
    {
        StallRow? row = null;
        int code = await _market.RunAsync(s, tx =>
        {
            row = Own(tx, s);
            if (!Active(row)) return ErrorCodes.StallNotOpen;
            var item = row!.Stock.FirstOrDefault(i => i.Id == req.StallItemID);
            if (item == null) return ErrorCodes.StallItemMissing;
            row.Stock.Remove(item); row.Stock.Insert(0, item); Save(tx, row); return 0;
        });
        await s.SendGameAsync(Top, new PlayerStallsTopItemRsp { Result = code, Info = code == 0 ? SellerView(row!) : null });
        if (code == 0) await PublishAsync(row!);
    }

    private Task ShareAsync(GameSession s, PlayerStallsShareCheckReq req)
    {
        // SellerWidgetCtrl allows empty text and caps it at StallsShareWordCount; rune counting is (inferred).
        int code = req.ShareData.Any(char.IsControl) || req.ShareData.EnumerateRunes().Count() > Param("StallsShareWordCount", 20) ? ErrorCodes.Parameter
            : _lastShare.TryGetValue(s.Role!.Id, out var then) && Now - then < Param("StallsShareCDTime", 180) ? ErrorCodes.StallShareCooldown : 0;
        // No profanity dictionary was recovered; this only validates shape/cooldown (inferred), and sends no chat.
        if (code == 0) _lastShare[s.Role!.Id] = Now;
        return s.SendGameAsync(Share, new PlayerStallsShareCheckResp { Result = code });
    }

    private static double Distance(StallRow row, GameSession s) => Math.Sqrt(Math.Pow(row.X - s.Position.X, 2) + Math.Pow(row.Z - s.Position.Z, 2));
}
