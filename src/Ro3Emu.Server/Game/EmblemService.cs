using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Soul Echo: generated itemInfos 1001-1100, saved 5005 plans,
/// 82422/82423 deployment and 82424 feeding. Worn instances remain in ordinary pack 5 with Grid=0;
/// itemInfos 10001/10002 and the active plan are two projections of the same selection.
/// </summary>
public sealed partial class EmblemService
{
    public const uint PlanSync = 5005, SetUp = 82422, SetDown = 82423, Upgrade = 82424;
    // Five physical slots: UI_Sys_SoulDetailsWidgetCtrl.lua:94-110 builds five widgets (:663-670 bounds the position).
    private const int FirstSlot = 1, LastSlot = 5, Source = 10001, Position = 10002;
    private const int Level = 1001, Exp = 1002, TotalExp = 1003;
    private static readonly IReadOnlyDictionary<int, long> MaterialXp = new Dictionary<int, long>
    {
        [1257000] = 100, [1257001] = 1000, [1257002] = 10000,
    };

    public sealed class EmblemState
    {
        public Dictionary<int, Plan> Plans { get; set; } = new();
        public int CurrentPlan { get; set; }
        public long HistorySeasonPower { get; set; }
        /// <summary>The role's published and best power total (PowerService); null before its first publish.</summary>
        public PowerService.Record? Power { get; set; }
    }

    public sealed class Plan
    {
        public Dictionary<int, long> Slots { get; set; } = new();
        public string Name { get; set; } = "";
    }

    private readonly GameData data;
    private readonly CharacterService character;
    private readonly FunctionService functions;
    private readonly SeasonService seasons;
    private readonly RoleStore roles;
    private readonly ILogger<EmblemService> log;

    /// <summary>After a committed 82424 upgrade: the levels it gained (quest target 317002). Listeners post their work.</summary>
    public List<Action<GameSession, int>> Upgraded { get; } = new();

    public EmblemService(GameData data, CharacterService character, FunctionService functions, SeasonService seasons,
        RoleStore roles, ILogger<EmblemService> log)
    {
        this.data = data;
        this.character = character;
        this.functions = functions;
        this.seasons = seasons;
        this.roles = roles;
        this.log = log;
        character.EmblemAttrFactory = GenerateAttrs;
        character.ExtraAttributes.Add(ResonanceAttributes);
    }

    /// <summary>Set by PowerService: publish 31502/31503/5066 after a Soul Echo change (the caller holds the lock).</summary>
    internal Func<GameSession, Task>? PublishPower { get; set; }

    /// <summary>Load before 5009 and ENTITYSELF; roll missing old instance attrs and repair stale active refs (inferred).</summary>
    public async Task LoadAsync(GameSession session)
    {
        var saved = roles.LoadEmblems(session.Role!.Id);
        var next = saved == null ? new EmblemState() : JsonSerializer.Deserialize<EmblemState>(saved) ?? new EmblemState();
        var beforeItems = session.Items.Select(i => i.Clone()).ToArray();
        var afterItems = beforeItems.Select(i => i.Clone()).ToList();
        bool changed = saved == null;
        // Plan key 0 is an inferred default. Empty grids are initialized by LC_DAL_Knapsack.InitGridInfo:1239-1281,
        // called before processing the initial 5009 at :134-137; LC_DAL_Emblem.IsSchemeEmpty accepts an empty plan.
        for (int index = 0; index < PlanCount; index++)
            if (!next.Plans.ContainsKey(index)) { next.Plans[index] = new Plan(); changed = true; }
        if (!next.Plans.ContainsKey(next.CurrentPlan))
        {
            next.CurrentPlan = next.Plans.ContainsKey(0) ? 0 : next.Plans.Keys.Min();
            changed = true;
        }
        foreach (var item in afterItems.Where(i => IsEmblem(data, i.Entry)))
        {
            // Pre-feature grants followed ItemConfig._iBagTab=1. The client resolves Soul Echo instances in pack 5.
            if (item.Grid == 0 && (item.Pack != 5 || item.Sort <= 0))
            {
                long nextSort = afterItems.Where(i => i.Grid == 0 && i.Pack == 5).Select(i => i.Sort).DefaultIfEmpty(0).Max() + 1;
                item.Pack = 5;
                item.Sort = nextSort;
                changed = true;
            }
            if (item.Attrs == null || !item.Attrs.ContainsKey(Level))
            {
                item.Attrs = GenerateAttrs(item.Entry);
                changed = true;
            }
        }
        var active = next.Plans[next.CurrentPlan];
        var seen = new HashSet<long>();
        foreach (var (slot, guid) in active.Slots.OrderBy(p => p.Key).ToList())
        {
            if (slot is < FirstSlot or > LastSlot || !seen.Add(guid)
                || !afterItems.Any(i => i.Guid == guid && i.Grid == 0 && IsEmblem(data, i.Entry)))
            {
                active.Slots.Remove(slot);
                changed = true;
            }
        }
        foreach (var item in afterItems.Where(i => IsEmblem(data, i.Entry)))
        {
            int slot = active.Slots.FirstOrDefault(p => p.Value == item.Guid).Key;
            item.Attrs ??= new();
            if (item.Attrs.GetValueOrDefault(Source) == (slot == 0 ? 0 : 2)
                && item.Attrs.GetValueOrDefault(Position) == slot) continue;
            item.Attrs[Source] = slot == 0 ? 0 : 2;
            item.Attrs[Position] = slot;
            changed = true;
        }
        long current = PlanPower(afterItems, active);
        if (current > next.HistorySeasonPower) { next.HistorySeasonPower = current; changed = true; }
        if (changed)
        {
            var builder = roles.NewTransaction();
            builder.ItemDelta(session.Role.Id, beforeItems, afterItems);
            builder.RoleBlob(session.Role.Id, "emblems", JsonSerializer.SerializeToUtf8Bytes(next));
            builder.WithEconomy(character.FamilyEconomy(session, "cd13b/emblem-login-reconcile", [], [],
                new { CurrentPlan = next.CurrentPlan, Power = current }));
            await roles.CommitAsync(builder, "emblem/login-reconcile", _ =>
            {
                PublishItems(session, afterItems);
                session.Emblems = next;
                session.EmblemPower = current;
                session.EmblemPlanSwitchUtc = default;
            });
        }
        else
        {
            session.Emblems = next;
            session.EmblemPower = current;
            session.EmblemPlanSwitchUtc = default;
        }
    }

    /// <summary>5005 only: PowerService publishes the power once every power owner is loaded (RoleSystems).</summary>
    public async Task LoginAsync(GameSession session)
    {
        session.EmblemPower = PlanPower(session, Active(session)).SeasonPower;
        await SyncAsync(session);
    }

    private static Plan Active(GameSession session) => session.Emblems.Plans[session.Emblems.CurrentPlan];

    private static EmblemState CopyState(EmblemState state) =>
        JsonSerializer.Deserialize<EmblemState>(JsonSerializer.SerializeToUtf8Bytes(state))!;

    private static void PublishItems(GameSession session, IReadOnlyList<InvItem> staged)
    {
        var existing = session.Items.ToDictionary(i => i.Guid);
        session.Items.Clear();
        foreach (var item in staged)
        {
            if (!existing.TryGetValue(item.Guid, out var live)) { session.Items.Add(item); continue; }
            live.Stack = item.Stack;
            live.Pack = item.Pack;
            live.Sort = item.Sort;
            live.Grid = item.Grid;
            live.Attrs = item.Attrs;
            session.Items.Add(live);
        }
    }

    /// <summary>The plan's item scores plus its reached Echo Resonance stages' season power (StagePower).</summary>
    private long PlanPower(IReadOnlyList<InvItem> inventory, Plan plan)
    {
        var worn = plan.Slots.Values.Distinct().Select(guid => inventory.FirstOrDefault(i => i.Guid == guid)).OfType<InvItem>().ToList();
        return worn.Sum(Score) + StagePower(worn, data);
    }

    /// <summary>
    /// The Soul Echo plan power of a role from its committed "emblems" blob and item rows, for roles that are not
    /// online (leaderboards). Slots that do not resolve to an owned instance count 0, as in <see cref="PlanPower"/>.
    /// </summary>
    internal long StoredPower(byte[]? emblems, IReadOnlyList<InvItem> inventory)
    {
        if (emblems == null || JsonSerializer.Deserialize<EmblemState>(emblems) is not { } state
            || !state.Plans.TryGetValue(state.CurrentPlan, out var plan)) return 0;
        return PlanPower(inventory, plan);
    }

    private long StageHistory(EmblemState state, IReadOnlyList<InvItem> inventory)
    {
        long current = PlanPower(inventory, state.Plans[state.CurrentPlan]);
        state.HistorySeasonPower = Math.Max(state.HistorySeasonPower, current);
        return current;
    }

    private async Task CommitEmblemsAsync(GameSession session, string label, EmblemState next,
        IReadOnlyList<InvItem> beforeItems, IReadOnlyList<InvItem> afterItems,
        IReadOnlyList<(int Entry, long Count)> costs, object outcome, Action? applied = null)
    {
        long newPower = StageHistory(next, afterItems);
        var builder = roles.NewTransaction();
        builder.ItemDelta(session.Role!.Id, beforeItems, afterItems);
        builder.RoleBlob(session.Role.Id, "emblems", JsonSerializer.SerializeToUtf8Bytes(next));
        builder.WithEconomy(character.FamilyEconomy(session, "cd13b/" + label, costs, [], outcome));
        await roles.CommitAsync(builder, "emblem/" + label, _ =>
        {
            PublishItems(session, afterItems);
            session.Emblems = next;
            session.EmblemPower = newPower;
            applied?.Invoke();
        });
    }

    /// <summary>Check that a Build preset references distinct, owned Soul Echo instances before selection.</summary>
    internal int ValidateGenrePlan(GameSession session, EmblemState state, int index)
    {
        if (!state.Plans.TryGetValue(index, out var plan)) return ErrorCodes.Parameter;
        var distinct = new HashSet<long>();
        foreach (var (slot, guid) in plan.Slots)
        {
            if (slot is < FirstSlot or > LastSlot || !distinct.Add(guid)) return ErrorCodes.Parameter;
            var item = session.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0);
            if (item == null || !IsEmblem(data, item.Entry)) return ErrorCodes.ItemMissing;
            if (!SeasonValid(item.Entry)) return ErrorCodes.FeatureUnavailable;
        }
        state.CurrentPlan = index;
        return 0;
    }

    internal PowerUnit GenrePower(GameSession session, EmblemState state) => state.Plans.TryGetValue(state.CurrentPlan, out var plan)
        ? PlanPower(session, plan) : new PowerUnit();

    internal async Task SyncGenrePlanAsync(GameSession session, long previousPower)
    {
        await SyncAsync(session);
        await PowerAsync(session, previousPower);
    }

    /// <summary>All active, extant GUIDs; the plan is authoritative for stat input.</summary>
    public static IEnumerable<InvItem> Worn(GameSession session)
    {
        if (!session.Emblems.Plans.TryGetValue(session.Emblems.CurrentPlan, out var plan)) yield break;
        foreach (var guid in plan.Slots.Values.Distinct())
            if (session.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0) is { } item) yield return item;
    }

    /// <summary>
    /// Echo Resonance (残响共鸣): lit marks (itemInfos 1021+, up to the entry's _kInitialMark count) of the worn echoes,
    /// counted per mark id (LC_EmblemHelper.GetAllMarkEquipedNum, LC_ItemProtoParse.lua:1055-1100), reach the
    /// EmblemMarkConfig stage whose _iMarkNum they meet (3/6/10, LC_SoulHelper.GetLevelByMarkNum). Mark id → stage.
    /// </summary>
    internal static Dictionary<long, long> Resonances(GameSession session, GameData data) => Resonances(Worn(session), data);

    internal static Dictionary<long, long> Resonances(IEnumerable<InvItem> worn, GameData data)
    {
        var marks = MarkCounts(worn, data);
        var stages = new Dictionary<long, long>();
        foreach (var row in data.Table("EmblemMarkConfig").Values)
        {
            long mark = GameData.Int(row, "_iEmblemMarkID"), stage = GameData.Int(row, "_iMarkStage");
            if (stage > 0 && marks.GetValueOrDefault(mark) >= GameData.Int(row, "_iMarkNum") && stage > stages.GetValueOrDefault(mark))
                stages[mark] = stage;
        }
        return stages;
    }

    /// <summary>Lit marks per mark id over the given echoes (LC_EmblemHelper.GetAllMarkEquipedNum).</summary>
    private static Dictionary<long, int> MarkCounts(IEnumerable<InvItem> worn, GameData data)
    {
        var marks = new Dictionary<long, int>();
        foreach (var item in worn)
        {
            int lit = data.Row("EmblemConfig", item.Entry) is { } row && row.TryGetProperty("_kInitialMark", out var initial)
                && initial.ValueKind == JsonValueKind.Array ? initial.GetArrayLength() : 0;
            for (int i = 0; i < lit && item.Attrs?.TryGetValue(1021 + i, out long mark) == true; i++)
                marks[mark] = marks.GetValueOrDefault(mark) + 1;
        }
        return marks;
    }

    public static bool IsReserved(GameSession session, InvItem item) =>
        item.Attrs?.GetValueOrDefault(Source) == 2 || session.Emblems.Plans.Values.Any(p => p.Slots.Values.Contains(item.Guid));

    public static bool IsEmblem(GameData data, int entry) =>
        data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == 8 && GameData.Int(row, "_iSubType") == 800;

    /// <summary>Client item parser's paired contiguous primary/subattribute ranges (LC_ItemProtoParse.lua:957-1054).</summary>
    public static IEnumerable<(int Id, double Value)> Attributes(InvItem item, GameData data)
    {
        var a = item.Attrs;
        if (a == null) yield break;
        for (int i = 0; i < 10 && a.TryGetValue(1031 + i, out long id) && a.TryGetValue(1041 + i, out long value); i++)
            yield return ((int)id, value);
        // Subattribute keys hold config row IDs, not AttributeConfig IDs.
        for (int i = 0; i < 20 && a.TryGetValue(1051 + i, out long rowId) && a.TryGetValue(1071 + i, out long value); i++)
            if (data.Row("EmblemSubAttriGroupConfig", rowId) is { } row && GameData.Int(row, "_iSubAttriID") is var id and > 0)
                yield return ((int)id, value);
    }

    private DBEmblemInfo Build(GameSession session)
    {
        var info = new DBEmblemInfo { CurrentPlan = session.Emblems.CurrentPlan, Inited = true };
        foreach (var (index, saved) in session.Emblems.Plans)
        {
            var plan = new DBEmblemPlan { PlanName = saved.Name, Power = PlanPower(session, saved) };
            foreach (var (slot, guid) in saved.Slots) plan.SetUpEmblem[slot] = guid;
            info.EmblePlans[index] = plan;
        }
        return info;
    }

    private Task SyncAsync(GameSession session) => CharacterService.SyncAsync(session, PlanSync, Build(session));

    private PowerUnit PlanPower(GameSession session, Plan plan) => new() { SeasonPower = PlanPower(session.Items, plan) };

    private long Score(InvItem item)
    {
        long score = data.Row("EmblemConfig", item.Entry) is { } config ? GameData.Int(config, "_iSeasonPower") : 0;
        int quality = Quality(item.Entry), level = (int)(item.Attrs?.GetValueOrDefault(Level) ?? 0);
        score += LevelRow(quality, level) is { } row ? GameData.Int(row, "_iSeasonPower") : 0;
        for (int i = 0; i < 20 && item.Attrs?.TryGetValue(1051 + i, out long rowId) == true
            && item.Attrs.TryGetValue(1071 + i, out _); i++)
            if (data.Row("EmblemSubAttriGroupConfig", rowId) is { } sub) score += GameData.Int(sub, "_iSeasonPower");
        for (int i = 0; i < 10 && item.Attrs?.TryGetValue(1091 + i, out long specialId) == true; i++)
            if (data.Row("SpecialEffectConfig", specialId) is { } special) score += GameData.Int(special, "_iPower");
        // Item score sums the configured base, level, subrow, special power (LC_EmblemHelper.GetEmblemScore).
        // The plan adds its reached mark stages on top (PlanPower, StagePower). No _kSpecialAttribute generation
        // pool exists here; preexisting specials can score but their combat evaluator is outside this slice.
        return score;
    }

    /// <summary>The Soul Echo part changed: PowerService republishes the whole package (31502, 31503, 5066).</summary>
    private async Task PowerAsync(GameSession session, long previous)
    {
        session.EmblemPower = PlanPower(session, Active(session)).SeasonPower;
        if (PublishPower != null) await PublishPower(session);
    }

    /// <summary>One accepted change: 5009, 5005, final properties/AOI, power, then 5066 (packet order inferred).</summary>
    private async Task ChangedAsync(GameSession session, long previous, bool inventory)
    {
        if (inventory) await character.ItemsChangedAsync(session, stats: false);
        await SyncAsync(session);
        await character.RefreshStatsAsync(session);
        await PowerAsync(session, previous);
    }

    private int Gate(GameSession session) => session.Tasks != null && data.Row("FunctionConfig", 55) != null
        && TaskService.IsDelivered(session, 10103040) && functions.IsOpen(session, 55) ? 0 : ErrorCodes.FunctionLocked;

    /// <summary>82422: validate slot, owned generated item, current season, and active-plan uniqueness first.</summary>
    public async Task SetUpAsync(GameSession session, SetUpEmblemReq req)
    {
        using var _ = await CharacterService.Lock(session);
        var plan = Active(session);
        var item = session.Items.FirstOrDefault(i => i.Guid == req.EmblemGuid && i.Grid == 0);
        int code = Gate(session);
        if (code == 0) code = req.EmblemPos is < FirstSlot or > LastSlot ? ErrorCodes.EmblemSlot
            : item == null || !IsEmblem(data, item.Entry) ? ErrorCodes.EmblemInvalid
            : !SeasonValid(item.Entry) ? ErrorCodes.EmblemOtherSeason
            : plan.Slots.Values.Contains(req.EmblemGuid) ? ErrorCodes.EmblemAlreadyDeployed : 0;
        long oldGuid = code == 0 ? plan.Slots.GetValueOrDefault(req.EmblemPos) : 0;
        if (code == 0)
        {
            long previous = session.EmblemPower;
            var beforeItems = session.Items.Select(i => i.Clone()).ToArray();
            var afterItems = beforeItems.Select(i => i.Clone()).ToList();
            var next = CopyState(session.Emblems);
            if (oldGuid != 0 && afterItems.FirstOrDefault(i => i.Guid == oldGuid) is { } old)
            {
                old.Attrs ??= new(); old.Attrs[Source] = 0; old.Attrs[Position] = 0;
            }
            var deployed = afterItems.Single(i => i.Guid == req.EmblemGuid);
            deployed.Attrs ??= GenerateAttrs(deployed.Entry) ?? new();
            deployed.Attrs[Source] = 2; deployed.Attrs[Position] = req.EmblemPos;
            next.Plans[next.CurrentPlan].Slots[req.EmblemPos] = deployed.Guid;
            await CommitEmblemsAsync(session, "emblem-setup", next, beforeItems, afterItems, [],
                new { req.EmblemPos, req.EmblemGuid, OldEmblemGuid = oldGuid });
            await ChangedAsync(session, previous, inventory: true);
        }
        await session.SendGameAsync(SetUp, new SetUpEmblemResp { Result = code, EmblemPos = req.EmblemPos, EmblemGuid = req.EmblemGuid, OldEmblemGuid = oldGuid });
        log.LogInformation("{Name} deploys Soul Echo {Guid} in {Slot}: {Code}", session.Role!.Name, req.EmblemGuid, req.EmblemPos, code);
    }

    /// <summary>82423: remove the selected slot and clear the item's matching setup attributes.</summary>
    public async Task SetDownAsync(GameSession session, SetDownEmblemReq req)
    {
        using var _ = await CharacterService.Lock(session);
        var plan = Active(session);
        int code = Gate(session);
        if (code == 0) code = req.EmblemPos is < FirstSlot or > LastSlot ? ErrorCodes.EmblemSlot
            : !plan.Slots.ContainsKey(req.EmblemPos) ? ErrorCodes.EmblemSlotEmpty : 0;
        long guid = code == 0 ? plan.Slots[req.EmblemPos] : 0;
        if (code == 0)
        {
            long previous = session.EmblemPower;
            var beforeItems = session.Items.Select(i => i.Clone()).ToArray();
            var afterItems = beforeItems.Select(i => i.Clone()).ToList();
            var next = CopyState(session.Emblems);
            next.Plans[next.CurrentPlan].Slots.Remove(req.EmblemPos);
            if (afterItems.FirstOrDefault(i => i.Guid == guid) is { } item)
            {
                item.Attrs ??= new(); item.Attrs[Source] = 0; item.Attrs[Position] = 0;
            }
            await CommitEmblemsAsync(session, "emblem-setdown", next, beforeItems, afterItems, [],
                new { req.EmblemPos, EmblemGuid = guid });
            await ChangedAsync(session, previous, inventory: true);
        }
        await session.SendGameAsync(SetDown, new SetDownEmblemResp { Result = code, EmblemPos = req.EmblemPos, EmblemGuid = guid });
    }

    /// <summary>82424: validate every selection, then serialize payment and update under StateLock (inferred rounding).</summary>
    public async Task UpgradeAsync(GameSession session, UpgradeEmblemReq req)
    {
        using var _ = await CharacterService.Lock(session);
        int code = Gate(session);
        var target = session.Items.FirstOrDefault(i => i.Guid == req.EmblemGuid && i.Grid == 0);
        if (code == 0) code = target == null || !IsEmblem(data, target.Entry) ? ErrorCodes.EmblemInvalid
            : !SeasonValid(target.Entry) ? ErrorCodes.EmblemOtherSeason : 0;
        int quality = target == null ? 0 : Quality(target.Entry);
        int level = (int)(target?.Attrs?.GetValueOrDefault(Level) ?? 0);
        int cap = data.Table("EmblemLvConfig").Values.Where(r => GameData.Int(r, "_iQuality") == quality).Select(r => (int)GameData.Int(r, "_iLv")).DefaultIfEmpty(0).Max();
        if (code == 0 && (cap == 0 || level >= cap)) code = ErrorCodes.EmblemCannotLevel;
        var ids = req.ConsumeEmblemGuids.ToList();
        if (code == 0 && (ids.Count != ids.Distinct().Count() || ids.Contains(req.EmblemGuid))) code = ErrorCodes.EmblemInvalid;
        var feed = new List<InvItem>();
        if (code == 0)
            foreach (var guid in ids)
            {
                var item = session.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0);
                if (item == null || !IsEmblem(data, item.Entry) || IsReserved(session, item) || !SeasonValid(item.Entry))
                { code = ErrorCodes.EmblemInvalid; break; }
                feed.Add(item);
            }
        long xp = 0;
        if (code == 0)
        {
            try
            {
                foreach (var (entry, count) in req.ConsumeItems)
                {
                    if (!MaterialXp.TryGetValue(entry, out long value) || count <= 0) { code = ErrorCodes.Parameter; break; }
                    xp = checked(xp + value * count);
                }
            }
            catch (OverflowException) { code = ErrorCodes.Parameter; }
            if (code == 0 && req.ConsumeItems.Any(p => character.CountOf(session, p.Key) < p.Value)) code = ErrorCodes.ItemsShort;
        }
        if (code == 0)
        {
            try
            {
                foreach (var item in feed)
                {
                    int q = Quality(item.Entry), lv = (int)(item.Attrs?.GetValueOrDefault(Level) ?? 0);
                    long baseXp = q switch { 2 or 3 => 100, 4 => 300, 5 or 6 => 1000, _ => 0 };
                    long rate = lv <= 5 ? 10000 : lv <= 10 ? 9000 : lv <= 15 ? 8000 : 7000;
                    // Client has no rounding step; integral feed XP floors each consumed item (inferred).
                    xp = checked(xp + baseXp + checked((item.Attrs?.GetValueOrDefault(TotalExp) ?? 0) * rate) / 10000);
                }
            }
            catch (OverflowException) { code = ErrorCodes.Parameter; }
            if (xp <= 0) code = ErrorCodes.Parameter;
        }
        Dictionary<int, long>? upgradedAttrs = null;
        if (code == 0)
        {
            try
            {
                // Compute the whole new item state before payment; no roll/overflow can fail after item removal.
                upgradedAttrs = target!.Attrs == null ? GenerateAttrs(target.Entry) ?? new() : new(target.Attrs);
                long remaining = checked(upgradedAttrs.GetValueOrDefault(Exp) + xp);
                int next = level;
                while (next < cap && LevelRow(quality, next + 1) is { } row && remaining >= GameData.Int(row, "_iExp"))
                {
                    remaining -= GameData.Int(row, "_iExp");
                    next++;
                    AddPrimaryLevel(upgradedAttrs, target.Entry);
                }
                upgradedAttrs[Level] = next;
                upgradedAttrs[Exp] = remaining; // retain cap remainder as SimulateAddExp preview (inferred server policy)
                upgradedAttrs[TotalExp] = checked(upgradedAttrs.GetValueOrDefault(TotalExp) + xp); // full feed XP, no refund (inferred)
                AddMarks(upgradedAttrs, target.Entry, next);
                AddSubattrs(upgradedAttrs, target.Entry, next);
            }
            catch (OverflowException) { code = ErrorCodes.Parameter; }
        }
        if (code == 0)
        {
            var beforeItems = session.Items.Select(i => i.Clone()).ToArray();
            var afterItems = beforeItems.Select(i => i.Clone()).ToList();
            foreach (var consumed in feed)
            {
                var copy = afterItems.Single(i => i.Guid == consumed.Guid);
                copy.Stack--;
                if (copy.Stack == 0) afterItems.Remove(copy);
            }
            foreach (var (entry, count) in req.ConsumeItems)
            {
                long left = count;
                foreach (var material in afterItems.Where(i => i.Grid == 0 && i.Entry == entry
                    && session.Items.FirstOrDefault(source => source.Guid == i.Guid) is { } source
                    && !IsReserved(session, source) && !TotemService.IsReserved(session, source))
                    .OrderBy(i => i.Stack).ToList())
                {
                    if (left == 0) break;
                    int take = (int)Math.Min(left, material.Stack);
                    material.Stack -= take;
                    left -= take;
                    if (material.Stack == 0) afterItems.Remove(material);
                }
                if (left != 0) { code = ErrorCodes.ItemsShort; break; }
            }
            if (code == 0)
            {
                afterItems.Single(i => i.Guid == req.EmblemGuid).Attrs = upgradedAttrs!;
                var costs = req.ConsumeItems.Select(p => (Entry: p.Key, Count: (long)p.Value))
                    .Concat(feed.Select(i => (Entry: i.Entry, Count: 1L)))
                    .GroupBy(p => p.Entry).Select(g => (Entry: g.Key, Count: g.Sum(p => p.Count))).ToArray();
                var next = CopyState(session.Emblems);
                long previous = session.EmblemPower;
                await CommitEmblemsAsync(session, "emblem-upgrade", next, beforeItems, afterItems, costs,
                    new
                    {
                        req.EmblemGuid, Materials = req.ConsumeItems.OrderBy(p => p.Key).ToArray(),
                        Feeds = feed.Select(i => new { i.Guid, i.Entry }).OrderBy(i => i.Guid).ToArray(),
                        AddedExperience = xp, Attributes = upgradedAttrs
                    });
                await ChangedAsync(session, previous, inventory: true);
                int gained = (int)upgradedAttrs!.GetValueOrDefault(Level) - level;
                foreach (var handler in Upgraded)
                    try { handler(session, gained); }
                    catch (Exception e) { log.LogError(e, "Soul Echo upgrade hook failed for {Role}", session.Role?.Id); }
            }
        }
        // No excess-XP refund, so gainItems is empty (inferred).
        await session.SendGameAsync(Upgrade, new UpgradeEmblemResp { Result = code, EmblemGuid = req.EmblemGuid });
    }

    private int Quality(int entry) => data.Row("ItemConfig", entry) is { } row ? (int)GameData.Int(row, "_iQuality") : 0;

    private JsonElement? LevelRow(int quality, int level)
    {
        var row = data.Table("EmblemLvConfig").Values
            .FirstOrDefault(r => GameData.Int(r, "_iQuality") == quality && GameData.Int(r, "_iLv") == level);
        return row.ValueKind == JsonValueKind.Object ? row : null;
    }

    private bool SeasonValid(int entry)
    {
        if (data.Row("ItemConfig", entry) is not { } row || !row.TryGetProperty("_kMultiverseArray", out var list) || list.ValueKind != JsonValueKind.Array) return true;
        var values = list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
        return values.Length == 0 || values.Contains(0) || values.Length == 2 && values[1] == -1 && seasons.PlayBook >= values[0] || values.Contains(seasons.PlayBook);
    }

    /// <summary>Initial rolls use configured inclusive ranges; weighted unique-attribute subrow selection is inferred.</summary>
    private Dictionary<int, long>? GenerateAttrs(int entry)
    {
        if (!IsEmblem(data, entry) || data.Row("EmblemConfig", entry) is not { } row) return null;
        var attrs = new Dictionary<int, long> { [Level] = 0, [Exp] = 0, [TotalExp] = 0 };
        if (row.TryGetProperty("_kPrimaryAttribute", out var primary) && primary.ValueKind == JsonValueKind.Array)
        {
            int slot = 0;
            foreach (var pair in primary.EnumerateArray())
            {
                if (slot >= 10 || pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 3) break;
                attrs[1031 + slot] = (long)(GameData.Number(pair[0]) ?? 0);
                attrs[1041 + slot] = Roll(pair);
                slot++;
            }
        }
        AddMarks(attrs, entry, 0);
        AddSubattrs(attrs, entry, 0);
        return attrs;
    }

    private static long Roll(JsonElement pair)
    {
        long min = (long)(GameData.Number(pair[1]) ?? 0), max = (long)(GameData.Number(pair[2]) ?? min);
        return max <= min ? min : Random.Shared.NextInt64(min, max + 1);
    }

    /// <summary>Roll every _kPrimaryAttributeLvUp range once per reached level (inferred frequency).</summary>
    private void AddPrimaryLevel(Dictionary<int, long> attrs, int entry)
    {
        if (data.Row("EmblemConfig", entry) is not { } row || !row.TryGetProperty("_kPrimaryAttributeLvUp", out var list) || list.ValueKind != JsonValueKind.Array) return;
        int slot = 0;
        foreach (var pair in list.EnumerateArray())
        {
            if (slot >= 10 || pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 3) break;
            if (attrs.GetValueOrDefault(1031 + slot) == (long)(GameData.Number(pair[0]) ?? 0)) attrs[1041 + slot] = attrs.GetValueOrDefault(1041 + slot) + Roll(pair);
            slot++;
        }
    }

    private void AddMarks(Dictionary<int, long> attrs, int entry, int level)
    {
        if (data.Row("EmblemConfig", entry) is not { } row || !row.TryGetProperty("_kInitialMark", out var list) || list.ValueKind != JsonValueKind.Array) return;
        int slot = 0;
        foreach (var pair in list.EnumerateArray())
        {
            if (slot >= 10 || pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) break;
            if ((long)(GameData.Number(pair[0]) ?? 0) > level) break;
            attrs[1021 + slot] = (long)(GameData.Number(pair[1]) ?? 0);
            slot++;
        }
    }

    /// <summary>Positive-weight rows selected without replacement by actual attribute ID (inferred selection).</summary>
    private void AddSubattrs(Dictionary<int, long> attrs, int entry, int level)
    {
        if (data.Row("EmblemConfig", entry) is not { } config || !config.TryGetProperty("_kSubAttributeNum", out var list) || list.ValueKind != JsonValueKind.Array) return;
        int wanted = 0;
        foreach (var pair in list.EnumerateArray())
            if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() >= 2 && (long)(GameData.Number(pair[0]) ?? 0) <= level)
                wanted += (int)(GameData.Number(pair[1]) ?? 0);
        long group = GameData.Int(config, "_iSubAttributeGroup");
        if (group == 0) return; // config has no pool/slot directive; do not fill from another group.
        int existing = Enumerable.Range(0, 20).TakeWhile(i => attrs.ContainsKey(1051 + i) && attrs.ContainsKey(1071 + i)).Count();
        var usedIds = Enumerable.Range(0, existing).Select(i => data.Row("EmblemSubAttriGroupConfig", attrs[1051 + i]))
            .Where(r => r != null).Select(r => GameData.Int(r!.Value, "_iSubAttriID")).ToHashSet();
        while (existing < Math.Min(20, wanted))
        {
            var pool = data.Table("EmblemSubAttriGroupConfig").Values
                .Where(r => GameData.Int(r, "_iGroup") == group && GameData.Int(r, "_iWeight") > 0
                    && !usedIds.Contains(GameData.Int(r, "_iSubAttriID")) && SeasonRowsValid(r))
                .ToList();
            long total = pool.Sum(r => GameData.Int(r, "_iWeight"));
            if (total <= 0) break;
            long roll = Random.Shared.NextInt64(total);
            JsonElement picked = pool[^1];
            foreach (var candidate in pool)
            {
                roll -= GameData.Int(candidate, "_iWeight");
                if (roll < 0) { picked = candidate; break; }
            }
            long rowId = GameData.Int(picked, "_iID"), id = GameData.Int(picked, "_iSubAttriID");
            attrs[1051 + existing] = rowId;
            long min = GameData.Int(picked, "_iMin"), max = GameData.Int(picked, "_iMax");
            attrs[1071 + existing] = max <= min ? min : Random.Shared.NextInt64(min, max + 1);
            usedIds.Add(id);
            existing++;
        }
    }

    private bool SeasonRowsValid(JsonElement row)
    {
        if (!row.TryGetProperty("_kMultiverseArray", out var list) || list.ValueKind != JsonValueKind.Array) return true;
        var values = list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
        return values.Length == 0 || values.Contains(0) || values.Length == 2 && values[1] == -1 && seasons.PlayBook >= values[0] || values.Contains(seasons.PlayBook);
    }
}
