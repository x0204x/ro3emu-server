using System.Text.Json;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Owned pets and incubator. 5017 is authoritative; acquisition is earned, never seeded.
/// All requests hold StateLock through validation, durable commit and sync. Unrecovered policies are marked (inferred).
/// No learned-skill field or skill-learning request exists: the client derives skills from the synchronized star/stage.
/// </summary>
public sealed partial class PetService
{
    public const uint PetSync = 5017, LevelUp = 33302, ProgressUp = 33303, StarUp = 33304, Combine = 33308,
        Rebirth = 33310, StateUpdate = 33311, FragmentsBroken = 33312, SwitchPlan = 33314, ChangePlanName = 33315,
        RoomLevelUp = 33316, OpenRoom = 33317, GenerateEgg = 33318, EggReward = 33319, SetSkipAnim = 33320,
        AccelerateEgg = 33322, Break = 33323, CollectActive = 33324, FastEgg = 33325;

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly RoleStore _roles;
    private readonly PetFollowerService _follower;
    private readonly TimeProvider _clock;
    private readonly SeasonService _seasons;
    private readonly Dictionary<(int Pet, int Star, int Stage), JsonElement> _stars;
    private readonly Dictionary<(int Quality, int Level), JsonElement> _levels;

    public PetService(GameData data, CharacterService character, FunctionService functions, RoleStore roles,
        PetFollowerService follower, TimeProvider clock, SeasonService seasons)
    {
        this._data = data;
        this._character = character;
        this._functions = functions;
        this._roles = roles;
        this._follower = follower;
        this._clock = clock;
        this._seasons = seasons;
        _stars = data.Table("PetStarConfig").Values.ToDictionary(r => ((int)GameData.Int(r, "_iPetID"), (int)GameData.Int(r, "_iStar"), (int)GameData.Int(r, "_iStage")));
        _levels = data.Table("PetExpConfig").Values.ToDictionary(r => ((int)GameData.Int(r, "_iQuality"), (int)GameData.Int(r, "_iLv")));
        character.ExtraAttributes.Add(Attributes);
        character.SpecialItemUse.Add(TryUseSelfChooseAsync);
        character.RegisterItemAction(AddIncubateRoomExpAction, UseRoomExpItemAsync);
    }

    /// <summary>5017 before 5056: empty roster, room 1, and five empty plans 0–4 (inferred initial plans/inited).</summary>
    public async Task LoginAsync(GameSession s)
    {
        s.Pets = _roles.LoadPets(s.Role!.Id) is { } saved ? Normalize(DBPetInfo.Parser.ParseFrom(saved)) : NewState();
        _roles.SavePets(s.Role.Id, s.Pets.ToByteArray());
        await SyncAsync(s);
        _character.Refresh(s);
        await _follower.RefreshAsync(s);
    }

    private static DBPetInfo NewState() =>
        Normalize(new DBPetInfo { CurrentPlan = 0, Inited = true, IncubationLv = 1, OnOpenUIRoomLv = 1 });

    /// <summary>
    /// Plans 0..4: the client shows plan N as "Build N+1" like every other build (talents send planId 0 for Build 1;
    /// the Build window lists five per system). The count is inferred from SeasonTalentService's five plans and the
    /// dropdown. States saved before this kept their only plan under key 1 (shown as "Build 2"): moved to 0.
    /// </summary>
    private static DBPetInfo Normalize(DBPetInfo state)
    {
        if (!state.SetupPlans.ContainsKey(0) && state.SetupPlans.Remove(1, out var only))
        {
            state.SetupPlans[0] = only;
            if (state.CurrentPlan == 1) state.CurrentPlan = 0;
        }
        for (int plan = 0; plan < PlanCount; plan++)
            if (!state.SetupPlans.ContainsKey(plan)) state.SetupPlans[plan] = new DBPetSetUpPlan { PlanName = "" };
        if (state.CurrentPlan is < 0 or >= PlanCount) state.CurrentPlan = 0;
        return state;
    }

    private const int PlanCount = 5;

    /// <summary>Validate all owned pets and unlocked slots before a Genre composite selects a 5017 plan.</summary>
    internal int ValidateGenrePlan(GameSession s, DBPetInfo state, int index)
    {
        if (Gate(s) is var gate and not 0) return gate;
        if (!state.SetupPlans.TryGetValue(index, out var plan)) return ErrorCodes.PetPlan;
        var selected = new HashSet<int>();
        foreach (var (slot, id) in plan.SetUpPets)
            if (!state.ListPet.ContainsKey(id)) return ErrorCodes.PetNotOwned;
            else if (!SlotOpen(s, 1, slot)) return ErrorCodes.PetSetupSlot;
            else if (!selected.Add(id)) return ErrorCodes.PetAlreadyAssigned;
        foreach (var (slot, id) in plan.SupportPets)
            if (!state.ListPet.ContainsKey(id)) return ErrorCodes.PetNotOwned;
            else if (!SlotOpen(s, 2, slot)) return ErrorCodes.PetSupportSlot;
            else if (!selected.Add(id)) return ErrorCodes.PetAlreadyAssigned;
        UpdatePower(state);
        state.CurrentPlan = index;
        return 0;
    }

    internal async Task SyncGenrePlanAsync(GameSession session)
    {
        await SyncAsync(session);
        await _follower.RefreshAsync(session);
        await PublishGuardianChangeAsync(session);
    }

    private int Gate(GameSession s, bool incubation = false) => s.Pets == null || !_functions.IsOpen(s, PetFunction)
        || incubation && (!_functions.IsOpen(s, 3301) || !_functions.IsOpen(s, 3302)) ? ErrorCodes.FeatureUnavailable : 0;

    private int Owned(GameSession s, int id) => Gate(s) is var gate and not 0 ? gate
        : _data.Row("PetBaseConfig", id) == null ? ErrorCodes.PetNoConfig
        : !s.Pets!.ListPet.ContainsKey(id) ? ErrorCodes.PetNotOwned : 0;

    private Task SyncAsync(GameSession s) => CharacterService.SyncAsync(s, PetSync, s.Pets!.Clone());

    private async Task ChangedAsync(GameSession s, bool items = false, bool stats = false)
    {
        await SyncAsync(s);
        await PublishGuardianChangeAsync(s);
        if (items) await _character.ItemsChangedAsync(s, stats: false);
        if (stats)
        {
            await _character.RefreshStatsAsync(s);
            await _follower.RefreshAsync(s);
            // A committed lineup change: quest targets read it (growth-manual 103034 CheckSetUpPetCount).
            if (!items) foreach (var handler in _character.StateChanged) handler(s);
        }
    }

    private async Task SaveAsync(GameSession s, DBPetInfo next)
    {
        UpdatePower(next);
        var tx = _roles.NewTransaction();
        tx.ItemDelta(s.Role!.Id, s.Items, s.Items);
        tx.RoleBlob(s.Role.Id, "pets", next.ToByteArray());
        tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/pet-state", [], [],
            new { StateHash = Convert.ToHexString(SHA256.HashData(next.ToByteArray())) }));
        await _roles.CommitAsync(tx, "pet/state", _ => s.Pets = next);
    }

    /// <param name="lottery">Hatch pity records (DBAppearanceLotteryData.pityRecords), committed with the pets blob.</param>
    private async Task<bool> ExchangeAsync(GameSession s, string kind, DBPetInfo next, IReadOnlyList<(int, long)> cost,
        IReadOnlyList<(int, int)>? goods = null, DBLotteryData? lottery = null)
    {
        UpdatePower(next);
        var grants = goods ?? [];
        bool done = await _character.ExchangeLockedAsync(s, cost, grants, popup: false, sync: false,
            persist: tx =>
            {
                tx.RoleBlob(s.Role!.Id, "pets", next.ToByteArray());
                if (lottery != null) tx.RoleBlob(s.Role.Id, "lottery", LotteryService.Pack(lottery, s.LotteryDay));
                tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/pet-" + kind, cost, grants,
                    new { StateHash = Convert.ToHexString(SHA256.HashData(next.ToByteArray())) }));
            }, committed: () =>
            {
                s.Pets = next;
                if (lottery != null) s.Lottery = lottery;
            }, asyncCommit: true) != null;
        if (done && lottery != null) await CharacterService.SyncAsync(s, LotteryService.LotterySync, lottery.Clone());
        return done;
    }

    private Dictionary<int, Dictionary<int, long>> BookLedger(GameSession s) => _roles.LoadPetBooks(s.Role!.Id) is { Length: > 0 } bytes
        ? JsonSerializer.Deserialize<Dictionary<int, Dictionary<int, long>>>(bytes) ?? new() : new();

    /// <summary>Pre-ledger migration: configured progression plus carried surplus, in whole smallest books.</summary>
    private Dictionary<int, long>? LegacyBooks(PetData pet)
    {
        long experience = pet.LevelExp;
        var prior = pet.Clone();
        for (int level = 1; level < pet.Level; level++)
        {
            prior.Level = level;
            if (LevelRow(prior) is not { } row || GameData.Int(row, "_iExp") <= 0) return null;
            experience = checked(experience + GameData.Int(row, "_iExp"));
        }
        return experience >= 500 ? new Dictionary<int, long> { [1250701] = experience / 500 } : new();
    }

    /// <summary>Pet state, spend ledger, items and currency commit in one CharacterService exchange transaction.</summary>
    private async Task<bool> ExchangeBooksAsync(GameSession s, string kind, DBPetInfo next, IReadOnlyList<(int, long)> cost,
        IReadOnlyList<(int, int)> goods, Dictionary<int, Dictionary<int, long>> books)
    {
        UpdatePower(next);
        var paid = await _character.ExchangeLockedAsync(s, cost, goods, popup: false, sync: false, persist: tx =>
        {
            tx.RoleBlob(s.Role!.Id, "pets", next.ToByteArray());
            tx.RoleBlob(s.Role.Id, "pet_books", JsonSerializer.SerializeToUtf8Bytes(books));
            tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/pet-" + kind, cost, goods,
                new
                {
                    StateHash = Convert.ToHexString(SHA256.HashData(next.ToByteArray())),
                    BooksHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(books)))
                }));
        }, committed: () => s.Pets = next, asyncCommit: true);
        if (paid == null) return false;
        return true;
    }

    private static PetData NewPet(int id) => new() { PetId = id, Level = 1, Star = 1 };
    private JsonElement? StarRow(PetData p) => _stars.TryGetValue((p.PetId, p.Star, p.StarProgress), out var row) ? row : null;
    private JsonElement? LevelRow(PetData p) => _data.Row("PetBaseConfig", p.PetId) is { } b
        && _levels.TryGetValue(((int)GameData.Int(b, "_iQuality"), p.Level), out var r) ? r : null;
    private static long[] Flat(JsonElement row, string key) => row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
        ? v.EnumerateArray().Select(x => (long)(GameData.Number(x) ?? 0)).ToArray() : [];
    private static List<(int, long)> Cost(JsonElement row, string key) => GameData.Pairs(row, key).Select(p => ((int)p.Id, (long)p.Value)).ToList();
    private static List<(int, long)> Piece(JsonElement row)
    {
        var pair = Flat(row, "_kPiece");
        return pair.Length == 2 && pair[0] > 0 && pair[1] > 0 ? [((int)pair[0], pair[1])] : [];
    }

    /// <summary>33308: target 1/0 _kPiece. Reply appends locally, then 5017 replaces the roster; never also send 33309.</summary>
    public async Task CombineAsync(GameSession s, PetCombineReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s);
        if (code == 0 && (!_stars.TryGetValue((req.PetId, 1, 0), out _) || _data.Row("PetBaseConfig", req.PetId) == null)) code = ErrorCodes.PetNoConfig;
        if (code == 0 && s.Pets!.ListPet.ContainsKey(req.PetId)) code = ErrorCodes.PetAlreadyOwned;
        if (code == 0)
        {
            var next = s.Pets!.Clone();
            next.ListPet[req.PetId] = NewPet(req.PetId);
            var cost = Piece(_stars[(req.PetId, 1, 0)]);
            if (cost.Count == 0) code = ErrorCodes.PetNoConfig;
            else if (!await ExchangeAsync(s, "combine", next, cost)) code = ErrorCodes.PetCombineMaterials;
        }
        await s.SendGameAsync(Combine, new PetCombineResp { Result = code, PetId = req.PetId });
        if (code == 0) await ChangedAsync(s, items: true);
    }

    /// <summary>
    /// 33302: enum 1/2 means +1/+10, capped by owner and config. Books use 500/10000/100000 EXP (§2).
    /// Consume small books first, stop at the requested level and retain all surplus EXP (inferred debit/carry policy).
    /// </summary>
    public async Task LevelAsync(GameSession s, PetLvUpReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = Owned(s, req.PetId), count = (int)req.EType switch { 1 => 1, 2 => 10, _ => 0 };
        PetData? pet = s.Pets?.ListPet.GetValueOrDefault(req.PetId);
        if (code == 0 && (count == 0 || pet!.Level + count > 99)) code = ErrorCodes.PetLevelConditions;
        if (code == 0 && pet!.Level + count > s.Progress!.Level) code = ErrorCodes.PetOwnerLevel;
        if (code == 0)
        {
            var next = s.Pets!.Clone();
            var target = next.ListPet[req.PetId];
            long need = -target.LevelExp;
            for (int n = 0; n < count; n++)
            {
                if (LevelRow(target) is not { } row || GameData.Int(row, "_iExp") <= 0) { code = ErrorCodes.PetLevelConditions; break; }
                need += GameData.Int(row, "_iExp");
                target.Level++;
            }
            var cost = new List<(int, long)>();
            foreach (var (entry, exp) in new[] { (1250701, 500L), (1250702, 10000L), (1250703, 100000L) })
            {
                if (need <= 0) break;
                long take = Math.Min(_character.CountOf(s, entry), (need + exp - 1) / exp);
                if (take > 0) { cost.Add((entry, take)); need -= take * exp; }
            }
            if (code == 0 && need > 0) code = ErrorCodes.PetLevelMaterials;
            if (code == 0)
            {
                target.LevelExp = -need;
                var books = BookLedger(s);
                if (!books.TryGetValue(req.PetId, out var spent)) books[req.PetId] = spent = LegacyBooks(pet!) ?? new();
                foreach (var (id, amount) in cost) spent[id] = checked(spent.GetValueOrDefault(id) + amount);
                if (!await ExchangeBooksAsync(s, "level", next, cost, [], books)) code = ErrorCodes.PetLevelMaterials;
                else pet = target;
            }
        }
        await s.SendGameAsync(LevelUp, new PetLvUpResp { Result = code, EType = req.EType, PetId = req.PetId, NewLevel = pet?.Level ?? 0, LevelExp = pet?.LevelExp ?? 0 });
        if (code == 0) await ChangedAsync(s, items: true, stats: true);
    }

    /// <summary>
    /// 33310: refund exact book denominations recorded by new level-ups. For pre-ledger pets,
    /// convert earned progression plus surplus to whole 500-EXP books (inferred conservative migration).
    /// The client response locally resets only level/EXP; stars and deployment are retained.
    /// </summary>
    public async Task RebirthAsync(GameSession s, PetRebirthReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = Owned(s, req.PetId);
        var pet = s.Pets?.ListPet.GetValueOrDefault(req.PetId);
        if (code == 0 && pet!.Level <= 1) code = ErrorCodes.PetOperation;
        if (code == 0)
        {
            var books = BookLedger(s);
            if (!books.TryGetValue(req.PetId, out var spent))
            {
                spent = LegacyBooks(pet!) ?? new();
            }
            if (code == 0 && spent.Any(x => x.Value < 0 || x.Value > int.MaxValue
                || x.Key is not (1250701 or 1250702 or 1250703))) code = ErrorCodes.PetNoConfig;
            if (code == 0)
            {
                var next = s.Pets!.Clone();
                next.ListPet[req.PetId].Level = 1;
                next.ListPet[req.PetId].LevelExp = 0;
                books.Remove(req.PetId);
                var goods = spent.Where(x => x.Value > 0).Select(x => (x.Key, (int)x.Value)).ToList();
                if (!await ExchangeBooksAsync(s, "rebirth", next, [], goods, books)) code = ErrorCodes.ItemNoConfig;
                else await ChangedAsync(s, items: goods.Count > 0, stats: true);
            }
        }
        await s.SendGameAsync(Rebirth, new PetRebirthResp { Result = code, PetId = req.PetId });
    }

    /// <summary>33303/33304: three stages then next star; pay the TARGET _kPiece row, never the current row (§2).</summary>
    public async Task AdvanceAsync(GameSession s, int petId, bool star)
    {
        using var held = await CharacterService.Lock(s);
        int code = Owned(s, petId);
        var pet = s.Pets?.ListPet.GetValueOrDefault(petId);
        if (code == 0 && pet!.Star >= 6) code = ErrorCodes.PetMaxStar;
        if (code == 0 && star && pet!.StarProgress != 3) code = ErrorCodes.PetNeedsProgress;
        if (code == 0 && !star && pet!.StarProgress >= 3) code = ErrorCodes.PetMaxProgress;
        if (code == 0)
        {
            var next = s.Pets!.Clone();
            var target = next.ListPet[petId];
            if (star) { target.Star++; target.StarProgress = 0; } else target.StarProgress++;
            if (StarRow(target) is not { } row || Piece(row) is not { Count: > 0 } cost) code = ErrorCodes.PetNoConfig;
            else if (!await ExchangeAsync(s, star ? "star" : "progress", next, cost)) code = star ? ErrorCodes.PetStarMaterials : ErrorCodes.PetProgressMaterials;
            else pet = target;
        }
        if (star) await s.SendGameAsync(StarUp, new PetStarUpResp { Result = code, PetId = petId, Star = pet?.Star ?? 0, StarProgress = pet?.StarProgress ?? 0 });
        else await s.SendGameAsync(ProgressUp, new PetProgressUpResp { Result = code, PetId = petId, StarProgress = pet?.StarProgress ?? 0 });
        if (code == 0) await ChangedAsync(s, items: true, stats: true);
    }

    /// <summary>
    /// 33311: owned, unique pets; stale source rejected; destination occupant swaps into source, or returns to roster.
    /// Slot limits enforce both PublicParam levels and PetSetLimitConfig conditions (inferred conservative intersection).
    /// </summary>
    public async Task LineupAsync(GameSession s, PetStateUpdateReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = Owned(s, req.PetId), source = (int)req.Source, target = (int)req.Target;
        var next = s.Pets?.Clone();
        var plan = next?.SetupPlans.GetValueOrDefault(next.CurrentPlan);
        if (code == 0 && plan == null) code = ErrorCodes.PetPlan;
        if (code == 0 && (source is < 0 or > 2 || target is < 0 or > 2 || source == target && req.SourceSlotIndex == req.TargetSlotIndex
            || source == 0 && req.SourceSlotIndex != 0 || target == 0 && req.TargetSlotIndex != 0)) code = ErrorCodes.PetOperation;
        if (code == 0 && source == 0 && plan!.SetUpPets.Values.Concat(plan.SupportPets.Values).Contains(req.PetId)) code = ErrorCodes.PetAlreadyAssigned;
        if (code == 0 && source != 0 && Slots(plan!, source).GetValueOrDefault(req.SourceSlotIndex) != req.PetId) code = ErrorCodes.PetSourceSlot;
        if (code == 0 && target != 0 && !SlotOpen(s, target, req.TargetSlotIndex)) code = target == 1 ? ErrorCodes.PetSetupSlot : ErrorCodes.PetSupportSlot;
        if (code == 0)
        {
            int displaced = target == 0 ? 0 : Slots(plan!, target).GetValueOrDefault(req.TargetSlotIndex);
            if (source != 0) Slots(plan!, source).Remove(req.SourceSlotIndex);
            if (target != 0) Slots(plan!, target)[req.TargetSlotIndex] = req.PetId;
            if (displaced != 0 && source != 0) Slots(plan!, source)[req.SourceSlotIndex] = displaced;
            await SaveAsync(s, next!);
            await ChangedAsync(s, stats: true);
        }
        var reply = new PetStateUpdateResp { Result = code, Req = req };
        if (s.Pets?.SetupPlans.GetValueOrDefault(s.Pets.CurrentPlan) is { } current)
        {
            reply.SetUpPets.Add(current.SetUpPets);
            reply.SupportPets.Add(current.SupportPets);
        }
        await s.SendGameAsync(StateUpdate, reply);
    }

    private static MapField<int, int> Slots(DBPetSetUpPlan plan, int source) => source == 1 ? plan.SetUpPets : plan.SupportPets;

    private bool SlotOpen(GameSession s, int source, int slot)
    {
        int[] levels = source == 1 ? [33, 33, 45, 50] : [40, 55, 60, 65, 70]; // PublicParamConfig Pet*FieldUnlockLv.
        return slot >= 1 && slot <= levels.Length && s.Progress!.Level >= levels[slot - 1]
            && _data.Row("PetSetLimitConfig", slot) is { } row
            && GameData.Int(row, source == 1 ? "_iCondition1" : "_iCondition2") is var condition and > 0
            && _functions.ConditionMet(s, condition);
    }

    public async Task SwitchAsync(GameSession s, PetSwitchPlanReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s);
        if (code == 0 && !s.Pets!.SetupPlans.ContainsKey(req.TargetIndex)) code = ErrorCodes.PetPlan;
        if (code == 0)
        {
            var next = s.Pets!.Clone();
            next.CurrentPlan = req.TargetIndex;
            await SaveAsync(s, next);
            await ChangedAsync(s, stats: true);
        }
        await s.SendGameAsync(SwitchPlan, new PetSwitchPlanResp { Result = code, CurrentPlan = s.Pets?.CurrentPlan ?? 0 });
    }

    /// <summary>33315: preserve all plans. A 32-character/control-free name bound is inferred; no moderation emulation.</summary>
    public async Task RenameAsync(GameSession s, PetChangePlanNameReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s);
        if (code == 0 && !s.Pets!.SetupPlans.ContainsKey(req.TargetIndex)) code = ErrorCodes.PetPlan;
        if (code == 0 && (req.Name.Length > 32 || req.Name.Any(char.IsControl))) code = ErrorCodes.Parameter;
        if (code == 0)
        {
            var next = s.Pets!.Clone();
            next.SetupPlans[req.TargetIndex].PlanName = req.Name;
            await SaveAsync(s, next);
            await SyncAsync(s);
        }
        await s.SendGameAsync(ChangePlanName, new PetChangePlanNameResp { Result = code });
    }

    /// <summary>
    /// 33324: LC_PetHelper.CanActivateCollect checks ownership and inactive state, with no star threshold.
    /// Grant the _iActiveAward preview (UI_Sys_PetRole_WidgetCtrl:508) once on activation (inferred grant point).
    /// </summary>
    public async Task ActivateAsync(GameSession s, PetCollectActiveReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = req.PetId is <= 0 or > int.MaxValue ? ErrorCodes.PetNoConfig : Owned(s, (int)req.PetId);
        if (code == 0 && s.Pets!.ListPet[(int)req.PetId].IsActiveCollect) code = ErrorCodes.AlreadyUnlocked;
        if (code == 0)
        {
            var next = s.Pets!.Clone();
            next.ListPet[(int)req.PetId].IsActiveCollect = true;
            var goods = ActivationAward((int)req.PetId);
            if (goods == null || !await ExchangeAsync(s, "collect", next, [], goods)) code = ErrorCodes.FeatureUnavailable;
            else await ChangedAsync(s, items: true, stats: true);
        }
        await s.SendGameAsync(CollectActive, new PetCollectActiveRes { Result = code, PetId = req.PetId });
    }

    private List<(int, int)>? ActivationAward(int id)
    {
        long group = GameData.Int(_data.Row("PetBaseConfig", id)!.Value, "_iActiveAward");
        if (group == 0) return [];
        if (_data.Row("MultiverseGroupConfig", group) is not { } mapping) return null;
        long award = GameData.Int(mapping, $"_iValue_{_seasons.PlayBook}");
        if (award == 0) award = GameData.Int(mapping, "_iValue_default");
        // AwardConfig is keyed by award ID, with an array of rows at each key. All supplied pet activation
        // awards (4101..4103) have one unconditional row; refuse an unknown shape instead of inventing selection.
        if (_data.Row("AwardConfig", award) is not { ValueKind: JsonValueKind.Array } rows
            || rows.GetArrayLength() != 1 || rows[0].ValueKind != JsonValueKind.Object) return null;
        var values = Cost(rows[0], "_kAward");
        return values.Any(p => p.Item2 is <= 0 or > int.MaxValue || _data.Row("ItemConfig", p.Item1) == null)
            ? null : values.Select(p => (p.Item1, (int)p.Item2)).ToList();
    }

    /// <summary>33312: fragment item _kDecompose, distinct positive counts. Multiplying the preview by count is inferred.</summary>
    public async Task DecomposeAsync(GameSession s, PetFragmentsBrokenReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s);
        if (code == 0 && (req.Fragments.Count is 0 or > 100 || req.Fragments.Select(f => f.FragmentId).Distinct().Count() != req.Fragments.Count)) code = ErrorCodes.Parameter;
        var cost = new List<(int, long)>();
        var goods = new Dictionary<int, long>();
        var fragments = _stars.Values.SelectMany(Piece).Select(p => p.Item1).ToHashSet();
        if (code == 0)
            foreach (var f in req.Fragments)
            {
                if (f.Count <= 0 || !fragments.Contains(f.FragmentId) || _data.Row("ItemConfig", f.FragmentId) is not { } row)
                { code = ErrorCodes.Parameter; break; }
                var products = Cost(row, "_kDecompose");
                if (products.Count == 0) { code = ErrorCodes.NothingToDismantle; break; }
                cost.Add((f.FragmentId, f.Count));
                foreach (var (id, count) in products)
                {
                    long total = goods.GetValueOrDefault(id) + count * f.Count;
                    if (total is <= 0 or > int.MaxValue) { code = ErrorCodes.Parameter; break; }
                    goods[id] = total;
                }
                if (code != 0) break;
            }
        if (code == 0 && !await ExchangeAsync(s, "decompose", s.Pets!.Clone(), cost, goods.Select(g => (g.Key, (int)g.Value)).ToList())) code = _character.ShortageOf(s, cost);
        if (code == 0) await ChangedAsync(s, items: true);
        await s.SendGameAsync(FragmentsBroken, new PetFragmentsBrokenResp { Result = code, Req = req });
    }

    /// <summary>Config-resolved skills, not learned state (LC_PetHelper.GetPetAllSkill; no authoritative pet combat AI).</summary>
    public (long Protect, long Passive, long[] Active, long[] Stage) ConfiguredSkills(PetData pet) => StarRow(pet) is { } row
        ? (GameData.Int(row, "_iProtectSkill"), GameData.Int(row, "_iPassiveMain"), Flat(row, "_kActiveSkill"), Flat(row, "_kStageSkill"))
        : (0, 0, [], []);

    /// <summary>
    /// Static lineup/collection bonuses from PetStarConfig. Scaling each slot by PetSetLimitConfig's addition and
    /// summing active collection attributes is inferred. Reactive effects are separate from these attributes.
    /// </summary>
    private IEnumerable<(long Id, double Value)> Attributes(GameSession s)
    {
        if (s.Pets == null || !_functions.IsOpen(s, PetFunction)) yield break;
        foreach (var pet in s.Pets.ListPet.Values.Where(p => p.IsActiveCollect))
            if (StarRow(pet) is { } row)
                foreach (var attr in GameData.Pairs(row, "_kCollectAttr")) yield return attr;
        if (s.Pets.SetupPlans.GetValueOrDefault(s.Pets.CurrentPlan) is not { } plan) yield break;
        foreach (var source in new[] { 1, 2 })
            foreach (var (slot, id) in Slots(plan, source))
                if (SlotOpen(s, source, slot) && s.Pets.ListPet.TryGetValue(id, out var pet)
                    && StarRow(pet) is { } row && _data.Row("PetSetLimitConfig", slot) is { } limit)
                    foreach (var (attr, value) in GameData.Pairs(row, source == 1 ? "_kFightSkill" : "_kAssistSkill"))
                        yield return (attr, value * GameData.Int(limit, source == 1 ? "_iFightAttrAddition" : "_iAssistAttrAddition") / 10000.0);
    }

    /// <summary>Client displayed strength: star strength + floor(level strength * multiplier / 10000), §2.</summary>
    private long Strength(PetData pet, bool support) => StarRow(pet) is { } star && LevelRow(pet) is { } level
        ? GameData.Int(star, support ? "_iStarAssistStrength" : "_iStarFightStrength")
            + GameData.Int(level, support ? "_iLvAssistStrength" : "_iLvFightStrength") * GameData.Int(star, "_iFightOrAssistStrengthMultiple") / 10000 : 0;

    private void UpdatePower(DBPetInfo next)
    {
        // Plan power aggregation is inferred; pet strength itself follows LC_PetHelper.GetPetFight/AssistStrength.
        foreach (var plan in next.SetupPlans.Values)
        {
            long power = plan.SetUpPets.Values.Sum(id => next.ListPet.TryGetValue(id, out var p) ? Strength(p, false) : 0)
                + plan.SupportPets.Values.Sum(id => next.ListPet.TryGetValue(id, out var p) ? Strength(p, true) : 0);
            plan.Power = new PowerUnit { NormalPower = power };
        }
    }
}
