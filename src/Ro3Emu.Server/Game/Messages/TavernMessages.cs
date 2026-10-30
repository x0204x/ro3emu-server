using Google.Protobuf;
using Romsg;
namespace Ro3Emu.Server.Game;
/// <summary>酒館房間由服務同時取得角色與房間 owner；未支援語音、禮物、管理功能明確拒絕。</summary>
public sealed class TavernMessages(TavernService tavern) : Messaging.IMessageModule
{
    public void Map(Messaging.MessageRoutes routes)
    {
        routes.AdmitRuntime(id => id is 82481 or 82482 or 82483 or 82508 or 82484 or 82486 or 82487 or 82488 or 82489 or 82490 or 82491 or 82492 or 82493 or 82494 or 82498 or 82499 or 82500 or 82501 or 82504 or 82505 or 82513 or 82514, "tavern/owned room");
        routes.Map(82481, "tavern", (s, id, body) => tavern.CreateAsync(s, CreateBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(82482, "tavern", (s, id, body) => tavern.EnterAsync(s, EnterBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(82483, "tavern", (s, id, body) => tavern.ExitAsync(s, ExitBarRoomReq.Parser.ParseFrom(body.Span)));
        routes.Map(82508, "tavern", (s, id, body) => tavern.GetAsync(s));
        routes.Map(82484, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new InviteEnterBarRoomResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82486, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new ResponseInviteEnterBarRoomResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82487, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new ApplyOpenMicResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82488, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new DownMicResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82489, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new TransferOwnerResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82490, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new ModifyControlMicTypeResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82491, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new PresentGiftResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82492, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new OwnerHandleOpenMicResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82493, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new SetMicOrderResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82494, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new SetBarRoomInfoResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82498, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new ForbiddenUserResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82499, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new CanecelForbiddenUserResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82500, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new KickUserResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82501, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new CancelKickUserResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82504, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new ShutUpAllResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82505, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new CancelShutUpAllResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82513, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new SetGmBarRoomInfoResp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(82514, "tavern/refused", (s, id, body) => s.SendGameAsync(id, new MuteBarRoomPlayerResp { Result = ErrorCodes.FeatureUnavailable }));
    }
}
