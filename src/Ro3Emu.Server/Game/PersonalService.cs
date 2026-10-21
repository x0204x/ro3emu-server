using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Profile card (function 63, Profile, base level 20): AUTO_SYNC 5016 DBPersonalInfo and the editor's 82553 appearance,
/// 82559 current preset and 82560 preset name. LC_DAL_Personal._processPersonalInfo (lua-src Logic/Asset/LC_DAL_Personal.lua:64-241)
/// projects 5016 into PersonalUnlockInfo (unlockInfos), PersonalUpvote (upvoteTimes), PersonalPlanInfo (curPlanId, the
/// presets' names and backgrounds) and PersonalPlayerInfo (planInfos[curPlanId]); the 82553 reply handler is empty (:456-459)
/// and 82559's only raises PersonalChangePBOver (:649-662), so every change reaches the client through 5016. The editor
/// (UI_Sys_PersonalPanelModel.ApplyEditChange, the sticker panel, the friend window's signature and mood) sends the whole
/// preset back in 82553. Other players read the current preset as UserDataService's "Personal" field
/// (LC_DAL_RolePublic.lua:874-900) and its avatar, frame and title through Base.
/// <para>Stored: presets, current preset, upvote times and the 620 item unlocks (role blob "personal"). The rest of
/// unlockInfos is derived each time from the unlock conditions of the tables the editor checks (theme, background,
/// action). The likes a card received are the role blob "personal_upvote" (PersonalUpvoteList), its popularity the role blob "popularity" (DBPopularityStar).</para>
/// </summary>
public sealed class PersonalService
{
    public const uint Sync = 5016, UpdateAppearance = 82553, Upvote = 82554, UpdateCurPlan = 82559, UpdatePlanName = 82560;
    // ErrorCodeConfig 3351-3367, the profile family.
    public const int ErrConfig = 3351, ErrParameter = 3352, ErrFunction = 3354, ErrLocked = 3357, ErrStickers = 3358,
        ErrUnlocked = 3356, ErrUpvoted = 3359, ErrUpvoteSelf = 3361, ErrUpvoteCooldown = 3362,
        ErrPreset = 3363, ErrSignLength = 3364, ErrSignSensitive = 3365, ErrNameLength = 3366, ErrNameSensitive = 3367;
    // The gift panel's effect ids (UI_Sys_Friend_GiftToFriendsPanelCtrl: Intimacy 107, TotalPopularity 108, Charity 109).
    private const int EffectPopularity = 108, EffectCharity = 109;
    private const int Function = 63;
    // enum_PersonalUnlockType (Lua_GameDefine.lua:13645-13660); the editor's lists check Theme, BackGround and Action
    // (UI_Sys_Personal_EditTheme/EditBg/EditRoleAction_ListWidgetCtrl via LC_PersonalHelper.CheckItemIsOpen).
    private const int UnlockTheme = 4, UnlockBackground = 5, UnlockAction = 6;
    // PersonalHomepageImageConfig._iImageType = enum_PersonalImageType: 1 theme, 2 background, 3 mood.
    private const int ImageTheme = 1, ImageBackground = 2, ImageMood = 3;
    // EPersonalShowcaseType (PBEnumDefine.lua:1905-1915): 1 card, 2 page (book), 3 honor.
    private const int ShowcaseCard = 1, ShowcasePage = 2, ShowcaseHonor = 3;

    /// <summary>
    /// (inferred) Three presets, ids 1-3: LC_DAL_Personal._getPlanName names only 1-3 (UIWords PersonalPlanName1-3) and
    /// the preset list walks PersonalPlanInfo.m_kPlanInfo with ipairs (UI_Sys_Personal_EditStencilWidgetCtrl._refreshPlanInfo),
    /// so the ids are contiguous from 1.
    /// </summary>
    public const int Presets = 3;

    private readonly GameData _data;
    private readonly RoleStore _roles;
    private readonly FunctionService _functions;
    private readonly CharacterService _character;
    private readonly IOptions<ServerOptions> _options;
    private readonly TimeProvider _clock;

    public PersonalService(GameData data, RoleStore roles, FunctionService functions, CharacterService character,
        IOptions<ServerOptions> options, TimeProvider clock)
    {
        this._data = data; this._roles = roles; this._functions = functions; this._character = character; this._options = options;
        this._clock = clock;
        // Action 1002 unlocks at base level 50 (PersonalPerformanceConfig, condition 18000001).
        character.ProgressHandlers.Add(RefreshAsync);
        character.RegisterItemAction(UnlockDecorationAction, UseDecorationItemAsync);
    }

    // ------------------------------------------------------------------ load / 5016

    public async Task LoginAsync(GameSession s)
    {
        var bytes = _roles.LoadPersonal(s.Role!.Id);
        var info = bytes is { Length: > 0 } ? DBPersonalInfo.Parser.ParseFrom(bytes) : new DBPersonalInfo();
        Shape(info);
        s.Personal = info;
        await SyncAsync(s);
    }

    /// <summary>
    /// 5016. The full handler walks unlockInfos, each unlockMap, upvoteTimes and planInfos with pairs() unguarded
    /// (LC_DAL_Personal.lua:83-98,144-148,182-204), and lua-protobuf (no_decode_default_array, NW_GameServerHandler.lua:343-345)
    /// decodes an empty map as nil. Unlocks and presets are never empty; a role that upvoted nobody gets a wire-only
    /// upvoteTimes key 0, deleted by the next increment (ParseMapTypeIncrement creates the map before the del), as
    /// SeasonRefineService seeds its empty maps (inferred, PersonalUpvote keeps the harmless roleId 0 entry).
    /// </summary>
    public async Task SyncAsync(GameSession s)
    {
        if (s.Personal == null) return;
        var now = Build(s);
        if (!s.Synced.ContainsKey(Sync) && now.UpvoteTimes.Count == 0)
        {
            var wire = now.Clone();
            wire.UpvoteTimes[0] = 0;
            await CharacterService.SyncAsync(s, Sync, wire);
        }
        await CharacterService.SyncAsync(s, Sync, now);
    }

    /// <summary>1023 replay: a full 5016 again, with the same empty-map seed.</summary>
    public Task ReplayAsync(GameSession s)
    {
        s.Synced.Remove(Sync);
        return SyncAsync(s);
    }

    private Task RefreshAsync(GameSession s) => s.Personal != null && s.Synced.ContainsKey(Sync) ? SyncAsync(s) : Task.CompletedTask;

    private DBPersonalInfo Build(GameSession s)
    {
        var info = s.Personal!.Clone();
        foreach (var (type, ids) in Unlocks(s))
        {
            var unlock = new DBPersonalUnlockInfo();
            foreach (int id in ids) unlock.UnlockMap[id] = true;
            if (unlock.UnlockMap.Count > 0) info.UnlockInfos[type] = unlock;
        }
        return info;
    }

    /// <summary>
    /// The unlocked theme, background and action ids. A row with an _iUnlockCondition is unlocked when the condition holds
    /// (all are base level conditions: [1002,1] for the default theme, background and idle action, [1002,50] for walk).
    /// A row without one (0) is obtained elsewhere (_kImageObtain: the Fashion Shop, Growth Support rewards): a 620 item
    /// unlocks it for good (UseDecorationItemAsync), the shops do not sell it yet (inferred).
    /// </summary>
    private IEnumerable<(int Type, List<int> Ids)> Unlocks(GameSession s)
    {
        var images = _data.Table("PersonalHomepageImageConfig").Values;
        yield return (UnlockTheme, images.Where(r => GameData.Int(r, "_iImageType") == ImageTheme
            && (Unlocked(s, r) || ItemUnlocked(s, UnlockTheme, Id(r)))).Select(Id).ToList());
        yield return (UnlockBackground, images.Where(r => GameData.Int(r, "_iImageType") == ImageBackground
            && (Unlocked(s, r) || ItemUnlocked(s, UnlockBackground, Id(r)))).Select(Id).ToList());
        yield return (UnlockAction, _data.Table("PersonalPerformanceConfig").Values
            .Where(r => Unlocked(s, r) || ItemUnlocked(s, UnlockAction, Id(r))).Select(Id).ToList());
    }

    private static int Id(JsonElement row) => (int)GameData.Int(row, "_iID");

    private bool Unlocked(GameSession s, JsonElement row) =>
        GameData.Int(row, "_iUnlockCondition") is var condition and > 0 && s.Progress != null && _functions.ConditionMet(s, condition);

    private bool IsUnlocked(GameSession s, int type, int id) => Unlocks(s).Any(u => u.Type == type && u.Ids.Contains(id));

    /// <summary>Every preset present, the current one valid: a missing preset is the default card.</summary>
    private void Shape(DBPersonalInfo info)
    {
        for (int plan = 1; plan <= Presets; plan++)
            if (!info.PlanInfos.ContainsKey(plan)) info.PlanInfos[plan] = DefaultPlan();
        if (info.CurPlanId is < 1 or > Presets) info.CurPlanId = 1;
    }

    /// <summary>
    /// The server-side defaults of PublicParamConfig, which the client never reads (inferred to be the original server's):
    /// PersonalDefaultTheme 1001, PersonalDefaultBackground 2001, PersonalEmoEmpty 3001 (the "no mood" image) and
    /// PersonalShowcaseDefault [6301]. 6301 is FunctionConfig "Profile Card Album Display", the card showcase.
    /// </summary>
    private PersonalInfo DefaultPlan()
    {
        var plan = new PersonalInfo
        {
            ThemeId = (int)Param("PersonalDefaultTheme", 1001), BackgroundId = (int)Param("PersonalDefaultBackground", 2001),
            Mood = (int)Param("PersonalEmoEmpty", 3001),
        };
        foreach (long function in ParamList("PersonalShowcaseDefault"))
            if (ShowcaseOf(function) is { } showcase && !plan.ShowcaseVisibleList.Contains(showcase)) plan.ShowcaseVisibleList.Add(showcase);
        return plan;
    }

    /// <summary>LC_DAL_Personal.FunctionTypeToEPersonalShowcaseType (:845-870), plus the 630x display functions (inferred).</summary>
    private static int? ShowcaseOf(long function) => function switch
    {
        6301 or 630331 => ShowcaseCard,
        6302 or 630332 => ShowcasePage,
        6307 => ShowcaseHonor,
        _ => null,
    };

    private double Param(string key, double fallback) =>
        _data.Table("PublicParamConfig").TryGetValue(key, out var value) && GameData.Number(value) is { } number ? number : fallback;

    private IEnumerable<long> ParamList(string key) =>
        _data.Table("PublicParamConfig").TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).Where(v => v > 0) : [];

    // ------------------------------------------------------------------ public card

    /// <summary>The current preset, for Base (avatar, frame, title) and the "Personal" field of others' view.</summary>
    public static PersonalInfo? Current(DBPersonalInfo? info) =>
        info != null && info.PlanInfos.TryGetValue(info.CurPlanId, out var plan) ? plan : null;

    /// <summary>
    /// Whether the client can parse the card: LC_DAL_Personal.ParsePersonalInfo walks decalInfos and showcaseVisibleList with
    /// pairs() unguarded (:724-747), and an empty repeated field decodes as nil. A card without stickers or showcases
    /// would abort LC_DAL_RolePublic's whole public-card parse (:874-900, before Library, GuildSnap and Team), so it is
    /// left out of the public data. The own 5016 has no such choice: an unpatched client builds no PersonalPlayerInfo for
    /// it; Ro3Emu.ClientHooks.PersonalCards reads the missing lists as empty. Kept until that patch is proven in game.
    /// </summary>
    public static bool Parseable(PersonalInfo plan) => plan.DecalInfos.Count > 0 && plan.ShowcaseVisibleList.Count > 0;

    // ------------------------------------------------------------------ 82553 / 82559 / 82560

    /// <summary>
    /// 82553: the whole preset as the editor holds it. Checked against what the editor itself enforces (unlocked theme,
    /// background and actions; PersonalMaxSticker 20 stickers, as many of one sticker as owned, LC_ItemHelper.GetItemCount;
    /// PersonalMaxCabinet 3 showcases; PersonalMaxCharacter 1 figure; PersonalSignatureLimit 30). The preset name is kept:
    /// the editor sends the localized default name for an unnamed preset, and 82560 renames (inferred).
    /// </summary>
    public async Task UpdateAppearanceAsync(GameSession s, UpdatePersonalAppearanceReq req)
    {
        using var held = await CharacterService.Lock(s);
        int result = Gate(s);
        if (result == 0 && !Preset(req.PlanId)) result = ErrPreset;
        if (result == 0 && req.Info == null) result = ErrParameter;
        if (result == 0) result = Validate(s, req.Info!);
        if (result == 0)
        {
            var next = s.Personal!.Clone();
            var plan = req.Info!.Clone();
            plan.PlanName = next.PlanInfos[req.PlanId].PlanName;
            next.PlanInfos[req.PlanId] = plan;
            await CommitAsync(s, next, "appearance");
        }
        await s.SendGameAsync(UpdateAppearance, new UpdatePersonalAppearanceResp { Result = result, Req = req });
        if (result == 0) await SyncAsync(s);
    }

    /// <summary>82559: the shown preset. The reply echoes planId; the client only raises PersonalChangePBOver.</summary>
    public async Task UpdateCurPlanAsync(GameSession s, UpdateCurPersonalPlanReq req)
    {
        using var held = await CharacterService.Lock(s);
        int result = Gate(s);
        if (result == 0 && !Preset(req.PlanId)) result = ErrPreset;
        if (result == 0 && s.Personal!.CurPlanId != req.PlanId)
        {
            var next = s.Personal.Clone();
            next.CurPlanId = req.PlanId;
            await CommitAsync(s, next, "current");
        }
        await s.SendGameAsync(UpdateCurPlan, new UpdateCurPersonalPlanResp { Result = result, PlanId = req.PlanId });
        if (result == 0) await SyncAsync(s);
    }

    /// <summary>
    /// 82560: rename a preset. UI_Com_InputContentPanel caps the input at PersonalTemplateLimit (7) and refuses an empty
    /// one (UI_Sys_Personal_EditStencil_ListWidgetCtrl._onEditClick); LC_DAL_Personal registers no reply handler, the
    /// name arrives with 5016 (PersonalPlanInfo.kPlanName).
    /// </summary>
    public async Task UpdatePlanNameAsync(GameSession s, UpdatePersonalPlanNameReq req)
    {
        using var held = await CharacterService.Lock(s);
        string name = req.PlanName ?? "";
        int length = new StringInfo(name).LengthInTextElements;
        int result = Gate(s);
        if (result == 0 && !Preset(req.PlanId)) result = ErrPreset;
        if (result == 0 && (name.Trim().Length == 0 || length > Param("PersonalTemplateLimit", 7))) result = ErrNameLength;
        if (result == 0 && Sensitive(name)) result = ErrNameSensitive;
        if (result == 0 && s.Personal!.PlanInfos[req.PlanId].PlanName != name)
        {
            var next = s.Personal.Clone();
            next.PlanInfos[req.PlanId].PlanName = name;
            await CommitAsync(s, next, "name");
        }
        await s.SendGameAsync(UpdatePlanName, new UpdatePersonalPlanNameResp { Result = result, Req = req });
        if (result == 0) await SyncAsync(s);
    }

    // ------------------------------------------------------------------ 620 item unlocks

    internal const int UnlockDecorationAction = 620;

    /// <summary>A theme, background or action a 620 item unlocked: stored in DBPersonalInfo.unlockInfos.</summary>
    private static bool ItemUnlocked(GameSession s, int type, int id) =>
        s.Personal?.UnlockInfos.TryGetValue(type, out var unlocks) == true && unlocks.UnlockMap.GetValueOrDefault(id);

    /// <summary>
    /// 620 UnlockPersonalDecoration [620, enum_PersonalUnlockType, id]: 2120001..2120003 [620,4,1003..1005] a card theme,
    /// 2110001/2110002 [620,5,2003/2004] a background, the test card 1960003 [620,6,50004] an action. The themes and
    /// backgrounds they name are the PersonalHomepageImageConfig rows without an _iUnlockCondition (_kImageObtain [3001,
    /// 3004]), which nothing else unlocks. The unlock is kept in unlockInfos (5016, LC_DAL_Personal PersonalUnlockInfo, the
    /// editor's lock via LC_PersonalHelper.CheckItemIsOpen) and the item goes in the same commit. An id without its
    /// config row (50004) is ErrConfig and an id already open is ErrUnlocked "Item unlocked." (inferred: the profile
    /// family's own code), both keeping the item.
    /// </summary>
    private async Task<int> UseDecorationItemAsync(ItemActionUse use)
    {
        var s = use.Session;
        if (s.Personal == null || s.Progress == null) return ErrorCodes.FeatureUnavailable;
        var picks = use.Effects.Select(e => (Type: e.Length > 1 ? (int)e[1] : 0, Id: e.Length > 2 ? (int)e[2] : 0)).ToList();
        foreach (var (type, id) in picks)
        {
            var row = type switch
            {
                UnlockTheme or UnlockBackground => _data.Row("PersonalHomepageImageConfig", id) is { } image
                    && GameData.Int(image, "_iImageType") == (type == UnlockTheme ? ImageTheme : ImageBackground) ? image : (JsonElement?)null,
                UnlockAction => _data.Row("PersonalPerformanceConfig", id),
                _ => null,
            };
            if (row == null) return ErrConfig;
        }
        if (picks.All(p => IsUnlocked(s, p.Type, p.Id))) return ErrUnlocked;
        var next = s.Personal.Clone();
        foreach (var (type, id) in picks)
        {
            if (!next.UnlockInfos.TryGetValue(type, out var unlocks)) next.UnlockInfos[type] = unlocks = new DBPersonalUnlockInfo();
            unlocks.UnlockMap[id] = true;
        }
        byte[] saved = next.ToByteArray();
        if (!await _character.CommitItemActionAsync(use, tx => tx.RoleBlob(s.Role!.Id, "personal", saved), () => s.Personal = next,
            new { Personal = picks.Select(p => new[] { p.Type, p.Id }).ToArray() })) return ErrorCodes.ItemsShort;
        await SyncAsync(s);
        return 0;
    }

    // ------------------------------------------------------------------ 82554 likes

    /// <summary>
    /// 82554: like another role's card (the heart, UI_Sys_PersonalPanelCtrl._onLickBtnClick). LC_DAL_Personal.SendPersonalUpvote
    /// sends only roleId, at most once per PersonalLikeCD (1000 ms, else UIWords 62038). The reply handler stores
    /// PersonalUpvote[req.roleId] = isCancle and raises PersonalUpvoteInfoChange, which asks again for the target's
    /// PersonalUpvoteCount (LC_System_PlayerInfo._onUpvoteValChange). The heart is lit from PersonalUpvote[target]
    /// (UI_Sys_PersonalPanelModel.GetDataFormAsset), which neither 5016 nor the reply ever clears, so the client cannot
    /// show a like taken back: (inferred) a like is kept for good, a second one is ErrUpvoted ("Liked.") and isCancle
    /// is false; the 5016 increment after the reply turns that false into the like's time. ErrUpvoteCooldown is the
    /// client's PersonalLikeCD between two likes, checked again here (inferred). No daily reset: nothing in the client
    /// re-arms a like. The liked role gets no notice; its card shows the count and Like History (UserDataService).
    /// </summary>
    /// <summary>After a committed like (quest target 106902); listeners post their work.</summary>
    public List<Action<GameSession>> Upvoted { get; } = new();

    public async Task UpvoteAsync(GameSession s, UpvotePersonalReq req)
    {
        using var held = await CharacterService.Lock(s);
        long target = req.RoleId, now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        int result = Gate(s);
        if (result == 0 && target == s.Role!.Id) result = ErrUpvoteSelf;
        if (result == 0 && (target <= 0 || await _roles.GetAsync(target) == null
            || (await _roles.ReadRevisionVectorAsync(target)).Deleted)) result = ErrParameter;
        if (result == 0 && s.Personal!.UpvoteTimes.ContainsKey(target)) result = ErrUpvoted;
        if (result == 0 && s.Personal!.UpvoteTimes.Count > 0
            && now - s.Personal.UpvoteTimes.Values.Max() < Param("PersonalLikeCD", 1000)) result = ErrUpvoteCooldown;
        if (result == 0)
        {
            var next = s.Personal!.Clone();
            next.UpvoteTimes[target] = now;
            var transaction = _roles.NewTransaction();
            var liked = _roles.LoadPersonalUpvotes(target, transaction) is { Length: > 0 } stored
                ? PersonalUpvoteList.Parser.ParseFrom(stored) : new PersonalUpvoteList();
            liked.Infos[s.Role!.Id] = now;
            transaction.RoleBlob(s.Role.Id, "personal", next.ToByteArray());
            _roles.SavePersonalUpvotes(target, liked.ToByteArray(), transaction);
            await _roles.CommitAsync(transaction, "personal/upvote", _ => s.Personal = next);
        }
        await s.SendGameAsync(Upvote, new UpvotePersonalResp { Result = result, Req = req });
        if (result == 0) await SyncAsync(s);
        if (result == 0) foreach (var handler in Upvoted) handler(s);
    }

    /// <summary>The likes a card received (liker id to unix ms), as stored; null when none.</summary>
    public static PersonalUpvoteList? Upvotes(byte[]? stored) =>
        stored is { Length: > 0 } && PersonalUpvoteList.Parser.ParseFrom(stored) is { Infos.Count: > 0 } list ? list : null;

    // ------------------------------------------------------------------ popularity

    /// <summary>
    /// Popularity comes from friend gifts. The gift panel (UI_Sys_Friend_GiftToFriendsPanelCtrl._changeGiftShowInfo) adds the
    /// item's _iUseEffect 108 (TotalPopularity) times the count to the receiver's weekly and total popularity, read from
    /// PopularityStar; a received record shows "Popularity: +n" (record.popularity), a sent one "Affection: +n" from 109
    /// (Charity, record.affection; UI_Sys_Friend_GiftRecordListWidgetCtrl._initView). Likes add none: the client links them
    /// nowhere to popularity.
    /// </summary>
    public (int Popularity, int Affection) GiftValues(long item, int count)
    {
        if (_data.Row("ItemConfig", item) is not { } row) return (0, 0);
        var effects = GameData.Pairs(row, "_iUseEffect").ToList();
        int Total(int effect) => (int)Math.Min(int.MaxValue, effects.Where(e => e.Id == effect).Sum(e => (long)e.Value * count));
        return (Total(EffectPopularity), Total(EffectCharity));
    }

    /// <summary>Credits a committed gift: the receiver's popularity, the sender's affection.</summary>
    public async Task CreditGiftAsync(long sender, long receiver, int popularity, int affection)
    {
        if (popularity <= 0 && affection <= 0) return;
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds(), week = WeekStartMs(_clock);
        var transaction = _roles.NewTransaction();
        if (popularity > 0)
        {
            var star = Popularity(_roles.LoadPopularity(receiver, transaction), week);
            star.Popularity = Add(star.Popularity, popularity);
            star.PopularityWeekly = Add(star.PopularityWeekly, popularity);
            star.UpdateTime = now;
            _roles.SavePopularity(receiver, star.ToByteArray(), transaction);
        }
        if (affection > 0)
        {
            var star = Popularity(_roles.LoadPopularity(sender, transaction), week);
            star.Affection = Add(star.Affection, affection);
            star.UpdateTime = now;
            _roles.SavePopularity(sender, star.ToByteArray(), transaction);
        }
        await _roles.CommitAsync(transaction, "personal/popularity");
    }

    private static int Add(int value, int delta) => (int)Math.Min(int.MaxValue, (long)value + delta);

    /// <summary>A stored DBPopularityStar, its weekly popularity zero when last changed before this week.</summary>
    public static DBPopularityStar Popularity(byte[]? stored, long weekStartMs)
    {
        var star = stored is { Length: > 0 } ? DBPopularityStar.Parser.ParseFrom(stored) : new DBPopularityStar();
        if (star.UpdateTime < weekStartMs) star.PopularityWeekly = 0;
        return star;
    }

    /// <summary>(inferred) The popularity week starts on Monday 05:00 realm time, as the guild activity week.</summary>
    public static long WeekStartMs(TimeProvider clock)
    {
        var local = clock.GetLocalNow();
        var day = local.AddHours(-5).Date;
        int sinceMonday = ((int)day.DayOfWeek + 6) % 7;
        return new DateTimeOffset(day.AddDays(-sinceMonday).AddHours(5), local.Offset).ToUnixTimeMilliseconds();
    }

    private int Gate(GameSession s) => s.Personal == null || s.Progress == null ? ErrorCodes.FeatureUnavailable
        : !_functions.IsOpen(s, Function) ? ErrFunction : 0;

    private static bool Preset(int plan) => plan is >= 1 and <= Presets;

    private int Validate(GameSession s, PersonalInfo info)
    {
        if (!IsUnlocked(s, UnlockTheme, info.ThemeId) || !IsUnlocked(s, UnlockBackground, info.BackgroundId)) return ErrLocked;
        // Mood: a PersonalHomepageImageConfig mood image whose condition holds (all are [1002,1]); 0 is no mood.
        if (info.Mood != 0 && (_data.Row("PersonalHomepageImageConfig", info.Mood) is not { } mood
            || GameData.Int(mood, "_iImageType") != ImageMood)) return ErrConfig;
        if (info.Mood != 0 && !Unlocked(s, _data.Row("PersonalHomepageImageConfig", info.Mood)!.Value)) return ErrLocked;
        if (info.AvatarId != 0 && _data.Row("PersonalAvatarConfig", info.AvatarId) == null) return ErrConfig;
        if (info.AvatarFrameId != 0 && _data.Row("PersonalAvatarFrameConfig", info.AvatarFrameId) == null) return ErrConfig;
        // PersonalTitleConfig exports no rows, so no title can be chosen.
        if (info.PersonalTitleId != 0 && _data.Row("PersonalTitleConfig", info.PersonalTitleId) == null) return ErrConfig;
        if (new StringInfo(info.Sign ?? "").LengthInTextElements > Param("PersonalSignatureLimit", 30)) return ErrSignLength;
        if (Sensitive(info.Sign ?? "")) return ErrSignSensitive;
        if (info.ShowcaseVisibleList.Count > Param("PersonalMaxCabinet", 3)
            || info.ShowcaseVisibleList.Any(v => v is < ShowcaseCard or > ShowcaseHonor)
            || info.ShowcaseVisibleList.Distinct().Count() != info.ShowcaseVisibleList.Count) return ErrParameter;
        if (info.RoleInfos.Count > Param("PersonalMaxCharacter", 1)) return ErrParameter;
        // The pose list is PerformanceConfig (UI_Sys_Personal_EditRoleAction_ListWidgetCtrl reads m_kCFG_PerformanceConfig),
        // so a pose is one of the role's unlocked emotes (5006 emojiUnlocks); the client preselects one (50001) unasked.
        if (info.RoleInfos.Any(r => r.ActionId != 0 && s.EmojiPlay?.EmojiUnlocks.Contains(r.ActionId) != true
            && !IsUnlocked(s, UnlockAction, r.ActionId))) return ErrLocked;
        if (info.DecalInfos.Count > Param("PersonalMaxSticker", 20)) return ErrParameter;
        // Stickers are currencies of item subtype 123 (ItemSubTypePersonalDecal); each placement uses one.
        foreach (var group in info.DecalInfos.GroupBy(d => d.DecalId))
        {
            if (_data.Row("ItemConfig", group.Key) is not { } item || GameData.Int(item, "_iSubType") != 123) return ErrConfig;
            if (_character.CountOf(s, group.Key) < group.Count()) return ErrStickers;
        }
        return 0;
    }

    private bool Sensitive(string text) => _options.Value.GuildBlockedWords.Any(word =>
        !string.IsNullOrWhiteSpace(word) && text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private async Task CommitAsync(GameSession s, DBPersonalInfo next, string action)
    {
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(s.Role!.Id, "personal", next.ToByteArray());
        await _roles.CommitAsync(transaction, "personal/" + action, _ => s.Personal = next);
    }
}
