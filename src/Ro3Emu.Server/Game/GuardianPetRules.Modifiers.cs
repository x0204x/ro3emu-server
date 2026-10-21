using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

internal static partial class GuardianPetRules
{
    private const string DamageFactor = "_ro3GuardianDamageFactor", HealFactor = "_ro3GuardianHealFactor";
    // PBSkillAffectValueType of the _kSkillAttributeList tuples read here, and SkillParameterRules' tag scope and operations.
    private const int CdFixedType = 0, TargetMaxType = 27, DamageParam1Type = 29;
    private const int TagScope = 1, AddOperation = 1, BasisPointOperation = 5;
    // SkillTagConfig 3001-3006 ("main combat": buff, debuff, recovery, shield, control, control immunity), as the fourth
    // value of a _kBuff1/_kBuff2 entry.
    private const int BuffTag = 3001, DebuffTag = 3002, ShieldTag = 3004, ControlTag = 3005, ImmunityTag = 3006;

    /// <summary>Client descriptions select guardian categories, never profession skills or the pet's own utility.
    /// (inferred) Contributions add within each multiplier, then multiply damage/healing once; coefficient bonuses
    /// add percentage points before defence. Tagged duration additions affect only the named Buff1/Buff2 entries.</summary>
    internal static Skill Prepare(GameData data, Skill skill, IEnumerable<Skill> guardians)
    {
        var utility = guardians.Where(g => !g.IsPrimary && g.Passive != null).ToArray();
        var effect = GuardianPetRules.CopyObject(skill.Effect);
        var active = GuardianPetRules.CopyObject(skill.Active);
        ApplyScalarModifiers(data, skill, utility, effect, active);
        var (damage, healing) = Multipliers(data, skill, utility, effect);
        effect[DamageFactor] = damage;
        effect[HealFactor] = healing;
        ApplyReactionFields(skill, utility, effect);
        AdjustDurations(skill.Effect, utility, effect, "_kBuff1");
        AdjustDurations(skill.Effect, utility, effect, "_kBuff2");
        return skill with { Active = JsonSerializer.SerializeToElement(active), Effect = JsonSerializer.SerializeToElement(effect) };
    }

    private static (double Damage, double Healing) Multipliers(GameData data, Skill skill, Skill[] utility, JsonObject effect)
    {
        bool offense = Category(skill.Effect, OffensiveCategory), recovery = Category(skill.Effect, RecoveryCategory);
        double damage = 1, healing = 1;
        foreach (var passive in utility)
        {
            double percent = Description(passive, 0) / 100;
            if (offense && passive.Reactive is Reaction.Damage or Reaction.DirectDamageHeal) damage += percent;
            if (recovery && passive.Reactive is Reaction.DirectHeal or Reaction.DirectDamageHeal) healing += percent;
            if ((offense || recovery) && passive.Reactive == Reaction.PiametteTargets)
            {
                double ratio = PiametteModifiers(data, skill, passive, effect);
                if (offense) damage += ratio;
                if (recovery) healing += ratio;
            }
            if (offense && passive.Reactive == Reaction.DamageCoefficient && effect["_kDamageParam1"] is JsonArray coefficients)
                for (int i = 0; i < coefficients.Count; i++)
                    coefficients[i] = (GameData.Number(skill.Effect.GetProperty("_kDamageParam1")[i]) ?? 0) + Description(passive, 0) * 100;
        }
        return (damage, healing);
    }

    /// <summary>The passive's same-ID BuffConfig belongs to a different skill. The named Piamette group carries explicit
    /// target and coefficient modifiers, including the sixth star's missing description value: an area skill gains the
    /// added targets, and the (negative) damage-ratio change is returned as a multiplier delta.</summary>
    private static double PiametteModifiers(GameData data, Skill skill, Skill passive, JsonObject effect)
    {
        if (data.Row("BuffConfig", BuffAtStar(data, GuardianGroup.PiametteModifier, passive.Star)) is not { } buff
            || !buff.TryGetProperty("_kSkillAttributeList", out var list) || list.ValueKind != JsonValueKind.Array) return 0;
        var mods = list.EnumerateArray().Select(SkillParameterRules.Parse).OfType<SkillParameterRules.Modifier>().ToArray();
        if (GameData.Int(skill.Effect, "_iRangeType") == SkillRelease.Area)
            effect["_iTargetMax"] = (int)GameData.Int(JsonSerializer.SerializeToElement(effect), "_iTargetMax")
                + (int)mods.Where(m => m.Type == TargetMaxType && m.Operation == AddOperation).Select(m => m.Value).DefaultIfEmpty(0).Max();
        var ratio = mods.FirstOrDefault(m => m.Type == DamageParam1Type && m.Operation == BasisPointOperation);
        return ratio.Value < 0 ? ratio.Value / 10000.0 : 0;
    }

    /// <summary>Miguel's critical chance and HP threshold on offensive skills, Retribution's player control time on
    /// control skills; CombatService reads these fields when the effect lands.</summary>
    private static void ApplyReactionFields(Skill skill, Skill[] utility, JsonObject effect)
    {
        if (Category(skill.Effect, OffensiveCategory))
        {
            effect["_ro3GuardianCritChance"] = utility.Where(p => p.Reactive == Reaction.MiguelCritical).Sum(p => Description(p, 0) / 100);
            effect["_ro3GuardianCritHpThreshold"] = utility.Where(p => p.Reactive == Reaction.MiguelCritical)
                .Select(p => Description(p, 1)).DefaultIfEmpty(0).Max();
        }
        if (Category(skill.Effect, ControlCategory))
            effect["_ro3GuardianPlayerControlMs"] = (int)utility.Where(p => p.Reactive == Reaction.RetributionControl)
                .Sum(p => Description(p, 0) * 1000);
    }

    private static void ApplyScalarModifiers(GameData data, Skill skill, Skill[] utility, JsonObject effect, JsonObject active)
    {
        foreach (var passive in utility.Where(g => g.Reactive is Reaction.FoxTargets or Reaction.WatcherTargets or Reaction.HealCooldown))
        {
            // Family-specific candidate: these rows contain only explicit category/field modifiers, independently
            // matching their localized description. No other same-ID buff is admitted by this resolver.
            if (passive.Passive is not { } row || data.Row("BuffConfig", GameData.Int(row, "_iID")) is not { } buff
                || !buff.TryGetProperty("_kSkillAttributeList", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            var mods = list.EnumerateArray().Select(SkillParameterRules.Parse).OfType<SkillParameterRules.Modifier>()
                .Where(m => m.Scope == TagScope && m.Type is CdFixedType or TargetMaxType or DamageParam1Type
                    && m.Operation is AddOperation or BasisPointOperation).ToArray();
            var applicable = mods.Where(m => m.Type != TargetMaxType || GameData.Int(skill.Effect, "_iRangeType") == SkillRelease.Area).ToArray();
            // Fox has one target tuple for each category: a dual-category skill still gets one advertised addition.
            if (passive.Reactive == Reaction.FoxTargets)
                applicable = [.. applicable.Where(m => m.Type != TargetMaxType).Concat(applicable.Where(m => m.Type == TargetMaxType
                    && SkillParameterRules.Matches(m, (int)GameData.Int(skill.Effect, "_iSkillID"),
                        [.. Numbers(skill.Effect, "_kSkillCategory").Select(n => (int)n)])).Take(1))];
            effect = Replace(effect, SkillParameterRules.Effective(JsonSerializer.SerializeToElement(effect), applicable,
                [.. Numbers(skill.Effect, "_kSkillCategory").Select(n => (int)n)]));
            active = Replace(active, SkillParameterRules.Effective(JsonSerializer.SerializeToElement(active), [.. mods.Where(m => m.Type == CdFixedType)],
                [.. Numbers(skill.Active, "_kSkillCategory").Select(n => (int)n)]));
        }
        active["_iCD"] = Math.Max(1, GameData.Int(JsonSerializer.SerializeToElement(active), "_iCD"));
    }

    private static JsonObject Replace(JsonObject target, JsonElement row)
    {
        target.Clear();
        foreach (var property in row.EnumerateObject()) target[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return target;
    }

    private static void AdjustDurations(JsonElement source, Skill[] utility, JsonObject effect, string field)
    {
        if (effect[field] is not JsonArray tuples) return;
        foreach (var item in tuples)
        {
            if (item is not JsonArray tuple || tuple.Count < 4 || tuple[3] is not { } tagNode) continue;
            int tag = tagNode.GetValue<int>();
            double addition = utility.Where(p => p.Reactive switch
            {
                Reaction.BuffDuration => field == "_kBuff1" && tag == BuffTag && Category(source, BuffCategory),
                Reaction.ShieldDuration => field == "_kBuff1" && tag == ShieldTag && Category(source, ShieldCategory),
                Reaction.ImmunityDuration => field == "_kBuff1" && tag == ImmunityTag && Category(source, ImmunityCategory),
                Reaction.DebuffDuration => field == "_kBuff2" && tag == DebuffTag && Category(source, DebuffCategory),
                Reaction.ControlDuration => field == "_kBuff2" && tag == ControlTag && Category(source, ControlCategory),
                _ => false,
            }).Sum(p => Description(p, 0) * 1000);
            if (addition > 0 && tuple[2] is { } duration)
                tuple[2] = (int)Math.Clamp(duration.GetValue<int>() + addition, 1, int.MaxValue);
        }
    }

    internal static long DirectDamage(JsonElement row, long amount) => Scale(row, DamageFactor, amount);
    internal static double CriticalChance(JsonElement row) => row.TryGetProperty("_ro3GuardianCritChance", out var value) ? GameData.Number(value) ?? 0 : 0;
    internal static bool CriticalAtHp(JsonElement row, long hp, long maximum) => maximum > 0
        && row.TryGetProperty("_ro3GuardianCritHpThreshold", out var value) && hp < maximum * (GameData.Number(value) ?? 0) / 100;

    internal static JsonElement PlayerBuffTuple(JsonElement row, JsonElement tuple)
    {
        if (tuple.GetArrayLength() < 4 || GameData.Number(tuple[3]) != ControlTag
            || !row.TryGetProperty("_ro3GuardianPlayerControlMs", out var value) || value.GetInt32() <= 0) return tuple;
        var node = (JsonNode.Parse(tuple.GetRawText()) as JsonArray ?? throw new InvalidOperationException("Guardian buff entry must be an array."));
        node[2] = (int)(GameData.Number(tuple[2]) ?? 0) + value.GetInt32();
        return JsonSerializer.SerializeToElement(node);
    }
    internal static long DirectHealing(JsonElement row, long amount) => Scale(row, HealFactor, amount);
    private static long Scale(JsonElement row, string field, long amount) => amount <= 0 ? 0
        : row.TryGetProperty(field, out var value) && GameData.Number(value) is { } factor
            ? (long)Math.Clamp(Math.Floor(amount * factor), 0, long.MaxValue) : amount;

    internal static bool OffensiveOrControl(JsonElement row) => Category(row, OffensiveCategory) || Category(row, ControlCategory);

    internal static bool ReactionTriggered(Reaction reaction, JsonElement row) => reaction switch
    {
        Reaction.Heal or Reaction.WaterResistance => Category(row, RecoveryCategory),
        Reaction.DispelHeal => Category(row, DispelCategory),
        Reaction.CastSpeed => Category(row, OffensiveCategory) || Category(row, RecoveryCategory),
        Reaction.PhysicalAttackBuff => Category(row, OffensiveCategory) || Category(row, DebuffCategory),
        Reaction.Defence => Category(row, ShieldCategory),
        Reaction.DamageBuff or Reaction.DefenceBuff => true,
        Reaction.ControlShield => Category(row, ControlCategory),
        Reaction.PeriodicHeal or Reaction.Immunity => Category(row, ShieldCategory) || Category(row, BuffCategory),
        Reaction.Endure => Category(row, ShieldCategory) || Category(row, ImmunityCategory),
        Reaction.FairyShield or Reaction.WanderSlash => true,
        Reaction.NightmareDebuff => Category(row, RecoveryCategory) || Category(row, BuffCategory),
        Reaction.IsisReduction or Reaction.DaedreDebuff or Reaction.IncubusAura => Category(row, BuffCategory),
        Reaction.SuccubusSilence => Category(row, DispelCategory) || Category(row, ImmunityCategory),
        Reaction.ChimeraDebuff => Category(row, ShieldCategory) || Category(row, ImmunityCategory),
        Reaction.WildcatShield or Reaction.SoheeReduction => Category(row, ShieldCategory) || Category(row, DispelCategory),
        Reaction.ShinobiDispel => Category(row, OffensiveCategory) || Category(row, RecoveryCategory),
        Reaction.AliotHealingWindow => Category(row, RecoveryCategory) || Category(row, DispelCategory),
        Reaction.LolaWindow or Reaction.DullahanWindow => Category(row, DebuffCategory) || Category(row, ControlCategory),
        Reaction.ElenaBurst => Category(row, ShieldCategory),
        Reaction.ScoutDetection or Reaction.OwlLightning => Category(row, OffensiveCategory) || Category(row, BuffCategory),
        Reaction.CatVulnerability => Category(row, BuffCategory),
        _ => false,
    };
}
