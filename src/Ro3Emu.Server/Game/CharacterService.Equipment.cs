using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    // ---- grid levels (EquipmentService changes them) ----

    /// <summary>StrengthenTable row of a grid (= _iEquipType 1..7) at a level; level 0 has a row without attributes.</summary>
    internal JsonElement? StrengthenRow(int grid, int level) => LevelRow("StrengthenTable", "_iStrengthenInterval", grid, level);

    /// <summary>RefiningTable row of a grid at a target level.</summary>
    internal JsonElement? RefineRow(int grid, int level) => LevelRow("RefiningTable", "_iRefiningInterval", grid, level);

    private JsonElement? LevelRow(string table, string levelField, int grid, int level)
    {
        if (!_levelRows.TryGetValue(table, out var rows))
            _levelRows[table] = rows = data.Table(table).Values.GroupBy(r => (GameData.Int(r, "_iEquipType"), GameData.Int(r, levelField))).ToDictionary(g => g.Key, g => g.First());
        return rows.TryGetValue((grid, level), out var row) ? row : null;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<(long, long), JsonElement>> _levelRows = new();

    /// <summary>The highest resonance row whose _kRequest [grids, level] holds over grids 1..7 (strengthen or refine levels).</summary>
    /// <summary>CardUpgradeConfig row for (level, part, quality): the first match in table order, indexed once (the
    /// stat sheet used to scan all 720 rows for every equipped card on every call).</summary>
    private JsonElement? CardUpgradeRow(long level, long part, long quality)
    {
        _cardUpgradeRows ??= data.Table("CardUpgradeConfig").Values
            .GroupBy(r => (GameData.Int(r, "_iLevel"), GameData.Int(r, "_iParts"), GameData.Int(r, "_iQuality")))
            .ToDictionary(g => g.Key, g => g.First());
        return _cardUpgradeRows.TryGetValue((level, part, quality), out var row) ? row : null;
    }

    private Dictionary<(long, long, long), JsonElement>? _cardUpgradeRows;

    /// <summary>Resonance rows with a usable _kRequest [grids, level], ordered by _iEffectLev once per table.</summary>
    private (JsonElement Row, long Grids, long Level)[] ResonanceRows(string table)
    {
        if (!_resonanceRows.TryGetValue(table, out var rows))
            _resonanceRows[table] = rows = data.Table(table).Values.OrderBy(r => GameData.Int(r, "_iEffectLev"))
                .Where(r => r.TryGetProperty("_kRequest", out var req) && req.ValueKind == JsonValueKind.Array && req.GetArrayLength() >= 2)
                .Select(r => (r, (long)(GameData.Number(r.GetProperty("_kRequest")[0]) ?? 0), (long)(GameData.Number(r.GetProperty("_kRequest")[1]) ?? 0)))
                .ToArray();
        return rows;
    }

    private readonly Dictionary<string, (JsonElement Row, long Grids, long Level)[]> _resonanceRows = new();

    private JsonElement? ResonanceRow(GameSession session, string table)
    {
        var levels = table.StartsWith("Strengthen") ? session.EquipLevels.Strength : session.EquipLevels.Refine;
        JsonElement? best = null;
        foreach (var (row, grids, level) in ResonanceRows(table))
        {
            if (Enumerable.Range(1, EquipInfoSlots).Count(g => levels.GetValueOrDefault(g) >= level) >= grids) best = row;
        }
        return best;
    }

    internal int Resonance(GameSession session, string table) => ResonanceRow(session, table) is { } row ? (int)GameData.Int(row, "_iEffectLev") : 0;

    internal void SaveEquipLevels(GameSession session) => roles.SaveEquipLevels(session.Role!.Id, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(session.EquipLevels));

    /// <summary>After a grid level change: 5007 and the changed properties.</summary>
    internal async Task EquipLevelsChangedAsync(GameSession session, bool persist = true)
    {
        if (persist) SaveEquipLevels(session);
        await SyncAsync(session, EquipSync, BuildEquip(session));
        await SendStatsAsync(session);
        foreach (var handler in StateChanged) handler(session);
    }

    // ---- per-instance gear stats (ItemAttrTypeEnum 30-49 attribute ids, 60-79 values; LC_ItemProtoParse.ParseEquipInfo) ----

    private const int AttrKeyStart = 30, AttrValueStart = 60, AttrSlots = 20;

    /// <summary>
    /// Common creation path for drops, crafts, purchases, GM and daily rewards. EquipmentAffixes uses the client's
    /// actual EquipAttriGroupConfig / EquipSpecialGroupConfig pools; unappraised instances defer affixes until 31401.
    /// Base rolls remain per instance, including before appraisal (inferred generation timing).
    /// </summary>
    private Dictionary<int, long>? RollAttrs(int entry) => EquipmentAffixes.Create(entry, data);

    /// <summary>A worn item's base attributes: its rolled values, or EquipConfig's minimums for an instance without them.</summary>
    private static IEnumerable<(long Id, double Value)> BaseAttributes(InvItem item, JsonElement equip)
    {
        if (item.Attrs is not { } attrs || !attrs.ContainsKey(AttrKeyStart)) return GameData.Pairs(equip, "_kBasicAttribute");
        var list = new List<(long, double)>();
        for (int i = 0; i < AttrSlots && attrs.TryGetValue(AttrKeyStart + i, out long id); i++) list.Add((id, attrs.GetValueOrDefault(AttrValueStart + i)));
        return list;
    }

    // ---- cards (CardService moves them) ----

    private const int AttrCardLevel = 501, AttrCardStar = 502, AttrCardType = 530;

    /// <summary>ItemConfig type 4 / subtype 400.</summary>
    public bool IsCard(int entry) => data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == 4 && GameData.Int(row, "_iSubType") == 400;

    /// <summary>An inserted card has Grid = -(equipment grid * 10 + socket).</summary>
    public static (int Grid, int Socket) CardSocket(InvItem card) => (-card.Grid / 10, -card.Grid % 10);

    public static int CardGrid(int grid, int socket) => -(grid * 10 + socket);

    /// <summary>CardConfig row of a card item at a tier (star): _kBasicAttribute feeds the stat pipeline.</summary>
    private JsonElement? CardRow(int entry, int tier)
    {
        _cardRows ??= data.Table("CardConfig").Values.GroupBy(r => (GameData.Int(r, "_iCardID"), GameData.Int(r, "_iTier"))).ToDictionary(g => g.Key, g => g.First());
        return _cardRows.TryGetValue((entry, tier), out var row) ? row : null;
    }

    private Dictionary<(long, long), JsonElement>? _cardRows;

    private bool IsEquipment(int entry) => data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeEquip;

    /// <summary>LC_ItemProtoParse.ParseEquipInfo: only itemInfos[3] == 1 is appraised.</summary>
    internal static bool IsAppraised(InvItem item) => item.Attrs?.GetValueOrDefault(AttrAppraise) == 1;

    // ---- equipment ----

    private static IEnumerable<InvItem> Equipped(GameSession session) => session.Items.Where(i => i.Grid > 0);

    private JsonElement? EquipRow(int entry) =>
        data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iEquipId") is var equipId and > 0 ? data.Row("EquipConfig", equipId) : null;

    /// <summary>EquipPosType (ItemConfig._iEquipPart) -> the grids it may go to (spec §4.2).</summary>
    private static int[] GridsFor(int part) => part switch
    {
        1 or 2 => [1],
        3 => [2],
        4 => [3],
        5 => [4],
        6 => [5],
        7 => [6, 7],
        8 or 9 or 10 => [part],
        _ => [],
    };

    /// <summary>
    /// 31400 equip / unequip (spec §4.7): bag 5009, equipment 5007 (before the reply, so the client's gear map exists),
    /// the reply with the item, then the model (31049 equipInfo) for the owner and everyone around, then the new stats.
    /// </summary>
    public async Task EquipAsync(GameSession session, EquipItemOprReq req)
    {
        using var _ = await LockAsync(session);
        var staged = session.Items.Select(i => i.Clone()).ToList();
        var item = staged.FirstOrDefault(i => i.Guid == req.ItemGUID);
        // Refusals as (inferred from the codes' texts).
        int result = item == null ? ErrorCodes.ItemMissing : ErrorCodes.Parameter;
        var toBag = new List<InvItem>();
        var fromBag = new List<InvItem>();
        int grid = (int)req.Pos;
        if (item != null && (int)req.Opr == 0)
        {
            var equipRow = data.Row("ItemConfig", item.Entry);
            result = item.Grid != 0 ? ErrorCodes.EquipNotInBag : equipRow == null ? ErrorCodes.ItemNoConfig
                : GameData.Int(equipRow.Value, "_iType") != ItemTypeEquip ? ErrorCodes.NotEquipment : ErrorCodes.EquipSlot;
        }
        if (item != null && (int)req.Opr == 0 && IsEquipment(item.Entry) && !IsAppraised(item))
            result = ErrorCodes.EquipNotAppraised;
        else if (item != null && (int)req.Opr == 0 && item.Grid == 0 && data.Row("ItemConfig", item.Entry) is { } row && GameData.Int(row, "_iType") == ItemTypeEquip)
        {
            int part = (int)GameData.Int(row, "_iEquipPart");
            var grids = GridsFor(part);
            if (grids.Length > 0)
            {
                if (!grids.Contains(grid)) grid = grids.FirstOrDefault(g => staged.Where(i => i.Grid > 0 && i.Pack == 2 && i.Grid <= 8).All(e => e.Grid != g), grids[0]);
                // What leaves: the item in that grid; a two-hander also clears the off hand, and an off-hand item a two-hander.
                var leaving = staged.Where(i => i.Grid > 0 && i.Pack == 2 && i.Grid <= 8).Where(e => e.Grid == grid
                    || (part == 1 && e.Grid == 2)
                    || (grid == 2 && e.Grid == 1 && PartOf(e.Entry) == 1)).ToList();
                foreach (var old in leaving)
                {
                    old.Grid = 0;
                    old.Sort = staged.Where(i => i.Grid == 0 && i.Pack == old.Pack).Select(i => i.Sort).DefaultIfEmpty().Max() + 1;
                    toBag.Add(old);
                }
                item.Grid = grid;
                fromBag.Add(item);
                result = 0;
            }
        }
        else if (item != null && (int)req.Opr == 1 && item.Grid > 0)
        {
            grid = item.Grid;
            item.Grid = 0;
            item.Sort = staged.Where(i => i.Grid == 0 && i.Pack == item.Pack).Select(i => i.Sort).DefaultIfEmpty().Max() + 1;
            toBag.Add(item);
            result = 0;
        }
        log.LogInformation("{Name} {Op} {Item} ({Entry}) grid {Grid}: {Result}", session.Role!.Name, (int)req.Opr == 0 ? "equips" : "unequips",
            req.ItemGUID, item?.Entry, grid, result);
        if (result != 0)
        {
            await session.SendGameAsync(EquipItemOpr, new EquipItemOprResp { Result = result, Opr = req.Opr, Pos = req.Pos, ItemGUID = req.ItemGUID });
            return;
        }

        var plans = session.EquipmentPlans.Clone();
        var active = plans.EquipPlans[plans.CurrentEquipPlan];
        active.ItemsEquipGrid.Clear();
        foreach (var worn in staged.Where(i => i.Grid > 0 && i.Pack == 2 && i.Grid <= 8)) active.ItemsEquipGrid[worn.Grid] = worn.Guid;
        await CommitCharacterAsync(session, "equip", nextItems: staged,
            blobs: new Dictionary<string, byte[]> { ["equip_plans"] = plans.ToByteArray() },
            apply: () => session.EquipmentPlans = plans);
        // Bag first, then equipment (its increment must reach the client before the reply).
        await SyncItemsAsync(session);
        await SyncAsync(session, EquipSync, BuildEquip(session));

        await session.SendGameAsync(EquipItemOpr, new EquipItemOprResp
        {
            Result = 0,
            Opr = req.Opr,
            Pos = (EquipGridType)grid,
            ItemGUID = item!.Guid,
            Item = ItemInfo(item),
        });
        await EquipmentAppearanceChangedAsync(session);
        await CombatLoadoutChangedAsync(session);
        foreach (var handler in StateChanged) handler(session);
    }

    private int PartOf(int entry) => (int)(data.Row("ItemConfig", entry) is { } row ? GameData.Int(row, "_iEquipPart") : 0);
}
