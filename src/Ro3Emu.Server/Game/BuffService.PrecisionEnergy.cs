using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    // 共用既有140001命中進度與140002普通百矛充能；不建立第二份計數器。
    // (inferred) 滿5層時仍消耗一次完成的N命中週期，額外充能按原上限捨去。
    internal async Task AddPrecisionHitAsync(GameSession player, int required, int reward, Func<bool> current)
    {
        if (!current() || required <= 0 || reward <= 0 || player.Progress?.Career != 1400
            || !player.Energy.TryGetValue(140001, out var progress)
            || !player.Energy.TryGetValue(140002, out var charge)
            || _data.Row("EnergyConfig", 140002) is not { } row) return;
        long limit = Math.Max(0, GameData.Int(row, "_iLimitValue"));
        long hits = progress.Value + 1;
        long earned = hits / required;
        progress.Value = hits % required;
        charge.Value = Math.Min(limit, charge.Value + earned * reward);
        // 兩欄在首次await前一起提交；Changed observer不會看見只扣進度尚未給充能。
        await SendEnergyAsync(player, 140001, (EPlayerEnergyChangeReason)2);
        if (earned > 0 && current() && player.Energy.GetValueOrDefault(140002) == charge)
            await SendEnergyAsync(player, 140002, (EPlayerEnergyChangeReason)2);
    }

    internal async Task ResetPrecisionEnergyOnDeathAsync(GameSession player, Func<bool> current)
    {
        if (!current()) return;
        var changed = new List<(int Id, PlayerEnergyState Meter)>();
        foreach (int id in new[] { 140001, 140002 })
        {
            if (!player.Energy.TryGetValue(id, out var meter) || _data.Row("EnergyConfig", id) is not { } row
                || GameData.Int(row, "_iDeadSave") != 0 || meter.Value == 0) continue;
            meter.Value = 0;
            changed.Add((id, meter));
        }
        foreach (var (id, meter) in changed)
        {
            if (!current() || player.Energy.GetValueOrDefault(id) != meter) break;
            await SendEnergyAsync(player, id, (EPlayerEnergyChangeReason)2);
        }
    }
}
