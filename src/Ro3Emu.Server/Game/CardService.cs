using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Cards. A card is an ItemConfig type 4 / subtype 400 item; in the bag it lives in pack 3, inserted
/// it moves into a socket of an equipment grid (5009 storagePack, an InvItem with a negative Grid, see
/// CharacterService.CardGrid). Persisted selections are 5026 DBStorageHoleInfo, the card collection
/// ("library") 5057 DBLibraryInfo.cards. Messages: 31406-31408 sockets, 31411-31412 plans, 31461-31463 progression,
/// 82404 collection unlock; the Codex monsters (5057 monsters, 82407) are CardService.Monsters.cs.
/// Sockets open by EquipGridConfig._kSocketOpen1..4 conditions (base level); main/off hand need a worn item there.
/// </summary>
public sealed partial class CardService
{
    public const uint StorageHoleSync = 5026, LibrarySync = 5057;
    public const uint SlotInsert = 31406, SlotRemove = 31407, SlotTransfer = 31408, LibraryCardUnlock = 82404,
        LibrarySwitchTheme = 82405;
    public const uint SwitchPlan = 31411, ChangePlanName = 31412, LevelUp = 31461, StarUp = 31462, Revert = 31463, Treasured = 31469;

    private const int PlanCount = 5; // (inferred) PublicParamConfig.SchemeNum; card plans have no separate count field.
    private const int NameMax = 8; // (inferred) PublicParamConfig.SchemeNameNumMax shared with scheme UI.
    public List<Func<GameSession, Task>> CollectionChanged { get; } = new();
    /// <summary>Required quest continuation, installed by TaskService. Unlike optional observers it
    /// retains durable debt until the task pass has committed.</summary>
    internal Func<GameSession, OwnerContext, Task<bool>>? RequiredCollectionRefresh { get; set; }

    /// <summary>ItemConfig._iCardPart → equipment grids (card names: Lunatic weapon, Poring armour, Worm Tail accessory ...).</summary>
    private static int[] GridsFor(long part) => part switch
    {
        1 => [1],
        2 => [2],
        3 => [3],
        4 => [4],
        5 => [5],
        6 => [6, 7],
        7 => [8],
        _ => [], // 8 / 9 "Sketch Card" (MVP): target not established
    };

    private readonly GameData data;
    private readonly CharacterService character;
    private readonly FunctionService functions;
    private readonly RoleStore roles;
    private readonly SeasonService season;
    private readonly TimeProvider clock;
    private readonly ILogger<CardService> log;
    private readonly GameLoop loop;

    public CardService(GameData data, CharacterService character, FunctionService functions, ItemStore items, RoleStore roles,
        SeasonService season, TimeProvider clock, ILogger<CardService> log, GameLoop loop)
    {
        this.data = data;
        this.character = character;
        this.functions = functions;
        this.roles = roles;
        this.season = season;
        this.clock = clock;
        this.log = log;
        this.loop = loop;
        character.ItemGained.Add(OnItemGained);
        character.StageFirstCardSight = StageFirstCardSight;
        character.RegisterItemAction(GainLibraryPageAction, UseLibraryPageItemAsync);
    }

    // ---- login ----

    /// <summary>After 5009: persisted socket plans and their names, then the collection when it has entries.</summary>
    public async Task LoginAsync(GameSession session)
    {
        var saved = roles.LoadLibrary(session.Role!.Id);
        session.Library = saved != null ? DBLibraryInfo.Parser.ParseFrom(saved) : new DBLibraryInfo();
        // An older/direct item grant may have committed before its legacy ItemGained callback.
        // First sight, the reached star and Flash ownership are derived from the durable bag and sockets on reconnect,
        // before publishing 5057.
        bool repaired = false;
        foreach (var item in session.Items) repaired |= Observe(session.Library, item);
        if (repaired) SaveLibrary(session);
        session.CardLibraryReadyAttachment = session.Attachment;
        var plans = roles.LoadCardPlans(session.Role.Id);
        session.CardPlans = plans != null ? DBStorageHoleInfo.Parser.ParseFrom(plans) : NewPlans(session);
        if (!session.CardPlans.StoragePlans.ContainsKey(session.CardPlans.CurrentPlan)) session.CardPlans.CurrentPlan = 1;
        await SyncPlans(session);
        if (session.Library.Cards.Count > 0 || session.Library.Monsters.Count > 0 || session.Library.Pages.Count > 0)
            await CharacterService.SyncAsync(session, LibrarySync, session.Library.Clone());
    }

    private static DBStorageHoleInfo NewPlans(GameSession session)
    {
        var info = new DBStorageHoleInfo { CurrentPlan = 1, Inited = true };
        for (int index = 1; index <= PlanCount; index++)
        {
            var plan = new DBStoragePlanInfo { PlanName = $"Plan {index}" };
            // (inferred) Inactive plans need a nested zero-ItemId slot for the Lua reader; active 5009 is authoritative.
            if (index != info.CurrentPlan)
            {
                plan.CardInfo[1] = new DBInsertCardInfo();
                plan.CardInfo[1].CardSlotInfo[1] = new DBSlotItemInfo();
            }
            info.StoragePlans[index] = plan;
        }
        if (session.Items.Any(i => i.Grid < 0)) CaptureActive(session, info);
        return info;
    }

    private static DBSlotItemInfo Selection(InvItem card) => new()
    {
        ItemId = card.Entry, StarLevel = card.Attrs?.GetValueOrDefault(502) ?? 0,
        Treasured = card.Attrs?.GetValueOrDefault(530) ?? 0,
        ItemBindInfo = new ItemBindInfo { ItemBindType = (ItemBindType)card.BindType, ExpireTimeStamp = card.BindExpire },
    };

    private static void CaptureActive(GameSession session, DBStorageHoleInfo info)
    {
        var plan = info.StoragePlans[info.CurrentPlan];
        plan.CardInfo.Clear();
        foreach (var card in session.Items.Where(i => i.Grid < 0))
        {
            var (grid, socket) = CharacterService.CardSocket(card);
            if (!plan.CardInfo.TryGetValue(grid, out var slots)) plan.CardInfo[grid] = slots = new DBInsertCardInfo();
            slots.CardSlotInfo[socket] = Selection(card);
        }
        if (plan.CardInfo.Count == 0)
        {
            plan.CardInfo[1] = new DBInsertCardInfo();
            plan.CardInfo[1].CardSlotInfo[1] = new DBSlotItemInfo();
        }
    }

    private async Task SyncPlans(GameSession session)
    {
        var plans = session.CardPlans!;
        roles.SaveCardPlans(session.Role!.Id, plans.ToByteArray());
        await CharacterService.SyncAsync(session, StorageHoleSync, plans.Clone());
    }

    // ---- sockets ----

    /// <summary>31406: a bag card into (grid, socket); a card already there goes back to the bag.</summary>
    public async Task InsertAsync(GameSession session, EquipmentCardInsertReq req)
    {
        using var _ = await CharacterService.Lock(session);
        int grid = (int)req.EquipPos, socket = req.SlotIndex;
        var card = session.Items.FirstOrDefault(i => i.Guid == req.CardGuid && i.Grid == 0);
        int error = card == null ? ErrorCodes.ItemMissing
            : !character.IsCard(card.Entry) ? ErrorCodes.Parameter // (inferred) no card-specific type error.
            : AddressError(grid, socket) is var address and not 0 ? address
            : !GridsFor(Part(card.Entry)).Contains(grid) ? ErrorCodes.EquipSlot // (inferred) grid incompatibility.
            : SocketError(session, grid, socket) is var sealedSocket and not 0 ? sealedSocket
            : InsertRuleError(card.Entry, SocketedExcept(session.Items, (grid, socket)));
        log.LogInformation("{Name} inserts card {Card} into {Grid}/{Socket}: {Result}", session.Role!.Name, card?.Entry, grid, socket, error);
        if (error != 0)
        {
            await session.SendGameAsync(SlotInsert, new EquipmentCardInsertResp { Result = error, EquipPos = req.EquipPos, CardGuid = req.CardGuid, SlotIndex = socket });
            return;
        }
        var staged = session.Items.Select(i => i.Clone()).ToList();
        var plans = session.CardPlans!.Clone();
        if (staged.FirstOrDefault(i => i.Grid == CharacterService.CardGrid(grid, socket)) is { } old)
            StageToBag(staged, old);
        var inserted = StageTakeOne(staged, staged.Single(i => i.Guid == card!.Guid));
        inserted.Grid = CharacterService.CardGrid(grid, socket);
        var original = session.Items;
        session.Items = staged;
        try { CaptureActive(session, plans); }
        finally { session.Items = original; }
        await CommitCardStateAsync(session, staged, plans, "cd13a/card-insert",
            new { Card = card!.Entry, grid, socket });
        await character.ItemsChangedAsync(session, stats: true);
        await CharacterService.SyncAsync(session, StorageHoleSync, plans.Clone());
        var committed = session.Items.Single(i => i.Guid == inserted.Guid);
        await session.SendGameAsync(SlotInsert, new EquipmentCardInsertResp
        {
            Result = 0, EquipPos = req.EquipPos, CardGuid = committed.Guid, SlotIndex = socket, CardInfo = character.ItemInfoOf(committed),
        });
        await NotifySocketsChangedAsync(session);
    }

    /// <summary>31407: the card of (grid, socket) back to the bag.</summary>
    public async Task RemoveAsync(GameSession session, EquipmentCardRemoveReq req)
    {
        using var _ = await CharacterService.Lock(session);
        int grid = (int)req.EquipPos, socket = req.SlotIndex;
        var card = ValidSocket(grid, socket) ? Socketed(session, grid, socket) : null;
        if (card == null)
        {
            int error = AddressError(grid, socket);
            await session.SendGameAsync(SlotRemove, new EquipmentCardRemoveResp { Result = error == 0 ? ErrorCodes.SlotEmpty : error, EquipPos = req.EquipPos, SlotIndex = socket });
            return;
        }
        long guid = card.Guid;
        var staged = session.Items.Select(i => i.Clone()).ToList();
        var plans = session.CardPlans!.Clone();
        var back = StageToBag(staged, staged.Single(i => i.Guid == card.Guid));
        var original = session.Items;
        session.Items = staged;
        try { CaptureActive(session, plans); }
        finally { session.Items = original; }
        await CommitCardStateAsync(session, staged, plans, "cd13a/card-remove",
            new { Card = card.Entry, grid, socket });
        await character.ItemsChangedAsync(session, stats: true);
        await CharacterService.SyncAsync(session, StorageHoleSync, plans.Clone());
        await session.SendGameAsync(SlotRemove, new EquipmentCardRemoveResp { Result = 0, EquipPos = req.EquipPos, SlotIndex = socket, CardGuid = back.Guid });
        log.LogInformation("{Name} removes card {Card} from {Grid}/{Socket}", session.Role!.Name, guid, grid, socket);
        await NotifySocketsChangedAsync(session);
    }

    /// <summary>31408: move a socketed card to another socket (swapping with the card there).</summary>
    public async Task TransferAsync(GameSession session, EquipmentCardTransferReq req)
    {
        using var _ = await CharacterService.Lock(session);
        int srcGrid = (int)req.SrcEquipPos, dstGrid = (int)req.DestEquipPos;
        var src = ValidSocket(srcGrid, req.SrcEquipSlot) ? Socketed(session, srcGrid, req.SrcEquipSlot) : null;
        var dst = ValidSocket(dstGrid, req.DestEquipSlot) ? Socketed(session, dstGrid, req.DestEquipSlot) : null;
        int error = src == null ? AddressError(srcGrid, req.SrcEquipSlot) is var e and not 0 ? e : ErrorCodes.SlotEmpty
            : !GridsFor(Part(src.Entry)).Contains(dstGrid) ? ErrorCodes.EquipSlot // (inferred)
            : SocketError(session, dstGrid, req.DestEquipSlot) is var d and not 0 ? d
            : dst != null && !GridsFor(Part(dst.Entry)).Contains(srcGrid) ? ErrorCodes.EquipSlot // (inferred)
                                                                                                 // The client sends 31408 without CheckCardInsert: a move or swap keeps the socketed set. (inferred) The
                                                                                                 // server still holds each moved card to the insert rules against the cards it stays beside.
            : InsertRuleError(src.Entry, SocketedExcept(session.Items, (srcGrid, req.SrcEquipSlot), (dstGrid, req.DestEquipSlot))
                .Concat(dst == null ? [] : [dst.Entry])) is var srcRule and not 0 ? srcRule
            : dst != null ? InsertRuleError(dst.Entry, SocketedExcept(session.Items, (srcGrid, req.SrcEquipSlot), (dstGrid, req.DestEquipSlot))
                .Append(src.Entry))
            : 0;
        var resp = new EquipmentCardTransferResp
        {
            Result = error, SrcEquipPos = req.SrcEquipPos, SrcEquipSlot = req.SrcEquipSlot,
            DestEquipPos = req.DestEquipPos, DestEquipSlot = req.DestEquipSlot,
        };
        if (error == 0)
        {
            var staged = session.Items.Select(i => i.Clone()).ToList();
            var stagedSrc = staged.Single(i => i.Guid == src!.Guid);
            var stagedDst = dst == null ? null : staged.Single(i => i.Guid == dst.Guid);
            stagedSrc.Grid = CharacterService.CardGrid(dstGrid, req.DestEquipSlot);
            if (stagedDst != null)
            {
                stagedDst.Grid = CharacterService.CardGrid(srcGrid, req.SrcEquipSlot);
            }
            var plans = session.CardPlans!.Clone();
            var original = session.Items;
            session.Items = staged;
            try { CaptureActive(session, plans); }
            finally { session.Items = original; }
            await CommitCardStateAsync(session, staged, plans, "cd13a/card-transfer",
                new { Source = src!.Entry, Destination = dst?.Entry, srcGrid, req.SrcEquipSlot, dstGrid, req.DestEquipSlot });
            if (stagedDst != null) resp.SrcCardInfo = character.ItemInfoOf(session.Items.Single(i => i.Guid == stagedDst.Guid));
            resp.DestCardInfo = character.ItemInfoOf(session.Items.Single(i => i.Guid == stagedSrc.Guid));
            await character.ItemsChangedAsync(session, stats: true);
            await CharacterService.SyncAsync(session, StorageHoleSync, plans.Clone());
        }
        await session.SendGameAsync(SlotTransfer, resp);
        if (error == 0) await NotifySocketsChangedAsync(session);
    }

    /// <summary>The worn item of a grid left: its cards stay in the grid's sockets (they belong to the grid, spec §2.3).</summary>
    private InvItem? Socketed(GameSession session, int grid, int socket) =>
        session.Items.FirstOrDefault(i => i.Grid == CharacterService.CardGrid(grid, socket));

    private bool ValidSocket(int grid, int socket) => socket is >= 1 and <= 4 && data.Table("EquipGridConfig").Values
        .Any(r => GameData.Int(r, "_iEquipGridType") == grid && GameData.Int(r, "_iGridCategory") is 1 or 3);

    private int AddressError(int grid, int socket) => socket is < 1 or > 4 ? ErrorCodes.SlotIndex
        : ValidSocket(grid, socket) ? 0 : ErrorCodes.EquipSlot; // (inferred) invalid grid.

    /// <summary>EquipGridConfig[grid]._kSocketOpenN condition (0 = never); the weapon grids also need a worn item.</summary>
    private int SocketError(GameSession session, int grid, int socket)
    {
        if (AddressError(grid, socket) is var address and not 0) return address;
        var row = data.Table("EquipGridConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iEquipGridType") == grid && GameData.Int(r, "_iGridCategory") is 1 or 3);
        if (row.ValueKind != JsonValueKind.Object) return ErrorCodes.EquipSlot; // (inferred) bad grid.
        if (!row.TryGetProperty($"_kSocketOpen{socket}", out var cond) || GameData.Number(cond) is not { } id || id <= 0) return ErrorCodes.SlotSealed;
        if (grid is 1 or 2 && session.Items.All(i => i.Grid != grid)) return ErrorCodes.EquipSlot; // (inferred) no worn weapon.
        return functions.ConditionMet(session, (long)id) ? 0 : ErrorCodes.SlotSealed;
    }

    private long Part(int entry) => data.Row("ItemConfig", entry) is { } row ? GameData.Int(row, "_iCardPart") : 0;

    private static InvItem CloneOne(InvItem stack)
    {
        var one = stack.Clone();
        one.Guid = 0;
        one.Grid = 0;
        one.Stack = 1;
        return one;
    }

    private static bool SameState(InvItem a, InvItem b) => a.BindType == b.BindType && a.BindExpire == b.BindExpire
        && SameMap(a.Attrs, b.Attrs) && SameMap(a.StringAttrs, b.StringAttrs);

    private static bool SameMap<T>(Dictionary<int, T>? a, Dictionary<int, T>? b) => (a?.Count ?? 0) == (b?.Count ?? 0)
        && (a == null || a.All(p => b != null && b.TryGetValue(p.Key, out var value) && EqualityComparer<T>.Default.Equals(p.Value, value)));

    // ---- saved socket plans ----

    /// <summary>Stage one 5026 selection with owned card instances; Genre commits these moves with the other plans.</summary>
    internal int StageGenrePlan(GameSession session, DBStorageHoleInfo plans, List<InvItem> stagedItems, int index, bool replaceCurrent = false)
    {
        if (!plans.StoragePlans.TryGetValue(index, out var target)) return ErrorCodes.Parameter;
        if (index == plans.CurrentPlan && !replaceCurrent) return 0;
        var original = session.Items;
        session.Items = stagedItems;
        try
        {
            var desired = target.CardInfo.SelectMany(g => g.Value.CardSlotInfo.Select(s => (Grid: g.Key, Socket: s.Key, Choice: s.Value)))
                .Where(x => x.Choice.ItemId > 0).OrderBy(x => x.Grid).ThenBy(x => x.Socket).ToList();
            foreach (var (grid, socket, choice) in desired)
            {
                if (SocketError(session, grid, socket) != 0 || !GridsFor(Part((int)choice.ItemId)).Contains(grid))
                    return ErrorCodes.EquipSlot;
            }
            if (!replaceCurrent || index != plans.CurrentPlan) CaptureActive(session, plans);
            long nextSort = stagedItems.Where(i => i.Pack == 3).Select(i => i.Sort).DefaultIfEmpty().Max();
            foreach (var card in stagedItems.Where(i => i.Grid < 0).ToArray())
            {
                card.Grid = 0;
                card.Sort = ++nextSort;
            }
            foreach (var (grid, socket, choice) in desired)
            {
                var match = stagedItems.Where(i => i.Grid == 0 && i.Stack > 0).OrderBy(i => i.Guid)
                    .FirstOrDefault(i => Matches(i, choice));
                if (match == null) continue; // Same saved-plan policy as 31411: missing copies leave empty sockets.
                if (InsertRuleError(match.Entry, SocketedExcept(stagedItems)) != 0) continue; // and insert-rule clashes.
                if (match.Stack > 1)
                {
                    match.Stack--;
                    var one = CloneOne(match);
                    stagedItems.Add(one);
                    match = one;
                }
                match.Grid = CharacterService.CardGrid(grid, socket);
            }
            plans.CurrentPlan = index;
            CaptureActive(session, plans);
            return 0;
        }
        finally { session.Items = original; }
    }

    internal async Task SyncGenrePlanAsync(GameSession session)
    {
        await CharacterService.SyncAsync(session, StorageHoleSync, session.CardPlans!.Clone());
        await NotifySocketsChangedAsync(session);
    }

    /// <summary>31411: move distinct matching instances into the target plan's sockets.</summary>
    public async Task SwitchPlanAsync(GameSession session, CardSwitchPlanReq req)
    {
        using var _ = await CharacterService.Lock(session);
        var plans = session.CardPlans!.Clone();
        int error = !plans.StoragePlans.ContainsKey(req.TargetIndex) ? ErrorCodes.Parameter : 0; // (inferred) bad index.
        if (error == 0 && req.TargetIndex != plans.CurrentPlan)
        {
            var target = plans.StoragePlans[req.TargetIndex];
            var desired = target.CardInfo.SelectMany(g => g.Value.CardSlotInfo.Select(s => (Grid: g.Key, Socket: s.Key, Choice: s.Value)))
                .Where(x => x.Choice.ItemId > 0).OrderBy(x => x.Grid).ThenBy(x => x.Socket).ToList();
            // Validate all selected sockets before moving any card. Missing copies simply leave that slot empty (inferred).
            if (desired.Any(x => SocketError(session, x.Grid, x.Socket) != 0 || !GridsFor(Part((int)x.Choice.ItemId)).Contains(x.Grid))) error = ErrorCodes.EquipSlot;
            if (error == 0)
            {
                var staged = session.Items.Select(i => i.Clone()).ToList();
                var original = session.Items;
                session.Items = staged;
                try
                {
                    CaptureActive(session, plans);
                    foreach (var old in staged.Where(i => i.Grid < 0).ToList()) StageToBag(staged, old);
                    foreach (var (grid, socket, choice) in desired)
                    {
                        var match = staged.Where(i => i.Grid == 0 && i.Stack > 0).OrderBy(i => i.Guid)
                            .FirstOrDefault(i => Matches(i, choice));
                        if (match == null) continue;
                        // (inferred) A saved selection that breaks the insert rules against the cards already placed
                        // leaves its socket empty, like a missing copy.
                        if (InsertRuleError(match.Entry, SocketedExcept(staged)) != 0) continue;
                        var one = StageTakeOne(staged, match);
                        one.Grid = CharacterService.CardGrid(grid, socket);
                    }
                    plans.CurrentPlan = req.TargetIndex;
                    CaptureActive(session, plans);
                }
                finally { session.Items = original; }
                await CommitCardStateAsync(session, staged, plans, "cd13a/card-switch-plan",
                    new { CurrentPlan = req.TargetIndex });
                await character.ItemsChangedAsync(session, stats: true);
                await CharacterService.SyncAsync(session, StorageHoleSync, plans.Clone());
            }
        }
        await session.SendGameAsync(SwitchPlan, new CardSwitchPlanResp { Result = error, CurrentPlan = session.CardPlans!.CurrentPlan });
        if (error == 0) await NotifySocketsChangedAsync(session);
    }

    private static bool Matches(InvItem item, DBSlotItemInfo choice) => item.Entry == choice.ItemId
        && (item.Attrs?.GetValueOrDefault(502) ?? 0) == choice.StarLevel
        && (item.Attrs?.GetValueOrDefault(530) ?? 0) == choice.Treasured
        && item.BindType == (int)(choice.ItemBindInfo?.ItemBindType ?? 0)
        && item.BindExpire == (choice.ItemBindInfo?.ExpireTimeStamp ?? 0);

    /// <summary>31412: rename an existing plan; the client updates the display from 5026.</summary>
    public async Task ChangeNameAsync(GameSession session, StorageholeChangeNameReq req)
    {
        using var _ = await CharacterService.Lock(session);
        int error = !session.CardPlans!.StoragePlans.TryGetValue(req.TargetIndex, out var plan) || string.IsNullOrWhiteSpace(req.Name)
            || req.Name.EnumerateRunes().Count() > NameMax ? ErrorCodes.Parameter : 0; // (inferred) name/index refusal.
        if (error == 0)
        {
            var plans = session.CardPlans!.Clone();
            plans.StoragePlans[req.TargetIndex].PlanName = req.Name;
            await CommitCardStateAsync(session, session.Items.Select(i => i.Clone()).ToList(), plans,
                "cd13a/card-rename", new { req.TargetIndex, req.Name });
            await CharacterService.SyncAsync(session, StorageHoleSync, plans.Clone());
        }
        await session.SendGameAsync(ChangePlanName, new StorageholeChangeNameResp { Result = error });
    }

    // ---- card progression ----

    private JsonElement? CardRow(int entry, long tier) => data.Table("CardConfig").Values
        .FirstOrDefault(r => GameData.Int(r, "_iCardID") == entry && GameData.Int(r, "_iTier") == tier) is var row
        && row.ValueKind == JsonValueKind.Object ? row : null;

    private JsonElement? UpgradeRow(InvItem card, long level)
    {
        if (data.Row("ItemConfig", card.Entry) is not { } item) return null;
        var row = data.Table("CardUpgradeConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iLevel") == level
            && GameData.Int(r, "_iParts") == GameData.Int(item, "_iCardPart") && GameData.Int(r, "_iQuality") == GameData.Int(item, "_iQuality"));
        return row.ValueKind == JsonValueKind.Object ? row : null;
    }

    private InvItem? ProgressTarget(GameSession session, int sourceType, long guid, int grid, int socket)
    {
        if (sourceType == 1) return session.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0 && character.IsCard(i.Entry));
        if (sourceType != 4) return null; // Legacy bonds / spare-equipment carriers have no live model here.
        var card = ValidSocket(grid, socket) ? Socketed(session, grid, socket) : null;
        return card != null && character.IsCard(card.Entry) && (card.Guid == guid || session.Items.Any(i => i.Guid == guid && i.Grid == grid)) ? card : null;
    }

    private static int MissingTarget(int sourceType) => sourceType is 2 or 3 ? ErrorCodes.FeatureUnavailable
        : sourceType is 1 or 4 ? ErrorCodes.ItemMissing : ErrorCodes.Parameter; // (inferred) legacy carrier refusal.

    private static List<(int Entry, long Count)> Cost(JsonElement row, string name) => GameData.Pairs(row, name)
        .Select(p => ((int)p.Item1, (long)p.Item2)).Where(p => p.Item1 > 0 && p.Item2 > 0)
        .GroupBy(p => p.Item1).Select(g => (Entry: g.Key, Count: g.Sum(p => p.Item2))).ToList();

    private readonly record struct PaidCost(int Entry, long Count, int BindType, long BindExpire, int CardType);

    /// <summary>Select exact bag instances before mutation, reserving one target copy. Normal star copies only (inferred).</summary>
    private (int Error, List<(int Entry, long Count)> Currency, Dictionary<long, int> Instances, List<PaidCost> Ledger)
        PickCosts(GameSession session, InvItem target, IReadOnlyList<(int Entry, long Count)> cost, bool normalCards)
    {
        var currency = new List<(int Entry, long Count)>();
        var instances = new Dictionary<long, int>();
        var ledger = new List<PaidCost>();
        foreach (var (entry, count) in cost)
        {
            if (data.Row("ItemConfig", entry) is not { } row) return (ErrorCodes.ItemNoConfig, currency, instances, ledger);
            if (GameData.Int(row, "_iType") == 1)
            {
                currency.Add((entry, count));
                ledger.Add(new PaidCost(entry, count, 0, 0, 0));
                continue;
            }
            long remaining = count;
            foreach (var item in session.Items.Where(i => i.Grid == 0 && i.Entry == entry && (!normalCards || Pristine(i)))
                .OrderBy(i => i.Guid == target.Guid).ThenBy(i => i.Guid))
            {
                int available = item.Stack - (item.Guid == target.Guid ? 1 : 0) - instances.GetValueOrDefault(item.Guid);
                int take = (int)Math.Min(remaining, available);
                if (take <= 0) continue;
                instances[item.Guid] = instances.GetValueOrDefault(item.Guid) + take;
                ledger.Add(new PaidCost(entry, take, item.BindType, item.BindExpire, (int)(item.Attrs?.GetValueOrDefault(530) ?? 0)));
                remaining -= take;
                if (remaining == 0) break;
            }
            if (remaining > 0) return (ErrorCodes.ItemsShort, currency, instances, ledger);
        }
        int shortCurrency = character.ShortageOf(session, currency);
        if (currency.Any(c => character.CountOf(session, c.Entry) < c.Count)) return (shortCurrency, currency, instances, ledger);
        return (0, currency, instances, ledger);
    }

    private static int RecordCost(InvItem target, IEnumerable<PaidCost> cost, int start, int end)
    {
        var ledger = target.StringAttrs ??= new();
        foreach (var (entry, count, bind, expire, type) in cost)
        {
            int slot = 0;
            for (int k = start; k <= end; k++)
            {
                if (!ledger.TryGetValue(k, out var old)) { if (slot == 0) slot = k; continue; }
                var parts = old.Split('|');
                if (parts.Length >= 5 && parts[0] == entry.ToString() && parts[2] == bind.ToString() && parts[3] == expire.ToString() && parts[4] == type.ToString())
                { slot = k; break; }
            }
            if (slot == 0) return ErrorCodes.Parameter; // (inferred) ledger capacity exhausted; no mutation.
            long prior = 0;
            if (ledger.TryGetValue(slot, out var text) && !long.TryParse(text.Split('|').ElementAtOrDefault(1), out prior)) return ErrorCodes.Parameter;
            if (count > long.MaxValue - prior) return ErrorCodes.Parameter;
            ledger[slot] = $"{entry}|{prior + count}|{bind}|{expire}|{type}";
        }
        return 0;
    }

    private static bool LedgerFits(InvItem card, IEnumerable<PaidCost> cost, int start, int end)
    {
        var copy = CloneOne(card);
        try { ReadLedger(copy); return RecordCost(copy, cost, start, end) == 0; }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>31461: current-level CardUpgradeConfig cost, next-level attributes, and remembered paid material.</summary>
    public async Task LevelUpAsync(GameSession session, CardLevelUpReq req)
    {
        using var _ = await CharacterService.Lock(session);
        var card = ProgressTarget(session, (int)req.SourceType, req.Guid, (int)req.GridType, req.SlotIndex);
        int level = (int)(card?.Attrs?.GetValueOrDefault(501) ?? 0), tier = (int)(card?.Attrs?.GetValueOrDefault(502) ?? 0);
        var tierRow = card == null ? null : CardRow(card.Entry, tier);
        var row = card == null ? null : UpgradeRow(card, level);
        var next = card == null ? null : UpgradeRow(card, level + 1);
        int cap = tierRow is { } tr ? (int)GameData.Int(tr, "_iLevel") : 0;
        int error = card == null ? MissingTarget((int)req.SourceType) : tierRow == null || row == null ? ErrorCodes.Parameter
            : cap > 0 && level >= cap ? ErrorCodes.CardRankFirst : next == null ? ErrorCodes.CardMaxLevel : 0;
        var cost = row is { } r ? Cost(r, "_iCost") : [];
        var payment = card == null ? default : PickCosts(session, card, cost, normalCards: false);
        if (error == 0 && cost.Count == 0) error = ErrorCodes.Parameter;
        if (error == 0 && payment.Error != 0) error = payment.Error;
        if (error == 0 && !LedgerFits(card!, payment.Ledger, 510, 513)) error = ErrorCodes.Parameter;
        List<(int Entry, long Count)> economyCosts = error == 0 ? CardPaymentCosts(session, payment.Currency, payment.Instances) : [];
        if (error == 0 && await character.ExchangeLockedAsync(session, payment.Currency, [], payment.Instances,
            popup: false, sync: false, reservedGuid: card!.Guid,
            persist: builder => builder.WithEconomy(character.FamilyEconomy(session, "cd13a/card-level",
                economyCosts, [],
                new { Card = card!.Entry, Guid = card!.Guid, NewLevel = level + 1 })),
            stageItems: staged =>
            {
                var target = staged.Single(i => i.Guid == card!.Guid);
                StageSingleTarget(staged, target);
                target.Attrs ??= new();
                target.Attrs[501] = level + 1;
                if (RecordCost(target, payment.Ledger, 510, 513) != 0)
                    throw new InvalidOperationException("Validated card level ledger changed during staging.");
            }, asyncCommit: true) == null) error = ErrorCodes.ItemsShort;
        if (error == 0 && card != null)
        {
            level++;
            await character.ItemsChangedAsync(session, stats: card.Grid < 0);
        }
        await session.SendGameAsync(LevelUp, new CardLevelUpResp { Result = error, SourceType = req.SourceType, Guid = req.Guid, SlotIndex = req.SlotIndex, GridType = req.GridType, NewLevel = level });
    }

    private static bool Pristine(InvItem item) => (item.Attrs?.GetValueOrDefault(501) ?? 0) == 0
        && (item.Attrs?.GetValueOrDefault(502) ?? 0) == 0 && (item.Attrs?.GetValueOrDefault(530) ?? 0) == 0
        && (item.StringAttrs?.Count ?? 0) == 0;

    /// <summary>31462: CardConfig tier cost in pristine normal card copies (inferred selection) and paid-card ledger.</summary>
    public async Task StarUpAsync(GameSession session, CardStarUpReq req)
    {
        using var _ = await CharacterService.Lock(session);
        var card = ProgressTarget(session, (int)req.SourceType, req.Guid, (int)req.GridType, req.SlotIndex);
        int tier = (int)(card?.Attrs?.GetValueOrDefault(502) ?? 0);
        var row = card == null ? null : CardRow(card.Entry, tier);
        int error = card == null ? MissingTarget((int)req.SourceType) : row == null ? ErrorCodes.Parameter : CardRow(card.Entry, tier + 1) == null ? ErrorCodes.CardMaxStar : 0;
        var cost = row is { } r ? Cost(r, "_kCost") : [];
        var payment = card == null ? default : PickCosts(session, card, cost, normalCards: true);
        if (error == 0 && cost.Count == 0) error = ErrorCodes.Parameter;
        if (error == 0 && payment.Error != 0) error = payment.Error;
        if (error == 0 && !LedgerFits(card!, payment.Ledger, 520, 529)) error = ErrorCodes.Parameter;
        List<(int Entry, long Count)> economyCosts = error == 0 ? CardPaymentCosts(session, payment.Currency, payment.Instances) : [];
        DBStorageHoleInfo? nextPlans = null;
        // The collection's reached star rises with the copy in the same commit (Observe); an unlocked entry's newly
        // lit tier rows pay their Collection Points with it (inferred; 0 for every tier above 0 in this client).
        DBLibraryInfo? nextLibrary = null;
        List<(int Entry, int Count)> tierGains = [];
        if (error == 0 && session.Library != null)
        {
            var starred = card!.Clone();
            starred.Attrs ??= new();
            starred.Attrs[502] = tier + 1;
            var library = session.Library.Clone();
            var before = library.Cards.GetValueOrDefault(card.Entry);
            int reached = before?.CardStar ?? 0;
            bool unlocked = before?.Unlock == true;
            if (Observe(library, starred)) nextLibrary = library;
            int points = unlocked ? TierPoints(card.Entry, reached, library.Cards[card.Entry].CardStar) : 0;
            if (points > 0 && Param("LibraryOverviewPointID") is var pointItem and > 0) tierGains.Add((pointItem, points));
        }
        if (error == 0 && await character.ExchangeLockedAsync(session, payment.Currency, tierGains, payment.Instances,
            popup: false, sync: false, reservedGuid: card!.Guid,
            persist: builder =>
            {
                if (nextPlans != null) builder.RoleBlob(session.Role!.Id, "card_plans", nextPlans.ToByteArray());
                if (nextLibrary != null) builder.RoleBlob(session.Role!.Id, "library", nextLibrary.ToByteArray());
                builder.WithEconomy(character.FamilyEconomy(session, "cd13a/card-star",
                    economyCosts, tierGains.ToArray(),
                    new { Card = card!.Entry, Guid = card!.Guid, NewStar = tier + 1 }));
            },
            stageItems: staged =>
            {
                var target = staged.Single(i => i.Guid == card!.Guid);
                StageSingleTarget(staged, target);
                target.Attrs ??= new();
                target.Attrs[502] = tier + 1;
                if (RecordCost(target, payment.Ledger, 520, 529) != 0)
                    throw new InvalidOperationException("Validated card star ledger changed during staging.");
                if (target.Grid < 0)
                {
                    nextPlans = session.CardPlans!.Clone();
                    CaptureActive(session, nextPlans);
                }
            },
            committed: () =>
            {
                if (nextPlans != null) session.CardPlans = nextPlans;
                if (nextLibrary != null) session.Library = nextLibrary;
            },
            asyncCommit: true) == null) error = ErrorCodes.ItemsShort;
        if (error == 0 && card != null)
        {
            tier++;
            await character.ItemsChangedAsync(session, stats: card.Grid < 0);
            if (nextPlans != null) await CharacterService.SyncAsync(session, StorageHoleSync, nextPlans.Clone());
            if (nextLibrary != null) await CharacterService.SyncAsync(session, LibrarySync, session.Library!.Clone());
            if (card.Grid < 0) await NotifySocketsChangedAsync(session);
        }
        await session.SendGameAsync(StarUp, new CardStarUpResp { Result = error, SourceType = req.SourceType, Guid = req.Guid, SlotIndex = req.SlotIndex, GridType = req.GridType, NewStarNum = tier });
    }

    private static List<PaidCost> ReadLedger(InvItem card)
    {
        var result = new List<PaidCost>();
        foreach (var (key, text) in card.StringAttrs ?? new())
        {
            if (key is < 510 or > 529 || key is > 513 and < 520) continue;
            var parts = text.Split('|');
            if (parts.Length < 5 || !int.TryParse(parts[0], out int entry) || !long.TryParse(parts[1], out long count)
                || !int.TryParse(parts[2], out int bind) || !long.TryParse(parts[3], out long expire)
                || !int.TryParse(parts[4], out int type) || entry <= 0 || count <= 0 || bind is < 0 or > 2 || expire < 0 || type is < 0 or > 2)
                throw new InvalidDataException("invalid card cost history");
            result.Add(new PaidCost(entry, count, bind, expire, type));
        }
        return result;
    }

    /// <summary>31463: pay quality return fee, restore base card, return the paid ledger (return UI).</summary>
    public async Task RevertAsync(GameSession session, CardRevertReq req)
    {
        using var _ = await CharacterService.Lock(session);
        var card = session.Items.FirstOrDefault(i => i.Guid == req.CardGuid && i.Grid == 0 && character.IsCard(i.Entry));
        int level = (int)(card?.Attrs?.GetValueOrDefault(501) ?? 0), tier = (int)(card?.Attrs?.GetValueOrDefault(502) ?? 0);
        int error = card == null ? ErrorCodes.ItemMissing : level == 0 && tier == 0 ? ErrorCodes.CardLevelReturn : 0;
        List<PaidCost> refund = [];
        if (error == 0)
        {
            try { refund = ReadLedger(card!); }
            catch (InvalidDataException) { error = ErrorCodes.Parameter; }
        }
        var itemRow = card == null ? null : data.Row("ItemConfig", card.Entry);
        var feeRow = itemRow is { } ir ? data.Table("CardReturnCostConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iQuality") == GameData.Int(ir, "_iQuality")) : default;
        var fee = feeRow.ValueKind == JsonValueKind.Object ? Cost(feeRow, "_iCost") : [];
        if (error == 0 && fee.Count == 0) error = ErrorCodes.Parameter;
        List<((int Entry, int BindType, long BindExpire, int CardType) Key, long Count)> grouped = [];
        if (error == 0)
        {
            try
            {
                grouped = refund.GroupBy(x => (x.Entry, x.BindType, x.BindExpire, x.CardType))
                    .Select(g => (g.Key, Count: g.Sum(x => x.Count))).ToList();
            }
            catch (OverflowException) { error = ErrorCodes.Parameter; }
        }
        if (error == 0 && grouped.Any(g => g.Count > int.MaxValue)) error = ErrorCodes.Parameter;
        var goods = error == 0 ? grouped.Select(g => new CharacterService.AttributedGood(g.Key.Entry, (int)g.Count,
            g.Key.BindType, g.Key.BindExpire, g.Key.CardType)).ToList() : [];
        if (error == 0 && goods.Any(g => data.Row("ItemConfig", g.Entry) == null || g.CardType != 0 && !character.IsCard(g.Entry)))
            error = ErrorCodes.Parameter;
        List<InvItem>? granted = null;
        if (error == 0 && (granted = await character.ExchangeLockedAsync(session, fee, [], popup: false, sync: false,
            reservedGuid: card!.Guid, attributedGoods: goods,
            persist: builder => builder.WithEconomy(character.FamilyEconomy(session, "cd13a/card-revert",
                fee, goods.Select(g => (g.Entry, g.Count)).ToArray(),
                new { Card = card!.Entry, Guid = card!.Guid, Refunded = goods.Select(g => new { g.Entry, g.Count, g.BindType, g.BindExpire, g.CardType }).ToArray() })),
            stageAfterGoods: staged =>
            {
                var target = staged.Single(i => i.Guid == card!.Guid);
                StageSingleTarget(staged, target);
                target.Attrs ??= new();
                target.Attrs[501] = 0;
                target.Attrs[502] = 0;
                if (target.StringAttrs != null)
                    foreach (var key in target.StringAttrs.Keys.Where(k => k is >= 510 and <= 513 or >= 520 and <= 529).ToList())
                        target.StringAttrs.Remove(key);
            }, asyncCommit: true)) == null)
            error = character.ShortageOf(session, fee);
        if (error == 0 && card != null)
        {
            await character.ItemsChangedAsync(session, stats: false);
        }
        var resp = new CardRevertResp { Result = error, CardGuid = req.CardGuid, EntryId = card?.Entry ?? 0, Type = (int)(card?.Attrs?.GetValueOrDefault(530) ?? 0) };
        if (card != null)
        {
            resp.IsBind = card.BindType != 0;
            resp.BindInfo = new ItemBindInfo { ItemBindType = (ItemBindType)card.BindType, ExpireTimeStamp = card.BindExpire };
        }
        if (error == 0)
        {
            foreach (var good in goods)
            {
                if (data.Row("ItemConfig", good.Entry) is { } row && GameData.Int(row, "_iType") == 1) continue;
                var item = granted!.First(i => i.Entry == good.Entry && i.BindType == good.BindType && i.BindExpire == good.BindExpire
                    && (i.Attrs?.GetValueOrDefault(530) ?? 0) == good.CardType);
                var delta = character.ItemInfoOf(item);
                delta.Stack = good.Count; // The popup count is the refund, even when the bag item was an existing stack.
                resp.RevertCards.Add(delta);
            }
            // The return preview always lists the reset original once, even for level-only returns
            // (UI_Sys_CardReturnContentWidgetCtrl.lua:212-255). This is display data, not another inventory grant.
            resp.RevertCards.Add(character.ItemInfoOf(card!));
        }
        await session.SendGameAsync(Revert, resp);
    }

    // ---- collection ----

    /// <summary>First sight of a card: a locked collection entry (activated by 82404); its star / Flash state from the
    /// committed copies (Observe).</summary>
    private void OnItemGained(GameSession session, int entry, int count)
    {
        if (session.Library == null || !character.IsCard(entry)) return;
        bool changed = false;
        if (!session.Library.Cards.ContainsKey(entry))
        {
            session.Library.Cards[entry] = new LibraryCardInfo { CardId = entry };
            changed = true;
        }
        foreach (var item in session.Items.Where(i => i.Entry == entry)) changed |= Observe(session.Library, item);
        if (!changed) return;
        SaveLibrary(session);
        var attachment = session.Attachment;
        loop.TrackLegacyDetached("card library sync", () =>
            attachment?.Cancelled == true || !ReferenceEquals(session.Attachment, attachment)
                ? Task.CompletedTask : CharacterService.SyncAsync(session, LibrarySync, session.Library.Clone()));
    }

    private CharacterService.FirstCardSightPlan? StageFirstCardSight(GameSession session, IReadOnlyList<int> entries, int monster)
    {
        if (session.Library == null) return null;
        var next = session.Library.Clone();
        bool changed = CountMonsterKill(next, monster);
        foreach (int entry in entries.Distinct())
            if (character.IsCard(entry) && !next.Cards.ContainsKey(entry))
            {
                next.Cards[entry] = new LibraryCardInfo { CardId = entry };
                changed = true;
            }
        // Inside an exchange the session shows its staged items: a granted Flash or starred copy (a trade or a
        // synthesis output) raises its entry in the same commit.
        var gained = entries.ToHashSet();
        foreach (var item in session.Items.Where(i => gained.Contains(i.Entry))) changed |= Observe(next, item);
        if (!changed) return null;
        var attachment = session.Attachment;
        var newCards = next.Cards.Keys.Except(session.Library.Cards.Keys).Order().ToArray();
        long role = session.Role!.Id;
        long epoch = session.Owner?.LifecycleEpoch ?? 0;
        return new(builder =>
            {
                if (newCards.Length == 0) { builder.RoleBlob(role, "library", next.ToByteArray()); return; } // a Codex kill only
                byte[]? old = roles.ReadTaskFollowup(builder, role);
                var pending = old is { Length: > 0 }
                    ? JsonSerializer.Deserialize<HashSet<string>>(old) ?? [] : new HashSet<string>();
                foreach (long card in newCards) pending.Add($"card-sight/{role}/{epoch}/{card}");
                builder.RoleBlob(role, "library", next.ToByteArray());
                builder.RoleBlob(role, "task_followup", JsonSerializer.SerializeToUtf8Bytes(pending));
            },
            () =>
            {
                session.Library = next;
                if (attachment?.Cancelled != true && ReferenceEquals(session.Attachment, attachment))
                    loop.TrackLegacyDetached("card library sync", () =>
                        attachment?.Cancelled == true || !ReferenceEquals(session.Attachment, attachment)
                            ? Task.CompletedTask : CharacterService.SyncAsync(session, LibrarySync, session.Library!.Clone()));
            });
    }

    /// <summary>
    /// 82404: activate a collected card's entry (the request carries the card item id). A normal unlock pays its
    /// Collection Points (CardUnlockPoints) in the same commit as the entry and its quest follow-up, an owned
    /// cd13e/library-award like the Codex monster unlock's; a failed commit changes nothing and answers Unknown.
    /// </summary>
    public async Task UnlockAsync(GameSession session, LibraryCardUnlockReq req)
    {
        using var owner = await session.Owners!.EnterAsync("card/unlock", ReservationKey.Role(session.Role!.Id));
        var context = owner.Context;
        context.AssertRole(session.Role.Id);
        context.BindPresentation(session);
        var attachment = session.Attachment;
        bool ok, failed = false;
        int error;
        using (await CharacterService.Lock(session, context))
        {
            ok = session.Library != null && session.Library.Cards.TryGetValue(req.CardId, out var entry) && !(req.IsTreasure ? entry.TreasureUnlock : entry.Unlock);
            if (ok)
            {
                var next = session.Library!.Clone();
                var e = next.Cards[req.CardId];
                if (req.IsTreasure) e.TreasureUnlock = true;
                else e.Unlock = true;
                long role = session.Role.Id, epoch = session.Owner!.LifecycleEpoch;
                byte[] bytes = next.ToByteArray();
                int points = req.IsTreasure ? 0 : CardUnlockPoints(req.CardId, e.CardStar), item = Param("LibraryOverviewPointID");
                if (points > 0 && item > 0)
                {
                    try
                    {
                        ok = await character.ApplyAwardAsync(session, new AwardPlan(0, 0, [new AwardGrant(item, points)]),
                            persist: tx => roles.StageCardUnlock(tx, role, epoch, req.CardId, false, bytes),
                            stateLockHeld: true, committed: () => session.Library = next,
                            ownedAward: new CharacterService.OwnedAwardSource("cd13e/library-award",
                                $"library-card/{role}/{req.CardId}",
                                new { Version = 1, Card = req.CardId, Item = item, Points = points }));
                    }
                    catch (Exception fault) when (fault is not OperationCanceledException)
                    {
                        log.LogError(fault, "Card {Card} unlock failed for {Role}", req.CardId, role);
                        ok = false;
                    }
                    failed = !ok;
                    if (ok) log.LogInformation("{Name} unlocks card {Card} (treasure {Treasure}): {Points} x {Item}",
                        session.Role.Name, req.CardId, req.IsTreasure, points, item);
                }
                else
                    await roles.CommitCardUnlockAsync(role, epoch, req.CardId, req.IsTreasure, bytes, session.Items,
                        character.FamilyEconomy(session, "cd13a/card-unlock", [], [],
                            new { req.CardId, req.IsTreasure }),
                        () => session.Library = next);
            }
            error = session.Library == null || failed ? ErrorCodes.Unknown : !session.Library.Cards.ContainsKey(req.CardId) ? ErrorCodes.ItemMissing : ErrorCodes.AlreadyUnlocked;
        }
        if (ok && attachment?.Cancelled != true && ReferenceEquals(session.Attachment, attachment))
            await CharacterService.SyncAsync(session, LibrarySync, session.Library!.Clone());
        if (ok && RequiredCollectionRefresh is { } refresh)
            try { await refresh(session, context); }
            catch (Exception e) { log.LogError(e, "Mandatory card quest refresh remains pending for role {Role} card {Card}", session.Role!.Id, req.CardId); }
        // Quest collection subscribers may deliver rewards; run them after the character lock and before the reply.
        // The library was already committed, so a failed subscriber cannot turn a successful unlock into a lost reply.
        if (ok && attachment?.Cancelled != true && ReferenceEquals(session.Attachment, attachment))
            foreach (var handler in CollectionChanged)
                try { await handler(session); }
                catch (Exception e) { log.LogError(e, "Card collection callback failed for role {Role} card {Card}", session.Role!.Id, req.CardId); }
        if (attachment?.Cancelled != true && ReferenceEquals(session.Attachment, attachment))
            await session.SendGameAsync(LibraryCardUnlock, new LibraryCardUnlockResp { Result = ok ? 0 : error, CardId = req.CardId });
    }

    /// <summary>
    /// PublicParamConfig.LibraryCardUnlockReward[ItemConfig._iQuality].PointValue["0"]: the Collection Points a card's
    /// collection entry shows (UI_Sys_LibraryList_CardWidgetCtrl, m_kTTXT_Point; "0" is the card's base star level).
    /// The points are item LibraryOverviewPointID, as for monsters. (inferred) Only the normal unlock pays: the entry shows
    /// one value, and no client lookup reaches the 104..106 rows (no ItemConfig._iQuality above 6), so a treasure unlock
    /// pays nothing and keeps its plain commit. The tier rows the entry already reached (cardStar) are lit by the same
    /// unlock (GetCardRewardHas) and pay their own PointValue with it (inferred; tiers 1..3 hold 0 in this client).
    /// </summary>
    private int CardUnlockPoints(int card, int reachedStar) => TierPoints(card, -1, reachedStar);

    private void SaveLibrary(GameSession session) => roles.SaveLibrary(session.Role!.Id, session.Library!.ToByteArray());

    /// <summary>
    /// 82405: the Codex overview theme. LC_DAL_Library._processLibraryChangeUI (lua:372-402) sets the dataset's
    /// iSelectUI to the reply's themeId, so the reply echoes the theme (the generic empty reply set it to 0, which is
    /// no LibraryThemeConfig row). The theme must exist and its _iUnlockCondition (theme 2: 15000000, item 118 >= 1000)
    /// hold; it is kept in DBLibraryInfo.overviewTheme, which the login 5057 maps to iSelectUI (lua:597, 790).
    /// </summary>
    public async Task SwitchThemeAsync(GameSession session, LibrarySwitchThemeReq req)
    {
        int code;
        using (await CharacterService.Lock(session))
        {
            code = session.Library == null || session.Role == null ? ErrorCodes.FeatureUnavailable
                : data.Row("LibraryThemeConfig", req.ThemeId) is not { } theme ? ErrorCodes.Parameter
                : !functions.ConditionMet(session, GameData.Int(theme, "_iUnlockCondition")) ? ErrorCodes.UnlockRequirement
                : 0;
            if (code == 0 && session.Library!.OverviewTheme != req.ThemeId)
            {
                var next = session.Library.Clone();
                next.OverviewTheme = req.ThemeId;
                roles.SaveLibrary(session.Role!.Id, next.ToByteArray());
                session.Library = next;
                await CharacterService.SyncAsync(session, LibrarySync, session.Library.Clone());
            }
        }
        await session.SendGameAsync(LibrarySwitchTheme, new LibrarySwitchThemeResp { Result = code, ThemeId = code == 0 ? req.ThemeId : session.Library?.OverviewTheme ?? 0 });
    }
}
