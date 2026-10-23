using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Season talents: 5050 is the durable global/plan view; 83550 pays the next
/// SeasonTalentEffectConfig level, 83552 selects a plan, 83553 renames one. The original server's path and
/// plan policies are not captured: active-plan max-parent activation, five zero-based plans, and active-plan
/// total levels for tree thresholds are inferred from the client graph and PublicParamConfig.SchemeNum=5.
/// </summary>
public sealed partial class SeasonTalentService
{
    public const uint TalentSync = 5050, Upgrade = 83550, SetPlan = 83552, RenamePlan = 83553;
    private const long TalentFunction = 1003, ParentFunction = 10, TalentTask = 10103160, ParentTask = 10101170;
    // (inferred) server-side limits: five zero-based plans, eight Unicode scalars, three-second switch cooldown.
    private const int PlanCount = 5, NameMaxRunes = 8, SwitchCooldownSeconds = 3;
    private const int PvpNodeType = 4;
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly RoleStore _roles;
    private readonly ILogger<SeasonTalentService> _log;

    public SeasonTalentService(GameData data, CharacterService character, FunctionService functions,
        SeasonService seasons, RoleStore roles, ILogger<SeasonTalentService> log)
    {
        this._data = data;
        this._character = character;
        this._functions = functions;
        this._seasons = seasons;
        this._roles = roles;
        this._log = log;
        character.TalentAttributes = Attributes;
    }

    /// <summary>Called before the first login stat snapshot; (inferred) saved plans carry across seasons without reset.</summary>
    public void Load(GameSession session)
    {
        session.SeasonTalent = _roles.LoadSeasonTalent(session.Role!.Id) is { } bytes
            ? DBSeasonTalent.Parser.ParseFrom(bytes) : new DBSeasonTalent();
        session.TalentEffectsEnabled = false; // TaskService has not loaded this role's quest state yet.
        session.TalentPlanSwitchUtc = default;
        session.TalentSkillModifiers = [];
        session.TalentProcBuffs = [];
        session.TalentBuffAttributes = [];
        Seed(session.SeasonTalent);
    }

    /// <summary>Called after 5027 and before 5056, also when quests or progress change.</summary>
    public async Task SyncAsync(GameSession session)
    {
        if (session.SeasonTalent == null) return;
        // The DAL expects configured tree and node keys in 5050. An empty map can crash its overlay.
        if (!HasTalentGraph()) return;
        bool before = session.TalentEffectsEnabled;
        var oldTrees = session.SeasonTalent.UnlockedTrees.ToDictionary(kv => kv.Key, kv => kv.Value);
        var oldNodes = session.SeasonTalent.UnlockedNode.ToDictionary(kv => kv.Key, kv => kv.Value);
        session.TalentEffectsEnabled = Available(session);
        BuildUnlocks(session);
        await CharacterService.SyncAsync(session, TalentSync, session.SeasonTalent, session.SeasonTalent.Clone);
        // Seeding false map keys at login cannot alter stats; avoid an extra pre-world 31008.
        if (before != session.TalentEffectsEnabled || (session.TalentEffectsEnabled
            && (!Same(oldTrees, session.SeasonTalent.UnlockedTrees)
                || !Same(oldNodes, session.SeasonTalent.UnlockedNode)))) await _character.RefreshStatsAsync(session);
        await RefreshEffectsAsync(session);
    }

    private static bool Same(IReadOnlyDictionary<long, bool> old, IDictionary<long, bool> now) =>
        old.Count == now.Count && old.All(kv => now.TryGetValue(kv.Key, out bool value) && value == kv.Value);

    private bool Available(GameSession s) => s.Role != null && s.Progress != null && s.Tasks != null
        && HasTalentGraph()
        && TaskService.IsDelivered(s, ParentTask) && TaskService.IsDelivered(s, TalentTask)
        && _functions.IsOpen(s, ParentFunction) && _functions.IsOpen(s, TalentFunction)
        && Trees().Any(Applies);

    /// <summary>Use 83552's unlock and cooldown policy when Genre changes the active talent plan.</summary>
    internal int ValidateGenrePlan(GameSession session, DBSeasonTalent state, int index, bool replaceCurrent = false)
    {
        if (index == state.CurPlanId && !replaceCurrent) return 0;
        if (!Available(session)) return ErrorCodes.FeatureUnavailable;
        if (index is < 0 or >= PlanCount || !state.Plan.ContainsKey(index)) return ErrorCodes.Parameter;
        if (index != state.CurPlanId && DateTime.UtcNow - session.TalentPlanSwitchUtc < TimeSpan.FromSeconds(SwitchCooldownSeconds)) return ErrorCodes.Cooldown;
        state.CurPlanId = index;
        BuildUnlocks(session, state);
        return 0;
    }

    internal async Task SyncGenrePlanAsync(GameSession session)
    {
        await CharacterService.SyncAsync(session, TalentSync, session.SeasonTalent!, session.SeasonTalent!.Clone);
        await RefreshEffectsAsync(session);
    }

    private bool HasTalentGraph() => Trees().Any(tree => tree.Id > 0) && Nodes().Count > 0;

    private bool Applies(JsonElement row) => row.TryGetProperty("_kMultiverseArray", out var books)
        && books.ValueKind == JsonValueKind.Array
        && books.EnumerateArray().Any(v => GameData.Number(v) == _seasons.PlayBook);
    private bool Applies(TalentTree tree) => tree.Books.Any(book => book == _seasons.PlayBook);

    private void Seed(DBSeasonTalent state)
    {
        // (inferred) plan keys 0..4, default 0: the client labels a key by key+1 (LC_DAL_SeasonTalent.GetPlanNameByPlan).
        if (state.CurPlanId is < 0 or >= PlanCount) state.CurPlanId = 0;
        for (int planId = 0; planId < PlanCount; planId++)
        {
            if (!state.Plan.TryGetValue(planId, out var plan)) state.Plan[planId] = plan = new SeasonTalentPlan();
            foreach (var node in Nodes())
                if (!plan.TalentNodes.ContainsKey(node.Id)) plan.TalentNodes[node.Id] = new DBSeasonTalentNode { TalentId = node.Id };
        }
    }

    private void BuildUnlocks(GameSession s, DBSeasonTalent? candidate = null)
    {
        var state = candidate ?? s.SeasonTalent!;
        s.TalentEffectsEnabled = Available(s);
        Seed(state);
        var active = state.Plan[state.CurPlanId];
        long total = TalentLevel(state, active);
        state.UnlockedTrees.Clear();
        long? oathMax = null;
        foreach (var tree in Trees())
        {
            if (tree.Id <= 0) continue;
            bool unlocked = s.TalentEffectsEnabled && Applies(tree)
                && _functions.ConditionMet(s, tree.Condition) && total >= tree.NeedLevel;
            state.UnlockedTrees[tree.Id] = unlocked;
            // (inferred) the oath cap is the largest cap marker on an eligible, unlocked tree.
            if (unlocked) oathMax = Math.Max(oathMax ?? long.MinValue, tree.PvpLimit);
        }
        state.BattleOathMaxLevel = oathMax ?? 0;
        // Every configured node is rewritten below, so existing entries are updated in place (a login used to
        // rebuild both maps several times: ~22% of the loop's allocations in a login storm); entries of nodes the
        // table no longer has are dropped as the old clear did.
        var nodes = Nodes();
        var plans = state.Plan.Values.ToArray(); // once, not two map views and enumerators per node
        foreach (var node in nodes)
        {
            long id = node.Id, tree = node.Tree;
            bool pvp = node.Type == PvpNodeType;
            bool activated = state.UnlockedTrees.GetValueOrDefault(tree) && (pvp
                ? state.BattleOathMaxLevel > 0 : total >= node.NeedLevel
                    && (node.Start || ParentMaxed(active, id, tree)));
            state.UnlockedNode[id] = activated;
            // (inferred) the global node view exposes highest paid level across plans; selected-plan levels remain separate.
            long level = 0;
            if (pvp) level = state.BattleOathLevel;
            else foreach (var p in plans) if (p.TalentNodes.TryGetValue(id, out var n) && n.CurrentLevel > level) level = n.CurrentLevel;
            if (state.TalentNodes.TryGetValue(id, out var global)) { global.TalentId = id; global.CurrentLevel = level; global.IsActivated = activated; }
            else state.TalentNodes[id] = new DBSeasonTalentNode { TalentId = id, CurrentLevel = level, IsActivated = activated };
            foreach (var plan in plans)
            {
                plan.TalentNodes[id].IsActivated = activated;
                // The DAL overlays selected plan nodes after global nodes, including the shared oath node.
                if (pvp) plan.TalentNodes[id].CurrentLevel = state.BattleOathLevel;
            }
        }
        if (state.UnlockedNode.Count != _nodeIds!.Count)
            foreach (long stale in state.UnlockedNode.Keys.Where(k => !_nodeIds.Contains(k)).ToArray()) state.UnlockedNode.Remove(stale);
        if (state.TalentNodes.Count != _nodeIds.Count)
            foreach (long stale in state.TalentNodes.Keys.Where(k => !_nodeIds.Contains(k)).ToArray()) state.TalentNodes.Remove(stale);
    }

    private sealed record TalentNode(long Id, long Tree, long Type, long NeedLevel, bool Start);
    private sealed record TalentTree(long Id, double?[] Books, long Condition, long NeedLevel, long PvpLimit);

    /// <summary>SeasonTalentTreeConfig's rows (table order), read once: a login rebuilt the unlocks several times,
    /// each time looking every field up in the JSON rows (stage G).</summary>
    private IReadOnlyList<TalentTree> Trees() => _trees ??= _data.Table("SeasonTalentTreeConfig").Values.Select(row =>
        new TalentTree(GameData.Int(row, "_iId"),
            row.TryGetProperty("_kMultiverseArray", out var books) && books.ValueKind == JsonValueKind.Array
                ? books.EnumerateArray().Select(GameData.Number).ToArray() : [],
            GameData.Int(row, "_iTreeCondition"), GameData.Int(row, "_iNeedLevel"), GameData.Int(row, "_iUpPvPLvLimit"))).ToArray();
    private TalentTree[]? _trees;

    /// <summary>SeasonTalentConfig's nodes (id &gt; 0, table order) and each node's same-tree parents with their caps,
    /// indexed once: ParentMaxed scanned all 232 rows for every node of every rebuild.</summary>
    private IReadOnlyList<TalentNode> Nodes()
    {
        if (_nodes != null) return _nodes;
        var rows = _data.Table("SeasonTalentConfig").Values.ToArray();
        var parents = new Dictionary<(long Node, long Tree), List<(long Parent, long Max)>>();
        foreach (var candidate in rows)
        {
            if (!candidate.TryGetProperty("_kAfterids", out var after) || after.ValueKind != JsonValueKind.Array) continue;
            long tree = GameData.Int(candidate, "_iTalentTreeID"), parent = GameData.Int(candidate, "_iId"), max = GameData.Int(candidate, "_iMaxLevel");
            foreach (var v in after.EnumerateArray())
                if (GameData.Number(v) is { } child)
                {
                    var key = ((long)child, tree);
                    if (!parents.TryGetValue(key, out var list)) parents[key] = list = new();
                    list.Add((parent, max));
                }
        }
        _parents = parents.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        var nodes = rows.Select(r => new TalentNode(GameData.Int(r, "_iId"), GameData.Int(r, "_iTalentTreeID"), GameData.Int(r, "_iType"),
            GameData.Int(r, "_iNeedLevel"), GameData.Int(r, "_iIsStartPoint") == 1)).Where(n => n.Id > 0).ToArray();
        _nodeIds = nodes.Select(n => n.Id).ToHashSet();
        return _nodes = nodes;
    }

    private TalentNode[]? _nodes;
    private HashSet<long>? _nodeIds;
    private Dictionary<(long Node, long Tree), (long Parent, long Max)[]>? _parents;

    private bool ParentMaxed(SeasonTalentPlan active, long nodeId, long tree)
    {
        Nodes();
        if (_parents!.TryGetValue((nodeId, tree), out var parents))
            foreach (var (parent, max) in parents)
                if (max > 0 && active.TalentNodes.TryGetValue(parent, out var n) && n.CurrentLevel >= max) return true;
        return false; // (inferred) one maxed incoming parent opens a branch, never an unearned child.
    }

    /// <summary>The "total Talent Lv" a tree's or node's _iNeedLevel names: the grade the talent panel shows
    /// (UI_Sys_Skill_SeasonTalentPanelCtrl._buildGradeNodeIDs/_refreshGrade), the sum of every node's level except the
    /// SkillPoint nodes. The chosen Profession_L/Profession_M count, and the battle oath at its shared level. A changed
    /// choice zeroes the old path before it sets the new one, so switching never raises the total. (inferred) The server
    /// checks the total the client shows.</summary>
    private long TalentLevel(DBSeasonTalent state, SeasonTalentPlan plan) => plan.TalentNodes.Sum(kv =>
        _data.Row("SeasonTalentConfig", kv.Key) is not { } row || GameData.Int(row, "_iType") == ChoiceNodeType ? 0
            : Math.Max(0, GameData.Int(row, "_iType") == PvpNodeType ? state.BattleOathLevel : kv.Value.CurrentLevel));

    private JsonElement? Effect(long group, long level, int career)
    {
        JsonElement? generic = null;
        foreach (var row in _data.Table("SeasonTalentEffectConfig").Values)
        {
            if (GameData.Int(row, "_iGroup") != group || GameData.Int(row, "_iLevel") != level) continue;
            if (!row.TryGetProperty("_iJobId", out var jobs) || jobs.ValueKind != JsonValueKind.Array || jobs.GetArrayLength() == 0)
                generic = row;
            else if (jobs.EnumerateArray().Any(j => GameData.Number(j) == career)) return row;
            else if (jobs.EnumerateArray().Any(j => GameData.Number(j) == 0)) generic = row;
        }
        return generic; // Lua CFG GetCfgByJob falls back to job 0.
    }

    private static bool HasEntries(JsonElement row, string key) => row.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;

    private static bool Supported(JsonElement row) => HasEntries(row, "_kAttrs") && HasEntries(row, "_kCosts")
        && !HasEntries(row, "_kSpecialEffectList") && !HasEntries(row, "_kProfessionID")
        && GameData.Int(row, "_iSkillPoint") == 0 && GameData.Int(row, "_kSkillPointCosts") == 0;

    private List<(int Entry, long Count)>? Costs(JsonElement effect)
    {
        var costs = new List<(int, long)>();
        foreach (var (id, amount) in GameData.Pairs(effect, "_kCosts"))
        {
            if (id <= 0 || id > int.MaxValue || amount <= 0 || amount != Math.Truncate(amount)
                || amount > long.MaxValue || _data.Row("ItemConfig", id) == null) return null;
            costs.Add(((int)id, (long)amount));
        }
        return costs.Count > 0 ? costs : null;
    }

    /// <summary>83550: lock validation, payment, plan/oath mutation and durable save as one role operation.</summary>
    public async Task UpgradeAsync(GameSession s, SeasonTalentUpgradeReq req)
    {
        if (_data.Row("SeasonTalentConfig", req.TalentId) is { } requested
            && GameData.Int(requested, "_iType") is ProfessionBranchType or ProfessionOptionType)
        {
            await SelectProfessionAsync(s, req);
            return;
        }
        int result;
        using (await CharacterService.Lock(s))
        {
            result = UpgradeLocked(s, req.TalentId, out var cost, out var node, out var isOath);
            if (result == 0)
            {
                var next = s.SeasonTalent!.Clone();
                if (isOath) next.BattleOathLevel++;
                else next.Plan[next.CurPlanId].TalentNodes[node].CurrentLevel++; // (inferred) ordinary purchases belong to one plan
                BuildUnlocks(s, next);
                List<InvItem>? paid = null;
                try
                {
                    var economy = _character.ProgressionEconomy(s, "cd13c/season-talent-upgrade", cost!, [],
                        new
                        {
                            Node = node, Oath = isOath, Level = isOath ? next.BattleOathLevel
                            : next.Plan[next.CurPlanId].TalentNodes[node].CurrentLevel, Plan = next.CurPlanId
                        });
                    paid = await _character.ExchangeLockedAsync(s, cost!, [], popup: false, sync: false,
                        persist: tx => { tx.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray()); tx.WithEconomy(economy); },
                        committed: () => s.SeasonTalent = next, asyncCommit: true);
                }
                catch (Exception e) when (e is SqliteException or InvalidOperationException)
                {
                    _log.LogError(e, "Talent purchase {Node} for role {Role} rolled back", node, s.Role!.Id);
                    result = ErrorCodes.Unknown;
                }
                if (result == 0 && paid == null) result = _character.ShortageOf(s, cost!);
                if (paid != null)
                {
                    await _character.ItemsChangedAsync(s, stats: false); // 5009 before 5050 and the reply
                    await CharacterService.SyncAsync(s, TalentSync, next.Clone());
                    await _character.RefreshStatsAsync(s);
                    await RefreshEffectsAsync(s);
                }
            }
        }
        _log.LogInformation("{Name} talent {Node}: {Result}", s.Role?.Name, req.TalentId, result);
        await s.SendGameAsync(Upgrade, new SeasonTalentUpgradeResp { Result = result, TalentId = req.TalentId });
    }

    private int UpgradeLocked(GameSession s, long id, out List<(int Entry, long Count)>? cost, out long node, out bool oath)
    {
        cost = null; node = id; oath = false;
        if (s.SeasonTalent == null || s.Progress == null || !Available(s)) return ErrorCodes.FeatureUnavailable;
        if (_data.Row("SeasonTalentConfig", id) is not { } row) return ErrorCodes.TalentInvalid;
        BuildUnlocks(s);
        var state = s.SeasonTalent;
        if (!state.UnlockedNode.GetValueOrDefault(id)) return ErrorCodes.TalentLocked;
        oath = GameData.Int(row, "_iType") == PvpNodeType;
        long current = oath ? state.BattleOathLevel : state.Plan[state.CurPlanId].TalentNodes[id].CurrentLevel;
        long cap = oath ? state.BattleOathMaxLevel : GameData.Int(row, "_iMaxLevel");
        if (cap <= 0 || current >= cap) return ErrorCodes.SkillMaxLevel;
        long group = GameData.Int(row, "_iLevelGroupId");
        if (group <= 0 || Effect(group, current + 1, s.Progress.Career) is not { } effect || !Supported(effect))
            return ErrorCodes.FeatureUnavailable; // no invented connector, missing group or profession/special engine
        cost = Costs(effect);
        if (cost == null) return ErrorCodes.FeatureUnavailable;
        return cost.GroupBy(c => c.Entry).Any(g => _character.CountOf(s, g.Key) < g.Sum(c => c.Count))
            ? _character.ShortageOf(s, cost) : 0;
    }

    /// <summary>83552: all plans have complete node maps so the client's overlay clears old levels; (inferred) 3s cooldown.</summary>
    public async Task SetPlanAsync(GameSession s, SeasonTalentSetCurPlanReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            result = !Available(s) || s.SeasonTalent == null ? ErrorCodes.FeatureUnavailable
                : req.PlanId is < 0 or >= PlanCount ? ErrorCodes.Parameter
                : DateTime.UtcNow - s.TalentPlanSwitchUtc < TimeSpan.FromSeconds(SwitchCooldownSeconds) ? ErrorCodes.Cooldown : 0;
            if (result == 0)
            {
                var next = s.SeasonTalent!.Clone();
                next.CurPlanId = req.PlanId;
                BuildUnlocks(s, next);
                try
                {
                    var transaction = _roles.NewTransaction();
                    transaction.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray());
                    await _roles.CommitAsync(transaction, "season-talent/set-plan", _ =>
                    {
                        s.SeasonTalent = next;
                        s.TalentPlanSwitchUtc = DateTime.UtcNow;
                    });
                    await CharacterService.SyncAsync(s, TalentSync, next.Clone());
                    await _character.RefreshStatsAsync(s);
                    await RefreshEffectsAsync(s);
                }
                catch (Exception e) when (e is SqliteException or InvalidOperationException)
                {
                    _log.LogError(e, "Talent plan switch for role {Role} failed", s.Role!.Id);
                    result = ErrorCodes.Unknown;
                }
            }
        }
        await s.SendGameAsync(SetPlan, new SeasonTalentSetCurPlanResp { Result = result, Req = req });
    }

    /// <summary>83553: (inferred) trim, nonempty/no-control and eight-Unicode-scalar server validation from PublicParamConfig.SchemeNameNumMax=8.</summary>
    public async Task RenameAsync(GameSession s, SeasonTalentSetPlanNameReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            string name = req.PlanName.Trim();
            result = !Available(s) || s.SeasonTalent == null ? ErrorCodes.FeatureUnavailable
                : req.PlanId is < 0 or >= PlanCount || name.Length == 0 || name.EnumerateRunes().Count() > NameMaxRunes
                    || name.Any(char.IsControl) ? ErrorCodes.Parameter : 0;
            if (result == 0)
            {
                var next = s.SeasonTalent!.Clone();
                next.Plan[req.PlanId].PlanName = name;
                try
                {
                    var transaction = _roles.NewTransaction();
                    transaction.RoleBlob(s.Role!.Id, "season_talent", next.ToByteArray());
                    await _roles.CommitAsync(transaction, "season-talent/rename", _ => s.SeasonTalent = next);
                    await CharacterService.SyncAsync(s, TalentSync, next.Clone());
                }
                catch (Exception e) when (e is SqliteException or InvalidOperationException)
                {
                    _log.LogError(e, "Talent plan rename for role {Role} failed", s.Role!.Id);
                    result = ErrorCodes.Unknown;
                }
            }
        }
        await s.SendGameAsync(RenamePlan, new SeasonTalentSetPlanNameResp { Result = result, Req = req });
    }

    /// <summary>Only bought target-level totals contribute once; profession rows add their special effects'
    /// attributes (SeasonTalentService.Effects).</summary>
    public IEnumerable<(long Id, double Value)> Attributes(GameSession s)
    {
        foreach (var row in ActiveEffects(s))
        {
            if (Supported(row)) foreach (var pair in GameData.Pairs(row, "_kAttrs")) yield return pair;
            foreach (var pair in SpecialAttributes(s, row)) yield return pair;
        }
    }

    /// <summary>The active plan's SeasonTalentEffectConfig rows at their bought levels, and the oath's at its level.</summary>
    private IEnumerable<JsonElement> ActiveEffects(GameSession s)
    {
        if (!Available(s) || s.SeasonTalent is not { } state || s.Progress == null
            || !state.Plan.TryGetValue(state.CurPlanId, out var plan)) yield break;
        long total = TalentLevel(state, plan);
        foreach (var (id, saved) in plan.TalentNodes)
        {
            if (id == 10100000 || saved.CurrentLevel <= 0 || _data.Row("SeasonTalentConfig", id) is not { } node) continue;
            if (!TreeEligible(s, GameData.Int(node, "_iTalentTreeID"), total)
                || total < GameData.Int(node, "_iNeedLevel")
                || (GameData.Int(node, "_iIsStartPoint") != 1
                    && !ParentMaxed(plan, id, GameData.Int(node, "_iTalentTreeID")))) continue;
            if (Effect(GameData.Int(node, "_iLevelGroupId"), saved.CurrentLevel, s.Progress.Career) is { } row) yield return row;
        }
        if (state.BattleOathLevel > 0 && _data.Row("SeasonTalentConfig", 10100000) is { } oath
            && TreeEligible(s, GameData.Int(oath, "_iTalentTreeID"), total)
            && Effect(GameData.Int(oath, "_iLevelGroupId"), state.BattleOathLevel, s.Progress.Career) is { } oathRow)
            yield return oathRow;
    }

    private bool TreeEligible(GameSession s, long treeId, long total) =>
        _data.Row("SeasonTalentTreeConfig", treeId) is { } tree && Applies(tree)
        && _functions.ConditionMet(s, GameData.Int(tree, "_iTreeCondition"))
        && total >= GameData.Int(tree, "_iNeedLevel");

}
