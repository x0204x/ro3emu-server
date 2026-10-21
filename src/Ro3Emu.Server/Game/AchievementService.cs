using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Achievements / Trophy panel (function 21; AUTO_SYNC 5019 DBRoleAchievementInfo; MG_Achievement 76002, 76003,
/// 76009, 76010). The server is the only judge of the 33 AchievementConfig condition types
/// (the client has no evaluator for them). Each row keeps one score slot per _kProgressTarget entry:
/// - snapshot conditions are re-read from owned state at login, after the hooks below and by a probe sweep, and a slot
///   keeps the best value ever seen (inferred, §9.3);
/// - event counters add the event's payload to the slot;
/// - a slot never exceeds the formatter's R (§5), a predicate sends 0 until true and then R.
/// A row is reached (status 1, stamptime in ms) when every slot holds its R, and never reverts. Claims (status 2,
/// AchievementLevelInfo.curLevelRewardLevel) commit their grant and the new state as one owned-award economy
/// transaction ("cd13e/achievement-award"). Points and levels are derived from reached rows with the client's algorithm.
/// Fifteen rows whose source does not exist or whose meaning is unresolved stay at status 0 (§5, "Blocked").
/// 76004 (share) has no sender in the client and stays unrouted.
/// </summary>
public sealed class AchievementService : IGameTicker
{
    public const uint Sync = 5019, GetReward = 76002, GetPointReward = 76003, UpdateHintLevel = 76009, GetAllReward = 76010;

    // ErrorCodeConfig 1901-1906; the mapping of each failure is inferred from the texts.
    internal const int NotExist = 1901, NotFinished = 1902, AlreadyReceived = 1903, RewardMissing = 1904,
        TypeMissing = 1905, LevelNotReached = 1906;

    // AchievementConfig._kProgressTarget condition ids (spec §5).
    internal const int CEquipQuality = 303004, CCraftQualityOnce = 302010, CCraftCount = 103006, CAppraiseCount = 302006,
        CAppraiseRare = 302007, CAppraiseStunt = 302008, CRefineLevel = 302009, CCardCount = 301001, CCardQuality = 301002,
        CCardInserted = 103007, CCardStars = 314002, CTalentLevel = 326002, CJobTalent = 326003, CEmblemQuality = 103036,
        CResonance = 316002, CPetQuality = 103009, CPetRank = 306005, CHatch = 106004, CMvp = 103011, CTowerFloor = 101014,
        CTeamBoss = 210008, CGodBoss = 210004, CGuild = 104007, CGuildPosition = 107003, CFriends = 106017,
        CAffection = 106016, CTavernQuest = 106007, CTavernStar = 106008, CFashionLevel = 303001, CAppearance = 303002,
        CAppearanceCount = 303003, CJobRank = 101019, CLoginDays = 101011;

    /// <summary>No source in this server (tavern), no table (fashion level) or an unresolved meaning (§5, §10.5).</summary>
    private static readonly HashSet<long> BlockedConditions =
        [CTavernQuest, CTavernStar, CFashionLevel, CAppraiseRare, CJobTalent];

    /// <summary>Event counters: no history, the slot is the count since the feature exists (spec §5 "E").</summary>
    private static readonly HashSet<long> CounterConditions =
        [CCraftQualityOnce, CCraftCount, CAppraiseCount, CAppraiseStunt, CCardCount, CMvp, CLoginDays];

    /// <summary>Friend conditions read the social relation from the database: login and relation hooks only.</summary>
    private static readonly HashSet<long> FriendConditions = [CFriends, CAffection];

    /// <summary>Realm of the Gods boss records (TenGameplayConfig normal / hard) for 210004.</summary>
    private static readonly long[] GodRealms = [100101, 100102];

    private const long StarAttr = 502;               // card stars (CardService.StarUpAsync)
    private const int StuntKey = EquipmentAffixes.SpecialKey, StuntSlots = EquipmentAffixes.SpecialSlots;
    private const long TalentOathNode = 10100000;    // battle oath, not a plan talent (SeasonTalentService)
    /// <summary>Hook-marked roles are probed every 250 ms; every role is also probed once per 20 ticks (5 s).</summary>
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(250);
    private const int SweepSlices = 20;

    /// <summary>Server-only state next to the wire state (role blob "achievement_runtime", JSON).</summary>
    public sealed class RuntimeState
    {
        public int Version { get; set; } = 1;
        /// <summary>Unix seconds of the 05:00 local day boundary last counted for 101011 (cumulative login days).</summary>
        public long LastLoginDay { get; set; }
    }

    internal sealed record Row(long Id, int Type, long Group, int Point, long Award, long[][] Targets, long[] Required,
        bool Blocked, string?[] RecordKeys, string[][] GodKeys);

    internal sealed record Tier(int Level, long Point, long Award);

    private sealed class Catalog
    {
        public Dictionary<long, Row> Rows { get; } = new();
        /// <summary>0 = AchievementLevel (global), 100-105 = AchievementType rows of the type, sorted by _iLevel.</summary>
        public Dictionary<int, Tier[]> Tiers { get; } = new();
    }

    private readonly GameData _data;
    private readonly RoleStore _roles;
    private readonly CharacterService _character;
    private readonly AwardService _awards;
    private readonly TowerService _tower;
    private readonly Micro.SocialOwnerCache _social;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly TimeProvider _clock;
    private readonly ILogger<AchievementService> _log;
    private Catalog? _catalog;
    private readonly HashSet<GameSession> _recheckQueued = new();
    private readonly HashSet<GameSession> _dirty = new();
    private int _sweepIndex;
    // Config lookups of the probes (GameData rows are frozen after preload).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (long Type, long SubType, long Quality)> _items = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, long> _petQuality = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _wardrobeType = new();
    private readonly HashSet<GameSession> _friendQueued = new();
    private TimeSpan _lastTick;

    public AchievementService(GameData data, RoleStore roles, CharacterService character, AwardService awards,
        EquipmentService equipment, AppraisalService appraisal, CardService cards, MvpService mvps, TowerService tower,
        DungeonService dungeons, GuildService guilds, Micro.FriendService friends, Micro.SocialOwnerCache social,
        SessionRegistry sessions, GameLoop loop, GmCommands gm, TimeProvider clock, ILogger<AchievementService> log)
    {
        this._data = data; this._roles = roles; this._character = character; this._awards = awards; this._tower = tower;
        this._social = social; this._sessions = sessions; this._loop = loop; this._clock = clock; this._log = log;
        // Snapshot sources: every hook only queues a re-read on the owner loop, so the firing service's locks are
        // released first (several fire inside the character lock or a commit's apply).
        character.StateChanged.Add(QueueRecheck);
        character.JobChanged.Add(s => { QueueRecheck(s); return Task.CompletedTask; });
        character.ProgressHandlers.Add(s => { QueueRecheck(s); return Task.CompletedTask; });
        equipment.Refined.Add(s => { QueueRecheck(s); return Task.CompletedTask; });
        cards.CollectionChanged.Add(s => { QueueRecheck(s); return Task.CompletedTask; });
        dungeons.Completed.Add((s, _, _) => { QueueRecheck(s); return Task.CompletedTask; });
        tower.FloorCleared.Add(QueueRecheck);
        guilds.MembershipChanged.Add(QueueRecheck);
        friends.RelationChanged.Add(QueueFriendRecheck);
        // Event counters: the payload is captured now, applied on the owner loop.
        equipment.Crafted.Add((s, entry, _, _) =>
        {
            long quality = Quality(entry);
            PostEvent(s, (t) => t[0] switch
            {
                CCraftQualityOnce when t.Length >= 3 && quality >= t[2] => 1,
                CCraftCount when t.Length >= 3 && quality >= t[1] => 1,
                _ => 0,
            });
            return Task.CompletedTask;
        });
        appraisal.Appraised.Add((s, items) =>
        {
            int count = items.Count;
            bool stunt = items.Any(i => i.Attrs != null
                && Enumerable.Range(StuntKey, StuntSlots).Any(k => i.Attrs.GetValueOrDefault(k) != 0));
            PostEvent(s, t => t[0] switch
            {
                CAppraiseCount => count,
                CAppraiseStunt => stunt ? 1 : 0,
                _ => 0,
            });
        });
        character.ItemGained.Add((s, entry, count) =>
        {
            if (count <= 0) return;
            var (type, subtype, _) = Item(entry);
            if (IsCard(entry))
            {
                PostEvent(s, t => t is [CCardCount, _, var wantType, var wantSub] && wantType == type && wantSub == subtype ? count : 0);
                QueueRecheck(s); // 301002 reads the first-sight collection and the bag
            }
        });
        mvps.ParticipantSettled.Add(s => PostEvent(s, t => t[0] == CMvp ? 1 : 0));
        gm.Register("ach", "<id> done|reset | fill <type> <count> (achievements test aid)", GmAsync);
    }

    // ---------------------------------------------------------------- catalog

    private Catalog Rows() => _catalog ??= Load();

    private Catalog Load()
    {
        var catalog = new Catalog();
        foreach (var row in _data.Table("AchievementConfig").Values)
        {
            long id = GameData.Int(row, "_iID");
            if (id <= 0) continue;
            var targets = row.TryGetProperty("_kProgressTarget", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Array)
                    .Select(t => t.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray())
                    .Where(t => t.Length > 0).ToArray()
                : [];
            var required = targets.Select(Required).ToArray();
            bool blocked = targets.Length == 0 || targets.Any(IsBlocked);
            if (required.Any(r => r <= 0))
            {
                _log.LogWarning("Achievement {Id}: unsupported target {Targets}; it stays not reached", id,
                    string.Join(" ", targets.Select(t => "[" + string.Join(",", t) + "]")));
                blocked = true;
            }
            catalog.Rows[id] = new Row(id, (int)GameData.Int(row, "_iType"), GameData.Int(row, "_iGroup"),
                (int)GameData.Int(row, "_iPoint"), GameData.Int(row, "_iAward"), targets, required, blocked,
                targets.Select(t => t is [CTeamBoss, _, var multi, var npc] ? BossRecord(multi, npc) : null).ToArray(),
                targets.Select(t => t is [CGodBoss, var npc, ..] ? GodRealms.Select(m => BossRecord(m, npc)).OfType<string>().ToArray() : []).ToArray());
        }
        catalog.Tiers[0] = _data.Table("AchievementLevel").Values.Select(r =>
                new Tier((int)GameData.Int(r, "_iLevel"), GameData.Int(r, "_iPoint"), GameData.Int(r, "_iAward")))
            .OrderBy(t => t.Level).ToArray();
        foreach (var group in _data.Table("AchievementType").Values.GroupBy(r => (int)GameData.Int(r, "_iTypeID")))
            if (group.Key > 0)
                catalog.Tiers[group.Key] = group.Select(r =>
                        new Tier((int)GameData.Int(r, "_iLevel"), GameData.Int(r, "_iPoint"), GameData.Int(r, "_iAward")))
                    .OrderBy(t => t.Level).ToArray();
        int blockedCount = catalog.Rows.Values.Count(r => r.Blocked);
        _log.LogInformation("Achievements: {Rows} rows, {Blocked} blocked, {Types} level tables", catalog.Rows.Count, blockedCount, catalog.Tiers.Count);
        return catalog;
    }

    /// <summary>
    /// The value the client treats as met (spec §5): the formatter registered in LC_ConditionDefine, else
    /// GetDefaultDes = p1. -1 = unknown condition (the row is blocked, never auto-completed).
    /// </summary>
    internal static long Required(long[] t) => t switch
    {
        [CCraftQualityOnce or CAppraiseStunt or CAppraiseRare or CJobTalent or CGuild or CGuildPosition or CJobRank, ..] => 1,
        [CEquipQuality or CAppraiseCount or CRefineLevel or CCardCount or CCardQuality or CCardStars or CTalentLevel
            or CPetRank or CHatch or CMvp or CTowerFloor or CTeamBoss or CFriends or CAffection or CTavernStar
            or CFashionLevel or CAppearance or CAppearanceCount or CLoginDays, var p1, ..] => p1,
        [CCraftCount or CCardInserted or CEmblemQuality or CResonance or CPetQuality or CGodBoss or CTavernQuest, _, var p2, ..] => p2,
        _ => -1,
    };

    /// <summary>Spec §5 "Blocked": the 17 rows whose source or meaning is missing. 303004's fashion variant lists
    /// grids 8-13, whose WardRobeConfig mapping is inferred only.</summary>
    private static bool IsBlocked(long[] t) => BlockedConditions.Contains(t[0])
        || t[0] == CEquipQuality && (t.Length < 4 || t[3..].Any(grid => grid is < 1 or > 7));

    /// <summary>DungeonService record key "{multiId}:{mark}"; the mark is the 1-based _kBoss position whose npc matches.</summary>
    private string? BossRecord(long multi, long npc)
    {
        if (_data.Row("TenGameplayConfig", multi) is not { } row || !row.TryGetProperty("_kBoss", out var bosses)
            || bosses.ValueKind != JsonValueKind.Array) return null;
        int mark = 0;
        foreach (var boss in bosses.EnumerateArray())
        {
            mark++;
            if (boss.ValueKind == JsonValueKind.Array && boss.GetArrayLength() > 1 && GameData.Number(boss[1]) == npc)
                return $"{multi}:{mark}";
        }
        return null;
    }

    internal IReadOnlyCollection<Row> AllRows => Rows().Rows.Values;
    internal IReadOnlyDictionary<int, Tier[]> LevelTables => Rows().Tiers;

    /// <summary>The client's level walk (LC_AchievementHelper.lua:27-48): level = row._iLevel, stop at the first
    /// row whose _iPoint is above the points.</summary>
    internal static int LevelOf(long points, IEnumerable<Tier> tiers)
    {
        int level = 0;
        foreach (var tier in tiers)
        {
            level = tier.Level;
            if (points < tier.Point) break;
        }
        return level;
    }

    private (long Type, long SubType, long Quality) Item(int entry) => _items.GetOrAdd(entry, id => _data.Row("ItemConfig", id) is { } row
        ? (GameData.Int(row, "_iType"), GameData.Int(row, "_iSubType"), GameData.Int(row, "_iQuality")) : (-1, -1, -1));

    private long Quality(int entry) => Item(entry).Quality;

    /// <summary>CharacterService.IsCard: ItemConfig type 4, subtype 400.</summary>
    private bool IsCard(int entry) => Item(entry) is (4, 400, _);

    // ---------------------------------------------------------------- state helpers

    private static bool Reached(AchievementInfo? info) =>
        info?.Status is AchievementStatusType.Complete or AchievementStatusType.GetReward;

    private static long Slot(AchievementInfo? info, int index) => info != null && index < info.Scores.Count ? info.Scores[index] : 0;

    private long Points(DBRoleAchievementInfo state, int type) => Rows().Rows.Values
        .Where(r => (type == 0 || r.Type == type) && Reached(state.Infos.GetValueOrDefault(r.Id))).Sum(r => (long)r.Point);

    internal int Level(DBRoleAchievementInfo state, int type) =>
        Rows().Tiers.TryGetValue(type, out var tiers) ? LevelOf(Points(state, type), tiers) : 0;

    /// <summary>achievementLevelInfo for type 0 and every category: point and level (the client reads neither,
    /// inferred), curLevelRewardLevel and curHintLevel (kept as stored).</summary>
    private void RefreshLevels(DBRoleAchievementInfo state)
    {
        foreach (int type in Rows().Tiers.Keys.Order())
        {
            var info = state.AchievementLevelInfo.TryGetValue(type, out var old) ? old.Clone() : new AchievementLevelInfo { Type = type };
            info.Type = type;
            info.Point = Points(state, type);
            info.Level = Level(state, type);
            if (!info.Equals(old)) state.AchievementLevelInfo[type] = info;
        }
    }

    /// <summary>Drops ids the client cannot resolve and fits every score list to the row's target count.</summary>
    private void Normalize(DBRoleAchievementInfo state)
    {
        foreach (long id in state.Infos.Keys.Where(id => !Rows().Rows.ContainsKey(id)).ToList()) state.Infos.Remove(id);
        foreach (var (id, info) in state.Infos.ToList())
        {
            var row = Rows().Rows[id];
            if (info.AchieveId != id) info.AchieveId = id;
            while (info.Scores.Count < row.Targets.Length) info.Scores.Add(0);
            while (info.Scores.Count > row.Targets.Length) info.Scores.RemoveAt(info.Scores.Count - 1);
        }
        foreach (int type in state.AchievementLevelInfo.Keys.Where(t => !Rows().Tiers.ContainsKey((int)t)).ToList())
            state.AchievementLevelInfo.Remove(type);
    }

    /// <summary>Writes new slot values; reaching every R sets status 1 and stamptime (ms). Never touches a reached row.</summary>
    private bool SetSlots(DBRoleAchievementInfo state, Row row, long[] slots, long nowMs)
    {
        state.Infos.TryGetValue(row.Id, out var info);
        if (Reached(info) || row.Blocked) return false;
        bool reached = slots.Select((v, i) => v >= row.Required[i]).All(x => x);
        if (info == null && !reached && slots.All(v => v == 0)) return false;
        var next = info?.Clone() ?? new AchievementInfo { AchieveId = row.Id };
        next.Scores.Clear();
        next.Scores.AddRange(reached ? row.Required : slots);
        if (reached)
        {
            next.Status = AchievementStatusType.Complete;
            next.Stamptime = nowMs;
        }
        if (next.Equals(info)) return false;
        state.Infos[row.Id] = next;
        return true;
    }

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    // ---------------------------------------------------------------- condition readers

    /// <summary>Snapshot value of one target from committed owned state, or null when its source is not loaded.
    /// Predicates return R or 0. Parameter meanings beyond the formatters are inferred (spec §5).</summary>
    private long? Snapshot(GameSession s, Row row, int index)
    {
        var t = row.Targets[index];
        long r = row.Required[index];
        switch (t[0])
        {
            case CEquipQuality: // [n, quality, grids...]: listed body grids holding an item of quality >= p2
                return t[3..].Count(grid => s.Items.Any(i => i.Grid == grid && Quality(i.Entry) >= t[2]));
            case CRefineLevel: // highest refine level on any grid
                return s.EquipLevels.Refine.Values.DefaultIfEmpty(0).Max();
            case CCardQuality: // [1, type, subtype, quality]: a card of quality >= p4 seen (collection or bag)
                return (s.Library?.Cards.Keys.ToList() ?? new List<int>()).Concat(s.Items.Select(i => i.Entry))
                    .Any(entry => CardOf(entry, t[2], t[3]) && Quality(entry) >= t[4]) ? r : 0;
            case CCardInserted: // [quality, n]: socketed cards of quality >= p1
                return s.Items.Count(i => i.Grid < 0 && IsCard(i.Entry) && Quality(i.Entry) >= t[1]);
            case CCardStars: // [1, stars]: one card raised to >= p2 stars
                return s.Items.Any(i => IsCard(i.Entry) && (i.Attrs?.GetValueOrDefault((int)StarAttr) ?? 0) >= t[2]) ? r : 0;
            case CTalentLevel: // total level of the active talent plan, battle oath excluded
                if (s.SeasonTalent is not { } talent || !talent.Plan.TryGetValue(talent.CurPlanId, out var plan)) return null;
                return plan.TalentNodes.Where(kv => kv.Key != TalentOathNode).Sum(kv => Math.Max(0, kv.Value.CurrentLevel));
            case CEmblemQuality: // [quality, n]: Soul Echoes of quality >= p1 in the active plan
                if (!s.Emblems.Plans.TryGetValue(s.Emblems.CurrentPlan, out var emblems)) return 0;
                return emblems.Slots.Values.Count(guid => s.Items.FirstOrDefault(i => i.Guid == guid) is { } item && Quality(item.Entry) >= t[1]);
            case CResonance: // [stage, n]: Echo Resonances at stage >= p1 (EmblemService.Resonances)
                return EmblemService.Resonances(s, _data).Values.Count(stage => stage >= t[1]);
            case CPetQuality: // [quality, n]: owned pets of PetBaseConfig quality >= p1
                return s.Pets == null ? null : s.Pets.ListPet.Values.Count(p => PetQuality(p.PetId) >= t[1]);
            case CPetRank: // [n, quality]: owned pets of quality >= p2 (5 = S rank)
                return s.Pets == null ? null : s.Pets.ListPet.Values.Count(p => PetQuality(p.PetId) >= t[2]);
            case CHatch: // eggs hatched in total (inferred: hatch count, not lottery draws)
                return s.Pets?.IncubationCount;
            case CTowerFloor:
                return s.Tower == null ? null : _tower.HighestClearedFloor(s);
            case CTeamBoss: // [1, multi, npc]: that boss's record passed
                return s.Dungeons == null ? null
                    : row.RecordKeys[index] is { } key && s.Dungeons.Records.TryGetValue(key, out var record) && record.Passed ? r : 0;
            case CGodBoss: // [npc, 1]: the boss passed in the normal or hard Realm of the Gods
                return s.Dungeons == null ? null
                    : row.GodKeys[index].Any(k => s.Dungeons.Records.TryGetValue(k, out var god) && god.Passed) ? r : 0;
            case CGuild:
                return s.Guild == null ? null : s.Guild.GuildId != 0 ? r : 0;
            case CGuildPosition: // [op, value]: LC_InteractCondition.CheckGuildPosition, ePosition op p2 (5 = "<")
                return s.Guild == null ? null : s.Guild.GuildId != 0 && Compare(t[1], s.Guild.Official, t[2]) ? r : 0;
            case CAppearance: // [appearanceId]: costume or mount unlocked; R is the id itself
                return s.Wardrobe == null && s.Mounts == null ? null : OwnedAppearances(s).Contains(t[1]) ? r : 0;
            case CAppearanceCount: // [n, k, types...]: owned appearances of the listed WardRobeConfig._iType
                if (s.Wardrobe == null && s.Mounts == null) return null;
                var types = t.Skip(3).Take((int)Math.Max(0, t[2])).ToHashSet();
                return OwnedAppearances(s).Count(id => types.Contains(_wardrobeType.GetOrAdd(id,
                    key => _data.Row("WardRobeConfig", key) is { } w ? GameData.Int(w, "_iType") : -1)));
            case CJobRank: // [rank]: JobConfig._iJobRank >= p1
                return s.Progress == null ? null
                    : _data.Row("JobConfig", s.Progress.Career) is { } job && GameData.Int(job, "_iJobRank") >= t[1] ? r : 0;
            default:
                return null; // counters, friends and blocked conditions are not snapshots
        }
    }

    private bool CardOf(int entry, long type, long subtype) => Item(entry) is var item && item.Type == type && item.SubType == subtype;

    private long PetQuality(int pet) => _petQuality.GetOrAdd(pet, id => _data.Row("PetBaseConfig", id) is { } row ? GameData.Int(row, "_iQuality") : -1);

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

    /// <summary>Lit-up costumes and owned mounts that have not expired (mount ids are WardRobeConfig ids).</summary>
    private HashSet<long> OwnedAppearances(GameSession s)
    {
        long now = NowMs;
        var owned = new HashSet<long>();
        if (s.Wardrobe != null)
            foreach (var (id, info) in s.Wardrobe.AllLightUpAppearances)
                if (info.OverdueTimeMs == 0 || info.OverdueTimeMs > now) owned.Add(id);
        if (s.Mounts != null)
            foreach (var (id, mount) in s.Mounts.Mounts)
                if (mount.OverdueTimeMs == 0 || mount.OverdueTimeMs > now) owned.Add(id);
        return owned;
    }

    /// <summary>Re-reads every snapshot target; a slot only grows. Returns whether the state changed.</summary>
    private bool EvaluateSnapshots(GameSession s, DBRoleAchievementInfo state, long nowMs)
    {
        bool changed = false;
        foreach (var row in Rows().Rows.Values)
        {
            if (row.Blocked || Reached(state.Infos.GetValueOrDefault(row.Id))) continue;
            if (!row.Targets.Any(t => !CounterConditions.Contains(t[0]) && !FriendConditions.Contains(t[0]))) continue;
            var info = state.Infos.GetValueOrDefault(row.Id);
            var slots = new long[row.Targets.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                long old = Slot(info, i);
                long? value = CounterConditions.Contains(row.Targets[i][0]) || FriendConditions.Contains(row.Targets[i][0])
                    ? null : Snapshot(s, row, i);
                slots[i] = value is { } v ? Math.Max(old, Math.Clamp(v, 0, row.Required[i])) : old;
            }
            changed |= SetSlots(state, row, slots, nowMs);
        }
        return changed;
    }

    /// <summary>Whether a snapshot re-read would change anything (the tick's cheap read-only probe).</summary>
    private bool SnapshotImproves(GameSession s)
    {
        var state = s.Achievements!;
        foreach (var row in Rows().Rows.Values)
        {
            if (row.Blocked || Reached(state.Infos.GetValueOrDefault(row.Id))) continue;
            var info = state.Infos.GetValueOrDefault(row.Id);
            for (int i = 0; i < row.Targets.Length; i++)
            {
                if (CounterConditions.Contains(row.Targets[i][0]) || FriendConditions.Contains(row.Targets[i][0])) continue;
                if (Snapshot(s, row, i) is { } v && Math.Clamp(v, 0, row.Required[i]) > Slot(info, i)) return true;
            }
        }
        return false;
    }

    /// <summary>Adds an event's amount to every matching counter slot, capped at R.</summary>
    private bool AddCounts(DBRoleAchievementInfo state, Func<long[], long> amount, long nowMs)
    {
        bool changed = false;
        foreach (var row in Rows().Rows.Values)
        {
            if (row.Blocked || Reached(state.Infos.GetValueOrDefault(row.Id))) continue;
            var info = state.Infos.GetValueOrDefault(row.Id);
            var slots = new long[row.Targets.Length];
            bool any = false;
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = Slot(info, i);
                if (!CounterConditions.Contains(row.Targets[i][0])) continue;
                long gain = amount(row.Targets[i]);
                if (gain <= 0) continue;
                slots[i] = Math.Min(row.Required[i], slots[i] + gain);
                any = true;
            }
            if (any) changed |= SetSlots(state, row, slots, nowMs);
        }
        return changed;
    }

    /// <summary>106017 [n, level]: friends whose affection level is >= p2; 106016 [level]: the best affection level.
    /// Affection level = the highest FriendlinessConfig._iFriendlev whose _iFriendNum the intimacy reaches.</summary>
    private async Task<bool> EvaluateFriendsAsync(GameSession s, DBRoleAchievementInfo state, long nowMs)
    {
        var rows = Rows().Rows.Values.Where(r => !r.Blocked && !Reached(state.Infos.GetValueOrDefault(r.Id))
            && r.Targets.Any(t => FriendConditions.Contains(t[0]))).ToList();
        if (rows.Count == 0) return false;
        var snapshot = (await _social.LoadSocialAsync([s.Role!.Id])).GetValueOrDefault(s.Role.Id);
        if (snapshot == null) return false;
        var levels = snapshot.Friends.Select(f => FriendLevel(snapshot.Intimacy.GetValueOrDefault(f))).ToList();
        bool changed = false;
        foreach (var row in rows)
        {
            var info = state.Infos.GetValueOrDefault(row.Id);
            var slots = new long[row.Targets.Length];
            for (int i = 0; i < slots.Length; i++)
            {
                var t = row.Targets[i];
                long value = t switch
                {
                    [CFriends, _, var level, ..] => levels.Count(l => l >= level),
                    [CAffection, ..] => levels.DefaultIfEmpty(0).Max(),
                    _ => 0,
                };
                slots[i] = FriendConditions.Contains(t[0]) ? Math.Max(Slot(info, i), Math.Clamp(value, 0, row.Required[i])) : Slot(info, i);
            }
            changed |= SetSlots(state, row, slots, nowMs);
        }
        return changed;
    }

    private long FriendLevel(int intimacy) => _data.Table("FriendlinessConfig").Values
        .Where(r => GameData.Int(r, "_iFriendNum") <= intimacy).Select(r => GameData.Int(r, "_iFriendlev")).DefaultIfEmpty(0).Max();

    /// <summary>101011: one more login day per 05:00 local boundary (DailyActivityService.Boundary), at login and
    /// at the online rollover. The first counted day of a role is its first login after this feature.</summary>
    private bool RollLoginDay(DBRoleAchievementInfo state, RuntimeState runtime, long nowMs)
    {
        long day = DayBoundary();
        if (runtime.LastLoginDay >= day) return false;
        runtime.LastLoginDay = day;
        AddCounts(state, t => t[0] == CLoginDays ? 1 : 0, nowMs);
        return true;
    }

    /// <summary>101011 cumulative login days as counted here: every login-day row counts in parallel (up to 365), so the
    /// highest slot is the role's count.</summary>
    internal long LoginDays(GameSession s) => s.Achievements is { } state
        ? Rows().Rows.Values.SelectMany(row => row.Targets.Select((t, i) => t[0] == CLoginDays
            ? Slot(state.Infos.GetValueOrDefault(row.Id), i) : 0)).DefaultIfEmpty(0).Max()
        : 0;

    private long DayBoundary()
    {
        var now = _clock.GetLocalNow().DateTime;
        var start = (now.TimeOfDay >= TimeSpan.FromHours(5) ? now.Date : now.Date.AddDays(-1)).AddHours(5);
        return new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), _clock.LocalTimeZone.GetUtcOffset(start)).ToUnixTimeSeconds();
    }

    // ---------------------------------------------------------------- lock, persistence, sync

    internal static async Task<IDisposable> LockAsync(GameSession s)
    {
        var role = s.Role;
        var attachment = s.Attachment;
        var owner = s.Owner;
        long? epoch = owner?.LifecycleEpoch;
        Ownership.LegacyOwnerScope? scope = null;
        if (role != null && s.Owners is { } owners)
            scope = await owners.EnterAsync("Achievement.Lock", Ownership.ReservationKey.Role(role.Id));
        bool acquired = false;
        try
        {
            await s.AchievementLock.WaitAsync();
            acquired = true;
            if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment)
                || !ReferenceEquals(s.Owner, owner) || owner?.LifecycleEpoch != epoch)
                throw new OperationCanceledException("Role changed before the achievement gate was acquired.");
            if (owner != null) scope?.Context.Pin(owner);
            return new Releaser(s.AchievementLock, scope);
        }
        catch
        {
            if (acquired) s.AchievementLock.Release();
            scope?.Dispose();
            throw;
        }
    }

    private sealed class Releaser(SemaphoreSlim gate, IDisposable? scope) : IDisposable
    {
        public void Dispose() { gate.Release(); scope?.Dispose(); }
    }

    /// <summary>Progress has no economic effect: a plain blob write per settled event (spec §9.2, inferred: losing the
    /// last increment on a crash is acceptable), then one 5019 increment.</summary>
    private async Task CommitProgressAsync(GameSession s, DBRoleAchievementInfo next)
    {
        RefreshLevels(next);
        if (next.Equals(s.Achievements)) return;
        _roles.SaveAchievements(s.Role!.Id, next.ToByteArray());
        s.Achievements = next;
        await CharacterService.SyncAsync(s, Sync, next.Clone());
    }

    private void SaveRuntime(GameSession s) =>
        _roles.SaveAchievementRuntime(s.Role!.Id, JsonSerializer.SerializeToUtf8Bytes(s.AchievementRuntime));

    private bool Ready(GameSession s) => s.Role != null && s.Achievements != null && s.AchievementRuntime != null;

    // ---------------------------------------------------------------- login

    /// <summary>
    /// After every owner snapshot is loaded (spec §7): load, count the login day, re-read every snapshot condition and the
    /// friend relation, save what changed, then one full 5019. Completions found here raise no HUD tip (the full
    /// handler clears the popup list); level-up popups still come when the panel opens, since curHintLevel lags.
    /// </summary>
    public async Task LoginAsync(GameSession s)
    {
        using var gate = await LockAsync(s);
        long role = s.Role!.Id, now = NowMs;
        var saved = _roles.LoadAchievements(role);
        var state = saved != null ? DBRoleAchievementInfo.Parser.ParseFrom(saved) : new DBRoleAchievementInfo();
        var runtime = _roles.LoadAchievementRuntime(role) is { } raw
            ? JsonSerializer.Deserialize<RuntimeState>(raw) ?? new RuntimeState() : new RuntimeState();
        var original = state.Clone();
        Normalize(state);
        bool runtimeChanged = RollLoginDay(state, runtime, now);
        EvaluateSnapshots(s, state, now);
        await EvaluateFriendsAsync(s, state, now);
        RefreshLevels(state);
        if (saved == null || !state.Equals(original)) _roles.SaveAchievements(role, state.ToByteArray());
        s.Achievements = state;
        s.AchievementRuntime = runtime;
        if (runtimeChanged) SaveRuntime(s);
        await CharacterService.SyncAsync(s, Sync, state.Clone());
    }

    // ---------------------------------------------------------------- in play

    /// <summary>A hook saw owned state change: the next probe (at most 250 ms later) re-reads the role's snapshots.
    /// Hooks such as StateChanged fire on every bag change (loot), so a role is probed at most once per tick.</summary>
    private void QueueRecheck(GameSession s)
    {
        if (Ready(s)) _dirty.Add(s);
    }

    private void PostRecheck(GameSession s)
    {
        if (!Ready(s) || !_recheckQueued.Add(s)) return;
        _loop.PostOwned(s, async () =>
        {
            _recheckQueued.Remove(s);
            if (!Ready(s)) return;
            using var gate = await LockAsync(s);
            if (!Ready(s)) return;
            var next = s.Achievements!.Clone();
            if (EvaluateSnapshots(s, next, NowMs)) await CommitProgressAsync(s, next);
        }, "achievement recheck", discarded: () => _recheckQueued.Remove(s));
    }

    private void QueueFriendRecheck(long roleId)
    {
        var s = _sessions.All().FirstOrDefault(x => x.Role?.Id == roleId);
        if (s == null || !Ready(s) || !_friendQueued.Add(s)) return;
        _loop.PostOwned(s, async () =>
        {
            _friendQueued.Remove(s);
            if (!Ready(s)) return;
            using var gate = await LockAsync(s);
            if (!Ready(s)) return;
            var next = s.Achievements!.Clone();
            if (await EvaluateFriendsAsync(s, next, NowMs)) await CommitProgressAsync(s, next);
        }, "achievement friends", discarded: () => _friendQueued.Remove(s));
    }

    private void PostEvent(GameSession s, Func<long[], long> amount)
    {
        if (!Ready(s)) return;
        _loop.PostOwned(s, async () =>
        {
            if (!Ready(s)) return;
            using var gate = await LockAsync(s);
            if (!Ready(s)) return;
            var next = s.Achievements!.Clone();
            if (AddCounts(next, amount, NowMs)) await CommitProgressAsync(s, next);
        }, "achievement event");
    }

    private void QueueLoginDay(GameSession s)
    {
        if (!Ready(s) || !_recheckQueued.Add(s)) return;
        _loop.PostOwned(s, async () =>
        {
            _recheckQueued.Remove(s);
            if (!Ready(s)) return;
            using var gate = await LockAsync(s);
            if (!Ready(s)) return;
            var next = s.Achievements!.Clone();
            long now = NowMs;
            if (RollLoginDay(next, s.AchievementRuntime!, now)) SaveRuntime(s);
            EvaluateSnapshots(s, next, now);
            await CommitProgressAsync(s, next);
        }, "achievement day", discarded: () => _recheckQueued.Remove(s));
    }

    /// <summary>
    /// Every 250 ms the hook-marked roles and one twentieth of all roles (every role once per 5 s) are probed: a role past
    /// a 05:00 boundary counts its login day; otherwise a read-only probe posts a re-read only when owned state improved.
    /// The sweep also catches sources without a hook (wardrobe and mount unlocks, pet hatching, Soul Echo slots, talent
    /// plans, a guild view changed while offline).
    /// </summary>
    public void Tick(GameTime time)
    {
        if (time.Now - _lastTick < ProbeEvery) return;
        _lastTick = time.Now;
        var all = _sessions.All();
        var probe = new HashSet<GameSession>(_dirty);
        _dirty.Clear();
        if (all.Count > 0)
        {
            int slice = (all.Count + SweepSlices - 1) / SweepSlices;
            for (int i = 0; i < slice; i++) probe.Add(all[(_sweepIndex + i) % all.Count]);
            _sweepIndex = (_sweepIndex + slice) % all.Count;
        }
        if (probe.Count == 0) return;
        long day = DayBoundary();
        foreach (var s in probe)
        {
            if (!Ready(s) || _recheckQueued.Contains(s)) continue;
            if (s.Handoff != null) { _dirty.Add(s); continue; }
            if (s.AchievementRuntime!.LastLoginDay < day) QueueLoginDay(s);
            else if (SnapshotImproves(s)) PostRecheck(s);
        }
    }

    // ---------------------------------------------------------------- claims

    private int? PlayBook(GameSession s) => s.Synced.GetValueOrDefault(SeasonService.SeasonSync) is DBSeasonInfo { Current: { } current }
        ? (int)current.PlayBookId : null;

    /// <summary>Award ids of the tiers (rewardLevel, level] of a level table; 1904 when the claimed level has no award.</summary>
    private int ValidateTier(DBRoleAchievementInfo state, int type, int level, out long[] tierAwards)
    {
        tierAwards = [];
        if (!Rows().Tiers.TryGetValue(type, out var tiers)) return TypeMissing;
        if (tiers.FirstOrDefault(t => t.Level == level) is not { Award: not 0 }) return RewardMissing;
        if (level > Level(state, type)) return LevelNotReached;
        int claimed = state.AchievementLevelInfo.TryGetValue(type, out var info) ? info.CurLevelRewardLevel : 0;
        if (level <= claimed) return AlreadyReceived;
        // (inferred) a claim at level N settles every lower unclaimed tier too (the award list marks every
        // _iLevel <= rewardLevel row as received, UI_Sys_Achievement_AwardWidgetCtrl.lua:124-200).
        tierAwards = tiers.Where(t => t.Level > claimed && t.Level <= level && t.Award != 0).Select(t => t.Award).ToArray();
        return 0;
    }

    private int ValidateTask(DBRoleAchievementInfo state, long id)
    {
        if (!Rows().Rows.ContainsKey(id)) return NotExist;
        return state.Infos.GetValueOrDefault(id)?.Status switch
        {
            AchievementStatusType.Complete => 0,
            AchievementStatusType.GetReward => AlreadyReceived,
            _ => NotFinished,
        };
    }

    /// <summary>
    /// The claim's grants and the new achievement state as one owned-award transaction (spec §9.2). No grant (rows with
    /// _iAward 0) is not economic: a plain blob write. Returns 0, 1904 (award unresolved) or Unknown (commit failed);
    /// on failure nothing changed.
    /// </summary>
    private async Task<int> CommitClaimAsync(GameSession s, DBRoleAchievementInfo next, IReadOnlyList<long> awardGroups,
        string kind, string description, object outcome)
    {
        RefreshLevels(next);
        long role = s.Role!.Id;
        AwardPlan[] plans;
        try
        {
            int? playBook = PlayBook(s);
            plans = awardGroups.Select(group => _awards.Resolve(s, group, playBook)).ToArray();
        }
        catch (AwardResolutionException e)
        {
            _log.LogWarning("Achievement {Kind} {What} reward held for {Role}: {Reason}", kind, description, role, e.Message);
            return RewardMissing;
        }
        var grants = plans.SelectMany(p => p.Grants).ToArray();
        byte[] bytes = next.ToByteArray();
        if (grants.Length == 0)
        {
            _roles.SaveAchievements(role, bytes);
            s.Achievements = next;
            return 0;
        }
        string source = $"achievement/{role}/{kind}/{description}";
        if (source.Length > 400)
            source = $"achievement/{role}/{kind}/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description)))}";
        try
        {
            bool ok = await _character.ApplyAwardAsync(s, new AwardPlan(0, 0, grants),
                persist: tx => _roles.SaveAchievements(role, bytes, tx),
                committed: () => s.Achievements = next,
                ownedAward: new CharacterService.OwnedAwardSource("cd13e/achievement-award", source,
                    new { Version = 1, Kind = kind, Claim = description, Awards = plans.Select(p => p.AwardId).ToArray(), Outcome = outcome }));
            return ok ? 0 : ErrorCodes.Unknown;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "Achievement {Kind} {What} transaction failed for {Role}", kind, description, role);
            return ErrorCodes.Unknown;
        }
    }

    /// <summary>76002: claim reached rows (the client sends one id of a row with an award). All or nothing (inferred).</summary>
    public async Task ClaimAsync(GameSession s, GetAchievementRewardReq req)
    {
        using var gate = await LockAsync(s);
        var resp = new GetAchievementRewardResp();
        if (!Ready(s)) { resp.Result = ErrorCodes.Unknown; await s.SendGameAsync(GetReward, resp); return; }
        var ids = req.ID.Distinct().ToList();
        var state = s.Achievements!;
        int code = ids.Count == 0 ? NotExist : 0;
        foreach (long id in ids)
        {
            code = ValidateTask(state, id);
            if (code != 0) { resp.ErrorID = id; break; }
        }
        if (code == 0)
        {
            var next = state.Clone();
            var claimed = ids.Select(id => $"{id}@{state.Infos[id].Stamptime}").ToList();
            foreach (long id in ids) next.Infos[id].Status = AchievementStatusType.GetReward;
            code = await CommitClaimAsync(s, next, ids.Select(id => Rows().Rows[id].Award).Where(a => a != 0).ToList(),
                "task", string.Join(",", claimed), new { Ids = ids });
        }
        resp.Result = code;
        if (code == 0) resp.ID.AddRange(ids);
        _log.LogInformation("{Name} claims achievements {Ids}: {Result}", s.Role!.Name, string.Join(",", req.ID), code);
        await s.SendGameAsync(GetReward, resp);
        if (code == 0) await CharacterService.SyncAsync(s, Sync, s.Achievements!.Clone());
    }

    /// <summary>76003: claim a global (type 0) or category level reward, settling every unclaimed tier up to it.</summary>
    public async Task ClaimTierAsync(GameSession s, GetAchievementPointRewardReq req)
    {
        using var gate = await LockAsync(s);
        var resp = new GetAchievementPointRewardResp { Type = req.Type };
        long[] tierAwards = [];
        int code = !Ready(s) ? ErrorCodes.Unknown : ValidateTier(s.Achievements!, req.Type, req.Level, out tierAwards);
        if (code == 0)
        {
            var next = s.Achievements!.Clone();
            int from = next.AchievementLevelInfo[req.Type].CurLevelRewardLevel;
            next.AchievementLevelInfo[req.Type].CurLevelRewardLevel = req.Level;
            code = await CommitClaimAsync(s, next, tierAwards, "tier", $"{req.Type}:{from}-{req.Level}",
                new { req.Type, From = from, To = req.Level });
        }
        resp.Result = code;
        if (code == 0) resp.CurLevelRewardLevel = req.Level;
        _log.LogInformation("{Name} claims achievement level {Type}:{Level}: {Result}", s.Role!.Name, req.Type, req.Level, code);
        await s.SendGameAsync(GetPointReward, resp);
        if (code == 0) await CharacterService.SyncAsync(s, Sync, s.Achievements!.Clone());
    }

    /// <summary>
    /// 76010: every reached row of a category plus its level reward, in one transaction. Ineligible entries are
    /// skipped; with nothing eligible the first failure's code (inferred). The client's handler stops at an empty
    /// (nil) list: a levels-only success also gets the 76003 response so its rewardLevel updates (inferred, §6.3).
    /// </summary>
    public async Task ClaimAllAsync(GameSession s, GetAchievementAllRewardReq req)
    {
        using var gate = await LockAsync(s);
        var resp = new GetAchievementAllRewardResp();
        if (!Ready(s)) { resp.Result = ErrorCodes.Unknown; await s.SendGameAsync(GetAllReward, resp); return; }
        var state = s.Achievements!;
        int firstError = 0;
        var ids = new List<long>();
        foreach (long id in req.TaskIds.Distinct())
        {
            int error = ValidateTask(state, id);
            if (error == 0) ids.Add(id); else if (firstError == 0) firstError = error;
        }
        var levels = new List<(int Type, int From, int To, long[] Awards)>();
        foreach (var wanted in req.LevelRewards.GroupBy(l => l.Type).Select(g => g.MaxBy(l => l.Level)!))
        {
            int error = ValidateTier(state, wanted.Type, wanted.Level, out var tierAwards);
            if (error == 0)
                levels.Add((wanted.Type, state.AchievementLevelInfo[wanted.Type].CurLevelRewardLevel, wanted.Level, tierAwards));
            else if (firstError == 0) firstError = error;
        }
        int code;
        if (ids.Count == 0 && levels.Count == 0) code = firstError != 0 ? firstError : NotExist;
        else
        {
            var next = state.Clone();
            foreach (long id in ids) next.Infos[id].Status = AchievementStatusType.GetReward;
            foreach (var level in levels) next.AchievementLevelInfo[level.Type].CurLevelRewardLevel = level.To;
            var groups = ids.Select(id => Rows().Rows[id].Award).Where(a => a != 0).Concat(levels.SelectMany(l => l.Awards)).ToList();
            string description = string.Join(",", ids.Select(id => $"{id}@{state.Infos[id].Stamptime}")
                .Concat(levels.Select(l => $"L{l.Type}:{l.From}-{l.To}")));
            code = await CommitClaimAsync(s, next, groups, "all", description,
                new { Ids = ids, Levels = levels.Select(l => new { l.Type, l.From, l.To }).ToArray() });
        }
        resp.Result = code;
        if (code == 0)
        {
            resp.SuccessTaskIds.AddRange(ids);
            resp.SuccessLevelRewards.AddRange(levels.Select(l => new LevelRewardInfo { Type = l.Type, Level = l.To }));
        }
        _log.LogInformation("{Name} claims all achievements: {Ids} / {Levels}: {Result}", s.Role!.Name, ids.Count, levels.Count, code);
        await s.SendGameAsync(GetAllReward, resp);
        if (code == 0 && ids.Count == 0)
            foreach (var level in levels)
                await s.SendGameAsync(GetPointReward, new GetAchievementPointRewardResp
                { Type = level.Type, Result = 0, CurLevelRewardLevel = level.To });
        if (code == 0) await CharacterService.SyncAsync(s, Sync, s.Achievements!.Clone());
    }

    /// <summary>
    /// 76009 (no reply: the response type is absent from the descriptor): the client has shown a level-up popup.
    /// The request names no level, so every level table's curHintLevel becomes its current level (inferred, §6.4).
    /// </summary>
    public async Task HintSeenAsync(GameSession s)
    {
        using var gate = await LockAsync(s);
        if (!Ready(s)) return;
        var next = s.Achievements!.Clone();
        RefreshLevels(next);
        foreach (var (type, info) in next.AchievementLevelInfo) info.CurHintLevel = info.Level;
        await CommitProgressAsync(s, next);
    }

    // ---------------------------------------------------------------- GM test aid

    private async Task GmAsync(GameSession s, string[] args)
    {
        string reply;
        using (var gate = await LockAsync(s))
        {
            if (!Ready(s)) reply = "No achievement state.";
            else if (args is ["fill", var typeText, var countText])
            {
                int type = int.Parse(typeText), count = int.Parse(countText);
                var next = s.Achievements!.Clone();
                var picked = Rows().Rows.Values.Where(r => r.Type == type && !Reached(next.Infos.GetValueOrDefault(r.Id)))
                    .OrderBy(r => r.Id).Take(count).ToList();
                foreach (var row in picked) Force(next, row);
                await CommitProgressAsync(s, next);
                reply = $"Reached {picked.Count} achievements of type {type}.";
            }
            else if (args.Length >= 1 && long.TryParse(args[0], out long id) && Rows().Rows.TryGetValue(id, out var row))
            {
                var next = s.Achievements!.Clone();
                if (args.Length > 1 && args[1] == "reset") next.Infos.Remove(id);
                else Force(next, row);
                await CommitProgressAsync(s, next);
                reply = $"Achievement {id}: {next.Infos.GetValueOrDefault(id)?.Status.ToString() ?? "reset"}.";
            }
            else reply = "Usage: !ach <id> done|reset | fill <type> <count>";
        }
        await GmReplyAsync(s, reply);
    }

    private void Force(DBRoleAchievementInfo state, Row row)
    {
        var info = new AchievementInfo { AchieveId = row.Id, Status = AchievementStatusType.Complete, Stamptime = NowMs };
        info.Scores.AddRange(row.Required.Select(r => Math.Max(0, r)));
        state.Infos[row.Id] = info;
    }

    private static Task GmReplyAsync(GameSession s, string text) => GmCommands.ReplyAsync(s, text);
}
