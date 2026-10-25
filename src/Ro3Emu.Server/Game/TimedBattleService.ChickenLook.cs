using Romsg;
using Ro3Emu.Server.Data;
using static Ro3Emu.Server.Game.ChickenRules;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle unit custom data (Lua_CustomDataDefine.lua:69-106, handlers LC_CustomDataHandler_Chicken.lua and
/// Lua_CustomDataConfigExt.lua:668-790). They ride on the participant's own entity: kept in WorldState.CustomData, so
/// the AOI entity carries them to anyone who sees the player later, and pushed as 31018 to the room when they change.
/// All of them are cleared before the player leaves the room, so the town entity is the player's own again.
/// </summary>
public sealed partial class TimedBattleService
{
    /// <summary>Chicken_PlayerCareer: LC_UnitBase.TryChangeUnitConfigID / ChangeChickenCareer swap the model for
    /// EatChickenGameCareer._iUnitB / _iUnitG of that career (a value above 0).</summary>
    internal const int CustomCareer = 5000007;

    /// <summary>Chicken_HUDClothQuality: LC_HudElement_Chicken.SetClothQuality shows the quality icon of the worn
    /// clothes behind the name (0 = None draws White; no value hides it).</summary>
    internal const int CustomClothQuality = 5000006;

    /// <summary>ChickenOpenShop / ChickenOpenAdventure / ChickenOpenAmbulance: value 1 shows the head sign of a player
    /// busy in that panel (LC_CustomDataHandler_Chicken._ShowHeadSign), 0 hides it. The 82672 panel numbers are these ids.</summary>
    internal const int CustomShopSign = 5000008, CustomAdventureSign = 5000009, CustomAmbulanceSign = 5000010;

    /// <summary>EmojiInfos: [emojiId, sendTime in seconds], a head bubble for PublicParam BubbleTime seconds
    /// (LC_CustomDataHandler_Chicken.PlayChickenEmoji).</summary>
    internal const int CustomEmoji = 4000003;

    /// <summary>Unit_CameraBindUnitCustomData: the watched role, 0 = the own camera (LC_CustomDataComp_Base.ProcessCamera).
    /// ProcessCamera does not ask whose unit carries it, so it goes to the watcher alone (DirtyDataConfig 1002
    /// _iSyncScope 0, the scope of the self-only attributes in AttributeConfig) and never rides on the AOI entity.</summary>
    internal const int CustomCamera = 1002;

    private static readonly int[] ChickenCustomIds =
        [CustomCareer, CustomClothQuality, CustomShopSign, CustomAdventureSign, CustomAmbulanceSign, CustomEmoji];

    private const int ClothesSubType = 303; // enum_ChickenItemSubType.Clothes

    private static CustomData LongCustom(int id, long value) =>
        new() { Id = id, CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = value } };

    /// <summary>Stores the value on the player's entity and pushes 31018 to the players in the room (DirtyDataConfig
    /// gives these ids no _iSyncScope; inferred: everyone on the island, like the HUD vars). An unchanged value is not
    /// sent again.</summary>
    private async Task SetMatchCustomAsync(Match match, GameSession player, CustomData value)
    {
        if (player.Role == null || player.SceneLoading || !InRoom(match).Contains(player)) return;
        if (player.WorldState.CustomData.TryGetValue(value.Id, out var known) && known.Equals(value)) return;
        player.WorldState.CustomData[value.Id] = value.Clone();
        var change = new CustomDataChange { EntityId = player.Role.Id, CustomDatas = { value } };
        await RoleFanout.EachAsync(InRoom(match), p => p.SendGameAsync(WorldCoreService.CustomChange, change));
    }

    /// <summary>The career model and the clothes icon, after every change of the player's 5036 state.</summary>
    private async Task RefreshLookAsync(Match match, ChickenRun run, GameSession player)
    {
        if (!run.Players.TryGetValue(player, out var me) || me.Career == 0) return;
        await SetMatchCustomAsync(match, player, LongCustom(CustomCareer, me.Career));
        if (run.Stage < StageStartGame) return;
        long quality = 0;
        foreach (long guid in me.EquipBar.Values)
            if (me.Pack.TryGetValue(guid, out var item) && ItemRow(item.Entry) is { } row
                && new EatChickenItemConfig(row).SubType == ClothesSubType) quality = new EatChickenItemConfig(row).Quality;
        await SetMatchCustomAsync(match, player, LongCustom(CustomClothQuality, quality));
    }

    public const uint EmojiPlay = 83304;
    private const long DefaultBubbleSeconds = 3;
    private const int ErrTooFrequent = 1211; // as EmojiService

    /// <summary>
    /// 83304 from the wheel on the match HUD (UI_Sys_ExpressionRouletteHuditemWidgetCtrl.lua:194 -> LC_DAL_Chicken
    /// SendEmojiInScene): EmojiInfos [emojiId, now in seconds] on the player's entity, which every client in the room
    /// shows as a head bubble until sendTime + BubbleTime. Only an emote of the player's wheel, alive in the match;
    /// (inferred) no faster than one bubble's time. The response handler is empty.
    /// </summary>
    public async Task PlayEmojiAsync(GameSession session, int emojiId, Func<int, bool> onWheel)
    {
        int result = ErrorCodes.Parameter;
        if (Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken emoji", async () =>
            {
                if (emojiId == 0 || !onWheel(emojiId)) return;
                if (me.State != RoundRoleStatus.InWar || session.Dead) { result = ErrorCodes.FeatureUnavailable; return; }
                long now = NowMs() / 1000, bubble = PublicValue("BubbleTime") is var b and > 0 ? b : DefaultBubbleSeconds;
                if (now < me.EmojiAtSec + bubble) { result = ErrTooFrequent; return; }
                me.EmojiAtSec = now;
                var value = new CustomData
                {
                    Id = CustomEmoji,
                    CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.ListLong, ListLongParam = { emojiId, now } },
                };
                await SetMatchCustomAsync(match, session, value);
                result = 0;
            });
        await session.SendGameAsync(EmojiPlay, new DungeonModuleEmojiPlayResp { Result = result });
    }

    public const uint OpenPanel = 82672;

    /// <summary>
    /// 82672 when a match NPC panel opens or closes (the Kafra shop UI_Sys_Chicken_ShopNpcPanel/Widget, the Encounter
    /// panel UI_Sys_Chicken_AdventurePanel, the Rescue Wagon UI_Com_ChickenAmbulanceRevivalPanel; LC_DAL_Chicken
    /// SendChickenOpenPanel). panelNum is the custom data id of that panel's head sign, so the others see the player
    /// shopping (1) until it closes (0). The client ignores the response; it echoes the request.
    /// </summary>
    public async Task OpenPanelAsync(GameSession session, ChickenOpenPanelReq req)
    {
        int id = (int)req.PanelNum;
        if (id is CustomShopSign or CustomAdventureSign or CustomAmbulanceSign && Chicken(session) is (var match, var run, var me))
            await RunMatchTransitionAsync(match, "chicken panel", async () =>
            {
                if (match.Settled || req.IsOpen && (me.State != RoundRoleStatus.InWar || session.Dead)) return;
                await SetMatchCustomAsync(match, session, LongCustom(id, req.IsOpen ? 1 : 0));
            });
        await session.SendGameAsync(OpenPanel, new ChickenOpenPanelResp { Req = req });
    }

    /// <summary>A player who dies or leaves the fight closes its panels for the others (inferred).</summary>
    private async Task ClosePanelSignsAsync(Match match, GameSession player)
    {
        foreach (int id in new[] { CustomShopSign, CustomAdventureSign, CustomAmbulanceSign })
            if (player.WorldState.CustomData.TryGetValue(id, out var sign) && sign.CustomParam?.LongParam == 1)
                await SetMatchCustomAsync(match, player, LongCustom(id, 0));
    }

    /// <summary>Before the player leaves the room (no packet: the scene change drops the match units).</summary>
    private static void ClearChickenCustom(GameSession player)
    {
        foreach (int id in ChickenCustomIds) player.WorldState.CustomData.Remove(id);
    }
}
