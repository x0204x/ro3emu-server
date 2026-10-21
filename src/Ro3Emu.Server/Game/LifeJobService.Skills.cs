using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class LifeJobService
{
    // LC_DAL_LifeJob stores selected skill ids in the title slots. LifeSkillSlots owns the
    // offers for each job/slot; LifeSkillConfig owns the effect value. _iProbability is an
    // offer weight (10000, 8000, 5000, 2000, 500 by rank), not a proc probability.
    private readonly record struct LifeSkill(long Job, int Slot, bool ExtraSlot, int Type, long Value,
        long[] Extra, JsonElement Config);

    private IEnumerable<LifeSkill> SelectedSkills(GameSession s, DBLifeJobInfo? selectedLife = null)
    {
        if ((selectedLife ?? s.LifeJob) is not { } life) yield break;
        foreach (long job in life.ActiveLifejob.Values.Distinct())
        {
            if (!life.LifeJobInfoMap.TryGetValue(job, out var info)) continue;
            foreach (var (slots, extra) in new[]
            {
                (info.TitleInfo.JobTitleDetail, false), (info.TitleInfo.SkillExtraSkill, true),
            })
                foreach (var (sort, chosen) in slots)
                {
                    int id = chosen.SelectedSkill;
                    int offeredId = chosen.OriginalSkill > 0 ? chosen.OriginalSkill : id;
                    if (id <= 0 || _data.Row("LifeSkillConfig", id) is not { } skill || !Current(skill)) continue;
                    if (_data.Row("LifeSkillConfig", offeredId) is not { } offered
                        || GameData.Int(offered, "_iGroup") != GameData.Int(skill, "_iGroup")
                        || GameData.Int(skill, "_iLevel") < GameData.Int(offered, "_iLevel")) continue;
                    var slot = _data.Table("LifeSkillSlots").Values.FirstOrDefault(r =>
                        GameData.Int(r, "_iJobId") == job && GameData.Int(r, "_iSort") == sort);
                    if (slot.ValueKind != JsonValueKind.Object || !GameData.Pairs(slot, "_kSkillpool").Any(p => p.Id == offeredId))
                        continue;
                    yield return new LifeSkill(job, sort, extra, checked((int)GameData.Int(skill, "_iFuctionType")),
                        Numbers(skill, "_kFuctionDatas").FirstOrDefault(), Numbers(skill, "_kFuctionExtraData"), skill);
                }
        }
    }

    private static long Extra(LifeSkill skill, int at) => skill.Extra.Length > at ? skill.Extra[at] : 0;
    private static bool Chance(long perHundredThousand) => Random.Shared.NextInt64(100_000)
        < Math.Clamp(perHundredThousand, 0, 100_000);
    private bool BothActiveOfType(GameSession s, long type) => s.LifeJob is { } life
        && life.ActiveLifejob.Count == 2 && life.ActiveLifejob.Values.All(job =>
            _data.Row("LifeJobConfig", job) is { } row && GameData.Int(row, "_iType") == type);
    private static bool HasCategory(JsonElement row, bool forge, long category) => category > 0
        && Numbers(row, forge ? "_iTypeConfig" : "_iTypeConfigIDs").Contains(category);

    /// <summary>
    /// Configured flat Vivify reductions; free-cost chance skills are rolled once at settlement.
    /// Stacking and reduction order are inferred from the exposed config values and descriptions.
    /// </summary>
    private int VigorCost(GameSession s, JsonElement row, bool forge)
    {
        long cost = GameData.Int(row, "_iVitalitycost");
        foreach (var skill in SelectedSkills(s))
            cost -= skill.Type switch
            {
                6 when !forge => skill.Value,
                7 when !forge && BothActiveOfType(s, 1) => skill.Value,
                8 when !forge && GameData.Int(row, "_iQuality") >= Extra(skill, 0)
                    && HasCategory(row, false, Extra(skill, 1)) => skill.Value,
                22 when forge => skill.Value,
                23 when forge && BothActiveOfType(s, 2) => skill.Value,
                24 when forge && GameData.Int(row, "_iQuality") >= Extra(skill, 0) => skill.Value,
                _ => 0,
            };
        return checked((int)Math.Max(0, cost));
    }

    /// <summary>_iPlayParam[1] and _iTime are milliseconds; 5000 in LifeSkillConfig means 5%.</summary>
    private int OperationDuration(GameSession s, JsonElement row, bool forge)
    {
        long[] play = Numbers(row, "_iPlayParam");
        long duration = forge ? GameData.Int(row, "_iTime") : play.Length > 1 ? play[1] : 0;
        long speed = SelectedSkills(s).Where(x => x.Type == (forge ? 2 : 1)).Sum(x => x.Value);
        // (inferred) Speed bonuses shorten the cast/presentation rather than changing the clock rate.
        return checked((int)Math.Max(1, duration * Math.Max(0, 100_000 - speed) / 100_000));
    }

    /// <summary>Per-operation profession EXP, with configured percentage bonuses.</summary>
    private long OperationExp(GameSession s, JsonElement row, bool forge)
    {
        long exp = GameData.Int(row, forge ? "_iEXPgained" : "_iNeedExp");
        long bonus = SelectedSkills(s).Where(x => x.Type == (forge ? 31 : 19)).Sum(x => x.Value);
        return checked(exp + exp * bonus / 100_000);
    }

    private bool RollFreeVigor(GameSession s, JsonElement row, bool forge)
    {
        var skills = SelectedSkills(s).ToArray();
        return skills.Any(x => x.Type == (forge ? 34 : 14) && RollSkillChance(skills, x));
    }

    // _kSkillTags[2] is the crafting-chance tag targeted by type 42.
    private static bool RollSkillChance(IReadOnlyList<LifeSkill> skills, LifeSkill skill)
    {
        long chance = skill.Value;
        if (Numbers(skill.Config, "_kSkillTags").Contains(2))
            chance += skills.Where(x => x.Type == 42).Sum(x => x.Value);
        return Chance(chance);
    }

    private static long DescriptionArgument(LifeSkill skill, int index)
    {
        if (!skill.Config.TryGetProperty("_iDescription", out var description)
            || description.ValueKind != JsonValueKind.Array || description.GetArrayLength() <= index) return 0;
        return long.TryParse(description[index].ToString(), out long value) ? value : 0;
    }

    /// <summary>Ordinary restriction cap plus selected title skills; threshold/title interpretation is inferred.</summary>
    private long VigorLimit(GameSession s, DBLifeJobInfo? nextLife = null)
    {
        var life = nextLife ?? s.LifeJob;
        long limit = RestrictMax(VigorId);
        foreach (var skill in SelectedSkills(s, life))
        {
            if (skill.Type == 36) limit += skill.Value;
            else if (skill.Type is 38 or 39 or 40 or 41)
            {
                int requiredTitle = skill.Type switch { 38 => 5, 39 => 6, 40 => 4, _ => 3 };
                long professions = life!.ActiveLifejob.Values.Distinct().Count(job =>
                    life.LifeJobInfoMap.TryGetValue(job, out var info) && info.TitleInfo.JobLevel >= requiredTitle);
                limit += checked(professions * skill.Value);
            }
        }
        return limit;
    }

    private long PotionAmount(GameSession s, long baseValue)
    {
        long bonus = SelectedSkills(s).Where(x => x.Type == 45).Sum(x => x.Value);
        return checked(baseValue + baseValue * bonus / 100_000);
    }

    /// <summary>Rare byproduct rate after Nature's Bounty. 5000 is five percentage points (inferred).</summary>
    private int DropChance(GameSession s, JsonElement drop)
    {
        long chance = GameData.Int(drop, "_iDropRate");
        if (GameData.Int(drop, "_iIsMain") == 0)
            foreach (var skill in SelectedSkills(s).Where(x => x.Type == 15))
                if (skill.Extra.Contains(GameData.Int(drop, "_iQuality"))) chance += skill.Value / 10;
        return checked((int)Math.Clamp(chance, 0, 10_000));
    }

    /// <summary>Skill 44 restores a fraction of the selected cap when spending drains Vivify to zero.</summary>
    private void RecoverZeroVigor(GameSession s, DBRestrictInfo restrict)
    {
        var vigor = restrict.RestrictCount[VigorId];
        if (vigor.Count > 0) return;
        foreach (var skill in SelectedSkills(s).Where(x => x.Type == 44))
            if (Chance(skill.Value))
            {
                // Localization_en:12380100054 fixes the restore at 10% across ranks.
                vigor.Count = checked(Math.Max(1, VigorLimit(s) / 10));
                return;
            }
    }

    private LifeJobTitleSkillInfo StagedSlot(DBLifeJobInfo next, LifeSkill skill)
    {
        var title = next.LifeJobInfoMap[skill.Job].TitleInfo;
        return skill.ExtraSlot ? title.SkillExtraSkill[skill.Slot] : title.JobTitleDetail[skill.Slot];
    }

    private bool CounterReached(DBLifeJobInfo next, LifeSkill skill)
    {
        long every = Extra(skill, 0);
        if (every <= 0) return false;
        var slot = StagedSlot(next, skill);
        slot.ExtraInfo ??= new SkillExtraInfo();
        slot.ExtraInfo.Param1 = checked(slot.ExtraInfo.Param1 + 1);
        if (slot.ExtraInfo.Param1 < every) return false;
        slot.ExtraInfo.Param1 = 0;
        return true;
    }

    private HashSet<int> MainEntries(JsonElement collection)
    {
        var entries = new HashSet<int>();
        foreach (long id in Numbers(collection, "_iDropId"))
            if (_data.Row("LifeDropConfig", id) is { } drop && GameData.Int(drop, "_iIsMain") == 1)
                foreach (long entry in Numbers(drop, "_iItemId")) entries.Add(checked((int)entry));
        return entries;
    }

    private static void AddCopies(List<AwardGrant> target, IReadOnlyList<AwardGrant> source, int copies)
    {
        if (copies <= 0) return;
        foreach (var grant in source)
            target.Add(grant with { Count = checked(grant.Count * copies) });
    }

    // A gather bonus grants individual main products. The base award may be a stack of four ores,
    // so copying that award wholesale would turn a +1 bonus into +4.
    private static void AddMainUnits(List<AwardGrant> target, IReadOnlyList<AwardGrant> main, int units)
    {
        if (units <= 0 || main.Count == 0) return;
        target.Add(main[0] with { Count = units });
    }

    /// <summary>
    /// Apply selected output/material effects once per completed operation. The caller supplies only that operation's
    /// grants/costs and commits the cloned DBLifeJobInfo atomically with inventory. Output composition is inferred.
    /// </summary>
    private void ApplySkillRewards(GameSession s, DBLifeJobInfo nextLife, JsonElement row, bool forge,
        List<AwardGrant> grants, List<(int Entry, long Count)> costs,
        IReadOnlyList<AwardGrant>? mainGrants = null, int extraMain = 0)
    {
        var skills = SelectedSkills(s).ToArray();
        if (skills.Any(x => x.Type == 35))
            throw new AwardResolutionException("selected life skill requires untraced special output policy");

        if (forge)
        {
            var baseGrants = grants.ToArray();
            int outputEvents = 0;
            foreach (var skill in skills)
            {
                long category = skill.Type is 25 or 26 or 28 ? Extra(skill, 1) : Extra(skill, 0);
                bool categoryMatches = HasCategory(row, true, category);
                switch (skill.Type)
                {
                    case 25 when categoryMatches && GameData.Int(row, "_iQuality") >= Extra(skill, 0)
                        && RollSkillChance(skills, skill):
                        AddCopies(grants, baseGrants, 1); break;
                    case 26 when categoryMatches && CounterReached(nextLife, skill):
                        AddMainUnits(grants, baseGrants, 1); outputEvents++; break;
                    case 27 when categoryMatches && RollSkillChance(skills, skill):
                        AddMainUnits(grants, baseGrants, 1); break;
                    case 28 when categoryMatches && GameData.Int(row, "_iQuality") >= Extra(skill, 0)
                        && RollSkillChance(skills, skill):
                        AddMainUnits(grants, baseGrants, 1); break;
                    case 29 when costs.Count > 0 && Chance(skill.Value):
                        costs.RemoveAt(Random.Shared.Next(costs.Count)); break;
                    case 33 when RollSkillChance(skills, skill):
                        costs.Clear(); break;
                }
            }
            foreach (var skill in skills.Where(x => x.Type == 37))
                AddMainUnits(grants, baseGrants, checked((int)(skill.Value * outputEvents)));
        }
        else
        {
            var allowed = MainEntries(row);
            var main = (mainGrants ?? []).Where(g => allowed.Contains(g.Entry)).ToArray();
            // Type 16 replaces the ordinary main stack count with a 1..N roll (inferred from
            // the localized "changes the main product quantity range" wording).
            foreach (var skill in skills.Where(x => x.Type == 16))
            {
                int upper = checked((int)Extra(skill, 0));
                if (upper < 1) continue;
                for (int i = 0; i < main.Length; i++)
                {
                    int index = grants.FindIndex(g => g.Equals(main[i]));
                    if (index < 0) continue;
                    main[i] = main[i] with { Count = Random.Shared.Next(1, upper + 1) };
                    grants[index] = main[i];
                }
            }
            int copies = Math.Max(0, extraMain), outputEvents = 0;
            foreach (var skill in skills)
            {
                switch (skill.Type)
                {
                    case 9 when nextLife.LifeJobInfoMap[skill.Job].TitleInfo.JobLevel >= 5:
                        copies += checked((int)skill.Value); outputEvents++; break;
                    case 10 when GameData.Int(row, "_iQuality") >= Extra(skill, 0)
                        && HasCategory(row, false, Extra(skill, 1)):
                        copies += checked((int)skill.Value); outputEvents++; break;
                    case 11 when Energy(s) * 100 < DescriptionArgument(skill, 1) * VigorLimit(s):
                        copies += checked((int)skill.Value); outputEvents++; break;
                    case 12 when CounterReached(nextLife, skill):
                        copies++; outputEvents++; break;
                    case 13:
                        copies += checked((int)skill.Value); outputEvents++; break;
                }
            }
            copies += checked((int)(skills.Where(x => x.Type == 37).Sum(x => x.Value) * outputEvents));
            AddMainUnits(grants, main, copies);
            foreach (var skill in skills.Where(x => x.Type == 21 && RollSkillChance(skills, x)))
                grants.AddRange(_awards.Resolve(s, Extra(skill, 0), _seasons.PlayBook).Grants);
            if (skills.Any(x => x.Type == 17 && Chance(x.Value))) AddCopies(grants, grants.ToArray(), 1);
        }
        foreach (var skill in skills.Where(x => x.Type == (forge ? 32 : 20)))
            foreach (var grant in _awards.Resolve(s, Extra(skill, 0), _seasons.PlayBook).Grants)
                grants.Add(grant with { Count = checked(grant.Count * checked((int)(skill.Value * 100))) });
    }

    /// <summary>The special recipe grant still needs a traced recipe mapping.</summary>
    private bool CanActivateLifeSkill(int id) => _data.Row("LifeSkillConfig", id) is { } row
        && GameData.Int(row, "_iFuctionType") != 35;
}
