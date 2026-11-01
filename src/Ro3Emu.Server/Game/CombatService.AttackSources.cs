using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>傷害種類決定物防／魔防及護盾，不等於傷害來源屬性。
    /// SkillConfig12201/1220111＋繁中10110301152：MATK造成物理傷害。
    /// SkillConfig14311→Buff14311._kLogicData→14316：雙重聖光的物理支線仍取MATK。
    /// SoulDestroyer15210全10級、Thief9_1 15240描述的PATK/MATK倍率相同，兩者加總後只計一次flat。
    /// Thief9_1原表仍有115固定值但描述未列出；保留原表，沒有自行刪除。</summary>
    private static double SkillAttackSource(Fighter attacker, SkillConfig row, bool magic) => row.ScriptLogic switch
    {
        "MoJiShu" or "MoJiPao" or "JudexMix1" => attacker.Matk,
        "SoulDestroyer" or "Thief9_1" => (double)attacker.Atk + attacker.Matk,
        _ => magic ? attacker.Matk : attacker.Atk,
    };
}
