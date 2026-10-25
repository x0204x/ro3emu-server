#pragma warning disable CS1998 // arms that only send or return keep the uniform async shape
using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>Shops, mail claims, auctions, player trade and stalls.</summary>
public sealed class EconomyMessages(ShopService shops, MailService mail, AuctionService auctions, TradeService trade, StallsService stalls,
    TimedBattleService timedBattles) : Messaging.IMessageModule
{
    public void Map(Messaging.MessageRoutes routes)
    {
        // Trade, stalls, auctions and mail claims reserve the goods and currencies they move themselves.
        routes.AdmitEconomic(id => id is >= 32601 and <= 32630); // trade
        routes.AdmitEconomic(id => id is >= 33401 and <= 33418); // stalls
        routes.AdmitEconomic(id => id is >= 82000 and <= 82020); // auctions
        routes.AdmitEconomic(id => id is MailService.ClaimMail);
        routes.AdmitRole(id => id is >= 82332 and <= 82340); // ordinary shops
        routes.Intercept("trade", id => id is >= 32601 and <= 32630, (session, msgId, body) =>
            session.Role != null && session.Progress != null ? trade.HandleAsync(session, msgId, body) : Task.FromResult(false));
        routes.Intercept("stalls", id => id is >= StallsService.Start and <= StallsService.Share, (session, msgId, body) =>
            session.Role != null && session.Progress != null ? stalls.HandleAsync(session, msgId, body) : Task.FromResult(false));

        routes.Map(ShopService.BuyCommodity, "shops", async (session, msgId, body) =>
        {
            var buy = BuyCommodityReq.Parser.ParseFrom(body.Span);
            // A Survivor Battle shop NPC is paid from the match pack (TimedBattleService.ChickenShop).
            if (!await timedBattles.TryBuyAsync(session, buy)) await shops.BuyAsync(session, buy);
        });
        routes.Map(TimedBattleService.DungeonShop, "shops", async (session, msgId, body) =>
            await timedBattles.GetDungeonShopAsync(session, GetDungeonShopReq.Parser.ParseFrom(body.Span)));
        routes.Map(ShopService.BatchBuyCommodity, "shops", async (session, msgId, body) =>
        {
            await shops.BatchBuyAsync(session, BatchBuyCommodityReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(ShopService.CommodityCollect, "shops", async (session, msgId, body) =>
        {
            await shops.CollectAsync(session, CommodityCollectReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(ShopService.CommodityPresent, "shops", async (session, msgId, body) =>
        {
            await shops.RefusePresentAsync(session, CommodityPresentReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(ShopService.CommodityGetInfo, "shops", async (session, msgId, body) =>
        {
            await shops.CommodityInfoAsync(session, CommodityGetInfoReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MailService.ClaimMail, "mail", async (session, msgId, body) =>
        {
            await mail.ClaimAsync(session, ClaimMailReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.Query, "auctions", async (session, msgId, body) =>
        {
            await auctions.QueryAsync(session, QueryAuctionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.Bid, "auctions", async (session, msgId, body) =>
        {
            await auctions.BidAsync(session, BiddingAuctionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.History, "auctions", async (session, msgId, body) =>
        {
            await auctions.HistoryAsync(session, QueryHistoryAuctionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.Self, "auctions", async (session, msgId, body) =>
        {
            await auctions.SelfAsync(session, QuerySelfAuctionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.QueryBonus, "auctions", async (session, msgId, body) =>
        {
            await auctions.QueryBonusAsync(session, QueryAuctionBonusReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.GetBonus, "auctions", async (session, msgId, body) =>
        {
            await auctions.GetBonusAsync(session, GetAuctionBonusReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.Get, "auctions", async (session, msgId, body) =>
        {
            await auctions.GetAsync(session, GetAuctionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.Cancel, "auctions", async (session, msgId, body) =>
        {
            await auctions.CancelAsync(session, CancelBiddingAuctionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.Quit, "auctions", async (session, msgId, body) =>
        {
            await auctions.QuitAsync(session, QuitAuctionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.BatchBid, "auctions", async (session, msgId, body) =>
        {
            await auctions.BatchBidAsync(session, BatchBiddingAuctionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.Resume, "auctions", async (session, msgId, body) =>
        {
            await auctions.ResumeAsync(session);
        });
        routes.Map(AuctionService.PreStack, "auctions", async (session, msgId, body) =>
        {
            await auctions.PreStackAsync(session, PreAuctionItemStackQueryReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.BatchCancel, "auctions", async (session, msgId, body) =>
        {
            await auctions.BatchCancelAsync(session, BatchCancelBiddingAuctionReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(AuctionService.Count, "auctions", async (session, msgId, body) =>
        {
            await auctions.CountAsync(session, QueryAuctionCountReq.Parser.ParseFrom(body.Span));
        });
    }
}
