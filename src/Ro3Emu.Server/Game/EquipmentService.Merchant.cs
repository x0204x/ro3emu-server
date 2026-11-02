using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class EquipmentService
{
    private Dictionary<(long Group, long Level), SkillConfig>? _merchantSkillRows;

    // 配對 LC_DAL_Composition._getLiveCountChangeRadio：只有 CurrencyMerchantSkill 的分解產物
    // 套用 SellHigh 的 SpSkillParam[1] / 10000。不是販售道具價格，也不放大其他材料。
    // 逐件向下取整（inferred）；加成先納入原交易 goods，與扣除物品一併提交，重送不另行發獎。
    internal List<(int Entry, long Count)> MerchantReturns(GameSession s, List<(int Entry, long Count)> returns)
    {
        var parameters = _data.Table("PublicParamConfig");
        if (s.Progress is not { } progress || !parameters.TryGetValue("OverflowMerchantSkill", out var groupRow)
            || !parameters.TryGetValue("CurrencyMerchantSkill", out var currencyRow)) return returns;
        int group = (int)(GameData.Number(groupRow) ?? 0), currency = (int)(GameData.Number(currencyRow) ?? 0);
        int level = progress.Skills.GetValueOrDefault(group);
        if (level <= 0) return returns;
        _merchantSkillRows ??= SkillConfig.All(_data).Where(r => r.Mode == 0 && r.SpSkillParam is [3, _, ..])
            .GroupBy(r => (r.SkillId, r.Level)).ToDictionary(g => g.Key, g => g.First());
        if (!_merchantSkillRows.TryGetValue((group, level), out var skill)
            || skill.SpSkillParam is not [3, var bonus, ..] || bonus <= 0) return returns;
        long career = progress.Career;
        while (career != 0 && career != skill.Job) career = JobConfig.Get(_data, career)?.ParentId ?? 0;
        if (career == 0) return returns;
        return [.. returns.Select(p => p.Entry == currency
            ? (p.Entry, p.Count + (long)Math.Floor(p.Count * (decimal)bonus / 10000)) : p)];
    }
}
