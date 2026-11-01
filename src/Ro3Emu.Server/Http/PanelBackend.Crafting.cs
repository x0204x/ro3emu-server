using System.Globalization;
using Ro3Emu.Panel;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The crafting recipes: the life jobs' formulas (LifeFormulaConfig, made by LifeJobService.ForgeAsync) and equipment
/// crafting (EquipCraftConfig, EquipmentService.CraftAsync). A formula's products are its _iSuccessOutput rows, each
/// picked by weight and rolled through its award's drop pool as RewardService rolls it; an equipment recipe gives one of
/// its qualities by EquipCraftQualityConfig's weights of its rank among the qualities it offers.
/// </summary>
public sealed partial class PanelBackend
{
    private const string CookingKind = "cooking", EquipmentKind = "equipment";
    /// <summary>LC_ConditionList sub-condition 11001: [11001, life job, level], the active life job at that level.</summary>
    private const long LifeJobLevelCondition = 11001;

    public async Task<PanelRecipeBook> RecipesAsync() =>
        new(await RecipeListAsync(), [new PanelOption(1, "Cooking"), new PanelOption(2, "Equipment crafting")]);

    public async Task<PanelItemCrafting> ItemCraftingAsync(long id)
    {
        var catalog = await CatalogAsync();
        var ids = (catalog.Item(id)?.Variants.SelectMany(v => catalog.IdsOf(v.Id)) ?? [id]).ToHashSet();
        var recipes = await RecipeListAsync();
        return new PanelItemCrafting([.. recipes.Where(r => r.Products.Any(p => ids.Contains(p.ItemId)))],
            [.. recipes.Where(r => r.Materials.Any(m => ids.Contains(m.ItemId)))]);
    }

    private Task<List<PanelRecipe>> RecipeListAsync() => CachedAsync("recipes", async () =>
    {
        var catalog = await CatalogAsync();
        return LifeFormulaConfig.All(data).OrderBy(f => f.JobId).ThenBy(f => f.FormulaId).Select(f => Formula(f, catalog))
            .Concat(EquipCraftConfig.All(data).OrderBy(e => e.Rank).ThenBy(e => e.EquipType).Select(EquipRecipe)).ToList();
    });

    private PanelRecipe Formula(LifeFormulaConfig formula, PanelCatalog catalog)
    {
        double total = formula.SuccessOutput.Where(o => o.Length >= 3).Sum(o => (double)o[1]);
        var products = formula.SuccessOutput.Where(o => o.Length >= 3 && total > 0)
            .SelectMany(o => catalog.PoolDrops([(RewardService.AwardPool(data, o[2]), [])], "", o[1] / total))
            .Select(d => new PanelRecipeOutput(d.ItemId, d.Item, d.Quality, d.Icon, d.Min, d.Max, d.Chance)).ToList();
        var facts = new List<PanelStat>
        {
            new("Vigor", formula.VitalityCost.ToString("N0", CultureInfo.InvariantCulture)),
            new("Time", (formula.Time / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " s"),
            new("Life job EXP", formula.ExpGained.ToString("N0", CultureInfo.InvariantCulture)),
        };
        facts.AddRange(Amounts(formula.UnlockCost).Select(c => new PanelStat("To learn", c.Count.ToString("N0", CultureInfo.InvariantCulture) + " " + c.Item)));
        string name = Plain(formula.FormulaName) is { Length: > 0 } n ? n : "Formula " + formula.FormulaId.ToString(CultureInfo.InvariantCulture);
        return new PanelRecipe(formula.FormulaId, CookingKind, name, Image("items", formula.FormulaIcon), LifeJobRequirement(formula), Amounts(formula.Cost),
            products, facts, Trans(formula.Description));
    }

    /// <summary>"Chef Lv 3" from the formula's _iUnlockCondition (FunctionService evaluates the same condition).</summary>
    private string LifeJobRequirement(LifeFormulaConfig formula)
    {
        long job = formula.JobId, level = 1;
        if (LCConditionList.Get(data, formula.UnlockCondition)?.SubConditionList is { ValueKind: System.Text.Json.JsonValueKind.Array } subs)
            foreach (var sub in subs.EnumerateArray())
                if (new LCConditionList(sub).Data is [LifeJobLevelCondition, var j, var l, ..])
                    (job, level) = (j, l);
        string name = LifeJobConfig.All(data).FirstOrDefault(j => j.Id == job) is { Name.Length: > 0 } row && Name(row.Name) is { Length: > 0 } n
            ? n : "Life job " + job.ToString(CultureInfo.InvariantCulture);
        return name + " Lv " + level.ToString(CultureInfo.InvariantCulture);
    }

    private PanelRecipe EquipRecipe(EquipCraftConfig recipe)
    {
        var weights = EquipCraftQualityConfig.All(data).Where(q => q.Rank == recipe.Rank).ToDictionary(q => q.Quality, q => (double)q.Weight);
        var offered = recipe.QualityEquipId.Where(q => q.Length >= 2 && q[1] > 0).ToList();
        double total = offered.Sum(q => weights.GetValueOrDefault(q[0]));
        var products = offered.Select(q => (Amount(q[1], 1), total > 0 ? weights.GetValueOrDefault(q[0]) / total : 0))
            .Select(p => new PanelRecipeOutput(p.Item1.ItemId, p.Item1.Item, p.Item1.Quality, p.Item1.Icon, 1, 1, p.Item2)).ToList();
        string kind = SubTypeName(5, recipe.EquipType) is { Length: > 0 } k ? k : "Equipment";
        string rank = recipe.Rank.ToString(CultureInfo.InvariantCulture);
        var facts = new List<PanelStat>();
        if (recipe.OrangeGuarantee > 0) facts.Add(new PanelStat(QualityLabel(5) + " or better", "at least once in " + recipe.OrangeGuarantee + " crafts"));
        if (recipe.RedGuarantee > 0) facts.Add(new PanelStat(QualityLabel(6), "at least once in " + recipe.RedGuarantee + " crafts"));
        return new PanelRecipe(recipe.Id, EquipmentKind, "Tier " + rank + " " + kind, products.FirstOrDefault()?.Icon ?? "", "Tier " + rank + " blueprint",
            Amounts(recipe.Cost), products, facts, []);
    }
}
