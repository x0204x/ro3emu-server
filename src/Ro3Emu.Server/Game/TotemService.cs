using System.Text.Json;
using System.Security.Cryptography;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Divine Totem, MG_Totem 82362..82370 and 5032 (LC_DAL_Totem, LC_TotemHelper).
/// Totems remain bag instances in pack 4; itemInfos 10001/10002/10003 identify the active deployment.
/// Client ranges/weights govern generation; uniform draws inside attribute ranges are (inferred).
/// </summary>
public sealed class TotemService
{
    public const uint Sync = 5032;
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly RoleStore _roles;
    private readonly ItemStore _items;
    private readonly FunctionService _functions;
    private readonly TotemEffects _effects;
    private readonly SeasonService _seasons;

    public TotemService(GameData data, CharacterService character, RoleStore roles, ItemStore items, FunctionService functions, TotemEffects effects, SeasonService seasons)
    {
        this._data = data; this._character = character; this._roles = roles; this._items = items; this._functions = functions; this._effects = effects; this._seasons = seasons;
        character.TotemAttrFactory = CreateAttributes;
        character.ExtraAttributes.Add(Attributes);
    }

    private string RefineEpoch => $"{_seasons.PlayBook}:{_seasons.Start.Ticks}";
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private JsonElement Param(string key) => _data.Table("PublicParamConfig").GetValueOrDefault(key);
    private int ParamInt(string key, int fallback) => (int)(GameData.Number(Param(key)) ?? fallback);
    private static long[][] Rows(JsonElement v) => v.ValueKind == JsonValueKind.Array
        ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Array).Select(x => x.EnumerateArray().Select(y => (long)(GameData.Number(y) ?? 0)).ToArray()).ToArray() : [];
    private static long[][] Rows(JsonElement row, string key) => row.TryGetProperty(key, out var v) ? Rows(v) : [];
    private static long Value(InvItem item, int key) => item.Attrs?.GetValueOrDefault(key) ?? 0;
    public static bool IsTotem(GameData data, int entry) => data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == 7;

    /// <summary>A committed combine: the session and the made totem's entry (quest target 315004).</summary>
    public List<Action<GameSession, int>> Combined { get; } = new();

    /// <summary>The active plan's deployed totem instances.</summary>
    internal static IEnumerable<InvItem> Deployed(GameSession s, GameData data)
    {
        if (s.Totems?.TotemPlans.TryGetValue(s.Totems.CurrentPlan, out var plan) != true) yield break;
        foreach (long guid in plan.SetUpTotem.Values.Where(g => g != 0).Distinct())
            if (s.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0 && i.Pack == 4 && IsTotem(data, i.Entry)) is { } item)
                yield return item;
    }

    /// <summary>
    /// Attribute lines at their bar's end: value &gt;= the upper bound 260+i, else PublicParam TotemAttrLimits
    /// (LC_ItemProtoParse.lua:700-770 iMaxValue; UI_Sys_Totem_Refine_ListWidgetCtrl.lua:235-250 shows full at iValue &gt;= iMaxValue).
    /// </summary>
    internal static int MaxedAttributes(GameData data, InvItem item)
    {
        var limits = data.Table("PublicParamConfig").TryGetValue("TotemAttrLimits", out var param) ? Rows(param) : [];
        int maxed = 0;
        for (int i = 0; i < 10 && Value(item, 220 + i) is var attr and not 0; i++)
        {
            long upper = item.Attrs!.TryGetValue(260 + i, out long bound) ? bound
                : limits.FirstOrDefault(r => r.Length >= 3 && r[0] == attr)?[2] ?? long.MaxValue;
            if (Value(item, 230 + i) >= upper) maxed++;
        }
        return maxed;
    }

    /// <summary>Every saved plan reserves its GUIDs against unrelated consumption (LC_SchemeHelper.IsInSchemeByItemInfo).</summary>
    public static bool IsReserved(GameSession s, InvItem item) => s.Totems?.TotemPlans.Values.Any(p => p.SetUpTotem.Values.Contains(item.Guid)) == true;

    public void Load(GameSession s)
    {
        string priorEpoch = System.Text.Encoding.UTF8.GetString(_roles.LoadTotemRefineEpoch(s.Role!.Id) ?? []);
        bool resetRefinements = priorEpoch.Length > 0 && priorEpoch != RefineEpoch;
        var saved = _roles.LoadTotems(s.Role!.Id);
        s.Totems = saved is { } bytes ? DBTotemInfo.Parser.ParseFrom(bytes) : new DBTotemInfo { Inited = true };
        // (inferred) Five ready, zero-based slots match the shared scheme UI, with explicit empty deployment slots.
        for (int n = 0; n < ParamInt("SchemeNum", 5); n++)
        {
            if (!s.Totems.TotemPlans.ContainsKey(n)) s.Totems.TotemPlans[n] = new TotemPlan { PlanName = $"Plan {n + 1}" };
            var plan = s.Totems.TotemPlans[n];
            for (int slot = 1; slot <= 3; slot++) if (!plan.SetUpTotem.ContainsKey(slot)) plan.SetUpTotem[slot] = 0;
            foreach (var (slot, guid) in plan.SetUpTotem.ToArray()) if (guid != 0 && !s.Items.Any(i => i.Guid == guid && i.Pack != 100 && IsTotem(_data, i.Entry))) plan.SetUpTotem[slot] = 0;
        }
        if (!s.Totems.TotemPlans.ContainsKey(s.Totems.CurrentPlan)) s.Totems.CurrentPlan = 0;
        s.TotemSwitchUtc = default;
        var copies = s.Items.Select(i => i.Clone()).ToList();
        foreach (var item in copies.Where(i => IsTotem(_data, i.Entry)))
        {
            if (item.Pack != 100) item.Pack = 4; // Merchant cart storage belongs to CharacterService.
            if (item.Attrs == null || !item.Attrs.ContainsKey(220)) item.Attrs = CreateAttributes(item.Entry);
            if (resetRefinements && item.Attrs != null) item.Attrs[270] = 0;
        }
        ApplyMarkers(copies, s.Totems, Now);
        // Stage G: only what this normalization changed is written. A transaction at every login (even with nothing
        // to change) was a third of a role's initialization in a login storm.
        var changed = copies.Where((item, i) => IsTotem(_data, item.Entry) && !SameTotemState(s.Items[i], item)).ToList();
        var totems = s.Totems.ToByteArray();
        bool totemsChanged = saved == null || !saved.AsSpan().SequenceEqual(totems), epochChanged = priorEpoch != RefineEpoch;
        if (changed.Count == 0 && _roles.IsLoginStaging(s.Role.Id))
        {
            // No item changed (a first login, a new epoch): the blobs join the selection's staged commit.
            if (totemsChanged) _roles.SaveTotems(s.Role.Id, totems);
            if (epochChanged) _roles.SaveTotemRefineEpoch(s.Role.Id, System.Text.Encoding.UTF8.GetBytes(RefineEpoch));
        }
        else if (changed.Count > 0 || totemsChanged || epochChanged)
            _roles.WithTransaction(tx =>
            {
                foreach (var item in changed) _items.Update(item, tx);
                if (totemsChanged) _roles.SaveTotems(s.Role.Id, totems, tx);
                if (epochChanged) _roles.SaveTotemRefineEpoch(s.Role.Id, System.Text.Encoding.UTF8.GetBytes(RefineEpoch), tx);
            });
        s.Items = copies; s.TotemRefineEpoch = RefineEpoch;
    }

    public Task RefreshEffectsAsync(GameSession s) => _effects.RefreshAsync(s);

    public async Task SyncAsync(GameSession s)
    {
        if (s.Totems == null) return;
        await RefreshEffectsAsync(s);
        // Power is a view of current item attributes. Keep the committed 5032 state immutable after Apply.
        var wire = s.Totems.Clone();
        foreach (var p in wire.TotemPlans.Values) p.Power = new PowerUnit { NormalPower = Power(s, p) };
        await CharacterService.SyncAsync(s, Sync, wire);
    }

    internal PowerUnit CurrentPlanPower(GameSession s, DBTotemInfo state) =>
        state.TotemPlans.TryGetValue(state.CurrentPlan, out var plan)
            ? new PowerUnit { NormalPower = Power(s, plan) } : new PowerUnit();

    /// <summary>Shared with atomic Genre application; these markers are the LC_ItemProtoParse deployment source.</summary>
    private static bool SameTotemState(InvItem before, InvItem after) => before.Pack == after.Pack
        && (before.Attrs?.Count ?? 0) == (after.Attrs?.Count ?? 0)
        && (before.Attrs == null || before.Attrs.All(kv => after.Attrs!.TryGetValue(kv.Key, out long v) && v == kv.Value));

    public static void ApplyMarkers(IEnumerable<InvItem> inventory, DBTotemInfo state, long now)
    {
        var selected = state.TotemPlans.GetValueOrDefault(state.CurrentPlan)?.SetUpTotem;
        foreach (var item in inventory)
        {
            int slot = selected?.FirstOrDefault(p => p.Value != 0 && p.Value == item.Guid).Key ?? 0;
            if (slot > 0)
            {
                item.Attrs ??= new();
                if (item.Attrs.GetValueOrDefault(10001) != 1 || item.Attrs.GetValueOrDefault(10002) != slot) item.Attrs[10003] = now;
                item.Attrs[10001] = 1; item.Attrs[10002] = slot;
            }
            else if (Value(item, 10001) == 1)
            { item.Attrs![10001] = 0; item.Attrs[10002] = 0; item.Attrs[10003] = 0; }
        }
    }

    public int ValidateSelection(GameSession s, TotemPlan plan)
    {
        if (plan.SetUpTotem.Values.Where(id => id != 0).Distinct().Count() != plan.SetUpTotem.Values.Count(id => id != 0)) return ErrorCodes.TotemMismatch;
        foreach (var (slot, guid) in plan.SetUpTotem)
        {
            if (slot is < 1 or > 3) return ErrorCodes.SlotIndex;
            if (guid == 0) continue;
            if (SlotError(s, slot) is var err and not 0) return err;
            if (ItemError(s, guid) is var itemErr and not 0) return itemErr;
        }
        return 0;
    }

    public int ValidatePlan(GameSession s, int index) => s.Totems?.TotemPlans.TryGetValue(index, out var p) == true ? ValidateSelection(s, p) : ErrorCodes.TotemPlan;

    private int SlotError(GameSession s, int slot)
    {
        var levels = Param("TotemHoleUnlockLv");
        return slot is < 1 or > 3 ? ErrorCodes.SlotIndex
            : levels.ValueKind == JsonValueKind.Array && slot <= levels.GetArrayLength() && s.Progress!.Level < GameData.Number(levels[slot - 1]) ? ErrorCodes.SlotSealed : 0;
    }

    private InvItem? Find(GameSession s, long guid) => s.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0 && i.Pack == 4 && IsTotem(_data, i.Entry));

    private int ItemError(GameSession s, long guid, bool consume = false)
    {
        var item = Find(s, guid);
        if (item == null || _data.Row("TotemConfig", item.Entry) is not { } row) return ErrorCodes.TotemInvalid;
        if (Value(item, 3) != 1) return ErrorCodes.EquipNotAppraised;
        if (consume && (item.IsLocked || IsReserved(s, item))) return item.IsLocked ? ErrorCodes.ItemLocked : ErrorCodes.CannotDismantleEquipped;
        long rank = _data.Row("JobConfig", s.Progress!.Career) is { } job ? GameData.Int(job, "_iJobRank") : 1;
        // CFG_JobConfig.GetChangeJobRank returns the exported rank directly.
        if (GameData.Int(row, "_iJobRank") > rank) return ErrorCodes.TotemJob;
        return 0;
    }

    private async Task CommitAsync(GameSession s, DBTotemInfo next, IReadOnlyList<InvItem>? updates = null)
    {
        var copies = s.Items.Select(i => updates?.FirstOrDefault(u => u.Guid == i.Guid) ?? i.Clone()).ToList();
        ApplyMarkers(copies, next, Now);
        var tx = _roles.NewTransaction();
        int itemReceipt = tx.ItemDelta(s.Role!.Id, s.Items, copies);
        tx.RoleBlob(s.Role.Id, "totems", next.ToByteArray());
        tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/totem-state", [], [],
            new
            {
                StateHash = Convert.ToHexString(SHA256.HashData(next.ToByteArray())),
                Markers = copies.Where(i => IsTotem(_data, i.Entry)).Select(i =>
                    new { i.Guid, Active = Value(i, 10001), Slot = Value(i, 10002), Stamp = Value(i, 10003) }).ToArray()
            }));
        await _roles.CommitAsync(tx, "totem/state", receipts =>
        {
            ItemSnapshotWritePlan.ApplyGuids(copies, (long[])receipts[itemReceipt]!);
            s.Items = copies; s.Totems = next;
        });
        await _character.ItemsChangedAsync(s, stats: true);
        await SyncAsync(s);
    }

    internal async Task RunOwnedAsync(GameSession s, Func<Task> command)
    {
        using var gate = await CharacterService.Lock(s);
        if (s.TotemRefineEpoch != RefineEpoch) await ReconcileEpochAsync(s);
        await command();
    }

    private async Task ReconcileEpochAsync(GameSession s)
    {
        string epoch = RefineEpoch;
        var copies = s.Items.Select(i => i.Clone()).ToList();
        foreach (var item in copies.Where(i => IsTotem(_data, i.Entry)))
            if (item.Attrs != null) item.Attrs[270] = 0;
        ApplyMarkers(copies, s.Totems!, Now);
        var tx = _roles.NewTransaction();
        int itemReceipt = tx.ItemDelta(s.Role!.Id, s.Items, copies);
        tx.RoleBlob(s.Role.Id, "totem_refine_epoch", System.Text.Encoding.UTF8.GetBytes(epoch));
        tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/totem-epoch", [], [], new
        {
            Epoch = epoch,
            Refined = copies.Where(i => IsTotem(_data, i.Entry)).Select(i => new { i.Guid, Count = Value(i, 270) }).ToArray()
        }));
        await _roles.CommitAsync(tx, "totem/epoch", receipts =>
        {
            ItemSnapshotWritePlan.ApplyGuids(copies, (long[])receipts[itemReceipt]!);
            s.Items = copies;
            s.TotemRefineEpoch = epoch;
        });
        await _character.ItemsChangedAsync(s, stats: false);
        await SyncAsync(s);
    }

    private int Gate(GameSession s) => _functions.IsOpen(s, 29) ? 0 : ErrorCodes.FunctionLocked;

    internal async Task SetupAsync(GameSession s, SetUpTotemReq req)
    {
        int error = Gate(s);
        if (error == 0) error = SlotError(s, req.TotemPos);
        if (error == 0 && req.TotemGuid != 0) error = ItemError(s, req.TotemGuid);
        long old = 0;
        if (error == 0)
        {
            var next = s.Totems!.Clone();
            var plan = next.TotemPlans[next.CurrentPlan];
            old = plan.SetUpTotem.GetValueOrDefault(req.TotemPos);
            foreach (var (slot, guid) in plan.SetUpTotem.ToArray()) if (guid == req.TotemGuid && guid != 0) plan.SetUpTotem[slot] = 0;
            plan.SetUpTotem[req.TotemPos] = req.TotemGuid;
            await CommitAsync(s, next);
        }
        await s.SendGameAsync(82362, new SetUpTotemResp { Result = error, TotemPos = req.TotemPos, TotemGuid = req.TotemGuid, OldTotemGuid = old });
    }

    private int TargetError(GameSession s, int pos, long guid)
    {
        int error = Gate(s);
        if (error != 0) return error;
        if ((error = ItemError(s, guid)) != 0) return error;
        int actual = s.Totems!.TotemPlans[s.Totems.CurrentPlan].SetUpTotem.FirstOrDefault(p => p.Value == guid).Key;
        return pos != actual ? ErrorCodes.TotemMismatch : 0;
    }

    internal async Task RefineAsync(GameSession s, RefineTotemAttrReq req)
    {
        int error = TargetError(s, req.TotemPos, req.TotemGuid);
        var item = Find(s, req.TotemGuid);
        int material = 0, count = 0;
        if (req.AttrId == 0 && Param("TotemRefineAttrItem") is { ValueKind: JsonValueKind.Array } all && all.GetArrayLength() >= 2)
        { material = (int)(GameData.Number(all[0]) ?? 0); count = (int)(GameData.Number(all[1]) ?? 0); }
        else
        {
            var specific = Rows(Param("TotemReAttrItem")).FirstOrDefault(r => r.Length >= 3 && r[2] == req.AttrId);
            if (specific != null) { material = (int)specific[0]; count = (int)specific[1]; }
        }
        if (error == 0 && (material == 0 || req.AttrId != 0 && !Enumerable.Range(220, 10).Any(k => Value(item!, k) == req.AttrId))) error = ErrorCodes.TotemAttribute;
        if (error == 0 && Value(item!, 270) >= ParamInt("TotemRefineNumPerMultiverse", 10)) error = ErrorCodes.AttemptsExhausted;
        if (error == 0)
        {
            var next = item!.Clone();
            var effect = _data.Row("ItemConfig", material) is { } mat ? Rows(mat, "_iUseEffect").FirstOrDefault(r => r.Length >= 3 && r[0] == 205) : null;
            if (effect == null) error = ErrorCodes.ItemNoConfig;
            else
            {
                for (int i = 0; i < 10 && Value(next, 220 + i) != 0; i++)
                {
                    if (req.AttrId != 0 && Value(next, 220 + i) != req.AttrId) continue;
                    long v = Value(next, 230 + i), lower = Value(next, 250 + i), upper = Value(next, 260 + i);
                    next.Attrs![230 + i] = Math.Clamp((long)Math.Floor(v * Random.Shared.NextInt64(effect[1], effect[2] + 1) / 10000d), lower, upper);
                }
                next.Attrs![270] = Value(next, 270) + 1;
                error = await PayForUpdate(s, next, [(material, (long)count)]);
            }
        }
        await s.SendGameAsync(82363, new RefineTotemAttrResp { Result = error, TotemPos = req.TotemPos, TotemGuid = req.TotemGuid, AttrId = req.AttrId });
    }

    private async Task<int> PayForUpdate(GameSession s, InvItem next, IReadOnlyList<(int Entry, long Count)> cost, IReadOnlyDictionary<long, int>? instances = null)
    {
        // Exchange's itemUpdate path stages attributes with item material payment; the target can be deployed.
        var original = s.Items.Single(i => i.Guid == next.Guid);
        var semanticCost = cost.Concat((instances ?? new Dictionary<long, int>()).Select(p =>
            (s.Items.Single(i => i.Guid == p.Key).Entry, (long)p.Value))).ToList();
        if (await _character.ExchangeLockedAsync(s, cost, [], instances, popup: false, sync: false,
            itemUpdate: (original, next.Attrs!), persist: tx =>
                tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/totem-item-update", semanticCost, [],
                    new { Target = next.Entry, Guid = next.Guid, Attrs = next.Attrs })),
            asyncCommit: true) == null)
            return cost.Count > 0 ? _character.ShortageOf(s, cost) : ErrorCodes.ItemsShort;
        await _character.ItemsChangedAsync(s, stats: true);
        await SyncAsync(s);
        return 0;
    }

    internal async Task LearnAsync(GameSession s, RefineTotemSkillReq req)
    {
        int error = TargetError(s, req.TotemPos, req.TotemGuid);
        var item = Find(s, req.TotemGuid);
        var book = s.Items.FirstOrDefault(i => i.Guid == req.SkillBookGuid && i.Grid == 0 && !i.IsLocked);
        int skill = book != null && _data.Row("ItemConfig", book.Entry) is { } row
            ? (int)(Rows(row, "_iUseEffect").FirstOrDefault(r => r.Length >= 2 && r[0] == 206)?[1] ?? 0) : 0;
        var list = item == null ? [] : Skills(item).ToList();
        long quality = item != null && _data.Row("ItemConfig", item.Entry) is { } config ? GameData.Int(config, "_iQuality") : 0;
        int limit = (int)(Rows(Param("TotemSkillInitMax")).FirstOrDefault(r => r.Length >= 2 && r[0] == quality)?[1] ?? 0);
        // Localization 10960000087 and StudyWidget's TotemSkillInitMax lookup: blue cannot learn.
        if (error == 0 && limit == 0) error = ErrorCodes.TotemInvalid;
        if (error == 0 && (skill == 0 || _data.Row("TotemSkillConfig", skill) == null)) error = ErrorCodes.ItemMissing;
        if (error == 0 && (list.Contains(skill) || list.Any(id => Superior(skill).Contains(id)))) error = ErrorCodes.TotemSkillExists;
        if (error == 0)
        {
            var next = item!.Clone();
            var born = Rows(_data.Row("TotemConfig", item.Entry)!.Value, "_kBornSkill").Select(r => (int)r[0]).ToHashSet();
            var chances = _data.Table("TotemNewGridPerConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iSkillNum") == list.Count);
            if (chances.ValueKind == JsonValueKind.Object) limit = Math.Min(limit, (int)GameData.Int(chances, "_iSkillMaxNum"));
            long chance = chances.ValueKind == JsonValueKind.Object ? GameData.Int(chances, "_iNewGridPer") : 0;
            int inferior = list.FindIndex(id => Superior(id).Contains(skill));
            if (inferior >= 0 && !born.Contains(list[inferior])) list[inferior] = skill;
            else if (list.Count < limit && Random.Shared.Next(10000) < chance) list.Add(skill);
            else
            {
                var replaceable = list.Select((id, i) => (id, i)).Where(x => !born.Contains(x.id)).ToArray();
                if (replaceable.Length == 0) error = ErrorCodes.TotemSkillExists;
                else list[replaceable[Random.Shared.Next(replaceable.Length)].i] = skill;
            }
            if (error == 0)
            {
                WriteSkills(next.Attrs!, list);
                error = await PayForUpdate(s, next, [], new Dictionary<long, int> { [book!.Guid] = 1 });
            }
        }
        await s.SendGameAsync(82364, new RefineTotemSkillResp { Result = error, TotemPos = req.TotemPos, TotemGuid = req.TotemGuid, SkillBookGuid = req.SkillBookGuid });
    }

    internal async Task UpgradeAsync(GameSession s, UpgradeTotemSystemReq req)
    {
        int error = Gate(s);
        if (error == 0 && (req.TotemGuids.Count == 0 && req.ConsumeItems.Count == 0 || req.TotemGuids.Distinct().Count() != req.TotemGuids.Count)) error = ErrorCodes.Parameter;
        long exp = 0;
        foreach (var guid in req.TotemGuids)
        {
            if (error != 0) break;
            error = ItemError(s, guid, consume: true);
            if (error == 0)
            {
                long quality = GameData.Int(_data.Row("ItemConfig", Find(s, guid)!.Entry)!.Value, "_iQuality");
                exp += Rows(Param("TotemQualityExp")).FirstOrDefault(r => r[0] == quality)?[1] ?? 0;
            }
        }
        foreach (var (entry, count) in req.ConsumeItems)
        {
            var value = Rows(Param("TotemCurrencyExp")).FirstOrDefault(r => r[0] == entry);
            if (count <= 0 || value == null) error = ErrorCodes.Parameter;
            else exp += value[1] * count;
        }
        long cap = _data.Table("TotemLvConfig").Values.Sum(r => GameData.Int(r, "_iExp"));
        var refund = new List<(int Entry, int Count)>();
        long overflow = Math.Max(0, exp - (cap - s.Totems!.TotemTotalExp));
        // The upgrade panel consumes gainItems; preserve excess EXP in configured denominations (inferred).
        foreach (var denomination in Rows(Param("TotemCurrencyExp")).OrderByDescending(r => r[1]))
        {
            long count = overflow / denomination[1];
            if (count > int.MaxValue) { error = ErrorCodes.Parameter; break; }
            if (count > 0) refund.Add(((int)denomination[0], (int)count));
            overflow %= denomination[1];
        }
        if (error == 0 && s.Totems.TotemTotalExp >= cap) error = 1401; // ErrorCodeConfig: already at maximum level.
        if (error == 0)
        {
            var next = s.Totems!.Clone(); next.TotemTotalExp = Math.Min(cap, next.TotemTotalExp + exp);
            var cost = req.ConsumeItems.Select(p => (p.Key, (long)p.Value)).ToList();
            var semanticCost = cost.Concat(req.TotemGuids.Select(g => (Find(s, g)!.Entry, 1L))).ToList();
            if (await _character.ExchangeLockedAsync(s, cost, refund, req.TotemGuids.ToDictionary(g => g, _ => 1), popup: false,
                persist: tx =>
                {
                    tx.RoleBlob(s.Role!.Id, "totems", next.ToByteArray());
                    tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/totem-upgrade", semanticCost, refund,
                        new
                        {
                            Experience = next.TotemTotalExp,
                            StateHash = Convert.ToHexString(SHA256.HashData(next.ToByteArray()))
                        }));
                }, committed: () => s.Totems = next, asyncCommit: true) == null) error = _character.ShortageOf(s, cost);
            else { await SyncAsync(s); await _character.RefreshStatsAsync(s); }
        }
        var response = new UpgradeTotemSystemResp { Result = error };
        if (error == 0) foreach (var (entry, count) in refund) response.GainItems[entry] = count;
        await s.SendGameAsync(82365, response);
    }

    internal int PlanSwitchCooldownError(GameSession s) => s.TotemSwitchUtc != default
        && DateTime.UtcNow - s.TotemSwitchUtc < TimeSpan.FromSeconds(ParamInt("SchemeCD", 3)) ? ErrorCodes.GenreCooldown : 0;

    internal async Task SwitchAsync(GameSession s, TotemSwitchPlanReq req)
    {
        int error = Gate(s);
        if (error == 0) error = ValidatePlan(s, req.TargetIndex);
        if (error == 0) error = PlanSwitchCooldownError(s);
        if (error == 0)
        {
            var next = s.Totems!.Clone(); next.CurrentPlan = req.TargetIndex;
            await CommitAsync(s, next); s.TotemSwitchUtc = DateTime.UtcNow;
        }
        await s.SendGameAsync(82367, new TotemSwitchPlanResp { Result = error, CurrentPlan = s.Totems!.CurrentPlan });
    }

    internal async Task RenameAsync(GameSession s, TotemChangeTotemNameReq req)
    {
        int error = Gate(s);
        if (error == 0 && !s.Totems!.TotemPlans.ContainsKey(req.TargetIndex)) error = ErrorCodes.TotemPlan;
        if (error == 0 && (string.IsNullOrWhiteSpace(req.Name) || req.Name.EnumerateRunes().Count() > ParamInt("SchemeNameNumMax", 8) || req.Name.Any(char.IsControl))) error = ErrorCodes.NameLengthInvalid;
        if (error == 0) { var next = s.Totems!.Clone(); next.TotemPlans[req.TargetIndex].PlanName = req.Name; await CommitAsync(s, next); }
        await s.SendGameAsync(82368, new TotemChangeTotemNameResp { Result = error });
    }

    internal async Task SwapAsync(GameSession s, TotemSwapReq req)
    {
        int error = Gate(s);
        var next = s.Totems!.Clone(); var p = next.TotemPlans[next.CurrentPlan];
        int from = p.SetUpTotem.FirstOrDefault(x => x.Value == req.SwapFromGuid && x.Value != 0).Key;
        int to = p.SetUpTotem.FirstOrDefault(x => x.Value == req.SwapToGuid && x.Value != 0).Key;
        if (error == 0 && (from == 0 || req.SwapFromGuid == req.SwapToGuid)) error = ErrorCodes.TotemSlotEmpty;
        if (error == 0) error = ItemError(s, req.SwapToGuid);
        if (error == 0)
        {
            p.SetUpTotem[from] = req.SwapToGuid;
            if (to != 0) p.SetUpTotem[to] = req.SwapFromGuid;
            await CommitAsync(s, next);
        }
        await s.SendGameAsync(82369, new TotemSwapResp { Result = error });
    }

    internal async Task AppraiseAsync(GameSession s, TotemAppraiseReq req)
    {
        int error = Gate(s);
        var updates = new List<InvItem>();
        if (error == 0 && (req.ItemGuids.Count == 0 || req.ItemGuids.Distinct().Count() != req.ItemGuids.Count)) error = ErrorCodes.Parameter;
        foreach (var guid in req.ItemGuids)
        {
            if (error != 0) break;
            var item = Find(s, guid);
            if (item == null || _data.Row("TotemConfig", item.Entry) is not { } row) { error = ErrorCodes.TotemInvalid; break; }
            if (Value(item, 3) == 1 || GameData.Int(row, "_iIfAppraise") != 1) { error = ErrorCodes.AppraiseParameter; break; }
            var next = item.Clone(); next.Attrs = Roll(row, appraised: true); updates.Add(next);
        }
        var resp = new TotemAppraiseResp { Result = error };
        if (error == 0)
        {
            await CommitAsync(s, s.Totems!.Clone(), updates);
            foreach (var item in updates) resp.AppraiseResult.Add(_character.ItemInfoOf(item));
        }
        await s.SendGameAsync(82370, resp);
    }

    internal async Task CombineAsync(GameSession s, CombineTotemReq req)
    {
        int error = Gate(s);
        var left = Find(s, req.LeftTotemGuid); var right = Find(s, req.RightTotemGuid);
        if (error == 0) error = ItemError(s, req.LeftTotemGuid, true);
        if (error == 0) error = ItemError(s, req.RightTotemGuid, true);
        if (error == 0 && req.LeftTotemGuid == req.RightTotemGuid) error = ErrorCodes.TotemCombine;
        if (error == 0 && (GameData.Int(_data.Row("TotemConfig", left!.Entry)!.Value, "_iIfCompose") != 0
            || GameData.Int(_data.Row("TotemConfig", right!.Entry)!.Value, "_iIfCompose") != 0)) error = ErrorCodes.TotemCombine;
        long quality = left == null ? 0 : GameData.Int(_data.Row("ItemConfig", left.Entry)!.Value, "_iQuality");
        if (error == 0 && (quality != GameData.Int(_data.Row("ItemConfig", right!.Entry)!.Value, "_iQuality")
            || GameData.Int(_data.Row("TotemConfig", left!.Entry)!.Value, "_iJobRank") != GameData.Int(_data.Row("TotemConfig", right.Entry)!.Value, "_iJobRank"))) error = ErrorCodes.TotemMismatch;
        long newGuid = 0;
        if (error == 0)
        {
            if (left == null || right == null) throw new InvalidOperationException("Validated totem inputs disappeared.");
            var next = left!.Clone(); next.Guid = 0; next.IsLocked = false; next.Attrs = new(left.Attrs!);
            // Permanent bind dominates temporary bind; temporary binds retain the later expiry (inferred).
            next.BindType = left.BindType == 1 || right!.BindType == 1 ? 1 : Math.Max(left.BindType, right.BindType);
            next.BindExpire = next.BindType == 2 ? Math.Max(left.BindExpire, right.BindExpire) : 0;
            var limits = Rows(Param("TotemAttrLimits")); var spread = Param("TotemComposeAttrRange");
            for (int i = 0; i < 10 && Value(left, 220 + i) != 0; i++)
            {
                long attr = Value(left, 220 + i), a = Value(left, 230 + i);
                int ri = Enumerable.Range(0, 10).FirstOrDefault(n => Value(right, 220 + n) == attr, -1);
                long center = ri < 0 ? a : (a + Value(right, 230 + ri)) / 2;
                var cap = limits.FirstOrDefault(r => r[0] == attr);
                long low = Math.Max(cap?[1] ?? 0, (long)Math.Floor(center * (1 - (GameData.Number(spread[0]) ?? 0) / 10000)));
                long high = Math.Min(cap?[2] ?? long.MaxValue, (long)Math.Floor(center * (1 + (GameData.Number(spread[1]) ?? 0) / 10000)));
                next.Attrs[230 + i] = Random.Shared.NextInt64(low, Math.Max(low, high) + 1);
            }
            var pool = Skills(left).Concat(Skills(right)).Distinct().ToList();
            var cfg = _data.Row("TotemConfig", left.Entry)!.Value;
            var countRows = _data.Table("TotemSkillNumRandom").Values.Where(r => GameData.Int(r, "_iJobRank") == GameData.Int(cfg, "_iJobRank")
                && GameData.Int(r, "_iQuality") == quality && GameData.Int(r, "_iTotalSkills") == pool.Count).ToArray();
            int count = countRows.Length == 0 ? pool.Count : (int)GameData.Int(Weighted(countRows, "_iWeight"), "_iSkillNum");
            var born = Rows(cfg, "_kBornSkill").Select(r => (int)r[0]).ToList();
            var chosen = new List<int>(born);
            foreach (int skill in pool.OrderBy(_ => Random.Shared.Next()).Where(x => !chosen.Contains(x))) { if (chosen.Count >= count) break; chosen.Add(skill); }
            WriteSkills(next.Attrs, chosen.Take(10)); next.Attrs[270] = Math.Max(Value(left, 270), Value(right, 270));
            // (inferred) Left supplies the output template; both GUIDs and output commit together. No rerolled grant hook.
            var copies = s.Items.Where(i => i.Guid != left.Guid && i.Guid != right.Guid)
                .Select(i => i.Clone()).Append(next).ToList();
            var tx = _roles.NewTransaction();
            int itemReceipt = tx.ItemDelta(s.Role!.Id, s.Items, copies);
            tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/totem-combine",
                [(left.Entry, 1L), (right.Entry, 1L)], [(next.Entry, 1)],
                new
                {
                    Left = left.Entry, Right = right.Entry, Output = next.Entry,
                    next.BindType, next.BindExpire, next.Attrs
                }));
            await _roles.CommitAsync(tx, "totem/combine", receipts =>
            {
                ItemSnapshotWritePlan.ApplyGuids(copies, (long[])receipts[itemReceipt]!);
                s.Items = copies;
                newGuid = next.Guid;
            });
            await _character.ItemsChangedAsync(s, stats: true); await SyncAsync(s);
            foreach (var handler in Combined) handler(s, next.Entry);
        }
        await s.SendGameAsync(82366, new CombineTotemResp { Result = error, NewTotemGuid = newGuid });
    }

    public Dictionary<int, long>? CreateAttributes(int entry) => _data.Row("TotemConfig", entry) is { } row ? Roll(row, GameData.Int(row, "_iIfAppraise") == 0) : null;

    private Dictionary<int, long> Roll(JsonElement row, bool appraised)
    {
        var result = new Dictionary<int, long> { [3] = appraised ? 1 : 0, [270] = 0 };
        int i = 0;
        foreach (var r in Rows(row, "_kBasicAttribute").Where(r => r.Length >= 3).Take(10))
        {
            var limit = Rows(Param("TotemAttrLimits")).FirstOrDefault(x => x[0] == r[0]);
            result[220 + i] = r[0]; result[230 + i] = Random.Shared.NextInt64(r[1], r[2] + 1);
            result[250 + i] = limit?[1] ?? r[1]; result[260 + i] = limit?[2] ?? r[2]; i++;
        }
        if (!appraised) return result;
        var skills = Rows(row, "_kBornSkill").Where(r => r.Length >= 2 && Random.Shared.Next(10000) < r[1]).Select(r => (int)r[0]).ToList();
        var countRows = Rows(row, "_kSkillNumInit");
        int count = (int)WeightedPair(countRows);
        var pool = Rows(row, "_kSkillGroup").Where(r => r.Length >= 2).ToList();
        while (count-- > 0 && pool.Count > 0)
        {
            int selected = (int)WeightedPair(pool.ToArray());
            if (!skills.Contains(selected)) skills.Add(selected);
            pool.RemoveAll(r => r[0] == selected);
        }
        WriteSkills(result, skills.Take(10)); return result;
    }

    private static long WeightedPair(long[][] rows)
    {
        long weight = rows.Where(r => r.Length >= 2).Sum(r => Math.Max(0, r[1]));
        if (weight == 0) return 0;
        long roll = Random.Shared.NextInt64(weight);
        foreach (var row in rows) if (row.Length >= 2 && (roll -= Math.Max(0, row[1])) < 0) return row[0];
        return 0;
    }
    private static JsonElement Weighted(JsonElement[] rows, string field)
    {
        long roll = Random.Shared.NextInt64(rows.Sum(r => GameData.Int(r, field)));
        foreach (var row in rows) if ((roll -= GameData.Int(row, field)) < 0) return row;
        return rows[0];
    }
    internal static IEnumerable<int> Skills(InvItem item) => Enumerable.Range(240, 10).Select(k => (int)Value(item, k)).TakeWhile(x => x > 0);
    private static void WriteSkills(Dictionary<int, long> attrs, IEnumerable<int> skills)
    { for (int n = 240; n < 250; n++) attrs.Remove(n); int key = 240; foreach (int id in skills) attrs[key++] = id; }
    private HashSet<int> Superior(int skill)
    {
        var result = new HashSet<int>();
        while (_data.Row("TotemSkillConfig", skill) is { } row && GameData.Int(row, "_iSuperiorSkillID") is var id and > 0 && result.Add((int)id)) skill = (int)id;
        return result;
    }
    public int Level(long exp)
    {
        int level = 0; long total = 0;
        foreach (var row in _data.Table("TotemLvConfig").Values.OrderBy(r => GameData.Int(r, "_iLv")))
        { total += GameData.Int(row, "_iExp"); if (total > exp) break; level = (int)GameData.Int(row, "_iLv"); }
        return level;
    }
    public IEnumerable<(long Id, double Value)> Attributes(GameSession s)
    {
        if (s.Totems?.TotemPlans.TryGetValue(s.Totems.CurrentPlan, out var plan) != true) yield break;
        int level = Level(s.Totems.TotemTotalExp);
        var lv = _data.Table("TotemLvConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iLv") == level);
        double multiplier = 1 + (lv.ValueKind == JsonValueKind.Object ? GameData.Int(lv, "_iAttributeEnhance") / 10000d : 0);
        var equipped = plan.SetUpTotem.Values.Where(x => x != 0).Select(id => Find(s, id)).OfType<InvItem>().ToArray();
        var allSkills = equipped.SelectMany(Skills).ToHashSet(); var used = new HashSet<int>();
        foreach (var item in equipped)
        {
            for (int i = 0; i < 10 && Value(item, 220 + i) != 0; i++) yield return (Value(item, 220 + i), Value(item, 230 + i) * multiplier);
            foreach (int skill in Skills(item))
            {
                if (_data.Row("TotemSkillConfig", skill) is not { } row || Superior(skill).Overlaps(allSkills)) continue;
                if (GameData.Int(row, "_iIsCoexist") == 0 && !used.Add(skill)) continue;
                foreach (var attr in _effects.Attributes(s, item, skill)) yield return attr;
            }
        }
    }

    /// <summary>The sum of the deployed totems' LC_TotemHelper.GetTotemScore, each rounded up on its own (:1198-1248).</summary>
    private long Power(GameSession s, TotemPlan plan)
    {
        long total = 0;
        foreach (var id in plan.SetUpTotem.Values.Where(g => g != 0))
        {
            var item = Find(s, id); if (item == null) continue;
            double power = 0;
            for (int n = 0; n < 10 && Value(item, 220 + n) != 0; n++)
                if (_data.Row("AttributeConfig", Value(item, 220 + n)) is { } attr && attr.TryGetProperty("_iPower", out var weight)) power += Value(item, 230 + n) * (GameData.Number(weight) ?? 0);
            foreach (var skill in Skills(item)) if (_data.Row("TotemSkillConfig", skill) is { } row) power += GameData.Int(row, "_iPower");
            total += (long)Math.Ceiling(power);
        }
        return total;
    }
}
