using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Astral Level (Paragon; client PatronLevel, function 71, open at base level 99): 5015 AUTO_SYNC DBParagonLevel and
/// 83101-83105 (LC_DAL_PatronLevel, UI_Sys_Personal_* panels).
/// <list type="bullet">
/// <item>Level and EXP are RoleProgress.ParagonLevel / ParagonExp (CharacterService.Paragon): Base EXP past the level
/// cap. lv is at least 1 once the function is open (the panel reads PatronLevelConfig[lv]).</item>
/// <item>runegroup is keyed by PatronTalentConfig _iID and carries every configured node, at 0 points too (the panel
/// reads runegroup[1].points unguarded). A node takes points while every node naming it in _iPostTalentID is at its
/// _iMaxLevel and its constellation is active; the client always sends points = 1 and checks only the budget and the
/// cap (UI_Sys_Personal_TalentistRunesWidgetCtrl).</item>
/// <item>activeGroup is a set of PatronTalentGroupConfig ids, always a prefix: group 1, then each group whose previous
/// one has every node at its _iMaxLevel (LC_PatronLevelHelper.IsTalentGroupCompleted; 16111 "unlocked after
/// activating all Stars in the prerequisite Constellation"). Attributes count only in active groups (the overview).</item>
/// <item>A Star Core (ItemConfig _iSubType 920-923, PatronRuneConfig) goes into a node whose _iType equals its subtype
/// and that has its point. The core leaves the bag (16113: the list shows only cores not installed) and comes back on
/// removal, a replacement or a reset (16103 "automatically unequip their Star Cores"), as one cd13b/paragon receipt.</item>
/// </list>
/// Inferred, the client never reads them: leftPoints = the _iStatusPoint of every reached level minus the allocated
/// points; lvPower = the allocated levels' _iSeasonPower plus the installed cores' (in active groups); resets are free
/// (no cost is shown); runeState is 0. Season resets (16084) are not applied: no season ends under the all-open policy.
/// Refusals: function closed 2057, unknown node, inactive group, locked node, no points or capped 82, core missing 154,
/// bag full 159.
/// </summary>
public sealed class ParagonService
{
    public const uint Sync = 5015, Allocate = 83101, Install = 83102, Remove = 83103, ResetAll = 83104, ResetSingle = 83105;
    public const int Function = 71;

    private sealed record Node(int Id, int Group, int Type, int MaxLevel, int[] Post, int[] Power, int[] AttrIds);

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly RoleStore _roles;
    private readonly ServerOptions _options;
    private Dictionary<int, Node>? _nodes;
    private Dictionary<int, int[]>? _predecessors;
    private int[]? _groups;

    public ParagonService(GameData data, CharacterService character, FunctionService functions, RoleStore roles,
        PowerService power, RewardService rewards, GmCommands gm, Microsoft.Extensions.Options.IOptions<ServerOptions> options)
    {
        this._data = data; this._character = character; this._functions = functions; this._roles = roles;
        this._options = options.Value;
        character.ExtraAttributes.Add(Attributes);
        character.StateChanged.Add(s => _ = SyncIfOpenAsync(s));
        power.ParagonPower = s => s.Paragon == null || s.Progress == null ? 0 : Power(s, s.Paragon);
        rewards.ExtraKillDrops.Add(StarCoreDrops);
        gm.Register("paragon", "exp <n> | level <n> | info (Astral Level test aid)", GmAsync);
    }

    private Dictionary<int, Node> Nodes() => _nodes ??= BuildNodes();

    private Dictionary<int, Node> BuildNodes()
    {
        var groupOf = new Dictionary<int, int>();
        foreach (var row in _data.Table("PatronTalentGroupConfig").Values)
            foreach (long id in new PatronTalentGroupConfig(row).TalentPoints) groupOf[(int)id] = (int)new PatronTalentGroupConfig(row).Id;
        _groups = [.. groupOf.Values.Distinct().Order()];
        var nodes = new Dictionary<int, Node>();
        foreach (var row in _data.Table("PatronTalentConfig").Values)
        {
            var talent = new PatronTalentConfig(row);
            int id = (int)talent.Id;
            if (!groupOf.TryGetValue(id, out int group)) continue;
            nodes[id] = new Node(id, group, (int)talent.Type, (int)talent.MaxLevel, Ints(talent.PostTalentId), Ints(talent.SeasonPower),
                Ints(talent.TalentAttrIds));
        }
        _predecessors = nodes.Keys.ToDictionary(id => id, id => nodes.Values.Where(n => n.Post.Contains(id)).Select(n => n.Id).ToArray());
        return nodes;
    }

    private static int[] Ints(long[]? values) => [.. (values ?? []).Select(v => (int)v)];

    private int[] Groups() { _ = Nodes(); return _groups!; }
    private int[] Predecessors(int id) { _ = Nodes(); return _predecessors!.GetValueOrDefault(id) ?? []; }

    public void Load(GameSession s)
    {
        var bytes = _roles.LoadParagon(s.Role!.Id);
        s.Paragon = bytes is { Length: > 0 } ? DBParagonLevel.Parser.ParseFrom(bytes) : new DBParagonLevel();
    }

    public Task LoginAsync(GameSession s) => SyncIfOpenAsync(s);

    private Task SyncIfOpenAsync(GameSession s) =>
        s.Paragon == null || s.Progress == null || s.Role == null || !_functions.IsOpen(s, Function) ? Task.CompletedTask
            : CharacterService.SyncAsync(s, Sync, Build(s, s.Paragon));

    /// <summary>The 5015 view: the stored nodes (every configured one), the progress columns and the derived fields.</summary>
    private DBParagonLevel Build(GameSession s, DBParagonLevel stored)
    {
        var p = s.Progress!;
        var info = new DBParagonLevel
        {
            Lv = Math.Max(1, p.ParagonLevel), Exp = p.ParagonExp, LeftPoints = LeftPoints(p, stored),
            LvPower = (int)Power(s, stored), ExtraAdd = _character.ParagonExpBonus(Math.Max(1, p.ParagonLevel)),
        };
        // The reset-all refund walks the map in key order and stops at the first gap (inferred: ids 28/33/35/37 do not
        // exist), so the gaps up to the highest node travel as empty entries too.
        for (int id = 1; id <= Nodes().Keys.DefaultIfEmpty(0).Max(); id++)
        {
            var rune = new ParagonLevelRune { Points = stored.Runegroup.GetValueOrDefault(id)?.Points ?? 0 };
            // A slot always carries its rune, empty as itemEntry 0: OnDecodeParagonLevelRune drops runeData only for
            // that, and an increment cannot unset the field, so a removed core travels as itemEntry 0.
            if (stored.Runegroup.GetValueOrDefault(id)?.Rune is { ItemEntry: > 0 } core) rune.Rune = core.Clone();
            else if (Nodes().TryGetValue(id, out var node) && node.Type != 1) rune.Rune = new ItemBaseInfo();
            info.Runegroup[id] = rune;
        }
        foreach (int group in ActiveGroups(stored)) info.ActiveGroup[group] = group;
        return info;
    }

    private static int Points(DBParagonLevel state, int id) => state.Runegroup.GetValueOrDefault(id)?.Points ?? 0;

    private bool Completed(DBParagonLevel state, int group) =>
        Nodes().Values.Where(n => n.Group == group).All(n => Points(state, n.Id) >= n.MaxLevel);

    /// <summary>Group 1, then every group whose previous one is completed (a prefix, as the overview walks it).</summary>
    private List<int> ActiveGroups(DBParagonLevel state)
    {
        var active = new List<int>();
        foreach (int group in Groups())
        {
            if (active.Count > 0 && !Completed(state, active[^1])) break;
            active.Add(group);
        }
        return active;
    }

    private long LeftPoints(RoleProgress p, DBParagonLevel state) =>
        _character.ParagonPointsEarned(Math.Max(1, p.ParagonLevel)) - state.Runegroup.Values.Sum(r => (long)r.Points);

    private long Power(GameSession s, DBParagonLevel state)
    {
        var active = ActiveGroups(state).ToHashSet();
        long power = 0;
        foreach (var node in Nodes().Values.Where(n => active.Contains(n.Group)))
        {
            int points = Points(state, node.Id);
            if (points > 0 && node.Power.Length > 0) power += node.Power[Math.Min(points, node.Power.Length) - 1];
            if (state.Runegroup.GetValueOrDefault(node.Id)?.Rune is { ItemEntry: > 0 } core
                && _data.Row("PatronRuneConfig", core.ItemEntry) is { } rune) power += new PatronRuneConfig(rune).SeasonPower;
        }
        return power;
    }

    /// <summary>The active groups' node attributes (_kTalentAttrIds at the node's level) and their cores' _kAttrs.</summary>
    private IEnumerable<(long Id, double Value)> Attributes(GameSession s)
    {
        if (s.Paragon is not { } state || s.Progress == null || s.Progress.Level < _character.MaxLevel) yield break;
        var active = ActiveGroups(state).ToHashSet();
        foreach (var node in Nodes().Values.Where(n => active.Contains(n.Group)))
        {
            int points = Points(state, node.Id);
            if (points <= 0) continue;
            if (node.AttrIds.Length > 0 && _data.Row("PatronTalentAttrConfig", node.AttrIds[Math.Min(points, node.AttrIds.Length) - 1]) is { } attr)
                foreach (var (id, value) in Pairs(new PatronTalentAttrConfig(attr).Attrs)) yield return (id, value);
            if (state.Runegroup.GetValueOrDefault(node.Id)?.Rune is { ItemEntry: > 0 } core && _data.Row("PatronRuneConfig", core.ItemEntry) is { } rune)
                foreach (var (id, value) in Pairs(new PatronRuneConfig(rune).Attrs)) yield return (id, value);
        }
    }

    private static IEnumerable<(long Id, double Value)> Pairs(long[][]? pairs) =>
        (pairs ?? []).Where(p => p.Length >= 2).Select(p => (p[0], (double)p[1]));

    // --- requests ---------------------------------------------------------------------------------------------------

    public async Task AllocateAsync(GameSession s, ParagonLevelAllocatePointReq req)
    {
        int result;
        using (await CharacterService.Lock(s)) result = await AllocateLockedAsync(s, req.Slot, req.Points);
        await s.SendGameAsync(Allocate, new ParagonLevelAllocatePointResp { Result = result });
    }

    private async Task<int> AllocateLockedAsync(GameSession s, int slot, int points)
    {
        if (Gate(s) is int closed and not 0) return closed;
        var state = s.Paragon!;
        if (!Nodes().TryGetValue(slot, out var node) || points <= 0 || !ActiveGroups(state).Contains(node.Group)
            || Predecessors(slot).Any(pre => Points(state, pre) < Nodes()[pre].MaxLevel)
            || Points(state, slot) + points > node.MaxLevel || LeftPoints(s.Progress!, state) < points) return ErrorCodes.Parameter;
        var next = state.Clone();
        if (!next.Runegroup.TryGetValue(slot, out var rune)) next.Runegroup[slot] = rune = new ParagonLevelRune();
        rune.Points += points;
        await SaveAsync(s, next);
        return 0;
    }

    public async Task InstallAsync(GameSession s, ParagonLevelRuneInstallReq req)
    {
        int result;
        using (await CharacterService.Lock(s)) result = await InstallLockedAsync(s, req.Slot, req.RuneGuid);
        await s.SendGameAsync(Install, new ParagonLevelRuneInstallResp { Result = result });
    }

    private async Task<int> InstallLockedAsync(GameSession s, int slot, long guid)
    {
        if (Gate(s) is int closed and not 0) return closed;
        var state = s.Paragon!;
        if (!Nodes().TryGetValue(slot, out var node) || node.Type == 1 || Points(state, slot) <= 0
            || !ActiveGroups(state).Contains(node.Group)) return ErrorCodes.Parameter;
        if (s.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0) is not { } item
            || EmblemService.IsReserved(s, item) || TotemService.IsReserved(s, item)) return ErrorCodes.ItemsShort;
        if (_data.Row("ItemConfig", item.Entry) is not { } row || new ItemConfig(row).SubType != node.Type
            || _data.Row("PatronRuneConfig", item.Entry) == null) return ErrorCodes.Parameter;
        var next = state.Clone();
        var installed = _character.ItemInfoOf(new InvItem { Entry = item.Entry, Stack = 1, BindType = item.BindType, BindExpire = item.BindExpire });
        var previous = next.Runegroup[slot].Rune;
        next.Runegroup[slot].Rune = installed;
        return await CommitCoresAsync(s, next, "install", new Dictionary<long, int> { [guid] = 1 }, [item.Entry],
            previous is { ItemEntry: > 0 } ? [previous] : []);
    }

    public async Task RemoveAsync(GameSession s, ParagonLevelRuneRemoveReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            result = Gate(s) is int closed and not 0 ? closed
                : !Nodes().ContainsKey(req.SlotId) || s.Paragon!.Runegroup.GetValueOrDefault(req.SlotId)?.Rune is not { ItemEntry: > 0 } core
                    ? ErrorCodes.Parameter
                    : await CommitCoresAsync(s, Without(s.Paragon!, [req.SlotId], keepPoints: true), "remove", null, [], [core]);
        }
        await s.SendGameAsync(Remove, new ParagonLevelRuneRemoveResp { Result = result });
    }

    /// <summary>83105: the star and every star after it along _iPostTalentID lose their points and cores (16103).</summary>
    public async Task ResetSingleAsync(GameSession s, ParagonLevelResetSingleReq req)
    {
        int result;
        using (await CharacterService.Lock(s))
        {
            if (Gate(s) is int closed and not 0) result = closed;
            else if (!Nodes().ContainsKey(req.SlotId) || Points(s.Paragon!, req.SlotId) <= 0) result = ErrorCodes.Parameter;
            else
            {
                var subtree = new List<int>();
                var open = new Stack<int>([req.SlotId]);
                while (open.TryPop(out int id))
                    if (!subtree.Contains(id) && Nodes().TryGetValue(id, out var node))
                    {
                        subtree.Add(id);
                        foreach (int post in node.Post) open.Push(post);
                    }
                result = await ResetAsync(s, subtree, "reset-single");
            }
        }
        await s.SendGameAsync(ResetSingle, new ParagonLevelResetSingleResp { Result = result });
    }

    public async Task ResetAllAsync(GameSession s)
    {
        int result;
        using (await CharacterService.Lock(s))
            result = Gate(s) is int closed and not 0 ? closed
                : s.Paragon!.Runegroup.Values.All(r => r.Points == 0) ? ErrorCodes.Parameter
                : await ResetAsync(s, [.. Nodes().Keys], "reset-all");
        await s.SendGameAsync(ResetAll, new ParagonLevelResetAllResp { Result = result });
    }

    private async Task<int> ResetAsync(GameSession s, IReadOnlyList<int> ids, string action)
    {
        var cores = ids.Select(id => s.Paragon!.Runegroup.GetValueOrDefault(id)?.Rune).Where(c => c is { ItemEntry: > 0 }).Select(c => c!).ToList();
        var next = Without(s.Paragon!, ids, keepPoints: false);
        if (cores.Count == 0)
        {
            await SaveAsync(s, next);
            return 0;
        }
        return await CommitCoresAsync(s, next, action, null, [], cores);
    }

    private static DBParagonLevel Without(DBParagonLevel state, IReadOnlyList<int> ids, bool keepPoints)
    {
        var next = state.Clone();
        foreach (int id in ids)
            if (next.Runegroup.TryGetValue(id, out var rune))
            {
                if (keepPoints && rune.Points > 0) rune.Rune = null;
                else next.Runegroup.Remove(id);
            }
        return next;
    }

    private int? Gate(GameSession s) =>
        s.Paragon == null || s.Progress == null ? ErrorCodes.Unknown
        : !_functions.IsOpen(s, Function) ? ErrorCodes.FunctionLocked : 0;

    /// <summary>A points-only change: the blob alone, then 5015 and the stats.</summary>
    private async Task SaveAsync(GameSession s, DBParagonLevel next)
    {
        _roles.SaveParagon(s.Role!.Id, next.ToByteArray());
        s.Paragon = next;
        await PublishAsync(s);
    }

    /// <summary>Cores in and out with the blob in one cd13b/paragon receipt: the paid core is a cost, a returned one a good
    /// with its binding.</summary>
    private async Task<int> CommitCoresAsync(GameSession s, DBParagonLevel next, string action, IReadOnlyDictionary<long, int>? paid,
        IReadOnlyList<int> costEntries, IReadOnlyList<ItemBaseInfo> returned)
    {
        long role = s.Role!.Id;
        byte[] bytes = next.ToByteArray();
        var result = await _character.ExchangeLockedAsync(s, [], [], paid, popup: false,
            boundGoods: [.. returned.Select(c => (c.ItemEntry, 1, (int)(c.ItemBindInfo?.ItemBindType ?? 0)))],
            persist: tx =>
            {
                tx.RoleBlob(role, "paragon", bytes);
                tx.WithEconomy(_character.FamilyEconomy(s, "cd13b/paragon-" + action, [.. costEntries.Select(e => (e, 1L))],
                    [.. returned.Select(c => (c.ItemEntry, 1))], new { Action = action, State = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) }));
            },
            committed: () => s.Paragon = next);
        if (result == null) return returned.Count > 0 ? ErrorCodes.BagFull : ErrorCodes.ItemsShort;
        await PublishAsync(s);
        return 0;
    }

    private async Task PublishAsync(GameSession s)
    {
        await SyncIfOpenAsync(s);
        await _character.RefreshStatsAsync(s);
    }

    // --- Star Core drops (inferred) ---------------------------------------------------------------------------------

    /// <summary>
    /// Star Cores have no source in the client data (ItemConfig _iAcquire and _kItemSource are empty). Reconstructed as a
    /// rare drop for a role whose Astral Level is open: 1 in 2000 per monster, 1 in 5 per boss (NPCConfig _iNPCSubType 3, 4
    /// or 6, the boss kinds), one of the four slot types at random and a quality weighted 50/25/13/7/4/1 from Gray to Red. The Cross cores
    /// (subtype 924) fit no star and do not drop.
    /// </summary>
    private IEnumerable<(int Entry, int Count)> StarCoreDrops(GameSession s, MonsterUnit monster)
    {
        if (!_options.StarCoreDrops || s.Progress == null || !_functions.IsOpen(s, Function)) yield break;
        bool boss = _data.Row("NPCConfig", monster.Template.NpcId) is { } npc && new NPCConfig(npc).NPCSubType is 3 or 4 or 6;
        if (Random.Shared.Next(boss ? 5 : 2000) != 0) yield break;
        int[] weights = [50, 25, 13, 7, 4, 1];
        int roll = Random.Shared.Next(weights.Sum()), quality = 1;
        for (int i = 0; i < weights.Length; i++) { if (roll < weights[i]) { quality = i + 1; break; } roll -= weights[i]; }
        var candidates = _data.Table("PatronRuneConfig").Values
            .Select(r => (int)new PatronRuneConfig(r).Id)
            .Where(id => _data.Row("ItemConfig", id) is { } item && new ItemConfig(item) is { SubType: >= 920 and <= 923 } config
                && config.Quality == quality).ToArray();
        if (candidates.Length > 0) yield return (candidates[Random.Shared.Next(candidates.Length)], 1);
    }

    // --- test aid ---------------------------------------------------------------------------------------------------

    private async Task GmAsync(GameSession s, string[] a)
    {
        if (a.Length >= 2 && a[0] is "exp" or "level" && long.TryParse(a[1], out long n))
        {
            string reply = a[0] == "exp" ? await _character.AddExpAsync(s, n, 0) : await _character.SetParagonLevelAsync(s, (int)n);
            await SyncIfOpenAsync(s);
            await GmCommands.ReplyAsync(s, reply);
            return;
        }
        var p = s.Progress!;
        await GmCommands.ReplyAsync(s, $"Astral Level {p.ParagonLevel} ({p.ParagonExp} EXP), {LeftPoints(p, s.Paragon!)} points left, "
            + $"groups [{string.Join(',', ActiveGroups(s.Paragon!))}], power {Power(s, s.Paragon!)}, open {_functions.IsOpen(s, Function)}");
    }
}
