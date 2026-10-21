using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>Ordinary guardians: PetStarConfig._iProtectSkill/_iPassiveMain, distinct from tower _kActiveSkill.
/// Only the named petyinyue families below are executable; unknown scripts are not generic attacks.</summary>
internal static partial class GuardianPetRules
{
    private static readonly string[] GuardianCostFields = ["_kCost", "_kEnergyInfo"];

    internal static JsonObject CopyObject(JsonElement row) => JsonNode.Parse(row.GetRawText()) as JsonObject
        ?? throw new InvalidOperationException("Guardian configuration must be an object.");

    // SkillConfig._iSystemType (Lua_GameDefine.enum_SkillSystemType): ActiveGuardSkill wraps the guardian's
    // GuardianSkill effects; a server-built proc row is a PetSkill, so it never starts another wrapper or reaction chain.
    internal const int PetSkillType = 4, WrapperType = 8, EffectType = 41;
    internal const int PrimarySlot = 1; // UI_Sys_Pet_LineUp_List_CellWidgetCtrl._initSkillWidget: slot 1 Protect, others Reactive.
    // SkillTagConfig 3010-3017 ("pet protection effect triggered": output, buff, debuff, recovery, shield, control,
    // control immunity, control removal), as SkillConfig._kSkillCategory values of the guardian effects.
    internal const int OffensiveCategory = 3010, BuffCategory = 3011, DebuffCategory = 3012, RecoveryCategory = 3013,
        ShieldCategory = 3014, ControlCategory = 3015, ImmunityCategory = 3016, DispelCategory = 3017;
    internal enum Kind { Buff, Heal, Damage, Shield, Debuff, Unsupported }
    internal enum Reaction
    {
        None, BuffDuration, Heal, Slow, Defence, ShieldDuration, CastSpeed, Damage, DirectHeal, DirectDamageHeal,
        DamageCoefficient, FoxTargets, WatcherTargets, HealCooldown, DebuffDuration, ControlDuration, ImmunityDuration,
        WaterResistance, PhysicalAttackBuff, DamageBuff, DefenceBuff, KillHeal, DispelHeal,
        ControlShield, PeriodicHeal, Immunity, Endure, FairyShield, Stun, WanderSlash,
        MarionetteDebuff, NightmareDebuff, IsisReduction, DaedreDebuff, PiametteTargets,
        SuccubusSilence, ChimeraDebuff, WildcatShield, SoheeReduction, ShinobiDispel, IncubusAura,
        AliotHealingWindow, DullahanWindow, LolaWindow, ElenaBurst,
        MiguelCritical, RetributionControl,
        RainbowExtraHit, SarahExtraHit, MummyCurse, MedusaPetrify, NightmareControl, SilverVulnerability, ScoutDetection, OwlLightning, CatVulnerability,
    }
    internal sealed record Skill(int Slot, int PetId, int Star, int Stage, int Plan,
        JsonElement Active, JsonElement Effect, JsonElement? Passive, Kind Kind)
    {
        public int Id => (int)GameData.Int(Active, "_iID");
        public int EffectId => (int)GameData.Int(Effect, "_iID");
        public bool IsPrimary => Slot == PrimarySlot;
        public JsonElement? Core { get; init; }
        public Reaction Reactive => Reactions.GetValueOrDefault(Script(Effect));
    }

    /// <summary>The reactive behaviour of each guardian family, by the _kScriptLogic of its GuardianSkill effect.</summary>
    private static readonly Dictionary<string, Reaction> Reactions = new()
    {
        ["PetSkill_2_2_QiaoKeHou"] = Reaction.BuffDuration,
        ["YaoGun"] = Reaction.Heal,
        ["Pet_WildRose_23552"] = Reaction.Slow,
        ["BoLiPaoPao"] = Reaction.Defence,
        ["yemanhudun"] = Reaction.ShieldDuration,
        ["SenLinCiFu"] = Reaction.CastSpeed,
        ["Pet_Salamander_23462"] = Reaction.Damage,
        ["Alice_Huzhu"] = Reaction.DirectHeal,
        ["xuesefengbao"] = Reaction.DirectDamageHeal,
        ["PetSkill_2_2_ShouRenZhanShiZhang"] = Reaction.DamageCoefficient,
        ["QingFeng"] = Reaction.FoxTargets,
        ["Observation_Huzhu"] = Reaction.WatcherTargets,
        ["ShengGuangJiangLin"] = Reaction.HealCooldown,
        ["KuangFengXiJuan"] = Reaction.DebuffDuration,
        ["XieXinLianRen"] = Reaction.DebuffDuration,
        ["GarmBaby_Huzhu"] = Reaction.ControlDuration,
        ["PetSkill_2_2_GuZhongMo"] = Reaction.ImmunityDuration,
        ["ChaoXiSongGe"] = Reaction.WaterResistance,
        ["AiLiSe_HuZhu"] = Reaction.PhysicalAttackBuff,
        ["yemujuechang"] = Reaction.DamageBuff,
        ["PetSkill_2_2_QiWangNvShen"] = Reaction.DefenceBuff,
        ["PetSkill_2_2_YaoDao"] = Reaction.KillHeal,
        ["Siwanliandao"] = Reaction.DispelHeal,
        ["Pet_AncientTree_23452"] = Reaction.ControlShield,
        ["GuoShuGui_HuZhu"] = Reaction.PeriodicHeal,
        ["Pet_Hodremlin_23052"] = Reaction.Immunity,
        ["MiYaBi_Huzhu"] = Reaction.Endure,
        ["YaoXianNv_HuZhu"] = Reaction.FairyShield,
        ["PetSkill_2_2_BuChongJin"] = Reaction.Stun,
        ["Pet_Wanderman_23172"] = Reaction.WanderSlash,
        ["Pet_Marionette_23392"] = Reaction.MarionetteDebuff,
        ["Pet_Tikbalang_23212"] = Reaction.NightmareDebuff,
        ["SerpentGuard"] = Reaction.IsisReduction,
        ["MingYingLieXi"] = Reaction.DaedreDebuff,
        ["tutujianglin"] = Reaction.PiametteTargets,
        ["XieEJiQu2"] = Reaction.SuccubusSilence,
        ["shengguanglingyu"] = Reaction.ChimeraDebuff,
        ["xianzhenchongfeng"] = Reaction.WildcatShield,
        ["GuiNv_HuZhu"] = Reaction.SoheeReduction,
        ["renfamizong"] = Reaction.ShinobiDispel,
        ["moqihuanrao"] = Reaction.IncubusAura,
        ["JingZhunShouShu"] = Reaction.AliotHealingWindow,
        ["WuShiZhiNu"] = Reaction.DullahanWindow,
        ["NvZhanShiLuoLa_HuZhu"] = Reaction.LolaWindow,
        ["Pet_Bolkoba_23292"] = Reaction.ElenaBurst,
        ["huxiaolieduan"] = Reaction.MiguelCritical,
        ["xingxingshike"] = Reaction.RetributionControl,
        ["Deleter_Huzhu"] = Reaction.RainbowExtraHit,
        ["Pet_MmSarah_23342"] = Reaction.SarahExtraHit,
        ["Pet_AncientMummy_23122"] = Reaction.MummyCurse,
        ["PetrifyingGaze"] = Reaction.MedusaPetrify,
        ["Pet_NightmareTerror_23302"] = Reaction.NightmareControl,
        ["BlueprintYinYueJiangLin2"] = Reaction.SilverVulnerability,
        ["BaXiLiSiKe_HuZhu"] = Reaction.ScoutDetection,
        ["PetSkill_2_2_EXiaoNanJue"] = Reaction.OwlLightning,
        ["PetSkill_2_2_KuangBaoYueYeMao"] = Reaction.CatVulnerability,
    };

    internal static string Script(JsonElement row) => row.TryGetProperty("_kScriptLogic", out var s) ? s.GetString() ?? "" : "";
    internal static double[] Numbers(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(n => GameData.Number(n) ?? 0)] : [];
    internal static bool HasCost(JsonElement row) => GuardianCostFields.Any(key =>
        row.TryGetProperty(key, out var value) && value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => true,
        });

    /// <summary>Explicit auto-learned child whose group is in _kLogicData. No single-child fallback for opaque wrappers.</summary>
    internal static JsonElement? Child(GameData data, JsonElement active)
    {
        if (GameData.Int(active, "_iSystemType") != WrapperType || !KnownWrapper(Script(active))) return null;
        var groups = Numbers(active, "_kLogicData");
        var children = Numbers(active, "_kAutoLearnSkill").Select(id => data.Row("SkillConfig", (long)id))
            .Where(r => r is { } row && GameData.Int(row, "_iSystemType") == EffectType
                && (groups.Contains(GameData.Int(row, "_iSkillID"))
                    || Script(active) == "GuiNv_Enter" && Script(row) == "GuiNv_HuZhu")).ToArray();
        return children.Length == 1 ? children[0] : null;
    }

    internal static Kind? Classify(JsonElement effect) => Script(effect) switch
    {
        "PetSkill_2_2_QiaoKeHou" or "Pet_Tikbalang_23212" or "MiYaBi_Huzhu" or "moqihuanrao" or "shengguanglingyu"
            or "tutujianglin" or "BaXiLiSiKe_HuZhu" or "renfamizong" => Kind.Buff,
        "YaoGun" or "SenLinCiFu" or "ChaoXiSongGe" or "Alice_Huzhu" or "SerpentGuard" or "ShengGuangJiangLin" => Kind.Heal,
        "Pet_WildRose_23552" or "Pet_Salamander_23462" or "AiLiSe_HuZhu"
            or "Observation_Huzhu" or "Pet_Wanderman_23172" or "PetSkill_2_2_QiWangNvShen"
            or "PetSkill_2_2_BuChongJin" or "Pet_AncientTree_23452" or "Pet_Hodremlin_23052"
            or "XieEJiQu2" or "QingFeng" or "MingYingLieXi" or "Pet_NightmareTerror_23302"
            or "PetSkill_2_2_ShouRenZhanShiZhang" or "Deleter_Huzhu" or "GarmBaby_Huzhu"
            or "PetSkill_2_2_YaoDao" or "huxiaolieduan" or "yemujuechang" or "GuoShuGui_HuZhu"
            or "xianzhenchongfeng" or "KuangFengXiJuan" or "WuShiZhiNu" or "NvZhanShiLuoLa_HuZhu"
            or "BlueprintYinYueJiangLin2" or "PetrifyingGaze" or "xingxingshike" or "JingZhunShouShu"
            or "Siwanliandao" or "Pet_Bolkoba_23292" or "XieXinLianRen" or "xuesefengbao"
            or "PetSkill_2_2_EXiaoNanJue" or "Pet_MmSarah_23342" or "Pet_AncientMummy_23122"
            or "PetSkill_2_2_KuangBaoYueYeMao" => Kind.Damage,
        "BoLiPaoPao" or "yemanhudun" or "GuiNv_HuZhu" or "YaoXianNv_HuZhu" => Kind.Shield,
        "Pet_Marionette_23392" or "PetSkill_2_2_GuZhongMo" => Kind.Debuff,
        _ => null,
    };

    internal static JsonElement TargetRow(Skill skill)
    {
        var row = GuardianPetRules.CopyObject(skill.Active);
        if (skill.Kind == Kind.Damage && GameData.Int(skill.Effect, "_iRangeType") == SkillRelease.SingleTarget)
            row["_iRelease"] = SkillRelease.Lock; // (inferred) Owl locks a target despite the wrapper's FixCircle marker.
        return JsonSerializer.SerializeToElement(row);
    }

    // SkillConfig wrappers with an explicit group in _kLogicData. GuiNv_Enter is the named exception:
    // its sole auto-learned GuiNv_HuZhu child is corroborated by the shield description (inferred link).
    private static bool KnownWrapper(string script) => script is "petyinyue" or "Deleter_Huzhu1" or "Alice_Huzhu1"
        or "Pet_HuXian" or "Pet_XiaoBaFengTe" or "GarmBaby_Huzhu1" or "BaXiLiSiKe_HuZhu2" or "GuoShuGui_HuZhu2"
        or "Pet_MmSarah_AimLock" or "Pet_AimLock" or "MingYingLieXi2" or "QiWangNvShen_Huzhu1"
        or "NvZhanShiLuoLa_HuZhu2" or "Observation_Huzhu1" or "Pet_SiWangNvWu" or "GuiNv_Enter"
        or "petmove" or "MiYaBi_Huzhu1";

    /// <summary>SkillConfig/Localization_en name adaptive ATK. (inferred) Greater PATK/MATK wins; ties use physical.
    /// Apply one effect, not one effect per duplicate animation point. Support is executed separately.</summary>
    internal static JsonElement DamageRow(Skill skill, StatSheet stats)
    {
        var node = JsonNode.Parse(skill.Effect.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException("Guardian effect must be a configuration object.");
        node["_iDmgType"] = stats[AttributeId.Matk] > stats[AttributeId.Atk] ? 2 : 1;
        if (Script(skill.Effect) is "Pet_AncientMummy_23122" or "Pet_Bolkoba_23292")
            node["_iRelease"] = GameData.Int(skill.Active, "_iRelease"); // explicit targeted wrapper center
        node["_kBuff1"] = new JsonArray(); // Applied once by GuardianSupportAsync, independently of hostile selection.
        if (skill.Kind == Kind.Debuff)
        {
            node["_kDamageParam1"] = new JsonArray(); // Marionette's unused 500% placeholder is not an attack.
            node["_kDamageParam2"] = new JsonArray();
        }
        return JsonSerializer.SerializeToElement(node);
    }

    // BuffConfig._iID of the first level of the SpecialStatusConfig control groups (BuffService.StateFor).
    internal const int StunBuff = 901601, KnockUpBuff = 901801, PetrifyBuff = 902101, RootBuff = 902401, BlindBuff = 902501,
        EndureBuff = 903101, ControlImmunityBuff = 903201;
    internal static bool DispelsControl(Skill skill) => Script(skill.Effect) is "PetSkill_2_2_GuZhongMo" or "XieEJiQu2" or "Siwanliandao" or "renfamizong";

    internal static double Description(Skill skill, int index) => skill.Passive is { } row
        ? Numbers(row, "_kDescData").ElementAtOrDefault(index) : 0;
    internal static double CoreDescription(Skill skill, int index) => skill.Core is { } row
        ? Numbers(row, "_kDescData").ElementAtOrDefault(index) : 0;

    // Localization's @{n} arguments are literal numbers in _iDescription, distinct from ${n} in _kDescData.
    internal static double[] DescriptionLiterals(JsonElement row) => row.TryGetProperty("_iDescription", out var values)
        && values.ValueKind == JsonValueKind.Array ? [.. values.EnumerateArray().Skip(1)
            .Where(v => v.ValueKind == JsonValueKind.String && GameData.Number(v).HasValue)
            .Select(v => GameData.Number(v) ?? 0)] : [];
    internal static bool Category(JsonElement row, int category) => Numbers(row, "_kSkillCategory").Contains(category);
    internal static double BuffMultiplier(JsonElement row) => row.TryGetProperty("_ro3GuardianBuffMultiplier", out var value)
        ? Math.Clamp(GameData.Number(value) ?? 1, 0, MaxBuffMultiplier) : 1;

    // (inferred) Bounds on description-driven repeats and multipliers. They are not client rules: they only stop a
    // malformed description from scheduling unbounded work.
    internal const int MaxRepeats = 50, MaxCoreHits = 20, MaxAuraPulses = 100;
    internal const double MaxBuffMultiplier = 10;

    internal static int BuffAtStar(GameData data, int group, int star)
    {
        var candidates = GuardianTables.Of(data).Buffs(group, star);
        return candidates.Count == 1 ? (int)GameData.Int(candidates[0], "_iID") : 0;
    }

    /// <summary>(inferred) Named-family candidate links, validated against the entire advertised attribute payload.
    /// This is not a generic same-ID SkillConfig/BuffConfig join; the Fairy counterexample must never use it.</summary>
    internal static int ReactiveBuff(GameData data, Skill skill)
    {
        int candidate = 0;
        (long Id, double Value)[] expected;
        if (skill.Reactive == Reaction.Slow)
        {
            candidate = skill.Id;
            expected = [(AttributeId.PerSpeed, -Description(skill, 0) * 100)];
        }
        else if (skill.Reactive == Reaction.Defence && skill.Passive is { } passive
            && data.Row("BuffConfig", GameData.Int(passive, "_iID")) is { } controller)
        {
            var logic = Numbers(controller, "_kLogicData");
            if (logic.Length < 3 || logic[0] != Description(skill, 0)) return 0;
            candidate = (int)logic[1];
            expected = [(AttributeId.BuffPerDef, Description(skill, 1) * 100), (AttributeId.BuffPerMdef, Description(skill, 1) * 100)];
        }
        else if (ReactionAttributeBuff(data, skill) is { } attributes)
        {
            candidate = attributes.Id;
            expected = attributes.Changes;
        }
        else return 0;
        if (candidate <= 0 || data.Row("BuffConfig", candidate) is not { } buff) return 0;
        var changes = GameData.Pairs(buff, "_kChangeAttri").OrderBy(p => p.Id).ToArray();
        var ordered = expected.OrderBy(p => p.Id).ToArray();
        return expected.All(p => p.Value != 0) && changes.Length == ordered.Length
            && changes.Zip(ordered).All(pair => pair.First.Id == pair.Second.Id
                && Math.Abs(pair.First.Value - pair.Second.Value) < 0.000001) ? candidate : 0;
    }

    private static (int Id, (long Id, double Value)[] Changes)? ReactionAttributeBuff(GameData data, Skill skill)
    {
        if (skill.Passive is not { } passive) return null;
        int id = (int)GameData.Int(passive, "_iID");
        var controller = data.Row("BuffConfig", id);
        var logic = controller is { } c ? Numbers(c, "_kLogicData") : [];
        return skill.Reactive switch
        {
            Reaction.CastSpeed when logic.Length == 2 && logic[1] == Description(skill, 0) * 1000
                => ((int)logic[0], [(AttributeId.CastSpeed, Description(skill, 1))]),
            Reaction.PhysicalAttackBuff when logic.Length == 1
                => ((int)logic[0], [(AttributeId.BuffPerAtk, Description(skill, 0) * 100)]),
            Reaction.WaterResistance => (id, [(AttributeId.BeWaterEle, Description(skill, 0) * 100)]),
            Reaction.DamageBuff => (LinkedDescriptionBuff(data, skill.Core ?? passive, skill.Star),
                [(AttributeId.PerDmg1, (skill.Core != null ? CoreDescription(skill, 0) : Description(skill, 0)) * 100)]),
            Reaction.DefenceBuff when logic.Length == 2 => ((int)logic[1],
                [(AttributeId.BuffPerDef, Description(skill, 0) * 100), (AttributeId.BuffPerMdef, Description(skill, 0) * 100)]),
            Reaction.MarionetteDebuff => (BuffAtStar(data, GuardianGroup.MarionetteDebuff, skill.Star),
                [(AttributeId.CastSpeed, -Description(skill, 0)), (AttributeId.PerSpDedution, Description(skill, 1) * 100)]),
            Reaction.NightmareDebuff => (BuffAtStar(data, GuardianGroup.NightmareDebuff, skill.Star),
                [(AttributeId.PerSpeed, -Description(skill, 3) * 100), (AttributeId.BuffPerAtk, -Description(skill, 2) * 100),
                    (AttributeId.BuffPerMatk, -Description(skill, 2) * 100)]),
            Reaction.IsisReduction when logic.Length == 1 => ((int)logic[0], [(AttributeId.PerResDmg, Description(skill, 3) * 100)]),
            Reaction.DaedreDebuff => (BuffAtStar(data, GuardianGroup.DaedreDebuff, skill.Star),
                Enumerable.Range(AttributeId.BeNoneEle, AttributeId.ElementCount).Select(id => ((long)id, -Description(skill, 4) * 100))
                    .Prepend(((long)AttributeId.BuffPerSpeed, -Description(skill, 5) * 100)).ToArray()),
            Reaction.SilverVulnerability => (BuffAtStar(data, GuardianGroup.SilverVulnerability, skill.Star),
                [(AttributeId.PerBeRemoteDmg1, -Description(skill, 1) * 100)]),
            _ => null,
        };
    }

    internal static int LinkedDescriptionBuff(GameData data, JsonElement row, int level)
    {
        if (!row.TryGetProperty("_iDescription", out var values) || values.ValueKind != JsonValueKind.Array) return 0;
        foreach (var value in values.EnumerateArray())
        {
            string text = value.ToString();
            const string prefix = "<link=\"buff/";
            if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith("\">", StringComparison.Ordinal)
                || !int.TryParse(text.AsSpan(prefix.Length, text.Length - prefix.Length - 2), out int reference)
                || data.Row("BuffConfig", reference) is not { } named) continue;
            long group = GameData.Int(named, "_iBuffID");
            var candidates = GuardianTables.Of(data).Buffs(group, level);
            if (candidates.Count == 1) return (int)GameData.Int(candidates[0], "_iID");
        }
        return 0;
    }
}
