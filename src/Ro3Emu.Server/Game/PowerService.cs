using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The role's power (战力, the number in the character panel): 31502 PlayerChangePowerNtf, 31503 PlayerPowerPackageNtf
/// and the 5066 curPower/historyMaxPower pair. The client sums every PowerUnit as
/// normalPower + seasonPower (LC_DAL_PlayerBasicInfo.lua:89-115, LC_DAL_BecomeStronger.lua:28-48).
/// <para>
/// The package holds the ten enum_PowerSystemType units the client reads (LC_PlayerInfoHelper.GetPower,
/// :420-517). BasePower is not in the package: the client derives it from BaseLvConfig._iPower of the level
/// (:422-432), and its Become Stronger detail shows currentPower minus the package sum as "other"
/// (LC_BecomeStrongerHelper.GetPowerDetailText). So curPower = BasePower + package sum + pet plan power, the pet
/// lineup being the one owner with plan power but no package field (inferred: it lands in that "other" line).
/// </para>
/// Per-unit rules (each a port of a client helper where one exists, otherwise marked inferred):
/// <list type="bullet">
/// <item>levelUpAddPointScore: the allocated STR..LUK points above the base 1, each weighted by
/// AttributeConfig._iPower (LC_ScoreHelper.CalcAttrScore, value x weight; using allocated points is inferred).</item>
/// <item>SkillAddPointScore: every learned skill row's _kAttri by CalcAttrScore plus its _iSpecialEffect rows'
/// SpecialEffectConfig._iPower (CalcSpecialScore). Inferred: no skill table carries a power column, and the
/// configured powers that do exist equal their rows' attribute weights (StrengthenTable 1050/1050 rows exactly).</item>
/// <item>titleScore: TitleConfig._iPowerAdd of the current title (UI_Sys_Role_Title_CellWidgetCtrl shows it) plus its
/// _kAttr weights; the rank is TitleService's (<see cref="TitleId"/>).</item>
/// <item>equipRankScore: the worn pieces' instance score (EquipmentRating, itemInfos[2]), EquipConfig._iPower
/// (Lua_CFG_EquipConfig.GetPower; absent in this export) and their Enchantment._iPower (season part
/// _iSeasonPower). Counting enchantments under rank is inferred: no package field names them.</item>
/// <item>equipRefineScore / equipStrengthenScore: RefiningTable / StrengthenTable._iPower of each worn grid's level,
/// plus the reached Refining/StrengthenEffectTable row's _iPower (the rows the stat sheet adds); the season part of
/// refine is the active RefiningSeasonAttrConfig groups' _iSeasonPower.</item>
/// <item>cardSuitScore: zero. Nothing in this client defines a card set bonus (no config, no reader).</item>
/// <item>cardFillInEquipScore: each socketed card's CardConfig _iPower, or _iFlashPower for a treasured card
/// (LC_CardHelper.GetPower, :3700-3742, by card id and star), plus CardUpgradeConfig._iPower of its level
/// (the _iSeasonPower column is identical in every row; counting it once is inferred).</item>
/// <item>totemScore: the current totem plan (LC_TotemHelper.GetTotemScore per totem) plus TotemLvConfig._iPower of
/// the totem level (no client reader; inferred as the level enhancement's power).</item>
/// <item>EmblemScore: the current Soul Echo plan's season power (EmblemService, item scores and reached Resonance
/// stages).</item>
/// </list>
/// Publishing: a change hook or the sweep marks the role; a changed computation is published at most once per
/// <see cref="Throttle"/> (inferred interval). maxHistoryPower is the highest total ever published and is saved,
/// with the current total for the offline leaderboard, in the role's Soul Echo blob (EmblemState.Power).
/// </summary>
public sealed class PowerService : IGameTicker
{
    public const uint ChangeNtf = 31502, PackageNtf = 31503;
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(250);
    private const long Throttle = 1000;
    private const int SweepSlices = 120, EquipGrids = 7, AttrCardLevel = 501, AttrCardStar = 502, AttrCardTreasured = 530;

    /// <summary>What the client was last told: total, best total and the package (the units are never mutated).</summary>
    public sealed record Snapshot(PowerUnit Current, PowerUnit History, PlayerPowerPackage Package);

    /// <summary>Persisted with the Soul Echo state: the last published and the best total, per unit part.</summary>
    public sealed class Record
    {
        public long CurrentNormal { get; set; }
        public long CurrentSeason { get; set; }
        public long HistoryNormal { get; set; }
        public long HistorySeason { get; set; }
    }

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly SkillService _skills;
    private readonly TotemService _totems;
    private readonly EmblemService _emblems;
    private readonly SeasonRefineService _seasonRefine;
    private readonly RoleStore _roles;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly ILogger<PowerService> _log;
    private readonly HashSet<GameSession> _dirty = new();
    private readonly HashSet<GameSession> _queued = new();
    private TimeSpan _lastProbe;
    private int _sweepIndex;
    private Dictionary<long, double>? _weights;
    private Dictionary<(long Card, long Star), JsonElement>? _cardRows;
    private Dictionary<(long Level, long Part, long Quality), JsonElement>? _cardUpgrades;

    /// <summary>The role's current TitleConfig id (TitleService).</summary>
    public Func<GameSession, long>? TitleId { get; set; }

    /// <summary>The Astral Level's lvPower (ParagonService). No package unit names it, so like the pet lineup it lands
    /// in the client's "other" line, as season power (35003 "Season Power" on the panel; inferred).</summary>
    public Func<GameSession, long>? ParagonPower { get; set; }

    public PowerService(GameData data, CharacterService character, SkillService skills, TotemService totems,
        EmblemService emblems, SeasonRefineService seasonRefine, RoleStore roles, SessionRegistry sessions, GameLoop loop,
        GmCommands gm, ILogger<PowerService> log)
    {
        this._data = data; this._character = character; this._skills = skills; this._totems = totems; this._emblems = emblems;
        this._seasonRefine = seasonRefine; this._roles = roles; this._sessions = sessions; this._loop = loop; this._log = log;
        emblems.PublishPower = s => PublishAsync(s, always: true);
        // Hooks only mark the role: several fire inside the character lock or a commit's apply.
        character.StateChanged.Add(Queue);
        character.ProgressHandlers.Add(s => { Queue(s); return Task.CompletedTask; });
        character.JobChanged.Add(s => { Queue(s); return Task.CompletedTask; });
        skills.Learned.Add(s => { Queue(s); return Task.CompletedTask; });
        gm.Register("power", "(this role's power package, 31502/31503 and the history)", GmAsync);
    }

    // ---------------------------------------------------------------- readers (5066, leaderboard, stage rewards)

    public static PowerUnit Current(GameSession s) => s.Power?.Current.Clone()
        ?? (s.Emblems.Power is { } r ? new PowerUnit { NormalPower = r.CurrentNormal, SeasonPower = r.CurrentSeason }
            : new PowerUnit { SeasonPower = s.EmblemPower });

    public static PowerUnit History(GameSession s) => s.Power?.History.Clone()
        ?? (s.Emblems.Power is { } r ? new PowerUnit { NormalPower = r.HistoryNormal, SeasonPower = r.HistorySeason }
            : new PowerUnit { SeasonPower = s.Emblems.HistorySeasonPower });

    public static long Total(PowerUnit? unit) => unit == null ? 0 : unit.NormalPower + unit.SeasonPower;

    /// <summary>The leaderboard's committed total, or null for a role whose power was never published.</summary>
    internal static long? StoredTotal(byte[]? emblems) =>
        emblems != null && JsonSerializer.Deserialize<EmblemService.EmblemState>(emblems)?.Power is { } r
            ? r.CurrentNormal + r.CurrentSeason : null;

    // ---------------------------------------------------------------- computation

    /// <summary>The role's package and total from its live (or Genre-projected) state.</summary>
    public (PowerUnit Total, PlayerPowerPackage Package) Compute(GameSession s)
    {
        var package = new PlayerPowerPackage
        {
            LevelUpAddPointScore = Normal(StatPoints(s.Progress!.Stats)),
            SkillAddPointScore = Normal(SkillScore(LearnedSkills(s))),
            TitleScore = Normal(TitleScore(TitleId?.Invoke(s) ?? 0)),
            EquipRankScore = EquipRank(s, Worn(s)),
            EquipRefineScore = EquipRefine(s, Worn(s)),
            EquipStrengthenScore = Normal(EquipStrengthen(s, Worn(s))),
            CardSuitScore = new PowerUnit(),
            CardFillInEquipScore = Normal(s.Items.Where(i => i.Grid < 0).Sum(CardScore)),
            TotemScore = Normal(TotemScore(s)),
            EmblemScore = _emblems.GenrePower(s, s.Emblems),
        };
        var total = new PowerUnit { NormalPower = BasePower(s.Progress.Level) + PetPower(s), SeasonPower = ParagonPower?.Invoke(s) ?? 0 };
        foreach (var unit in Units(package)) { total.NormalPower += unit.NormalPower; total.SeasonPower += unit.SeasonPower; }
        return (total, package);
    }

    /// <summary>
    /// A Build (82825/5008) preset's power: the selected child systems, i.e. the total without what
    /// LC_PlayerInfoHelper.GetPlanOtherPower adds outside the plans (BasePower + Title + CardSuit, :519-541).
    /// Call under GenreService's projection.
    /// </summary>
    public PowerUnit GenrePower(GameSession s)
    {
        var (total, package) = Compute(s);
        total.NormalPower -= BasePower(s.Progress!.Level) + package.TitleScore.NormalPower + package.CardSuitScore.NormalPower;
        total.SeasonPower -= package.TitleScore.SeasonPower + package.CardSuitScore.SeasonPower;
        return total;
    }

    private static IEnumerable<PowerUnit> Units(PlayerPowerPackage p) =>
    [
        p.LevelUpAddPointScore, p.SkillAddPointScore, p.TitleScore, p.EquipRankScore, p.EquipRefineScore,
        p.EquipStrengthenScore, p.CardSuitScore, p.CardFillInEquipScore, p.TotemScore, p.EmblemScore,
    ];

    private static PowerUnit Normal(long value) => new() { NormalPower = value };

    private double Weight(long attr)
    {
        _weights ??= _data.Table("AttributeConfig").Values.Where(r => r.TryGetProperty("_iPower", out _))
            .ToDictionary(r => GameData.Int(r, "_iID"), r => GameData.Number(r.GetProperty("_iPower")) ?? 0);
        return _weights.GetValueOrDefault(attr);
    }

    /// <summary>LC_ScoreHelper.CalcAttrScore: value x AttributeConfig._iPower.</summary>
    private double AttrScore(IEnumerable<(long Id, double Value)> attrs) => attrs.Sum(a => a.Value * Weight(a.Id));

    /// <summary>LC_ScoreHelper.CalcSpecialScore: SpecialEffectConfig._iPower.</summary>
    private long SpecialScore(long id) => _data.Row("SpecialEffectConfig", id) is { } row ? GameData.Int(row, "_iPower") : 0;

    private static IEnumerable<long> Ids(JsonElement row, string field) => row.TryGetProperty(field, out var list)
        && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(e => (long)(GameData.Number(e) ?? 0)).Where(id => id > 0) : [];

    /// <summary>BaseLvConfig[level]._iPower (Lua_CFG_BaseLvConfig.GetPower; 120 x level in this export).</summary>
    internal long BasePower(int level) => _data.Row("BaseLvConfig", level) is { } row ? GameData.Int(row, "_iPower") : 0;

    /// <summary>Allocated points (value - 1) of STR..LUK, weighted, rounded up like GetTotemScore (inferred rounding).</summary>
    internal long StatPoints(IReadOnlyList<int> stats) =>
        (long)Math.Ceiling(AttrScore(Enumerable.Range(0, Math.Min(6, stats.Count)).Select(i => ((long)i + 1, (double)Math.Max(0, stats[i] - 1)))) - 1e-9);

    private long StatPlan(PlanBaseProperty plan) =>
        StatPoints(Enumerable.Range(1, 6).Select(i => plan.BaseProperty.TryGetValue(i, out int v) ? v : 1).ToArray());

    private IEnumerable<(int Group, int Level)> LearnedSkills(GameSession s)
    {
        var p = s.Progress!;
        foreach (int group in _skills.Tree(p.Career).Select(t => t.Group).Concat(p.Skills.Keys).Distinct())
            if (_skills.Level(p, group) is var level and > 0) yield return (group, level);
    }

    internal long SkillScore(IEnumerable<(int Group, int Level)> learned)
    {
        double score = 0;
        foreach (var (group, level) in learned)
            if (_skills.RealId(group, level) is var id and > 0 && _data.Row("SkillConfig", id) is { } row)
                score += AttrScore(GameData.Pairs(row, "_kAttri")) + Ids(row, "_iSpecialEffect").Sum(SpecialScore);
        return (long)Math.Ceiling(score - 1e-9);
    }

    private long TitleScore(long title) => title > 0 && _data.Row("TitleConfig", title) is { } row
        ? GameData.Int(row, "_iPowerAdd") + (long)Math.Ceiling(AttrScore(GameData.Pairs(row, "_kAttr")) - 1e-9) : 0;

    private static List<InvItem> Worn(GameSession s) => s.Items.Where(i => i.Grid is >= 1 and <= EquipGrids).ToList();

    private PowerUnit EquipRank(GameSession s, IEnumerable<InvItem> worn)
    {
        var unit = new PowerUnit();
        foreach (var item in worn)
        {
            unit.NormalPower += EquipmentRating.Calculate(item, _data);
            if (_data.Row("ItemConfig", item.Entry) is { } config && GameData.Int(config, "_iEquipId") is var equip and > 0
                && _data.Row("EquipConfig", equip) is { } equipRow) unit.NormalPower += GameData.Int(equipRow, "_iPower");
            foreach (int enchant in item.Enchantments.Keys)
                if (_data.Row("Enchantment", enchant) is { } row)
                {
                    unit.NormalPower += GameData.Int(row, "_iPower");
                    unit.SeasonPower += GameData.Int(row, "_iSeasonPower");
                }
        }
        return unit;
    }

    private PowerUnit EquipRefine(GameSession s, IEnumerable<InvItem> worn) => new()
    {
        NormalPower = worn.Select(i => i.Grid).Distinct()
            .Sum(g => _character.RefineRow(g, s.EquipLevels.Refine.GetValueOrDefault(g)) is { } row ? GameData.Int(row, "_iPower") : 0)
            + Resonance(s, "RefiningEffectTable"),
        SeasonPower = _seasonRefine.ActiveGroups(s).Sum(group => GameData.Int(group, "_iSeasonPower")),
    };

    private long EquipStrengthen(GameSession s, IEnumerable<InvItem> worn) => worn.Select(i => i.Grid).Distinct()
        .Sum(g => _character.StrengthenRow(g, s.EquipLevels.Strength.GetValueOrDefault(g)) is { } row ? GameData.Int(row, "_iPower") : 0)
        + Resonance(s, "StrengthenEffectTable");

    /// <summary>The reached Strengthen/RefiningEffectTable row's _iPower (CharacterService.Resonance picks the row).</summary>
    private long Resonance(GameSession s, string table)
    {
        int level = _character.Resonance(s, table);
        return level <= 0 ? 0 : _data.Table(table).Values.Where(r => GameData.Int(r, "_iEffectLev") == level)
            .Select(r => GameData.Int(r, "_iPower")).FirstOrDefault();
    }

    /// <summary>LC_CardHelper.GetPower: CardConfig by (card, star), _iFlashPower for a treasured (Flash) card.</summary>
    private long CardRowPower(long card, long star, bool flash)
    {
        _cardRows ??= _data.Table("CardConfig").Values.GroupBy(r => (GameData.Int(r, "_iCardID"), GameData.Int(r, "_iTier")))
            .ToDictionary(g => g.Key, g => g.First());
        return _cardRows.TryGetValue((card, star), out var row) ? GameData.Int(row, flash ? "_iFlashPower" : "_iPower") : 0;
    }

    private long CardScore(InvItem card)
    {
        long score = CardRowPower(card.Entry, card.Attrs?.GetValueOrDefault(AttrCardStar) ?? 0,
            card.Attrs?.GetValueOrDefault(AttrCardTreasured) == 1);
        if (_data.Row("ItemConfig", card.Entry) is { } item)
        {
            _cardUpgrades ??= _data.Table("CardUpgradeConfig").Values
                .GroupBy(r => (GameData.Int(r, "_iLevel"), GameData.Int(r, "_iParts"), GameData.Int(r, "_iQuality")))
                .ToDictionary(g => g.Key, g => g.First());
            if (_cardUpgrades.TryGetValue((card.Attrs?.GetValueOrDefault(AttrCardLevel) ?? 0, GameData.Int(item, "_iCardPart"),
                GameData.Int(item, "_iQuality")), out var upgrade)) score += GameData.Int(upgrade, "_iPower");
        }
        return score;
    }

    private long TotemScore(GameSession s)
    {
        if (s.Totems == null) return 0;
        long score = _totems.CurrentPlanPower(s, s.Totems).NormalPower;
        int level = _totems.Level(s.Totems.TotemTotalExp);
        if (level > 0) score += _data.Table("TotemLvConfig").Values.Where(r => GameData.Int(r, "_iLv") == level)
            .Select(r => GameData.Int(r, "_iPower")).FirstOrDefault();
        return score;
    }

    private static long PetPower(GameSession s) =>
        s.Pets?.SetupPlans.GetValueOrDefault(s.Pets.CurrentPlan)?.Power is { } unit ? unit.NormalPower + unit.SeasonPower : 0;

    // ---------------------------------------------------------------- stored plan power (5067, 5025, 5007, 5026)

    /// <summary>Set at role load: every plan dataset carries each plan's power when it is diffed and sent.</summary>
    public void Load(GameSession session) => session.PlanPowers = (id, message) => FillPlans(session, id, message);

    private void FillPlans(GameSession s, uint id, IMessage message)
    {
        if (s.Progress == null) return;
        switch (message)
        {
            case BaseAttrInfo attrs when id == CharacterService.BaseAttrSync:
                foreach (var plan in attrs.AllPlanBaseProperty.Values) plan.Power = Normal(StatPlan(plan));
                break;
            case DBSkillData skillData when id == SkillService.SkillDataSync:
                // The current plan's assignments are regenerated from the learned levels by SkillService.Build.
                foreach (var plan in skillData.SkillAssignPlans)
                    plan.Power = Normal(SkillScore(plan.SkillAssigns.Values.Select(a => (a.SkillId, a.SkillLevel))));
                break;
            case DBEquipInfo equip when id == CharacterService.EquipSync:
                foreach (var plan in equip.EquipPlans.Values) plan.Power = EquipPlan(s, plan);
                break;
            case DBStorageHoleInfo cards when id == CardService.StorageHoleSync:
                foreach (var (index, plan) in cards.StoragePlans) plan.Power = Normal(CardPlan(s, cards.CurrentPlan == index, plan));
                break;
        }
    }

    /// <summary>A gear plan: rank, refine and strengthen of the pieces it places (inferred aggregation).</summary>
    private PowerUnit EquipPlan(GameSession s, DBEquipPlan plan)
    {
        var placed = plan.ItemsEquipGrid.Where(p => p.Value != 0 && p.Key is >= 1 and <= EquipGrids)
            .Select(p => (Grid: p.Key, Item: s.Items.FirstOrDefault(i => i.Guid == p.Value))).Where(p => p.Item != null)
            .Select(p => { var copy = p.Item!.Clone(); copy.Grid = p.Grid; return copy; }).ToList();
        var rank = EquipRank(s, placed);
        var refine = EquipRefine(s, placed);
        return new PowerUnit
        {
            NormalPower = rank.NormalPower + refine.NormalPower + EquipStrengthen(s, placed),
            SeasonPower = rank.SeasonPower + refine.SeasonPower,
        };
    }

    /// <summary>A card plan: the socketed instances for the current plan, the saved card/star picks otherwise.</summary>
    private long CardPlan(GameSession s, bool current, DBStoragePlanInfo plan) => current
        ? s.Items.Where(i => i.Grid < 0).Sum(CardScore)
        : plan.CardInfo.Values.SelectMany(g => g.CardSlotInfo.Values).Where(c => c.ItemId > 0)
            .Sum(c => CardRowPower(c.ItemId, c.StarLevel, c.Treasured == 1));

    // ---------------------------------------------------------------- publishing

    /// <summary>
    /// Compute and, when it changed (or <paramref name="always"/>), send 31502 then 31503 and the 5066 pair. The caller
    /// holds the character lock (or runs the role's login). A higher total raises and saves maxHistoryPower.
    /// </summary>
    public async Task PublishAsync(GameSession s, bool always = false)
    {
        if (s.Role == null || s.Progress == null) return;
        var (total, package) = Compute(s);
        var before = s.Power;
        if (!always && before != null && before.Current.Equals(total) && before.Package.Equals(package)) return;
        var previous = before?.Current ?? Current(s);
        var history = History(s);
        if (Total(total) > Total(history)) history = total.Clone();
        var snapshot = new Snapshot(total, history, package);
        var role = s.Role;
        var attachment = s.Attachment;
        // The client may disconnect as soon as it sees 31502. Persist before publishing or changing the cached score.
        await SaveAsync(s, snapshot);
        if (s.Role != role || !ReferenceEquals(s.Attachment, attachment)) return;
        s.Power = snapshot;
        s.PowerPublishedMs = MonsterService.NowMs;
        await s.SendGameAsync(ChangeNtf, new PlayerChangePowerNtf
        {
            CurPower = total.Clone(),
            ChangePower = new PowerUnit { NormalPower = total.NormalPower - previous.NormalPower, SeasonPower = total.SeasonPower - previous.SeasonPower },
            MaxHistoryPower = history.Clone(), PowerPackage = package.Clone(),
        });
        await s.SendGameAsync(PackageNtf, new PlayerPowerPackageNtf { PowerPackage = package.Clone() });
        await _character.SyncBaseInfoAsync(s);
    }

    /// <summary>
    /// End of role load: the snapshot the login 5066 carries (its curPower/historyMaxPower), so the later login publish
    /// changes 5066 only when an owner loaded at login (pets) moved the total. Nothing is sent or saved here.
    /// </summary>
    public void Prime(GameSession s)
    {
        if (s.Role == null || s.Progress == null) return;
        var (total, package) = Compute(s);
        var history = History(s);
        s.Power = new Snapshot(total, Total(total) > Total(history) ? total.Clone() : history, package);
    }

    /// <summary>Login: one publish after every power owner (pets, Build) is loaded, before functions open panels.</summary>
    public Task LoginAsync(GameSession s) => PublishAsync(s, always: true);

    private static bool Live(GameSession s) => s.Power != null && s.PowerPublishedMs > 0 && s.Role != null && s.Progress != null;

    private async Task SaveAsync(GameSession s, Snapshot snapshot)
    {
        var record = new Record
        {
            CurrentNormal = snapshot.Current.NormalPower, CurrentSeason = snapshot.Current.SeasonPower,
            HistoryNormal = snapshot.History.NormalPower, HistorySeason = snapshot.History.SeasonPower,
        };
        if (s.Emblems.Power is { } saved && saved.CurrentNormal == record.CurrentNormal && saved.CurrentSeason == record.CurrentSeason
            && saved.HistoryNormal == record.HistoryNormal && saved.HistorySeason == record.HistorySeason) return;
        var next = JsonSerializer.Deserialize<EmblemService.EmblemState>(JsonSerializer.SerializeToUtf8Bytes(s.Emblems))!;
        next.Power = record;
        var transaction = _roles.NewTransaction();
        transaction.RoleBlob(s.Role!.Id, "emblems", JsonSerializer.SerializeToUtf8Bytes(next));
        var echoes = s.Emblems;
        await _roles.CommitAsync(transaction, "power/record", _ => echoes.Power = record);
    }

    private void Queue(GameSession s)
    {
        if (Live(s)) _dirty.Add(s);
    }

    /// <summary>
    /// Every 250 ms the hook-marked roles and one 120th of all roles (each role every 30 s, which also catches owners
    /// without a hook: skills, pets, totems; 30 s keeps ~5 roles per probe at 500 online) are recomputed read-only; a role whose power differs from what it was told
    /// is published under its own lock, at most once per second (inferred throttle).
    /// </summary>
    public void Tick(GameTime time)
    {
        if (time.Now - _lastProbe < ProbeEvery) return;
        _lastProbe = time.Now;
        var all = _sessions.All();
        var probe = new HashSet<GameSession>(_dirty);
        _dirty.Clear();
        if (all.Count > 0)
        {
            int slice = (all.Count + SweepSlices - 1) / SweepSlices;
            for (int i = 0; i < slice; i++) probe.Add(all[(_sweepIndex + i) % all.Count]);
            _sweepIndex = (_sweepIndex + slice) % all.Count;
        }
        long now = MonsterService.NowMs;
        foreach (var s in probe)
        {
            if (!Live(s) || _queued.Contains(s)) continue;
            if (s.Handoff != null || now - s.PowerPublishedMs < Throttle) { _dirty.Add(s); continue; }
            try
            {
                var (total, package) = Compute(s);
                if (s.Power!.Current.Equals(total) && s.Power.Package.Equals(package)) continue;
            }
            catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException)
            {
                // A role between loads (owners not yet attached) is probed again by the next sweep.
                _log.LogDebug(e, "Power probe of {Role} skipped", s.Role!.Id);
                continue;
            }
            _queued.Add(s);
            _loop.PostOwned(s, async () =>
            {
                _queued.Remove(s);
                if (!Live(s)) return;
                using var gate = await CharacterService.Lock(s);
                if (Live(s)) await PublishAsync(s);
            }, "power refresh", discarded: () => _queued.Remove(s));
        }
    }

    private async Task GmAsync(GameSession s, string[] args)
    {
        var (total, package) = Compute(s);
        string Unit(PowerUnit u) => u.SeasonPower == 0 ? $"{u.NormalPower}" : $"{u.NormalPower}+{u.SeasonPower}s";
        await GmCommands.ReplyAsync(s, $"power {Total(total)} ({Unit(total)}), history {Total(History(s))}: base {BasePower(s.Progress!.Level)}, "
            + $"points {Unit(package.LevelUpAddPointScore)}, skills {Unit(package.SkillAddPointScore)}, title {Unit(package.TitleScore)}, "
            + $"rank {Unit(package.EquipRankScore)}, refine {Unit(package.EquipRefineScore)}, strengthen {Unit(package.EquipStrengthenScore)}, "
            + $"cards {Unit(package.CardFillInEquipScore)}, totem {Unit(package.TotemScore)}, echo {Unit(package.EmblemScore)}, pet {PetPower(s)}");
        _log.LogInformation("{Name} power {Total}", s.Role!.Name, Total(total));
    }
}
