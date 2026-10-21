namespace Ro3Emu.Server.Game;

/// <summary>BuffConfig._iBuffID and SkillConfig._iSkillID groups that guardian scripts name. Each group is resolved at
/// the pet's star with <see cref="GuardianPetRules.BuffAtStar"/> or <see cref="GuardianTables.Skills"/>; the comment
/// names the SkillConfig._kScriptLogic (or the reaction) that uses it.</summary>
internal static class GuardianGroup
{
    // BuffConfig._iBuffID
    internal const int WildcatCharge = 23014; // KuangBaoYueYeMao: damage charges spent by the owner's hits
    internal const int CatVulnerability = 23019; // Reaction.CatVulnerability: removed by the next damage taken
    internal const int ShinobiStealth = 23022; // renfamizong
    internal const int YinYueJiangLin = 23033; // BlueprintYinYueJiangLin2: self buff on the owner's hits
    internal const int SilverVulnerability = 23035; // Reaction.SilverVulnerability
    internal const int SoheeReduction = 23134; // Reaction.SoheeReduction
    internal const int FairyReactiveShield = 23186; // Reaction.FairyShield
    internal const int DaedreDebuff = 23194; // Reaction.DaedreDebuff
    internal const int NightmareDebuff = 23212; // Reaction.NightmareDebuff
    internal const int PiametteModifier = 23322; // Reaction.PiametteTargets
    internal const int XingXingShiKeDebuff = 23362; // xingxingshike: debuff on the owner's hits
    internal const int MarionetteDebuff = 23392; // Reaction.MarionetteDebuff
    internal const int BasiliskDefence = 23412; // BaXiLiSiKe_HuZhu: defence on the owner's hits
    internal const int ScoutDetection = 23414; // Reaction.ScoutDetection

    // SkillConfig._iSkillID
    internal const int OwlLightning = 23115; // PetSkill_2_3_EXiaoNanJue
    internal const int MummyKillCurse = 23126; // Pet_AncientMummy_23126: the primary window's curse on a kill
    internal const int MummyHitCurse = 23127; // Pet_AncientMummy_23127: Reaction.MummyCurse
    internal const int LolaPrimaryHit = 23266; // NvZhanShiLuoLa_ChuFa from the primary window
    internal const int LolaReactiveHit = 23267; // NvZhanShiLuoLa_ChuFa from Reaction.LolaWindow
    internal const int DullahanPrimaryHit = 23275; // WuLuLieShang from the primary window
    internal const int DullahanReactiveHit = 23276; // WuLuLieShang from Reaction.DullahanWindow
    internal const int SarahPrimaryHit = 23346; // Pet_MmSarah_23346 from the primary window
    internal const int SarahFist = 23347; // Pet_MmSarah_23346 from Reaction.SarahExtraHit
    internal const int RainbowFire = 23375; // Deleter_Passive: Reaction.RainbowExtraHit
}
