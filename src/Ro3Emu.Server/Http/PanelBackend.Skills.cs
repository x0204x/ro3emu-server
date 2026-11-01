using System.Globalization;
using Ro3Emu.Panel;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The classes and their skill trees as the client's skill window lays them out (UI_Sys_Skill_AddBitWidget): a class's
/// SkillTreeConfig rows job+1, job+2, ... (up to PublicParamConfig SkillTreeMax, 10, stopping at the first missing one),
/// each row's _iX1.._iX7 a skill id in a 7-column grid, each cell passed through the viewing class's _kSkillReplace.
/// A class plays with its own tree and its ancestors' (Lua_CFG_JobConfig.GetJobList). A skill's levels are its mode-0
/// SkillConfig rows; the description is Language.Trans of _iDescription with _kDescData (Lua_CFG_SkillConfig.GetDescription).
/// </summary>
public sealed partial class PanelBackend
{
    private const int TreeColumns = 7, TreeRowsMax = 10;

    private sealed record SkillIndex(Dictionary<long, SortedDictionary<int, SkillConfig>> Levels, Dictionary<long, PanelClassSummary> Classes,
        Dictionary<long, List<(int Row, int Col, long Skill)>> Trees, Dictionary<long, long> Owner);

    public async Task<IReadOnlyList<PanelClassSummary>> ClassesAsync() => [.. (await SkillIndexAsync()).Classes.Values.OrderBy(c => c.Id)];

    public async Task<PanelClassDetail?> ClassAsync(long id)
    {
        var index = await SkillIndexAsync();
        if (!index.Classes.TryGetValue(id, out var summary) || JobConfig.Get(data, id) is not { } job) return null;
        var line = new List<PanelClassSummary>();
        for (long at = id; index.Classes.TryGetValue(at, out var c) && line.Count < 8; at = c.ParentId) line.Insert(0, c);
        var trees = line.AsEnumerable().Reverse().Select(c => Tree(index, c, job)).Where(t => t.Cells.Count > 0).ToList();
        return new PanelClassDetail(summary, Image("classes", job.BigIcon), Trans(job.JobDesc), line,
            [.. index.Classes.Values.Where(c => c.ParentId == id).OrderBy(c => c.Id)], trees);
    }

    /// <summary>A class's own tree as <paramref name="viewer"/> sees it: its replacements applied; Novice's first row centred.</summary>
    private PanelSkillTree Tree(SkillIndex index, PanelClassSummary owner, JobConfig viewer)
    {
        var replace = viewer.SkillReplace.Where(r => r.Length >= 2).GroupBy(r => r[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        var cells = index.Trees.GetValueOrDefault(owner.Id) ?? [];
        var shown = cells.Select(c => (c.Row, c.Col, Skill: replace.GetValueOrDefault(c.Skill, c.Skill)))
            .Where(c => index.Levels.ContainsKey(c.Skill)).Select(c => new PanelSkillCell(c.Row, c.Col, SkillSummary(index, c.Skill))).ToList();
        return new PanelSkillTree(owner, shown.Count > 0 ? shown.Max(c => c.Row) : 0, shown);
    }

    public async Task<PanelSkillDetail?> SkillAsync(long id)
    {
        var index = await SkillIndexAsync();
        if (!index.Levels.TryGetValue(id, out var levels)) return null;
        var summary = SkillSummary(index, id);
        var first = levels.Values.First();
        var shown = levels.Where(l => l.Key <= Math.Max(1, summary.MaxLevel)).Select(l => SkillLevel(index, l.Key, l.Value)).ToList();
        var unlocks = index.Owner.Keys.Where(other => other != id && index.Levels[other].Values
                .Any(r => r.PreSkillRequired.Any(p => p.Length >= 2 && p[0] == id && p[1] > 0)))
            .Select(other => Requirement(index, other, 0)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        index.Classes.TryGetValue(index.Owner.GetValueOrDefault(id, first.Job), out var owner);
        string element = first.DmgType > 0 && first.EleType > 0 ? ElementName(first.EleType) : "";
        string damage = first.DmgType switch { 1 => "Physical", 2 => "Magical", 3 => "Recovery", _ => "" };
        return new PanelSkillDetail(summary, owner, element, damage, first.DistanceMax / 100.0, shown, unlocks);
    }

    private PanelSkillLevel SkillLevel(SkillIndex index, int level, SkillConfig row) =>
        new(level, Trans(row.Description, row.DescData), row.Cd / 1000.0, (row.ChantFix + row.ChantVar) / 1000.0, SkillCost(row),
            [.. row.PreSkillRequired.Where(p => p.Length >= 2 && p[0] > 0).Select(p => Requirement(index, p[0], (int)p[1]))]);

    private PanelSkillRequirement Requirement(SkillIndex index, long skill, int level)
    {
        var summary = index.Levels.ContainsKey(skill) ? SkillSummary(index, skill) : new PanelSkillSummary(skill, "Skill " + skill, "", 0, false);
        return new PanelSkillRequirement(skill, summary.Name, summary.Icon, level);
    }

    /// <summary>_kCost as the skill tip shows it: [1, 2001 SP / 2000 HP, ratio per 10000, flat], [2, item, count].</summary>
    private string SkillCost(SkillConfig row)
    {
        var parts = new List<string>();
        foreach (var cost in row.Cost)
        {
            if (cost is [1, var stat, var ratio, var flat, ..] && stat is 2000 or 2001)
            {
                string name = stat == 2001 ? "SP" : "HP";
                if (flat > 0) parts.Add(name + " " + flat.ToString("N0", CultureInfo.InvariantCulture));
                if (ratio > 0) parts.Add(name + " " + (ratio / 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "%");
            }
            else if (cost is [2, var item, var count, ..] && item > 0)
                parts.Add(Amount(item, count) is var a ? a.Item + " x" + a.Count.ToString("N0", CultureInfo.InvariantCulture) : "");
        }
        return string.Join(", ", parts);
    }

    private PanelSkillSummary SkillSummary(SkillIndex index, long skill)
    {
        var first = index.Levels[skill].Values.First();
        string name = Name(first.Name) is { Length: > 0 } n ? n : "Skill " + skill.ToString(CultureInfo.InvariantCulture);
        return new PanelSkillSummary(skill, name, Image("skills", first.Icon), (int)Math.Max(1, first.MaxLevel), first.BasicType == 0);
    }

    private Task<SkillIndex> SkillIndexAsync() => CachedAsync("skills", () => Task.FromResult(BuildSkillIndex()));

    private SkillIndex BuildSkillIndex()
    {
        var levels = new Dictionary<long, SortedDictionary<int, SkillConfig>>();
        foreach (var row in SkillConfig.All(data).Where(r => r.Mode == 0 && r.SkillId > 0))
        {
            if (!levels.TryGetValue(row.SkillId, out var byLevel)) levels[row.SkillId] = byLevel = [];
            byLevel.TryAdd((int)Math.Max(1, row.Level), row);
        }
        var trees = new Dictionary<long, List<(int, int, long)>>();
        var owner = new Dictionary<long, long>();
        foreach (var job in JobConfig.All(data))
        {
            var cells = TreeCells(job.Id);
            if (cells.Count == 0) continue;
            trees[job.Id] = cells;
            foreach (var (_, _, skill) in cells) owner.TryAdd(skill, job.Id);
        }
        // Classes with a tree, and Novice, the root of every line.
        var classes = JobConfig.All(data).Where(j => trees.ContainsKey(j.Id) || j.Id == Novice)
            .ToDictionary(j => j.Id, j => new PanelClassSummary(j.Id, ClassName(j.Id), ClassIcon(j.Id), (int)j.JobRank, j.ParentId,
                trees.GetValueOrDefault(j.Id)?.Count ?? 0));
        foreach (var replaced in JobConfig.All(data).SelectMany(j => j.SkillReplace).Where(r => r.Length >= 2))
            if (owner.TryGetValue(replaced[0], out long job)) owner.TryAdd(replaced[1], job);
        return new SkillIndex(levels, classes, trees.ToDictionary(t => t.Key, t => t.Value), owner);
    }

    /// <summary>The cells of a class's own rows: (row, column) from 1, Novice's first row centred as the client centres it.</summary>
    private List<(int Row, int Col, long Skill)> TreeCells(long job)
    {
        var cells = new List<(int, int, long)>();
        for (int y = 1; y <= TreeRowsMax && SkillTreeConfig.Get(data, job + y) is { } row; y++)
        {
            long[] xs = [row.X1, row.X2, row.X3, row.X4, row.X5, row.X6, row.X7];
            int shift = job == Novice && y == 1 ? (TreeColumns - xs.Count(x => x > 0)) / 2 : 0;
            var used = xs.Select((skill, x) => (skill, x)).Where(c => c.skill > 0).ToList();
            for (int i = 0; i < used.Count; i++)
                cells.Add(((int)row.Y, shift > 0 ? shift + i + 1 : used[i].x + 1, used[i].skill));
        }
        return cells;
    }
}
