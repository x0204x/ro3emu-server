namespace Ro3Emu.Server.Game;

/// <summary>AttributeConfig._iID values the guardian and monster combat rules read, named after the row's
/// _kVariable (shown on each constant). Lua_GameDefine.enum_unitAttrMap uses the same ids.</summary>
internal static class AttributeId
{
    internal const int MaxHp = 101; // maxhp
    internal const int Atk = 103; // atk
    internal const int Matk = 104; // matk
    internal const int Def = 105; // def
    internal const int Mdef = 106; // mdef
    internal const int Hit = 109; // hit
    internal const int Flee = 110; // flee
    internal const int Cri = 111; // cri
    internal const int ResCri = 112; // rescri
    internal const int Speed = 113; // speed
    internal const int CastSpeed = 116; // cspd
    internal const int VarChant = 201; // varchant
    internal const int PerVarChant = 202; // per_varchant
    internal const int FixChant = 203; // fixchant
    internal const int PerFixChant = 204; // per_fixchant
    internal const int PerTreat = 207; // per_treat
    internal const int PerCure = 208; // per_cure
    internal const int PerResDmg = 216; // per_res_dmg
    internal const int NoneEle = 301; // none_ele, the first of the ten element bonuses
    internal const int BeNoneEle = 311; // be_none_ele, the first of the ten element resistances
    internal const int BeWaterEle = 315; // be_water_ele
    internal const int ElementCount = 10; // none_ele..undead_ele and be_none_ele..be_undead_ele
    internal const int BuffPerAtk = 1032; // buff_per_atk
    internal const int BuffPerMatk = 1042; // buff_per_matk
    internal const int BuffPerDef = 1052; // buff_per_def
    internal const int BuffPerMdef = 1062; // buff_per_mdef
    internal const int PerSpeed = 1131; // per_speed
    internal const int BuffPerSpeed = 1132; // buff_per_speed
    internal const int PerTreat1 = 1207; // per_treat1
    internal const int PerCure1 = 1208; // per_cure1
    internal const int PerResAtkDmg1 = 1212; // per_resatk_dmg1
    internal const int PerResMatkDmg1 = 1214; // per_resmatk_dmg1
    internal const int PerDmg1 = 1215; // per_dmg1
    internal const int PerResDmg1 = 1216; // per_res_dmg1
    internal const int PerBeRemoteDmg1 = 1230; // per_beremote_dmg1
    internal const int SpDedution = 1231; // sp_dedution
    internal const int PerSpDedution = 1232; // per_sp_dedution
    internal const int Hp = 2000; // hp
    internal const int Sp = 2001; // sp
}
