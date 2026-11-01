using System.Globalization;
using System.Text.Json;
using Ro3Emu.Panel;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Http;

/// <summary>
/// Pets and mounts. A pet's stats at a level are its NPC's AttriMonsterConfig row (_iAttributeVariated * 1000 + level)
/// times PetStarConfig _iBaseAttr / 10000, as the pet window shows them; its fight and assist power are
/// PetService.Strength's (_iStarFightStrength + _iLvFightStrength * _iFightOrAssistStrengthMultiple / 10000). Its fight
/// and collection bonuses and skills are a star's stage-0 row, which the client reads for every stage. A mount's speed is
/// attribute 1131 of MountsConfig _kAttr (per 10000 of the base move speed, StatCalculator); the items that lend it are
/// those whose _iUseEffect is ["201", seconds, mount] (ItemService), 0 seconds for good.
/// </summary>
public sealed partial class PanelBackend
{
    private const long SpeedPercentAttribute = 1131, MountUseEffect = 201, MountAppearanceType = 6;
    private static readonly int[] GrowthLevels = [1, 10, 20, 30, 40, 50, 60, 70, 80, 90, 99];
    private static readonly long[] PetStats = [101, 103, 104, 105, 106];

    public Task<IReadOnlyList<PanelPetSummary>> PetsAsync() => CachedAsync<IReadOnlyList<PanelPetSummary>>("pets", () =>
        Task.FromResult<IReadOnlyList<PanelPetSummary>>([.. PetBaseConfig.All(data).Select(PetSummary).Where(p => p.Name.Length > 0)
            .OrderByDescending(p => p.Quality).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)]));

    private PanelPetSummary PetSummary(PetBaseConfig pet) =>
        new(pet.Id, Name(pet.Name), (int)pet.Quality, QualityLabel(pet.Quality), Image("pets", pet.HeadPic), Image("pets", pet.EncyclopediaPic), (int)pet.Camp);

    public async Task<PanelPet?> PetAsync(long id)
    {
        if (PetBaseConfig.Get(data, id) is not { } pet) return null;
        var summary = PetSummary(pet);
        var stars = PetStarConfig.All(data).Where(s => s.PetId == id).OrderBy(s => s.Star).ThenBy(s => s.Stage).ToList();
        if (stars.Count == 0) return new PanelPet(summary, [], [], null, null);
        var firstStage = stars.Where(s => s.Stage == 0).GroupBy(s => s.Star).ToDictionary(g => g.Key, g => g.First());
        var growth = PetGrowth(pet, stars[0]);
        var steps = stars.Select(s =>
        {
            var main = firstStage.GetValueOrDefault(s.Star, s);
            var (fight, assist) = PetPower(pet, s, 1);
            return new PanelPetStar((int)s.Star, (int)s.Stage, s.BaseAttr / 10000.0, fight, assist,
                [.. main.FightSkill.Where(a => a.Length >= 2).Select(a => AttributeLine(a[0], a[1]))],
                [.. main.CollectAttr.Where(a => a.Length >= 2).Select(a => AttributeLine(a[0], a[1]))],
                s.Piece is [var item, var count, ..] && item > 0 ? Amount(item, count) : null,
                s.Stage == 0 ? PetSkills(s) : []);
        }).ToList();
        var catalog = await CatalogAsync();
        var wild = catalog.MonstersNamed(summary.Name).Select(g => g.Rep.Summary).OrderBy(m => m.Level).FirstOrDefault();
        var fragment = pet.Decompose is [var piece, var pieces, ..] && piece > 0 ? Amount(piece, pieces) : null;
        return new PanelPet(summary, growth, steps, wild, fragment);
    }

    /// <summary>The pet's own stats, EXP to the next level and power at a few levels, at its first star.</summary>
    private List<PanelPetGrowth> PetGrowth(PetBaseConfig pet, PetStarConfig first)
    {
        long variated = NPCConfig.Get(data, pet.Monster)?.AttributeVariated ?? 0;
        var exp = PetExpConfig.All(data).Where(e => e.Quality == pet.Quality).ToDictionary(e => e.Lv);
        var rows = new List<PanelPetGrowth>();
        foreach (int level in GrowthLevels)
        {
            if (AttriMonsterConfig.Get(data, variated * 1000 + level) is not { } attri) continue;
            var values = attri.Attri.Where(a => a.Length >= 2).GroupBy(a => a[0]).ToDictionary(g => g.Key, g => g.First()[1]);
            var stats = PetStats.Where(values.ContainsKey)
                .Select(a => new PanelStat(AttributeName(a), AttributeValue(a, Math.Floor(values[a] * first.BaseAttr / 10000.0)))).ToList();
            var (fight, assist) = PetPower(pet, first, level);
            rows.Add(new PanelPetGrowth(level, stats, exp.TryGetValue(level, out var e) ? Math.Max(0, e.Exp) : 0, fight, assist));
        }
        return rows;
    }

    private (int Fight, int Assist) PetPower(PetBaseConfig pet, PetStarConfig star, int level)
    {
        var exp = PetExpConfig.All(data).Where(e => e.Quality == pet.Quality && e.Lv == level).Select(e => (PetExpConfig?)e).FirstOrDefault();
        long multiple = star.FightOrAssistStrengthMultiple, fight = exp?.LvFightStrength ?? 0, assist = exp?.LvAssistStrength ?? 0;
        return ((int)(star.StarFightStrength + fight * multiple / 10000), (int)(star.StarAssistStrength + assist * multiple / 10000));
    }

    private List<PanelPetSkill> PetSkills(PetStarConfig star)
    {
        var skills = new List<PanelPetSkill>();
        void Add(string role, long row)
        {
            if (row <= 0 || SkillConfig.Get(data, row) is not { } skill) return;
            skills.Add(new PanelPetSkill(role, skill.SkillId, Name(skill.Name), Image("skills", skill.Icon), Trans(skill.Description, skill.DescData)));
        }
        foreach (long row in star.ActiveSkill) Add("Active", row);
        Add("Passive", star.PassiveMain);
        Add("Protect", star.ProtectSkill);
        return skills;
    }

    public Task<IReadOnlyList<PanelMount>> MountsAsync() => CachedAsync<IReadOnlyList<PanelMount>>("mounts", () =>
    {
        var sources = MountItems();
        var looks = WardRobeConfig.All(data).Where(w => w.Type == MountAppearanceType).GroupBy(w => w.SystemId).ToDictionary(g => g.Key, g => g.First());
        IReadOnlyList<PanelMount> mounts = [.. MountsConfig.All(data).Select(m =>
        {
            var items = sources.GetValueOrDefault(m.Id) ?? [];
            bool dressed = looks.TryGetValue(m.Id, out var look);
            string icon = dressed ? Image("mounts", look.IconB) : "";
            if (icon.Length == 0) icon = items.Select(i => i.Icon).FirstOrDefault(i => i.Length > 0) ?? "";
            double speed = m.Attr.Where(a => a.Length >= 2 && a[0] == SpeedPercentAttribute).Sum(a => a[1]) / 100.0;
            return new PanelMount(m.Id, Name(m.Name) is { Length: > 0 } n ? n : "Mount " + m.Id, (int)m.Quality, QualityLabel(m.Quality), icon, speed,
                [.. m.Attr.Where(a => a.Length >= 2 && a[0] != SpeedPercentAttribute).Select(a => AttributeLine(a[0], a[1]))], items,
                dressed ? look.FashionScore : 0);
        }).Where(m => m.Name.Length > 0).OrderByDescending(m => m.Quality).ThenByDescending(m => m.SpeedPercent).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
        return Task.FromResult(mounts);
    });

    /// <summary>The items whose use lends a mount: _iUseEffect ["201", seconds, mount].</summary>
    private Dictionary<long, List<PanelMountSource>> MountItems()
    {
        var sources = new Dictionary<long, List<PanelMountSource>>();
        foreach (var item in ItemConfig.All(data))
        {
            if (item.UseEffect is not { ValueKind: JsonValueKind.Array } effects) continue;
            foreach (var effect in effects.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 3))
            {
                if (!long.TryParse(Param(effect[0]), CultureInfo.InvariantCulture, out long kind) || kind != MountUseEffect
                    || !long.TryParse(Param(effect[2]), CultureInfo.InvariantCulture, out long mount)) continue;
                long.TryParse(Param(effect[1]), CultureInfo.InvariantCulture, out long seconds);
                if (!sources.TryGetValue(mount, out var list)) sources[mount] = list = [];
                var amount = Amount(item.Id, 1);
                list.Add(new PanelMountSource(item.Id, amount.Item, amount.Quality, amount.Icon, seconds));
            }
        }
        return sources;
    }
}
