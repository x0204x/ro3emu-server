using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Adventure Group / "Adv. Guild" (FunctionConfig 1000000; MG_AdventureGroup 34000-34024; AUTO_SYNC 5068
/// DBAdventureGroupRelationData). The client never asks for its own group: everything it shows comes from the 34011 full
/// push (LC_DAL_AdventureGroup._processAdventureFullDataRes, "MyAdventureGroup"), the 34012 change pushes and 5068; most
/// responses only show an error tip (LC_DAL_AdventureGroup.lua:234-2455).
/// <list type="bullet">
/// <item>Support recruitment (status 1 Helper): a leader posts a recruit (34000 CreateType_Recruit), players respond
/// (34005 Ope_Helper_Apply), the leader accepts; with at least 3 members the leader starts the creation (34013/34006),
/// which invites the responders into a party and paths to the Adventure Guild Mentor (NPC 200011, map 10100).</item>
/// <item>NPC creation (status 2 Temp): a party leader with 3+ nearby members names the group at the mentor
/// (34000 CreateType_Npc).</item>
/// <item>The creation ritual (34014): the party enters dungeon 1010001 (PublicParam AdventureGroupDungeonSceneId), where
/// quests 2000500-2000502 (AdventureGroupSwornTask) run: the leader hangs the declaration on a sapling (34015), every
/// member waters it (34021), every member makes the declaration at NPC 200042 (34016). Then the group is Normal (3).</item>
/// <item>A normal group: applications (34009 Ope_Apply, max AdventureGroupApplyNormalLimit per player), invites through
/// a chat token, kick / leave / owner transfer, nicknames, daily watering of the group's sapling in Prontera (tree growth),
/// group EXP from the Adventure Bond Points (item 130) of the group quests, levels (AdventureGroupLevelAwardConfig) and
/// their once-per-role level rewards, rallies (34017-34019), the event log (34020).</item>
/// </list>
/// Server rules the client cannot prove are marked (inferred). One realm: every member's bornAreaId is this cluster, so
/// the server-transfer deadline (AdventureGroupServerTransferContinueTime) never applies.
/// All routes take the default Realm reservation (MessageRoutes.Admit): a group operation touches several roles.
/// </summary>
public sealed partial class AdventureGroupService : IGameTicker
{
    public const uint Create = 34000, Delete = 34001, HelperList = 34002, HelperApplicants = 34004, HelperOpe = 34005,
        StartBrothers = 34006, Edit = 34007, NormalList = 34008, NormalOpe = 34009, MemberData = 34010, FullData = 34011,
        ChangeNotify = 34012, CheckBrothers = 34013, EnterRitual = 34014, SelectSapling = 34015, Declare = 34016,
        Convene = 34017, ConveneNotify = 34018, ConfirmConvene = 34019, Commemorate = 34020, Water = 34021,
        SaplingData = 34022, Detail = 34023, CreatedNotify = 34024, RelationSync = 5068;

    /// <summary>FunctionConfig 1000000 (enum_FunctionType.AdventureGroup).</summary>
    public const long Function = 1000000;

    public const int StatusHelper = 1, StatusTemp = 2, StatusNormal = 3;

    // ErrorCodeConfig 10200-10249 (Localization_en 10630001267..10630001316). Which failure maps to which code is
    // read from the texts (inferred).
    internal const int ErrJoined = 10200, ErrCreating = 10201, ErrMaxGroups = 10202, ErrNameTaken = 10203,
        ErrNameInvalid = 10204, ErrDeclaration = 10205, ErrNotInGroup = 10206, ErrPermission = 10207,
        ErrNotHelperGroup = 10208, ErrApplied = 10209, ErrApplyLimit = 10210, ErrFull = 10211, ErrInGroup = 10212,
        ErrNoApplication = 10213, ErrOwnerCooldown = 10214, ErrFewMembers = 10216, ErrNotNearby = 10217,
        ErrOtherGroup = 10218, ErrNotInDungeon = 10219, ErrWrongSapling = 10220, ErrWatered = 10221, ErrNoGroup = 10222,
        ErrNoTask = 10223, ErrNoSaplingMember = 10224, ErrNoSaplingOwner = 10225, ErrCooldown = 10226,
        ErrFriendsOnly = 10227, ErrInviteExpired = 10228, ErrNoInvite = 10229, ErrNoRally = 10231,
        ErrRallyCooldown = 10232, ErrNameEmpty = 10234, ErrAnnounceEmpty = 10235, ErrRecruitExpired = 10236,
        ErrHelperLimit = 10237, ErrRallyArea = 10238, ErrRallyBattle = 10239, ErrClaimed = 10240, ErrLevel = 10241,
        ErrAcceptLimit = 10242, ErrRallyStartArea = 10243, ErrRallyAcceptArea = 10244, ErrSaplingTaken = 10246,
        ErrBanned = 10248, ErrTeamLocked = 10249;

    /// <summary>Adventure Bond Points (ItemConfig 130, "Adventure Party EXP obtained by completing Adventure Party
    /// quests. Increases the Adventure Party's level.").</summary>
    public const int BondPoints = 130;

    /// <summary>LC_AdventureGroupHelper.tSaplingNpcIds (HLP:49-69) and the oath NPC (iDungeonOathNpc, HLP:92-97).</summary>
    internal static readonly int[] SaplingNpcs = [200032, 200033, 200034, 200035, 200036, 200037, 200038, 200039, 200040, 200041];
    internal const int OathNpc = 200042;

    private readonly GameData _data;
    private readonly AdventureGroupStore _store;
    private readonly RoleStore _roles;
    private readonly FriendStore _friends;
    private readonly SessionRegistry _sessions;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly AwardService _awards;
    private readonly TaskService _tasks;
    private readonly DailyActivityService _daily;
    private readonly TeamService _teams;
    private readonly InstanceService _instances;
    private readonly SceneService _scene;
    private readonly GameLoop _loop;
    private readonly TimeProvider _clock;
    private readonly IOptions<ServerOptions> _options;
    private readonly ILogger<AdventureGroupService> _log;

    public AdventureGroupService(GameData data, AdventureGroupStore store, RoleStore roles, FriendStore friends,
        SessionRegistry sessions, CharacterService character, FunctionService functions, AwardService awards,
        TaskService tasks, DailyActivityService daily, TeamService teams, InstanceService instances, SceneService scene,
        Maps.WorldData world, GmCommands gm, GameLoop loop, TimeProvider clock, IOptions<ServerOptions> options,
        ILogger<AdventureGroupService> log)
    {
        this._data = data; this._store = store; this._roles = roles; this._friends = friends; this._sessions = sessions;
        this._character = character; this._functions = functions; this._awards = awards; this._tasks = tasks;
        this._daily = daily; this._teams = teams; this._instances = instances; this._scene = scene; this._loop = loop;
        this._clock = clock; this._options = options; this._log = log;
        character.RegisterItemAction(UnlockBackgroundAction, UseBackgroundItemAsync, keys: _ => [Ownership.ReservationKey.Realm]);
        // The creation instance's saplings and mentor: scene-editor monster points 26..36 of sc_prontera_001_dg02 carry
        // npcId 0 (HLP:70-97 tSaplingToMonsterAreaMap / iDungeonOathNpc), so the server places them.
        world.PlacePointNpcs(DungeonScene, SaplingNpcs.Select((npc, i) => (npc, 26 + i)).Append((OathNpc, 36)).ToArray());
        // Group quests (TaskConfig type 23, conditions 88000081-85 = level 999 in this export): a normal group's members.
        functions.ConditionOverride = GroupQuestCondition;
        tasks.QuestDelivered.Add(OnTaskDelivered);
        scene.Loaded.Add(OnSceneLoadedAsync);
        gm.Register("advguild", "create <name> | add <role name> | exp <n> | water | info | disband (Adventure Group test aid)", GmAsync);
    }

    // ---------------------------------------------------------------- config

    private JsonElement? Param(string key) => _data.Table("PublicParamConfig").TryGetValue(key, out var value) ? value : null;
    private long Number(string key, long fallback) => Param(key) is { } v && GameData.Number(v) is { } n ? (long)n : fallback;
    private (long Min, long Max) Range(string key, long min, long max) =>
        Param(key) is { ValueKind: JsonValueKind.Array } v && v.GetArrayLength() >= 2
            ? ((long)(GameData.Number(v[0]) ?? min), (long)(GameData.Number(v[1]) ?? max)) : (min, max);

    internal long DungeonScene => Number("AdventureGroupDungeonSceneId", 1010001);
    internal long SaplingScene => Number("AdventureGroupCreateSnaplingSceneId", 10100);
    private int MemberLimit => (int)Number("AdventureGroupMemberLimit", 10);
    private int MinimumMembers => (int)Number("AdventureGroupHelperCreateSuccessLimit", 3);
    private long HelperLifeSeconds => Number("AdventureGroupHelperCreateTimeLimit", 12) * 3600;
    private int HelperApplyLimit => (int)Number("AdventureGroupApplyHelperLimit", 5);
    private int NormalApplyLimit => (int)Number("AdventureGroupApplyNormalLimit", 10);
    private int GroupApplicationLimit => (int)Number("AdventureGroupNormalApplyLimit", 20);
    private long LeavePunishSeconds => Number("AdventureGroupLeavePunish", 1800);
    private long ChangeOwnerCooldown => Number("AdventureGroupChangeOwnerCD", 86400);
    private int WeeklyExpLimit => (int)Number("AdventureGroupMoneyWeeklyLimit", 1000);
    private int WaterLimit => (int)Number("AdventureGroupWaterSaplingLimit", 1);
    private int EventLimit => (int)Number("AdventureGroupMaxEventLimit", 100);
    private long[] SwornTasks => Param("AdventureGroupSwornTask") is { ValueKind: JsonValueKind.Array } v
        ? v.EnumerateArray().Select(x => (long)(GameData.Number(x) ?? 0)).ToArray() : [2000500, 2000501, 2000502];

    /// <summary>AdventureGroupLevelAwardConfig rows by _iLevel: (_iNum, _iLevelAward, _iConveneNum).</summary>
    private SortedDictionary<int, (long Num, long Award, int Convene)> Levels()
    {
        var levels = new SortedDictionary<int, (long, long, int)>();
        foreach (var row in _data.Table("AdventureGroupLevelAwardConfig").Values)
        {
            int level = (int)GameData.Int(row, "_iLevel");
            if (level > 0) levels[level] = (GameData.Int(row, "_iNum"), GameData.Int(row, "_iLevelAward"), (int)GameData.Int(row, "_iConveneNum"));
        }
        return levels;
    }

    private bool CardUnlocked(AdventureGroupStore.State g, int card) =>
        _data.Row("AdventureGroupCardConfig", card) is { } row
        && (GameData.Int(row, "_iDefaultUnlock") == 1 || g.AvailableBackgrounds.Contains(card));

    private bool DefaultCard(int card) => _data.Row("AdventureGroupCardConfig", card) is { } row && GameData.Int(row, "_iDefaultUnlock") == 1;

    // ---------------------------------------------------------------- time

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>Unix seconds of today's 05:00 local boundary (the daily reset of the To-Do system, inferred here).</summary>
    private long DayStart
    {
        get
        {
            var local = _clock.GetLocalNow();
            var day = local.AddHours(-5).Date.AddHours(5);
            return new DateTimeOffset(day, local.Offset).ToUnixTimeSeconds();
        }
    }

    private long WeekStart
    {
        get
        {
            var local = _clock.GetLocalNow();
            var day = local.AddHours(-5).Date;
            int sinceMonday = ((int)day.DayOfWeek + 6) % 7;
            return new DateTimeOffset(day.AddDays(-sinceMonday).AddHours(5), local.Offset).ToUnixTimeSeconds();
        }
    }

    /// <summary>Apply the daily (rallies) and weekly (group EXP cap) periods; true when something changed.</summary>
    private bool Roll(AdventureGroupStore.State g)
    {
        bool changed = false;
        long day = DayStart, week = WeekStart;
        if (g.DayResetTime < day) { g.DayResetTime = day; g.ConveneUseTime = 0; changed = true; }
        if (g.WeekResetTime < week) { g.WeekResetTime = week; g.WeekMoney = 0; changed = true; }
        return changed;
    }

    private int TodayWatering(AdventureGroupStore.Member m) => m.WaterDay == DayStart ? m.WateringCount : 0;

    // ---------------------------------------------------------------- sessions

    private GameSession? Online(long id) => _sessions.All().FirstOrDefault(s => s.Role?.Id == id && s.Progress != null);
    private AdventureGroupStore.State? Own(GameSession s) => s.Role == null ? null : _store.ForRole(s.Role.Id);
    private bool Open(GameSession s) => _functions.IsOpen(s, Function);
    private static bool Normal(AdventureGroupStore.State? g) => g?.Status == StatusNormal;

    private bool Banned(string text) => _options.Value.GuildBlockedWords.Any(word =>
        !string.IsNullOrWhiteSpace(word) && text.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static int TextLength(string text) => new System.Globalization.StringInfo(text).LengthInTextElements;

    private int CheckName(string name, long except = 0)
    {
        if (string.IsNullOrWhiteSpace(name)) return ErrNameEmpty;
        var (min, max) = Range("AdventureGroupNameLimit", 2, 7);
        int length = TextLength(name);
        if (length < min || length > max || name != name.Trim()) return ErrNameInvalid;
        if (Banned(name)) return ErrBanned;
        return _store.NameExists(name, except) ? ErrNameTaken : 0;
    }

    private int CheckAnnounce(string announce)
    {
        var (_, max) = Range("AdventureGroupAnnounceLimit", 0, 30);
        return TextLength(announce) > max ? ErrNameInvalid : Banned(announce) ? ErrBanned : 0;
    }

    private int CheckLabels(IReadOnlyCollection<int> labels)
    {
        var (_, max) = Range("AdventureGroupLabelLimit", 0, 6);
        return labels.Count > max || labels.Distinct().Count() != labels.Count || labels.Any(l => l is < 1 or > 1000)
            ? ErrorCodes.Parameter : 0;
    }

    // ---------------------------------------------------------------- wire views

    private AdventureGroupBaseInfo BaseInfo(AdventureGroupStore.State g)
    {
        var view = Clone(g);
        Roll(view);
        var info = new AdventureGroupBaseInfo
        {
            Name = view.Name, Level = view.Level, Announce = view.Announce, CreateTime = (int)view.CreateTime, Status = view.Status,
            AutoAgreeHelperApply = view.AutoAgreeHelperApply, Background = view.Background, Owner = view.Owner,
            AutoAgreeApply = view.AutoAgreeApply, RecruitTime = (int)view.RecruitTime, ChangeOwnerTime = (int)view.ChangeOwnerTime,
            ChatGroupid = "", CanSeeMemberAnnounce = view.CanSeeMemberAnnounce, CreateType = (AdventureGroupCreateType)view.CreateType,
            SaplingData = new SaplingData { SaplingId = view.SaplingId, WateringCount = view.SaplingWatering, UpvoteCount = view.SaplingUpvote },
            ResourceData = new Resource { AdventGroupMoney = view.Money, WeekMoney = view.WeekMoney },
            WeekResetTime = (int)view.WeekResetTime, DayResetTime = (int)view.DayResetTime,
            ConveneData = new ConveneData { UseTime = view.ConveneUseTime, LastConveneTime = (int)view.LastConveneTime },
            SeasonState = new SeasonState(), LastUpdateTime = (int)view.LastUpdateTime,
        };
        info.Label.Add(view.Labels);
        info.Availablebackground.Add(view.AvailableBackgrounds);
        return info;
    }

    private PlayerBaseInfo MemberInfo(AdventureGroupStore.Member m)
    {
        var info = new PlayerBaseInfo
        {
            PlayerID = m.RoleId, EnterTime = (int)m.EnterTime, BornAreaId = (int)_options.Value.ClusterId,
            OffTime = Online(m.RoleId) != null ? 0 : (int)(_roles.Get(m.RoleId)?.LastLoginTime ?? 0),
            Nickname = m.Nickname, WateringCount = TodayWatering(m), UserName = m.Name,
        };
        if (m.Declaration is { } d)
            info.DeclarationData = new DeclarationInfo
            {
                Content = d.Content, DeclarationType = (DeclarationType)d.Type, VoiceDuration = d.VoiceDuration,
                Voice = d.Voice is { Length: > 0 } voice ? ByteString.CopyFrom(voice) : ByteString.Empty,
            };
        return info;
    }

    private PlayerBaseInfo ApplicantInfo(AdventureGroupStore.Applicant a) => new()
    {
        PlayerID = a.RoleId, EnterTime = (int)a.Time, BornAreaId = (int)_options.Value.ClusterId, UserName = a.Name,
        OffTime = Online(a.RoleId) != null ? 0 : (int)(_roles.Get(a.RoleId)?.LastLoginTime ?? 0),
    };

    /// <summary>memberData[1] is read as the owner (Assemble_ConfirmWidget:131, HomePanel:951): owner first.</summary>
    private IEnumerable<AdventureGroupStore.Member> Ordered(AdventureGroupStore.State g) =>
        g.Members.Values.OrderBy(m => m.RoleId == g.Owner ? 0 : 1).ThenBy(m => m.EnterTime).ThenBy(m => m.RoleId);

    private AdventureGroupData GroupData(AdventureGroupStore.State g, long viewer)
    {
        var result = new AdventureGroupData { Uuid = g.Id, Data = BaseInfo(g), IsApply = g.Applications.ContainsKey(viewer) };
        result.MemberData.Add(Ordered(g).Select(MemberInfo));
        return result;
    }

    private static AdventureGroupStore.State Clone(AdventureGroupStore.State g)
    {
        var copy = JsonSerializer.Deserialize<AdventureGroupStore.State>(JsonSerializer.SerializeToUtf8Bytes(g))!;
        copy.CapturedRevision = g.CapturedRevision;
        return copy;
    }

    // ---------------------------------------------------------------- per-role 5068

    /// <summary>The role's own values (role blob "adventure_group"); group fields are filled in by <see cref="Relation"/>.</summary>
    private DBAdventureGroupRelationData Personal(GameSession s)
    {
        if (s.AdventureGroupRole is { } loaded) return loaded;
        var bytes = _roles.LoadAdventureGroup(s.Role!.Id);
        s.AdventureGroupRole = bytes is { Length: > 0 } ? DBAdventureGroupRelationData.Parser.ParseFrom(bytes) : new DBAdventureGroupRelationData();
        return s.AdventureGroupRole;
    }

    private DBAdventureGroupRelationData Relation(GameSession s, AdventureGroupStore.State? g)
    {
        var personal = Personal(s);
        var view = new DBAdventureGroupRelationData
        {
            AdventureGroupId = g?.Id ?? 0, LockApplyTime = personal.LockApplyTime, ChatGroupid = "",
            Status = (AdventureGroupStatus)(g?.Status ?? 0), Owner = g?.Owner ?? 0,
            CreateType = (AdventureGroupCreateType)(g?.CreateType ?? 0), ManagerLeavePunishTime = personal.ManagerLeavePunishTime,
            Level = g?.Level ?? 0, SocialMoneyWeekLimit = personal.SocialMoneyWeekLimit,
        };
        view.TakenGroupRewardLevel.Add(personal.TakenGroupRewardLevel);
        if (g != null) view.Availablebackground.Add(g.AvailableBackgrounds);
        return view;
    }

    private Task SyncRelationAsync(GameSession s, AdventureGroupStore.State? g) =>
        CharacterService.SyncAsync(s, RelationSync, Relation(s, g));

    /// <summary>Persist the role's own values, then publish them (a plain role blob write: no goods move).</summary>
    private async Task SavePersonalAsync(GameSession s, DBAdventureGroupRelationData next)
    {
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(s.Role!.Id, "adventure_group", next.ToByteArray());
        await _roles.CommitAsync(transaction, "advguild/personal", _ => s.AdventureGroupRole = next);
    }

    // ---------------------------------------------------------------- pushes

    private Task SendFullAsync(GameSession s, AdventureGroupStore.State g, bool joined = false)
    {
        var notify = new AdventureGroupDataNotify { Data = GroupData(g, s.Role!.Id), IsJoin = joined };
        // The applicant list is the owner's (HomeMember / MemberList accept buttons); members see it read-only.
        notify.ApplyData.Add(g.Applications.Values.OrderBy(a => a.Time).Select(ApplicantInfo));
        return s.SendGameAsync(FullData, notify);
    }

    /// <summary>34011 without data: CheckInAdventureGroup is false again (DAL:928-977, 1814-1846).</summary>
    private static Task SendClearAsync(GameSession s) => s.SendGameAsync(FullData, new AdventureGroupDataNotify());

    private IEnumerable<GameSession> OnlineMembers(AdventureGroupStore.State g) =>
        g.Members.Keys.Select(Online).OfType<GameSession>();

    private async Task BroadcastAsync(AdventureGroupStore.State g, AdventureGroupDataChangeNotify change, long except = 0)
    {
        foreach (var s in OnlineMembers(g))
            if (s.Role!.Id != except) await s.SendGameAsync(ChangeNotify, change);
    }

    private Task BroadcastBaseAsync(AdventureGroupStore.State g) =>
        BroadcastAsync(g, new AdventureGroupDataChangeNotify { Tp = ChangeType.ChangeBaseData, ChangeBaseData = BaseInfo(g) });

    private Task BroadcastMemberAsync(AdventureGroupStore.State g, AdventureGroupStore.Member m, ChangeType type, long except = 0) =>
        BroadcastAsync(g, new AdventureGroupDataChangeNotify { Tp = type, ApplyData = MemberInfo(m) }, except);

    private Task BroadcastApplicantAsync(AdventureGroupStore.State g, AdventureGroupStore.Applicant a, bool added) =>
        BroadcastAsync(g, new AdventureGroupDataChangeNotify
        { Tp = added ? ChangeType.ChangeApplyAdd : ChangeType.ChangeApplyDel, ApplyData = ApplicantInfo(a) });

    private void AddEvent(AdventureGroupStore.State g, AdventureGroupEventType type, long first = 0, long second = 0, string third = "")
    {
        g.Events.Add(new AdventureGroupStore.GroupEvent { Type = (int)type, Time = Now, First = first, Second = second, Third = third });
        int limit = Math.Max(1, EventLimit);
        if (g.Events.Count > limit) g.Events.RemoveRange(0, g.Events.Count - limit);
    }

    // ---------------------------------------------------------------- persistence

    /// <summary>One commit of the aggregate and its membership rows; false on a name or membership conflict.</summary>
    private async Task<bool> SaveAsync(AdventureGroupStore.State g, Action<Accounts.Persistence.RoleItemPlanBuilder>? also = null)
    {
        g.LastUpdateTime = Now;
        var transaction = _roles.NewTransaction();
        _store.EnlistSave(g, transaction);
        also?.Invoke(transaction);
        try { await _roles.CommitAsync(transaction, "advguild/save"); return true; }
        catch (Exception e) when (AdventureGroupStore.IsConflict(e)) { return false; }
    }

    private async Task<bool> DeleteAsync(AdventureGroupStore.State g)
    {
        var transaction = _roles.NewTransaction();
        _store.EnlistDelete(g, transaction);
        try { await _roles.CommitAsync(transaction, "advguild/delete"); return true; }
        catch (Exception e) when (AdventureGroupStore.IsConflict(e)) { return false; }
    }

    // ---------------------------------------------------------------- login

    /// <summary>5068 always (the client's DAL needs the dataset), 34011 for members, before 5056 opens the function.</summary>
    public async Task LoginAsync(GameSession s)
    {
        s.AdventureGroupRole = null;
        var g = Own(s);
        await SyncRelationAsync(s, g);
        if (g != null) await SendFullAsync(s, g);
    }

    // ---------------------------------------------------------------- hooks

    /// <summary>TaskConfig type-23 quests (AdventureGroupTaskConfig via DailyActivityConfig 3200, 3210-3213) carry
    /// conditions 88000081-88000085 = base level 999 in this export (the feature was closed). With the function open they
    /// are given to the members of a normal group (inferred: the original server's gate is not in the client).</summary>
    private bool? GroupQuestCondition(GameSession s, long condition)
    {
        if (condition is < 88000081 or > 88000085 || s.Role == null || s.Progress == null) return null;
        if (!Open(s)) return null;
        return Normal(Own(s));
    }

    /// <summary>A delivered group quest (TaskConfig type 23): its Adventure Bond Points (item 130, a currency, so no
    /// ItemGained) also count as group EXP (12390100036: "obtained by completing Adventure Party quests. Increases the
    /// Adventure Party's level").</summary>
    private void OnTaskDelivered(GameSession s, long taskId, AwardPlan plan)
    {
        if (s.Role == null || _tasks.TypeOf(taskId) != GroupQuestType) return;
        long amount = plan.Grants.Where(g => g.Entry == BondPoints).Sum(g => (long)g.Count);
        if (amount <= 0) return;
        long role = s.Role.Id;
        _loop.PostLegacy(() => AddExpAsync(role, amount));
    }

    /// <summary>enum_TaskType.AdventureGroup (Lua_GameDefine; HLP:1659-1697 GetTrackingTasks).</summary>
    private const int GroupQuestType = 23;

    // ---------------------------------------------------------------- queries

    public async Task MemberDataAsync(GameSession s)
    {
        var resp = new PullNormalAdventureGroupMemberRes();
        if (Own(s) is { } g) resp.MemberData.Add(Ordered(g).Select(MemberInfo));
        await s.SendGameAsync(MemberData, resp);
    }

    public Task DetailAsync(GameSession s, GetAdventureGroupDetailDataReq req)
    {
        var g = _store.Get(req.LastAdventureGroupId);
        var resp = new GetAdventureGroupDetailDataRes { Result = g == null ? ErrNoGroup : 0 };
        if (g != null) resp.Data = GroupData(g, s.Role?.Id ?? 0);
        return s.SendGameAsync(Detail, resp);
    }

    public Task CommemorateAsync(GameSession s)
    {
        var resp = new AdventureCommemorateRes();
        if (Own(s) is { } g)
            resp.EventData.Add(g.Events.AsEnumerable().Reverse().Select(e => new EventData
            {
                EventType = (AdventureGroupEventType)e.Type, EventTime = (int)e.Time, EventFirstParam = e.First,
                EventSecondParam = e.Second, EventThirdParam = e.Third,
            }));
        return s.SendGameAsync(Commemorate, resp);
    }

    /// <summary>34022: every normal group caring for the tree the player looks at (TreeWidget:384-420).</summary>
    public Task SaplingDataAsync(GameSession s, GetSaplingDataReq req)
    {
        var resp = new GetSaplingDataRes();
        resp.SaplingDataList.Add(_store.List().Where(g => g.SaplingId != 0 && g.SaplingId == req.SaplingId && g.Status != StatusHelper)
            .OrderByDescending(g => g.SaplingWatering).ThenBy(g => g.Id)
            .Select(g => new AdventureGroupSaplingData
            {
                WateringCount = g.SaplingWatering, UpvoteCount = g.SaplingUpvote, AdventureId = g.Id,
                Background = g.Background, Name = g.Name,
            }));
        return s.SendGameAsync(SaplingData, resp);
    }

    /// <summary>Filters of TypeConfig group 179 (JoinWidget): 1 fewer than 5 openings, 2 at least 5 (inferred mapping of
    /// romsg.JoinLimit), and a label (TypeConfig group 178).</summary>
    private bool Matches(AdventureGroupStore.State g, string search, ConditionJoin? condition)
    {
        if (search.Length > 0 && !g.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            && g.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) != search) return false;
        if (condition is { Label: > 0 } && !g.Labels.Contains(condition.Label)) return false;
        int openings = MemberLimit - g.Members.Count;
        return condition?.JoinType switch
        {
            JoinLimit.JoinGreaterThanLevelFive => openings < 5,
            JoinLimit.JoinLessThanFive => openings >= 5,
            _ => true,
        };
    }

    private bool HelperLive(AdventureGroupStore.State g) => g.Status == StatusHelper && g.CreateTime + HelperLifeSeconds > Now;

    public Task HelperListAsync(GameSession s, PullHelperAdventureGroupListReq req)
    {
        long self = s.Role!.Id;
        var all = _store.List();
        var matches = all.Where(g => HelperLive(g) && Matches(g, req.SearchName, req.ConditionJoinData)).ToList();
        var resp = new PullHelperAdventureGroupListRes
        {
            SearchName = req.SearchName, TotalAdventureGroupCount = matches.Count, ConditionJoinData = req.ConditionJoinData,
            AlreadyHelperCount = all.Count(g => g.Status == StatusHelper && g.Applications.ContainsKey(self)),
        };
        resp.Data.Add(matches.Where(g => g.Id > req.LastAdventureGroupId).Take(Math.Clamp(req.Count, 1, 200))
            .Select(g => GroupData(g, self)));
        return s.SendGameAsync(HelperList, resp);
    }

    /// <summary>34008: normal groups with room, by id (the client pages with lastAdventureGroupId, JoinWidget:1246-1266).</summary>
    public Task NormalListAsync(GameSession s, PullAdventureGroupReq req)
    {
        long self = s.Role!.Id;
        var matches = _store.List().Where(g => g.Status == StatusNormal && g.Members.Count < MemberLimit
            && Matches(g, req.SearchName, req.ConditionJoinData)).ToList();
        var resp = new PullAdventureGroupRes
        {
            SearchName = req.SearchName, TotalAdventureGroupCount = matches.Count, ConditionJoinData = req.ConditionJoinData,
        };
        resp.Data.Add(matches.Where(g => g.Id > req.LastAdventureGroupId).OrderBy(g => g.Id)
            .Take(Math.Clamp(req.Count, 1, 200)).Select(g => GroupData(g, self)));
        return s.SendGameAsync(NormalList, resp);
    }

    public Task HelperApplicantsAsync(GameSession s, PullHelperAdventureGroupApplyerReq req)
    {
        var g = _store.Get(req.AdventureGroupId);
        int code = g == null ? ErrNoGroup : g.Owner != s.Role!.Id ? ErrPermission : 0;
        var resp = new PullHelperAdventureGroupApplyerRes { Result = code, AdventureGroupId = req.AdventureGroupId };
        if (code == 0) resp.Applyer.Add(g!.Applications.Values.OrderBy(a => a.Time).Select(ApplicantInfo));
        return s.SendGameAsync(HelperApplicants, resp);
    }
}
