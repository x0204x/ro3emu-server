using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    // Named one-hit damage/vulnerability and stealth groups. Consumption follows a committed positive hit
    // (inferred); misses never spend the effect. Removal uses normal icon/stat/control publication.
    internal async Task ConsumeGuardianGroupAsync(GameSession target, int group)
    {
        foreach (var buff in target.Buffs.Where(b => b.IsGuardianOwned && b.ExpireMs > MonsterService.NowMs
            && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == group).ToArray())
            await RemoveAsync(target, buff);
    }

    internal async Task ConsumeGuardianGroupAsync(WorldUnit target, int group)
    {
        if (!_unitBuffs.TryGetValue(target.Uid, out var active)) return;
        foreach (var buff in active.Where(b => b.ExpireMs > MonsterService.NowMs
            && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == group).ToArray())
            await RemoveUnitBuffAsync(target, buff);
    }
}
