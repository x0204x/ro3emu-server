using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's SkillConfig (data/config/SkillConfig.json).</summary>
[ConfigTable("SkillConfig")]
public readonly partial record struct SkillConfig(JsonElement Row)
{
    [ConfigField("_bCancelCast")] public partial bool CancelCast { get; }
    [ConfigField("_bMovement")] public partial long Movement { get; }
    [ConfigField("_iActionTime")] public partial long ActionTime { get; }
    [ConfigField("_iActionTimeInfluence")] public partial long ActionTimeInfluence { get; }
    [ConfigField("_iActivateCondition")] public partial long ActivateCondition { get; }
    [ConfigField("_iBasicType")] public partial long BasicType { get; }
    [ConfigField("_iCD")] public partial long Cd { get; }
    [ConfigField("_iCdModeGroup")] public partial long CdModeGroup { get; }
    [ConfigField("_iChantFix")] public partial long ChantFix { get; }
    [ConfigField("_iChantVar")] public partial long ChantVar { get; }
    [ConfigField("_iDescription")] public partial JsonElement? Description { get; }
    [ConfigField("_iDistanceMax")] public partial long DistanceMax { get; }
    [ConfigField("_iDistanceMin")] public partial long DistanceMin { get; }
    [ConfigField("_iDistanceType")] public partial long DistanceType { get; }
    [ConfigField("_iDmgType")] public partial long DmgType { get; }
    [ConfigField("_iEleType")] public partial long EleType { get; }
    [ConfigField("_iExtraType")] public partial long ExtraType { get; }
    [ConfigField("_iHeightRange")] public partial long HeightRange { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iIfAutoAtk")] public partial long IfAutoAtk { get; }
    [ConfigField("_iImportantSkill")] public partial long ImportantSkill { get; }
    [ConfigField("_iInitialLevel")] public partial long InitialLevel { get; }
    [ConfigField("_iIsNormalAtk")] public partial long IsNormalAtk { get; }
    [ConfigField("_iJob")] public partial long Job { get; }
    [ConfigField("_iLevel")] public partial long Level { get; }
    [ConfigField("_iMaxLevel")] public partial long MaxLevel { get; }
    [ConfigField("_iMode")] public partial long Mode { get; }
    [ConfigField("_iName")] public partial long[] Name { get; }
    [ConfigField("_iPassBlock")] public partial long PassBlock { get; }
    [ConfigField("_iPubCD")] public partial long[] PubCD { get; }
    [ConfigField("_iPubCDGroup")] public partial long[] PubCDGroup { get; }
    [ConfigField("_iQueue")] public partial long Queue { get; }
    [ConfigField("_iRangeType")] public partial long RangeType { get; }
    [ConfigField("_iRelease")] public partial long Release { get; }
    [ConfigField("_iScriptShow")] public partial long ScriptShow { get; }
    [ConfigField("_iSkillAdvanced")] public partial long SkillAdvanced { get; }
    [ConfigField("_iSkillCri")] public partial long SkillCri { get; }
    [ConfigField("_iSkillEffectLevel")] public partial long SkillEffectLevel { get; }
    [ConfigField("_iSkillHit")] public partial long SkillHit { get; }
    [ConfigField("_iSkillID")] public partial long SkillId { get; }
    [ConfigField("_iSpecialEffect")] public partial long[] SpecialEffect { get; }
    [ConfigField("_iSystemType")] public partial long SystemType { get; }
    [ConfigField("_iTargetMax")] public partial long TargetMax { get; }
    [ConfigField("_kAttri")] public partial long[][] Attri { get; }
    [ConfigField("_kAutoLearnSkill")] public partial long[] AutoLearnSkill { get; }
    [ConfigField("_kBuff1")] public partial long[][] Buff1 { get; }
    [ConfigField("_kBuff2")] public partial long[][] Buff2 { get; }
    [ConfigField("_kCost")] public partial long[][] Cost { get; }
    [ConfigField("_kDamageParam1")] public partial long[] DamageParam1 { get; }
    [ConfigField("_kDamageParam2")] public partial long[] DamageParam2 { get; }
    [ConfigField("_kDescData")] public partial string[] DescData { get; }
    [ConfigField("_kEnergyInfo")] public partial JsonElement? EnergyInfo { get; }
    [ConfigField("_kLogicData")] public partial long[] LogicData { get; }
    [ConfigField("_kMultiverseArray")] public partial long[] MultiverseArray { get; }
    [ConfigField("_kPreSkillRequired")] public partial long[][] PreSkillRequired { get; }
    [ConfigField("_kRange")] public partial long[] Range { get; }
    [ConfigField("_kScriptLogic")] public partial string ScriptLogic { get; }
    [ConfigField("_kShowDmg")] public partial long[] ShowDmg { get; }
    [ConfigField("_kSkillCategory")] public partial long[] SkillCategory { get; }
    [ConfigField("_kSpSkillParam")] public partial long[] SpSkillParam { get; }

    /// <summary>
    /// This row as a hit whose damage only a description gives (talent procs): PATK * ratio (basis points) + flat, of an
    /// element and a target cap (0 keeps the row's). It keeps the row's id, release and show, but drops its group, tags,
    /// script, buffs and shown hit count, so no skill modifier, script effect or buff of the original applies to it.
    /// </summary>
    public JsonElement AsDescribedHit(long ratio, long flat, long element, long targetMax = 0)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Row.GetRawText())!.AsObject();
        node["_kDamageParam1"] = new System.Text.Json.Nodes.JsonArray(ratio);
        node["_kDamageParam2"] = new System.Text.Json.Nodes.JsonArray(flat);
        if (element > 0) node["_iEleType"] = element;
        if (targetMax > 0) node["_iTargetMax"] = targetMax;
        node["_iSkillID"] = 0;
        node["_kSkillCategory"] = new System.Text.Json.Nodes.JsonArray();
        node["_kScriptLogic"] = "";
        node["_kBuff1"] = new System.Text.Json.Nodes.JsonArray();
        node["_kBuff2"] = new System.Text.Json.Nodes.JsonArray();
        node["_kShowDmg"] = new System.Text.Json.Nodes.JsonArray();
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>This row with other damage ratios (basis points) and flat damage, everything else kept.</summary>
    public JsonElement WithDamage(long[] ratios, long[] flats)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Row.GetRawText())!.AsObject();
        node["_kDamageParam1"] = new System.Text.Json.Nodes.JsonArray([.. ratios.Select(v => (System.Text.Json.Nodes.JsonNode)v)]);
        node["_kDamageParam2"] = new System.Text.Json.Nodes.JsonArray([.. flats.Select(v => (System.Text.Json.Nodes.JsonNode)v)]);
        return JsonSerializer.SerializeToElement(node);
    }
}
